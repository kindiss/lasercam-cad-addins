// The type of a part whose shape could not be recognised, chosen by the person when sending (owner 2026-11-24:
// "when it cannot be told on export, let the person choose what type the part is"). The choice is remembered per
// part file in %APPDATA%\LaserCAM\part-types.json, so a part is asked about once; holding Shift on Send asks again.
// Nothing is written into the part itself.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public static class PartTypes
    {
        /// <summary>Type id (as in the data file, shape.chosen) and the text shown.</summary>
        public static readonly string[][] Kinds =
        {
            new[] { PartShape.Plate, "Sheet part (cut from sheet, flat or bent)" },
            new[] { PartShape.Tube, "Tube or profile (cut to length)" },
            new[] { PartShape.Turned, "Turned part" },
            new[] { PartShape.Milled, "Milled part" },
            new[] { PartShape.Purchased, "Bought part (not made here)" },
        };

        static Dictionary<string, string> _map;

        public static string DefaultPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserCAM"), "part-types.json"); }
        }

        public static int IndexOf(string kind)
        {
            for (int i = 0; i < Kinds.Length; i++) if (Kinds[i][0] == (kind ?? "")) return i;
            return -1;
        }

        /// <summary>The remembered type of a part file; "" = never chosen.</summary>
        public static string Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            Load();
            string kind;
            return _map.TryGetValue(path, out kind) && IndexOf(kind) >= 0 ? kind : "";
        }

        public static void Set(string path, string kind)
        {
            if (string.IsNullOrEmpty(path) || IndexOf(kind) < 0) return;
            Load();
            _map[path] = kind;
        }

        static void Load()
        {
            if (_map != null) return;
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(DefaultPath)) return;
                var root = Json.Obj(Json.Read(File.ReadAllText(DefaultPath, Encoding.UTF8)));
                object v;
                var items = root != null && root.TryGetValue("items", out v) ? Json.Obj(v) : null;
                if (items != null) foreach (var kv in items) { string k = Convert.ToString(kv.Value ?? ""); if (k != "") _map[kv.Key] = k; }
            }
            catch { /* an unreadable file: the parts are simply asked about again */ }
        }

        public static void Save()
        {
            if (_map == null) return;
            try
            {
                var items = new Dictionary<string, object>();
                foreach (var kv in _map) items[kv.Key.ToLowerInvariant()] = kv.Value;
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath));
                File.WriteAllText(DefaultPath, Json.Write(new Dictionary<string, object> { { "version", 1 }, { "items", items } }), Encoding.UTF8);
            }
            catch { /* not remembered: asked again next time */ }
        }
    }

    /// <summary>One list of the parts that could not be recognised, a type to choose for each.</summary>
    public sealed class PartTypeForm : Form
    {
        public sealed class Row
        {
            public string Key = "", Name = "", Why = "";
            /// <summary>In: the type offered first. Out: the type chosen.</summary>
            public string Kind = "";
            internal ComboBox Combo;
        }

        const int Pad = 12, Gap = 8, NameW = 220, WhyW = 330, KindW = 270;

        public PartTypeForm(string title, IList<Row> rows)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;

            int line = Font.Height;
            int rowH = Math.Max(line + 12, 28);
            int width = NameW + WhyW + KindW + 2 * Gap;
            int y = Pad;

            string heading = rows.Count == 1 ? "The type of this part could not be recognised" : "The type of " + rows.Count + " parts could not be recognised";
            var head = new Label { Text = heading, Font = new Font(Font, FontStyle.Bold), Location = new Point(Pad, y), Size = new Size(width, line + 6) };
            Controls.Add(head);
            y += head.Height + 2;
            var info = new Label
            {
                Text = "Choose what each part is. A suggestion is filled in - check it.\nThe choice is remembered for the part file; hold Shift when clicking Send to choose again.",
                ForeColor = SystemColors.GrayText, Location = new Point(Pad, y), Size = new Size(width, 2 * line + 4),
            };
            Controls.Add(info);
            y += info.Height + 8;

            Controls.Add(new Label { Text = "Part", ForeColor = SystemColors.GrayText, Location = new Point(Pad, y), Size = new Size(NameW, line + 4) });
            Controls.Add(new Label { Text = "What was found", ForeColor = SystemColors.GrayText, Location = new Point(Pad + NameW + Gap, y), Size = new Size(WhyW, line + 4) });
            Controls.Add(new Label { Text = "Type", ForeColor = SystemColors.GrayText, Location = new Point(Pad + NameW + WhyW + 2 * Gap, y), Size = new Size(KindW, line + 4) });
            y += line + 6;

            int shown = Math.Min(rows.Count, 10);
            var tips = new ToolTip();
            var table = new Panel { Location = new Point(Pad, y), Size = new Size(width + (rows.Count > shown ? SystemInformation.VerticalScrollBarWidth : 0), shown * rowH), AutoScroll = rows.Count > shown };
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                var name = new Label { Text = r.Name, AutoEllipsis = true, Location = new Point(0, i * rowH + 5), Size = new Size(NameW, line + 4) };
                var why = new Label { Text = r.Why, AutoEllipsis = true, ForeColor = SystemColors.GrayText, Location = new Point(NameW + Gap, i * rowH + 5), Size = new Size(WhyW, line + 4) };
                tips.SetToolTip(name, r.Name);
                tips.SetToolTip(why, r.Why);
                r.Combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(NameW + WhyW + 2 * Gap, i * rowH + 1), Width = KindW };
                foreach (string[] k in PartTypes.Kinds) r.Combo.Items.Add(k[1]);
                r.Combo.SelectedIndex = Math.Max(0, PartTypes.IndexOf(r.Kind));
                table.Controls.Add(name); table.Controls.Add(why); table.Controls.Add(r.Combo);
            }
            Controls.Add(table);
            y += table.Height + 8;

            int btnH = line + 14;
            var ok = new Button { Text = "OK", Size = new Size(96, btnH), Location = new Point(Pad + table.Width - 96 - Gap - 130, y + 4) };
            var cancel = new Button { Text = "Cancel sending", Size = new Size(130, btnH), Location = new Point(Pad + table.Width - 130, y + 4), DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                foreach (Row r in rows) r.Kind = PartTypes.Kinds[Math.Max(0, r.Combo.SelectedIndex)][0];
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            y += btnH + 4 + Pad;

            ClientSize = new Size(table.Width + 2 * Pad, y);
            if (rows.Count > 0) ActiveControl = rows[0].Combo;
        }
    }
}
