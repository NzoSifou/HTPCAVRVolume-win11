using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HTPCAVRVolume.Background;
using HTPCAVRVolume.Ipc;

namespace HTPCAVRVolume
{
    /// <summary>
    /// One executable, two jobs.
    ///
    /// Started with no argument it becomes the background process: a Win32 message loop holding
    /// the volume keys, the link to the receiver, the on-screen display and the tray icon, with
    /// WinUI never loaded into it at all. Started with --settings it becomes the window, which
    /// talks to that process over a pipe and exits when it is closed.
    ///
    /// The split is the whole point: a settings window nobody is looking at should not be costing
    /// eighty megabytes of XAML.
    /// </summary>
    static class Program
    {
        /// <summary>
        /// An unpackaged WinUI app finds its XAML metadata and resources through the name of its
        /// executable. Rename the file and the XAML runtime fails deep inside itself, taking the
        /// process down with a stowed exception and no message at all. Catching it here turns a
        /// silent crash into a sentence the user can act on.
        /// </summary>
        private const string RequiredName = "HTPCAVRVolume";

        [STAThread]
        static void Main(string[] args)
        {
            string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? RequiredName;
            if (!string.Equals(name, RequiredName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox(IntPtr.Zero,
                    "This file has been renamed to \"" + name + "\".\n\n" +
                    "It has to keep its original name, " + RequiredName + ".exe, or Windows cannot " +
                    "find the resources it needs and the app closes without a word.\n\n" +
                    "Rename it back and start it again. The folder it sits in does not matter.",
                    RequiredName, MB_ICONERROR);
                return;
            }

            bool settings = Array.IndexOf(args, "--settings") >= 0;
            bool raise = Array.IndexOf(args, "--raise") >= 0;

            if (settings)
            {
                RunWindow(raise);
            }
            else
            {
                RunBackground();
            }
        }

        /// <summary>The resident half. Nothing here loads WinUI.</summary>
        private static void RunBackground()
        {
            using Mutex only = new Mutex(true, @"Local\HTPCAVRVolume.Background", out bool first);
            if (!first)
            {
                // Already running: the user started the app again, which means they want to see
                // it rather than have a second copy of it.
                using IpcClient client = new IpcClient();
                if (client.Connect(2000))
                {
                    client.Send(new Request { Verb = "show" });
                    Thread.Sleep(300);
                }

                return;
            }

            using BackgroundApp app = new BackgroundApp();
            app.Run(false);
        }

        /// <summary>
        /// The window. Kept in its own method so that the WinUI assemblies are only ever loaded
        /// by a process that is actually going to show something.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunWindow(bool raiseOnly)
        {
            using EventWaitHandle raise = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HTPCAVRVolume.Raise");
            using Mutex only = new Mutex(true, @"Local\HTPCAVRVolume.Settings", out bool first);

            if (!first)
            {
                // A window is already open: ask it to come forward and leave.
                raise.Set();
                return;
            }

            // Asked only to raise a window that turns out not to exist: show one instead, which is
            // what the person clicking wanted either way.
            _ = raiseOnly;

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext context =
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }

        private const uint MB_ICONERROR = 0x00000010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    }
}
