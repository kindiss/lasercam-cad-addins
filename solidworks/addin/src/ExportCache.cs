// Reuse of unchanged exports (owner 2026-11-17): a flat pattern DXF or a drawing PDF made for an earlier send is
// taken again when nothing it depends on has changed, so sending the same product twice does not wait for SolidWorks.
// This file is the plain part: keys, file stamps, the folder with stored files. What "unchanged" means for a part
// or a drawing is decided in AssemblySend.cs.
// Folder: %APPDATA%\LaserCAM\cache (this PC only). Files not used for 45 days are removed.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LaserCAM.SolidWorks
{
    static class ExportCache
    {
        /// <summary>Raised whenever the way DXF or PDF files are made changes: older stored files are then not used.</summary>
        public const string Version = "2";

        public static string DefaultDir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM"), "cache"); }
        }

        /// <summary>One key from everything the export depends on. Every part is length-prefixed, so "a","bc" differs from "ab","c".</summary>
        public static string Key(params string[] parts)
        {
            var sb = new StringBuilder();
            if (parts != null) foreach (string p in parts) { string x = p ?? ""; sb.Append(x.Length).Append(':').Append(x).Append('\n'); }
            return Hash(sb.ToString());
        }

        public static string Hash(string text)
        {
            using (var sha = SHA1.Create())
            {
                var sb = new StringBuilder(40);
                foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Last write time and size of a file; "" when it does not exist. A saved change always changes it.</summary>
        public static string FileStamp(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "";
                var f = new FileInfo(path);
                return f.Exists ? f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + f.Length.ToString(CultureInfo.InvariantCulture) : "";
            }
            catch { return ""; }
        }

        /// <summary>The stored file for this key, or "". Using a file marks it as used now (see Sweep).</summary>
        public static string Find(string dir, string key, string ext)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return "";
                string path = Path.Combine(dir, key + ext);
                var f = new FileInfo(path);
                if (!f.Exists || f.Length == 0) return "";
                try { f.LastWriteTimeUtc = DateTime.UtcNow; } catch { /* still usable */ }
                return path;
            }
            catch { return ""; }
        }

        /// <summary>Keeps a copy of an exported file under the key. Failing to store never fails the send.</summary>
        public static bool Store(string dir, string key, string ext, string sourceFile)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !File.Exists(sourceFile)) return false;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, key + ext);
                string tmp = path + ".tmp";
                File.Copy(sourceFile, tmp, true);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Removes stored files not used for longer than <paramref name="maxAge"/>; returns how many.</summary>
        public static int Sweep(string dir, TimeSpan maxAge, DateTime nowUtc)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { if (nowUtc - File.GetLastWriteTimeUtc(f) > maxAge) { File.Delete(f); removed++; } }
                    catch { /* in use: next time */ }
                }
            }
            catch { /* no folder access: nothing is removed */ }
            return removed;
        }

        /// <summary>
        /// Numbers as text that does not change with calculation noise: each value relative to the largest one in the
        /// group (at least <paramref name="minScale"/>), rounded to 7 digits. A real change of the model changes it.
        /// </summary>
        public static string NumbersSig(double[] values, double minScale)
        {
            if (values == null || values.Length == 0) return "";
            double scale = minScale;
            foreach (double v in values) if (!double.IsNaN(v) && !double.IsInfinity(v)) scale = Math.Max(scale, Math.Abs(v));
            var sb = new StringBuilder();
            sb.Append(scale <= minScale ? "0" : scale.ToString("G7", CultureInfo.InvariantCulture));
            foreach (double v in values)
            {
                double r = double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v / scale, 7);
                if (r == 0) r = 0;   // no "-0"
                sb.Append(';').Append(r.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
