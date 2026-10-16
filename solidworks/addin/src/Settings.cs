// Add-in settings: %APPDATA%\LaserCAM\solidworks-settings.json (same keys as the VBA macro constants).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LaserCAM.SolidWorks
{
    public class Settings
    {
        public string LC_API = "https://laser.ucase.eu/api";
        public string LC_PID = "0";            // Odoo contact id (res.partner) that holds the LaserCAM plan
        public string LC_KEY = "";             // LaserCAM key from that contact
        public string ODOO_URL = "";           // your Odoo address, e.g. https://mycompany.odoo.com
        public string ODOO_API_KEY = "";       // Odoo user API key
        public string ODOO_DB = "";            // empty = default database (Odoo Online)
        public string NUMBER_PROPERTY = "Number";
        public string TEMPLATE_NUMBER = "";    // product whose BOM sheet is used when the part's product has no BOM yet
        public bool USE_ODOO_SHEET = true;
        public bool ALWAYS_PICK_PRODUCT = true;
        public string SEND_MODE = "lasercam";  // last choice in the Send dialog: lasercam | odoo (files straight to the product)
        public bool ATTACH_DXF = true;     // Send dialog defaults (direct send): DXF flat pattern
        public bool ATTACH_STEP = true;    // STEP export
        public bool ATTACH_PDF = true;     // drawing (<part>.SLDDRW next to the part) as PDF, when it exists
        public bool ATTACH_CAD = true;     // the part file itself (.SLDPRT)   // true: always show the Odoo product/template list before sending; false: only when the number is not found
        public string DEFAULT_GAS = "O2";
        public double SHEET_W = 1250;
        public double SHEET_H = 2500;
        public double SHEET_MARGIN = 5;
        public double SPACING = 5;
        public double PRICE_PER_KG = 0.9;
        public int QUANTITY = 1;
        public bool OPEN_APP = true;           // true: open LaserCAM in the browser; false: silent /api/quote + write to Odoo from here
        public bool SEND_ODOO_FROM_APP = true; // true: LaserCAM "Save to Odoo" writes to Odoo (credentials travel with the job)
        public bool WAIT_FOR_RESULT = false;   // true: wait for the browser result and copy it into the part custom properties
        public int APP_WAIT_MIN = 30;

        public static string Path
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM");
                return System.IO.Path.Combine(dir, "solidworks-settings.json");
            }
        }

        /// Secrets are stored encrypted with Windows DPAPI (current user): "dpapi:<base64>". Only the same Windows
        /// account on the same PC can read them back; the file itself shows no key. Plain values from older files are
        /// accepted and re-saved encrypted.
        static readonly string[] SecretKeys = { "LC_KEY", "ODOO_API_KEY" };
        const string Prefix = "dpapi:";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LaserCAM-settings-v1");

        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try { return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return plain; }
        }
        static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix)) return stored ?? "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }   // another user / PC: the secret is simply empty -> the dialog asks again
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(Path))
                {
                    var ser = new JavaScriptSerializer();
                    var d = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path));
                    bool hadPlain = false;
                    if (d != null)
                    {
                        foreach (var k in SecretKeys)
                        {
                            object v;
                            if (d.TryGetValue(k, out v) && v is string)
                            {
                                string sv = (string)v;
                                if (sv != "" && !sv.StartsWith(Prefix)) hadPlain = true;
                                d[k] = Unprotect(sv);
                            }
                        }
                        s.Apply(d);
                    }
                    if (hadPlain) { try { s.Save(); } catch { } }   // upgrade: encrypt the old plain-text keys
                }
                else s.Save();
            }
            catch { /* unreadable file -> defaults */ }
            return s;
        }

        public void Save()
        {
            string dir = System.IO.Path.GetDirectoryName(Path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var d = ToDictionary();
            foreach (var k in SecretKeys) { object v; if (d.TryGetValue(k, out v)) d[k] = Protect(v as string); }
            var ser = new JavaScriptSerializer();
            File.WriteAllText(Path, ser.Serialize(d));
        }

        public Dictionary<string, object> ToDictionary()
        {
            var d = new Dictionary<string, object>();
            foreach (var f in typeof(Settings).GetFields())
                if (f.IsPublic && !f.IsStatic) d[f.Name] = f.GetValue(this);
            return d;
        }

        public void Apply(Dictionary<string, object> d)
        {
            foreach (var f in typeof(Settings).GetFields())
            {
                object v;
                if (!f.IsPublic || f.IsStatic || !d.TryGetValue(f.Name, out v) || v == null) continue;
                try { f.SetValue(this, Convert.ChangeType(v, f.FieldType, System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            }
        }
    }
}
