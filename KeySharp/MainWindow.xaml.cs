using System;
using System.Collections.Generic;
using System.Linq;
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

        // Device Connection check flag
        private bool _hasShownDeviceNotFoundPopup = false;

        private string PriorityAlertedPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeySharp",
            "priority_alerted.txt");

        // Setup Carousel Variables
        private int _currentSetupStep = 1;
        private int _maxSetupSteps = 0;

        // Zone Preview Live Mirroring
        private List<System.Windows.Controls.Border> _previewBars = new List<System.Windows.Controls.Border>();
        private System.Windows.Threading.DispatcherTimer? _previewTimer;

        public MainWindow(bool startHidden = false)
        {
            InitializeComponent();

            _engine = new RGBEngine();
            SetupTrayIcon();

            bool delayInit = startHidden;
            if (!delayInit)
            {
                string[] args = Environment.GetCommandLineArgs();
                foreach (var arg in args)
                {
                    if (arg.Equals("--background", StringComparison.OrdinalIgnoreCase))
                    {
                        delayInit = true;
                        break;
                    }
                }
            }

            // Wire up Hardware connection event to UI FIRST so it populates the dynamic zone count
            _engine.OnHardwareConnected = () =>
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        PopulateZoneDropdown();
                        if (DeviceStatusText != null)
                        {
                            DeviceStatusText.Text = _engine.ConnectedDeviceName;
                            DeviceStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                        }
                    }
                    catch
                    {
                    }
                }));
            };

            _engine.OnDeviceNotFound = () =>
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (DeviceStatusText != null)
                        {
                            DeviceStatusText.Text = "None Detected";
                            DeviceStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                        }
                        if (!delayInit && !_hasShownDeviceNotFoundPopup)
                        {
                            _hasShownDeviceNotFoundPopup = true;
                            MessageBox.Show("No supported RGB keyboard detected. Please ensure your device is connected and supports Windows Dynamic Lighting.", "Device Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    catch
                    {
                    }
                }));
            };

            _ = _engine.InitializeAsync(delayInit);

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

            // Register Power Change & Session Switch Events
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

            _isLoaded = true;

            // Initialize UI from saved engine settings
            SyncUIWithEngineSettings();

            // Initialize auto-start toggle from registry
            InitializeAutoStartToggle();

            // Check if KeySharp has top priority in Windows Dynamic Lighting settings (Handled by the interactive tour now)
            // Check if KeySharp has top priority in Windows Dynamic Lighting settings (only if launched in foreground)
            if (!delayInit)
            {
                CheckDynamicLightingPriority();
            }

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
            else if (e.Mode == PowerModes.Resume)
            {
                if (_engine != null)
                {
                    _ = _engine.RefreshHardwareConnectionAsync();
                }
            }
        }

        private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                // Reset/re-register global keyboard hook and refresh lighting connection on session unlock
                KeyboardHook.Stop();
                KeyboardHook.Start();

                if (_engine != null)
                {
                    _ = _engine.RefreshHardwareConnectionAsync();
                }
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

                    _maxSetupSteps = 5;
                    _currentSetupStep = 1;
                    
                    // Delay slightly to ensure layout and controls render before calculating coordinates
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        LoadTourStep(_currentSetupStep);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch { }
        }

        private void LoadTourStep(int step)
        {
            try
            {
                if (WelcomeOverlay == null || TourTitle == null || TourDescription == null || TourIcon == null || TourActionButton == null || TourHighlight == null || TourBubble == null) return;

                FrameworkElement? target = null;
                string title = "";
                string desc = "";
                string nextText = "Next >";
                bool showActionBtn = false;
                string actionText = "";

                // Reset sidebar selection if moving away from settings steps
                if (step <= 2)
                {
                    if (SettingsListBox != null && SettingsListBox.SelectedIndex != -1)
                    {
                        _isHandlingSelection = true;
                        SettingsListBox.SelectedIndex = -1;
                        _isHandlingSelection = false;
                    }
                    if (ModeListBox != null && ModeListBox.SelectedIndex == -1)
                    {
                        _isHandlingSelection = true;
                        ModeListBox.SelectedIndex = 0;
                        _isHandlingSelection = false;
                    }

                    // Reset main panels
                    if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Visible;
                    if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Collapsed;
                }

                switch (step)
                {
                    case 1:
                        target = ModeListBox;
                        TourIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PaintBrush24;
                        TourIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));
                        title = "1. Select Lighting Effect";
                        desc = "Select a lighting mode from the sidebar: choose Static colors, Rainbow Waves, keypress Ripples, or Music Sync.";
                        break;

                    case 2:
                        target = SettingsListBox;
                        TourIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Settings24;
                        TourIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A0A0A0"));
                        title = "2. Open App Settings";
                        desc = "Configuration toggles, priority control, and keyboard mapping can be found under the Settings tab.";
                        break;

                    case 3:
                        // Make sure we show Settings tab and main settings card
                        _isHandlingSelection = true;
                        if (SettingsListBox != null) SettingsListBox.SelectedIndex = 0;
                        if (ModeListBox != null) ModeListBox.SelectedIndex = -1;
                        _isHandlingSelection = false;

                        _settingsToolMode = null;
                        _engine.SetSettingsActive(true);
                        _engine.SetMode(LightMode.Static);

                        if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Visible;
                        if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Collapsed;
                        if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
                        if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
                        if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
                        if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;

                        target = ManagePriorityBtn;
                        TourIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Important24;
                        TourIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCC00"));
                        title = "3. Lighting Priority";
                        desc = "Windows manages light control priority. If default vendor apps (like Razer or Legion) override KeySharp, click the button below to open Windows Settings and drag KeySharp to the top of the Background control list.";
                        showActionBtn = true;
                        actionText = "Open Windows Settings";
                        break;

                    case 4:
                        // Make sure we are on the settings main view
                        if (SettingsMainView != null && SettingsMainView.Visibility != Visibility.Visible)
                        {
                            SettingsMainView.Visibility = Visibility.Visible;
                            SettingsCalibrationView.Visibility = Visibility.Collapsed;
                            if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;
                        }

                        target = OpenCalibrationBtn;
                        TourIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Wrench24;
                        TourIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007AFF"));
                        title = "4. Map Keyboard Zones";
                        desc = "To make keypress ripples and lighting waves match your hardware keys layout, click this button to open the zone mapping tool.";
                        showActionBtn = true;
                        actionText = "Start Calibration Utility";
                        break;

                    case 5:
                        // Trigger open calibration subview programmatically
                        if (_settingsToolMode != LightMode.Calibration)
                        {
                            _settingsToolMode = LightMode.Calibration;
                            _engine.SetSettingsActive(false);
                            _engine.SetMode(LightMode.Calibration);
                            UpdateCalibrationUI();
                            Check24ZoneCalibration();

                            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
                            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Visible;
                        }

                        target = SettingsCalibrationView;
                        TourIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Save24;
                        TourIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34C759"));
                        title = "5. Calibration & Auto-Save";
                        desc = "Tap any key on your keyboard to register it to the active zone. Use Prev/Next to loop zones, and click 'Save Map' when finished to save your layout permanently.";
                        nextText = "Finish";
                        break;
                }

                // Position relative to target
                if (target != null)
                {
                    target.UpdateLayout();
                    Point point = target.TranslatePoint(new Point(0, 0), WelcomeOverlay);

                    // Update Highlight box
                    TourHighlight.Visibility = Visibility.Visible;
                    TourHighlight.Width = target.ActualWidth + 8;
                    TourHighlight.Height = target.ActualHeight + 8;
                    TourHighlight.Margin = new Thickness(point.X - 4, point.Y - 4, 0, 0);

                    // Position Tour Bubble next to target
                    double bubbleX = point.X + target.ActualWidth + 20;
                    double bubbleY = point.Y;

                    // If bubble goes out of right window bounds, place it to the left of the target instead
                    if (bubbleX + TourBubble.Width > WelcomeOverlay.ActualWidth)
                    {
                        bubbleX = point.X - TourBubble.Width - 20;
                    }

                    // Constrain Y position within window bounds
                    double bubbleHeight = TourBubble.ActualHeight > 0 ? TourBubble.ActualHeight : 200;
                    if (bubbleY + bubbleHeight > WelcomeOverlay.ActualHeight)
                    {
                        bubbleY = WelcomeOverlay.ActualHeight - bubbleHeight - 20;
                    }

                    if (bubbleX < 10) bubbleX = 10;
                    if (bubbleY < 10) bubbleY = 10;

                    TourBubble.HorizontalAlignment = HorizontalAlignment.Left;
                    TourBubble.VerticalAlignment = VerticalAlignment.Top;
                    TourBubble.Margin = new Thickness(bubbleX, bubbleY, 0, 0);
                }
                else
                {
                    TourHighlight.Visibility = Visibility.Collapsed;
                    TourBubble.HorizontalAlignment = HorizontalAlignment.Center;
                    TourBubble.VerticalAlignment = VerticalAlignment.Center;
                    TourBubble.Margin = new Thickness(0);
                }

                // Update text elements
                TourTitle.Text = title;
                TourDescription.Text = desc;
                if (TourStepCounter != null) TourStepCounter.Text = $"{step}/5";
                if (SetupPrevBtn != null) SetupPrevBtn.IsEnabled = step > 1;
                if (SetupNextBtn != null) SetupNextBtn.Content = nextText;

                TourActionButton.Visibility = showActionBtn ? Visibility.Visible : Visibility.Collapsed;
                TourActionButton.Content = actionText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tour step error: {ex.Message}");
            }
        }

        private void SetupPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSetupStep > 1)
            {
                _currentSetupStep--;
                LoadTourStep(_currentSetupStep);
            }
        }

        private void SetupNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSetupStep < _maxSetupSteps)
            {
                _currentSetupStep++;
                LoadTourStep(_currentSetupStep);
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

        private void TourActionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSetupStep == 3)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true });
                }
                else if (_currentSetupStep == 4)
                {
                    // Move to Step 5 programmatically (which loads calibration utility)
                    _currentSetupStep = 5;
                    LoadTourStep(_currentSetupStep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tour Action Error: {ex.Message}");
            }
        }

        private void WelcomeOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WelcomeOverlay != null && WelcomeOverlay.Visibility == Visibility.Visible)
            {
                LoadTourStep(_currentSetupStep);
            }
        }

        private void GetStarted_Click(object? sender, RoutedEventArgs? e)
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

                // Return to settings main view when closing tour
                _settingsToolMode = null;
                _engine.SetSettingsActive(false);
                if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
                if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
                if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;

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
                _trayIcon.Text = "KeySharp Pro";
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += (s, e) =>
                {
                    RestoreWindow();
                };

                // Load high-quality tray icon from app.png resource
                try
                {
                    var iconUri = new Uri("pack://application:,,,/Images/app.png");
                    var iconStream = Application.GetResourceStream(iconUri)?.Stream;
                    if (iconStream != null)
                    {
                        using (var bitmap = new System.Drawing.Bitmap(iconStream))
                        {
                            _trayIcon.Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
                        }
                    }
                    else
                    {
                        _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                    }
                }
                catch
                {
                    _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                var contextMenu = new WinForms.ContextMenuStrip();

                var openItem = new WinForms.ToolStripMenuItem("Open KeySharp Pro");
                openItem.Click += (s, e) =>
                {
                    RestoreWindow();
                };
                contextMenu.Items.Add(openItem);
                contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                
                var exitItem = new WinForms.ToolStripMenuItem("Exit");
                exitItem.Click += (s, e) =>
                {
                    ExitApplication();
                };
                contextMenu.Items.Add(exitItem);

                _trayIcon.ContextMenuStrip = contextMenu;
            }
            catch
            {
            }
        }

        public void RestoreWindow()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RestoreWindow));
                return;
            }
            try
            {
                
                // Centering the window to recover it from potential off-screen starting coordinates (-10000)
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;
                double w = this.Width > 0 ? this.Width : 780;
                double h = this.Height > 0 ? this.Height : 550;
                this.Left = (screenWidth - w) / 2;
                this.Top = (screenHeight - h) / 2;

                // Ensure properties allow window to be seen
                this.ShowInTaskbar = true;
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();

                // Bring to foreground
                this.Topmost = true;
                this.Topmost = false;


                // Check Dynamic Lighting priority when user restores window
                CheckDynamicLightingPriority();
            }
            catch
            {
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitClose)
            {
                e.Cancel = true;
                this.Hide();
                StopPreviewTimer();
            }
            else
            {
                try
                {
                    SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
                    SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
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

        /// <summary>
        /// Called during system shutdown/restart (via App.SessionEnding) to allow graceful cleanup.
        /// </summary>
        public void ForceClose()
        {
            try
            {
                _engine.StopEngine();
                KeyboardHook.Stop();
            }
            catch { }
        }

        private void ExitApplication()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ExitApplication));
                return;
            }
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

                // Map sidebar indices to LightMode: 0=Static, 1=RainbowWave, 2=Ripple (sub-type from combo), 3=MusicSync, 4=ScreenMirror
                LightMode mode;
                switch (idx)
                {
                    case 0: mode = LightMode.Static; break;
                    case 1: mode = LightMode.RainbowWave; break;
                    case 2: 
                        mode = _engine.GetLastRippleMode(); 
                        if (RippleTypeCombo != null)
                        {
                            _isUpdatingUI = true;
                            RippleTypeCombo.SelectedIndex = mode switch
                            {
                                LightMode.FixedRipple => 0,
                                LightMode.PerZoneRipple => 1,
                                LightMode.PerKeyRipple => 2,
                                _ => 0
                            };
                            _isUpdatingUI = false;
                        }
                        break;
                    case 3: mode = LightMode.MusicSync; break;
                    case 4: mode = LightMode.ScreenMirror; break;
                    default: mode = LightMode.Static; break;
                }

                // Re-enable hardware color updates when leaving settings tab
                _engine.SetSettingsActive(false);
                _engine.SetMode(mode);
                UpdateModeTitle();

                UpdatePanelVisibilities(mode, idx);
                StopPreviewTimer();
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

                if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
                if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
                if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
                if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;
                if (SettingsTitle != null) SettingsTitle.Text = "SETTINGS";

                if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Collapsed;
                if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Visible;
                StopPreviewTimer();
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
            _engine.SetBeatSensitivity(e.NewValue);
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

        private void SliderRainbowSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || RainbowSpeedVal == null) return;
            RainbowSpeedVal.Text = $"{(int)e.NewValue}ms";
            _engine.SetRainbowSpeed((int)e.NewValue);
        }

        private LightMode GetRippleModeFromCombo()
        {
            if (RippleTypeCombo == null) return LightMode.FixedRipple;
            return RippleTypeCombo.SelectedIndex switch
            {
                0 => LightMode.FixedRipple,
                1 => LightMode.PerZoneRipple,
                2 => LightMode.PerKeyRipple,
                _ => LightMode.FixedRipple
            };
        }

        private void RippleTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || _engine == null || RippleTypeCombo == null || _isUpdatingUI) return;

            LightMode mode = GetRippleModeFromCombo();
            _engine.SetLastRippleMode(mode);

            // Only apply mode immediately if we're currently in Ripple mode (sidebar index 2)
            if (ModeListBox != null && ModeListBox.SelectedIndex == 2)
            {
                _engine.SetMode(mode);

                // Show color panel only for Fixed Color ripple
                if (ColorPanel != null)
                    ColorPanel.Visibility = (mode == LightMode.FixedRipple) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        #endregion

        #region Settings Tools (Map Test / Calibration / Zone Preview)

        private void OpenMapTest_Click(object sender, RoutedEventArgs e)
        {
            _settingsToolMode = LightMode.MapTest;
            // Disable settings black-out mode temporarily so the tools can update LEDs
            _engine.SetSettingsActive(false);
            _engine.SetMode(LightMode.MapTest);

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Visible;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
            if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;
            if (SettingsTitle != null) SettingsTitle.Text = "MAP TEST MODE";
        }

        private void OpenCalibration_Click(object sender, RoutedEventArgs e)
        {
            _settingsToolMode = LightMode.Calibration;
            // Disable settings black-out mode temporarily so the tools can update LEDs
            _engine.SetSettingsActive(false);
            _engine.SetMode(LightMode.Calibration);
            UpdateCalibrationUI();
            Check24ZoneCalibration();

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Visible;
            if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;
            if (SettingsTitle != null) SettingsTitle.Text = "HARDWARE CALIBRATION";
        }

        private void OpenZonePreview_Click(object sender, RoutedEventArgs e)
        {
            _settingsToolMode = null;

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Collapsed;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
            if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Visible;
            if (SettingsTitle != null) SettingsTitle.Text = "ZONE MAP PREVIEW";

            RenderZonePreview();
            StartPreviewTimer();
        }

        private void BackToSettings_Click(object sender, RoutedEventArgs e)
        {
            // Return to main settings view and restore last active lighting mode
            _settingsToolMode = null;
            _engine.SetSettingsActive(false);

            // Restore the last active user mode from saved settings
            LightMode savedMode = _engine.GetLastUserMode();
            _engine.SetMode(savedMode);

            if (SettingsMainView != null) SettingsMainView.Visibility = Visibility.Visible;
            if (SettingsMapTestView != null) SettingsMapTestView.Visibility = Visibility.Collapsed;
            if (SettingsCalibrationView != null) SettingsCalibrationView.Visibility = Visibility.Collapsed;
            if (SettingsZonePreviewView != null) SettingsZonePreviewView.Visibility = Visibility.Collapsed;
            if (SettingsTitle != null) SettingsTitle.Text = "SETTINGS";
            StopPreviewTimer();
        }

        #endregion

        #region Auto-Start

        private bool IsPackaged()
        {
            try
            {
                return global::Windows.ApplicationModel.Package.Current != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private async void InitializeAutoStartToggle()
        {
            try
            {
                if (IsPackaged())
                {
                    var startupTask = await global::Windows.ApplicationModel.StartupTask.GetAsync("KeySharpStartupTask");
                    if (AutoStartToggle != null)
                    {
                        AutoStartToggle.IsChecked = (startupTask.State == global::Windows.ApplicationModel.StartupTaskState.Enabled ||
                                                     startupTask.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy);
                    }

                    // Clean up any legacy registry run key
                    try
                    {
                        using (var key = WinRegistry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true))
                        {
                            if (key != null && key.GetValue(AutoStartValueName) != null)
                            {
                                key.DeleteValue(AutoStartValueName, false);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    // Clean up and delete registry auto-start run key if it exists
                    try
                    {
                        using (var key = WinRegistry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true))
                        {
                            if (key != null && key.GetValue(AutoStartValueName) != null)
                            {
                                key.DeleteValue(AutoStartValueName, false);
                            }
                        }
                    }
                    catch
                    {
                    }

                    if (AutoStartToggle != null)
                    {
                        AutoStartToggle.IsChecked = false;
                        AutoStartToggle.IsEnabled = false;
                        AutoStartToggle.Content = "Launch on Startup (Packaged version only)";
                    }
                }
            }
            catch
            {
            }
        }

        private async void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            try
            {
                if (IsPackaged())
                {
                    var startupTask = await global::Windows.ApplicationModel.StartupTask.GetAsync("KeySharpStartupTask");
                    if (AutoStartToggle.IsChecked == true)
                    {
                        var state = await startupTask.RequestEnableAsync();
                        AutoStartToggle.IsChecked = (state == global::Windows.ApplicationModel.StartupTaskState.Enabled ||
                                                     state == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy);
                    }
                    else
                    {
                        startupTask.Disable();
                    }
                }
                else
                {
                    try
                    {
                        using (var key = WinRegistry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true))
                        {
                            if (key != null && key.GetValue(AutoStartValueName) != null)
                            {
                                key.DeleteValue(AutoStartValueName, false);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch
            {
            }
        }

        #endregion

        #region Windows Dynamic Lighting Priority

        private void CheckDynamicLightingPriority()
        {
            try
            {
                // If we already alerted the user once, don't show the startup popup again
                if (System.IO.File.Exists(PriorityAlertedPath))
                {
                    return;
                }

                using (var key = WinRegistry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting\Providers", false))
                {
                    if (key == null)
                    {
                        return;
                    }

                    string[] valueNames = key.GetValueNames();
                    string? keySharpName = null;

                    foreach (var name in valueNames)
                    {
                        var val = key.GetValue(name)?.ToString();
                        if (val != null && val.Contains("KeySharp", StringComparison.OrdinalIgnoreCase))
                        {
                            keySharpName = name;
                            break;
                        }
                    }


                    if (keySharpName != null && keySharpName != "1")
                    {
                        // Get the name of the app holding top priority
                        var topPriorityVal = key.GetValue("1")?.ToString() ?? "Another App";
                        string topAppName = topPriorityVal;
                        
                        // Simplify name (e.g. Remove package family suffix or namespaces)
                        if (topAppName.Contains('_'))
                        {
                            topAppName = topAppName.Split('_')[0];
                        }
                        if (topAppName.Contains('.'))
                        {
                            topAppName = topAppName.Split('.')[topAppName.Split('.').Length - 1];
                        }


                        var result = MessageBox.Show(
                            $"KeySharp is registered for Dynamic Lighting, but '{topAppName}' currently has a higher priority and might block KeySharp's lighting commands in the background.\n\nWould you like to open Windows settings to drag KeySharp to the top of the priority list?",
                            "Dynamic Lighting Priority Conflict",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        // Mark as alerted so we don't pop up again
                        string folder = System.IO.Path.GetDirectoryName(PriorityAlertedPath) ?? "";
                        if (!System.IO.Directory.Exists(folder))
                        {
                            System.IO.Directory.CreateDirectory(folder);
                        }
                        System.IO.File.WriteAllText(PriorityAlertedPath, "alerted");

                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true });
                        }
                    }
                    else
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void ManagePriority_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestartTour_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string appFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySharp");
                string flagFile = System.IO.Path.Combine(appFolder, "setup_complete.txt");
                if (System.IO.File.Exists(flagFile))
                {
                    System.IO.File.Delete(flagFile);
                }

                if (WelcomeOverlay != null) WelcomeOverlay.Visibility = Visibility.Visible;
                if (MainContentPanel != null) MainContentPanel.Visibility = Visibility.Collapsed;
                if (SettingsContentPanel != null) SettingsContentPanel.Visibility = Visibility.Collapsed;
                if (ModeListBox != null) ModeListBox.IsEnabled = false;
                if (SettingsListBox != null) SettingsListBox.IsEnabled = false;

                _maxSetupSteps = 5;
                _currentSetupStep = 1;
                LoadTourStep(_currentSetupStep);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start tour: {ex.Message}", "Tour Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        #region 24-Zone Legion Detection Popup Handlers

        private void Check24ZoneCalibration()
        {
            try
            {
                if (_engine == null) return;
                int zoneCount = _engine.GetZoneCount();
                if (zoneCount == 24 && Legion24PopupOverlay != null)
                {
                    Legion24PopupOverlay.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private void UseLegion24Layout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Legion24PopupOverlay != null)
                    Legion24PopupOverlay.Visibility = Visibility.Collapsed;

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string path = System.IO.Path.Combine(baseDir, "Layouts", "Legion24.csv");
                if (!System.IO.File.Exists(path))
                {
                    path = System.IO.Path.Combine(baseDir, "Legion24.csv");
                }
                if (!System.IO.File.Exists(path))
                {
                    string rootRepoPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "Legion24.csv"));
                    if (System.IO.File.Exists(rootRepoPath))
                    {
                        path = rootRepoPath;
                    }
                }

                if (System.IO.File.Exists(path))
                {
                    _engine.LoadMap(path);
                    UpdateCalibrationUI();
                    MessageBox.Show("Legion 24-Zone layout loaded successfully!", "Layout Applied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Legion 24-Zone layout file could not be found at: {path}", "Layout Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading Legion 24-Zone layout: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseLegion24Popup_Click(object sender, RoutedEventArgs e)
        {
            if (Legion24PopupOverlay != null)
                Legion24PopupOverlay.Visibility = Visibility.Collapsed;
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

        #region Zone Preview Rendering



        /// <summary>
        /// Formats virtual key codes to a readable list of key names.
        /// </summary>
        private string GetFriendlyKeyNames(List<int>? vkCodes)
        {
            if (vkCodes == null || vkCodes.Count == 0) return "None";
            return string.Join(", ", vkCodes.Select(vk =>
            {
                try
                {
                    var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
                    return key.ToString();
                }
                catch
                {
                    return $"0x{vk:X2}";
                }
            }));
        }

        /// <summary>
        /// Renders the zone preview on the canvas. Supports two modes:
        /// 1. Vertical Bars mode (default): shows vertical zone segments with gradients.
        /// 2. Key Map mode: shows detailed visual layout of individual keys.
        /// </summary>
        private void RenderZonePreview()
        {
            try
            {
                if (ZonePreviewCanvas == null || _engine == null) return;

                ZonePreviewCanvas.Children.Clear();

                var calibrationMap = _engine.GetCalibrationMap();
                int zoneCount = _engine.GetZoneCount();
                if (zoneCount <= 0)
                {
                    zoneCount = calibrationMap.Keys.Count > 0 ? calibrationMap.Keys.Max() + 1 : 24;
                }

                // Generate distinct zone colors using golden-angle hue spacing for all possible zones
                var zoneColors = new Dictionary<int, Color>();
                for (int i = 0; i < zoneCount; i++)
                {
                    double hue = (i * 137.508) % 360;
                    zoneColors[i] = ColorFromHSV(hue, 0.8, 0.95);
                }

                double canvasWidth = ZonePreviewCanvas.ActualWidth > 0 ? ZonePreviewCanvas.ActualWidth : 480;
                double canvasHeight = ZonePreviewCanvas.ActualHeight > 0 ? ZonePreviewCanvas.ActualHeight : 210;

                // Render Vertical Bars for backlit zones
                double barGap = 3;
                double barW = (canvasWidth - barGap * (zoneCount + 1)) / zoneCount;

                _previewBars.Clear();

                for (int z = 0; z < zoneCount; z++)
                {
                    double x = barGap + z * (barW + barGap);
                    Color bgColor = zoneColors[z];

                    // Create a beautiful fading vertical gradient for the bar
                    var gradient = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 1),
                        EndPoint = new Point(0, 0)
                    };
                    gradient.GradientStops.Add(new GradientStop(bgColor, 0.05));
                    gradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(bgColor.A * 0.15), bgColor.R, bgColor.G, bgColor.B), 1.0));

                    var barBorder = new System.Windows.Controls.Border
                    {
                        Width = barW,
                        Height = canvasHeight - barGap * 2,
                        Background = gradient,
                        CornerRadius = new CornerRadius(3),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(90, bgColor.R, bgColor.G, bgColor.B)),
                        BorderThickness = new Thickness(1),
                    };

                    // Zone number label at the bottom of the bar
                    var textBlock = new TextBlock
                    {
                        Text = $"Z{z}",
                        FontSize = zoneCount > 16 ? 8 : 10,
                        Foreground = new SolidColorBrush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 8),
                        FontWeight = FontWeights.Bold,
                        Opacity = 0.85
                    };
                    barBorder.Child = textBlock;

                    // Tooltip shows zone number and the mapped keys
                    calibrationMap.TryGetValue(z, out var mappedKeys);
                    string keysString = GetFriendlyKeyNames(mappedKeys);
                    barBorder.ToolTip = $"Zone {z}\nMapped Keys: {keysString}";

                    Canvas.SetLeft(barBorder, x);
                    Canvas.SetTop(barBorder, barGap);
                    ZonePreviewCanvas.Children.Add(barBorder);
                    _previewBars.Add(barBorder);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RenderZonePreview Error: {ex.Message}");
            }
        }

        private void StartPreviewTimer()
        {
            if (_previewTimer == null)
            {
                _previewTimer = new System.Windows.Threading.DispatcherTimer();
                _previewTimer.Interval = TimeSpan.FromMilliseconds(33); // 30 FPS updates
                _previewTimer.Tick += PreviewTimer_Tick;
            }
            _previewTimer.Start();
        }

        private void StopPreviewTimer()
        {
            _previewTimer?.Stop();
        }

        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_engine == null || _previewBars.Count == 0) return;

                var currentColors = _engine.GetCurrentLampColors();
                if (currentColors.Length == 0) return;

                for (int z = 0; z < _previewBars.Count; z++)
                {
                    if (z >= currentColors.Length) break;

                    var winColor = currentColors[z];
                    Color wpfColor = Color.FromArgb(winColor.A, winColor.R, winColor.G, winColor.B);

                    var bar = _previewBars[z];
                    if (bar.Background is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2)
                    {
                        gradient.GradientStops[0].Color = wpfColor;
                        gradient.GradientStops[1].Color = Color.FromArgb((byte)(wpfColor.A * 0.15), wpfColor.R, wpfColor.G, wpfColor.B);
                        bar.BorderBrush = new SolidColorBrush(Color.FromArgb(90, wpfColor.R, wpfColor.G, wpfColor.B));
                    }
                }
            }
            catch { }
        }

        #endregion


        #region Screen Mirroring Logic

        private void PopulateScreenDropdown()
        {
            if (ScreenMirrorCombo == null) return;

            int prevSelection = ScreenMirrorCombo.SelectedIndex;
            ScreenMirrorCombo.Items.Clear();
            ScreenMirrorCombo.Items.Add(new ComboBoxItem { Content = "Primary Monitor" });

            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    ScreenMirrorCombo.Items.Add(new ComboBoxItem { Content = $"Monitor {i + 1} ({s.Bounds.Width}x{s.Bounds.Height})" });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error populating screens: {ex.Message}");
            }

            ScreenMirrorCombo.SelectedIndex = prevSelection >= 0 && prevSelection < ScreenMirrorCombo.Items.Count ? prevSelection : 0;
        }

        private void ScreenMirrorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || _engine == null || ScreenMirrorCombo == null) return;
            _engine.SetSelectedScreenIndex(ScreenMirrorCombo.SelectedIndex);
        }

        private void SliderScreenMirrorSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded || ScreenMirrorSpeedVal == null || _engine == null) return;
            ScreenMirrorSpeedVal.Text = $"{(int)e.NewValue} FPS";
            _engine.SetScreenMirrorSpeedFps((int)e.NewValue);
        }

        private void ScreenMirrorContrastToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded || _engine == null || ScreenMirrorContrastToggle == null) return;
            _engine.SetScreenMirrorHighContrast(ScreenMirrorContrastToggle.IsChecked == true);
        }

        #endregion

        #region UI Settings Sync & Visibilities

        private void UpdatePanelVisibilities(LightMode mode, int idx)
        {
            if (MusicPanel != null)
                MusicPanel.Visibility = (mode == LightMode.MusicSync) ? Visibility.Visible : Visibility.Collapsed;

            if (RainbowPanel != null)
                RainbowPanel.Visibility = (mode == LightMode.RainbowWave) ? Visibility.Visible : Visibility.Collapsed;

            if (RipplePanel != null)
                RipplePanel.Visibility = (idx == 2 || mode == LightMode.MusicSync) ? Visibility.Visible : Visibility.Collapsed;

            if (RippleTypeRow != null)
                RippleTypeRow.Visibility = (mode == LightMode.MusicSync) ? Visibility.Collapsed : Visibility.Visible;

            if (ColorPanel != null)
                ColorPanel.Visibility = (mode == LightMode.Static || mode == LightMode.FixedRipple) ? Visibility.Visible : Visibility.Collapsed;

            if (ScreenMirrorPanel != null)
            {
                ScreenMirrorPanel.Visibility = (mode == LightMode.ScreenMirror) ? Visibility.Visible : Visibility.Collapsed;
                if (mode == LightMode.ScreenMirror)
                {
                    PopulateScreenDropdown();
                }
            }
        }

        private void SyncUIWithEngineSettings()
        {
            _isHandlingSelection = true;
            _isUpdatingUI = true;

            try
            {
                // 1. Sync Mode
                LightMode mode = _engine.GetMode();
                int modeIdx = 0;
                switch (mode)
                {
                    case LightMode.Static: modeIdx = 0; break;
                    case LightMode.RainbowWave: modeIdx = 1; break;
                    case LightMode.FixedRipple:
                    case LightMode.PerZoneRipple:
                    case LightMode.PerKeyRipple:
                        modeIdx = 2;
                        break;
                    case LightMode.MusicSync: modeIdx = 3; break;
                    case LightMode.ScreenMirror: modeIdx = 4; break;
                }

                if (RippleTypeCombo != null)
                {
                    LightMode lastRipple = _engine.GetLastRippleMode();
                    RippleTypeCombo.SelectedIndex = lastRipple switch
                    {
                        LightMode.FixedRipple => 0,
                        LightMode.PerZoneRipple => 1,
                        LightMode.PerKeyRipple => 2,
                        _ => 0
                    };
                }

                if (ModeListBox != null)
                {
                    ModeListBox.SelectedIndex = modeIdx;
                }

                // 2. Sync Color
                Windows.UI.Color c = _engine.GetColor();
                if (InputR != null && InputG != null && InputB != null && HexInput != null)
                {
                    InputR.Text = c.R.ToString();
                    InputG.Text = c.G.ToString();
                    InputB.Text = c.B.ToString();
                    HexInput.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
                RgbToHsv(c.R, c.G, c.B, out _currentH, out _currentS, out _currentV);
                if (HueSlider != null) HueSlider.Value = _currentH;
                UpdateCanvasThumbs();

                // 3. Sync Brightness
                if (SliderBrightness != null && BrightnessVal != null)
                {
                    double brightnessPercent = _engine.GetBrightness() * 100.0;
                    SliderBrightness.Value = brightnessPercent;
                    BrightnessVal.Text = $"{(int)brightnessPercent}%";
                }

                // 4. Sync Rainbow settings
                if (SliderRainbowSpread != null && RainbowSpreadVal != null)
                {
                    double spread = _engine.GetRainbowSpread();
                    SliderRainbowSpread.Value = spread;
                    RainbowSpreadVal.Text = $"{(int)spread}";
                }
                if (SliderRainbowSpeed != null && RainbowSpeedVal != null)
                {
                    int speed = _engine.GetRainbowSpeed();
                    SliderRainbowSpeed.Value = speed;
                    RainbowSpeedVal.Text = $"{speed}ms";
                }

                // 5. Sync Ripple settings
                if (BounceToggle != null)
                {
                    BounceToggle.IsChecked = _engine.GetBounce();
                }
                if (SliderSteps != null && DistanceVal != null)
                {
                    int steps = _engine.GetMaxSteps();
                    SliderSteps.Value = steps;
                    DistanceVal.Text = $"{steps}";
                }
                if (SliderWidth != null && WidthVal != null)
                {
                    int width = _engine.GetRippleWidth();
                    SliderWidth.Value = width;
                    WidthVal.Text = $"{width}";
                }
                if (SliderSpeed != null && SpeedVal != null)
                {
                    int speed = _engine.GetSpeed();
                    SliderSpeed.Value = speed;
                    SpeedVal.Text = $"{speed}ms";
                }

                // 6. Sync Screen Mirror settings
                if (ScreenMirrorCombo != null)
                {
                    ScreenMirrorCombo.SelectedIndex = _engine.GetSelectedScreenIndex();
                }
                if (SliderScreenMirrorSpeed != null && ScreenMirrorSpeedVal != null)
                {
                    int fps = _engine.GetScreenMirrorSpeedFps();
                    SliderScreenMirrorSpeed.Value = fps;
                    ScreenMirrorSpeedVal.Text = $"{fps} FPS";
                }
                if (ScreenMirrorContrastToggle != null)
                {
                    ScreenMirrorContrastToggle.IsChecked = _engine.GetScreenMirrorHighContrast();
                }

                // 7. Sync Audio settings
                if (SliderThreshold != null && ThresholdVal != null)
                {
                    double sens = _engine.GetBeatSensitivity() * 100.0;
                    SliderThreshold.Value = sens;
                    ThresholdVal.Text = $"{(int)sens}%";
                }

                // Update panel visibilities
                UpdatePanelVisibilities(mode, modeIdx);
                UpdateModeTitle();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncUIWithEngineSettings Error: {ex.Message}");
            }
            finally
            {
                _isHandlingSelection = false;
                _isUpdatingUI = false;
            }
        }

        #endregion
    }
}