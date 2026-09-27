using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RankedDuelsCompanion
{
    // A "no installer" install: copy the .exe to %LOCALAPPDATA%\Programs,
    // add a Start menu shortcut, an Apps & Features entry and (optionally) a
    // start-with-Windows entry. All per-user, no admin needed.
    static class Install
    {
        public const string AppName = "Ranked Duels Companion";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RankedDuelsCompanion";

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string Exe => Path.Combine(Dir, "RankedDuelsCompanion.exe");
        static string Shortcut => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static bool IsRunningInstalledCopy() =>
            string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Exe),
                StringComparison.OrdinalIgnoreCase);

        // minimized: a background self-update, so the new copy stays in the tray.
        public static void InstallSelf(bool minimized = false)
        {
            Directory.CreateDirectory(Dir);
            File.Copy(Application.ExecutablePath, Exe, overwrite: true);

            try { CreateShortcut(); } catch { /* nice to have */ }
            try { RegisterUninstall(); } catch { /* nice to have */ }

            Process.Start(new ProcessStartInfo(Exe, minimized ? "--minimized" : "") { UseShellExecute = true });
        }

        static void CreateShortcut()
        {
            // WScript.Shell via late binding, so there's no COM interop DLL to ship.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut });
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Exe });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Dir });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Uploads your Ranked Duels duels automatically" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }

        static void RegisterUninstall()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Hampa1337");
                key.SetValue("DisplayIcon", Exe);
                key.SetValue("InstallLocation", Dir);
                key.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        public static bool StartsWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(AppName) != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(AppName, $"\"{Exe}\" --minimized");
                    else key.DeleteValue(AppName, throwOnMissingValue: false);
                }
            }
        }

        // Returns false if the player changed their mind.
        public static bool Uninstall(bool interactive)
        {
            if (interactive && MessageBox.Show(
                    "Remove Ranked Duels Companion from this PC?\n\nYour duels stay on the website.",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            try { StartsWithWindows = false; } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
            try { File.Delete(Shortcut); } catch { }
            try { Directory.Delete(Settings.Dir, recursive: true); } catch { }

            if (interactive)
                MessageBox.Show("Ranked Duels Companion was removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // The .exe can't delete itself while running, so a hidden cmd
            // waits a moment and removes the folder after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Dir}\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            });
            return true;
        }
    }
}
