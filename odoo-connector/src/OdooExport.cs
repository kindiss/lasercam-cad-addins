// Port of the Odoo module's export (`controllers/main.py: _collect`, `_product_dxf`) through the Odoo JSON-2 API:
// BOM CSV + work center CSV in the exact format the LaserCAM app reads, plus the product DXF attachments.
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
        public int DxfCount;
        public string LaserOp = "";
    }

    public class ExportPackage
    {
        public string BomCsv = "", WcCsv = "";
        public List<KeyValuePair<string, string>> Dxfs = new List<KeyValuePair<string, string>>(); // name, base64
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
            var atts = o.SearchRead("ir.attachment", new object[] { new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "in", tmplIds }, new object[] { "name", "=ilike", "%.dxf" } }, new[] { "id", "res_id" }, 5000);
            foreach (var b in boms)
            {
                var bi = new BomInfo { Id = Convert.ToInt32(b["id"]), TmplId = Json.M2oId(b["product_tmpl_id"]), ProductId = Json.M2oId(b["product_id"]) };
                Dictionary<string, object> t;
                if (tmplById.TryGetValue(bi.TmplId, out t)) { bi.Code = Json.Str(t, "default_code"); bi.ProductName = Json.Str(t, "name"); }
                bi.ShortCode = ShortCode(bi.Code);
                bi.Lines = lines.Where(l => Json.M2oId(l["bom_id"]) == bi.Id).ToList();
                bi.Ops = ops.Where(x => Json.M2oId(x["bom_id"]) == bi.Id).ToList();
                foreach (var l in bi.Lines) { string n = Json.M2oName(l["product_id"]); if (SheetRe.IsMatch(n)) { bi.SheetName = n; break; } }
                bi.DxfCount = atts.Count(a => Convert.ToInt32(a["res_id"]) == bi.TmplId);
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

        /// Newest .dxf attachment of the product (template or variant) -> (file name with the code, base64) or null.
        static KeyValuePair<string, string>? ProductDxf(OdooClient o, BomInfo b)
        {
            var domain = new object[]
            {
                "|",
                "&", new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "=", b.TmplId },
                "&", new object[] { "res_model", "=", "product.product" }, new object[] { "res_id", "=", b.ProductId },
            };
            var atts = o.SearchRead("ir.attachment", domain, new[] { "id", "name" }, 50);
            foreach (var a in atts.OrderByDescending(a => Convert.ToInt32(a["id"])))
            {
                string fname = Json.Str(a, "name");
                if (!fname.ToLowerInvariant().EndsWith(".dxf")) continue;
                var data = o.SearchRead("ir.attachment", new object[] { new object[] { "id", "=", Convert.ToInt32(a["id"]) } }, new[] { "datas" }, 1);
                string b64 = data.Count > 0 ? Json.Str(data[0], "datas") : "";
                if (b64 == "") continue;
                // the app links DXF <-> BOM by the code in the file name
                if (b.ShortCode != "" && !fname.Contains(b.ShortCode)) fname = b.ShortCode + "_" + fname;
                return new KeyValuePair<string, string>(fname, b64);
            }
            return null;
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
                var dxf = ProductDxf(o, b);
                if (dxf != null)
                {
                    string name = dxf.Value.Key, baseName = name; int i = 1;
                    while (dxfNames.Contains(name)) { int dot = baseName.LastIndexOf('.'); name = dot > 0 ? baseName.Substring(0, dot) + "_" + i + baseName.Substring(dot) : baseName + "_" + i; i++; }
                    dxfNames.Add(name);
                    pkg.Dxfs.Add(new KeyValuePair<string, string>(name, dxf.Value.Value));
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
            return pkg;
        }
    }
}
