using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LaserCAM.SolidWorks
{
    /// <summary>
    /// "Did this part change?" decided from what makes its flat pattern, read from the data the add-in dumps for LaserCAM:
    /// the feature tree (type and name of every real feature) and the sheet metal parameters (thickness, radius,
    /// K-factor or bend allowance, every bend, the flat pattern options). The shape itself is added by the caller
    /// (mass properties of the bodies).
    ///
    /// Deliberately NOT part of it (owner 2026-11-18: "SolidWorks offers to save although nothing changed; a repeated
    /// send must not make the same flat patterns again"): file date and size (saving a file changes them without
    /// changing the part), the "modified" mark (SolidWorks sets it by the flat pattern export itself), suppression
    /// states (the export flattens and folds the part), the configuration list, cut list and mass numbers (rebuilt by
    /// the export), the sketches SolidWorks itself keeps under the flat pattern, and the last digits of numbers.
    /// </summary>
    public static class StableSig
    {
        static readonly string[] ParamObjects = { "sheetMetal", "oneBend", "jog", "flatPattern", "customBendAllowance" };

        /// <summary>Folders, lights and the like: not features of the part; cut list folders are remade on a rebuild.</summary>
        static bool Housekeeping(string type)
        {
            return type.EndsWith("Folder", StringComparison.Ordinal) || type.EndsWith("Light", StringComparison.Ordinal) || type == "DetailCabinet";
        }

        /// <summary>Under the flat pattern: sketches (bend lines, bounding box) and bend entries SolidWorks keeps itself.</summary>
        static bool OwnOfFlatPattern(string type) { return type == "ProfileFeature" || type == "UiBend"; }

        static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is string ? (string)v : "";
        }

        static string Value(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "1" : "0";
            if (v is string) return (string)v;
            if (v is int || v is long || v is decimal || v is double || v is float)
                return Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("G6", CultureInfo.InvariantCulture);
            return Json.Write(v);
        }

        static void Walk(object features, StringBuilder skeleton, StringBuilder pars, bool underFlat)
        {
            foreach (object o in Json.Arr(features))
            {
                var f = Json.Obj(o);
                if (f == null) continue;
                string type = Text(f, "type"), name = Text(f, "name");
                if (!Housekeeping(type) && !(underFlat && OwnOfFlatPattern(type))) skeleton.Append(type).Append(':').Append(name).Append(';');
                foreach (string key in ParamObjects)
                {
                    object data;
                    if (!f.TryGetValue(key, out data)) continue;
                    var d = Json.Obj(data);
                    if (d == null) continue;
                    var keys = new List<string>(d.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    pars.Append(type).Append(':').Append(name).Append('.').Append(key).Append('{');
                    foreach (string k in keys)
                    {
                        if (k.IndexOf("SketchSegmentCount", StringComparison.Ordinal) >= 0) continue;   // depends on whether the part is flattened now
                        pars.Append(k).Append('=').Append(Value(d[k])).Append(';');
                    }
                    pars.Append('}');
                }
                object subs;
                if (f.TryGetValue("subFeatures", out subs)) Walk(subs, skeleton, pars, underFlat || type == "FlatPattern");
            }
        }

        /// <summary>The two halves of the signature as text (for the log: which of them changed). False = no features in the data.</summary>
        public static bool Parts(Dictionary<string, object> partDoc, out string skeleton, out string parameters)
        {
            skeleton = ""; parameters = "";
            object features;
            if (partDoc == null || !partDoc.TryGetValue("features", out features)) return false;
            var s = new StringBuilder();
            var p = new StringBuilder();
            try { Walk(features, s, p, false); } catch { return false; }
            skeleton = s.ToString(); parameters = p.ToString();
            return skeleton.Length > 0;
        }

        /// <summary>Signature of one document section of the data ("parts" entry or "document"); "" = unknown, never reused.</summary>
        public static string Of(Dictionary<string, object> partDoc)
        {
            string skeleton, parameters;
            return Parts(partDoc, out skeleton, out parameters) ? ExportCache.Hash(skeleton + "\n" + parameters) : "";
        }
    }
}
