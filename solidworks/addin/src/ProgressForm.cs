// Small always-on-top progress window: the user sees what the add-in is doing (export, Odoo, LaserCAM, uploads)
// instead of waiting blindly until the browser opens.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public class ProgressForm : Form
    {
        readonly Label _text = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 12, 12, 0), AutoEllipsis = true };
        readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100 };
        readonly Label _detail = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(12, 4, 12, 0), ForeColor = SystemColors.GrayText, AutoEllipsis = true };

        public ProgressForm(string title)
        {
            Text = title;
            Width = 460; Height = 130;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Not TopMost: it would cover SolidWorks' own dialogs and our message boxes (user 2026-10-15).
            // Parked in the top-right corner of the screen instead; our message boxes are shown with this form as owner.
            TopMost = false;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 20, wa.Top + 20);
            Font = SystemFonts.MessageBoxFont;
            if (Branding.AppIcon != null) Icon = Branding.AppIcon;
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            panel.Controls.Add(_detail); panel.Controls.Add(_bar); panel.Controls.Add(_text);
            Controls.Add(panel);
        }

        /// Update the step text and progress (0-100); repaints immediately (the add-in works on the UI thread).
        public void Step(int percent, string text, string detail = "")
        {
            _text.Text = text;
            _detail.Text = detail;
            _bar.Value = Math.Max(0, Math.Min(100, percent));
            if (!Visible) Show();
            Refresh();
            Application.DoEvents();
        }

        /// Owner for message boxes so they appear above this window (null when hidden).
        public IWin32Window OwnerOrNull { get { return Visible ? this : (IWin32Window)null; } }
    }
}
