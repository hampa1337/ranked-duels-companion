using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RankedDuelsCompanion
{
    // Finds World of Warcraft and every account's "Ranked Duels.lua"
    // (WTF\Account\<ACCOUNT>\SavedVariables\Ranked Duels.lua).
    static class SaveFiles
    {
        public const string FileName = "Ranked Duels.lua";

        // Returns the _retail_ folder, or null if WoW wasn't found.
        public static string FindRetail(string pickedByHand)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(pickedByHand)) candidates.Add(pickedByHand);
            candidates.Add(FromRegistry(RegistryView.Registry32));
            candidates.Add(FromRegistry(RegistryView.Registry64));
            candidates.Add(@"C:\Program Files (x86)\World of Warcraft");
            candidates.Add(@"C:\Program Files\World of Warcraft");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                candidates.Add(Path.Combine(drive.Name, "World of Warcraft"));
                candidates.Add(Path.Combine(drive.Name, "Games", "World of Warcraft"));
                candidates.Add(Path.Combine(drive.Name, "Program Files (x86)", "World of Warcraft"));
                candidates.Add(Path.Combine(drive.Name, "Battle.net", "World of Warcraft"));
            }
            foreach (var c in candidates)
            {
                var retail = AsRetail(c);
                if (retail != null) return retail;
            }
            return null;
        }

        // Accepts the WoW folder, the _retail_ folder, or anything inside it.
        public static string AsRetail(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            try
            {
                var dir = new DirectoryInfo(folder.Trim().TrimEnd('\\', '/'));
                for (var d = dir; d != null; d = d.Parent)
                {
                    if (d.Name.Equals("_retail_", StringComparison.OrdinalIgnoreCase) && d.Exists) return d.FullName;
                    var inner = Path.Combine(d.FullName, "_retail_");
                    if (Directory.Exists(inner)) return inner;
                }
            }
            catch { }
            return null;
        }

        static string FromRegistry(RegistryView view)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\World of Warcraft"))
                    return key?.GetValue("InstallPath") as string;
            }
            catch { return null; }
        }

        public static string AccountFolder(string retail) => Path.Combine(retail, "WTF", "Account");

        public static List<string> Find(string retail)
        {
            var result = new List<string>();
            var accounts = AccountFolder(retail);
            if (!Directory.Exists(accounts)) return result;
            foreach (var acc in Directory.GetDirectories(accounts))
            {
                var f = Path.Combine(acc, "SavedVariables", FileName);
                if (File.Exists(f)) result.Add(f);
            }
            return result;
        }

        // WoW may be writing the file right now, so share and retry.
        public static string Read(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(400);
                }
            }
        }

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        // "WTF\Account\123456#1" -> "123456#1", for showing which account a file is.
        public static string AccountName(string savePath) =>
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(savePath)));
    }
}
