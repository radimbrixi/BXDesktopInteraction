using System.Drawing.Imaging;

namespace BX.DesktopInteraction;

internal sealed class MouseController(CursorOverlay overlay, bool enableHid = false)
{
    private VirtualHidClient? hid;
    private readonly HashSet<string> held = [];
    private readonly object gate = new();
    internal object Status() => new { backend = "SendInput", kernelDriver = false, stopped = overlay.Stopped, control = overlay.Control.Status(), bounds = Bounds(), monitors = Screen.AllScreens.Select((s,i) => new { index=i, name=s.DeviceName, primary=s.Primary, x=s.Bounds.X, y=s.Bounds.Y, width=s.Bounds.Width, height=s.Bounds.Height }).ToArray(), cursor = Position(), emergencyStop = "Ctrl+Alt+F12", coordinates = "physical desktop pixels; origin can be negative" };
    internal void TakeControl() => overlay.Control.Claim();
    internal static object Bounds() { var b = SystemInformation.VirtualScreen; return new { x = b.X, y = b.Y, width = b.Width, height = b.Height }; }
    internal static object Position() { if (!Native.GetCursorPos(out var p)) throw new InvalidOperationException("Cannot read cursor position."); return new { x = p.X, y = p.Y }; }
    private void Check()
    {
        if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) throw new OperationCanceledException("Escape is held; mouse operation cancelled.");
    }
    internal async Task Move(int x, int y, int duration = 250)
    {
        var b = SystemInformation.VirtualScreen;
        if (!b.Contains(x, y)) throw new ArgumentOutOfRangeException(nameof(x), "Point is outside the virtual desktop.");
        if (duration is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(duration));
        Native.GetCursorPos(out var start);
        int steps = Math.Max(1, duration / 10);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps; t = t * t * (3 - 2 * t);
            int px = (int)Math.Round(start.X + (x - start.X) * t), py = (int)Math.Round(start.Y + (y - start.Y) * t);
            overlay.Control.Execute(() => { Check(); Native.Send(0x0001 | 0x8000 | 0x4000,
                (int)Math.Round((px - b.X) * 65535.0 / Math.Max(1, b.Width - 1)),
                (int)Math.Round((py - b.Y) * 65535.0 / Math.Max(1, b.Height - 1))); });
            if (duration > 0) await Task.Delay(Math.Max(1, duration / steps));
        }
    }
    private static (uint down, uint up, uint data) ButtonFlags(string button) => button switch
    {
        "left" => (2, 4, 0), "right" => (8, 16, 0), "middle" => (32, 64, 0),
        "x1" => (128, 256, 1), "x2" => (128, 256, 2), _ => throw new ArgumentException("Unknown mouse button.")
    };
    internal void Button(string button, bool down)
    {
        var f = ButtonFlags(button);
        lock (gate)
        {
            int mask = button switch { "left" => 1, "right" => 2, "middle" => 4, "x1" => 8, _ => 16 };
            if (down) overlay.Control.Execute(() => { Check(); Native.Send(f.down, data: f.data); overlay.Control.SetButton(mask, true); });
            else overlay.Control.ReleaseButton(mask, () => Native.Send(f.up, data: f.data));
            if (down) held.Add(button); else held.Remove(button);
        }
    }
    internal void TypeText(string text)
    {
        if (text.Length is < 1 or > 1000 || text.Any(char.IsControl))
            throw new ArgumentException("Text must contain 1–1000 printable characters; no control keys.");
        var target = Native.GetForegroundWindow();
        foreach (char character in text)
        {
            overlay.Control.Execute(() => {
                Check();
                if (Native.GetForegroundWindow() != target)
                    throw new OperationCanceledException("Foreground window changed while typing.");
                Native.SendUnicode(character);
            });
        }
    }
    internal async Task Click(string button, int count)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count));
        for (int i = 0; i < count; i++)
        {
            Button(button, true);
            try { await Task.Delay(45); } finally { Button(button, false); }
            if (i + 1 < count) await Task.Delay((int)Math.Min(100, Native.GetDoubleClickTime() / 3));
        }
    }
    internal async Task Drag(int x1, int y1, int x2, int y2, int duration, string button)
    {
        // Validate destination before pressing the button.
        if (!SystemInformation.VirtualScreen.Contains(x2, y2)) throw new ArgumentException("Drag destination is outside the desktop.");
        if (duration is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(duration));
        await Move(x1, y1);
        Button(button, true);
        try { await Move(x2, y2, duration); } finally { Button(button, false); }
    }
    internal void Scroll(int delta, bool horizontal)
    {
        if (delta is < -12000 or > 12000) throw new ArgumentOutOfRangeException(nameof(delta));
        overlay.Control.Execute(() => { Check(); Native.Send(horizontal ? 0x1000u : 0x0800u, data: unchecked((uint)delta)); });
    }
    internal void ReleaseAll()
    {
        lock (gate)
        {
            overlay.Control.ReleaseOwnedButtons();
            held.Clear();
            if (hid is not null)
            {
                try { hid.Report(0, 0, 0, 0, 0); } catch (Exception e) { Console.Error.WriteLine(e.Message); }
                hid.Dispose(); hid = null;
            }
        }
    }
    internal void HidReport(int dx, int dy, int buttons, int wheel, int horizontalWheel)
    {
        lock (gate)
        {
            overlay.Control.Execute(() => {
                Check();
                if (!enableHid) throw new InvalidOperationException("Start with --enable-hid to enable the separately installed driver transport.");
                hid ??= new VirtualHidClient();
                hid.Report(dx, dy, buttons, wheel, horizontalWheel);
            });
        }
    }
    internal object[] Capture(string scope, int monitorIndex = 0)
    {
        var screens = Screen.AllScreens;
        if (scope == "monitor" && (monitorIndex < 0 || monitorIndex >= screens.Length)) throw new ArgumentException("Invalid monitorIndex");
        var b = scope switch { "primary" => Screen.PrimaryScreen!.Bounds, "desktop" => SystemInformation.VirtualScreen, "monitor" => screens[monitorIndex].Bounds, _ => throw new ArgumentException("scope must be primary, monitor or desktop") };
        if ((long)b.Width * b.Height > 40_000_000) throw new InvalidOperationException("Desktop exceeds capture limit of 40 million pixels.");
        using var bitmap = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
            if (Native.GetCursorPos(out var p)) CursorOverlay.PaintMarker(g, p.X - b.X, p.Y - b.Y);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return [new { type = "text", text = System.Text.Json.JsonSerializer.Serialize(new { bounds = new { x=b.X, y=b.Y, width=b.Width, height=b.Height }, cursor = Position(), cursorComposited = true }) },
            new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(stream.ToArray()) }];
    }
}
