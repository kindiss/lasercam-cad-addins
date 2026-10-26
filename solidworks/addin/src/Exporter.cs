// "Send to Odoo" flow - a one-to-one port of the ExportToOdoo.bas macro Main().
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace LaserCAM.SolidWorks
{
    public class Exporter
    {
        readonly ISldWorks _sw;
        readonly Settings _s;
        readonly OdooClient _odoo;
        readonly LaserCamClient _lc;

        // Odoo BOM sheet line (e.g. "[00002] Lakstas DC01 3x1250x2500")
        string _sheetName = ""; double _sheetThick, _sheetW, _sheetH; int _sheetProductId;
        int _tmpl;

        static readonly Regex SheetRe = new Regex(@"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)");

        public Exporter(ISldWorks sw, Settings s)
        {
            _sw = sw; _s = s;
            _odoo = new OdooClient(s.ODOO_URL, s.ODOO_API_KEY, s.ODOO_DB);
            _lc = new LaserCamClient(s.LC_API, s.LC_PID, s.LC_KEY);
        }

        ProgressForm _prog;   // current progress window: message boxes are owned by it so they show on top of it
        IWin32Window Owner { get { return _prog != null ? _prog.OwnerOrNull : null; } }
        DialogResult Ask(string text, MessageBoxButtons b = MessageBoxButtons.YesNo, MessageBoxIcon i = MessageBoxIcon.Question)
        {
            return MessageBox.Show(Owner, text, AddIn.Title, b, i);
        }
        void Info(string text, string title = null) { MessageBox.Show(Owner, text, title ?? AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void Err(string text) { MessageBox.Show(Owner, text, AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }

        public void Run(IModelDoc2 model)
        {
            if (model == null) { Ask("Open a sheet metal part first.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART) { Ask("The active document is not a part.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
            if (string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) || string.IsNullOrEmpty((_s.ODOO_API_KEY ?? "").Trim()))
            {
                // first run without the Odoo address / key: explain what is needed and open Settings (no bare HTTP 401 later)
                Ask("LaserCAM is not connected to Odoo yet - " + (string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) ? "the Odoo address (ODOO_URL)" : "the Odoo API key (ODOO_API_KEY)") + " is missing.\n\n"
                    + "Fill in the LaserCAM settings:\n"
                    + "- ODOO_URL: your Odoo address, e.g. https://company.odoo.com\n"
                    + "- ODOO_API_KEY: in Odoo open My profile > Account security > New API key, copy it once and paste it here (keys are stored encrypted and cannot be viewed again)\n"
                    + "- ODOO_DB: only for on-premise Odoo with several databases\n\n"
                    + "The settings window opens now; after saving, click Send to Odoo again.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var f = new SettingsForm(_s)) f.ShowDialog();
                return;
            }
            using (var prog = new ProgressForm("LaserCAM - sending " + CleanTitle(model.GetTitle())))
            {
                _prog = prog;
                try { RunSteps(model, prog); }
                finally { prog.Hide(); _prog = null; }
            }
        }

        void RunSteps(IModelDoc2 model, ProgressForm prog)
        {
            prog.Step(3, "Reading the part...");

            string title = CleanTitle(model.GetTitle());
            string number = CustomProp(model, _s.NUMBER_PROPERTY);
            if (number == "False" || number == "false") number = "";   // 1.0.6 wrote Odoo's empty code as "False"
            if (number == "")
            {
                number = Prompt("The part has no custom property \"" + _s.NUMBER_PROPERTY + "\". Enter the Odoo number (Internal Reference):", "");
                if (number == "") return;
                SetCustomProp(model, _s.NUMBER_PROPERTY, number);
            }
            double swThick = SheetThickness(model);

            // Odoo product first: its BOM sheet line (or the template product's) defines material + thickness + sheet.
            prog.Step(10, "Looking up the Odoo product " + number + "...");
            int pid = OdooFindProduct(number);
            bool created = false;
            int templateTmpl = 0;   // chosen TEMPLATE product (template id): its BOM sheet gives material/thickness/sheet
            SendAction action;
            bool fDxf, fStep, fPdf, fCad;
            // The Send dialog (always): what to do + files + the Odoo product / template list.
            using (var pick = new ProductPickerForm(_odoo, _s, number, title, pid))
            {
                prog.Hide();
                var dr = pick.ShowDialog();
                if (dr != DialogResult.OK || pick.Mode == PickMode.Cancel) return;
                prog.Step(20, "Odoo product chosen...");
                action = pick.Action; fDxf = pick.SendDxf; fStep = pick.SendStep; fPdf = pick.SendPdf; fCad = pick.SendCad;
                _s.SEND_MODE = action == SendAction.OdooFiles ? "odoo" : "lasercam";
                _s.ATTACH_DXF = fDxf; _s.ATTACH_STEP = fStep; _s.ATTACH_PDF = fPdf; _s.ATTACH_CAD = fCad;
                try { _s.Save(); } catch { }
                if (pick.Mode == PickMode.Product)
                {
                    pid = pick.ProductId;
                    _tmpl = pick.TemplateId;
                    if (!string.IsNullOrEmpty(pick.Code) && pick.Code != number)
                    {
                        number = pick.Code;   // the picked reference becomes the part's number
                        SetCustomProp(model, _s.NUMBER_PROPERTY, number);
                    }
                }
                else
                {
                    if (pick.Mode == PickMode.Template) templateTmpl = pick.TemplateId;
                    if (pid == 0) { pid = OdooCreateProduct(number, title); created = true; }
                }
            }
            if (pid == 0) { Err("Could not find or create the Odoo product."); return; }
            if (_tmpl == 0) _tmpl = OdooTemplateOf(pid);
            if (action == SendAction.OdooFiles)
            {
                // Direct send: export the ticked files and attach them to the product. No nesting, no LaserCAM job.
                string attachedDirect = UploadAttachments(model, number, prog, fDxf, fStep, fPdf, fCad);
                prog.Step(100, "Done.");
                Info("Odoo product " + (created ? "CREATED" : "updated") + ": " + number + "\n\nAttached: " + (attachedDirect == "" ? "nothing (no file could be exported)" : attachedDirect), "LaserCAM -> Odoo");
                return;
            }
            if (string.IsNullOrEmpty(_s.LC_KEY) || _s.LC_PID == "0" || string.IsNullOrEmpty(_s.LC_PID))
            {
                prog.Hide();
                MessageBox.Show(Owner, "Sending to LaserCAM (nesting, weight, time) needs your LaserCAM key: Settings -> LC_PID and LC_KEY (Basic plan, laser.ucase.eu). Sending files straight to the Odoo product works without it.", AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            prog.Step(25, "Reading the Odoo bill of materials (sheet)...");
            string sheetSource = "";
            if (_s.USE_ODOO_SHEET)
            {
                if (OdooSheetOf(_tmpl)) sheetSource = "product BOM";
                else if (templateTmpl > 0 && OdooSheetOf(templateTmpl)) sheetSource = "chosen template";
                else if (_s.TEMPLATE_NUMBER != "")
                {
                    int tp = OdooFindProduct(_s.TEMPLATE_NUMBER);
                    if (tp > 0 && OdooSheetOf(OdooTemplateOf(tp))) sheetSource = "template " + _s.TEMPLATE_NUMBER;
                }
            }

            double thick; string material;
            if (sheetSource != "")
            {
                material = _sheetName;
                thick = _sheetThick;
                if (swThick > 0 && Math.Abs(swThick - thick) > 0.05)
                {
                    if (Ask("SolidWorks thickness " + Json.NumStr(swThick) + " mm differs from the Odoo sheet " + Json.NumStr(thick) + " mm (" + _sheetName + ").\nUse the Odoo sheet thickness?") == DialogResult.No) thick = swThick;
                }
            }
            else
            {
                thick = swThick;
                if (thick <= 0)
                {
                    double.TryParse(Prompt("Sheet metal thickness not found. Enter thickness in mm:", "2").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out thick);
                    if (thick <= 0) return;
                }
                material = CustomProp(model, "LC_Material");   // optional override: LaserCAM material name/alias
                if (material == "")
                {
                    string db;
                    var partDoc = model as IPartDoc;   // material + flat pattern export live on IPartDoc
                    material = partDoc != null ? (partDoc.GetMaterialPropertyName2("", out db) ?? "") : "";
                }
                if (material == "") material = Prompt("The part has no material. Enter the material name from the LaserCAM database:", "Carbon steel");
                sheetSource = "SolidWorks";
            }
            string gas = CustomProp(model, "LC_Gas");
            if (gas == "") gas = _s.DEFAULT_GAS;
            string warnTail = "";
            double shW = _sheetW > 0 ? _sheetW : _s.SHEET_W;
            double shH = _sheetH > 0 ? _sheetH : _s.SHEET_H;

            // 1. Flat pattern DXF
            prog.Step(40, "Exporting the flat pattern DXF...");
            string dxfPath = Path.Combine(Path.GetTempPath(), "lasercam_" + SafeName(number) + ".dxf");
            if (!ExportFlatDxf(model, dxfPath)) { Err("Flat pattern DXF export failed (does the part have a flat pattern?)."); return; }
            string dxf = File.ReadAllText(dxfPath, Encoding.UTF8);

            // 2. LaserCAM
            var part = new Dictionary<string, object>
            {
                { "id", number }, { "name", title }, { "dxf", dxf }, { "quantity", _s.QUANTITY },
                { "thicknessMm", thick }, { "material", material }, { "gas", gas },
                { "odooProductId", pid },   // the product is already known: the server updates it by id, never by name
            };
            var body = new Dictionary<string, object>
            {
                { "parts", new object[] { part } },
                { "sheet", new Dictionary<string, object> { { "width", shW }, { "height", shH }, { "margin", _s.SHEET_MARGIN } } },
                { "spacing", _s.SPACING }, { "pricePerKg", _s.PRICE_PER_KG }, { "fast", true },
            };
            Dictionary<string, object> resp;
            if (_s.OPEN_APP)
            {
                body["number"] = number;
                body["source"] = "SolidWorks";
                if (_s.SEND_ODOO_FROM_APP)
                {
                    var od = new Dictionary<string, object> { { "url", _s.ODOO_URL }, { "apiKey", _s.ODOO_API_KEY } };
                    if (_s.ODOO_DB != "") od["db"] = _s.ODOO_DB;
                    body["odoo"] = od;
                }
                if (_sheetProductId > 0) body["sheetProductId"] = _sheetProductId;
                if (_s.WAIT_FOR_RESULT) body["wait"] = true;   // LaserCAM shows "Send result to SolidWorks" only then
                prog.Step(55, "Creating the LaserCAM job (uploading the DXF)...");
                resp = _lc.Post("/job", body);
                string jobId = Json.Str(resp, "id"), jobUrl = Json.Str(resp, "url");
                if (jobId == "" || jobUrl == "") { Err("LaserCAM did not return a job id."); return; }
                prog.Step(65, "Opening LaserCAM in the browser...");
                Process.Start(jobUrl);
                prog.Step(100, "Done.");
                if (!_s.WAIT_FOR_RESULT)
                {
                    Info("LaserCAM opened in the browser with " + number + " (" + material + ", " + Json.NumStr(thick) + " mm, sheet " + Json.NumStr(shW) + " x " + Json.NumStr(shH) + ").\n\n" +
                         (_s.SEND_ODOO_FROM_APP ? "Nest it there and click \"Save to Odoo\" - weight, DXF, BOM sheet quantity and laser time go straight to Odoo."
                                               : "Nest it there and click \"Send result to SolidWorks\", then run Send to Odoo again with WAIT_FOR_RESULT on to copy the numbers into the part."));
                    return;
                }
                var t0 = DateTime.Now;
                while (true)
                {
                    Thread.Sleep(3000);
                    Application.DoEvents();
                    resp = _lc.GetResult(jobId);
                    if (resp != null) break;
                    if ((DateTime.Now - t0).TotalMinutes > _s.APP_WAIT_MIN) { Ask("No result from LaserCAM after " + _s.APP_WAIT_MIN + " minutes. Run Send to Odoo again when the nesting is done.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
                }
            }
            else
            {
                prog.Step(55, "Computing the quote on the LaserCAM server...");
                resp = _lc.Post("/quote", body);
            }

            // Result: first part (quote) or the job result parts[0]
            var parts = Json.Arr(resp.ContainsKey("parts") ? resp["parts"] : null);
            var p0 = parts.Length > 0 ? Json.Obj(parts[0]) : resp;
            double netKg = Json.Num(p0, "netKgPerPart"), usedKg = Json.Num(p0, "usedKgPerPart"), secs = Json.Num(p0, "secPerPart"), price = Json.Num(p0, "pricePerPart");
            double perSheet = Json.Num(p0, "partsPerSheet");
            string algo = Json.Str(p0, "algo");
            if (_s.OPEN_APP) { perSheet = Json.Num(resp, "sheets"); algo = "sheets used in LaserCAM"; }
            string warn = string.Join("; ", Array.ConvertAll(Json.Arr(resp.ContainsKey("warnings") ? resp["warnings"] : null), w => Convert.ToString(w)));

            // 3. Odoo: write results (skipped when LaserCAM already saved to Odoo itself)
            bool odooByApp = _s.OPEN_APP && resp.ContainsKey("odooDone") && resp["odooDone"] != null;
            if (!odooByApp)
            {
                _odoo.Write("product.product", pid, new Dictionary<string, object> { { "weight", Math.Round(netKg, 4) } });
                var quote = new Dictionary<string, object>
                {
                    { "netKg", netKg }, { "usedKg", usedKg }, { "sec", secs }, { "price", price }, { "partsPerSheet", perSheet },
                    { "thicknessMm", thick }, { "material", material }, { "gas", gas }, { "algo", algo }, { "date", DateTime.Now.ToString("yyyy-MM-dd HH:mm") },
                };
                try { _odoo.Write("product.template", _tmpl, new Dictionary<string, object> { { "x_studio_lasercam_quote", Json.Write(quote) } }); } catch { /* field may not exist */ }
                try
                {
                    _odoo.Create("ir.attachment", new Dictionary<string, object>
                    {
                        { "name", SafeName(number) + ".dxf" }, { "res_model", "product.template" }, { "res_id", _tmpl },
                        { "datas", Convert.ToBase64String(File.ReadAllBytes(dxfPath)) }, { "mimetype", "application/dxf" },
                    });
                }
                catch (Exception ex) { warn += (warn == "" ? "" : "; ") + "DXF attachment failed: " + ex.Message; }
            }

            // 4. Into the part - visible in SolidWorks too
            SetCustomProp(model, "LC_NetKg", Json.NumStr(netKg));
            SetCustomProp(model, "LC_UsedKg", Json.NumStr(usedKg));
            SetCustomProp(model, "LC_CutSec", Json.NumStr(secs));
            SetCustomProp(model, "LC_Price", Json.NumStr(price));

            Info("Odoo product " + (created ? "CREATED" : "updated") + (odooByApp ? " (by LaserCAM)" : "") + ": " + number + "\n\n" +
                 "Thickness " + Json.NumStr(thick) + " mm, " + material + ", " + gas + "  [" + sheetSource + "]\n" +
                 "Sheet " + Json.NumStr(shW) + " x " + Json.NumStr(shH) + " mm\n" +
                 "Net weight: " + Json.NumStr(netKg) + " kg\n" +
                 "With waste: " + Json.NumStr(usedKg) + " kg\n" +
                 "Cutting time: " + Json.NumStr(secs) + " s\n" +
                 "Price: " + Json.NumStr(price) + " EUR\n" +
                 "Parts per sheet: " + Json.NumStr(perSheet) + " (" + algo + ")" +
                 (warnTail != "" ? "\n" + warnTail : "") +
                 (warn != "" ? "\n\nWarning: " + warn : ""), "LaserCAM -> Odoo");
        }

        // ---------------- attachments: part file, STEP, drawing PDF -> Odoo product ----------------
        /// Exports the ticked files and uploads them to the Odoo product (product.template). Returns "a, b, c" or "".
        string UploadAttachments(IModelDoc2 model, string number, ProgressForm prog, bool withDxf, bool withStep, bool withPdf, bool withCad)
        {
            if (_tmpl == 0) return "";
            var files = new List<KeyValuePair<string, string>>();   // name -> path
            string partPath = model.GetPathName() ?? "";
            string stem = SafeName(number);
            if (withDxf)
            {
                prog.Step(40, "Exporting the flat pattern DXF...");
                string dxfPath = Path.Combine(Path.GetTempPath(), "lasercam_" + stem + ".dxf");
                if (ExportFlatDxf(model, dxfPath)) files.Add(new KeyValuePair<string, string>(stem + ".dxf", dxfPath));
            }
            if (withCad)
            {
                if (File.Exists(partPath)) files.Add(new KeyValuePair<string, string>(Path.GetFileName(partPath), partPath));
            }
            if (withStep)
            {
                prog.Step(72, "Exporting STEP...");
                string stepPath = Path.Combine(Path.GetTempPath(), "lasercam_" + stem + ".step");
                try
                {
                    if (File.Exists(stepPath)) File.Delete(stepPath);
                    int e = 0, w = 0;
                    bool ok = model.Extension.SaveAs(stepPath, 0, 0, null, ref e, ref w);
                    if (ok && File.Exists(stepPath)) files.Add(new KeyValuePair<string, string>(stem + ".step", stepPath));
                }
                catch { }
            }
            if (withPdf && File.Exists(partPath))
            {
                string drwPath = Path.ChangeExtension(partPath, ".SLDDRW");
                if (File.Exists(drwPath))
                {
                    prog.Step(78, "Exporting the drawing to PDF...", Path.GetFileName(drwPath));
                    string pdfPath = Path.Combine(Path.GetTempPath(), "lasercam_" + stem + ".pdf");
                    try
                    {
                        if (File.Exists(pdfPath)) File.Delete(pdfPath);
                        int e = 0, w = 0;
                        var drw = _sw.OpenDoc6(drwPath, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
                        if (drw != null)
                        {
                            var pdfData = _sw.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                            if (pdfData != null) pdfData.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
                            int e2 = 0, w2 = 0;
                            bool ok = drw.Extension.SaveAs(pdfPath, 0, 0, pdfData, ref e2, ref w2);
                            _sw.CloseDoc(drw.GetTitle());
                            if (ok && File.Exists(pdfPath)) files.Add(new KeyValuePair<string, string>(stem + ".pdf", pdfPath));
                        }
                    }
                    catch { }
                }
            }
            var done = new List<string>();
            int i = 0;
            foreach (var f in files)
            {
                i++;
                prog.Step(80 + (int)(18.0 * i / files.Count), "Uploading to Odoo (" + i + "/" + files.Count + "): " + f.Key + "...");
                try
                {
                    byte[] bytes = File.ReadAllBytes(f.Value);
                    string b64 = Convert.ToBase64String(bytes);
                    string mime = f.Key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : f.Key.EndsWith(".step", StringComparison.OrdinalIgnoreCase) ? "application/step" : f.Key.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? "application/dxf" : "application/octet-stream";
                    var old = _odoo.SearchRead("ir.attachment", new object[] { new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "=", _tmpl }, new object[] { "name", "=", f.Key } }, new[] { "id" }, 1);
                    if (old.Count > 0) _odoo.Write("ir.attachment", Convert.ToInt32(old[0]["id"]), new Dictionary<string, object> { { "datas", b64 } });
                    else _odoo.Create("ir.attachment", new Dictionary<string, object> { { "name", f.Key }, { "res_model", "product.template" }, { "res_id", _tmpl }, { "type", "binary" }, { "datas", b64 }, { "mimetype", mime } });
                    done.Add(f.Key + " (" + (bytes.Length / 1024) + " KB)");
                }
                catch (Exception ex) { done.Add(f.Key + " FAILED: " + ex.Message); }
            }
            return string.Join(", ", done);
        }

        // ---------------- SolidWorks ----------------
        static bool ExportFlatDxf(IModelDoc2 model, string path)
        {
            // ExportToDWG2: Action 1 = sheet metal flat pattern; SheetMetalOptions bit 1 = geometry (4 = bend lines).
            // Alignment: 12 doubles (origin + X/Y/Z axes); Views: empty.
            var align = new double[12];
            align[3] = 1; align[7] = 1; align[11] = 1;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var partDoc = model as IPartDoc;
                if (partDoc == null) return false;
                bool ok = partDoc.ExportToDWG2(path, model.GetPathName(), 1, true, align, false, false, 1, null);
                return ok && File.Exists(path);
            }
            catch { return false; }
        }

        static double SheetThickness(IModelDoc2 model)
        {
            // First "SheetMetal" feature -> SheetMetalFeatureData.Thickness (meters -> mm).
            var f = model.FirstFeature() as IFeature;
            while (f != null)
            {
                if (f.GetTypeName2() == "SheetMetal")
                {
                    var d = f.GetDefinition() as ISheetMetalFeatureData;
                    if (d != null) return Math.Round(d.Thickness * 1000, 3);
                }
                f = f.GetNextFeature() as IFeature;
            }
            return 0;
        }

        static string CustomProp(IModelDoc2 model, string name)
        {
            try
            {
                ICustomPropertyManager mgr = model.Extension.get_CustomPropertyManager("");
                string v, r;
                mgr.Get4(name, false, out v, out r);
                string res = (r ?? "").Trim();
                return res != "" ? res : (v ?? "").Trim();
            }
            catch { return ""; }
        }

        static void SetCustomProp(IModelDoc2 model, string name, string value)
        {
            try
            {
                ICustomPropertyManager mgr = model.Extension.get_CustomPropertyManager("");
                mgr.Add3(name, 30, value, 1);   // 30 = swCustomInfoText, 1 = replace existing value
            }
            catch { }
        }

        // ---------------- Odoo ----------------
        int OdooFindProduct(string code)
        {
            var r = _odoo.SearchRead("product.product", new object[] { new object[] { "default_code", "=", code } }, new[] { "id" }, 1);
            if (r.Count == 0) r = _odoo.SearchRead("product.product", new object[] { new object[] { "name", "=ilike", code + "%" } }, new[] { "id" }, 1);
            return r.Count > 0 ? Convert.ToInt32(r[0]["id"]) : 0;
        }

        int OdooTemplateOf(int pid)
        {
            // ids go UNQUOTED (numbers) in domains: a string on a many2one field makes Odoo search by name.
            var r = _odoo.SearchRead("product.product", new object[] { new object[] { "id", "=", pid } }, new[] { "product_tmpl_id" }, 1);
            return r.Count > 0 ? Json.M2oId(r[0]["product_tmpl_id"]) : 0;
        }

        /// BOM of a product template -> first component whose name looks like "T x W x H" (thickness = smallest).
        bool OdooSheetOf(int tmplId)
        {
            if (tmplId == 0) return false;
            var boms = _odoo.SearchRead("mrp.bom", new object[] { new object[] { "product_tmpl_id", "=", tmplId } }, new[] { "bom_line_ids" }, 1);
            if (boms.Count == 0) return false;
            var ids = Json.Arr(boms[0].ContainsKey("bom_line_ids") ? boms[0]["bom_line_ids"] : null);
            if (ids.Length == 0) return false;
            var lines = _odoo.SearchRead("mrp.bom.line", new object[] { new object[] { "id", "in", ids } }, new[] { "product_id" }, 80);
            foreach (var l in lines)
            {
                object pv; if (!l.TryGetValue("product_id", out pv)) continue;
                if (ParseSheet(Json.M2oName(pv))) { _sheetProductId = Json.M2oId(pv); return true; }
            }
            return false;
        }

        bool ParseSheet(string text)
        {
            var m = SheetRe.Match(text ?? "");
            if (!m.Success) return false;
            double a = P(m.Groups[1].Value), b = P(m.Groups[2].Value), c = P(m.Groups[3].Value);
            double t = Math.Min(a, Math.Min(b, c));
            if (t <= 0 || t > 100) return false;
            double d1, d2;
            if (t == a) { d1 = b; d2 = c; } else if (t == b) { d1 = a; d2 = c; } else { d1 = a; d2 = b; }
            _sheetName = text; _sheetThick = t; _sheetW = Math.Min(d1, d2); _sheetH = Math.Max(d1, d2);
            return true;
        }
        static double P(string s) { double d; double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out d); return d; }

        /// Product name: the title if it already starts with the number, otherwise "number title".
        int OdooCreateProduct(string code, string title)
        {
            string nm = title.StartsWith(code, StringComparison.OrdinalIgnoreCase) ? title : (code + " " + title).Trim();
            return _odoo.Create("product.product", new Dictionary<string, object> { { "name", nm }, { "default_code", code }, { "type", "consu" } });
        }

        // ---------------- helpers ----------------
        static string CleanTitle(string t)
        {
            t = (t ?? "").Trim();
            return Regex.Replace(t, @"\.(sldprt|sldasm|slddrw)$", "", RegexOptions.IgnoreCase).Trim();
        }
        static string SafeName(string s) { return Regex.Replace(s ?? "", @"[\\/:*?""<>|]", "_"); }

        static string Prompt(string text, string def)
        {
            using (var f = new Form { Text = AddIn.Title, Width = 460, Height = 170, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false })
            {
                if (Branding.AppIcon != null) f.Icon = Branding.AppIcon;
                var lbl = new Label { Left = 12, Top = 12, Width = 420, Height = 40, Text = text };
                var tb = new TextBox { Left = 12, Top = 58, Width = 420, Text = def };
                var ok = new Button { Text = "OK", Left = 250, Top = 92, Width = 85, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 347, Top = 92, Width = 85, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : "";
            }
        }
    }
}
