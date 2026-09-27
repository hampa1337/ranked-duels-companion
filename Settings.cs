using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RankedDuelsCompanion
{
    // %APPDATA%\Ranked Duels Companion\settings.json. The login token is
    // encrypted with Windows (DPAPI), so only this Windows user can read it.
    class Settings
    {
        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Install.AppName);
        static string FilePath => Path.Combine(Dir, "settings.json");

        public string SessionProtected { get; set; }
        public string Battletag { get; set; }
        public string WowFolder { get; set; }              // only when the player picked one by hand
        public Dictionary<string, string> UploadedHashes { get; set; } = new Dictionary<string, string>();
        public DateTime? LastSyncUtc { get; set; }
        public string LastResult { get; set; }
        public bool Installed { get; set; }                // first-run setup (start with Windows) done
        public bool TrayHintShown { get; set; }

        [ScriptIgnore] // never write the plain token to disk
        public string Session
        {
            get
            {
                if (string.IsNullOrEmpty(SessionProtected)) return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(SessionProtected), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return null;
                }
            }
            set
            {
                SessionProtected = value == null ? null : Convert.ToBase64String(
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        s.UploadedHashes = s.UploadedHashes ?? new Dictionary<string, string>();
                        return s;
                    }
                }
            }
            catch { /* corrupt file: start fresh */ }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch { /* next save will try again */ }
        }
    }
}
