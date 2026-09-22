using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HTPCAVRVolume.Background;

namespace HTPCAVRVolume.Ui
{
    /// <summary>
    /// Grabs the volume keys before Windows sees them.
    ///
    /// A low-level hook is called on the thread that installed it, and only while that thread is
    /// pumping messages; the whole system's input waits on the answer. Windows gives it at most a
    /// second (LowLevelHooksTimeout, capped at one second since Windows 10 1709). Past that the key
    /// goes to Windows, and the hook may be removed without a word -- there is no way for the
    /// application to find out.
    ///
    /// So the hook lives on a thread of its own that does nothing else. It used to share the main
    /// thread, which also runs the collection and working-set trim when the settings window goes
    /// away: on a slower machine that was long enough for Windows to take the keys back, and to
    /// keep them, from the moment the window was minimised to the notification area. Holding the
    /// main thread still for three seconds reproduces it; with the hook here, the keys stay ours
    /// throughout.
    ///
    /// The callback decides nothing and does nothing but hand the key over: whatever the key is
    /// for runs on the main thread, where it always did.
    /// </summary>
    sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const uint WM_QUIT = 0x0012;
        private const uint WM_TIMER = 0x0113;

        private const int VK_VOLUME_MUTE = 0xAD;
        private const int VK_VOLUME_DOWN = 0xAE;
        private const int VK_VOLUME_UP = 0xAF;

        /// <summary>
        /// How often the hook is put back. A hook Windows has removed cannot be detected, only
        /// replaced, so it is replaced regularly; a new one goes in before the old one comes out,
        /// so no key can slip between the two.
        /// </summary>
        private const uint RearmMs = 60000;

        private readonly MessagePump _pump;
        private readonly LowLevelKeyboardProc _proc;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        // Built once: the callback must not allocate a closure per key.
        private readonly Action _up;
        private readonly Action _down;
        private readonly Action _mute;

        private uint _threadId;
        private IntPtr _hookId;

        public GlobalKeyboardHook(MessagePump pump)
        {
            _pump = pump;

            // Kept in a field: a delegate collected while Windows still holds the pointer takes
            // the process down with it.
            _proc = HookCallback;

            _up = () => Raise(VolumeUpPressed);
            _down = () => Raise(VolumeDownPressed);
            _mute = () => Raise(VolumeMutePressed);

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Volume keys",
                Priority = ThreadPriority.Highest
            };
            _thread.Start();
            _ready.Wait(2000);
        }

        /// <summary>Raised on the pump's thread, not on the hook's.</summary>
        public event EventHandler VolumeUpPressed;

        public event EventHandler VolumeDownPressed;

        public event EventHandler VolumeMutePressed;

        public bool IsInstalled => _hookId != IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        public void Dispose()
        {
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            _thread.Join(1000);
            _ready.Dispose();
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _hookId = SetHook(_proc);
            UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, RearmMs, IntPtr.Zero);
            _ready.Set();

            // The hook is called from inside GetMessage, so this loop is what keeps it alive.
            while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WM_TIMER)
                {
                    Rearm();
                }
            }

            KillTimer(IntPtr.Zero, timer);

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private void Rearm()
        {
            IntPtr fresh = SetHook(_proc);
            if (fresh == IntPtr.Zero)
            {
                return;
            }

            IntPtr old = _hookId;
            _hookId = fresh;

            if (old != IntPtr.Zero)
            {
                // Fails harmlessly when Windows had already removed it.
                UnhookWindowsHookEx(old);
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process process = Process.GetCurrentProcess();
            using ProcessModule module = process.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                switch (Marshal.ReadInt32(lParam))
                {
                    case VK_VOLUME_UP:
                        _pump.Post(_up);
                        return (IntPtr)1;   // Swallowed, so Windows neither moves its own volume nor shows its flyout.

                    case VK_VOLUME_DOWN:
                        _pump.Post(_down);
                        return (IntPtr)1;

                    case VK_VOLUME_MUTE:
                        _pump.Post(_mute);
                        return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, code, wParam, lParam);
        }

        private static void Raise(EventHandler handler)
        {
            try
            {
                handler?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                // A fault in what a key does must not take the volume keys down with it.
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint elapse, IntPtr timerProc);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool KillTimer(IntPtr window, UIntPtr id);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
