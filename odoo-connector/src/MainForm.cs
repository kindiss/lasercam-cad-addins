// LaserCAM Odoo Connector - BOM picker: search Odoo bills of materials, tick them, "Nest in LaserCAM".
// Same flow as the Odoo module's "Nest in LaserCAM" button, for Odoo Enterprise / Online (no module needed):
// the LaserCAM server plays the module's role, the browser does the nesting and "Save to Odoo".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LaserCAM.SolidWorks;

namespace LaserCAM.OdooConnector
{
    public class MainForm : Form
    {
        Settings _s;
        OdooClient _odoo;
        LaserCamClient _lc;
        readonly TextBox _search = new TextBox { Width = 260 };
        readonly Button _btnSearch = new Button { Text = "Search", Width = 80 };
        readonly Button _btnSettings = new Button { Text = "Settings...", Width = 90 };
        readonly Button _btnNest = new Button { Text = "Nest in LaserCAM", Width = 150, Height = 32 };
        readonly CheckBox _template = new CheckBox { Text = "Template only (new products are copies of it)", AutoSize = true, Checked = true };
        readonly ListView _list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
        readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        readonly Label _odooLbl = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 8, 3, 3) };
        List<BomInfo> _boms = new List<BomInfo>();

        public MainForm()
        {
            Text = "LaserCAM Odoo Connector";
            Width = 1000; Height = 620;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            top.Controls.AddRange(new Control[] { new Label { Text = "Bill of materials:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, _search, _btnSearch, _btnSettings, _odooLbl });
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 6, 8, 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            bottom.Controls.AddRange(new Control[] { _btnNest, new Label { Width = 20 }, _template });
            _template.Margin = new Padding(3, 9, 3, 3);
            foreach (var c in new[] { new { n = "Code", w = 110 }, new { n = "Product", w = 330 }, new { n = "Components", w = 90 }, new { n = "Sheet (raw material)", w = 230 }, new { n = "Laser work center", w = 140 }, new { n = "DXF", w = 50 } })
                _list.Columns.Add(c.n, c.w);
            Controls.Add(_list); Controls.Add(top); Controls.Add(bottom); Controls.Add(_status);
            _list.BringToFront();

            _btnSearch.Click += (o, e) => { var _ = LoadBoms(); };
            _search.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var _ = LoadBoms(); } };
            _btnSettings.Click += (o, e) => { if (EditSettings()) { var _ = LoadBoms(); } };
            _btnNest.Click += (o, e) => { var _ = Nest(); };
            Shown += async (o, e) =>
            {
                _s = Settings.Load();
                if (string.IsNullOrEmpty(_s.ODOO_API_KEY) || string.IsNullOrEmpty(_s.LC_KEY) || _s.LC_PID == "0")
                {
                    MessageBox.Show("Welcome to LaserCAM. Fill in your LaserCAM key (LC_PID, LC_KEY) and the Odoo API key once; they are stored on this PC only.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (!EditSettings()) { Close(); return; }
                }
                Apply();
                await LoadBoms();
            };
        }

        bool EditSettings()
        {
            using (var f = new SettingsForm(_s ?? Settings.Load()))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                _s = f.Result; _s.Save(); Apply();
                return true;
            }
        }
        void Apply()
        {
            _odoo = new OdooClient(_s.ODOO_URL, _s.ODOO_API_KEY, _s.ODOO_DB);
            _lc = new LaserCamClient(_s.LC_API, _s.LC_PID, _s.LC_KEY);
            _odooLbl.Text = "Odoo: " + _s.ODOO_URL;
        }
        void Status(string s) { _status.Text = s; }

        async Task LoadBoms()
        {
            if (_odoo == null) return;
            string q = _search.Text;
            Status("Loading bills of materials from Odoo...");
            _btnSearch.Enabled = false;
            try
            {
                var boms = await Task.Run(() => OdooExport.Search(_odoo, q));
                _boms = boms;
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var b in boms)
                {
                    var it = new ListViewItem(new[] { b.Code, b.ProductName, b.Lines.Count.ToString(), b.SheetName, b.LaserOp, b.DxfCount > 0 ? "yes" : "-" }) { Tag = b };
                    if (b.DxfCount == 0) it.ForeColor = SystemColors.GrayText;
                    _list.Items.Add(it);
                }
                _list.EndUpdate();
                Status(boms.Count + " bills of materials" + (q.Trim() != "" ? " matching \"" + q.Trim() + "\"" : " (newest)") + ". Tick one or more and click Nest in LaserCAM. Grey = no DXF attached.");
            }
            catch (Exception ex)
            {
                Status("Odoo error: " + ex.Message);
                MessageBox.Show("Could not read Odoo:\n" + ex.Message + "\n\nCheck ODOO_URL / ODOO_API_KEY in Settings and that the user has Manufacturing rights.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _btnSearch.Enabled = true; }
        }

        async Task Nest()
        {
            var sel = _list.CheckedItems.Cast<ListViewItem>().Select(i => (BomInfo)i.Tag).ToList();
            if (sel.Count == 0 && _list.SelectedItems.Count == 1) sel.Add((BomInfo)_list.SelectedItems[0].Tag);
            if (sel.Count == 0) { MessageBox.Show("Tick at least one bill of materials.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            _btnNest.Enabled = false;
            try
            {
                Status("Collecting BOM lines, work centers and DXF from Odoo...");
                var pkg = await Task.Run(() => OdooExport.Collect(_odoo, sel));
                if (pkg.Dxfs.Count == 0 && MessageBox.Show("None of the selected products has a DXF attachment. LaserCAM will open the BOM without parts (you can drop DXF files there). Continue?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Status("Creating the LaserCAM job...");
                var odoo = new Dictionary<string, object> { { "url", _s.ODOO_URL }, { "apiKey", _s.ODOO_API_KEY } };
                if (!string.IsNullOrEmpty(_s.ODOO_DB)) odoo["db"] = _s.ODOO_DB;
                var body = new Dictionary<string, object>
                {
                    { "bom_csv", pkg.BomCsv }, { "wc_csv", pkg.WcCsv },
                    { "dxfs", pkg.Dxfs.Select(d => new Dictionary<string, object> { { "name", d.Key }, { "b64", d.Value } }).ToArray() },
                    { "odoo", odoo }, { "template", _template.Checked },
                    { "templateTmplId", pkg.TemplateTmplId },
                };
                var resp = await Task.Run(() => _lc.Post("/odoo-job", body));
                string url = Json.Str(resp, "url");
                if (url == "") throw new Exception("LaserCAM did not return a job url.");
                Process.Start(url);
                Status("LaserCAM opened in the browser with " + string.Join(", ", pkg.Codes) + " (" + pkg.Dxfs.Count + " DXF). Nest there and click \"Save to Odoo\". The link works once, for 1 hour.");
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _btnNest.Enabled = true; }
        }
    }
}
