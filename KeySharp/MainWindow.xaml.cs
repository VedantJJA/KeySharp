using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input; // Required for KeyEventArgs, Keyboard, etc.
using System.Windows.Media;
using Microsoft.Win32;      // Required for OpenFileDialog and SaveFileDialog
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;

// --- THESE ALIASES FIX THE AMBIGUOUS ERRORS ---
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace KeySharp
{
    public partial class MainWindow : FluentWindow
    {
        private WinForms.NotifyIcon? _trayIcon;
        private bool _isExplicitClose = false;
        private RGBEngine _engine;

        private bool _isLoaded = false;

        public MainWindow()
        {
            InitializeComponent();

            _engine = new RGBEngine();
            SetupTrayIcon();

            _ = _engine.InitializeAsync();

            // Re-integrated Keyboard Hook logic
            KeyboardHook.Start();
            KeyboardHook.OnKeyPressed += (vk) =>
            {
                _engine.TriggerKeyPress(vk);
                Dispatcher.Invoke(() =>
                {
                    int modeIdx = ModeSelector.SelectedIndex;
                    if (modeIdx == 5 || modeIdx == 6)
                    {
                        UpdateCalibrationUI();
                    }
                });
            };

            _isLoaded = true;

            // Set default color to Blue
            SliderR.Value = 0;
            SliderG.Value = 0;
            SliderB.Value = 255;

            UpdateColorPreview();
            UpdateCalibrationUI();
        }

        // Re-integrated OnPreviewKeyDown override
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // Aggressively block all keyboard input from interacting with the WPF UI.
            // This prevents typing from accidentally changing the ComboBox or Sliders.
            e.Handled = true;
            base.OnPreviewKeyDown(e);
        }

        #region System Tray Logic

        private void SetupTrayIcon()
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

        private void RestoreWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
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
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                base.OnClosing(e);
            }
        }

        private void ExitApplication()
        {
            _isExplicitClose = true;
            _engine.StopEngine();
            KeyboardHook.Stop(); // Ensure global hook is removed on exit
            Application.Current.Shutdown();
        }

        #endregion

        #region UI Event Handlers

        private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || ModeSelector == null || _engine == null) return;

            int idx = ModeSelector.SelectedIndex;
            _engine.SetMode((LightMode)idx);

            // Music Panel Visibility (Mode 7)
            if (MusicPanel != null)
                MusicPanel.Visibility = (idx == 7) ? Visibility.Visible : Visibility.Collapsed;

            // Ripple Panel (Modes 2, 3, 4, 7)
            if (RipplePanel != null)
                RipplePanel.Visibility = ((idx >= 2 && idx <= 4) || idx == 7) ? Visibility.Visible : Visibility.Collapsed;

            // Color Panel (0, 2, 5, 6)
            if (ColorPanel != null)
                ColorPanel.Visibility = (idx == 0 || idx == 2 || idx == 5 || idx == 6) ? Visibility.Visible : Visibility.Collapsed;

            // Calibration (6)
            if (CalibrationPanel != null)
                CalibrationPanel.Visibility = (idx == 6) ? Visibility.Visible : Visibility.Collapsed;
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

        private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isLoaded) return;
            UpdateColorPreview();
        }

        private void UpdateColorPreview()
        {
            if (SliderR == null || SliderG == null || SliderB == null || ColorPreview == null) return;

            byte r = (byte)(SliderR.Value);
            byte g = (byte)(SliderG.Value);
            byte b = (byte)(SliderB.Value);

            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            _engine.SetColor(r, g, b);
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
            if (ZoneInfoText != null && _engine != null)
            {
                ZoneInfoText.Text = $"ZONE ID: {_engine.GetCurrentZoneIndex()}\nKEYS: {_engine.GetCurrentZoneInfo()}";
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
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

        private void LoadBtn_Click(object sender, RoutedEventArgs e)
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

                if (!System.IO.File.Exists(targetPath))
                {
                    return;
                }

                _engine.LoadMap(targetPath);
                UpdateCalibrationUI();
            }
        }

        #endregion
    }
}