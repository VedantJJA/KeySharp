using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace KeySharp
{
    public partial class App : Application
    {
        // Import DPI Awareness API for High-Res Displays
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(int dpiFlag);
        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        private const string MutexName = "KeySharp_SingleInstance_Mutex";
        private const string EventName = "KeySharp_Show_Event";
        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private Thread? _waitThread;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Enable DPI Awareness (Fixes blurry text and UI scaling on High-Res screens)
            try
            {
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { /* Ignore if OS doesn't support it */ }

            // 2. Single Instance Logic
            _mutex = new Mutex(true, MutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle? existingEvent))
                {
                    existingEvent?.Set();
                }
                Current.Shutdown();
                return;
            }

            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                _waitThread = new Thread(WaitThreadFunc) { IsBackground = true };
                _waitThread.Start();
            }
            catch { }

            // Set shutdown mode to explicit since window may start hidden
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            bool startHidden = false;
            foreach (var arg in e.Args)
            {
                if (arg.Equals("--background", StringComparison.OrdinalIgnoreCase))
                {
                    startHidden = true;
                    break;
                }
            }

            if (IsPackaged())
            {
                try
                {
                    var activatedEventArgs = global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
                    if (activatedEventArgs != null && 
                        activatedEventArgs.Kind == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask)
                    {
                        startHidden = true;
                    }
                }
                catch { }
            }

            var mainWindow = new MainWindow(startHidden);
            Current.MainWindow = mainWindow;

            if (!startHidden)
            {
                mainWindow.Show();
            }
            else
            {
                // To allow the low-level keyboard hook (WH_KEYBOARD_LL) to receive callbacks,
                // the WPF Dispatcher's message pump needs to start with at least one visible window.
                // If we immediately hide the window, the OS may not fully hook up the thread's input queue.
                // By positioning the window offscreen (-10000, -10000) and disabling the taskbar/activation,
                // we keep it technically "visible" to the Win32 window manager without displaying anything to the user.
                try
                {
                    mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    mainWindow.Left = -10000;
                    mainWindow.Top = -10000;
                    mainWindow.ShowInTaskbar = false;
                    mainWindow.ShowActivated = false;
                    mainWindow.Show();
                }
                catch { }
            }

            base.OnStartup(e);
        }

        private void WaitThreadFunc()
        {
            while (true)
            {
                try
                {
                    if (_showEvent?.WaitOne() == true)
                    {
                        Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var mainWindow = Current.MainWindow as MainWindow;
                                if (mainWindow != null)
                                {
                                    mainWindow.RestoreWindow();
                                }
                            }
                            catch { }
                        }));
                    }
                }
                catch
                {
                    Thread.Sleep(1000); // Prevent tight loop in case of continuous exceptions
                }
            }
        }

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
    }
}