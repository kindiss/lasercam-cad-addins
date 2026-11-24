// "A newer version is available" check, shared by the SolidWorks add-in and the Odoo Connector.
//
// The add-in asks only the LaserCAM server (GET <LC_API>/addins/version), at most once a day and never on the
// UI thread. The request carries no data about the user, the PC or the files. The answer is kept in
// %APPDATA%\LaserCAM\update-check.json; the offer itself is shown from that file, so it costs no network call.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public static class UpdateCheck
    {
        /// Version of the installer this build ships in. MUST match AppVersion in installer/LaserCAM-Setup.iss
        /// (checked by scripts/addins-version-check.mjs).
        public const string SuiteVersion = "1.4.0";

        const string ReleasesPage = "https://github.com/kindiss/lasercam-cad-addins/releases/latest";
        const string OwnUrlPrefix = "https://github.com/kindiss/lasercam-cad-addins/releases/";
        const int CheckEveryHours = 24;
        const int AskAgainDays = 7;

        public class Info { public string Version = ""; public string Url = ""; }

        static string StatePath
        {
            get
            {
                // LASERCAM_UPDATE_STATE: another file for automated checks, so they never touch the user's real state.
                string forced = Environment.GetEnvironmentVariable("LASERCAM_UPDATE_STATE");
                if (!string.IsNullOrEmpty(forced)) return forced;
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM"), "update-check.json");
            }
        }

        /// > 0 when a is newer than b. Compared number by number ("1.3.11" is newer than "1.3.9").
        public static int Compare(string a, string b)
        {
            string[] pa = (a ?? "").Split('.'), pb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(pa[i], out x);
                if (i < pb.Length) int.TryParse(pb[i], out y);
                if (x != y) return x - y;
            }
            return 0;
        }

        static Dictionary<string, object> LoadState()
        {
            try { if (File.Exists(StatePath)) return Json.Obj(Json.Read(File.ReadAllText(StatePath))) ?? new Dictionary<string, object>(); }
            catch { }
            return new Dictionary<string, object>();
        }

        static void SaveState(Dictionary<string, object> s)
        {
            try
            {
                string dir = Path.GetDirectoryName(StatePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StatePath, Json.Write(s));
            }
            catch { }
        }

        static DateTime Time(Dictionary<string, object> s, string key)
        {
            DateTime t;
            return DateTime.TryParse(Json.Str(s, key), null, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : DateTime.MinValue;
        }

        /// The newer version known from the last check, or null. Reads the state file only - no network.
        public static Info Known()
        {
            var s = LoadState();
            string latest = Json.Str(s, "latest");
            if (latest == "" || Compare(latest, SuiteVersion) <= 0) return null;
            string url = Json.Str(s, "url");
            return new Info { Version = latest, Url = url.StartsWith(OwnUrlPrefix, StringComparison.Ordinal) ? url : ReleasesPage };
        }

        /// Asks the server on a background thread when the last check is older than a day. Never throws, never blocks.
        /// `done` (optional) is called on that background thread after a successful check.
        public static void RefreshInBackground(string apiBase, Action done = null)
        {
            try
            {
                var s = LoadState();
                if ((DateTime.UtcNow - Time(s, "checkedAt")).TotalHours < CheckEveryHours) return;
                string url = (apiBase ?? "").TrimEnd('/') + "/addins/version";
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        int status;
                        string body = Http.Send("GET", url, null, null, out status, 5000);
                        // The server answered (even an old one without this address): ask again tomorrow.
                        var st = LoadState();
                        st["checkedAt"] = DateTime.UtcNow.ToString("o");
                        if (status == 200)
                        {
                            var d = Json.Obj(Json.Read(body));
                            string v = Json.Str(d, "version");
                            if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+){1,3}$")) { st["latest"] = v; st["url"] = Json.Str(d, "url"); }
                        }
                        SaveState(st);
                        if (done != null) done();
                    }
                    catch { /* offline, old server, bad answer: try again tomorrow */ }
                });
            }
            catch { }
        }

        /// Shows "a newer version is available" once per version per week. Yes opens the download in the browser.
        /// Returns true when the question was shown.
        public static bool Offer(IWin32Window owner, string title)
        {
            try
            {
                var info = Known();
                if (info == null) return false;
                var s = LoadState();
                if (Json.Str(s, "declined") == info.Version && (DateTime.UtcNow - Time(s, "declinedAt")).TotalDays < AskAgainDays) return false;
                var answer = MessageBox.Show(owner,
                    "A newer version of the LaserCAM add-ins is available: " + info.Version + " (you have " + SuiteVersion + ").\n\n"
                    + "Download the installer now? Close SolidWorks and Fusion before you run it; your settings are kept.",
                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) Process.Start(info.Url);
                else { s["declined"] = info.Version; s["declinedAt"] = DateTime.UtcNow.ToString("o"); SaveState(s); }
                return true;
            }
            catch { return false; }
        }
    }
}
