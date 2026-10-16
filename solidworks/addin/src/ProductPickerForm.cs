// The "Send" dialog of the SolidWorks add-in (opens on every click):
//   1. Action: send to LaserCAM (nesting; "Save to Odoo" there)  OR  send files straight to the Odoo product.
//   2. Files for the direct send: DXF flat pattern, STEP, PDF (drawing <part>.SLDDRW), CAD file (.SLDPRT).
//   3. Odoo product list (same columns as the Odoo Connector) and what the selected row is:
//      this product (update it) / a template for a new product / ignore (new product without template).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public enum PickMode { Cancel, Product, Template, NewProduct }
    public enum SendAction { LaserCAM, OdooFiles }

    public class ProductPickerForm : Form
    {
        readonly OdooClient _odoo;
        readonly int _preselectProductId;
        readonly RadioButton _actLaser = new RadioButton { Text = "Send to LaserCAM: nesting, weight and time; \"Save to Odoo\" there", AutoSize = true };
        readonly RadioButton _actOdoo = new RadioButton { Text = "Send files straight to the Odoo product (no nesting)", AutoSize = true };
        readonly CheckBox _fDxf = new CheckBox { Text = "DXF flat pattern", AutoSize = true };
        readonly CheckBox _fStep = new CheckBox { Text = "STEP", AutoSize = true };
        readonly CheckBox _fPdf = new CheckBox { Text = "PDF drawing (<part>.SLDDRW)", AutoSize = true };
        readonly CheckBox _fCad = new CheckBox { Text = "CAD file (.SLDPRT)", AutoSize = true };
        readonly TextBox _search = new TextBox { Width = 260 };
        readonly Button _btnSearch = new Button { Text = "Search", Width = 80 };
        readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
        readonly RadioButton _rowProduct = new RadioButton { Text = "the selected row IS this part (update it; its reference goes into the part's Number)", AutoSize = true, Checked = true };
        readonly RadioButton _rowTemplate = new RadioButton { Text = "the selected row is a TEMPLATE: create a new product, take material / thickness / sheet from its BOM", AutoSize = true };
        readonly RadioButton _rowNew = new RadioButton { Text = "ignore the list: new product without a template", AutoSize = true };
        readonly Button _ok = new Button { Text = "Send", Width = 120, Height = 30 };
        readonly Button _cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };

        public PickMode Mode { get; private set; }
        public SendAction Action { get; private set; }
        public bool SendDxf, SendStep, SendPdf, SendCad;
        public int ProductId { get; private set; }
        public int TemplateId { get; private set; }
        public string Code { get; private set; }
        public string Name { get; private set; }

        static readonly Regex SheetRe = new Regex(@"(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)\s*[xX*]\s*(\d+(?:[.,]\d+)?)");

        public ProductPickerForm(OdooClient odoo, Settings s, string number, string title, int foundProductId)
        {
            _odoo = odoo;
            _preselectProductId = foundProductId;
            Text = "LaserCAM - " + title + " (" + number + ")";
            Width = 1000; Height = 660;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            Font = SystemFonts.MessageBoxFont;
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;

            // --- top: action + files ---
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 8, 10, 0) };
            var actBox = new GroupBox { Text = "What to do", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10, 4, 10, 6) };
            var actFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            actFlow.Controls.AddRange(new Control[] { _actLaser, _actOdoo });
            actBox.Controls.Add(actFlow);
            var fileBox = new GroupBox { Text = "Files to attach to the Odoo product (direct send)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10, 4, 10, 6) };
            var fileFlow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            foreach (var c in new[] { _fDxf, _fStep, _fPdf, _fCad }) c.Margin = new Padding(3, 3, 18, 3);
            fileFlow.Controls.AddRange(new Control[] { _fDxf, _fStep, _fPdf, _fCad });
            fileBox.Controls.Add(fileFlow);
            top.Controls.Add(actBox); top.Controls.Add(fileBox);

            // --- middle: product list ---
            string head = foundProductId > 0
                ? "Odoo product \"" + number + "\" found and selected below."
                : "No Odoo product has the Internal Reference \"" + number + "\". Pick the row that IS this part, or a template, or create a new product.";
            var hint = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(10, 4, 10, 0), Text = head, AutoEllipsis = true };
            var searchRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 2, 10, 0), WrapContents = false };
            searchRow.Controls.AddRange(new Control[] { new Label { Text = "Odoo product (search code / name):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, _search, _btnSearch });
            foreach (var c in new[] { new { n = "Code", w = 120 }, new { n = "Product", w = 320 }, new { n = "BOM", w = 50 }, new { n = "Sheet (raw material)", w = 230 }, new { n = "Laser work center", w = 140 }, new { n = "DXF", w = 45 } })
                _list.Columns.Add(c.n, c.w);
            var rowBox = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(10, 4, 10, 0) };
            rowBox.Controls.AddRange(new Control[] { _rowProduct, _rowTemplate, _rowNew });
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 0), FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            bottom.Controls.AddRange(new Control[] { _cancel, _ok });

            Controls.Add(_list); Controls.Add(searchRow); Controls.Add(hint); Controls.Add(top); Controls.Add(rowBox); Controls.Add(bottom); Controls.Add(_status);
            _list.BringToFront();
            AcceptButton = _ok; CancelButton = _cancel;

            // defaults from settings (remembered from the last send)
            if (s.SEND_MODE == "odoo") _actOdoo.Checked = true; else _actLaser.Checked = true;
            _fDxf.Checked = s.ATTACH_DXF; _fStep.Checked = s.ATTACH_STEP; _fPdf.Checked = s.ATTACH_PDF; _fCad.Checked = s.ATTACH_CAD;
            EventHandler sync = (o, e) => { bool d = _actOdoo.Checked; foreach (var c in new[] { _fDxf, _fStep, _fPdf, _fCad }) c.Enabled = d; _ok.Text = d ? "Send files to Odoo" : "Send to LaserCAM"; };
            _actLaser.CheckedChanged += sync; _actOdoo.CheckedChanged += sync; sync(null, null);

            _search.Text = Seed(number);
            _btnSearch.Click += (o, e) => { var _ = Load(); };
            _search.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var _ = Load(); } };
            _list.DoubleClick += (o, e) => Finish();
            _ok.Click += (o, e) => Finish();
            Shown += (o, e) => { var _ = Load(); };
        }

        static string Seed(string number)
        {
            var m = Regex.Match(number ?? "", @"\d{3,}");
            return m.Success ? m.Value : (number ?? "");
        }

        void Finish()
        {
            Action = _actOdoo.Checked ? SendAction.OdooFiles : SendAction.LaserCAM;
            SendDxf = _fDxf.Checked; SendStep = _fStep.Checked; SendPdf = _fPdf.Checked; SendCad = _fCad.Checked;
            if (Action == SendAction.OdooFiles && !(SendDxf || SendStep || SendPdf || SendCad)) { MessageBox.Show("Tick at least one file to send.", AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_rowNew.Checked) { Mode = PickMode.NewProduct; }
            else
            {
                if (_list.SelectedItems.Count != 1) { MessageBox.Show("Select a product row in the list, or choose \"new product without a template\".", AddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                var d = (Dictionary<string, object>)_list.SelectedItems[0].Tag;
                Mode = _rowTemplate.Checked ? PickMode.Template : PickMode.Product;
                ProductId = Convert.ToInt32(d["id"]);
                TemplateId = Json.M2oId(d["product_tmpl_id"]);
                Code = Json.Str(d, "default_code");
                Name = Json.Str(d, "name");
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        async Task Load()
        {
            string q = _search.Text.Trim();
            _status.Text = "Searching Odoo...";
            _btnSearch.Enabled = false;
            try
            {
                var rows = await Task.Run(() => Query(q));
                _list.BeginUpdate();
                _list.Items.Clear();
                ListViewItem sel = null;
                foreach (var r in rows)
                {
                    var it = new ListViewItem(new[] { r.Code, r.Name, r.HasBom ? "yes" : "-", r.Sheet, r.LaserWc, r.Dxf ? "yes" : "-" }) { Tag = r.Row };
                    if (!r.HasBom) it.ForeColor = SystemColors.GrayText;
                    _list.Items.Add(it);
                    if (r.Id == _preselectProductId) sel = it;
                }
                _list.EndUpdate();
                if (sel != null) { sel.Selected = true; sel.EnsureVisible(); }
                else if (_list.Items.Count == 1) _list.Items[0].Selected = true;
                _status.Text = rows.Count + " products" + (q != "" ? " matching \"" + q + "\"" : "") + (rows.Count >= 100 ? " (first 100 - narrow the search)" : "") + ". Grey = no bill of materials.";
            }
            catch (Exception ex) { _status.Text = "Odoo error: " + ex.Message; }
            finally { _btnSearch.Enabled = true; }
        }

        class Item { public int Id, TmplId; public string Code = "", Name = "", Sheet = "", LaserWc = ""; public bool HasBom, Dxf; public Dictionary<string, object> Row; }

        List<Item> Query(string q)
        {
            var domain = new List<object>();
            if (q != "") { domain.Add("|"); domain.Add(new object[] { "default_code", "ilike", q }); domain.Add(new object[] { "name", "ilike", q }); }
            var prods = _odoo.SearchRead("product.product", domain.ToArray(), new[] { "id", "default_code", "name", "product_tmpl_id" }, 100);
            var items = prods.Select(p => new Item { Id = Convert.ToInt32(p["id"]), TmplId = Json.M2oId(p["product_tmpl_id"]), Code = Json.Str(p, "default_code"), Name = Json.Str(p, "name"), Row = p }).OrderBy(i => i.Code).ToList();
            if (items.Count == 0) return items;
            var tmplIds = items.Select(i => i.TmplId).Where(i => i > 0).Distinct().ToArray();
            var boms = _odoo.SearchRead("mrp.bom", new object[] { new object[] { "product_tmpl_id", "in", tmplIds } }, new[] { "id", "product_tmpl_id" }, 500);
            var bomIds = boms.Select(b => Convert.ToInt32(b["id"])).ToArray();
            var lines = bomIds.Length > 0 ? _odoo.SearchRead("mrp.bom.line", new object[] { new object[] { "bom_id", "in", bomIds } }, new[] { "bom_id", "product_id" }, 5000) : new List<Dictionary<string, object>>();
            var ops = bomIds.Length > 0 ? _odoo.SearchRead("mrp.routing.workcenter", new object[] { new object[] { "bom_id", "in", bomIds } }, new[] { "bom_id", "workcenter_id" }, 5000) : new List<Dictionary<string, object>>();
            var atts = _odoo.SearchRead("ir.attachment", new object[] { new object[] { "res_model", "=", "product.template" }, new object[] { "res_id", "in", tmplIds }, new object[] { "name", "=ilike", "%.dxf" } }, new[] { "res_id" }, 5000);
            foreach (var it in items)
            {
                var bom = boms.FirstOrDefault(b => Json.M2oId(b["product_tmpl_id"]) == it.TmplId);
                if (bom != null)
                {
                    it.HasBom = true;
                    int bid = Convert.ToInt32(bom["id"]);
                    foreach (var l in lines.Where(l => Json.M2oId(l["bom_id"]) == bid)) { string n = Json.M2oName(l["product_id"]); if (SheetRe.IsMatch(n)) { it.Sheet = n; break; } }
                    var op = ops.FirstOrDefault(o => Json.M2oId(o["bom_id"]) == bid && Regex.IsMatch(Json.M2oName(o["workcenter_id"]), "la[sz]er", RegexOptions.IgnoreCase)) ?? ops.FirstOrDefault(o => Json.M2oId(o["bom_id"]) == bid);
                    if (op != null) it.LaserWc = Json.M2oName(op["workcenter_id"]);
                }
                it.Dxf = atts.Any(a => Convert.ToInt32(a["res_id"]) == it.TmplId);
            }
            return items;
        }
    }
}
