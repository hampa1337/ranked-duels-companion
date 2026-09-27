using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace RankedDuelsCompanion
{
    // Self-update: download the new .exe from the GitHub release, check it
    // against the release's SHA256SUMS.txt and its version, then start it.
    // The new copy installs itself over this one the same way a normal
    // install does (Program.Main: not the installed copy -> quit us, copy, start).
    static class Updater
    {
        const string Releases = "https://github.com/hampa1337/ranked-duels-companion/releases/download";

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RankedDuelsCompanion/" + Install.Version);
            return c;
        }

        // Returns false if anything didn't check out; nothing is started then.
        public static async Task<bool> DownloadAndStart(string version, bool minimized)
        {
            try
            {
                var tag = "v" + version;
                var exeBytes = await http.GetByteArrayAsync($"{Releases}/{tag}/RankedDuelsCompanion.exe");
                var sums = await http.GetStringAsync($"{Releases}/{tag}/SHA256SUMS.txt");
                var expected = sums.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) return false;

                var dir = Path.Combine(Path.GetTempPath(), "RankedDuelsCompanion-update");
                Directory.CreateDirectory(dir);
                var exe = Path.Combine(dir, "RankedDuelsCompanion.exe");
                File.WriteAllBytes(exe, exeBytes);

                // The file must really be the version we were told about.
                var v = FileVersionInfo.GetVersionInfo(exe);
                if ($"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}" != version) return false;

                Process.Start(new ProcessStartInfo(exe, minimized ? "--minimized" : "") { UseShellExecute = false });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
