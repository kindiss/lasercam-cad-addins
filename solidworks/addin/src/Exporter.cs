// "Send to LaserCAM" flow - a one-to-one port of the ExportToOdoo.bas macro Main().
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
    public partial class Exporter
    {
        readonly ISldWorks _sw;
        readonly Settings _s;
        readonly OdooClient _odoo;
        readonly LaserCamClient _lc;

        // Odoo BOM sheet line (e.g. "[00002] Lakstas DC01 3x1250x2500")
        string _sheetName = ""; double _sheetThick, _sheetW, _sheetH; int _sheetProductId;
        int _tmpl;
        bool _useOdoo = true;   // false: LaserCAM only (the Odoo address and key are both empty, or "LaserCAM only" chosen in the Send dialog)
        bool LcKeySet { get { return !string.IsNullOrEmpty(_s.LC_KEY) && !string.IsNullOrEmpty(_s.LC_PID) && _s.LC_PID != "0"; } }

        static readonly Regex SheetRe = new Regex(@"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)");

        public Exporter(ISldWorks sw, Settings s)
        {
            _sw = sw; _s = s;
            _odoo = new OdooClient(s.ODOO_URL, s.ODOO_API_KEY, s.ODOO_DB);
            _lc = new LaserCamClient(s.LC_API, s.LC_PID, s.LC_KEY);
        }

        ProgressForm _prog;   // current progress window
        /// <summary>Owner of our message boxes: the SolidWorks main window, so a message is in front as soon as SolidWorks
        /// is (also from its taskbar button); without it - the progress window. When another program is in front (the
        /// browser with LaserCAM), the SolidWorks taskbar button flashes so the waiting message is noticed.</summary>
        IWin32Window Owner
        {
            get
            {
                IntPtr main = MainWindow();
                if (main == IntPtr.Zero) return _prog != null ? _prog.OwnerOrNull : null;
                if (Attention.OtherProgramInFront()) Attention.Flash(main);
                return new HwndOwner(main);
            }
        }
        /// <summary>The SolidWorks main window.</summary>
        IntPtr MainWindow()
        {
            try { var f = _sw.Frame() as IFrame; if (f != null) return new IntPtr(f.GetHWndx64()); } catch { /* next way */ }
            try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }
        /// <summary>Keeps the progress window above the SolidWorks window while part and drawing windows open for the export.</summary>
        void KeepAbove(ProgressForm prog)
        {
            IntPtr main = MainWindow();
            if (main != IntPtr.Zero) prog.StayAbove = new HwndOwner(main);
        }
        DialogResult Ask(string text, MessageBoxButtons b = MessageBoxButtons.YesNo, MessageBoxIcon i = MessageBoxIcon.Question)
        {
            return MessageBox.Show(Owner, text, AddIn.Title, b, i);
        }
        void Info(string text, string title = null) { MessageBox.Show(Owner, text, title ?? AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void Err(string text) { MessageBox.Show(Owner, text, AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }

        /// <summary>
        /// Owner 2026-11-23: "the program must not change it by itself, it must only say that the number is not the
        /// right one". Until then picking an Odoo product with another reference silently overwrote the file's Number
        /// property. Now the property is left alone: the user is told both values and decides in SolidWorks.
        /// <paramref name="used"/> is the number the sending started with (the property, or the file name when it is empty).
        /// </summary>
        void TellNumberDiffers(IModelDoc2 model, string used, string picked)
        {
            if (_s.NumberFrom != Settings.FromProperty)
            {
                // The Odoo number is the file name (or the title): a product code that is that name plus a stage ending
                // (17511-...-P-BEND for 17511-...-P) is the same product - nothing to tell.
                if (picked.StartsWith(used, StringComparison.OrdinalIgnoreCase)) return;
                Log.Write("number differs: " + NameNumberWord + " = " + used + ", chosen Odoo product = " + picked + " - nothing is changed");
                Info("The SolidWorks " + NameNumberWord + " is \"" + used + "\", but the chosen Odoo product has the reference \"" + picked + "\".\n\n"
                    + "Nothing was changed. This sending goes to the chosen product.");
                return;
            }
            string prop = CustomProp(model, _s.NUMBER_PROPERTY);
            if (prop == "False" || prop == "false") prop = "";
            string inFile = prop != "" ? "is \"" + prop + "\"" : "is empty (\"" + used + "\" was taken from the file name)";
            Log.Write("number differs: property " + _s.NUMBER_PROPERTY + " " + (prop != "" ? "= " + prop : "is empty") + ", chosen Odoo product = " + picked + " - the property is not changed");
            Info("The custom property \"" + _s.NUMBER_PROPERTY + "\" of this file " + inFile + ", but the chosen Odoo product has the reference \"" + picked + "\".\n\n"
                + "The property was NOT changed. This sending goes to the chosen product.\n"
                + "If the number in the file is wrong, correct it in SolidWorks yourself.");
        }

        public void Run(IModelDoc2 model)
        {
            if (model == null) { Ask("Open a sheet metal part or an assembly first.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
            // A drawing: send the part or the assembly it shows (its window becomes the active one).
            if (model.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            {
                var shown = ModelOfDrawing(model);
                if (shown == null) { Ask("This drawing shows no part or assembly.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
                try { int err = 0; _sw.ActivateDoc2(shown.GetTitle(), true, ref err); } catch { /* send it without its own window */ }
                model = shown;
            }
            // An assembly: everything goes in one step (data + a flat pattern of every sheet metal part) - see AssemblySend.cs.
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY) { RunAssembly(model); return; }
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART) { Ask("The active document is not a part or an assembly.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
            // A single part goes the same way as an assembly (owner 2026-11-20: "one path" - the part must get its coating,
            // bending, STEP and data file like an assembly does). Only the silent quote without the browser (OPEN_APP off)
            // still uses the older single-part steps below.
            if (_s.OPEN_APP) { RunAssembly(model); return; }
            // No Odoo address and no Odoo key = LaserCAM only: no product list, nothing read from or written to Odoo.
            _useOdoo = !(string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) && string.IsNullOrEmpty((_s.ODOO_API_KEY ?? "").Trim()));
            if (!_useOdoo && !LcKeySet)
            {
                Ask("LaserCAM is not set up yet. Fill in the LaserCAM settings one of two ways:\n\n"
                    + "1. LaserCAM only (no Odoo): LC_PID and LC_KEY - your LaserCAM key from laser.ucase.eu (Basic plan). The flat pattern opens in LaserCAM for nesting.\n\n"
                    + "2. With Odoo: ODOO_URL and ODOO_API_KEY (in Odoo: My profile > Account security > New API key). Add LC_PID and LC_KEY too if you want nesting, weight and time in LaserCAM.\n\n"
                    + "The settings window opens now; after saving, click Send to LaserCAM again.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var f = new SettingsForm(_s)) { if (Attention.ShowDialogInFront(f, MainWindow()) == DialogResult.OK) f.Result.Save(); }
                return;
            }
            if (_useOdoo &&(string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) || string.IsNullOrEmpty((_s.ODOO_API_KEY ?? "").Trim())))
            {
                // first run without the Odoo address / key: explain what is needed and open Settings (no bare HTTP 401 later)
                Ask("LaserCAM is not connected to Odoo yet - " + (string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) ? "the Odoo address (ODOO_URL)" : "the Odoo API key (ODOO_API_KEY)") + " is missing.\n\n"
                    + "Fill in the LaserCAM settings:\n"
                    + "- ODOO_URL: your Odoo address, e.g. https://company.odoo.com\n"
                    + "- ODOO_API_KEY: in Odoo open My profile > Account security > New API key, copy it once and paste it here (keys are stored encrypted and cannot be viewed again)\n"
                    + "- ODOO_DB: only for on-premise Odoo with several databases\n\n"
                    + "With both filled in you can still send to LaserCAM only - it is a choice in the Send dialog.\n\n"
                    + "The settings window opens now; after saving, click Send to LaserCAM again.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var f = new SettingsForm(_s)) { if (Attention.ShowDialogInFront(f, MainWindow()) == DialogResult.OK) f.Result.Save(); }
                return;
            }
            using (var prog = new ProgressForm("LaserCAM - sending " + CleanTitle(model.GetTitle())))
            {
                _prog = prog;
                KeepAbove(prog);
                try { RunSteps(model, prog); }
                finally { prog.Hide(); _prog = null; }
            }
        }

        void RunSteps(IModelDoc2 model, ProgressForm prog)
        {
            prog.Step(3, "Reading the part...");
            ShapeReader.Reset();   // the part may have been edited since the last command

            string title = CleanTitle(model.GetTitle());
            // The Odoo number: the file name, the title or the custom property (setting ODOO_NUMBER_FROM, owner 2026-11-23).
            // With the file name or the title there is nothing to ask for and nothing is written into the part.
            string number = _s.NumberFrom != Settings.FromProperty ? NameNumber(model) : CustomProp(model, _s.NUMBER_PROPERTY);
            if (number == "False" || number == "false") number = "";   // 1.0.6 wrote Odoo's empty code as "False"
            if (number == "" && !_useOdoo) number = title != "" ? title : "part";   // without Odoo the number is not needed: the file name will do
            if (number == "")
            {
                number = Prompt("The part has no custom property \"" + _s.NUMBER_PROPERTY + "\". Enter the Odoo number (Internal Reference):", "");
                if (number == "") return;
                SetCustomProp(model, _s.NUMBER_PROPERTY, number);
            }
            double swThick = SheetThickness(model);

            // Odoo product first: its BOM sheet line (or the template product's) defines material + thickness + sheet.
            int pid = 0;
            bool created = false;
            int templateTmpl = 0;   // chosen TEMPLATE product (template id): its BOM sheet gives material/thickness/sheet
            SendAction action = SendAction.LaserCAM;
            bool fDxf = false, fStep = false, fPdf = false, fCad = false;
            // The Send dialog (always with Odoo): what to do + files + the Odoo product / template list.
            if (_useOdoo)
            {
            prog.Step(10, "Looking up the Odoo product " + number + "...");
            pid = OdooFindProduct(number);
            using (var pick = new ProductPickerForm(_odoo, _s, number, title, pid))
            {
                prog.Hide();
                var dr = Attention.ShowDialogInFront(pick, MainWindow());
                if (dr != DialogResult.OK || pick.Mode == PickMode.Cancel) return;
                prog.Step(20, "Odoo product chosen...");
                action = pick.Action; fDxf = pick.SendDxf; fStep = pick.SendStep; fPdf = pick.SendPdf; fCad = pick.SendCad;
                _s.SEND_MODE = action == SendAction.OdooFiles ? "odoo" : action == SendAction.LaserCamOnly ? "lasercam-only" : "lasercam";
                if (action != SendAction.LaserCamOnly) { _s.ATTACH_DXF = fDxf; _s.ATTACH_STEP = fStep; _s.ATTACH_PDF = fPdf; _s.ATTACH_CAD = fCad; }
                try { _s.Save(); } catch { }
                if (action == SendAction.LaserCamOnly)
                {
                    // "LaserCAM only" in the Send dialog: no product is picked or created, and from here on nothing is
                    // read from or written to Odoo - the same path as with empty Odoo settings.
                    _useOdoo = false; pid = 0;
                }
                else if (pick.Mode == PickMode.Product)
                {
                    pid = pick.ProductId;
                    _tmpl = pick.TemplateId;
                    if (!string.IsNullOrEmpty(pick.Code) && pick.Code != number)
                    {
                        // Owner 2026-11-23: the add-in never changes the Number property by itself - it only says that
                        // the number differs. This sending goes to the picked product; the file stays as it is.
                        TellNumberDiffers(model, number, pick.Code);
                        number = pick.Code;
                    }
                }
                else
                {
                    if (pick.Mode == PickMode.Template) templateTmpl = pick.TemplateId;
                    if (pid == 0) { pid = OdooCreateProduct(number, title); created = true; }
                }
            }
            if (_useOdoo)
            {
                if (pid == 0) { Err("Could not find or create the Odoo product."); return; }
                if (_tmpl == 0) _tmpl = OdooTemplateOf(pid);
            }
            }
            if (action == SendAction.OdooFiles)
            {
                // Direct send: export the ticked files and attach them to the product. No nesting, no LaserCAM job.
                string attachedDirect = UploadAttachments(model, number, prog, fDxf, fStep, fPdf, fCad);
                prog.Step(100, "Done.");
                Info("Odoo product " + (created ? "CREATED" : "updated") + ": " + number + "\n\nAttached: " + (attachedDirect == "" ? "nothing (no file could be exported)" : attachedDirect), "LaserCAM -> Odoo");
                return;
            }
            if (!LcKeySet)
            {
                prog.Hide();
                MessageBox.Show(Owner, "Sending to LaserCAM (nesting, weight, time) needs your LaserCAM key: Settings -> LC_PID and LC_KEY (Basic plan, laser.ucase.eu). Sending files straight to the Odoo product works without it.", AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string sheetSource = "";
            if (_useOdoo && _s.USE_ODOO_SHEET)
            {
                prog.Step(25, "Reading the Odoo bill of materials (sheet)...");
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
            };
            if (_useOdoo) part["odooProductId"] = pid;   // the product is already known: the server updates it by id, never by name
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
                if (_useOdoo && _s.SEND_ODOO_FROM_APP)
                {
                    var od = new Dictionary<string, object> { { "url", _s.ODOO_URL }, { "apiKey", _s.ODOO_API_KEY } };
                    if (_s.ODOO_DB != "") od["db"] = _s.ODOO_DB;
                    body["odoo"] = od;
                }
                if (_sheetProductId > 0) body["sheetProductId"] = _sheetProductId;
                if (_s.WAIT_FOR_RESULT) body["wait"] = true;   // LaserCAM shows "Send result to SolidWorks" only then
                // The part's drawing (<part>.SLDDRW next to it) travels as PDF: LaserCAM checks the holes and threads against it.
                try
                {
                    prog.Step(50, "Exporting the drawing to PDF (when the part has one)...");
                    string drwDir = Path.Combine(Path.GetTempPath(), "lasercam_assembly");
                    Directory.CreateDirectory(drwDir);
                    string drwPdf = DrawingPdfOf(model.GetPathName() ?? "", drwDir);
                    if (drwPdf != "")
                    {
                        byte[] pdfBytes = File.ReadAllBytes(drwPdf);
                        if (pdfBytes.Length <= MaxDrawingBytes)
                            body["drawings"] = new object[] { new Dictionary<string, object> { { "name", SafeName(number) + ".pdf" }, { "b64", Convert.ToBase64String(pdfBytes) } } };
                    }
                }
                catch { /* the job goes without the drawing */ }
                prog.Step(55, "Creating the LaserCAM job (uploading the DXF)...");
                resp = _lc.Post("/job", body);
                string jobId = Json.Str(resp, "id"), jobUrl = Json.Str(resp, "url");
                if (jobId == "" || jobUrl == "") { Err("LaserCAM did not return a job id."); return; }
                prog.Step(65, "Opening LaserCAM in the browser...");
                Process.Start(AppUrlOf(jobUrl, jobId));
                prog.Step(100, "Done.");
                if (!_s.WAIT_FOR_RESULT)
                {
                    Info("LaserCAM opened in the browser with " + number + " (" + material + ", " + Json.NumStr(thick) + " mm, sheet " + Json.NumStr(shW) + " x " + Json.NumStr(shH) + ").\n\n" +
                         (!_useOdoo ? "Nest it there, then download the cutting file (DXF) or the PDF report."
                          : _s.SEND_ODOO_FROM_APP ? "Nest it there and click \"Save to Odoo\" - weight, DXF, BOM sheet quantity and laser time go straight to Odoo."
                                               : "Nest it there and click \"Send result to SolidWorks\", then run Send to LaserCAM again with WAIT_FOR_RESULT on to copy the numbers into the part."));
                    return;
                }
                var t0 = DateTime.Now;
                while (true)
                {
                    Thread.Sleep(3000);
                    Application.DoEvents();
                    resp = _lc.GetResult(jobId);
                    if (resp != null) break;
                    if ((DateTime.Now - t0).TotalMinutes > _s.APP_WAIT_MIN) { Ask("No result from LaserCAM after " + _s.APP_WAIT_MIN + " minutes. Run Send to LaserCAM again when the nesting is done.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
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
            if (_useOdoo && !odooByApp)
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

            Info((!_useOdoo ? "LaserCAM quote" : "Odoo product " + (created ? "CREATED" : "updated") + (odooByApp ? " (by LaserCAM)" : "")) + ": " + number + "\n\n" +
                 "Thickness " + Json.NumStr(thick) + " mm, " + material + ", " + gas + "  [" + sheetSource + "]\n" +
                 "Sheet " + Json.NumStr(shW) + " x " + Json.NumStr(shH) + " mm\n" +
                 "Net weight: " + Json.NumStr(netKg) + " kg\n" +
                 "With waste: " + Json.NumStr(usedKg) + " kg\n" +
                 "Cutting time: " + Json.NumStr(secs) + " s\n" +
                 "Price: " + Json.NumStr(price) + " EUR\n" +
                 "Parts per sheet: " + Json.NumStr(perSheet) + " (" + algo + ")" +
                 (warnTail != "" ? "\n" + warnTail : "") +
                 (warn != "" ? "\n\nWarning: " + warn : ""), _useOdoo ? "LaserCAM -> Odoo" : "LaserCAM");
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
            // A plain plate (no sheet metal feature, thickness from the custom property): there is no flat pattern, its
            // outline is the largest flat face (owner 2026-11-23: such parts went to Odoo without DXF and STEP).
            // A body that is seen not to be flat (the person chose "sheet part" for a part bent without the sheet metal
            // feature): its largest face is one flange, not its outline - no DXF is made; its STEP still goes.
            if (SheetMetalThickness(model) <= 0) return !ShapeReader.Of(model).NotFlat && ExportPlateDxf(model, path);
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

        /// <summary>
        /// The outline of a plain plate: its largest flat face with every hole in it, exported as seen along the face normal
        /// (ExportToDWG2, Action 2 = the selected faces). The alignment is the face's own plane, so the drawing lies flat.
        /// </summary>
        static bool ExportPlateDxf(IModelDoc2 model, string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var partDoc = model as IPartDoc;
                if (partDoc == null) return false;
                IFace2 best = null;
                double bestArea = 0;
                var bodies = partDoc.GetBodies2(0, true) as object[];   // 0 = solid bodies, visible only
                if (bodies == null) return false;
                foreach (object bo in bodies)
                {
                    var body = bo as IBody2;
                    var faces = body != null ? body.GetFaces() as object[] : null;
                    if (faces == null) continue;
                    foreach (object fo in faces)
                    {
                        var face = fo as IFace2;
                        var surf = face != null ? face.GetSurface() as ISurface : null;
                        if (surf == null || !surf.IsPlane()) continue;
                        double area = face.GetArea();
                        if (area > bestArea) { bestArea = area; best = face; }
                    }
                }
                if (best == null) return false;
                var plane = ((ISurface)best.GetSurface()).PlaneParams as double[];   // normal x y z, a point x y z
                if (plane == null || plane.Length < 6) return false;
                double[] n = UnitVec(new[] { plane[0], plane[1], plane[2] });
                if (n == null) return false;
                // Any direction in the plane serves as X: LaserCAM lays the part out itself.
                double[] x = UnitVec(CrossVec(Math.Abs(n[2]) < 0.9 ? new double[] { 0, 0, 1 } : new double[] { 1, 0, 0 }, n));
                if (x == null) return false;
                double[] y = CrossVec(n, x);
                var align = new double[] { plane[3], plane[4], plane[5], x[0], x[1], x[2], y[0], y[1], y[2], n[0], n[1], n[2] };
                model.ClearSelection2(true);
                if (!((IEntity)best).Select4(false, null)) return false;
                bool ok = partDoc.ExportToDWG2(path, model.GetPathName(), 2, true, align, false, false, 0, null);
                model.ClearSelection2(true);
                return ok && File.Exists(path);
            }
            catch { return false; }
        }

        static double[] CrossVec(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        static double[] UnitVec(double[] v)
        {
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return len < 1e-12 ? null : new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        /// <summary>
        /// Thickness of a sheet part in mm; 0 = not a sheet part. From the sheet metal feature when there is one. A part
        /// without it is measured (ShapeClassifier.cs, owner 2026-11-23): only a flat plate of one thickness is cut from
        /// sheet - a milled or turned part or a tube is not, whatever its "Thickness" property says. A standard
        /// (Toolbox) part is never one, and neither is a plate with no material and no "Thickness" property (a bought
        /// pad or shim). When the shape cannot be told (several bodies, free-form, a block of one thickness), the person
        /// chooses the type at the start of a send (owner 2026-11-24, AskPartTypes); "sheet part" chosen: the thickness
        /// is the measured one when the body allowed it, else the "Thickness" property. Only where nobody was asked
        /// (the silent quote) the property decides as before.
        /// LaserCAM applies the same rules to the data file (assembly/fromSolidworks.ts).
        /// </summary>
        static double SheetThickness(IModelDoc2 model)
        {
            double t = SheetMetalThickness(model);
            if (t > 0) return t;
            if (IsToolbox(model)) return 0;
            PartShape shape = ShapeReader.Of(model);
            double prop = PlateThickness(model);
            if (shape.Kind == PartShape.Plate) return prop > 0 || HasMaterial(model) ? shape.ThicknessMm : 0;
            if (shape.Kind != PartShape.Unknown) return 0;
            if (shape.Chosen == "") return prop;
            return shape.Chosen == PartShape.Plate ? (shape.ThicknessMm > 0 ? shape.ThicknessMm : prop) : 0;
        }

        /// <summary>Why a part without a flat pattern is not sent as a sheet part, for the message to the user.</summary>
        static string NotSheetText(IModelDoc2 model)
        {
            if (IsToolbox(model)) return "This is a standard (Toolbox) part: there is no flat pattern to send.";
            PartShape shape = ShapeReader.Of(model);
            if (shape.Kind == PartShape.Plate) return "This part is a " + shape.Text() + ", but it has no material and no \"Thickness\" property, so it is taken as a bought part. Set its material and send again.";
            if (shape.Kind == PartShape.Unknown)
            {
                if (shape.Chosen == PartShape.Plate) return "This part was chosen as a sheet part, but its thickness could not be measured. Add the custom property \"Thickness\" (mm) and send again.";
                int at = PartTypes.IndexOf(shape.Chosen);
                if (at >= 0) return "This part was chosen as: " + PartTypes.Kinds[at][1] + ". There is no flat pattern to send.\n\nTo choose its type again, hold Shift when clicking Send.";
                return "This part is not a sheet metal part: there is no flat pattern to send.";
            }
            return "This part is not cut from sheet: it was recognised as a " + shape.Text() + ". There is no flat pattern to send.";
        }

        /// <summary>A standard part of the SolidWorks Toolbox (also a copied one, also by its folder).</summary>
        static bool IsToolbox(IModelDoc2 model)
        {
            try { if (model.Extension.ToolboxPartType != 0) return true; } catch { /* decided by the folder */ }
            try { return System.Text.RegularExpressions.Regex.IsMatch(model.GetPathName() ?? "", @"[\\/]solidworks data[^\\/]*[\\/]browser[\\/]", System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return false; }
        }

        /// <summary>The part has a material in any of its configurations.</summary>
        static bool HasMaterial(IModelDoc2 model)
        {
            var part = model as IPartDoc;
            if (part == null) return false;
            try
            {
                string db;
                if ((part.GetMaterialPropertyName2("", out db) ?? "").Trim() != "") return true;
                var names = model.GetConfigurationNames() as string[];
                if (names != null)
                    foreach (string cfg in names)
                        if ((part.GetMaterialPropertyName2(cfg, out db) ?? "").Trim() != "") return true;
            }
            catch { /* no material */ }
            return false;
        }

        static double PlateThickness(IModelDoc2 model)
        {
            double t;
            string raw = CustomProp(model, "Thickness").Replace(',', '.');
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out t) && t > 0 && t < 200 ? Math.Round(t, 3) : 0;
        }

        static double SheetMetalThickness(IModelDoc2 model)
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
                return Attention.ShowDialogInFront(f, IntPtr.Zero) == DialogResult.OK ? tb.Text.Trim() : "";
            }
        }
    }
}
