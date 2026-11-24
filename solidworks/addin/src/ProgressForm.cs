// Small progress window: the user sees what the add-in is doing (export, Odoo, LaserCAM, uploads) instead of waiting
// blindly until the browser opens.
//
// The window lives on its OWN thread (owner 2026-11-24: "I cannot move the window, I need to be able to"): the add-in
// works on the SolidWorks thread, and a window of that thread only reacts between two steps - it could not be dragged
// while a flat pattern or a STEP was being exported. With its own thread it is moved by its title bar at any moment.
// The callers keep the same calls as before (Step, Hide, Visible, Bounds, Dispose).
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LaserCAM.SolidWorks
{
    public sealed class ProgressForm : IDisposable
    {
        readonly string _title;
        readonly object _gate = new object();
        readonly ManualResetEvent _ready = new ManualResetEvent(false);
        Thread _thread;
        Window _win;
        volatile bool _visible;
        Rectangle _bounds = Rectangle.Empty;
        /// Where the user left the window: the next one opens there (until SolidWorks is closed).
        static Point? _lastLocation;

        public ProgressForm(string title) { _title = title; }

        /// The SolidWorks main window. When set, this window keeps itself above SolidWorks (not above other programs):
        /// part and drawing windows that open for the export bring SolidWorks to the front and would otherwise cover the
        /// progress line (user 2026-11-18: "I want to see the bar while it exports").
        public IWin32Window StayAbove;

        public bool Visible { get { return _visible; } }

        /// Where the window is on the screen (empty until it was shown).
        public Rectangle Bounds { get { lock (_gate) return _bounds; } }

        /// The window belongs to another thread, so it is never the owner of a message box.
        public IWin32Window OwnerOrNull { get { return null; } }

        /// Update the step text and progress (0-100). Returns at once: the window paints on its own thread.
        public void Step(int percent, string text, string detail = "")
        {
            Ensure();
            Post(w => w.Set(percent, text, detail));
            _visible = true;
            // As before: SolidWorks gets a moment for its own messages between two steps, so Windows does not mark it
            // as not responding during a long send.
            Application.DoEvents();
        }

        public void Hide()
        {
            _visible = false;
            Post(w => w.Hide());
        }

        public void Dispose()
        {
            _visible = false;
            Post(w => { w.Close(); Application.ExitThread(); });
        }

        void Ensure()
        {
            if (_thread != null) return;
            IntPtr above = IntPtr.Zero;
            try { if (StayAbove != null) above = StayAbove.Handle; } catch { above = IntPtr.Zero; }
            _thread = new Thread(() =>
            {
                try
                {
                    _win = new Window(_title, above, this);
                    IntPtr created = _win.Handle;   // the handle must exist before the first call from the other thread
                    _ready.Set();
                    Application.Run();
                }
                catch { _ready.Set(); }
            });
            _thread.IsBackground = true;   // never keeps SolidWorks alive
            _thread.Name = "LaserCAM progress";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.WaitOne(5000);
        }

        void Post(Action<Window> action)
        {
            Window w = _win;
            if (w == null) return;
            try { w.BeginInvoke((MethodInvoker)delegate { try { action(w); } catch { /* the window is closing */ } }); }
            catch { /* the window is gone */ }
        }

        void Track(Rectangle bounds, bool moved)
        {
            lock (_gate) _bounds = bounds;
            if (moved) _lastLocation = bounds.Location;
        }

        sealed class Window : Form
        {
            readonly Label _text = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 12, 12, 0), AutoEllipsis = true };
            readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100 };
            readonly Label _detail = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(12, 4, 12, 0), ForeColor = SystemColors.GrayText, AutoEllipsis = true };
            readonly System.Windows.Forms.Timer _raise = new System.Windows.Forms.Timer { Interval = 400 };
            readonly IntPtr _above;
            readonly ProgressForm _host;
            bool _placed;

            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
            const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

            /// Shown without taking the keyboard from SolidWorks or from a dialog of the add-in.
            protected override bool ShowWithoutActivation { get { return true; } }

            public Window(string title, IntPtr above, ProgressForm host)
            {
                _above = above;
                _host = host;
                Text = title;
                // A fixed frame with a title bar: the window is dragged by it; no close button (the send ends it).
                FormBorderStyle = FormBorderStyle.FixedDialog;
                ControlBox = false; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                // Not TopMost: it would cover SolidWorks' own dialogs and our message boxes (user 2026-10-15).
                TopMost = false;
                Font = SystemFonts.MessageBoxFont;
                // Heights follow the font (it grows with the Windows display scale); the window is sized by its inside,
                // so the title bar never eats the room of the last line (user 2026-11-17: the detail line looked cut off).
                int line = Font.Height;
                _text.Height = line + 28;
                _detail.Height = line + 10;
                ClientSize = new Size(460, _text.Height + _bar.Height + _detail.Height + 16);
                // Where the user left it last time; else the top-right corner of the screen.
                var wa = Screen.PrimaryScreen.WorkingArea;
                Point at = new Point(wa.Right - Width - 20, wa.Top + 20);
                if (_lastLocation.HasValue && OnAScreen(new Rectangle(_lastLocation.Value, Size))) at = _lastLocation.Value;
                Location = at;
                if (Branding.AppIcon != null) Icon = Branding.AppIcon;
                var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
                panel.Controls.Add(_detail); panel.Controls.Add(_bar); panel.Controls.Add(_text);
                Controls.Add(panel);
                _raise.Tick += delegate { Raise(); };
                Move += delegate { if (_placed) _host.Track(Bounds, true); };
            }

            static bool OnAScreen(Rectangle r)
            {
                foreach (Screen s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) return true;
                return false;
            }

            public void Set(int percent, string text, string detail)
            {
                _text.Text = text;
                _detail.Text = detail;
                _bar.Value = Math.Max(0, Math.Min(100, percent));
                if (!Visible)
                {
                    Show();
                    _placed = true;
                    _raise.Start();
                    Raise();
                }
                _host.Track(Bounds, false);
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (!Visible) _raise.Stop();
            }

            /// Above SolidWorks while SolidWorks itself is in front; never above another program or a dialog.
            void Raise()
            {
                if (!Visible || _above == IntPtr.Zero) return;
                try { if (GetForegroundWindow() == _above) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
                catch { /* stays where it is */ }
            }
        }
    }
}
