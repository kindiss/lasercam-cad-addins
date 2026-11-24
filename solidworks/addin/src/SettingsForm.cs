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
            { "LC_APP_URL", "LaserCAM address to open; empty = the usual one" },
            { "LC_PID", "Odoo contact id (res.partner) with the LaserCAM plan" },
            { "LC_KEY", "LaserCAM key from that contact" },
            { "ODOO_URL", "Your Odoo address" },
            { "ODOO_API_KEY", "Odoo user API key (My Profile -> Account Security)" },
            { "ODOO_DB", "Database name; empty for Odoo Online" },
            { "ODOO_NUMBER_FROM", "What the Odoo number (Internal Reference) is taken from" },
            { "NUMBER_PROPERTY", "Name of that custom property (used only with \"Custom property\" above)" },
            { "TEMPLATE_NUMBER", "Product whose BOM sheet is used when the part has no BOM" },
            { "USE_ODOO_SHEET", "Material / thickness / sheet from the Odoo BOM sheet line" },
            { "ALWAYS_PICK_PRODUCT", "(not used since 1.0.9 - the Send dialog always opens)" },
            { "SEND_MODE", "Last choice in the Send dialog: lasercam-only | lasercam | odoo" },
            { "ATTACH_DXF", "Send dialog default: DXF flat pattern" },
            { "ATTACH_STEP", "Send dialog default: STEP export" },
            { "ATTACH_PDF", "Send dialog default: drawing (<part>.SLDDRW) as PDF" },
            { "ATTACH_CAD", "Send dialog default: the part file (.SLDPRT)" },
            { "EXPORT_STEP", "Assembly: export a STEP of every sheet metal part and send it to LaserCAM" },
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
            { "CHECK_UPDATES", "Tell me when a newer version is available (asks once a day)" },
            { "SHOW_READ_DATA", "Show the \"Read data...\" button (for troubleshooting; needs a SolidWorks restart)" },
        };

        public SettingsForm(Settings s)
        {
            Text = "LaserCAM settings";
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;
            // An ordinary window: it can be dragged by its edges, made small or maximised (owner 2026-11-20: "I need a
            // normally controllable window"). The settings scroll, Save and Cancel stay at the bottom.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = false;
            MinimumSize = new Size(420, 260);
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
                else if (f.Name == "ODOO_NUMBER_FROM")
                {
                    // A choice from a list, not free text (owner 2026-11-24): file name, title or the custom property.
                    var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
                    foreach (string[] source in Settings.NumberSources) list.Items.Add(source[1]);
                    list.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.NumberSources, x => x[0] == s.NumberFrom));
                    // The property name matters only with "Custom property": greyed out otherwise.
                    list.SelectedIndexChanged += delegate
                    {
                        Control name;
                        if (_inputs.TryGetValue("NUMBER_PROPERTY", out name)) name.Enabled = Settings.NumberSources[Math.Max(0, list.SelectedIndex)][0] == Settings.FromProperty;
                    };
                    c = list;
                }
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
            Control propName;
            if (_inputs.TryGetValue("NUMBER_PROPERTY", out propName)) propName.Enabled = s.NumberFrom == Settings.FromProperty;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(10) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            ok.Click += (o, e) => Result = Read(s);
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            var path = new Label { Text = "File: " + Settings.Path, Dock = DockStyle.Bottom, ForeColor = SystemColors.GrayText, Padding = new Padding(10, 0, 0, 0), Height = 20 };
            // The settings scroll inside their own area, Save and Cancel stay at the bottom of the window (owner 2026-11-20:
            // with one more setting the window became taller than the screen and Save could not be reached).
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            body.Controls.Add(grid);
            Controls.Add(body); Controls.Add(path); Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
            const int below = 75;   // the file path line and the buttons
            int work = Screen.FromPoint(Cursor.Position).WorkingArea.Height;
            int frame = Height - ClientSize.Height;   // title bar and borders
            int wanted = grid.PreferredSize.Height + below;
            int height = ClientHeightFor(wanted, work - frame);
            // Wide enough for all three columns: at 640 the help text on the right was cut off and the window cannot be resized.
            // When the content scrolls, the scroll bar needs its own width.
            ClientSize = new Size(Math.Max(640, grid.PreferredSize.Width) + (height < wanted ? SystemInformation.VerticalScrollBarWidth : 0), height);
        }

        /// <summary>Client height the window opens with: the content's own height, but no more than three quarters of the screen.</summary>
        internal static int ClientHeightFor(int contentHeight, int workingAreaHeight)
        {
            // The window opens at most about three quarters of the screen high: the rest scrolls, and the window can be
            // dragged taller or maximised by hand.
            int room = Math.Max(240, workingAreaHeight * 3 / 4);
            return Math.Min(contentHeight, room);
        }

        Settings Read(Settings orig)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in _inputs)
            {
                var cb = kv.Value as CheckBox;
                var list = kv.Value as ComboBox;
                if (list != null) d[kv.Key] = Settings.NumberSources[Math.Max(0, list.SelectedIndex)][0];
                else d[kv.Key] = cb != null ? (object)cb.Checked : ((TextBox)kv.Value).Text.Trim();
            }
            var s = new Settings();
            s.Apply(d);
            return s;
        }
    }
}
