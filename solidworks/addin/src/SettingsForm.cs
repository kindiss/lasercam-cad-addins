// Settings dialog: one row per setting; keys are masked.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public class SettingsForm : Form
    {
        readonly Dictionary<string, Control> _inputs = new Dictionary<string, Control>();
        public Settings Result { get; private set; }

        static readonly string[] Secret = { "LC_KEY", "ODOO_API_KEY" };
        static readonly Dictionary<string, string> Help = new Dictionary<string, string>
        {
            { "LC_API", "LaserCAM server API" },
            { "LC_PID", "Odoo contact id (res.partner) with the LaserCAM plan" },
            { "LC_KEY", "LaserCAM key from that contact" },
            { "ODOO_URL", "Your Odoo address" },
            { "ODOO_API_KEY", "Odoo user API key (My Profile -> Account Security)" },
            { "ODOO_DB", "Database name; empty for Odoo Online" },
            { "NUMBER_PROPERTY", "Part custom property holding the Odoo number" },
            { "TEMPLATE_NUMBER", "Product whose BOM sheet is used when the part has no BOM" },
            { "USE_ODOO_SHEET", "Material / thickness / sheet from the Odoo BOM sheet line" },
            { "ALWAYS_PICK_PRODUCT", "(not used since 1.0.9 - the Send dialog always opens)" },
            { "SEND_MODE", "Last choice in the Send dialog: lasercam | odoo" },
            { "ATTACH_DXF", "Send dialog default: DXF flat pattern" },
            { "ATTACH_STEP", "Send dialog default: STEP export" },
            { "ATTACH_PDF", "Send dialog default: drawing (<part>.SLDDRW) as PDF" },
            { "ATTACH_CAD", "Send dialog default: the part file (.SLDPRT)" },
            { "DEFAULT_GAS", "O2 / N2 / air (LC_Gas part property overrides)" },
            { "SHEET_W", "Default sheet width, mm" },
            { "SHEET_H", "Default sheet height, mm" },
            { "SHEET_MARGIN", "Sheet margin, mm" },
            { "SPACING", "Part spacing, mm" },
            { "PRICE_PER_KG", "Material price EUR/kg for the quote" },
            { "QUANTITY", "Quantity for the quote" },
            { "OPEN_APP", "Open LaserCAM in the browser (off = silent quote)" },
            { "SEND_ODOO_FROM_APP", "LaserCAM 'Save to Odoo' writes to Odoo itself" },
            { "WAIT_FOR_RESULT", "Wait for the browser result and copy it into the part" },
            { "APP_WAIT_MIN", "How long to wait for the browser result, minutes" },
        };

        public SettingsForm(Settings s)
        {
            Text = "LaserCAM settings";
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;

            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            int row = 0;
            foreach (var f in typeof(Settings).GetFields())
            {
                if (!f.IsPublic || f.IsStatic) continue;
                object v = f.GetValue(s);
                Control c;
                if (f.FieldType == typeof(bool)) c = new CheckBox { Checked = (bool)v, AutoSize = true };
                else
                {
                    var tb = new TextBox { Width = 250, Text = Convert.ToString(v, CultureInfo.InvariantCulture) };
                    if (Array.IndexOf(Secret, f.Name) >= 0) tb.UseSystemPasswordChar = true;
                    c = tb;
                }
                _inputs[f.Name] = c;
                grid.Controls.Add(new Label { Text = f.Name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, row);
                grid.Controls.Add(c, 1, row);
                string help; Help.TryGetValue(f.Name, out help);
                grid.Controls.Add(new Label { Text = help ?? "", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 2, row);
                row++;
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(10) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            ok.Click += (o, e) => Result = Read(s);
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            var path = new Label { Text = "File: " + Settings.Path, Dock = DockStyle.Bottom, ForeColor = SystemColors.GrayText, Padding = new Padding(10, 0, 0, 0), Height = 20 };
            Controls.Add(grid); Controls.Add(path); Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(640, grid.PreferredSize.Height + 75);
        }

        Settings Read(Settings orig)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in _inputs)
            {
                var cb = kv.Value as CheckBox;
                d[kv.Key] = cb != null ? (object)cb.Checked : ((TextBox)kv.Value).Text.Trim();
            }
            var s = new Settings();
            s.Apply(d);
            return s;
        }
    }
}
