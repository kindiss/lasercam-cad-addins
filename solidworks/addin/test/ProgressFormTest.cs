// Test of the progress window (src\ProgressForm.cs) without SolidWorks. Owner 2026-11-24: "I cannot move the window,
// I need to be able to" - the add-in keeps its thread busy during a send, so the window must live on a thread of its own.
// Here the calling thread is kept busy (it sleeps, as SolidWorks does inside an export) and the window is checked from
// outside: it must not be "not responding", and it must follow when it is moved.
// A small window titled "progress test" shows on the screen for a few seconds while this runs.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using LaserCAM.SolidWorks;

static class ProgressFormTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    static int _failed, _run;
    static void Check(string name, bool ok, string detail = "")
    {
        _run++;
        if (!ok) _failed++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail != "" ? "  ->  " + detail : ""));
    }

    [STAThread]
    static int Main()
    {
        const string Title = "progress test";
        var p = new ProgressForm(Title);
        p.Step(10, "First step", "detail");
        Thread.Sleep(700);
        IntPtr h = FindWindow(null, Title);
        Check("the window is on the screen after the first step", h != IntPtr.Zero && IsWindowVisible(h) && p.Visible);
        Check("its place is known to the caller", p.Bounds.Width > 300 && p.Bounds.Height > 50, p.Bounds.ToString());

        // The calling thread is busy for longer than Windows waits before it calls a window "not responding" (5 s).
        Thread.Sleep(6000);
        Check("the window answers while the calling thread is busy", !IsHungAppWindow(h));

        // Moved from outside, as the mouse would do it: the window follows at once, though nobody calls Step.
        SetWindowPos(h, IntPtr.Zero, 140, 160, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Thread.Sleep(400);
        Check("moved while the calling thread is busy: the window is at the new place", p.Bounds.X == 140 && p.Bounds.Y == 160, p.Bounds.Location.ToString());

        p.Hide();
        Thread.Sleep(300);
        Check("hidden on request", !IsWindowVisible(h) && !p.Visible);
        p.Step(60, "Second step");
        Thread.Sleep(300);
        Check("shown again by the next step, where it was left", IsWindowVisible(h) && p.Bounds.X == 140 && p.Bounds.Y == 160, p.Bounds.Location.ToString());
        p.Dispose();
        Thread.Sleep(400);
        Check("gone after the send ends", FindWindow(null, Title) == IntPtr.Zero);

        // The next send in the same SolidWorks session: the window opens where the user left the last one.
        var q = new ProgressForm(Title);
        q.Step(5, "Another send");
        Thread.Sleep(500);
        Check("the next window opens where the last one was left", q.Bounds.X == 140 && q.Bounds.Y == 160, q.Bounds.Location.ToString());
        q.Dispose();
        Thread.Sleep(300);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL " + _run + " PASSED" : _failed + " OF " + _run + " FAILED");
        return _failed;
    }
}
