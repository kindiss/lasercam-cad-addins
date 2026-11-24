using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    /// <summary>What the user said about one product (a part or an assembly) while sending an assembly.</summary>
    public sealed class SetupAnswer
    {
        /// <summary>Coating id as LaserCAM knows it; "" = not coated.</summary>
        public string Coating = "";
        /// <summary>Own colour; "" = the colour of the whole product.</summary>
        public string Color = "";

        /// <summary>Bending / coating as a separate Odoo product (true) or an operation inside the product before it (false);
        /// null = not said (an assembly, a part without bends, a part that is not coated): LaserCAM follows its rules.</summary>
        public bool? BendSeparate;
        public bool? CoatingSeparate;

        public SetupAnswer Copy() { return new SetupAnswer { Coating = Coating, Color = Color, BendSeparate = BendSeparate, CoatingSeparate = CoatingSeparate }; }
    }

    /// <summary>
    /// Operations of one sheet metal part, counted from the data the add-in reads for LaserCAM (the same rules as
    /// LaserCAM: assembly/fromSolidworks.ts): bends, threads by size, countersinks. Shown in the part table as information.
    /// </summary>
    public sealed class PartFacts
    {
        public int Bends;
        public int Countersinks;
        public readonly List<KeyValuePair<string, int>> Threads = new List<KeyValuePair<string, int>>();
        public int ThreadCount { get { int n = 0; foreach (var t in Threads) n += t.Value; return n; } }

        static double Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || v is string || v is bool) return double.NaN;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return double.NaN; }
        }
        static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is string ? ((string)v).Trim() : "";
        }
        static bool Suppressed(Dictionary<string, object> f) { object v; return f.TryGetValue("suppressed", out v) && v is bool && (bool)v; }
        static Dictionary<string, object> Sub(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? Json.Obj(v) : null; }
        static string Dia(double metres) { return "\u00D8" + Math.Round(metres * 1000, 2).ToString("0.##", CultureInfo.InvariantCulture); }

        void AddThread(string size, int count)
        {
            if (size == "") return;
            for (int i = 0; i < Threads.Count; i++)
                if (Threads[i].Key == size) { Threads[i] = new KeyValuePair<string, int>(size, Threads[i].Value + count); return; }
            Threads.Add(new KeyValuePair<string, int>(size, count));
        }

        void Walk(object features, Dictionary<string, object> parent)
        {
            foreach (object o in Json.Arr(features))
            {
                var f = Json.Obj(o);
                if (f == null) continue;
                bool off = Suppressed(f);
                if (!off)
                {
                    string type = Text(f, "type");
                    if (type == "OneBend") Bends++;
                    var h = Sub(f, "holeWizard");
                    if (h != null)
                    {
                        double points = Num(h, "GetSketchPointCount()");
                        int count = points > 0 ? (int)Math.Round(points) : 1;
                        string size = Text(h, "FastenerSize");
                        bool tapped = Num(h, "ThreadDiameter") > 0;
                        object subs;
                        if (!tapped && f.TryGetValue("subFeatures", out subs))
                            foreach (object so in Json.Arr(subs))
                            {
                                var sf = Json.Obj(so);
                                if (sf != null && Text(sf, "type") == "CosmeticThread" && !Suppressed(sf)) { tapped = true; break; }
                            }
                        if (tapped) AddThread(size != "" ? size : Dia(Num(h, "ThreadDiameter")), count);
                        else if (Num(h, "CounterSinkDiameter") > 0) Countersinks += count;
                    }
                    else if (type == "CosmeticThread" && Sub(f, "cosmeticThread") != null && !(parent != null && Sub(parent, "holeWizard") != null))
                    {
                        var t = Sub(f, "cosmeticThread");
                        string size = Text(t, "Size");
                        if (size == "") size = Text(t, "ThreadCallout");
                        if (size == "") size = Dia(Num(t, "Diameter"));
                        AddThread(size, 1);
                    }
                }
                object children;
                if (!off && f.TryGetValue("subFeatures", out children)) Walk(children, f);   // children of a suppressed feature do not count
            }
        }

        /// <summary>Counts the operations of one part document of the data ("parts" entry). Never throws: unknown data gives zeros.</summary>
        public static PartFacts Read(Dictionary<string, object> partDoc)
        {
            var facts = new PartFacts();
            try { object features; if (partDoc != null && partDoc.TryGetValue("features", out features)) facts.Walk(features, null); }
            catch { /* what was counted so far stays */ }
            return facts;
        }

        /// <summary>"Laser cutting, tapping M4 x 5, countersinking x 2, bending x 4".</summary>
        public string OperationsText()
        {
            var ops = new List<string> { "Laser cutting" };
            foreach (var t in Threads) ops.Add("tapping " + t.Key + " x " + t.Value);
            if (Countersinks > 0) ops.Add("countersinking x " + Countersinks);
            if (Bends > 0) ops.Add("bending x " + Bends);
            return string.Join(", ", ops.ToArray());
        }

        /// <summary>What Odoo gets for these choices, in the words of the table: "2 products in Odoo: cut + bent, coated".</summary>
        public static string ProductsText(int bends, bool coated, bool bendSeparate, bool coatingSeparate)
        {
            var groups = new List<string> { "cut" };
            if (bends > 0) { if (bendSeparate) groups.Add("bent"); else groups[groups.Count - 1] += " + bent"; }
            if (coated) { if (coatingSeparate) groups.Add("coated"); else groups[groups.Count - 1] += " + coated"; }
            return groups.Count + (groups.Count == 1 ? " product in Odoo: " : " products in Odoo: ") + string.Join(", ", groups.ToArray());
        }
    }

    /// <summary>
    /// Product setup during "Send to LaserCAM" of an assembly (owner 2026-11-18): the part window opens for the export
    /// anyway, so a small table is shown over it where the opened product is set up (coating, colour). The answers
    /// travel to LaserCAM inside the assembly data ("setup") and fill its Odoo plan, so little is left to mark there.
    /// They are remembered on this computer per file, so a product is asked once. No SolidWorks types here.
    /// </summary>
    public static class ProductSetup
    {
        /// <summary>Coatings LaserCAM knows by default: id, name, "1" when the coating has a colour. First = not coated.</summary>
        public static readonly string[][] Coatings =
        {
            new[] { "", "Not coated", "" },
            new[] { "powderCoating", "Powder coating", "1" },
            new[] { "zincPlating", "Zinc plating", "" },
            new[] { "hotDipGalvanizing", "Hot-dip galvanizing", "" },
            new[] { "anodizing", "Anodizing", "1" },
            new[] { "blackOxide", "Black oxide", "" },
            new[] { "passivation", "Passivation", "" },
            new[] { "brushing", "Brushing", "" },
        };
        public const string DefaultCoating = "powderCoating";

        public static int IndexOf(string coatingId)
        {
            for (int i = 0; i < Coatings.Length; i++) if (Coatings[i][0] == (coatingId ?? "")) return i;
            return 0;
        }
        public static bool HasColor(string coatingId) { return Coatings[IndexOf(coatingId)][2] == "1"; }

        public static string DefaultPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM"), "product-setup.json"); }
        }

        /// <summary>Remembered answers: product colour per assembly file and one answer per product file (paths in lower case).</summary>
        public sealed class Store
        {
            public readonly Dictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, SetupAnswer> Items = new Dictionary<string, SetupAnswer>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The last choice of separate products: what the table of a new part starts with.</summary>
            public bool BendSeparate, CoatingSeparate;
        }

        /// <summary>Reads the remembered answers; a missing or damaged file gives an empty store.</summary>
        public static Store Load(string path)
        {
            var s = new Store();
            try
            {
                if (!File.Exists(path)) return s;
                var root = Json.Obj(Json.Read(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) return s;
                object v;
                if (root.TryGetValue("colors", out v))
                {
                    var colors = Json.Obj(v);
                    if (colors != null) foreach (var kv in colors) { string c = Convert.ToString(kv.Value ?? ""); if (c != "") s.Colors[kv.Key] = c; }
                }
                if (root.TryGetValue("items", out v))
                {
                    var items = Json.Obj(v);
                    if (items != null)
                        foreach (var kv in items)
                        {
                            var d = Json.Obj(kv.Value);
                            if (d == null) continue;
                            string coating = Json.Str(d, "coating");
                            if (IndexOf(coating) == 0 && coating != "") coating = "";   // a coating this version does not know
                            var answer = new SetupAnswer { Coating = coating, Color = Json.Str(d, "color") };
                            object flag;
                            if (d.TryGetValue("bendSeparate", out flag) && flag is bool) answer.BendSeparate = (bool)flag;
                            if (d.TryGetValue("coatingSeparate", out flag) && flag is bool) answer.CoatingSeparate = (bool)flag;
                            s.Items[kv.Key] = answer;
                        }
                }
                if (root.TryGetValue("defaults", out v))
                {
                    var defaults = Json.Obj(v);
                    object flag;
                    if (defaults != null && defaults.TryGetValue("bendSeparate", out flag) && flag is bool) s.BendSeparate = (bool)flag;
                    if (defaults != null && defaults.TryGetValue("coatingSeparate", out flag) && flag is bool) s.CoatingSeparate = (bool)flag;
                }
            }
            catch { /* an unreadable file: everything is simply asked again */ }
            return s;
        }

        public static void Save(string path, Store s)
        {
            try
            {
                var colors = new Dictionary<string, object>();
                foreach (var kv in s.Colors) colors[kv.Key.ToLowerInvariant()] = kv.Value;
                var items = new Dictionary<string, object>();
                foreach (var kv in s.Items)
                {
                    var e = new Dictionary<string, object> { { "coating", kv.Value.Coating }, { "color", kv.Value.Color } };
                    if (kv.Value.BendSeparate.HasValue) e["bendSeparate"] = kv.Value.BendSeparate.Value;
                    if (kv.Value.CoatingSeparate.HasValue) e["coatingSeparate"] = kv.Value.CoatingSeparate.Value;
                    items[kv.Key.ToLowerInvariant()] = e;
                }
                var defaults = new Dictionary<string, object> { { "bendSeparate", s.BendSeparate }, { "coatingSeparate", s.CoatingSeparate } };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Write(new Dictionary<string, object> { { "version", 1 }, { "colors", colors }, { "defaults", defaults }, { "items", items } }), Encoding.UTF8);
            }
            catch { /* not remembered: asked again next time */ }
        }

        /// <summary>
        /// The "setup" object for LaserCAM: product colour and one entry per file name (lower case, as LaserCAM names
        /// the CAD file of an item). "asked" = answered in this send (replaces what LaserCAM has); otherwise it is a
        /// remembered answer that only fills a row LaserCAM has nothing for.
        /// </summary>
        public static Dictionary<string, object> ToJob(string color, IDictionary<string, SetupAnswer> answersByPath, ICollection<string> askedPaths)
        {
            var items = new Dictionary<string, object>();
            foreach (var kv in answersByPath)
            {
                string name = Path.GetFileName(kv.Key).ToLowerInvariant();
                if (name == "" || kv.Value == null) continue;
                var e = new Dictionary<string, object> { { "coating", kv.Value.Coating == "" ? null : kv.Value.Coating }, { "asked", ContainsPath(askedPaths, kv.Key) } };
                if (kv.Value.Color != "" && HasColor(kv.Value.Coating)) e["color"] = kv.Value.Color;
                if (kv.Value.BendSeparate.HasValue) e["bendSeparate"] = kv.Value.BendSeparate.Value;
                if (kv.Value.CoatingSeparate.HasValue) e["coatingSeparate"] = kv.Value.CoatingSeparate.Value;
                items[name] = e;
            }
            return new Dictionary<string, object> { { "version", 1 }, { "color", (color ?? "").Trim() }, { "items", items } };
        }

        static bool ContainsPath(ICollection<string> paths, string path)
        {
            foreach (string p in paths) if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// The table: one line per product with its coating and colour. Used twice: for the assembly and its
    /// sub-assemblies before the export starts, and for each sheet metal part when its window opens.
    /// </summary>
    public sealed class ProductSetupForm : Form
    {
        public sealed class Row
        {
            public string Key = "";
            public string Name = "";
            public SetupAnswer Answer = new SetupAnswer();
            internal ComboBox Combo;
            internal TextBox ColorBox;
            /// <summary>The user typed a colour of their own here: it no longer follows the product colour.</summary>
            internal bool OwnColor;
        }

        readonly IList<Row> _rows;
        readonly TextBox _productColor;
        readonly CheckBox _skipRest;
        readonly string _knownColor;
        /// <summary>True while the form itself writes into a colour box (not the user).</summary>
        bool _filling;
        /// <summary>Part table only: operations of the part and the choice of separate Odoo products.</summary>
        readonly PartFacts _facts;
        readonly CheckBox _bendSeparate, _coatingSeparate;
        readonly Label _products;

        /// <summary>The product colour: typed here (assembly table) or the one given (part table).</summary>
        public string ProductColor { get { return _productColor != null ? _productColor.Text.Trim() : _knownColor; } }
        /// <summary>"Do not ask for the remaining parts" was ticked.</summary>
        public bool SkipRest { get { return _skipRest != null && _skipRest.Checked; } }

        const int Pad = 12, Gap = 8, NameW = 260, CoatW = 170, ColorW = 120;

        public ProductSetupForm(string title, string heading, string note, string productColor, bool editProductColor, IList<Row> rows, string skipRestText, PartFacts facts = null)
        {
            _rows = rows;
            _facts = rows.Count == 1 ? facts : null;
            _knownColor = (productColor ?? "").Trim();
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Font = SystemFonts.MessageBoxFont;
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;

            int line = Font.Height;
            int rowH = Math.Max(line + 12, 28);
            int width = NameW + CoatW + ColorW + 2 * Gap;
            int y = Pad;

            var head = new Label { Text = heading, Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true, Location = new Point(Pad, y), Size = new Size(width, line + 6) };
            Controls.Add(head);
            y += head.Height + 2;
            if (!string.IsNullOrEmpty(note))
            {
                int lines = note.Split('\n').Length;
                var info = new Label { Text = note, ForeColor = SystemColors.GrayText, Location = new Point(Pad, y), Size = new Size(width, lines * line + 4) };
                Controls.Add(info);
                y += info.Height + 6;
            }
            if (editProductColor)
            {
                var lab = new Label { Text = "Product colour", Location = new Point(Pad, y + 4), Size = new Size(NameW, line + 4) };
                _productColor = new TextBox { Text = _knownColor, Location = new Point(Pad + NameW + Gap, y), Width = CoatW };
                _productColor.TextChanged += delegate { FillColors(); };
                var eg = new Label { Text = "e.g. RAL 9005", ForeColor = SystemColors.GrayText, Location = new Point(Pad + NameW + CoatW + 2 * Gap, y + 4), Size = new Size(ColorW, line + 4) };
                Controls.Add(lab); Controls.Add(_productColor); Controls.Add(eg);
                y += rowH + 4;
            }

            Controls.Add(new Label { Text = "Product", ForeColor = SystemColors.GrayText, Location = new Point(Pad, y), Size = new Size(NameW, line + 4) });
            Controls.Add(new Label { Text = "Coating", ForeColor = SystemColors.GrayText, Location = new Point(Pad + NameW + Gap, y), Size = new Size(CoatW, line + 4) });
            Controls.Add(new Label { Text = "Colour", ForeColor = SystemColors.GrayText, Location = new Point(Pad + NameW + CoatW + 2 * Gap, y), Size = new Size(ColorW, line + 4) });
            y += line + 6;

            int shown = Math.Min(rows.Count, 9);
            var table = new Panel { Location = new Point(Pad, y), Size = new Size(width + (rows.Count > shown ? SystemInformation.VerticalScrollBarWidth : 0), shown * rowH), AutoScroll = rows.Count > shown };
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                var name = new Label { Text = r.Name, AutoEllipsis = true, Location = new Point(0, i * rowH + 5), Size = new Size(NameW, line + 4) };
                r.Combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(NameW + Gap, i * rowH + 1), Width = CoatW };
                foreach (string[] c in ProductSetup.Coatings) r.Combo.Items.Add(c[1]);
                r.ColorBox = new TextBox { Location = new Point(NameW + CoatW + 2 * Gap, i * rowH + 1), Width = ColorW };
                r.OwnColor = r.Answer.Color != "" && !SameColor(r.Answer.Color, _knownColor);
                Row row = r;
                // The colour of a row follows the product colour until the user types another one into it (owner
                // 2026-11-18: "after filling the first one the others should fill too; a child is edited only when needed").
                r.ColorBox.TextChanged += delegate
                {
                    if (_filling) return;
                    string typed = row.ColorBox.Text.Trim();
                    row.OwnColor = typed != "" && !SameColor(typed, ProductColor);
                };
                r.Combo.SelectedIndexChanged += delegate { FillColor(row); UpdateProducts(); };
                r.Combo.SelectedIndex = ProductSetup.IndexOf(r.Answer.Coating);
                _filling = true;
                if (r.OwnColor) r.ColorBox.Text = r.Answer.Color;
                _filling = false;
                FillColor(r);
                table.Controls.Add(name); table.Controls.Add(r.Combo); table.Controls.Add(r.ColorBox);
            }
            Controls.Add(table);
            y += table.Height + 4;

            if (_facts != null)
            {
                // What the part needs (information) and how it becomes Odoo products (owner 2026-11-18: "see the bends,
                // countersinks and threads at once; set at once whether it is three products or one with operations").
                string ops = _facts.OperationsText();
                var opsLabel = new Label { Text = "Operations: " + char.ToLowerInvariant(ops[0]) + ops.Substring(1), AutoEllipsis = true, Location = new Point(Pad, y + 2), Size = new Size(table.Width, 2 * line + 4) };
                Controls.Add(opsLabel);
                y += opsLabel.Height + 4;
                SetupAnswer first = rows[0].Answer;
                _bendSeparate = new CheckBox { Text = "Bending is a separate product", Checked = first.BendSeparate == true, Enabled = _facts.Bends > 0, Location = new Point(Pad, y), Size = new Size(table.Width / 2 - Gap, line + 8) };
                _coatingSeparate = new CheckBox { Text = "Coating is a separate product", Checked = first.CoatingSeparate == true, Location = new Point(Pad + table.Width / 2, y), Size = new Size(table.Width / 2, line + 8) };
                _bendSeparate.CheckedChanged += delegate { UpdateProducts(); };
                _coatingSeparate.CheckedChanged += delegate { UpdateProducts(); };
                Controls.Add(_bendSeparate); Controls.Add(_coatingSeparate);
                y += _bendSeparate.Height + 2;
                _products = new Label { Font = new Font(Font, FontStyle.Bold), Location = new Point(Pad, y), Size = new Size(table.Width, line + 6) };
                Controls.Add(_products);
                y += _products.Height + 6;
                UpdateProducts();
            }

            var hint = new Label { Text = "The colour follows the product colour: change it in a row only where it differs. Everything can still be changed in LaserCAM.", ForeColor = SystemColors.GrayText, Location = new Point(Pad, y), Size = new Size(table.Width, 2 * line + 4) };
            Controls.Add(hint);
            y += hint.Height + 4;

            if (!string.IsNullOrEmpty(skipRestText))
            {
                _skipRest = new CheckBox { Text = skipRestText, Location = new Point(Pad, y), Size = new Size(table.Width, line + 8) };
                Controls.Add(_skipRest);
                y += _skipRest.Height + 4;
            }

            int btnH = line + 14;
            var ok = new Button { Text = "OK", Size = new Size(96, btnH), Location = new Point(Pad + table.Width - 96 - Gap - 130, y + 4) };
            var cancel = new Button { Text = "Cancel sending", Size = new Size(130, btnH), Location = new Point(Pad + table.Width - 130, y + 4), DialogResult = DialogResult.Cancel };
            ok.Click += delegate { if (Collect()) { DialogResult = DialogResult.OK; Close(); } };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            y += btnH + 4 + Pad;

            ClientSize = new Size(table.Width + 2 * Pad, y);
            if (rows.Count > 0) ActiveControl = editProductColor ? (Control)_productColor : rows[0].Combo;
        }

        /// <summary>Parks the window at the right edge of the screen under <paramref name="below"/> (the progress window), so the model stays visible.</summary>
        public void ParkUnder(Rectangle below)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int top = below.IsEmpty ? wa.Top + 20 : below.Bottom + 8;
            Location = new Point(wa.Right - Width - 20, Math.Min(top, Math.Max(wa.Top, wa.Bottom - Height)));
        }

        /// <summary>Part table: coating as a separate product makes sense only for a coated part; the bold line says what Odoo gets.</summary>
        void UpdateProducts()
        {
            if (_facts == null || _products == null || _rows[0].Combo == null) return;
            bool coated = _rows[0].Combo.SelectedIndex > 0;
            _coatingSeparate.Enabled = coated;
            _products.Text = PartFacts.ProductsText(_facts.Bends, coated, _bendSeparate.Checked, _coatingSeparate.Checked);
        }

        static bool SameColor(string a, string b) { return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }

        /// <summary>One row: the colour box is on only for a coating with colour; it shows the product colour unless the row has its own.</summary>
        void FillColor(Row r)
        {
            bool colored = ProductSetup.Coatings[Math.Max(0, r.Combo.SelectedIndex)][2] == "1";
            _filling = true;
            try
            {
                r.ColorBox.Enabled = colored;
                if (!colored) { r.ColorBox.Text = ""; r.OwnColor = false; }
                else if (!r.OwnColor) r.ColorBox.Text = ProductColor;
            }
            finally { _filling = false; }
        }

        void FillColors() { foreach (Row r in _rows) if (r.Combo != null && r.ColorBox != null) FillColor(r); }

        /// <summary>Writes the choices back into the rows. False (with a message) when a coloured coating has no colour at all.</summary>
        bool Collect()
        {
            string product = ProductColor;
            foreach (Row r in _rows)
            {
                string id = ProductSetup.Coatings[Math.Max(0, r.Combo.SelectedIndex)][0];
                string own = ProductSetup.HasColor(id) ? r.ColorBox.Text.Trim() : "";
                if (ProductSetup.HasColor(id) && own == "" && product == "")
                {
                    MessageBox.Show(this, "Enter the colour, for example RAL 9005" + (_productColor != null ? " (Product colour)." : " (Colour)."), AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (_productColor != null) _productColor.Focus(); else r.ColorBox.Focus();
                    return false;
                }
            }
            foreach (Row r in _rows)
            {
                string id = ProductSetup.Coatings[Math.Max(0, r.Combo.SelectedIndex)][0];
                r.Answer.Coating = id;
                // A colour equal to the product colour is not an own colour: the row then follows the product.
                string own = ProductSetup.HasColor(id) ? r.ColorBox.Text.Trim() : "";
                r.Answer.Color = SameColor(own, product) ? "" : own;
            }
            if (_facts != null)
            {
                _rows[0].Answer.BendSeparate = _facts.Bends > 0 ? (bool?)_bendSeparate.Checked : null;
                _rows[0].Answer.CoatingSeparate = _rows[0].Answer.Coating != "" ? (bool?)_coatingSeparate.Checked : null;
            }
            return true;
        }
    }

    /// <summary>A window handle as an owner of our dialogs (the SolidWorks main window).</summary>
    public sealed class HwndOwner : IWin32Window
    {
        readonly IntPtr _h;
        public HwndOwner(IntPtr handle) { _h = handle; }
        public IntPtr Handle { get { return _h; } }
    }
}
