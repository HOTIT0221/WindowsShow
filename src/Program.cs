using System;
using System.Threading;
using System.Windows.Forms;

namespace WindowsShow
{
    internal static class Program
    {
        private const string MutexName = "Local\\WindowsShow.SingleInstance";
        private const string OpenSettingsEventName = "Local\\WindowsShow.OpenSettings";

        [STAThread]
        private static void Main(string[] args)
        {
            bool startInTray = Array.Exists(args, a => string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));

            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the mutex; this one can't even open it.
                return;
            }

            using (mutex)
            {
                if (!createdNew)
                {
                    // Already running: launched by hand, so bring up the existing instance's settings.
                    if (!startInTray) SignalRunningInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show(e.Exception.Message, "WindowsShow error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                using (var openSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName))
                {
                    Application.Run(new TrayApp(openSettingsEvent, startInTray));
                }
            }
        }

        private static void SignalRunningInstance()
        {
            try
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(OpenSettingsEventName))
                {
                    ev.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
