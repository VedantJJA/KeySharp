using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.System;
using WinUIColor = Windows.UI.Color;
using NAudio.Wave; // Requires NAudio NuGet package

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
        MusicSync = 7
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
        private WinUIColor _currentColor = WinUIColor.FromArgb(255, 0, 0, 255);
        private Random _rng = new Random();

        // Audio Variables
        private WasapiLoopbackCapture? _capture;
        private float _audioThreshold = 0.5f;
        private int _lastRippleOrigin = -1;
        private DateTime _lastBeatTime = DateTime.Now;

        // Animation & Thread Variables
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isReconnecting = false;
        private double _rainbowHue = 0;
        private int _waveSpeedMs = 40;
        private int _maxWaveSteps = 10;
        private int _rippleWidth = 1;
        private double _globalBrightness = 1.0;
        private bool _isBounceEnabled = false;

        private ConcurrentDictionary<int, double> _activeRipples = new ConcurrentDictionary<int, double>();
        private ConcurrentDictionary<int, WinUIColor> _rippleColors = new ConcurrentDictionary<int, WinUIColor>();
        private List<SequentialWave> _activeWaves = new List<SequentialWave>();

        private int _calibrationIndex = 0;
        private ConcurrentDictionary<int, List<int>> _calibrationMap = new ConcurrentDictionary<int, List<int>>();

        public async Task InitializeAsync()
        {
            try
            {
                await AttemptReconnectAsync();
                InitializeAudio();
                LoadMap();

                // Start the self-healing render loop
                _ = Task.Run(() => RenderLoopAsync(_cts.Token), _cts.Token);
            }
            catch (Exception ex) { Console.WriteLine("Init Error: " + ex.Message); }
        }

        private async Task AttemptReconnectAsync()
        {
            try
            {
                string selector = LampArray.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                if (devices.Count > 0)
                {
                    _lampArray = await LampArray.FromIdAsync(devices[0].Id);
                    if (_lampArray != null)
                    {
                        _lampArray.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                        UpdateHardwareColor();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reconnect attempt failed: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        public void StopEngine()
        {
            _cts.Cancel();
            _capture?.StopRecording();
            _capture?.Dispose();
        }

        private void InitializeAudio()
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += (s, e) =>
            {
                if (_currentMode != LightMode.MusicSync) return;

                float max = 0;
                var buffer = new WaveBuffer(e.Buffer);
                for (int i = 0; i < e.BytesRecorded / 4; i++)
                {
                    float sample = Math.Abs(buffer.FloatBuffer[i]);
                    if (sample > max) max = sample;
                }

                if (max > _audioThreshold && (DateTime.Now - _lastBeatTime).TotalMilliseconds > 150)
                {
                    TriggerRandomRipple();
                    _lastBeatTime = DateTime.Now;
                }
            };
            _capture.StartRecording();
        }

        public void SetAudioThreshold(double val) => _audioThreshold = (float)(val / 100.0);

        private void TriggerRandomRipple()
        {
            if (_lampArray == null) return;
            int count = _lampArray.LampCount;
            if (count < 2) return;

            int newOrigin;
            int attempts = 0;
            do
            {
                newOrigin = _rng.Next(0, count);
                attempts++;
            }
            while (Math.Abs(newOrigin - _lastRippleOrigin) < (count * 0.2) && attempts < 10);

            _lastRippleOrigin = newOrigin;

            _activeWaves.Add(new SequentialWave
            {
                Origin = newOrigin,
                MaxSteps = _maxWaveSteps,
                Width = _rippleWidth,
                WaveColor = ColorFromHSV(_rng.Next(0, 360), 1, 1),
                IsPerZoneRainbow = false
            });
        }

        public void SetMode(LightMode mode)
        {
            _currentMode = mode;
            _activeRipples.Clear();
            _rippleColors.Clear();
            _activeWaves.Clear();

            try
            {
                _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                if (mode == LightMode.Static) UpdateHardwareColor();
            }
            catch { /* Handled by loop */ }
        }

        public void SetBrightness(double val) { _globalBrightness = val / 100.0; if (_currentMode == LightMode.Static) UpdateHardwareColor(); }
        public void SetBounce(bool enabled) => _isBounceEnabled = enabled;
        public void SetMaxSteps(int steps) => _maxWaveSteps = steps;
        public void SetRippleWidth(int width) => _rippleWidth = width;
        public void SetSpeed(int ms) => _waveSpeedMs = ms;
        public void SetColor(byte r, byte g, byte b) { _currentColor = WinUIColor.FromArgb(255, r, g, b); if (_currentMode == LightMode.Static) UpdateHardwareColor(); }

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
                    _lampArray.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
                    var testZones = _calibrationMap.Where(x => x.Value.Contains(vkCode)).Select(x => x.Key).ToArray();
                    if (testZones.Length > 0)
                    {
                        WinUIColor bColor = WinUIColor.FromArgb(255, (byte)(_currentColor.R * _globalBrightness), (byte)(_currentColor.G * _globalBrightness), (byte)(_currentColor.B * _globalBrightness));
                        _lampArray.SetColorsForIndices(testZones.Select(_ => bColor).ToArray(), testZones);
                    }
                    return;
                }
            }
            catch { /* Ignore drop, let the background loop fix it */ }

            var zones = _calibrationMap.Where(x => x.Value.Contains(vkCode)).Select(x => x.Key).ToList();
            foreach (var zoneIndex in zones)
            {
                WinUIColor waveBaseColor = (_currentMode == LightMode.PerKeyRipple) ? ColorFromHSV(_rng.Next(0, 360), 1, 1) : _currentColor;
                _activeWaves.Add(new SequentialWave { Origin = zoneIndex, MaxSteps = _maxWaveSteps, Width = _rippleWidth, WaveColor = waveBaseColor, IsPerZoneRainbow = (_currentMode == LightMode.PerZoneRipple) });
            }
        }

        private async Task RenderLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. Connection check
                    if (_lampArray == null)
                    {
                        if (!_isReconnecting)
                        {
                            _isReconnecting = true;
                            Console.WriteLine("Hardware connection lost. Attempting to reconnect...");
                            _ = AttemptReconnectAsync();
                        }
                        await Task.Delay(1000, token); // Wait before hammering the system
                        continue;
                    }

                    // 2. Render logic
                    if (_currentMode == LightMode.RainbowWave) RenderRainbow();
                    if (_currentMode >= LightMode.FixedRipple && _currentMode <= LightMode.MusicSync)
                    {
                        ProcessSequentialWaves();
                        RenderRippleFading();
                    }
                    if (_currentMode == LightMode.Calibration) RenderCalibrationFrame();

                    // 3. Sleep until next frame
                    await Task.Delay(20, token);
                }
                catch (TaskCanceledException)
                {
                    break; // Clean exit on app close
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: The LampArray threw a COM exception (device asleep/disconnected)
                    Console.WriteLine($"Hardware Write Error: {ex.Message}");
                    _lampArray = null; // Force a reconnect on the next tick
                }
            }
        }

        private void ProcessSequentialWaves()
        {
            foreach (var wave in _activeWaves.ToList())
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
                    if (wave.CurrentDistance > wave.MaxSteps) _activeWaves.Remove(wave);
                }
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
                byte r = (byte)(color.R * finalIntensity), g = (byte)(color.G * finalIntensity), b = (byte)(color.B * finalIntensity);

                // If this throws, RenderLoopAsync will catch it
                _lampArray!.SetColorsForIndices(new[] { WinUIColor.FromArgb(255, r, g, b) }, new[] { item.Key });

                _activeRipples[item.Key] -= 0.04;
                if (_activeRipples[item.Key] <= 0)
                {
                    _activeRipples.TryRemove(item.Key, out _);
                    _lampArray!.SetColorsForIndices(new[] { WinUIColor.FromArgb(255, 0, 0, 0) }, new[] { item.Key });
                }
            }
        }

        public void AdvanceCalibration() => _calibrationIndex = (_calibrationIndex + 1) % (_lampArray?.LampCount ?? 1);
        public void BackCalibration() => _calibrationIndex = (_calibrationIndex - 1 + (_lampArray?.LampCount ?? 1)) % (_lampArray?.LampCount ?? 1);
        public int GetCurrentZoneIndex() => _calibrationIndex;
        public string GetCurrentZoneInfo() => _calibrationMap.TryGetValue(_calibrationIndex, out var keys) && keys.Count > 0 ? string.Join(", ", keys) : "None";
        public void SaveMap(string? path = null) { string target = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keymap.csv"); using StreamWriter sw = new StreamWriter(target); foreach (var entry in _calibrationMap.OrderBy(x => x.Key)) sw.WriteLine($"{entry.Key},{string.Join(";", entry.Value)}"); }
        public void LoadMap(string? path = null) { string target = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keymap.csv"); if (!File.Exists(target)) return; try { _calibrationMap.Clear(); foreach (var line in File.ReadAllLines(target)) { var parts = line.Split(','); if (parts.Length < 2) continue; var keys = parts[1].Split(';').Where(s => !string.IsNullOrEmpty(s)).Select(int.Parse).ToList(); _calibrationMap[int.Parse(parts[0])] = keys; } } catch { } }

        private void RenderRainbow()
        {
            int count = _lampArray!.LampCount;
            int[] indices = Enumerable.Range(0, count).ToArray();
            WinUIColor[] colors = indices.Select(i => ColorFromHSV((_rainbowHue + (i * 5)) % 360, 0.8, _globalBrightness)).ToArray();
            _rainbowHue = (_rainbowHue + 3) % 360;
            _lampArray.SetColorsForIndices(colors, indices);
        }

        private void RenderCalibrationFrame()
        {
            _lampArray?.SetColor(WinUIColor.FromArgb(255, 0, 0, 0));
            byte r = (byte)(255 * _globalBrightness);
            _lampArray?.SetColorsForIndices(new[] { WinUIColor.FromArgb(255, r, 0, 0) }, new[] { _calibrationIndex });
        }

        private void UpdateHardwareColor()
        {
            try
            {
                byte r = (byte)(_currentColor.R * _globalBrightness), g = (byte)(_currentColor.G * _globalBrightness), b = (byte)(_currentColor.B * _globalBrightness);
                _lampArray?.SetColor(WinUIColor.FromArgb(255, r, g, b));
            }
            catch { /* Let the loop handle it */ }
        }

        private WinUIColor ColorFromHSV(double h, double s, double v) { int hi = (int)Math.Floor(h / 60) % 6; double f = h / 60 - Math.Floor(h / 60), p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s); v *= 255; p *= 255; q *= 255; t *= 255; return hi switch { 0 => WinUIColor.FromArgb(255, (byte)v, (byte)t, (byte)p), 1 => WinUIColor.FromArgb(255, (byte)q, (byte)v, (byte)p), 2 => WinUIColor.FromArgb(255, (byte)p, (byte)v, (byte)t), 3 => WinUIColor.FromArgb(255, (byte)p, (byte)q, (byte)v), 4 => WinUIColor.FromArgb(255, (byte)t, (byte)p, (byte)v), _ => WinUIColor.FromArgb(255, (byte)v, (byte)p, (byte)q) }; }
    }
}