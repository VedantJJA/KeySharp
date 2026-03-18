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

        // Custom Color Picker Variables
        private double _currentH = 211; // Blue Hue
        private double _currentS = 1.0;
        private double _currentV = 1.0;
        private bool _isUpdatingUI = false;
        private bool _isDraggingColor = false;

        public MainWindow()
        {
            InitializeComponent();

            _engine = new RGBEngine();
            SetupTrayIcon();

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
                            int modeIdx = ModeListBox.SelectedIndex;
                            if (modeIdx == 5 || modeIdx == 6)
                            {
                                UpdateCalibrationUI();
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"UI Update Hook Error: {ex.Message}"); }
                    }));
                }
                catch (Exception ex) { Console.WriteLine($"Hook Primary Error: {ex.Message}"); }
            };

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
            if (!_isLoaded || ModeListBox == null || _engine == null) return;

            try
            {
                int idx = ModeListBox.SelectedIndex;
                if (idx < 0) return;

                _engine.SetMode((LightMode)idx);
                UpdateModeTitle();

                if (MusicPanel != null)
                    MusicPanel.Visibility = (idx == 7) ? Visibility.Visible : Visibility.Collapsed;

                if (RipplePanel != null)
                    RipplePanel.Visibility = ((idx >= 2 && idx <= 4) || idx == 7) ? Visibility.Visible : Visibility.Collapsed;

                if (ColorPanel != null)
                    ColorPanel.Visibility = (idx == 0 || idx == 2) ? Visibility.Visible : Visibility.Collapsed;

                if (CalibrationPanel != null)
                    CalibrationPanel.Visibility = (idx == 6) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Console.WriteLine($"Selection Change Error: {ex.Message}"); }
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

        private void ListBoxItem_Selected(object sender, RoutedEventArgs e)
        {

        }
    }
}