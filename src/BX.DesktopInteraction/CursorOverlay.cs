using System.Drawing.Drawing2D;

namespace BX.DesktopInteraction;

internal sealed class CursorOverlay : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly NotifyIcon tray;
    internal SharedControl Control { get; } = new();
    internal bool Stopped => Control.Stopped;
    private bool hotkeyRegistered;
    private long nextHotkeyAttempt;
    internal Action? EmergencyStop;
    internal bool MarkerEnabled = true;
    internal CursorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new(40, 48);
        DoubleBuffered = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show/hide BX cursor marker", null, (_, _) => { MarkerEnabled = !MarkerEnabled; Invalidate(); });
        menu.Items.Add("Stop all BX Desktop Interaction instances and exit", null, (_, _) => { Control.Stop(); EmergencyStop?.Invoke(); Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "BX Desktop Interaction — Ctrl+Alt+F12 stops input", ContextMenuStrip = menu, Visible = true };
        timer.Tick += (_, _) =>
        {
            RegisterEmergencyHotkey();
            if (!Control.Active || Control.Stopped) EmergencyStop?.Invoke();
            if (Native.GetCursorPos(out var p)) Location = new(p.X - 6, p.Y - 6);
            Invalidate();
        };
        Shown += (_, _) =>
        {
            RegisterEmergencyHotkey();
            timer.Start();
        };
    }
    private void RegisterEmergencyHotkey()
    {
        if (hotkeyRegistered || Environment.TickCount64 < nextHotkeyAttempt) return;
        nextHotkeyAttempt = Environment.TickCount64 + 500;
        if (Native.RegisterHotKey(Handle, 1, 0x4000 | 0x0002 | 0x0001, 0x7B))
        {
            hotkeyRegistered = true;
            Control.HotkeyRegistered();
        }
        // Another compatible instance can own the session's hotkey. No stop is latched.
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000; return p; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) { Control.Stop(); EmergencyStop?.Invoke(); }
        if (m.Msg == 0x0084) { m.Result = -1; return; }
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (MarkerEnabled && Control.Active) PaintMarker(e.Graphics, 6, 6);
    }
    internal static void PaintMarker(Graphics g, int x, int y)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Point[] points = [new(x, y), new(x + 2, y + 26), new(x + 9, y + 19), new(x + 15, y + 31), new(x + 21, y + 28), new(x + 15, y + 17), new(x + 26, y + 16)];
        using var fill = new SolidBrush(Color.FromArgb(255, 0, 210, 255));
        using var outline = new Pen(Color.FromArgb(255, 12, 35, 55), 2);
        g.FillPolygon(fill, points);
        g.DrawPolygon(outline, points);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); if (hotkeyRegistered) { Native.UnregisterHotKey(Handle, 1); Control.HotkeyReleased(); hotkeyRegistered = false; } Control.Dispose(); }
        base.Dispose(disposing);
    }
}
