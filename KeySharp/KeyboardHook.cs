using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeySharp
{
    public static class KeyboardHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        // Tracks keys currently held down to prevent repeating signals
        private static HashSet<int> _pressedKeys = new HashSet<int>();

        public static event Action<int>? OnKeyPressed;

        private static bool _isRetrying = false;

        public static void Start()
        {
            try
            {
                if (_hookID != IntPtr.Zero) return;

                _hookID = SetHook(_proc);
                if (_hookID == IntPtr.Zero)
                {
                    StartRetryLoop();
                }
            }
            catch 
            {
                StartRetryLoop();
            }
        }

        private static async void StartRetryLoop()
        {
            if (_isRetrying) return;
            _isRetrying = true;

            while (_hookID == IntPtr.Zero)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                try
                {
                    _hookID = SetHook(_proc);
                }
                catch { }
            }

            _isRetrying = false;
        }

        public static void Stop()
        {
            try
            {
                if (_hookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookID);
                    _hookID = IntPtr.Zero;
                }
                _pressedKeys.Clear();
            }
            catch { }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            IntPtr hMod = GetModuleHandle(null);
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                    {
                        // Debounce: Only trigger if it wasn't already held down
                        if (!_pressedKeys.Contains(vkCode))
                        {
                            _pressedKeys.Add(vkCode);
                            OnKeyPressed?.Invoke(vkCode);
                        }
                    }
                    else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                    {
                        // Remove from tracking when finger lifted
                        _pressedKeys.Remove(vkCode);
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}