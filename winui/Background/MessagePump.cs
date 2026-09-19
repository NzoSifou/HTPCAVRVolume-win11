using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HTPCAVRVolume.Background
{
    /// <summary>
    /// A plain Win32 message loop, which is all the background process needs and all it can
    /// afford. The keyboard hook has to be installed on a thread that pumps messages, the tray
    /// icon and the on-screen display are both raw windows, and none of that wants WinUI loaded
    /// into the process to hold a timer for it.
    ///
    /// Its shape deliberately mirrors the dispatcher it replaces, so the code that used to run
    /// under WinUI works here unchanged.
    /// </summary>
    sealed class MessagePump : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int WM_RUN = WM_APP + 7;
        private const int WM_TIMER = 0x0113;
        private const int WM_QUIT = 0x0012;

        private readonly object _gate = new object();
        private readonly Queue<Action> _posted = new Queue<Action>();
        private readonly Dictionary<IntPtr, PumpTimer> _timers = new Dictionary<IntPtr, PumpTimer>();
        private readonly WndProc _wndProc;
        private readonly string _className;

        private IntPtr _window;
        private int _nextTimerId = 1;

        public MessagePump()
        {
            _className = "HTPCAVRVolumePump";
            _wndProc = OnMessage;

            WNDCLASSEX wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = _className
            };

            RegisterClassEx(ref wc);
            _window = CreateWindowEx(0, _className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        /// <summary>Runs the given work on the pump's thread, from any thread.</summary>
        public void Post(Action action)
        {
            if (action == null || _window == IntPtr.Zero)
            {
                return;
            }

            lock (_gate)
            {
                _posted.Enqueue(action);
            }

            PostMessage(_window, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        public PumpTimer CreateTimer()
        {
            return new PumpTimer(this);
        }

        /// <summary>Pumps until <see cref="Quit"/>. Blocks the calling thread, as a main loop does.</summary>
        public void Run()
        {
            while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        public void Quit()
        {
            PostQuitMessage(0);
        }

        public void Dispose()
        {
            if (_window != IntPtr.Zero)
            {
                DestroyWindow(_window);
                _window = IntPtr.Zero;
            }
        }

        internal IntPtr StartTimer(PumpTimer timer, int intervalMs)
        {
            IntPtr id;
            lock (_gate)
            {
                id = (IntPtr)_nextTimerId++;
                _timers[id] = timer;
            }

            SetTimer(_window, id, (uint)Math.Max(1, intervalMs), IntPtr.Zero);
            return id;
        }

        internal void StopTimer(IntPtr id)
        {
            if (id == IntPtr.Zero)
            {
                return;
            }

            KillTimer(_window, id);

            lock (_gate)
            {
                _timers.Remove(id);
            }
        }

        private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_RUN)
            {
                Action action = null;
                lock (_gate)
                {
                    if (_posted.Count > 0)
                    {
                        action = _posted.Dequeue();
                    }
                }

                Invoke(action);
                return IntPtr.Zero;
            }

            if (message == WM_TIMER)
            {
                PumpTimer timer;
                lock (_gate)
                {
                    _timers.TryGetValue(wParam, out timer);
                }

                if (timer != null)
                {
                    if (!timer.IsRepeating)
                    {
                        timer.Stop();
                    }

                    Invoke(timer.RaiseTick);
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private static void Invoke(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch
            {
                // One piece of work failing must not take the whole loop, and with it the user's
                // volume keys, down with it.
            }
        }

        #region Win32

        private static readonly IntPtr HWND_MESSAGE = (IntPtr)(-3);

        private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG message, IntPtr window, uint first, uint last);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr window, IntPtr id, uint interval, IntPtr callback);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr window, IntPtr id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        #endregion
    }

    /// <summary>A timer that fires on the pump's thread, shaped like the one WinUI hands out.</summary>
    sealed class PumpTimer
    {
        private readonly MessagePump _pump;
        private IntPtr _id;

        internal PumpTimer(MessagePump pump)
        {
            _pump = pump;
        }

        public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(100);

        public bool IsRepeating { get; set; } = true;

        public event EventHandler Tick;

        public void Start()
        {
            Stop();
            _id = _pump.StartTimer(this, (int)Interval.TotalMilliseconds);
        }

        public void Stop()
        {
            if (_id != IntPtr.Zero)
            {
                _pump.StopTimer(_id);
                _id = IntPtr.Zero;
            }
        }

        internal void RaiseTick()
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
