using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RankedDuelsCompanion
{
    static class Program
    {
        // One copy per Windows user. A second launch just pokes the running
        // copy to show its window (or, when updating, to quit).
        const string MutexName = @"Local\RankedDuelsCompanion";
        public const string ShowEventName = @"Local\RankedDuelsCompanion.Show";
        public const string QuitEventName = @"Local\RankedDuelsCompanion.Quit";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Contains("--uninstall"))
            {
                AskRunningCopyToQuit();
                Install.Uninstall(interactive: true);
                return;
            }

            // Running from Downloads (or wherever): copy into the player's
            // Programs folder, then start that copy. Also how updates work.
            if (!Install.IsRunningInstalledCopy())
            {
                AskRunningCopyToQuit();
                try
                {
                    Install.InstallSelf(minimized: args.Contains("--minimized"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't install Ranked Duels Companion:\n\n" + ex.Message,
                        "Ranked Duels Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            using (var mutex = new Mutex(true, MutexName, out bool firstCopy))
            {
                if (!firstCopy)
                {
                    Signal(ShowEventName);
                    return;
                }
                Application.Run(new MainForm(startHidden: args.Contains("--minimized")));
            }
        }

        static void Signal(string name)
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(name)) ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        // Before overwriting the installed .exe, the running one must let go of it.
        static void AskRunningCopyToQuit()
        {
            Signal(QuitEventName);
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    using (var m = Mutex.OpenExisting(MutexName)) { }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    return; // not running (anymore)
                }
                Thread.Sleep(100);
            }
        }
    }
}
