using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.System;
using WinUIColor = Windows.UI.Color;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace KeySharp
{
    public enum LightMode
    {
        Static = 0,
        RainbowWave = 1,
        FixedRipple = 2,
        PerZoneRipple = 3,
        PerKeyRipple = 4,
        MapTest = 5,
        Calibration = 6,
        MusicSync = 7,
        ScreenMirror = 8
    }

    public class SequentialWave
    {
        public int Origin;
        public int CurrentDistance = 0;
        public int MaxSteps;
        public int Width;
        public DateTime LastStepTime = DateTime.Now;
        public WinUIColor WaveColor;
        public bool IsPerZoneRainbow;
    }

    public class RGBEngine
    {
        private LampArray? _lampArray;
        private LightMode _currentMode = LightMode.Static;
        private LightMode _lastUserMode = LightMode.Static;
        private WinUIColor _currentColor = WinUIColor.FromArgb(255, 0, 122, 255);
        private Random _rng = new Random();

        // Lock to prevent overlapping COM/I2C commands which cause AccessViolationException in KernelBase.dll
        private readonly object _hwLock = new object();

        // Audio Variables
        private WasapiLoopbackCapture? _capture;
        private float _beatThreshold = 0.1f;
        private float _minThresholdFloor = 0.5f;
        private float _recentMaxVolume = 0.01f;
        private int _lastRippleOrigin = -1;
        private DateTime _lastBeatTime = DateTime.Now;
        private int _maxConcurrentWaves = 4;

        private MMDeviceEnumerator? _deviceEnumerator;
        private AudioDeviceNotificationClient? _notificationClient;
        private DateTime _lastAudioReinitTime = DateTime.MinValue;
        private readonly object _audioLock = new object();

        // Device Properties
        private string _connectedDeviceName = "None Detected";
        public string ConnectedDeviceName => _connectedDeviceName;

        // Settings State
        private bool _isSettingsActive = false;

        // Animation & Thread Variables
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isReconnecting = false;
        private double _rainbowHue = 0;
        private double _rainbowSpread = 5;
        private int _rainbowSpeedMs = 20;
        private DateTime _lastRainbowFrame = DateTime.Now;
        private int _waveSpeedMs = 20;
        private int _maxWaveSteps = 5;
        private int _rippleWidth = 1;
        private double _globalBrightness = 1.0;
        private bool _isBounceEnabled = false;

        private ConcurrentDictionary<int, double> _activeRipples = new ConcurrentDictionary<int, double>();
        private ConcurrentDictionary<int, WinUIColor> _rippleColors = new ConcurrentDictionary<int, WinUIColor>();

        private readonly object _wavesLock = new object();
        private List<SequentialWave> _activeWaves = new List<SequentialWave>();

        private int _calibrationIndex = 0;
        private ConcurrentDictionary<int, List<int>> _calibrationMap = new ConcurrentDictionary<int, List<int>>();

        // Cache for Calibration Frame to prevent garbage collection and eliminate flicker
        private WinUIColor[] _calibColorsCache = Array.Empty<WinUIColor>();
        private int[] _calibIndicesCache = Array.Empty<int>();

        // Screen Mirroring variables
        private (double X, double Y)[] _lampPositions = Array.Empty<(double X, double Y)>();
        private Bitmap? _screenBmp;
        private Graphics? _screenGraphics;
        private Bitmap? _smallBmp;
        private Graphics? _smallGraphics;
        private int _selectedScreenIndex = 0;
        private int _screenMirrorSpeedMs = 33; // 30 FPS default
        private DateTime _lastScreenMirrorFrame = DateTime.Now;
        private int _consecutiveWriteErrors = 0;
        private WinUIColor[] _currentLampColors = Array.Empty<WinUIColor>();
        private bool _isScreenMirrorHighContrast = false;

        private static readonly string _appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySharp");
        private static readonly string _configPath = Path.Combine(_appDataFolder, "last_map_config.txt");
        private static readonly string _cachedMapPath = Path.Combine(_appDataFolder, "keymap_cache.csv");
        private static readonly string _settingsPath = Path.Combine(_appDataFolder, "settings.txt");

        // Fired when the LampArray is loaded so the UI can populate available zones dynamically
        public Action? OnHardwareConnected;
        public Action? OnDeviceNotFound;

        public async Task InitializeAsync(bool delayHardwareInit = false)
        {
            try
            {
                LoadSettings();

                await AttemptReconnectAsync();

                InitializeAudio();

                LoadMap();

                _ = Task.Run(() => RenderLoopAsync(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RGBEngine InitializeAsync Error: {ex.Message}");
            }
        }

        public async Task RefreshHardwareConnectionAsync()
        {
            if (!_isReconnecting)
            {
                _isReconnecting = true;
                await AttemptReconnectAsync();
            }
        }

        private async Task AttemptReconnectAsync()
        {
            try
            {
                string selector = LampArray.GetDeviceSelector();
                var devices = await FindAllDevicesWithTimeoutAsync(selector, 5000);
                if (devices.Count > 0)
                {
                    var newLamp = await FromIdWithTimeoutAsync(devices[0].Id, 5000);

                    lock (_hwLock)
                    {
                        _lampArray = newLamp;
                        if (_lampArray != null)
                        {
                            _lampArray.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                            InitializeLampPositions();
                        }
                    }
                    _connectedDeviceName = devices[0].Name;
                    UpdateHardwareColor();
                    OnHardwareConnected?.Invoke();
                }
                else
                {
                    _connectedDeviceName = "None Detected";
                    OnDeviceNotFound?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AttemptReconnectAsync Error: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        public void StopEngine()
        {
            try
            {
                _cts.Cancel();
                _capture?.StopRecording();
                _capture?.Dispose();
                ReleaseScreenMirrorResources();

                if (_deviceEnumerator != null && _notificationClient != null)
                {
                    try
                    {
                        _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                    }
                    catch { }
                    _deviceEnumerator = null;
                    _notificationClient = null;
                }
            }
            catch { }
        }

        private void ReinitializeAudio()
        {
            if (_cts.IsCancellationRequested) return;

            lock (_audioLock)
            {
                if (_cts.IsCancellationRequested) return;

                if ((DateTime.Now - _lastAudioReinitTime).TotalSeconds < 2)
                {
                    System.Diagnostics.Debug.WriteLine("Audio reinitialization rate-limited.");
                    return;
                }
                _lastAudioReinitTime = DateTime.Now;

                try
                {
                    if (_capture != null)
                    {
                        try { _capture.StopRecording(); } catch { }
                        try { _capture.Dispose(); } catch { }
                        _capture = null;
                    }

                    InitializeAudio();
                    System.Diagnostics.Debug.WriteLine("Audio reinitialized successfully.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to reinitialize audio: " + ex.Message);
                }
            }
        }

        private void InitializeAudio()
        {
            if (_cts.IsCancellationRequested) return;

            try
            {
                try
                {
                    if (_deviceEnumerator == null)
                    {
                        _deviceEnumerator = new MMDeviceEnumerator();
                        _notificationClient = new AudioDeviceNotificationClient(() => ReinitializeAudio());
                        _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("MMDeviceEnumerator initialization failed: " + ex.Message);
                }

                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += (s, e) =>
                {
                    try
                    {
                        if (_currentMode != LightMode.MusicSync) return;

                        float max = 0;
                        var buffer = new WaveBuffer(e.Buffer);
                        for (int i = 0; i < e.BytesRecorded / 4; i++)
                        {
                            float sample = Math.Abs(buffer.FloatBuffer[i]);
                            if (sample > max) max = sample;
                        }

                        // Update running maximum volume over last few seconds
                        _recentMaxVolume = Math.Max(max, _recentMaxVolume * 0.995f);
                        if (_recentMaxVolume < 0.01f) _recentMaxVolume = 0.01f;

                        // Normalize peak against the recent peak level
                        float normMax = max / _recentMaxVolume;

                        // Onset Envelope Beat trigger: if normalized peak exceeds threshold, trigger beat
                        if (normMax > _beatThreshold && (DateTime.Now - _lastBeatTime).TotalMilliseconds > 200)
                        {
                            TriggerRandomRipple();
                            _lastBeatTime = DateTime.Now;
                            // Set threshold to peak level (with a small buffer) to prevent immediate triggers
                            _beatThreshold = normMax * 1.05f;
                        }
                        else
                        {
                            // Exponential decay of beat threshold down to minimum floor
                            _beatThreshold = Math.Max(_beatThreshold * 0.95f, _minThresholdFloor);
                        }
                    }
                    catch { }
                };

                // Packaged apps can throw a 'Permission Denied' exception here if the manifest is missing 'microphone'
                _capture.StartRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Audio Init Failed. Check Manifest capabilities: " + ex.Message);
                _capture = null;
            }
        }

        private class AudioDeviceNotificationClient : IMMNotificationClient
        {
            private readonly Action _onDefaultDeviceChanged;

            public AudioDeviceNotificationClient(Action onDefaultDeviceChanged)
            {
                _onDefaultDeviceChanged = onDefaultDeviceChanged;
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && (role == Role.Console || role == Role.Multimedia))
                {
                    Task.Run(() => _onDefaultDeviceChanged());
                }
            }

            public void OnDeviceAdded(string deviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
        }
        // Slider 1-100 maps to minimum floor threshold of 0.01 to 1.0
        public void SetBeatSensitivity(double val)
        {
            _minThresholdFloor = (float)(val / 100.0);
            if (_beatThreshold < _minThresholdFloor)
            {
                _beatThreshold = _minThresholdFloor;
            }
            SaveSettings();
        }

        private void TriggerRandomRipple()
        {
            if (_lampArray == null) return;
            int count = _lampArray.LampCount;
            if (count < 2) return;

            try
            {
                // Limit concurrent waves to prevent light clutter
                lock (_wavesLock)
                {
                    if (_activeWaves.Count >= _maxConcurrentWaves) return;
                }

                int newOrigin;
                int attempts = 0;
                do
                {
                    newOrigin = _rng.Next(0, count);
                    attempts++;
                }
                while (Math.Abs(newOrigin - _lastRippleOrigin) < (count * 0.2) && attempts < 10);

                _lastRippleOrigin = newOrigin;

                lock (_wavesLock)
                {
                    _activeWaves.Add(new SequentialWave
                    {
                        Origin = newOrigin,
                        MaxSteps = _maxWaveSteps,
                        Width = _rippleWidth,
                        WaveColor = ColorFromHSV(_rng.Next(0, 360), 1, 1),
                        IsPerZoneRainbow = false
                    });
                }
            }
            catch { }
        }

        public void SetMode(LightMode mode)
        {
            _currentMode = mode;
            _activeRipples.Clear();
            _rippleColors.Clear();

            // Track last user-facing mode (not tool modes) for settings persistence
            if (mode != LightMode.MapTest && mode != LightMode.Calibration)
            {
                _lastUserMode = mode;
            }

            lock (_wavesLock)
            {
                _activeWaves.Clear();
            }

            if (mode != LightMode.ScreenMirror)
            {
                ReleaseScreenMirrorResources();
            }

            try
            {
                lock (_hwLock)
                {
                    _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                    if (_lampArray != null)
                    {
                        int count = _lampArray.LampCount;
                        if (_currentLampColors.Length != count) _currentLampColors = new WinUIColor[count];
                        for (int i = 0; i < count; i++) _currentLampColors[i] = WinUIColor.FromArgb(255, 0, 0, 0);
                    }
                }
                if (mode == LightMode.Static) UpdateHardwareColor();
            }
            catch { }
            SaveSettings();
        }

        public void SetBrightness(double val) { _globalBrightness = val / 100.0; if (_currentMode == LightMode.Static) UpdateHardwareColor(); SaveSettings(); }
        public void SetBounce(bool enabled) { _isBounceEnabled = enabled; SaveSettings(); }
        public void SetMaxSteps(int steps) { _maxWaveSteps = steps; SaveSettings(); }
        public void SetRippleWidth(int width) { _rippleWidth = width; SaveSettings(); }
        public void SetSpeed(int ms) { _waveSpeedMs = ms; SaveSettings(); }
        public void SetRainbowSpread(double val) { _rainbowSpread = val; SaveSettings(); }
        public void SetRainbowSpeed(int ms) { _rainbowSpeedMs = ms; SaveSettings(); }
        public void SetSettingsActive(bool active)
        {
            _isSettingsActive = active;
            if (active && _currentMode != LightMode.Calibration && _currentMode != LightMode.MapTest)
            {
                try
                {
                    lock (_hwLock)
                    {
                        _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                        if (_lampArray != null)
                        {
                            int count = _lampArray.LampCount;
                            if (_currentLampColors.Length != count) _currentLampColors = new WinUIColor[count];
                            for (int i = 0; i < count; i++) _currentLampColors[i] = WinUIColor.FromArgb(255, 0, 0, 0);
                        }
                    }
                }
                catch { }
            }
            else if (!active)
            {
                if (_currentMode == LightMode.Static)
                {
                    UpdateHardwareColor();
                }
            }
        }
        public void SetColor(byte r, byte g, byte b) { _currentColor = WinUIColor.FromArgb(255, r, g, b); if (_currentMode == LightMode.Static) UpdateHardwareColor(); SaveSettings(); }

        public void TriggerKeyPress(int vkCode)
        {
            if (_lampArray == null || _currentMode == LightMode.MusicSync) return;

            try
            {
                if (_currentMode == LightMode.Calibration)
                {
                    var list = _calibrationMap.GetOrAdd(_calibrationIndex, new List<int>());
                    if (list.Contains(vkCode)) list.Remove(vkCode); else list.Add(vkCode);
                    return;
                }

                if (_currentMode == LightMode.MapTest)
                {
                    var testZones = _calibrationMap.Where(x => x.Value.Contains(vkCode)).Select(x => x.Key).ToArray();
                    if (testZones.Length > 0)
                    {
                        byte br = (byte)Math.Clamp(_currentColor.R * _globalBrightness, 0, 255);
                        byte bg = (byte)Math.Clamp(_currentColor.G * _globalBrightness, 0, 255);
                        byte bb = (byte)Math.Clamp(_currentColor.B * _globalBrightness, 0, 255);
                        WinUIColor bColor = WinUIColor.FromArgb(255, br, bg, bb);

                        lock (_hwLock)
                        {
                            _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                            _lampArray?.SetColorsForIndices(testZones.Select(_ => bColor).ToArray(), testZones);
                            
                            // Update cache
                            if (_lampArray != null)
                            {
                                int count = _lampArray.LampCount;
                                if (_currentLampColors.Length != count) _currentLampColors = new WinUIColor[count];
                                for (int i = 0; i < count; i++) _currentLampColors[i] = WinUIColor.FromArgb(255, 0, 0, 0);
                                for (int i = 0; i < testZones.Length; i++) _currentLampColors[testZones[i]] = bColor;
                            }
                        }
                    }
                    return;
                }
            }
            catch { }

            try
            {
                var zones = _calibrationMap.Where(x => x.Value.Contains(vkCode)).Select(x => x.Key).ToList();

                lock (_wavesLock)
                {
                    foreach (var zoneIndex in zones)
                    {
                        WinUIColor waveBaseColor = (_currentMode == LightMode.PerKeyRipple) ? ColorFromHSV(_rng.Next(0, 360), 1, 1) : _currentColor;
                        _activeWaves.Add(new SequentialWave { Origin = zoneIndex, MaxSteps = _maxWaveSteps, Width = _rippleWidth, WaveColor = waveBaseColor, IsPerZoneRainbow = (_currentMode == LightMode.PerZoneRipple) });
                    }
                }
            }
            catch { }
        }

        public int GetZoneCount()
        {
            return _lampArray?.LampCount ?? 0;
        }

        public void TriggerZoneRipple(int zoneIndex)
        {
            if (_lampArray == null) return;

            try
            {
                lock (_wavesLock)
                {
                    WinUIColor waveBaseColor = (_currentMode == LightMode.PerKeyRipple) ? ColorFromHSV(_rng.Next(0, 360), 1, 1) : _currentColor;
                    _activeWaves.Add(new SequentialWave
                    {
                        Origin = zoneIndex,
                        MaxSteps = _maxWaveSteps,
                        Width = _rippleWidth,
                        WaveColor = waveBaseColor,
                        IsPerZoneRainbow = (_currentMode == LightMode.PerZoneRipple)
                    });
                }
            }
            catch { }
        }

        // Dedicated Method for Charger connection Events
        public void TriggerPowerRipple(int zoneIndex, bool isPluggedIn)
        {
            if (_lampArray == null) return;

            try
            {
                lock (_wavesLock)
                {
                    // Target specific zone, or if -1 use the middle-most zone of the keyboard
                    int targetZone = zoneIndex == -1 ? _lampArray.LampCount / 2 : zoneIndex;

                    // Connected = Green, Disconnected = Red
                    WinUIColor waveColor = isPluggedIn ? WinUIColor.FromArgb(255, 0, 255, 0) : WinUIColor.FromArgb(255, 255, 0, 0);

                    _activeWaves.Add(new SequentialWave
                    {
                        Origin = targetZone,
                        MaxSteps = _maxWaveSteps,
                        Width = _rippleWidth,
                        WaveColor = waveColor,
                        IsPerZoneRainbow = false
                    });
                }
            }
            catch { }
        }

        private async Task RenderLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_lampArray == null)
                    {
                        if (!_isReconnecting)
                        {
                            _isReconnecting = true;
                            _ = AttemptReconnectAsync();
                        }
                        await Task.Delay(1000, token);
                        continue;
                    }

                    // If Settings view is active, turn lights off unless we're in map test or calibration
                    if (_isSettingsActive && _currentMode != LightMode.Calibration && _currentMode != LightMode.MapTest)
                    {
                        lock (_hwLock)
                        {
                            _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                        }
                        await Task.Delay(50, token);
                        continue;
                    }

                    if (_currentMode == LightMode.RainbowWave)
                    {
                        if ((DateTime.Now - _lastRainbowFrame).TotalMilliseconds >= _rainbowSpeedMs)
                        {
                            RenderRainbow();
                            _lastRainbowFrame = DateTime.Now;
                        }
                    }
                    if (_currentMode >= LightMode.FixedRipple && _currentMode <= LightMode.MusicSync)
                    {
                        ProcessSequentialWaves();
                        RenderRippleFading();
                    }
                    if (_currentMode == LightMode.ScreenMirror)
                    {
                        if ((DateTime.Now - _lastScreenMirrorFrame).TotalMilliseconds >= _screenMirrorSpeedMs)
                        {
                            RenderScreenMirror();
                            _lastScreenMirrorFrame = DateTime.Now;
                        }
                    }
                    if (_currentMode == LightMode.Calibration) RenderCalibrationFrame();

                    _consecutiveWriteErrors = 0;

                    // Use minimum delay of 5ms for responsive rendering
                    await Task.Delay(5, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware Write Error: {ex.Message}");
                    _consecutiveWriteErrors++;
                    if (_consecutiveWriteErrors >= 5)
                    {
                        lock (_hwLock)
                        {
                            _lampArray = null;
                        }
                        _consecutiveWriteErrors = 0;
                    }
                }
            }
        }

        private void ProcessSequentialWaves()
        {
            List<SequentialWave> currentWaves;

            lock (_wavesLock)
            {
                currentWaves = _activeWaves.ToList();
            }

            foreach (var wave in currentWaves)
            {
                if ((DateTime.Now - wave.LastStepTime).TotalMilliseconds >= _waveSpeedMs)
                {
                    for (int w = 0; w < wave.Width; w++)
                    {
                        int rawL = wave.Origin - (wave.CurrentDistance + w);
                        int rawR = wave.Origin + (wave.CurrentDistance + w);
                        ApplyWaveStep(rawL, wave);
                        ApplyWaveStep(rawR, wave);
                    }
                    wave.CurrentDistance++;
                    wave.LastStepTime = DateTime.Now;
                }
            }

            lock (_wavesLock)
            {
                _activeWaves.RemoveAll(w => w.CurrentDistance > w.MaxSteps);
            }
        }

        private void ApplyWaveStep(int index, SequentialWave wave)
        {
            int finalIndex = index;
            int count = _lampArray!.LampCount;
            if (_isBounceEnabled)
            {
                if (finalIndex < 0) finalIndex = Math.Abs(finalIndex);
                if (finalIndex >= count) finalIndex = count - 1 - (finalIndex - count);
            }
            if (finalIndex >= 0 && finalIndex < count)
            {
                _activeRipples[finalIndex] = 1.0;
                _rippleColors[finalIndex] = wave.IsPerZoneRainbow ? ColorFromHSV((_rainbowHue + (finalIndex * 15)) % 360, 1, 1) : wave.WaveColor;
            }
        }

        private void RenderRippleFading()
        {
            foreach (var item in _activeRipples.ToList())
            {
                var color = _rippleColors.ContainsKey(item.Key) ? _rippleColors[item.Key] : _currentColor;
                double finalIntensity = item.Value * _globalBrightness;

                byte r = (byte)Math.Clamp(color.R * finalIntensity, 0, 255);
                byte g = (byte)Math.Clamp(color.G * finalIntensity, 0, 255);
                byte b = (byte)Math.Clamp(color.B * finalIntensity, 0, 255);

                lock (_hwLock)
                {
                    _lampArray?.SetColorsForIndices(new[] { WinUIColor.FromArgb(255, r, g, b) }, new[] { item.Key });
                    UpdateCurrentLampColorsCache(new[] { WinUIColor.FromArgb(255, r, g, b) }, new[] { item.Key });
                }

                _activeRipples[item.Key] -= 0.04;
                if (_activeRipples[item.Key] <= 0)
                {
                    _activeRipples.TryRemove(item.Key, out _);
                    lock (_hwLock)
                    {
                        _lampArray?.SetColorsForIndices(new[] { WinUIColor.FromArgb(255, 0, 0, 0) }, new[] { item.Key });
                        UpdateCurrentLampColorsCache(new[] { WinUIColor.FromArgb(255, 0, 0, 0) }, new[] { item.Key });
                    }
                }
            }
        }

        public void AdvanceCalibration() => _calibrationIndex = (_calibrationIndex + 1) % (_lampArray?.LampCount ?? 1);
        public void BackCalibration() => _calibrationIndex = (_calibrationIndex - 1 + (_lampArray?.LampCount ?? 1)) % (_lampArray?.LampCount ?? 1);
        public int GetCurrentZoneIndex() => _calibrationIndex;
        public string GetCurrentZoneInfo() => _calibrationMap.TryGetValue(_calibrationIndex, out var keys) && keys.Count > 0 ? string.Join(", ", keys) : "None";

        /// <summary>Returns a snapshot of the full zone-to-VK-codes calibration map for UI preview.</summary>
        public Dictionary<int, List<int>> GetCalibrationMap()
        {
            return _calibrationMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }

        public void SaveMap(string? path = null)
        {
            try
            {
                if (!Directory.Exists(_appDataFolder)) Directory.CreateDirectory(_appDataFolder);

                string target = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keymap.csv");
                using (StreamWriter sw = new StreamWriter(target))
                {
                    foreach (var entry in _calibrationMap.OrderBy(x => x.Key))
                        sw.WriteLine($"{entry.Key},{string.Join(";", entry.Value)}");
                }

                // Cache a copy in LocalAppData so it survives restarts and reinstalls
                if (target != _cachedMapPath)
                    File.Copy(target, _cachedMapPath, true);

                File.WriteAllText(_configPath, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SaveMap Exception: {ex.Message}");
            }
        }

        public void LoadMap(string? path = null)
        {
            try
            {
                if (!Directory.Exists(_appDataFolder)) Directory.CreateDirectory(_appDataFolder);

                string target = path ?? string.Empty;

                if (string.IsNullOrEmpty(target))
                {
                    if (File.Exists(_configPath))
                    {
                        string saved = File.ReadAllText(_configPath).Trim();
                        // Prefer the original path if it still exists, otherwise fall back to cached copy
                        if (File.Exists(saved))
                            target = saved;
                        else if (File.Exists(_cachedMapPath))
                            target = _cachedMapPath;
                    }
                    else if (File.Exists(_cachedMapPath))
                    {
                        target = _cachedMapPath;
                    }
                    else
                    {
                        target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keymap.csv");
                    }
                }

                if (!File.Exists(target)) return;

                _calibrationMap.Clear();
                foreach (var line in File.ReadAllLines(target))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;
                    var keys = parts[1].Split(';').Where(s => !string.IsNullOrEmpty(s)).Select(int.Parse).ToList();
                    _calibrationMap[int.Parse(parts[0])] = keys;
                }

                // Cache a copy in LocalAppData so it survives restarts and reinstalls
                if (target != _cachedMapPath)
                    File.Copy(target, _cachedMapPath, true);

                File.WriteAllText(_configPath, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LoadMap Exception: {ex.Message}");
            }
        }

        private void RenderRainbow()
        {
            int count = _lampArray!.LampCount;
            int[] indices = Enumerable.Range(0, count).ToArray();
            WinUIColor[] colors = indices.Select(i => ColorFromHSV((_rainbowHue + (i * _rainbowSpread)) % 360, 1.0, _globalBrightness)).ToArray();
            _rainbowHue = (_rainbowHue + 3) % 360;

            lock (_hwLock)
            {
                _lampArray?.SetColorsForIndices(colors, indices);
                UpdateCurrentLampColorsCache(colors, indices);
            }
        }

        private void RenderCalibrationFrame()
        {
            if (_lampArray == null) return;

            int count = _lampArray.LampCount;

            if (_calibColorsCache.Length != count)
            {
                _calibColorsCache = new WinUIColor[count];
                _calibIndicesCache = new int[count];
                for (int i = 0; i < count; i++) _calibIndicesCache[i] = i;
            }

            for (int i = 0; i < count; i++)
                _calibColorsCache[i] = WinUIColor.FromArgb(255, 0, 0, 0);

            if (_calibrationIndex >= 0 && _calibrationIndex < count)
            {
                byte r = (byte)Math.Clamp(255 * _globalBrightness, 0, 255);
                _calibColorsCache[_calibrationIndex] = WinUIColor.FromArgb(255, r, 0, 0);
            }

            lock (_hwLock)
            {
                _lampArray?.SetColorsForIndices(_calibColorsCache, _calibIndicesCache);
                UpdateCurrentLampColorsCache(_calibColorsCache, _calibIndicesCache);
            }
        }

        private void UpdateHardwareColor()
        {
            if (_isSettingsActive && _currentMode != LightMode.Calibration && _currentMode != LightMode.MapTest) return;

            try
            {
                byte r = (byte)Math.Clamp(_currentColor.R * _globalBrightness, 0, 255);
                byte g = (byte)Math.Clamp(_currentColor.G * _globalBrightness, 0, 255);
                byte b = (byte)Math.Clamp(_currentColor.B * _globalBrightness, 0, 255);

                lock (_hwLock)
                {
                    WinUIColor c = WinUIColor.FromArgb(255, r, g, b);
                    _lampArray?.SetColor(c);
                    if (_lampArray != null)
                    {
                        int count = _lampArray.LampCount;
                        if (_currentLampColors.Length != count) _currentLampColors = new WinUIColor[count];
                        for (int i = 0; i < count; i++) _currentLampColors[i] = c;
                    }
                }
            }
            catch { }
        }

        private WinUIColor ColorFromHSV(double h, double s, double v)
        {
            try
            {
                int hi = (int)Math.Floor(h / 60) % 6;
                if (hi < 0) hi += 6;
                double f = h / 60 - Math.Floor(h / 60);
                double p = v * (1 - s);
                double q = v * (1 - f * s);
                double t = v * (1 - (1 - f) * s);
                v *= 255; p *= 255; q *= 255; t *= 255;

                byte bv = (byte)Math.Clamp(v, 0, 255);
                byte bt = (byte)Math.Clamp(t, 0, 255);
                byte bp = (byte)Math.Clamp(p, 0, 255);
                byte bq = (byte)Math.Clamp(q, 0, 255);

                return hi switch
                {
                    0 => WinUIColor.FromArgb(255, bv, bt, bp),
                    1 => WinUIColor.FromArgb(255, bq, bv, bp),
                    2 => WinUIColor.FromArgb(255, bp, bv, bt),
                    3 => WinUIColor.FromArgb(255, bp, bq, bv),
                    4 => WinUIColor.FromArgb(255, bt, bp, bv),
                    _ => WinUIColor.FromArgb(255, bv, bp, bq)
                };
            }
            catch
            {
                return WinUIColor.FromArgb(255, 255, 0, 0);
            }
        }

        private void InitializeLampPositions()
        {
            if (_lampArray == null) return;
            try
            {
                int count = _lampArray.LampCount;
                _lampPositions = new (double X, double Y)[count];

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;

                var lampInfos = new Windows.Devices.Lights.LampInfo[count];
                for (int i = 0; i < count; i++)
                {
                    var info = _lampArray.GetLampInfo(i);
                    lampInfos[i] = info;
                    var pos = info.Position;
                    if (pos.X < minX) minX = pos.X;
                    if (pos.X > maxX) maxX = pos.X;
                    if (pos.Y < minY) minY = pos.Y;
                    if (pos.Y > maxY) maxY = pos.Y;
                }

                float width = maxX - minX;
                float height = maxY - minY;

                for (int i = 0; i < count; i++)
                {
                    var pos = lampInfos[i].Position;
                    double normX = width > 0.001f ? (pos.X - minX) / width : 0.5;
                    // Invert Y: physical top (larger Y) maps to screen top (normY = 0)
                    double normY = height > 0.001f ? 1.0 - (pos.Y - minY) / height : 0.5;
                    _lampPositions[i] = (normX, normY);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitializeLampPositions Error: {ex.Message}");
            }
        }

        private void CaptureScreenFrame()
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                System.Windows.Forms.Screen? targetScreen = System.Windows.Forms.Screen.PrimaryScreen;
                if (_selectedScreenIndex > 0 && _selectedScreenIndex - 1 < screens.Length)
                {
                    targetScreen = screens[_selectedScreenIndex - 1];
                }

                if (targetScreen == null)
                {
                    if (screens.Length > 0)
                        targetScreen = screens[0];
                    else
                        return;
                }

                int screenW = targetScreen.Bounds.Width;
                int screenH = targetScreen.Bounds.Height;
                int screenX = targetScreen.Bounds.X;
                int screenY = targetScreen.Bounds.Y;

                if (_screenBmp == null || _screenBmp.Width != screenW || _screenBmp.Height != screenH)
                {
                    _screenBmp?.Dispose();
                    _screenGraphics?.Dispose();

                    _screenBmp = new Bitmap(screenW, screenH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    _screenGraphics = Graphics.FromImage(_screenBmp);
                }

                if (_smallBmp == null)
                {
                    _smallBmp = new Bitmap(32, 18, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    _smallGraphics = Graphics.FromImage(_smallBmp);
                    _smallGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                }

                _screenGraphics?.CopyFromScreen(screenX, screenY, 0, 0, new Size(screenW, screenH));
                _smallGraphics?.DrawImage(_screenBmp, 0, 0, _smallBmp.Width, _smallBmp.Height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CaptureScreenFrame Error: {ex.Message}");
            }
        }

        private void RenderScreenMirror()
        {
            if (_lampArray == null) return;
            try
            {
                CaptureScreenFrame();

                if (_smallBmp == null || _lampPositions.Length != _lampArray.LampCount) return;

                int count = _lampArray.LampCount;
                int[] indices = new int[count];
                WinUIColor[] colors = new WinUIColor[count];

                int bmpW = _smallBmp.Width;
                int bmpH = _smallBmp.Height;

                for (int i = 0; i < count; i++)
                {
                    indices[i] = i;
                    var (normX, normY) = _lampPositions[i];

                    int x = (int)Math.Clamp(normX * (bmpW - 1), 0, bmpW - 1);
                    int y = (int)Math.Clamp(normY * (bmpH - 1), 0, bmpH - 1);

                    System.Drawing.Color gdiColor = _smallBmp.GetPixel(x, y);

                    double rVal = gdiColor.R;
                    double gVal = gdiColor.G;
                    double bVal = gdiColor.B;

                    if (_isScreenMirrorHighContrast)
                    {
                        // Apply contrast factor of 1.75 to enhance color vibrancy
                        double factor = 1.75;
                        rVal = Math.Clamp((rVal - 128.0) * factor + 128.0, 0.0, 255.0);
                        gVal = Math.Clamp((gVal - 128.0) * factor + 128.0, 0.0, 255.0);
                        bVal = Math.Clamp((bVal - 128.0) * factor + 128.0, 0.0, 255.0);
                    }

                    byte r = (byte)Math.Clamp(rVal * _globalBrightness, 0, 255);
                    byte g = (byte)Math.Clamp(gVal * _globalBrightness, 0, 255);
                    byte b = (byte)Math.Clamp(bVal * _globalBrightness, 0, 255);

                    colors[i] = WinUIColor.FromArgb(255, r, g, b);
                }

                lock (_hwLock)
                {
                    _lampArray.SetColorsForIndices(colors, indices);
                    UpdateCurrentLampColorsCache(colors, indices);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RenderScreenMirror Error: {ex.Message}");
            }
        }

        private void ReleaseScreenMirrorResources()
        {
            _screenGraphics?.Dispose();
            _screenGraphics = null;
            _screenBmp?.Dispose();
            _screenBmp = null;
            _smallGraphics?.Dispose();
            _smallGraphics = null;
            _smallBmp?.Dispose();
            _smallBmp = null;
        }

        public void SetSelectedScreenIndex(int index)
        {
            _selectedScreenIndex = index;
            SaveSettings();
        }

        public void SetScreenMirrorSpeedFps(int fps)
        {
            int targetFps = Math.Clamp(fps, 1, 60);
            _screenMirrorSpeedMs = 1000 / targetFps;
            SaveSettings();
        }

        public LightMode GetMode() => _currentMode;
        public LightMode GetLastUserMode() => _lastUserMode;
        public WinUIColor GetColor() => _currentColor;
        public WinUIColor[] GetCurrentLampColors()
        {
            lock (_hwLock)
            {
                return _currentLampColors.ToArray();
            }
        }

        private void UpdateCurrentLampColorsCache(WinUIColor[] colors, int[] indices)
        {
            if (_lampArray == null) return;
            int count = _lampArray.LampCount;
            if (_currentLampColors.Length != count)
            {
                _currentLampColors = new WinUIColor[count];
            }
            for (int i = 0; i < indices.Length; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < count)
                {
                    _currentLampColors[idx] = colors[i];
                }
            }
        }
        public double GetBrightness() => _globalBrightness;
        public double GetRainbowSpread() => _rainbowSpread;
        public int GetRainbowSpeed() => _rainbowSpeedMs;
        public bool GetBounce() => _isBounceEnabled;
        public int GetMaxSteps() => _maxWaveSteps;
        public int GetRippleWidth() => _rippleWidth;
        public int GetSpeed() => _waveSpeedMs;
        public int GetSelectedScreenIndex() => _selectedScreenIndex;
        public int GetScreenMirrorSpeedFps() => 1000 / _screenMirrorSpeedMs;
        public bool GetScreenMirrorHighContrast() => _isScreenMirrorHighContrast;
        public void SetScreenMirrorHighContrast(bool enabled)
        {
            _isScreenMirrorHighContrast = enabled;
            SaveSettings();
        }
        public double GetBeatSensitivity() => _minThresholdFloor;

        public void SaveSettings()
        {
            try
            {
                if (!Directory.Exists(_appDataFolder)) Directory.CreateDirectory(_appDataFolder);
                using (var sw = new StreamWriter(_settingsPath))
                {
                    sw.WriteLine($"Mode={(int)_lastUserMode}");
                    sw.WriteLine($"Color={_currentColor.R},{_currentColor.G},{_currentColor.B}");
                    sw.WriteLine($"Brightness={_globalBrightness * 100.0}");
                    sw.WriteLine($"RainbowSpeed={_rainbowSpeedMs}");
                    sw.WriteLine($"RainbowSpread={_rainbowSpread}");
                    sw.WriteLine($"WaveSpeed={_waveSpeedMs}");
                    sw.WriteLine($"MaxWaveSteps={_maxWaveSteps}");
                    sw.WriteLine($"RippleWidth={_rippleWidth}");
                    sw.WriteLine($"Bounce={_isBounceEnabled}");
                    sw.WriteLine($"ScreenIndex={_selectedScreenIndex}");
                    sw.WriteLine($"ScreenMirrorFps={1000 / _screenMirrorSpeedMs}");
                    sw.WriteLine($"ScreenMirrorHighContrast={_isScreenMirrorHighContrast}");
                    sw.WriteLine($"BeatSensitivity={_minThresholdFloor * 100.0}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SaveSettings Exception: {ex.Message}");
            }
        }

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                foreach (var line in File.ReadAllLines(_settingsPath))
                {
                    var parts = line.Split('=');
                    if (parts.Length < 2) continue;
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();
                    switch (key)
                    {
                        case "Mode":
                            _currentMode = (LightMode)int.Parse(val);
                            _lastUserMode = _currentMode;
                            break;
                        case "Color":
                            var rgb = val.Split(',');
                            if (rgb.Length == 3)
                                _currentColor = WinUIColor.FromArgb(255, byte.Parse(rgb[0]), byte.Parse(rgb[1]), byte.Parse(rgb[2]));
                            break;
                        case "Brightness":
                            _globalBrightness = double.Parse(val) / 100.0;
                            break;
                        case "RainbowSpeed":
                            _rainbowSpeedMs = int.Parse(val);
                            break;
                        case "RainbowSpread":
                            _rainbowSpread = double.Parse(val);
                            break;
                        case "WaveSpeed":
                            _waveSpeedMs = int.Parse(val);
                            break;
                        case "MaxWaveSteps":
                            _maxWaveSteps = int.Parse(val);
                            break;
                        case "RippleWidth":
                            _rippleWidth = int.Parse(val);
                            break;
                        case "Bounce":
                            _isBounceEnabled = bool.Parse(val);
                            break;
                        case "ScreenIndex":
                            _selectedScreenIndex = int.Parse(val);
                            break;
                        case "ScreenMirrorFps":
                            int fps = int.Parse(val);
                            _screenMirrorSpeedMs = 1000 / Math.Clamp(fps, 1, 60);
                            break;
                        case "ScreenMirrorHighContrast":
                            _isScreenMirrorHighContrast = bool.Parse(val);
                            break;
                        case "BeatSensitivity":
                            _minThresholdFloor = (float)(double.Parse(val) / 100.0);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LoadSettings Exception: {ex.Message}");
            }
        }
        private async Task<DeviceInformationCollection> FindAllDevicesWithTimeoutAsync(string selector, int timeoutMs)
        {
            var task = DeviceInformation.FindAllAsync(selector).AsTask();
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
            {
                return await task;
            }
            throw new TimeoutException("DeviceInformation.FindAllAsync timed out.");
        }

        private async Task<LampArray?> FromIdWithTimeoutAsync(string id, int timeoutMs)
        {
            var task = LampArray.FromIdAsync(id).AsTask();
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
            {
                return await task;
            }
            throw new TimeoutException("LampArray.FromIdAsync timed out.");
        }
    }
}