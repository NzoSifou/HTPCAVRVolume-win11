using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HTPCAVRVolume
{
    /// <summary>
    /// Replaces the entry point XAML would generate, only so the app can say something useful
    /// before it dies.
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

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                DispatcherQueueSynchronizationContext context =
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }

        private const uint MB_ICONERROR = 0x00000010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    }
}
