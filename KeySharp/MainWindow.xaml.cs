using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;
using WinRegistry = Microsoft.Win32.Registry;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace KeySharp
{
    public partial class MainWindow : FluentWindow
    {
        private WinForms.NotifyIcon? _trayIcon;
        private bool _isExplicitClose = false;
        private RGBEngine _engine;
        private bool _isLoaded = false;
        private bool _isHandlingSelection = false;

        // Custom Color Picker Variables
        private double _currentH = 211; // Blue Hue
        private double _currentS = 1.0;
        private double _currentV = 1.0;
        private bool _isUpdatingUI = false;
        private bool _isDraggingColor = false;

        // Power Tracking Variable
        private bool? _lastPowerState = null;

        // Track which settings tool sub-view is active
        private LightMode? _settingsToolMode = null;

        // Registry key for auto-start
        private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "KeySharp Pro";

        // Setup Carousel Variables
        private int _currentSetupStep = 1;
        private int _maxSetupSteps = 0;
        private string _imagesBasePath = "";

        public MainWindow()
        {
            InitializeComponent();

            _engine = new RGBEngine();
            SetupTrayIcon();

            // Wire up Hardware connection event to UI FIRST so it populates the dynamic zone count
            _engine.OnHardwareConnected = () =>
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    PopulateZoneDropdown();
                }));
            };

            _ = _engine.InitializeAsync();

            KeyboardHook.Start();
            KeyboardHook.OnKeyPressed += (vk) =>
            {
                try
                {
                    _engine.TriggerKeyPress(vk);

                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (ModeListBox == null) return;

                            // Update calibration UI if in calibration or map test mode (triggered from settings)
                            if (_settingsToolMode == LightMode.Calibration || _settingsToolMode == LightMode.MapTest)
                            {
                                UpdateCalibrationUI();
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"UI Update Hook Error: {ex.Message}"); }
                    }));
                }
                catch (Exception ex) { Console.WriteLine($"Hook Primary Error: {ex.Message}"); }
            };

            // Register Power Change Events
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

            _isLoaded = true;

            // Initialize Custom Color Picker (Default Blue)
            _isUpdatingUI = true;
            _currentH = 211;
            _currentS = 1.0;
            _currentV = 1.0;
            HueSlider.Value = _currentH;
            UpdateCanvasThumbs();
            SyncColorFromHSV();
            _isUpdatingUI = false;

            UpdateCalibrationUI();
            UpdateModeTitle();

            // Initialize auto-start toggle from registry
            InitializeAutoStartToggle();

            // Run first time setup check
            CheckFirstRun();

            // Check initial power status
            _lastPowerState = WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Online;
            CheckPowerStatus(false);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // Allow typing in textboxes, block everywhere else
            if (e.OriginalSource is TextBox) return;

            try
            {
                e.Handled = true;
                base.OnPreviewKeyDown(e);
            }
            catch { }
        }

        #region Power Management

        private void PopulateZoneDropdown()
        {
            if (ChargerZoneCombo == null) return;

            int prevSelection = ChargerZoneCombo.SelectedIndex;
            ChargerZoneCombo.Items.Clear();
            ChargerZoneCombo.Items.Add(new ComboBoxItem { Content = "Middle" });

            int count = _engine.GetZoneCount();
            for (int i = 0; i < count; i++)
            {
                ChargerZoneCombo.Items.Add(new ComboBoxItem { Content = $"Zone {i}" });
            }

            ChargerZoneCombo.SelectedIndex = prevSelection >= 0 && prevSelection <= count ? prevSelection : 0;
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.StatusChange)
            {
                Application.Current.Dispatcher.Invoke(() => CheckPowerStatus(true));
            }
        }

        private void CheckPowerStatus(bool triggerRipple)
        {
            try
            {
                bool isPluggedIn = WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Online;

                // Handle battery turn-off toggle
                if (OffOnBatteryToggle.IsChecked == true)
                {
                    _engine.SetBrightness(isPluggedIn ? SliderBrightness.Value : 0);
                }
                else
                {
                    _engine.SetBrightness(SliderBrightness.Value);
                }

                // Handle charger ripple effect toggle
                if (triggerRipple && _lastPowerState != null && _lastPowerState != isPluggedIn)
                {
                    if (ChargerRippleToggle.IsChecked == true)
                    {
                        int selectedIdx = ChargerZoneCombo.SelectedIndex;
                        int zone = selectedIdx <= 0 ? -1 : selectedIdx - 1; // 0 is Middle, >0 is Zone ID
                        _engine.TriggerPowerRipple(zone, isPluggedIn);
                    }
                }

                _lastPowerState = isPluggedIn;
            }
            catch { }
        }

        private void SettingsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            CheckPowerStatus(false);
        }

        private void SettingsCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Just placeholder if you want immediate reaction, but it will read the index upon power change.
        }

        #endregion

        #region Setup Carousel Logic

        private void CheckFirstRun()
        {
            try
            {
                string appFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySharp");
                if (!System.IO.Directory.Exists(appFolder)) System.IO.Directory.CreateDirectory(appFolder);
                string flagFile = System.IO.Path.Combine(appFolder, "setup_complete.txt");

                if (!System.IO.File.Exists(flagFile))
                {
                    if (WelcomeOverlay != null) WelcomeOverlay.Visibility = Visibility.Visible;
                    if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Collapsed;
                    if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Collapsed;
                    if (ModeListBox != null) ModeListBox.IsEnabled = false;
                    if (SettingsListBox != null) SettingsListBox.IsEnabled = false;

                    // SMART PATH RESOLUTION FOR MSIX AND LOCAL DEPLOYMENTS
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                    string[] possiblePaths = new string[]
                    {
                        System.IO.Path.Combine(baseDir, "Images"),       // Running locally inside WPF bin/Debug/Images
                        System.IO.Path.Combine(baseDir, "..", "Images"), // MSIX Package root/Images (Wapproj mapped)
                        baseDir,                                         // Running locally inside WPF bin/Debug root
                        System.IO.Path.Combine(baseDir, "..")            // MSIX Package root
                    };

                    _imagesBasePath = "";
                    bool imageExists = false;

                    foreach (var path in possiblePaths)
                    {
                        string test = System.IO.Path.Combine(path, "1.png");
                        if (System.IO.File.Exists(test))
                        {
                            _imagesBasePath = path;
                            imageExists = true;
                            break;
                        }
                    }

                    _maxSetupSteps = 0;
                    if (imageExists)
                    {
                        while (System.IO.File.Exists(System.IO.Path.Combine(_imagesBasePath, $"{_maxSetupSteps + 1}.png")))
                        {
                            _maxSetupSteps++;
                        }
                    }

                    if (_maxSetupSteps == 0)
                    {
                        // No images found - fall back to Text mode
                        _maxSetupSteps = 1;
                        if (WelcomeFallbackText != null) WelcomeFallbackText.Visibility = Visibility.Visible;
                        if (SetupImage != null) SetupImage.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        // Images found - Show Carousel
                        if (WelcomeFallbackText != null) WelcomeFallbackText.Visibility = Visibility.Collapsed;
                        if (SetupImage != null) SetupImage.Visibility = Visibility.Visible;
                        LoadSetupImage(_currentSetupStep);
                    }
                }
            }
            catch { }
        }

        private void LoadSetupImage(int step)
        {
            try
            {
                string imgPath = System.IO.Path.Combine(_imagesBasePath, $"{step}.png");
                if (System.IO.File.Exists(imgPath) && SetupImage != null)
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imgPath, UriKind.Absolute);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    SetupImage.Source = bitmap;
                }

                if (SetupPrevBtn != null)
                {
                    SetupPrevBtn.IsEnabled = step > 1;
                }

                if (SetupNextBtn != null)
                {
                    if (step >= _maxSetupSteps)
                    {
                        SetupNextBtn.Content = "Finish";
                    }
                    else
                    {
                        SetupNextBtn.Content = ">";
                    }
                }
            }
            catch { }
        }

        private void SetupPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSetupStep > 1)
            {
                _currentSetupStep--;
                LoadSetupImage(_currentSetupStep);
            }
        }

        private void SetupNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSetupStep < _maxSetupSteps)
            {
                _currentSetupStep++;
                LoadSetupImage(_currentSetupStep);
            }
            else
            {
                GetStarted_Click(sender, e);
            }
        }

        private void SetupSkip_Click(object sender, RoutedEventArgs e)
        {
            GetStarted_Click(sender, e);
        }

        private void GetStarted_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string appFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySharp");
                string flagFile = System.IO.Path.Combine(appFolder, "setup_complete.txt");
                System.IO.File.WriteAllText(flagFile, "done");

                if (WelcomeOverlay != null) WelcomeOverlay.Visibility = Visibility.Collapsed;
                if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Visible;
                if (ModeListBox != null) ModeListBox.IsEnabled = true;
                if (SettingsListBox != null) SettingsListBox.IsEnabled = true;

                if (ModeListBox != null && ModeListBox.SelectedIndex == -1)
                    ModeListBox.SelectedIndex = 0;
            }
            catch { }
        }

        #endregion

        #region System Tray Logic

        private void SetupTrayIcon()
        {
            try
            {
                _trayIcon = new WinForms.NotifyIcon();
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                _trayIcon.Text = "KeySharp Pro";
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += (s, e) => RestoreWindow();

                var contextMenu = new WinForms.ContextMenuStrip();

                var openItem = new WinForms.ToolStripMenuItem("Open KeySharp Pro");
                openItem.Click += (s, e) => RestoreWindow();
                contextMenu.Items.Add(openItem);
                contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                var exitItem = new WinForms.ToolStripMenuItem("Exit");
                exitItem.Click += (s, e) => ExitApplication();
                contextMenu.Items.Add(exitItem);

                _trayIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex) { Console.WriteLine($"Tray setup failed: {ex.Message}"); }
        }

        private void RestoreWindow()
        {
            try
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            }
            catch { }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitClose)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                try
                {
                    SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged; // Clean up hook
                    if (_trayIcon != null)
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                    base.OnClosing(e);
                }
                catch { }
            }
        }

        private void ExitApplication()
        {
            try
            {
                _isExplicitClose = true;
                _engine.StopEngine();
                KeyboardHook.Stop();
                Application.Current.Shutdown();
            }
            catch { }
        }

        #endregion

        #region UI Event Handlers

        private void ModeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || ModeListBox == null || _engine == null || _isHandlingSelection) return;

            try
            {
                int idx = ModeListBox.SelectedIndex;
                if (idx < 0) return;

                _isHandlingSelection = true;
                if (SettingsListBox != null) SettingsListBox.SelectedIndex = -1;
                _isHandlingSelection = false;

                // Clear any active settings tool mode when switching back to sidebar modes
                _settingsToolMode = null;

                if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Visible;
                if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Collapsed;

                // Map sidebar indices to LightMode: 0=Static, 1=RainbowWave, 2=FixedRipple, 3=PerZoneRipple, 4=PerKeyRipple, 5=MusicSync
                LightMode mode;
                switch (idx)
                {
                    case 0: mode = LightMode.Static; break;
                    case 1: mode = LightMode.RainbowWave; break;
                    case 2: mode = LightMode.FixedRipple; break;
                    case 3: mode = LightMode.PerZoneRipple; break;
                    case 4: mode = LightMode.PerKeyRipple; break;
                    case 5: mode = LightMode.MusicSync; break;
                    default: mode = LightMode.Static; break;
                }

                _engine.SetMode(mode);
                UpdateModeTitle();

                if (MusicPanel != null)
                    MusicPanel.Visibility = (mode == LightMode.MusicSync) ? Visibility.Visible : Visibility.Collapsed;

                if (RainbowPanel != null)
                    RainbowPanel.Visibility = (mode == LightMode.RainbowWave) ? Visibility.Visible : Visibility.Collapsed;

                if (RipplePanel != null)
                    RipplePanel.Visibility = ((idx >= 2 && idx <= 4) || mode == LightMode.MusicSync) ? Visibility.Visible : Visibility.Collapsed;

                if (ColorPanel != null)
                    ColorPanel.Visibility = (mode == LightMode.Static || mode == LightMode.FixedRipple) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Console.WriteLine($"Selection Change Error: {ex.Message}"); }
        }

        private void SettingsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || SettingsListBox == null || _isHandlingSelection) return;
            if (SettingsListBox.SelectedIndex < 0) return;

            try
            {
                _isHandlingSelection = true;
                if (ModeListBox != null) ModeListBox.SelectedIndex = -1;
                _isHandlingSelection = false;

                // Reset to settings main view when navigating to settings tab
                _settingsToolMode = null;
                _engine.SetMode(LightMode.Static);

                if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
                if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
                if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
                if (SettingsTitle != null) SettingsTitle.Text = "SETTINGS";

                if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Collapsed;
                if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Console.WriteLine($"Settings Selection Error: {ex.Message}"); }
        }

        private void UpdateModeTitle()
        {
            try
            {
                if (ModeListBox?.SelectedItem is ListBoxItem item && ModeTitle != null)
                {
                    if (item.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                    {
                        ModeTitle.Text = tb.Text.ToUpper();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Title Update Error: {ex.Message}");
                if (ModeTitle != null) ModeTitle.Text = "MODE";
            }
        }

        private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || BrightnessVal == null) return;
            BrightnessVal.Text = $"{(int)e.NewValue}%";
            _engine.SetBrightness(e.NewValue);
        }

        private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || ThresholdVal == null) return;
            ThresholdVal.Text = $"{(int)e.NewValue}%";
            _engine.SetAudioThreshold(e.NewValue);
        }

        private void Bounce_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            _engine.SetBounce(BounceToggle.IsChecked ?? false);
        }

        private void SliderSteps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || DistanceVal == null) return;
            DistanceVal.Text = $"{(int)e.NewValue}";
            _engine.SetMaxSteps((int)e.NewValue);
        }

        private void SliderWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || WidthVal == null) return;
            WidthVal.Text = $"{(int)e.NewValue}";
            _engine.SetRippleWidth((int)e.NewValue);
        }

        private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || SpeedVal == null) return;
            SpeedVal.Text = $"{(int)e.NewValue}ms";
            _engine.SetSpeed((int)e.NewValue);
        }

        private void SliderRainbowSpread_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || RainbowSpreadVal == null) return;
            RainbowSpreadVal.Text = $"{(int)e.NewValue}";
            _engine.SetRainbowSpread((int)e.NewValue);
        }

        #endregion

        #region Settings Tools (Map Test / Calibration)

        private void OpenMapTest_Click(object sender, RoutedEventArgs e)
        {
            _settingsToolMode = LightMode.MapTest;
            _engine.SetMode(LightMode.MapTest);

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Visible;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
            if (SettingsTitle != null) SettingsTitle.Text = "MAP TEST MODE";
        }

        private void OpenCalibration_Click(object sender, RoutedEventArgs e)
        {
            _settingsToolMode = LightMode.Calibration;
            _engine.SetMode(LightMode.Calibration);
            UpdateCalibrationUI();

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Visible;
            if (SettingsTitle != null) SettingsTitle.Text = "HARDWARE CALIBRATION";
        }

        private void BackToSettings_Click(object sender, RoutedEventArgs e)
        {
            // Restore previous mode (default to Static if nothing was selected)
            _settingsToolMode = null;
            _engine.SetMode(LightMode.Static);

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
            if (SettingsTitle != null) SettingsTitle.Text = "SETTINGS";
        }

        #endregion

        #region Auto-Start

        private void InitializeAutoStartToggle()
        {
            try
            {
                using (var key = WinRegistry.CurrentUser.OpenSubKey(AutoStartRegistryKey, false))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(AutoStartValueName);
                        if (AutoStartToggle != null)
                            AutoStartToggle.IsChecked = val != null;
                    }
                }
            }
            catch { }
        }

        private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            try
            {
                using (var key = WinRegistry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true))
                {
                    if (key == null) return;

                    if (AutoStartToggle.IsChecked == true)
                    {
                        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (!string.IsNullOrEmpty(exePath))
                            key.SetValue(AutoStartValueName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AutoStartValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auto-start registry error: {ex.Message}");
            }
        }

        #endregion

        #region Custom Color Picker Logic

        private void Preset_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border borderBtn && borderBtn.Tag is string tag)
            {
                var parts = tag.Split(',');
                if (parts.Length == 3 && byte.TryParse(parts[0], out byte r) && byte.TryParse(parts[1], out byte g) && byte.TryParse(parts[2], out byte b))
                {
                    if (!_isUpdatingUI)
                    {
                        _isUpdatingUI = true;

                        InputR.Text = r.ToString();
                        InputG.Text = g.ToString();
                        InputB.Text = b.ToString();
                        HexInput.Text = $"#{r:X2}{g:X2}{b:X2}";

                        _engine.SetColor(r, g, b);
                        RgbToHsv(r, g, b, out _currentH, out _currentS, out _currentV);
                        UpdateCanvasThumbs();

                        _isUpdatingUI = false;
                    }
                }
            }
        }

        private void CustomColor_Toggle(object sender, RoutedEventArgs e)
        {
            if (CustomColorPanel != null && CustomColorBtn != null)
            {
                CustomColorPanel.Visibility = (CustomColorBtn.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ColorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingColor = true;
            ColorCanvas.CaptureMouse();
            UpdateColorFromCanvas(e);
        }

        private void ColorCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingColor) UpdateColorFromCanvas(e);
        }

        private void ColorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingColor = false;
            ColorCanvas.ReleaseMouseCapture();
        }

        private void UpdateColorFromCanvas(MouseEventArgs e)
        {
            var pos = e.GetPosition(ColorCanvas);
            double x = Math.Clamp(pos.X, 0, 130);
            double y = Math.Clamp(pos.Y, 0, 130);

            Canvas.SetLeft(ColorThumb, x - 6);
            Canvas.SetTop(ColorThumb, y - 6);

            _currentS = x / 130.0;
            _currentV = 1.0 - (y / 130.0);

            SyncColorFromHSV();
        }

        private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || HueSlider == null) return;

            _currentH = HueSlider.Value;

            Color hueBase = ColorFromHSV(_currentH, 1.0, 1.0);
            ColorCanvasBackground.Fill = new SolidColorBrush(hueBase);

            double thumbTop = (1.0 - (_currentH / 360.0)) * 130.0;
            Canvas.SetTop(HueThumbVisual, thumbTop - 5);

            SyncColorFromHSV();
        }

        private void SyncColorFromHSV()
        {
            if (_isUpdatingUI) return;
            _isUpdatingUI = true;

            Color c = ColorFromHSV(_currentH, _currentS, _currentV);
            _engine.SetColor(c.R, c.G, c.B);

            InputR.Text = c.R.ToString();
            InputG.Text = c.G.ToString();
            InputB.Text = c.B.ToString();
            HexInput.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

            _isUpdatingUI = false;
        }

        private void UpdateCanvasThumbs()
        {
            Canvas.SetLeft(ColorThumb, _currentS * 130.0 - 6);
            Canvas.SetTop(ColorThumb, (1.0 - _currentV) * 130.0 - 6);

            Canvas.SetTop(HueThumbVisual, (1.0 - (_currentH / 360.0)) * 130.0 - 5);
            HueSlider.Value = _currentH;

            Color hueBase = ColorFromHSV(_currentH, 1.0, 1.0);
            ColorCanvasBackground.Fill = new SolidColorBrush(hueBase);
        }

        private void RgbInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            if (byte.TryParse(InputR?.Text, out byte r) &&
                byte.TryParse(InputG?.Text, out byte g) &&
                byte.TryParse(InputB?.Text, out byte b))
            {
                _isUpdatingUI = true;

                HexInput.Text = $"#{r:X2}{g:X2}{b:X2}";
                _engine.SetColor(r, g, b);

                RgbToHsv(r, g, b, out _currentH, out _currentS, out _currentV);
                UpdateCanvasThumbs();

                _isUpdatingUI = false;
            }
        }

        private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            try
            {
                string hex = HexInput.Text.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);

                    _isUpdatingUI = true;
                    InputR.Text = r.ToString();
                    InputG.Text = g.ToString();
                    InputB.Text = b.ToString();

                    _engine.SetColor(r, g, b);
                    RgbToHsv(r, g, b, out _currentH, out _currentS, out _currentV);
                    UpdateCanvasThumbs();
                    _isUpdatingUI = false;
                }
            }
            catch { }
        }

        // --- MATH HELPERS ---
        private static Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            if (hi < 0) hi += 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            byte v = (byte)Math.Clamp(value, 0, 255);
            byte p = (byte)Math.Clamp(value * (1 - saturation), 0, 255);
            byte q = (byte)Math.Clamp(value * (1 - f * saturation), 0, 255);
            byte t = (byte)Math.Clamp(value * (1 - (1 - f) * saturation), 0, 255);

            return hi switch
            {
                0 => Color.FromArgb(255, v, t, p),
                1 => Color.FromArgb(255, q, v, p),
                2 => Color.FromArgb(255, p, v, t),
                3 => Color.FromArgb(255, p, q, v),
                4 => Color.FromArgb(255, t, p, v),
                _ => Color.FromArgb(255, v, p, q)
            };
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double min = Math.Min(Math.Min(r, g), b);
            double max = Math.Max(Math.Max(r, g), b);
            double delta = max - min;

            v = max / 255.0;
            s = (max == 0) ? 0 : delta / max;

            if (s == 0) h = 0;
            else
            {
                if (r == max) h = (g - b) / delta;
                else if (g == max) h = 2 + (b - r) / delta;
                else h = 4 + (r - g) / delta;

                h *= 60;
                if (h < 0) h += 360;
            }
        }

        #endregion

        #region Calibration Handlers

        private void PrevZone_Click(object sender, RoutedEventArgs e)
        {
            _engine.BackCalibration();
            UpdateCalibrationUI();
        }

        private void NextZone_Click(object sender, RoutedEventArgs e)
        {
            _engine.AdvanceCalibration();
            UpdateCalibrationUI();
        }

        private void UpdateCalibrationUI()
        {
            try
            {
                if (ZoneInfoText != null && _engine != null)
                {
                    ZoneInfoText.Text = $"ZONE ID: {_engine.GetCurrentZoneIndex()}\nKEYS: {_engine.GetCurrentZoneInfo()}";
                }
            }
            catch { }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = ".csv",
                    FileName = "keymap.csv",
                    Title = "Save KeyMap"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    _engine.SaveMap(saveFileDialog.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving map: " + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = ".csv",
                    Title = "Load KeyMap"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    string targetPath = openFileDialog.FileName;
                    if (!System.IO.File.Exists(targetPath)) return;

                    _engine.LoadMap(targetPath);
                    UpdateCalibrationUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading map: " + ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}