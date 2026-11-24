// Send to LaserCAM for an ASSEMBLY: one click sends everything LaserCAM needs -
// the assembly data (the same as "Read data...") and a flat pattern DXF of every sheet metal part,
// made by SolidWorks itself. No files to save or upload by hand.
// LaserCAM opens with the assembly window; each flat pattern fills its own sheet (parts per sheet, weight, time).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace LaserCAM.SolidWorks
{
    public partial class Exporter
    {
        /// <summary>One unique sheet metal part file used in the assembly.</summary>
        class SheetPart
        {
            public IModelDoc2 Doc;
            public string Path = "";
            public string Config = "";
            public int Count;
            public IComponent2 Comp;
            /// <summary>Files of the assemblies that hold this part directly (the sent assembly itself for a top-level part).</summary>
            public readonly HashSet<string> Parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Bends, threads and countersinks of the part, counted from the data read for LaserCAM.</summary>
            public PartFacts Facts = new PartFacts();
        }

        /// <summary>The user closed a setup table with "Cancel sending".</summary>
        sealed class SendCancelled : Exception { }

        // ---- Product setup tables (ProductSetup.cs): state of one send ----
        ProductSetup.Store _setup = new ProductSetup.Store();
        string _asmPath = "";
        string _setupColor = "";
        /// <summary>File path -> answer that goes to LaserCAM with this send (given now or remembered).</summary>
        readonly Dictionary<string, SetupAnswer> _answers = new Dictionary<string, SetupAnswer>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Files answered in this send (the others in <see cref="_answers"/> are remembered answers).</summary>
        readonly HashSet<string> _asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _skipRest;
        /// <summary>Coating of the last part that is not inside a coated assembly: suggested for the next such part.</summary>
        string _lastLoose;
        /// <summary>The last choice of separate Odoo products: suggested for the next part and remembered for the next send.</summary>
        bool _lastBendSeparate, _lastCoatingSeparate;

        // ---- Reuse of unchanged exports (ExportCache.cs): state of one send ----
        /// <summary>Shift held while clicking Send: everything is exported again, nothing is reused.</summary>
        bool _fresh;
        string _cacheDir = "";
        string _swRev = "";
        /// <summary>Hash of everything read from the assembly (without the time of reading) and of all its file stamps.</summary>
        string _wholeSig = "";
        /// <summary>Model file path -> hash of what was read from that document (properties, features, sheet metal data).</summary>
        readonly Dictionary<string, string> _sectionSig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IComponent2> _compByPath = new Dictionary<string, IComponent2>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IModelDoc2> _docByPath = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _modelSig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Model file path -> the part's section of the data read for this send (to see what an export changed in it).</summary>
        readonly Dictionary<string, Dictionary<string, object>> _partData = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Part file path -> its signature after the flat pattern export, when the export itself moved it.</summary>
        readonly Dictionary<string, string> _sigAfterExport = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Part documents whose window had to be shown for the flat pattern export (they were hidden before).</summary>
        readonly List<IModelDoc2> _shownForExport = new List<IModelDoc2>();

        /// <summary>The Odoo number when it is the SolidWorks file name (setting ODOO_NUMBER_FROM = file name): the file name
        /// without the extension, else the window title, else "part". The Number property is not looked at.</summary>
        static string FileNumber(IModelDoc2 model)
        {
            string number = Path.GetFileNameWithoutExtension(model.GetPathName() ?? "") ?? "";
            if (number == "") number = CleanTitle(model.GetTitle());
            return number != "" ? number : "part";
        }

        /// <summary>The Odoo number when it is not a custom property (setting ODOO_NUMBER_FROM): the document title,
        /// or the file name - also when the title is empty. The Number property is not looked at.</summary>
        string NameNumber(IModelDoc2 model)
        {
            if (_s.NumberFrom == Settings.FromTitle)
            {
                string title = "";
                try { title = CleanTitle(model.GetTitle()); } catch { title = ""; }
                if (title != "") return title;
            }
            return FileNumber(model);
        }

        /// <summary>"title" or "file name": the word for the messages about the number.</summary>
        string NameNumberWord { get { return _s.NumberFrom == Settings.FromTitle ? "title" : "file name"; } }

        /// <summary>The number an assembly is sent under: its Number property, else its file name, else its title.</summary>
        string AssemblyNumber(IModelDoc2 model)
        {
            if (_s.NumberFrom != Settings.FromProperty) return NameNumber(model);
            string number = CustomProp(model, _s.NUMBER_PROPERTY);
            if (number == "False" || number == "false") number = "";
            if (number == "") number = Path.GetFileNameWithoutExtension(model.GetPathName() ?? "") ?? "";
            if (number == "") number = CleanTitle(model.GetTitle());
            return number;
        }

        /// <summary>
        /// The Send dialog for an assembly - the same one a single part gets (owner 2026-11-20: "there must be one path,
        /// an assembly must be sent like a single part"): what to do, the Odoo product found by the number, the files.
        /// Returns true when the sending goes on to LaserCAM (the product setup tables follow), false when there is
        /// nothing more to do: cancelled, or the files went straight to the Odoo product.
        /// </summary>
        bool AssemblySendDialog(IModelDoc2 model)
        {
            string title = CleanTitle(model.GetTitle());
            string number = AssemblyNumber(model);
            int pid = 0;
            try { pid = OdooFindProduct(number); }
            catch (Exception ex) { Log.Write("assembly send: the Odoo product lookup failed: " + ex.Message); }
            bool part = !(model is IAssemblyDoc);
            using (var pick = new ProductPickerForm(_odoo, _s, number, title, pid, !part, part))
            {
                var dr = Attention.ShowDialogInFront(pick, MainWindow());
                if (dr != DialogResult.OK || pick.Mode == PickMode.Cancel) return false;
                var action = pick.Action;
                _s.SEND_MODE = action == SendAction.OdooFiles ? "odoo" : action == SendAction.LaserCamOnly ? "lasercam-only" : "lasercam";
                if (action == SendAction.OdooFiles) { if (part) _s.ATTACH_DXF = pick.SendDxf; _s.ATTACH_STEP = pick.SendStep; _s.ATTACH_PDF = pick.SendPdf; _s.ATTACH_CAD = pick.SendCad; }
                try { _s.Save(); } catch { /* the choice is simply not remembered */ }
                if (action == SendAction.LaserCamOnly)
                {
                    // Nothing is read from or written to Odoo from here on: the job travels without the Odoo connection.
                    _useOdoo = false;
                    return true;
                }
                if (pick.Mode == PickMode.Product && !string.IsNullOrEmpty(pick.Code) && pick.Code != number)
                {
                    // As with a part (owner 2026-11-23): the Number property is never changed by the add-in - it only
                    // says that the number differs. This sending goes to the picked product.
                    TellNumberDiffers(model, number, pick.Code);
                    number = pick.Code;
                }
                if (action == SendAction.LaserCAM) return true;

                // Files straight to the Odoo product: no nesting, no LaserCAM job.
                using (var prog = new ProgressForm("LaserCAM - sending " + title))
                {
                    _prog = prog;
                    KeepAbove(prog);
                    try
                    {
                        bool created = false;
                        _tmpl = 0;
                        if (pick.Mode == PickMode.Product) { pid = pick.ProductId; _tmpl = pick.TemplateId; }
                        else if (pid == 0) { prog.Step(20, "Creating the Odoo product " + number + "..."); pid = OdooCreateProduct(number, title); created = true; }
                        if (pid == 0) { Err("Could not find or create the Odoo product."); return false; }
                        if (_tmpl == 0) _tmpl = OdooTemplateOf(pid);
                        string attached = UploadAttachments(model, number, prog, part && pick.SendDxf, pick.SendStep, pick.SendPdf, pick.SendCad);
                        prog.Step(100, "Done.");
                        Info("Odoo product " + (created ? "CREATED" : "updated") + ": " + number + "\n\nAttached: " + (attached == "" ? "nothing (no file could be exported)" : attached), "LaserCAM -> Odoo");
                    }
                    finally { prog.Hide(); _prog = null; }
                }
                return false;
            }
        }

        void RunAssembly(IModelDoc2 model)
        {
            // Shift held when the command was clicked: everything is exported again. Read before any dialog opens.
            _fresh = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            // Odoo address and key both filled in: the Send dialog comes first, as for a single part.
            _useOdoo = !string.IsNullOrEmpty((_s.ODOO_URL ?? "").Trim()) && !string.IsNullOrEmpty((_s.ODOO_API_KEY ?? "").Trim());
            if (_useOdoo && !AssemblySendDialog(model)) return;
            if (!LcKeySet)
            {
                Ask("Sending an assembly needs the LaserCAM key: fill in LC_PID and LC_KEY in the settings (your LaserCAM key from laser.ucase.eu, Basic plan).\n\n"
                    + "The settings window opens now; after saving, click Send to LaserCAM again.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var f = new SettingsForm(_s)) { if (Attention.ShowDialogInFront(f, MainWindow()) == DialogResult.OK) f.Result.Save(); }
                return;
            }
            // With "Send to LaserCAM and Odoo" the Odoo address and key travel with the job, so LaserCAM can create the products in Odoo.
            using (var prog = new ProgressForm("LaserCAM - sending assembly " + CleanTitle(model.GetTitle())))
            {
                _prog = prog;
                KeepAbove(prog);
                try { RunAssemblySteps(model, prog); }
                catch (SendCancelled) { Info("Sending was cancelled. Nothing was sent to LaserCAM."); }
                finally { prog.Hide(); _prog = null; }
            }
        }

        void RunAssemblySteps(IModelDoc2 model, ProgressForm prog)
        {
            var asm = model as IAssemblyDoc;
            // A single part goes the same way as an assembly (owner 2026-11-20: "one path"): it is the product and its only
            // sheet metal part, so it gets the same setup table, coating, bending, STEP and data file in LaserCAM.
            bool single = asm == null;
            if (single && model.GetType() != (int)swDocumentTypes_e.swDocPART) { Err("The active document is not a part or an assembly."); return; }

            // 1. Assembly data: tree, quantities, materials, threads, bends (exactly what "Read data..." saves).
            prog.Step(5, single ? "Reading the part..." : "Reading the assembly...");
            if (asm != null) { try { asm.ResolveAllLightWeightComponents(false); } catch { /* nothing lightweight */ } }
            CollectDocs(model, asm);
            WatchModified("before this send started");
            // 0. Parts whose type could not be told from their shape: the person chooses (owner 2026-11-24). Before the
            // data is read, so the choice is in the data file.
            ShapeReader.Reset();   // every part is measured again: it may have been edited since the last command
            prog.Step(4, "Recognising the parts...");
            if (!AskPartTypes(model, asm)) throw new SendCancelled();
            string dumpText = new Inspector(_sw) { OdooCodeFrom = Inspector.CodeFromOf(_s) }.Run(model);
            // A copy of what this send read, next to the log: what the hidden "Read data..." button used to give for
            // troubleshooting (owner 2026-11-20). Only the last send is kept.
            try
            {
                string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "LaserCAM");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "last-send-data.json"), dumpText, Encoding.UTF8);
            }
            catch { /* the copy is a convenience: the send goes on without it */ }
            object dump = Json.Read(dumpText);
            WatchModified("while reading the assembly data");
            PrepareReuse(model, asm, dump);
            WatchModified("while reading the body properties");

            // 2. Unique sheet metal parts (one flat pattern per part file, however many times it is used).
            prog.Step(8, "Finding the sheet metal parts...");
            var list = new List<SheetPart>();
            var byPath = new Dictionary<string, SheetPart>(StringComparer.OrdinalIgnoreCase);
            var comps = asm != null ? asm.GetComponents(false) as object[] : null;
            _asmPath = model.GetPathName() ?? "";
            var subAssemblies = new List<string>();
            // Parts that are made but not cut from sheet: only their STEP is sent.
            var others = new List<SheetPart>();
            var otherSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (single)
            {
                if (_asmPath == "") { Err("Save the part first: it is sent under its file name."); return; }
                string cfg = "";
                try { var ac = model.ConfigurationManager != null ? model.ConfigurationManager.ActiveConfiguration : null; cfg = ac != null ? ac.Name : ""; } catch { cfg = ""; }
                var one = new SheetPart { Doc = model, Path = _asmPath, Config = cfg, Comp = null };
                one.Count = 1;
                if (SheetThickness(model) > 0)
                {
                    byPath[_asmPath] = one;
                    list.Add(one);
                }
                else
                {
                    // Not cut from sheet (milled, turned, tube...; owner 2026-11-24: "so that everything can be loaded
                    // from CAD, not only sheet metal"): the part goes without a flat pattern - its data, drawing and
                    // STEP. Stopped only where a flat pattern was meant but cannot be made: a plate with no material,
                    // a "sheet part" with no thickness - and a standard (Toolbox) part, which is not a product to make.
                    PartShape shape = ShapeReader.Of(model);
                    if (IsToolbox(model) || shape.Effective == PartShape.Plate) { Err(NotSheetText(model)); return; }
                    if (_s.EXPORT_STEP && shape.Effective != PartShape.Purchased) others.Add(one);
                }
            }
            if (comps != null)
            {
                foreach (object o in comps)
                {
                    var c = o as IComponent2;
                    if (c == null) continue;
                    try
                    {
                        if (c.IsSuppressed()) continue;
                        var doc = c.GetModelDoc2() as IModelDoc2;
                        if (doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                        {
                            string ap = doc.GetPathName() ?? "";
                            if (ap != "" && !subAssemblies.Exists(x => string.Equals(x, ap, StringComparison.OrdinalIgnoreCase))) subAssemblies.Add(ap);
                            continue;
                        }
                        if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) continue;
                        string path = doc.GetPathName() ?? "";
                        if (path == "") continue;
                        if (SheetThickness(doc) <= 0)
                        {
                            // Not cut from sheet (milled, turned, tube...): no flat pattern, but its STEP goes along
                            // (owner 2026-11-24: "every STEP is sent") - except a standard (Toolbox) part and a part
                            // the person said is bought.
                            if (_s.EXPORT_STEP && otherSeen.Add(path) && !IsToolbox(doc) && ShapeReader.Of(doc).Effective != PartShape.Purchased)
                                others.Add(new SheetPart { Doc = doc, Path = path, Config = c.ReferencedConfiguration ?? "", Comp = c });
                            continue;
                        }
                        SheetPart sp;
                        if (!byPath.TryGetValue(path, out sp))
                        {
                            sp = new SheetPart { Doc = doc, Path = path, Config = c.ReferencedConfiguration ?? "", Comp = c };
                            byPath[path] = sp;
                            list.Add(sp);
                        }
                        sp.Count++;
                        string parentPath = _asmPath;
                        try { var par = c.GetParent() as IComponent2; if (par != null) parentPath = par.GetPathName() ?? _asmPath; } catch { parentPath = _asmPath; }
                        if (parentPath != "") sp.Parents.Add(parentPath);
                    }
                    catch { /* a broken component must not stop the whole assembly */ }
                }
            }
            // No sheet metal part at all: the job still goes - the data, the drawings and the STEP files (owner 2026-11-24).
            if (list.Count == 0) Log.Write("no sheet metal parts: the job goes without flat patterns (" + others.Count + " other part(s) with STEP)");
            if (list.Count > MaxSheetParts) { Err("This assembly has " + list.Count + " different sheet metal parts; one LaserCAM job takes up to " + MaxSheetParts + ". Send its sub-assemblies separately."); return; }

            // 2b. The table for the product and its sub-assemblies: product colour and what is coated as a whole.
            _setup = ProductSetup.Load(ProductSetup.DefaultPath);
            _answers.Clear(); _asked.Clear(); _skipRest = false; _lastLoose = null; _setupColor = "";
            _lastBendSeparate = _setup.BendSeparate; _lastCoatingSeparate = _setup.CoatingSeparate;
            // Operations of every part, from the data just read: shown in the table of the part.
            var dumpParts = Json.Obj(dump);
            object partDocs;
            if (dumpParts != null && dumpParts.TryGetValue("parts", out partDocs))
                foreach (object o in Json.Arr(partDocs))
                {
                    var d = Json.Obj(o);
                    SheetPart sp;
                    if (d != null && byPath.TryGetValue(Json.Str(d, "path"), out sp)) sp.Facts = PartFacts.Read(d);
                }
            if (single)
            {
                // The part is the product itself: its facts are in the "document" section, and it gets only its own table
                // (coating, colour, bending) - there is no "whole product" above it to ask about.
                object docObj;
                if (list.Count > 0 && dumpParts != null && dumpParts.TryGetValue("document", out docObj) && Json.Obj(docObj) != null) list[0].Facts = PartFacts.Read(Json.Obj(docObj));
            }
            else if (!AskAssemblies(CleanTitle(model.GetTitle()), subAssemblies, list)) throw new SendCancelled();

            // 3. Flat pattern DXF of every sheet metal part. The part window opens for the export anyway, so the table
            // of that product is shown over it (owner 2026-11-18); a part that did not change and was answered before
            // is neither opened nor asked.
            string tmp = Path.Combine(Path.GetTempPath(), "lasercam_assembly");
            Directory.CreateDirectory(tmp);
            var parts = new List<object>();
            var failed = new List<string>();
            var drawings = new List<object>();
            var drawingsSkipped = new List<string>();
            int dxfReused = 0, pdfReused = 0, drawingCount = 0;
            var steps = new List<KeyValuePair<string, string>>();   // file name -> path on this PC
            var stepsSkipped = new List<string>();
            int stepCount = 0, stepReused = 0;
            long stepBytes = 0;
            try
            {
            for (int i = 0; i < list.Count; i++)
            {
                var sp = list[i];
                string id = Path.GetFileNameWithoutExtension(sp.Path);
                string dxfPath = Path.Combine(tmp, SafeName(id) + ".dxf");
                // Not changed since an earlier send: the flat pattern made then is taken, SolidWorks is not asked again.
                string dxfKey = ModelSig(sp.Path) == "" ? ""
                    : ExportCache.Key("dxf", ExportCache.Version, _swRev, sp.Path.ToLowerInvariant(), sp.Config, ModelSig(sp.Path));
                bool dxfTrusted = dxfKey != "";
                string dxfStored = !_fresh && dxfTrusted ? ExportCache.Find(_cacheDir, dxfKey, ".dxf") : "";
                prog.Step(10 + 45 * i / list.Count, "Flat pattern " + (i + 1) + " of " + list.Count + (dxfStored != "" ? " (not changed, reused)..." : "..."), id);
                string material = "";
                try
                {
                    string db;
                    var pd = sp.Doc as IPartDoc;
                    material = pd != null ? (pd.GetMaterialPropertyName2(sp.Config, out db) ?? "") : "";
                }
                catch { /* no material */ }
                if (material == "") material = "Carbon steel";
                // The table: for a part that is exported now, or has no answer yet (Shift: for every part).
                bool ask = !_skipRest && (_fresh || dxfStored == "" || !AnswerComplete(sp));
                if (!ask)
                {
                    if (_skipRest) AcceptAnswer(sp, SuggestFor(sp));            // "do not ask for the remaining parts"
                    else _answers[sp.Path] = _setup.Items[sp.Path].Copy();      // remembered from an earlier send
                }
                int exported = 1;
                if (ask || dxfStored == "")
                {
                    int partNo = i + 1;
                    string partMaterial = material;
                    exported = ExportFlatDxfOf(sp, dxfPath, ask ? (Func<bool>)(() => AskPart(sp, partNo, list.Count, partMaterial)) : null, dxfStored == "");
                    if (exported < 0) throw new SendCancelled();
                }
                // No flat pattern (the export failed, or the part is bent without the sheet metal feature): the part is
                // listed as failed, but its STEP still goes (owner 2026-11-24: "every STEP is sent").
                bool hasDxf = true;
                if (dxfStored != "") { dxfPath = dxfStored; dxfReused++; }
                else if (exported == 0) { failed.Add(id); hasDxf = false; }
                else
                {
                    if (dxfTrusted) ExportCache.Store(_cacheDir, dxfKey, ".dxf", dxfPath);
                    StoreForStateAfterExport(sp, dxfPath, dxfKey);
                }
                if (hasDxf)
                    parts.Add(new Dictionary<string, object>
                    {
                        { "id", id }, { "name", id }, { "dxf", File.ReadAllText(dxfPath, Encoding.UTF8) }, { "quantity", Math.Max(1, sp.Count) },
                        { "thicknessMm", SheetThickness(sp.Doc) }, { "material", material }, { "gas", _s.DEFAULT_GAS },
                    });
                // 3a. STEP of the part (setting EXPORT_STEP, owner 2026-11-20): it travels to LaserCAM with the job, and
                // LaserCAM attaches it in Odoo when asked to. Not changed since an earlier send: the stored one is taken.
                if (_s.EXPORT_STEP)
                {
                    stepCount++;
                    string stepKey = dxfTrusted ? ExportCache.Key("step", ExportCache.Version, _swRev, sp.Path.ToLowerInvariant(), sp.Config, ModelSig(sp.Path)) : "";
                    string stepPath = !_fresh && stepKey != "" ? ExportCache.Find(_cacheDir, stepKey, ".step") : "";
                    if (stepPath != "") stepReused++;
                    else
                    {
                        prog.Step(10 + 45 * i / list.Count, "STEP " + (i + 1) + " of " + list.Count + "...", id);
                        stepPath = ExportStepOf(sp, Path.Combine(tmp, SafeName(id) + ".step"));
                        if (stepPath != "" && stepKey != "") ExportCache.Store(_cacheDir, stepKey, ".step", stepPath);
                    }
                    if (stepPath == "") { stepsSkipped.Add(id); continue; }
                    long size = new FileInfo(stepPath).Length;
                    // The server takes up to ~20 MB per STEP and ~90 MB of STEP files in one job.
                    if (size > MaxStepBytes || stepBytes + size > MaxStepsTotalBytes) { stepsSkipped.Add(id + " (too large)"); continue; }
                    stepBytes += size;
                    steps.Add(new KeyValuePair<string, string>(id + ".step", stepPath));
                }
            }
            // 3a2. STEP of every other made part (milled, turned, tube...; owner 2026-11-24: "every STEP is sent"):
            // LaserCAM attaches it to the product of that part in Odoo. The sheet parts went first, so the size limits
            // cut these off first.
            for (int i = 0; i < others.Count; i++)
            {
                var op = others[i];
                string id = Path.GetFileNameWithoutExtension(op.Path);
                stepCount++;
                string sig = ModelSig(op.Path);
                string stepKey = sig != "" ? ExportCache.Key("step", ExportCache.Version, _swRev, op.Path.ToLowerInvariant(), op.Config, sig) : "";
                string stepPath = !_fresh && stepKey != "" ? ExportCache.Find(_cacheDir, stepKey, ".step") : "";
                if (stepPath != "") stepReused++;
                else
                {
                    prog.Step(55, "STEP of the other parts, " + (i + 1) + " of " + others.Count + "...", id);
                    stepPath = ExportStepOf(op, Path.Combine(tmp, SafeName(id) + ".step"));
                    if (stepPath != "" && stepKey != "") ExportCache.Store(_cacheDir, stepKey, ".step", stepPath);
                }
                if (stepPath == "") { stepsSkipped.Add(id); continue; }
                long size = new FileInfo(stepPath).Length;
                if (size > MaxStepBytes || stepBytes + size > MaxStepsTotalBytes || steps.Count >= MaxSteps) { stepsSkipped.Add(id + " (too large)"); continue; }
                stepBytes += size;
                steps.Add(new KeyValuePair<string, string>(id + ".step", stepPath));
            }
            // 3b. Drawings as PDF: <name>.SLDDRW next to the assembly, a sub-assembly or a part (same file name).
            var modelPaths = new List<string>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string rootPath = model.GetPathName() ?? "";
            if (rootPath != "" && seenPaths.Add(rootPath)) modelPaths.Add(rootPath);
            if (comps != null)
            {
                foreach (object o in comps)
                {
                    var c = o as IComponent2;
                    if (c == null) continue;
                    try
                    {
                        if (c.IsSuppressed()) continue;
                        string p = c.GetPathName() ?? "";
                        if (p != "" && seenPaths.Add(p)) modelPaths.Add(p);
                    }
                    catch { /* skip a broken component */ }
                }
            }
            var withDrawing = new List<string>();
            var seenDrawings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in modelPaths)
            {
                try
                {
                    string drw = Path.ChangeExtension(p, ".SLDDRW");
                    if (!File.Exists(drw) || !seenDrawings.Add(drw)) continue;   // a part and an assembly of the same name share one drawing
                    if (Path.GetFileNameWithoutExtension(p).Contains("..")) { drawingsSkipped.Add(Path.GetFileNameWithoutExtension(p) + " (file name)"); continue; }
                    withDrawing.Add(p);
                }
                catch { /* skip */ }
            }
            long drawingBytes = 0;
            drawingCount = withDrawing.Count;
            for (int i = 0; i < withDrawing.Count; i++)
            {
                string stem = Path.GetFileNameWithoutExtension(withDrawing[i]);
                string pdfKey = DrawingKey(withDrawing[i]);
                bool pdfTrusted = pdfKey != "" && DrawingTrusted(withDrawing[i]);
                string pdfPath = !_fresh && pdfTrusted ? ExportCache.Find(_cacheDir, pdfKey, ".pdf") : "";
                bool pdfStored = pdfPath != "";
                prog.Step(55 + 20 * i / withDrawing.Count, "Drawing " + (i + 1) + " of " + withDrawing.Count + (pdfStored ? " (not changed, reused)..." : " to PDF..."), stem);
                if (pdfStored) pdfReused++;
                else
                {
                    // Which drawing was made again and why: the summary line only gives the count (2026-11-18: "14 of 15").
                    if (!_fresh) Log.Write("drawing exported again: " + stem + (pdfKey == "" ? " (no signature for it)" : !pdfTrusted ? " (its model is not trusted as unchanged)" : " (not in the cache)"));
                    pdfPath = DrawingPdfOf(withDrawing[i], tmp);
                    WatchModified("by the drawing export of " + stem);
                    if (pdfPath != "" && pdfTrusted)
                    {
                        ExportCache.Store(_cacheDir, pdfKey, ".pdf", pdfPath);
                        string moved;
                        if (_sigAfterExport.TryGetValue(withDrawing[i], out moved))
                            ExportCache.Store(_cacheDir, ExportCache.Key("pdf", ExportCache.Version, _swRev, Path.ChangeExtension(withDrawing[i], ".SLDDRW").ToLowerInvariant(),
                                ExportCache.FileStamp(Path.ChangeExtension(withDrawing[i], ".SLDDRW")), moved), ".pdf", pdfPath);
                    }
                }
                if (pdfPath == "") { drawingsSkipped.Add(stem); continue; }
                byte[] bytes = File.ReadAllBytes(pdfPath);
                // The server takes up to ~10 MB per drawing and ~18 MB of drawings in one job.
                if (bytes.Length > MaxDrawingBytes || drawingBytes + bytes.Length > MaxDrawingsTotalBytes) { drawingsSkipped.Add(stem + " (too large)"); continue; }
                drawingBytes += bytes.Length;
                drawings.Add(new Dictionary<string, object> { { "name", stem + ".pdf" }, { "b64", Convert.ToBase64String(bytes) } });
            }
            // 3c. STEP of the assembly itself and of its sub-assemblies (owner 2026-11-20: "the parts were sent, but 17510
            // itself got no STEP"). Reused while nothing in the assembly changed.
            if (_s.EXPORT_STEP && !single)
            {
                var asmPaths = new List<string>();
                if (rootPath != "") asmPaths.Add(rootPath);
                asmPaths.AddRange(subAssemblies);
                for (int i = 0; i < asmPaths.Count; i++)
                {
                    string ap = asmPaths[i];
                    string stem = Path.GetFileNameWithoutExtension(ap);
                    IModelDoc2 doc;
                    if (!_docByPath.TryGetValue(ap, out doc) || doc == null) { stepsSkipped.Add(stem); continue; }
                    stepCount++;
                    string key = _wholeSig != "" ? ExportCache.Key("step", ExportCache.Version, _swRev, ap.ToLowerInvariant(), "assembly", _wholeSig) : "";
                    string stepFile = !_fresh && key != "" ? ExportCache.Find(_cacheDir, key, ".step") : "";
                    if (stepFile != "") stepReused++;
                    else
                    {
                        prog.Step(75 + 3 * i / asmPaths.Count, "STEP of the assembly " + (i + 1) + " of " + asmPaths.Count + "...", stem);
                        string outPath = Path.Combine(tmp, SafeName(stem) + ".step");
                        stepFile = SaveStep(doc, outPath) ? outPath : "";
                        if (stepFile == "")
                        {
                            // As with a part: some SolidWorks versions export only from the document's own window.
                            try
                            {
                                bool wasVisible = true;
                                try { wasVisible = doc.Visible; } catch { wasVisible = true; }
                                int err = 0;
                                _sw.ActivateDoc2(doc.GetTitle(), true, ref err);
                                if (!wasVisible && !_shownForExport.Contains(doc)) _shownForExport.Add(doc);
                                stepFile = SaveStep(doc, outPath) ? outPath : "";
                            }
                            catch { stepFile = ""; }
                        }
                        WatchModified("by the STEP export of " + stem);
                        if (stepFile != "" && key != "") ExportCache.Store(_cacheDir, key, ".step", stepFile);
                        if (stepFile == "") Log.Write("STEP export failed for the assembly " + stem);
                    }
                    if (stepFile == "") { stepsSkipped.Add(stem); continue; }
                    long size = new FileInfo(stepFile).Length;
                    if (size > MaxStepBytes || stepBytes + size > MaxStepsTotalBytes) { stepsSkipped.Add(stem + " (too large)"); continue; }
                    stepBytes += size;
                    steps.Add(new KeyValuePair<string, string>(stem + ".step", stepFile));
                }
            }
            }
            finally
            {
                // Opening part and drawing windows for the export may have changed the active document: go back to the assembly,
                // also when the export stopped with an error.
                try { int err = 0; _sw.ActivateDoc2(model.GetTitle(), true, ref err); } catch { /* stays where it is */ }
                // Part windows this command had to show for the export: hidden again, the parts stay loaded in the assembly.
                foreach (var shown in _shownForExport) { try { shown.Visible = false; } catch { /* the window stays open */ } }
                _shownForExport.Clear();
                WatchModified("while returning to the assembly window");
                SaveSetup();
                Log.Write("assembly send: flat patterns reused " + dxfReused + " of " + list.Count + ", drawings reused " + pdfReused + " of " + drawingCount
                    + (_s.EXPORT_STEP ? ", STEP reused " + stepReused + " of " + stepCount : "") + (_fresh ? " (Shift held: everything exported again)" : ""));
            }
            if (parts.Count == 0 && list.Count > 0) { Err("The flat pattern export failed for every sheet metal part (" + list.Count + ")."); return; }

            // 4. One LaserCAM job with everything.
            string number = AssemblyNumber(model);
            var body = new Dictionary<string, object>
            {
                { "parts", parts.ToArray() },
                { "sheet", new Dictionary<string, object> { { "width", _s.SHEET_W }, { "height", _s.SHEET_H }, { "margin", _s.SHEET_MARGIN } } },
                { "spacing", _s.SPACING }, { "pricePerKg", _s.PRICE_PER_KG }, { "fast", true },
                { "number", number }, { "source", "SolidWorks" }, { "assembly", dump },
            };
            if (drawings.Count > 0) body["drawings"] = drawings.ToArray();
            // The answers from the setup tables travel inside the assembly data: LaserCAM fills its Odoo plan from them.
            var dumpObj = Json.Obj(dump);
            if (dumpObj != null) dumpObj["setup"] = ProductSetup.ToJob(_setupColor, _answers, _asked);
            if (_useOdoo && _s.SEND_ODOO_FROM_APP)
            {
                var od = new Dictionary<string, object> { { "url", _s.ODOO_URL }, { "apiKey", _s.ODOO_API_KEY } };
                if (_s.ODOO_DB != "") od["db"] = _s.ODOO_DB;
                body["odoo"] = od;
            }
            prog.Step(78, "Creating the LaserCAM job (uploading " + parts.Count + " flat patterns, " + drawings.Count + " drawings)...");
            var resp = _lc.Post("/job", body);
            string jobId = Json.Str(resp, "id"), jobUrl = Json.Str(resp, "url");
            if (jobId == "" || jobUrl == "") { Err("LaserCAM did not return a job id."); return; }
            // 4b. STEP files are added to the job one by one (they would not fit into the one request next to the drawings),
            // before the browser opens, so LaserCAM sees them from the start. A failed upload does not stop the sending.
            int stepsSent = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                prog.Step(79 + 10 * i / steps.Count, "Uploading STEP " + (i + 1) + " of " + steps.Count + "...", steps[i].Key);
                try
                {
                    _lc.Post("/job/" + jobId + "/step", new Dictionary<string, object> { { "name", steps[i].Key }, { "b64", Convert.ToBase64String(File.ReadAllBytes(steps[i].Value)) } });
                    stepsSent++;
                }
                catch (Exception ex)
                {
                    Log.Write("STEP upload failed for " + steps[i].Key + ": " + ex.Message);
                    stepsSkipped.Add(Path.GetFileNameWithoutExtension(steps[i].Key) + " (upload failed)");
                }
            }
            prog.Step(90, "Opening LaserCAM in the browser...");
            Process.Start(AppUrlOf(jobUrl, jobId));
            prog.Step(100, "Done.");
            string note = failed.Count > 0
                ? "\n\nNo flat pattern could be exported for " + failed.Count + " part(s): " + string.Join(", ", failed.ToArray()) + ".\nLaserCAM marks them; their weight stays approximate."
                : "";
            if (drawingsSkipped.Count > 0)
                note += "\n\nNo PDF could be made for " + drawingsSkipped.Count + " drawing(s): " + string.Join(", ", drawingsSkipped.ToArray()) + ".";
            if (stepsSkipped.Count > 0)
                note += "\n\nNo STEP could be sent for " + stepsSkipped.Count + " part(s): " + string.Join(", ", stepsSkipped.ToArray()) + ".";
            Info("LaserCAM opened in the browser with the " + (single ? "part " : "assembly ") + number + ": "
                + (list.Count == 0 ? "no sheet metal parts (nothing to nest), " : parts.Count + " flat pattern(s) of " + list.Count + " sheet metal part(s), ")
                + drawings.Count + " drawing(s) as PDF" + (_s.EXPORT_STEP ? ", " + stepsSent + " STEP file(s)" : "") + "." + note);
        }

        const long MaxStepBytes = 20L * 1024 * 1024;
        const long MaxStepsTotalBytes = 88L * 1024 * 1024;
        /// <summary>The server takes up to 300 STEP files in one job; a few places are kept for the assemblies themselves.</summary>
        const int MaxSteps = 280;

        /// <summary>
        /// STEP of one sheet metal part in the configuration the assembly uses (the bent part, not the flat pattern).
        /// Returns the file path, or "" when SolidWorks could not export it. The part itself is never saved.
        /// </summary>
        string ExportStepOf(SheetPart sp, string path)
        {
            string prev = "";
            bool switched = false;
            try
            {
                var active = sp.Doc.ConfigurationManager != null ? sp.Doc.ConfigurationManager.ActiveConfiguration : null;
                prev = active != null ? active.Name : "";
                if (sp.Config != "" && prev != "" && !string.Equals(prev, sp.Config, StringComparison.OrdinalIgnoreCase))
                    switched = sp.Doc.ShowConfiguration2(sp.Config);
            }
            catch { /* export what is active */ }
            string part = Path.GetFileNameWithoutExtension(sp.Path);
            bool ok = SaveStep(sp.Doc, path);
            if (!ok)
            {
                // As with the flat pattern: some SolidWorks versions export only from the part's own (active) window.
                ok = ShowPartWindow(sp) && SaveStep(sp.Doc, path);
            }
            WatchModified("by the STEP export of " + part);
            if (switched) { try { sp.Doc.ShowConfiguration2(prev); } catch { /* leave as is */ } WatchModified("by switching " + part + " back to its configuration"); }
            if (!ok) Log.Write("STEP export failed for " + part);
            return ok ? path : "";
        }

        static bool SaveStep(IModelDoc2 doc, string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                int e = 0, w = 0;
                bool ok = doc.Extension.SaveAs(path, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e, ref w);
                return ok && File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>The server takes up to 300 parts in one assembly job.</summary>
        const int MaxSheetParts = 300;
        const int MaxDrawingBytes = 10 * 1024 * 1024;
        const long MaxDrawingsTotalBytes = 17L * 1024 * 1024;

        /// <summary>The address opened in the browser: the one the server returns, or the LC_APP_URL setting (e.g. the test version) with the job id.</summary>
        string AppUrlOf(string jobUrl, string jobId)
        {
            string custom = (_s.LC_APP_URL ?? "").Trim();
            if (custom == "") return jobUrl;
            return custom + (custom.Contains("?") ? "&" : "?") + "lcjob=" + Uri.EscapeDataString(jobId);
        }

        /// <summary>PDF (all sheets) of the drawing that lies next to a model file under the same name (&lt;name&gt;.SLDDRW).
        /// Returns the PDF path, or "" when there is no drawing or the export fails. A drawing the user has open stays open.</summary>
        string DrawingPdfOf(string modelPath, string outDir)
        {
            try
            {
                if (string.IsNullOrEmpty(modelPath)) return "";
                string drwPath = Path.ChangeExtension(modelPath, ".SLDDRW");
                if (!File.Exists(drwPath)) return "";
                string pdfPath = Path.Combine(outDir, SafeName(Path.GetFileNameWithoutExtension(modelPath)) + ".pdf");
                if (File.Exists(pdfPath)) File.Delete(pdfPath);
                bool wasOpen = false;
                try { wasOpen = _sw.GetOpenDocumentByName(drwPath) != null; } catch { wasOpen = false; }
                // First without showing the drawing window (no flashing, faster); if SolidWorks cannot make the PDF
                // that way, once more with the window. A drawing the user has open is used as it is.
                string made = wasOpen ? "" : SaveDrawingPdf(drwPath, pdfPath, false, false);
                if (made == "") made = SaveDrawingPdf(drwPath, pdfPath, true, wasOpen);
                return made;
            }
            catch { return ""; }
        }

        /// <summary>Opens the drawing (hidden when <paramref name="visible"/> is false), saves all sheets as PDF and closes it again
        /// unless <paramref name="keepOpen"/>. Returns the PDF path or "".</summary>
        string SaveDrawingPdf(string drwPath, string pdfPath, bool visible, bool keepOpen)
        {
            bool hidden = false;
            try
            {
                if (File.Exists(pdfPath)) File.Delete(pdfPath);
                if (!visible) { _sw.DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING); hidden = true; }
                int e = 0, w = 0;
                var drw = _sw.OpenDoc6(drwPath, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
                if (drw == null) return "";
                try
                {
                    var pdfData = _sw.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                    if (pdfData != null)
                    {
                        pdfData.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
                        try { pdfData.ViewPdfAfterSaving = false; } catch { /* older SolidWorks: the user's own option applies */ }
                    }
                    int e2 = 0, w2 = 0;
                    bool ok = drw.Extension.SaveAs(pdfPath, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, pdfData, ref e2, ref w2);
                    return ok && File.Exists(pdfPath) ? pdfPath : "";
                }
                finally
                {
                    if (!keepOpen) { try { _sw.CloseDoc(drw.GetTitle()); } catch { /* stays open */ } }
                }
            }
            catch { return ""; }
            finally
            {
                // New drawings must open in a window again, whatever happened above.
                if (hidden) { try { _sw.DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING); } catch { /* nothing to restore */ } }
            }
        }

        /// <summary>The model (part or assembly) a drawing shows: the document of its first model view.</summary>
        IModelDoc2 ModelOfDrawing(IModelDoc2 drawing)
        {
            try
            {
                var dd = drawing as IDrawingDoc;
                if (dd == null) return null;
                var view = dd.GetFirstView() as IView;   // the first "view" is the sheet itself
                while (view != null)
                {
                    var refDoc = view.ReferencedDocument as IModelDoc2;
                    if (refDoc != null) return refDoc;
                    view = view.GetNextView() as IView;
                }
            }
            catch { /* no model */ }
            return null;
        }

        // ---------------- documents of the assembly, and which step marks them as modified ----------------

        /// <summary>The assembly and every loaded component document, by file path. Read before anything else is done.</summary>
        void CollectDocs(IModelDoc2 model, IAssemblyDoc asm)
        {
            _compByPath.Clear(); _docByPath.Clear(); _dirtySeen.Clear();
            try
            {
                string rootPath = model.GetPathName() ?? "";
                if (rootPath != "") _docByPath[rootPath] = model;
                var comps = asm != null ? asm.GetComponents(false) as object[] : null;   // a single part has no components
                if (comps == null) return;
                foreach (object o in comps)
                {
                    var c = o as IComponent2;
                    if (c == null) continue;
                    try
                    {
                        if (c.IsSuppressed()) continue;
                        string p = c.GetPathName() ?? "";
                        if (p == "" || _compByPath.ContainsKey(p)) continue;
                        _compByPath[p] = c;
                        var d = c.GetModelDoc2() as IModelDoc2;
                        if (d != null) _docByPath[p] = d;
                    }
                    catch { /* a broken component is simply never reused */ }
                }
            }
            catch { /* nothing collected: nothing is reused and nothing is watched */ }
        }

        /// <summary>Documents already reported as modified in this send.</summary>
        readonly HashSet<string> _dirtySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Writes to the log which documents SolidWorks marked as modified during the step that has just ended
        /// (each document once, at the first such step). The command never saves a model; a modified mark only makes
        /// SolidWorks offer to save on close. This shows which step causes it. Reads only.</summary>
        void WatchModified(string step)
        {
            try
            {
                var hit = new List<string>();
                foreach (var kv in _docByPath)
                {
                    if (_dirtySeen.Contains(kv.Key)) continue;
                    bool dirty;
                    try { dirty = kv.Value.GetSaveFlag(); } catch { continue; }
                    if (!dirty) continue;
                    _dirtySeen.Add(kv.Key);
                    hit.Add(Path.GetFileName(kv.Key));
                }
                if (hit.Count == 0) return;
                string names = string.Join(", ", hit.GetRange(0, Math.Min(hit.Count, 12)).ToArray()) + (hit.Count > 12 ? ", ..." : "");
                Log.Write("marked as modified " + step + ": " + hit.Count + " document(s): " + names);
            }
            catch { /* diagnostics only */ }
        }

        // ---------------- reuse of unchanged exports ----------------

        /// <summary>Reads what the reuse needs from the data of this send, before anything is exported. Whether a model
        /// "did not change" is decided from what SolidWorks holds in memory now - its features, sheet metal parameters
        /// (StableSig) and the shape of its bodies - not from file dates or the "modified" mark: SolidWorks sets that
        /// mark by the export itself, and saving a file changes its date without changing the part (owner 2026-11-18).</summary>
        void PrepareReuse(IModelDoc2 model, IAssemblyDoc asm, object dump)
        {
            _sectionSig.Clear(); _modelSig.Clear(); _partData.Clear(); _sigAfterExport.Clear();
            _wholeSig = ""; _swRev = "";
            try
            {
                _cacheDir = ExportCache.DefaultDir;
                ExportCache.Sweep(_cacheDir, TimeSpan.FromDays(45), DateTime.UtcNow);
                try { _swRev = _sw.RevisionNumber() ?? ""; } catch { _swRev = ""; }

                var root = Json.Obj(dump);
                if (root == null) return;
                object v;
                if (root.TryGetValue("parts", out v))
                    foreach (object o in Json.Arr(v))
                    {
                        var d = Json.Obj(o);
                        string p = Json.Str(d, "path");
                        if (p == "" || _sectionSig.ContainsKey(p)) continue;
                        _sectionSig[p] = StableSig.Of(d);
                        _partData[p] = d;
                    }

                // The assembly as a whole (for the drawings of assemblies): what is in it and where, every part, and
                // the features of the assembly itself. A model whose signature is unknown counts by its file date.
                var lines = new List<string>();
                var comps = asm != null ? asm.GetComponents(false) as object[] : null;   // a single part has no components
                if (comps != null)
                    foreach (object o in comps)
                    {
                        var c = o as IComponent2;
                        if (c == null) continue;
                        try { lines.Add((c.GetPathName() ?? "").ToLowerInvariant() + "|" + (c.ReferencedConfiguration ?? "") + "|" + (c.IsSuppressed() ? "0" : "1") + "|" + PlaceSig(c)); }
                        catch { /* a broken component is left out */ }
                    }
                lines.Sort(StringComparer.Ordinal);
                var models = new List<string>();
                foreach (string p in _compByPath.Keys)
                {
                    string sig = ModelSig(p);
                    models.Add(p.ToLowerInvariant() + "=" + (sig != "" ? sig : "file:" + ExportCache.FileStamp(p)));
                }
                models.Sort(StringComparer.Ordinal);
                object rootDoc;
                string own = root.TryGetValue("document", out rootDoc) ? StableSig.Of(Json.Obj(rootDoc)) : "";
                _wholeSig = ExportCache.Hash(string.Join("\n", lines.ToArray()) + "\n#\n" + string.Join("\n", models.ToArray()) + "\n#\n" + own);
            }
            catch (Exception ex)
            {
                // Without this data nothing is reused: every file is exported as before.
                _sectionSig.Clear(); _modelSig.Clear(); _wholeSig = "";
                Log.Write("reuse of unchanged exports is off for this send: " + ex.Message);
            }
        }

        /// <summary>Where a component sits in the assembly (its transform), as text that calculation noise does not change.</summary>
        static string PlaceSig(IComponent2 c)
        {
            try
            {
                var t = c.Transform2 as IMathTransform;
                var a = t != null ? t.ArrayData as double[] : null;
                return a == null ? "" : ExportCache.NumbersSig(a, 1e-6);
            }
            catch { return ""; }
        }

        /// <summary>Signature of a model as it is in memory now: its features and sheet metal parameters and, for a
        /// part, the shape of its bodies in the configuration the assembly uses. "" = unknown, such a model is never reused.</summary>
        string ModelSig(string path)
        {
            string s;
            if (_modelSig.TryGetValue(path, out s)) return s;
            string section;
            _sectionSig.TryGetValue(path, out section);
            s = section ?? "";
            if (s != "" && path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
            {
                IComponent2 c;
                string bodies = _compByPath.TryGetValue(path, out c) ? BodiesSig(c) : "";
                s = bodies == "" ? "" : s + "|" + bodies;
            }
            _modelSig[path] = s;
            return s;
        }

        /// <summary>Volume, area, centre of mass and moments of inertia of every solid body of a component, as text that
        /// calculation noise does not change. A moved hole changes the centre of mass, a longer flange the volume.</summary>
        static string BodiesSig(IComponent2 c)
        {
            try
            {
                object info;
                var bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
                if (bodies == null || bodies.Length == 0) return "";
                var each = new List<string>();
                foreach (object o in bodies)
                {
                    var b = o as IBody2;
                    if (b == null) return "";
                    var m = b.GetMassProperties(1.0) as double[];
                    if (m == null || m.Length < 12) return "";
                    each.Add(ExportCache.NumbersSig(new[] { m[0], m[1], m[2] }, 1e-6) + "/" + ExportCache.NumbersSig(new[] { m[3] }, 1e-15) + "/"
                        + ExportCache.NumbersSig(new[] { m[4] }, 1e-12) + "/" + ExportCache.NumbersSig(new[] { m[6], m[7], m[8], m[9], m[10], m[11] }, 1e-16));
                }
                each.Sort(StringComparer.Ordinal);
                return string.Join("+", each.ToArray());
            }
            catch { return ""; }
        }

        /// <summary>Key of the PDF of the drawing that lies next to a model. It depends on the drawing file itself and on
        /// what it shows: a part drawing on its part, an assembly drawing on the whole assembly being sent. "" = cannot be reused.</summary>
        string DrawingKey(string modelPath)
        {
            string drw = Path.ChangeExtension(modelPath, ".SLDDRW");
            string stamp = ExportCache.FileStamp(drw);
            if (stamp == "") return "";
            if (modelPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                return ModelSig(modelPath) == "" ? "" : ExportCache.Key("pdf", ExportCache.Version, _swRev, drw.ToLowerInvariant(), stamp, ModelSig(modelPath));
            return _wholeSig == "" ? "" : ExportCache.Key("pdf", ExportCache.Version, _swRev, drw.ToLowerInvariant(), stamp, "assembly", _wholeSig);
        }

        /// <summary>A drawing the user has open with unsaved changes is always exported again.</summary>
        bool DrawingTrusted(string modelPath)
        {
            try
            {
                var open = _sw.GetOpenDocumentByName(Path.ChangeExtension(modelPath, ".SLDDRW")) as IModelDoc2;
                return open == null || !open.GetSaveFlag();
            }
            catch { return false; }
        }

        /// <summary>After a flat pattern export SolidWorks may mark the part as modified. This writes to the log what the
        /// export changed in the part (configurations, features, the signature) and, when the signature moved with it,
        /// stores the same flat pattern under the new signature too - so the next send finds it in either state.</summary>
        void StoreForStateAfterExport(SheetPart sp, string dxfPath, string keyBefore)
        {
            try
            {
                if (keyBefore == "" || !sp.Doc.GetSaveFlag()) return;
                Dictionary<string, object> before;
                if (!_partData.TryGetValue(sp.Path, out before)) return;
                var read = Json.Obj(Json.Read(new Inspector(_sw).Run(sp.Doc)));
                object d;
                if (read == null || !read.TryGetValue("document", out d)) return;
                var after = Json.Obj(d);
                string sk0, pr0, sk1, pr1;
                if (!StableSig.Parts(before, out sk0, out pr0) || !StableSig.Parts(after, out sk1, out pr1)) return;
                string bodies = BodiesSig(sp.Comp);
                string sigBefore = ModelSig(sp.Path);
                string sigAfter = bodies == "" ? "" : ExportCache.Hash(sk1 + "\n" + pr1) + "|" + bodies;
                string part = Path.GetFileNameWithoutExtension(sp.Path);
                Log.Write("what the flat pattern export changed in " + part + ": configurations " + CountOf(before, "configurations") + " -> " + CountOf(after, "configurations")
                    + ", features " + Json.Str(before, "featureCount") + " -> " + Json.Str(after, "featureCount")
                    + "; signature: " + (sigAfter == sigBefore ? "the same" : "changed (" + (sk0 != sk1 ? "feature tree " : "") + (pr0 != pr1 ? "sheet metal parameters " : "")
                        + (sigAfter == "" || !sigBefore.EndsWith("|" + bodies, StringComparison.Ordinal) ? "body shape" : "") + ")"));
                if (sigAfter == "" || sigAfter == sigBefore) return;
                ExportCache.Store(_cacheDir, ExportCache.Key("dxf", ExportCache.Version, _swRev, sp.Path.ToLowerInvariant(), sp.Config, sigAfter), ".dxf", dxfPath);
                _sigAfterExport[sp.Path] = sigAfter;   // the drawing of this part is stored for this state too
            }
            catch (Exception ex) { Log.Write("reading " + Path.GetFileName(sp.Path) + " after its export failed: " + ex.Message); }
        }

        static int CountOf(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? Json.Arr(v).Length : 0;
        }

        /// <summary>
        /// One sheet metal part: its window is opened and the setup table shown when <paramref name="ask"/> is given, then
        /// the flat pattern of the configuration the assembly uses is exported when <paramref name="export"/> is set.
        /// Returns 1 = done, 0 = the export failed, -1 = the user cancelled the sending in the table.
        /// </summary>
        int ExportFlatDxfOf(SheetPart sp, string path, Func<bool> ask, bool export)
        {
            string prev = "";
            bool switched = false;
            try
            {
                var active = sp.Doc.ConfigurationManager != null ? sp.Doc.ConfigurationManager.ActiveConfiguration : null;
                prev = active != null ? active.Name : "";
                if (sp.Config != "" && prev != "" && !string.Equals(prev, sp.Config, StringComparison.OrdinalIgnoreCase))
                    switched = sp.Doc.ShowConfiguration2(sp.Config);
            }
            catch { /* export what is active */ }
            string part = Path.GetFileNameWithoutExtension(sp.Path);
            if (switched) WatchModified("by switching " + part + " to the configuration the assembly uses");
            bool shown = false, ok = true, cancelled = false;
            try
            {
                if (ask != null)
                {
                    shown = ShowPartWindow(sp);
                    WatchModified("by opening the window of " + part);
                    bool go;
                    try { go = ask(); }
                    catch (Exception ex) { Log.Write("setup table of " + part + " failed, the part is sent without an answer: " + ex.Message); go = true; }
                    cancelled = !go;
                }
                if (export && !cancelled)
                {
                    ok = ExportFlatDxf(sp.Doc, path);
                    WatchModified("by the flat pattern export of " + part);
                    if (!ok && !shown)
                    {
                        // Some SolidWorks versions export a flat pattern only from the part's own (active) window.
                        shown = ShowPartWindow(sp);
                        WatchModified("by opening the window of " + part);
                        ok = shown && ExportFlatDxf(sp.Doc, path);
                        WatchModified("by the flat pattern export of " + part + " from its own window");
                    }
                }
            }
            catch { ok = false; }
            if (switched) { try { sp.Doc.ShowConfiguration2(prev); } catch { /* leave as is */ } WatchModified("by switching " + part + " back to its configuration"); }
            return cancelled ? -1 : ok ? 1 : 0;
        }

        /// <summary>Opens the part in its own window (hidden again once the assembly is active, if it was hidden before).</summary>
        bool ShowPartWindow(SheetPart sp)
        {
            try
            {
                bool wasVisible = true;
                try { wasVisible = sp.Doc.Visible; } catch { wasVisible = true; }
                int err = 0;
                _sw.ActivateDoc2(sp.Doc.GetTitle(), true, ref err);
                if (!wasVisible && !_shownForExport.Contains(sp.Doc)) _shownForExport.Add(sp.Doc);
                return true;
            }
            catch { return false; }
        }

        // ---------------- the type of a part that could not be recognised ----------------

        /// <summary>
        /// Parts without a sheet metal feature whose shape says nothing certain (ShapeClassifier: "unknown"): one list,
        /// a type to choose for each, with a suggestion - the remembered choice, else "sheet part" for a part with the
        /// "Thickness" property, "bought" for a part without a material, else what the shape looks like most. A part
        /// answered in an earlier send is not asked about again (Shift held: it is). False = the user cancelled.
        /// </summary>
        bool AskPartTypes(IModelDoc2 model, IAssemblyDoc asm)
        {
            var docs = new List<IModelDoc2>();
            if (asm == null) docs.Add(model);
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var comps = asm.GetComponents(false) as object[];
                if (comps != null)
                    foreach (object o in comps)
                    {
                        var c = o as IComponent2;
                        if (c == null) continue;
                        try
                        {
                            if (c.IsSuppressed()) continue;
                            var doc = c.GetModelDoc2() as IModelDoc2;
                            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART) continue;
                            string p = doc.GetPathName() ?? "";
                            if (p != "" && seen.Add(p)) docs.Add(doc);
                        }
                        catch { /* a broken component must not stop the whole assembly */ }
                    }
            }
            var rows = new List<PartTypeForm.Row>();
            var shapes = new Dictionary<string, PartShape>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in docs)
            {
                try
                {
                    string path = doc.GetPathName() ?? "";
                    if (path == "" || SheetMetalThickness(doc) > 0 || IsToolbox(doc)) continue;
                    PartShape shape = ShapeReader.Of(doc);
                    Log.Write("shape of " + Path.GetFileNameWithoutExtension(path) + ": " + shape.Kind + (shape.Chosen != "" ? " (chosen earlier: " + shape.Chosen + ")" : "") + " - " + shape.Why);
                    if (shape.Kind != PartShape.Unknown || (shape.Chosen != "" && !_fresh)) continue;
                    string start = shape.Chosen != "" ? shape.Chosen
                        : PlateThickness(doc) > 0 ? PartShape.Plate
                        : !HasMaterial(doc) ? PartShape.Purchased
                        : shape.Guess != "" ? shape.Guess : PartShape.Milled;
                    rows.Add(new PartTypeForm.Row { Key = path, Name = Path.GetFileNameWithoutExtension(path), Why = shape.Why, Kind = start });
                    shapes[path] = shape;
                }
                catch { /* a part that cannot be read is left as it is */ }
            }
            if (rows.Count == 0) return true;
            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            using (var f = new PartTypeForm(AddIn.Title + " - part types", rows))
            {
                IntPtr main = MainWindow();
                DialogResult res = main != IntPtr.Zero ? f.ShowDialog(new HwndOwner(main)) : f.ShowDialog();
                if (res != DialogResult.OK) return false;
            }
            foreach (var row in rows)
            {
                PartTypes.Set(row.Key, row.Kind);
                shapes[row.Key].Chosen = row.Kind;
                Log.Write("type chosen for " + row.Name + ": " + row.Kind);
            }
            PartTypes.Save();
            return true;
        }

        // ---------------- product setup tables ----------------

        DialogResult ShowSetup(ProductSetupForm f)
        {
            f.ParkUnder(_prog != null && _prog.Visible ? _prog.Bounds : Rectangle.Empty);
            IntPtr main = MainWindow();
            return main != IntPtr.Zero ? f.ShowDialog(new HwndOwner(main)) : f.ShowDialog();
        }

        /// <summary>The first table: the product, its colour and the sub-assemblies. An assembly that holds sheet metal
        /// parts directly is suggested as coated as a whole (owner 2026-11-18); the product itself only when it has no
        /// such sub-assembly. Answers of an earlier send come back as they were. False = the user cancelled.</summary>
        bool AskAssemblies(string asmName, List<string> subAssemblies, List<SheetPart> list)
        {
            var withSheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in list) foreach (string pp in sp.Parents) withSheets.Add(pp);
            subAssemblies.Sort(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ProductSetupForm.Row>();
            bool subCoated = false;
            foreach (string ap in subAssemblies)
            {
                SetupAnswer a;
                if (_setup.Items.TryGetValue(ap, out a)) a = a.Copy();
                else a = new SetupAnswer { Coating = withSheets.Contains(ap) ? ProductSetup.DefaultCoating : "" };
                if (a.Coating != "") subCoated = true;
                rows.Add(new ProductSetupForm.Row { Key = ap, Name = Path.GetFileNameWithoutExtension(ap), Answer = a });
            }
            if (_asmPath != "")
            {
                SetupAnswer r;
                if (_setup.Items.TryGetValue(_asmPath, out r)) r = r.Copy();
                else r = new SetupAnswer { Coating = !subCoated && withSheets.Contains(_asmPath) ? ProductSetup.DefaultCoating : "" };
                rows.Insert(0, new ProductSetupForm.Row { Key = _asmPath, Name = "The whole product", Answer = r });
            }
            string color;
            if (_asmPath == "" || !_setup.Colors.TryGetValue(_asmPath, out color)) color = "";
            using (var f = new ProductSetupForm(AddIn.Title + " - product setup", "Product " + asmName,
                "Choose a coating for an assembly that is coated as a whole (for example a welded frame).\nSheet metal parts get their own table when their window opens.",
                color, true, rows, null))
            {
                if (ShowSetup(f) != DialogResult.OK) return false;
                _setupColor = f.ProductColor;
            }
            foreach (var row in rows) { _answers[row.Key] = row.Answer; _asked.Add(row.Key); }
            return true;
        }

        /// <summary>True when every assembly that holds the part is coated as a whole: the part is coated together with it.</summary>
        bool InsideCoated(SheetPart sp)
        {
            if (sp.Parents.Count == 0) return false;
            foreach (string pp in sp.Parents)
            {
                SetupAnswer a;
                if (!_answers.TryGetValue(pp, out a) || a.Coating == "") return false;
            }
            return true;
        }

        /// <summary>What the table of a part starts with: the earlier answer; not coated inside a coated assembly;
        /// otherwise like the previous such part (first time: powder coating).</summary>
        SetupAnswer SuggestFor(SheetPart sp)
        {
            SetupAnswer a;
            if (_setup.Items.TryGetValue(sp.Path, out a)) a = a.Copy();
            else if (InsideCoated(sp)) a = new SetupAnswer();
            else a = new SetupAnswer { Coating = _lastLoose ?? ProductSetup.DefaultCoating };
            // Separate products: the earlier answer, otherwise like the previous part (first part: like the last send).
            if (sp.Facts.Bends > 0 && !a.BendSeparate.HasValue) a.BendSeparate = _lastBendSeparate;
            if (a.Coating != "" && !a.CoatingSeparate.HasValue) a.CoatingSeparate = _lastCoatingSeparate;
            return a;
        }

        /// <summary>True when the part has a remembered answer that says everything its table asks.</summary>
        bool AnswerComplete(SheetPart sp)
        {
            SetupAnswer a;
            if (!_setup.Items.TryGetValue(sp.Path, out a)) return false;
            if (sp.Facts.Bends > 0 && !a.BendSeparate.HasValue) return false;
            return a.Coating == "" || a.CoatingSeparate.HasValue;
        }

        void AcceptAnswer(SheetPart sp, SetupAnswer a)
        {
            // The first colour typed for a part becomes the product colour when none was given yet.
            if (_setupColor == "" && a.Color != "") { _setupColor = a.Color; a.Color = ""; }
            _answers[sp.Path] = a;
            _asked.Add(sp.Path);
            if (a.BendSeparate.HasValue) _lastBendSeparate = a.BendSeparate.Value;
            if (a.CoatingSeparate.HasValue) _lastCoatingSeparate = a.CoatingSeparate.Value;
            if (!InsideCoated(sp)) _lastLoose = a.Coating;
        }

        /// <summary>The table of one sheet metal part, shown over its opened window. False = the user cancelled the sending.</summary>
        bool AskPart(SheetPart sp, int index, int total, string material)
        {
            string name = Path.GetFileNameWithoutExtension(sp.Path);
            string note = "Sheet metal " + Json.NumStr(SheetThickness(sp.Doc)) + " mm, " + material + "; used " + Math.Max(1, sp.Count) + " time(s) in this assembly.";
            if (InsideCoated(sp)) note += "\nIt is inside an assembly that is coated as a whole, so it needs no coating of its own.";
            SetupAnswer a = SuggestFor(sp);
            var rows = new List<ProductSetupForm.Row> { new ProductSetupForm.Row { Key = sp.Path, Name = name, Answer = a } };
            using (var f = new ProductSetupForm(AddIn.Title + " - part " + index + " of " + total, name, note, _setupColor, false, rows,
                "Do not ask for the remaining parts (use what is suggested)", sp.Facts))
            {
                if (ShowSetup(f) != DialogResult.OK) return false;
                _skipRest = f.SkipRest;
            }
            AcceptAnswer(sp, a);
            return true;
        }

        /// <summary>Remembers the answers of this send on this computer, also when the sending stopped half way.</summary>
        void SaveSetup()
        {
            try
            {
                if (_asked.Count == 0) return;
                foreach (string path in _asked)
                {
                    SetupAnswer a;
                    if (_answers.TryGetValue(path, out a)) _setup.Items[path] = a.Copy();
                }
                if (_asmPath != "")
                {
                    if (_setupColor != "") _setup.Colors[_asmPath] = _setupColor; else _setup.Colors.Remove(_asmPath);
                }
                _setup.BendSeparate = _lastBendSeparate; _setup.CoatingSeparate = _lastCoatingSeparate;
                ProductSetup.Save(ProductSetup.DefaultPath, _setup);
            }
            catch { /* not remembered: asked again next time */ }
        }
    }
}
