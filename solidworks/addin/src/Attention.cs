using System;
using System.Runtime.InteropServices;

namespace LaserCAM.SolidWorks
{
    /// <summary>
    /// A message for the user while another program is in front (LaserCAM has just opened in the browser): the
    /// SolidWorks button in the taskbar flashes until SolidWorks comes to the front, and the message, owned by the
    /// SolidWorks window, is then right there (user 2026-11-18: "I find this window only with Alt+Tab").
    /// </summary>
    public static class Attention
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

        const uint FlashTray = 2, FlashUntilInFront = 12;

        /// <summary>
        /// Shows a dialog so that it is seen at once (owner 2026-11-20: "this window must pop up, now it opens and is
        /// not visible"): it belongs to the SolidWorks main window, so it can never be behind it, and it is brought to
        /// the front when it appears. When another program is in front, the SolidWorks taskbar button flashes.
        /// </summary>
        public static System.Windows.Forms.DialogResult ShowDialogInFront(System.Windows.Forms.Form dialog, IntPtr main)
        {
            EventHandler up = (o, e) =>
            {
                try { dialog.Activate(); dialog.BringToFront(); SetForegroundWindow(dialog.Handle); } catch { /* stays where Windows put it */ }
            };
            dialog.Shown += up;
            try
            {
                if (main != IntPtr.Zero && OtherProgramInFront()) Flash(main);
                return main != IntPtr.Zero ? dialog.ShowDialog(new HwndOwner(main)) : dialog.ShowDialog();
            }
            finally { dialog.Shown -= up; }
        }

        /// <summary>The SolidWorks main window (zero when it cannot be found).</summary>
        public static IntPtr MainWindowOf(global::SolidWorks.Interop.sldworks.ISldWorks sw)
        {
            try { var f = sw.Frame() as global::SolidWorks.Interop.sldworks.IFrame; if (f != null) return new IntPtr(f.GetHWndx64()); } catch { /* next way */ }
            try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        /// <summary>True when the window in front belongs to another program (or nothing is in front).</summary>
        public static bool OtherProgramInFront()
        {
            try
            {
                IntPtr front = GetForegroundWindow();
                if (front == IntPtr.Zero) return true;
                uint pid;
                GetWindowThreadProcessId(front, out pid);
                return pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        /// <summary>Makes the taskbar button of the window flash until the window comes to the front. False = not done.</summary>
        public static bool Flash(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            try
            {
                var info = new FLASHWINFO { hwnd = window, dwFlags = FlashTray | FlashUntilInFront, uCount = uint.MaxValue, dwTimeout = 0 };
                info.cbSize = (uint)Marshal.SizeOf(info);
                FlashWindowEx(ref info);
                return true;
            }
            catch { return false; }
        }
    }
}
