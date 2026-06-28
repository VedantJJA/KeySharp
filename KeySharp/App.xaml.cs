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
                if (EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle existingEvent))
                {
                    existingEvent.Set();
                }
                Current.Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _waitThread = new Thread(WaitThreadFunc) { IsBackground = true };
            _waitThread.Start();

            base.OnStartup(e);
        }

        private void WaitThreadFunc()
        {
            while (true)
            {
                if (_showEvent?.WaitOne() == true)
                {
                    Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var mainWindow = Current.MainWindow;
                        if (mainWindow != null)
                        {
                            mainWindow.Show();
                            mainWindow.WindowState = WindowState.Normal;
                            mainWindow.Activate();
                            mainWindow.Topmost = true;  
                            mainWindow.Topmost = false; 
                        }
                    }));
                }
            }
        }
    }
}