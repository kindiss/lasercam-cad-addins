// Port of the Odoo module's export (`controllers/main.py: _collect`, `_product_dxf`) through the Odoo JSON-2 API:
// BOM CSV + work center CSV in the exact format the LaserCAM app reads, plus the DXF / STEP / PDF attachments of the
// product AND of the bill of materials, each tagged with its source (LaserCAM keeps one source per part - its setting).
// Works with Odoo 14+ (operations on the BOM); Odoo Online is always the latest version.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LaserCAM.SolidWorks;

namespace LaserCAM.OdooConnector
{
    public class BomInfo
    {
        public int Id, TmplId, ProductId;
        public string Code = "", ProductName = "", ShortCode = "";
        public List<Dictionary<string, object>> Lines = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Ops = new List<Dictionary<string, object>>();
        public string SheetName = "";       // first component whose name looks like "T x W x H"
        public int DxfCount;               // DXF / STEP attachments on the product + on the BOM
        public int PdfAttId;               // drawing PDF (product first, else BOM) for the row's PDF button; 0 = none
        public string PdfName = "";
        public string LaserOp = "";
    }

    /// One job file: DXF / STEP / PDF drawing; Source = "product" | "bom" (module 5.5 rule, LaserCAM picks by its setting).
    public class OdooFile
    {
        public string Name, B64, Source;
    }

    public class ExportPackage
    {
        public string BomCsv = "", WcCsv = "";
        public List<OdooFile> Dxfs = new List<OdooFile>();
        public List<string> SkippedPdfs = new List<string>();   // drawings left out to keep the job within the server budget
        public List<string> Codes = new List<string>();
        public int TemplateTmplId;
    }

    public static class OdooExport
    {
        static readonly Regex CodeRun = new Regex(@"\d{3,}(?:-\d+)?");
        static readonly Regex SheetRe = new Regex(@"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)");

        public static string Xid(string model, int id) { return "__export__." + model.Replace('.', '_') + "_" + id; }
        public static string ShortCode(string defaultCode) { var m = CodeRun.Match(defaultCode ?? ""); return m.Success ? m.Value : ""; }

        static string Q(object cell)
        {
            string s = cell == null ? "" : Convert.ToString(cell, CultureInfo.InvariantCulture);
            return s.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        static string Csv(List<string[]> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows) sb.Append(string.Join(",", r.Select(Q))).Append("\r\n");
            return sb.ToString();
        }

        /// BOM list for the picker: search by product code / name (empty = newest 100).
        public static List<BomInfo> Search(OdooClient o, string query, int limit = 100)
        {
            var domain = new List<object>();
            if (!string.IsNullOrWhiteSpace(query))
            {
                domain.Add("|");
                domain.Add(new object[] { "product_tmpl_id.default_code", "ilike", query.Trim() });
                domain.Add(new object[] { "product_tmpl_id.name", "ilike", query.Trim() });
            }
            var boms = o.SearchRead("mrp.bom", domain.ToArray(), new[] { "id", "product_tmpl_id", "product_id", "code" }, limit);
            var list = new List<BomInfo>();
            if (boms.Count == 0) return list;
            var tmplIds = boms.Select(b => Json.M2oId(b["product_tmpl_id"])).Where(i => i > 0).Distinct().ToArray();
            var tmpls = o.SearchRead("product.template", new object[] { new object[] { "id", "in", tmplIds } }, new[] { "id", "name", "default_code" }, tmplIds.Length);
            var tmplById = tmpls.ToDictionary(t => Convert.ToInt32(t["id"]));
            var bomIds = boms.Select(b => Convert.ToInt32(b["id"])).ToArray();
            var lines = o.SearchRead("mrp.bom.line", new object[] { new object[] { "bom_id", "in", bomIds } }, new[] { "id", "bom_id", "product_id", "product_qty" }, 5000);
            var ops = o.SearchRead("mrp.routing.workcenter", new object[] { new object[] { "bom_id", "in", bomIds } }, new[] { "id", "bom_id", "name", "workcenter_id", "time_cycle_manual" }, 5000);
            var geomDom = new object[] { "|", "|", new object[] { "name", "=ilike", "%.dxf" }, new object[] { "name", "=ilike", "%.step" }, new object[] { "name", "=ilike", "%.stp" } };
            var atts = o.SearchRead("ir.attachment", new object[] { "|", "&", new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "in", tmplIds }, "&", new object[] { "res_model", "=", "mrp.bom" }, new object[] { "res_id", "in", bomIds }, "|", new object[] { "name", "=ilike", "%.pdf" } }.Concat(geomDom).ToArray(), new[] { "id", "res_id", "res_model", "name" }, 5000);
            foreach (var b in boms)
            {
                var bi = new BomInfo { Id = Convert.ToInt32(b["id"]), TmplId = Json.M2oId(b["product_tmpl_id"]), ProductId = Json.M2oId(b["product_id"]) };
                Dictionary<string, object> t;
                if (tmplById.TryGetValue(bi.TmplId, out t)) { bi.Code = Json.Str(t, "default_code"); bi.ProductName = Json.Str(t, "name"); }
                bi.ShortCode = ShortCode(bi.Code);
                bi.Lines = lines.Where(l => Json.M2oId(l["bom_id"]) == bi.Id).ToList();
                bi.Ops = ops.Where(x => Json.M2oId(x["bom_id"]) == bi.Id).ToList();
                foreach (var l in bi.Lines) { string n = Json.M2oName(l["product_id"]); if (SheetRe.IsMatch(n)) { bi.SheetName = n; break; } }
                Func<Dictionary<string, object>, bool> mine = a => (Json.Str(a, "res_model") == "product.template" && Convert.ToInt32(a["res_id"]) == bi.TmplId) || (Json.Str(a, "res_model") == "mrp.bom" && Convert.ToInt32(a["res_id"]) == bi.Id);
                bi.DxfCount = atts.Count(a => mine(a) && !Json.Str(a, "name").ToLowerInvariant().EndsWith(".pdf"));
                var pdf = PickPdf(atts.Where(a => mine(a) && Json.Str(a, "res_model") == "product.template"), bi.ShortCode) ?? PickPdf(atts.Where(a => mine(a) && Json.Str(a, "res_model") == "mrp.bom"), bi.ShortCode);
                if (pdf != null) { bi.PdfAttId = Convert.ToInt32(pdf["id"]); bi.PdfName = Json.Str(pdf, "name"); }
                var op = PickLaserOp(bi);
                bi.LaserOp = op != null ? Json.M2oName(op["workcenter_id"]) : "";
                list.Add(bi);
            }
            return list;
        }

        /// Module rule: the laser op is the one whose work center is "Laser <code>" (or contains the code); else the first op.
        static Dictionary<string, object> PickLaserOp(BomInfo b)
        {
            string laserName = b.ShortCode != "" ? "Laser " + b.ShortCode : "";
            if (b.ShortCode != "")
                foreach (var o in b.Ops)
                {
                    string wcn = Json.M2oName(o["workcenter_id"]) ?? "";
                    if (wcn.Trim() == laserName || wcn.Contains(b.ShortCode)) return o;
                }
            return b.Ops.Count > 0 ? b.Ops[0] : null;
        }

        /// Attachment content as base64. Odoo <= 19: `datas`; Odoo 20 removed it - `raw` comes back as {filename, content, size}.
        static string AttachmentB64(OdooClient o, int id)
        {
            try
            {
                var d = o.SearchRead("ir.attachment", new object[] { new object[] { "id", "=", id } }, new[] { "datas" }, 1);
                return d.Count > 0 ? Json.Str(d[0], "datas") : "";
            }
            catch (Exception) { /* Odoo 20: Invalid field 'datas' */ }
            var r = o.SearchRead("ir.attachment", new object[] { new object[] { "id", "=", id } }, new[] { "raw" }, 1);
            if (r.Count == 0) return "";
            var raw = r[0]["raw"] as Dictionary<string, object>;
            return raw != null ? Json.Str(raw, "content") : Convert.ToString(r[0]["raw"] ?? "");
        }

        /// Drawing PDF among attachments (newest first by id): the newest whose name holds the code, else the newest PDF.
        static Dictionary<string, object> PickPdf(IEnumerable<Dictionary<string, object>> atts, string shortCode)
        {
            var pdfs = atts.Where(a => Json.Str(a, "name").ToLowerInvariant().EndsWith(".pdf")).OrderByDescending(a => Convert.ToInt32(a["id"])).ToList();
            if (pdfs.Count == 0) return null;
            var withCode = shortCode != "" ? pdfs.FirstOrDefault(a => Json.Str(a, "name").Contains(shortCode)) : null;
            return withCode ?? pdfs[0];
        }

        /// Files of one BOM (module 5.5 `_bom_files`): from the product (template or variant) AND from the BOM - the newest .dxf,
        /// the newest STEP (.step/.stp) and the drawing PDF of each, tagged with the source. LaserCAM keeps one whole source per part
        /// (its "files on the product / on the BOM" setting) and asks DXF-or-STEP when both are there. Product files first.
        static List<OdooFile> BomFiles(OdooClient o, BomInfo b)
        {
            var list = new List<OdooFile>();
            var sources = new[]
            {
                new { src = "product", dom = new object[] { "|", "&", new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "=", b.TmplId }, "&", new object[] { "res_model", "=", "product.product" }, new object[] { "res_id", "=", b.ProductId } } },
                new { src = "bom", dom = new object[] { new object[] { "res_model", "=", "mrp.bom" }, new object[] { "res_id", "=", b.Id } } },
            };
            foreach (var src in sources)
            {
                var atts = o.SearchRead("ir.attachment", src.dom, new[] { "id", "name" }, 50).OrderByDescending(a => Convert.ToInt32(a["id"])).ToList();
                var picked = new List<Dictionary<string, object>>();
                var dxf = atts.FirstOrDefault(a => Json.Str(a, "name").ToLowerInvariant().EndsWith(".dxf"));
                var step = atts.FirstOrDefault(a => { var l = Json.Str(a, "name").ToLowerInvariant(); return l.EndsWith(".step") || l.EndsWith(".stp"); });
                var pdf = PickPdf(atts, b.ShortCode);
                foreach (var a in new[] { dxf, step, pdf }) if (a != null) picked.Add(a);
                foreach (var a in picked)
                {
                    string b64 = AttachmentB64(o, Convert.ToInt32(a["id"]));
                    if (b64 == "") continue;
                    string fname = Json.Str(a, "name");
                    // the app links files <-> BOM by the code in the file name
                    if (b.ShortCode != "" && !fname.Contains(b.ShortCode)) fname = b.ShortCode + "_" + fname;
                    list.Add(new OdooFile { Name = fname, B64 = b64, Source = src.src });
                }
            }
            return list;
        }

        /// The row's drawing PDF -> %TEMP%\LaserCAM\<name> (overwritten); null when the BOM has none. The caller opens it.
        public static string DownloadPdf(OdooClient o, BomInfo b)
        {
            if (b.PdfAttId <= 0) return null;
            string b64 = AttachmentB64(o, b.PdfAttId);
            if (b64 == "") return null;
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LaserCAM");
            System.IO.Directory.CreateDirectory(dir);
            string name = string.Join("_", (b.PdfName == "" ? "drawing.pdf" : b.PdfName).Split(System.IO.Path.GetInvalidFileNameChars()));
            string path = System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllBytes(path, Convert.FromBase64String(b64));
            return path;
        }

        /// Drawings must never block nesting: geometry always goes, PDFs only while the job stays within the LaserCAM server
        /// budget (20 MB of base64 / 200 files per job - we keep a margin). Skipped drawing names are collected for the status line.
        public static List<OdooFile> FitPdfs(List<OdooFile> files, long maxB64, int maxFiles, List<string> skipped)
        {
            Func<OdooFile, bool> isPdf = f => f.Name.ToLowerInvariant().EndsWith(".pdf");
            long bytes = files.Where(f => !isPdf(f)).Sum(f => (long)f.B64.Length);
            int count = files.Count(f => !isPdf(f));
            var kept = new List<OdooFile>();
            foreach (var f in files)
            {
                if (!isPdf(f)) { kept.Add(f); continue; }
                if (bytes + f.B64.Length <= maxB64 && count + 1 <= maxFiles) { bytes += f.B64.Length; count++; kept.Add(f); }
                else skipped.Add(f.Name);
            }
            return kept;
        }

        /// `_collect`: BOM rows + WC rows + DXF files for the selected BOMs.
        public static ExportPackage Collect(OdooClient o, List<BomInfo> boms)
        {
            var pkg = new ExportPackage();
            var bomRows = new List<string[]> { new[] { "External ID", "BoM Lines/ID", "BoM Lines/Product Quantity", "BoM Lines/Display Name", "Routing/Work Centers/Name" } };
            var wcSeen = new Dictionary<string, string[]>();
            var wcOrder = new List<string>();
            var dxfNames = new HashSet<string>();
            foreach (var b in boms)
            {
                string bomXid = Xid("mrp.bom", b.Id);
                if (b.ShortCode != "" && !pkg.Codes.Contains(b.ShortCode)) pkg.Codes.Add(b.ShortCode);
                if (pkg.TemplateTmplId == 0) pkg.TemplateTmplId = b.TmplId;   // module: the exported product becomes the copy() template
                foreach (var f in BomFiles(o, b)) // DXF / STEP / PDF from the product and the BOM (LaserCAM picks the source)
                {
                    string name = f.Name, baseName = name; int i = 1;
                    while (dxfNames.Contains(name)) { int dot = baseName.LastIndexOf('.'); name = dot > 0 ? baseName.Substring(0, dot) + "_" + i + baseName.Substring(dot) : baseName + "_" + i; i++; }
                    dxfNames.Add(name);
                    pkg.Dxfs.Add(new OdooFile { Name = name, B64 = f.B64, Source = f.Source });
                }
                var op = PickLaserOp(b);
                string laserName = b.ShortCode != "" ? "Laser " + b.ShortCode : "";
                string opName = op != null ? Json.Str(op, "name") : "";
                string wcName = op != null ? Json.M2oName(op["workcenter_id"]) : "";
                if (wcName == "") wcName = opName;
                double timeH = op != null ? Json.Num(op, "time_cycle_manual") / 60.0 : 0.0;
                string exportName = laserName != "" ? laserName : wcName;
                if (op != null)
                {
                    string targetXid = Xid("mrp.routing.workcenter", Convert.ToInt32(op["id"]));
                    if (!wcSeen.ContainsKey(targetXid)) { wcSeen[targetXid] = new[] { wcName, "0", timeH.ToString(CultureInfo.InvariantCulture) }; wcOrder.Add(targetXid); }
                }
                if (b.Lines.Count == 0) { bomRows.Add(new[] { bomXid, "", "", "", exportName }); continue; }
                bool first = true;
                foreach (var l in b.Lines)
                {
                    bomRows.Add(new[]
                    {
                        first ? bomXid : "",
                        Convert.ToString(l["id"], CultureInfo.InvariantCulture),
                        Json.Num(l, "product_qty").ToString(CultureInfo.InvariantCulture),
                        Json.M2oName(l["product_id"]),
                        first ? exportName : "",
                    });
                    first = false;
                }
            }
            var wcRows = new List<string[]> { new[] { "External ID", "Name", "Capacity per Cycle", "Time for 1 cycle (hour)" } };
            foreach (var xid in wcOrder) { var v = wcSeen[xid]; wcRows.Add(new[] { xid, v[0], v[1], v[2] }); }
            pkg.BomCsv = Csv(bomRows);
            pkg.WcCsv = Csv(wcRows);
            pkg.Dxfs = FitPdfs(pkg.Dxfs, 18L * 1024 * 1024, 200, pkg.SkippedPdfs);
            return pkg;
        }
    }
}
