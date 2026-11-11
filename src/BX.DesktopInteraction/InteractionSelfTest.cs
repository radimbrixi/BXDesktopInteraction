namespace BX.DesktopInteraction;

// A deliberate foreground integration test. It injects only into its own test surface.
internal static class InteractionSelfTest
{
    internal static int Run()
    {
        using var form = new Form { Text = "BX mouse integration test — do not move the mouse", Width = 600, Height = 400, StartPosition = FormStartPosition.CenterScreen, BackColor = Color.LightCyan };
        using var overlay = new CursorOverlay();
        var mouse = new MouseController(overlay);
        overlay.EmergencyStop = mouse.ReleaseAll;
        int downs = 0, ups = 0, doubles = 0, wheels = 0, result = 1;
        form.MouseDown += (_, _) => Interlocked.Increment(ref downs);
        form.MouseUp += (_, _) => Interlocked.Increment(ref ups);
        form.MouseDoubleClick += (_, _) => Interlocked.Increment(ref doubles);
        form.MouseWheel += (_, _) => Interlocked.Increment(ref wheels);
        form.Shown += (_, _) =>
        {
            overlay.Show();
            form.Activate();
            var point = form.PointToScreen(new Point(180, 140));
            var expected = form.Handle;
            Native.GetCursorPos(out var original);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    void OwnForeground() { if (Native.GetForegroundWindow() != expected) throw new InvalidOperationException("Test window lost foreground; refusing input."); }
                    OwnForeground();
                    await mouse.Move(point.X, point.Y, 150);
                    OwnForeground(); await mouse.Click("left", 1);
                    await Task.Delay((int)Native.GetDoubleClickTime() + 100);
                    OwnForeground(); await mouse.Click("left", 2);
                    OwnForeground(); await mouse.Drag(point.X, point.Y, point.X + 80, point.Y + 40, 250, "left");
                    OwnForeground(); mouse.Scroll(120, false);
                    await Task.Delay(200);
                    Native.GetCursorPos(out var actual);
                    if (Math.Abs(actual.X - point.X - 80) > 1 || Math.Abs(actual.Y - point.Y - 40) > 1) throw new InvalidOperationException("Final mouse position mismatch.");
                    if (Volatile.Read(ref downs) < 4 || Volatile.Read(ref ups) < 4 || Volatile.Read(ref doubles) < 1 || Volatile.Read(ref wheels) < 1)
                        throw new InvalidOperationException($"Mouse events incomplete: down={downs},up={ups},double={doubles},wheel={wheels}");
                    Console.WriteLine($"PASS: real move, single/double click, drag and vertical wheel on BX's own window. down={downs},up={ups},double={doubles},wheel={wheels}");
                    result = 0;
                }
                catch (Exception e) { Console.Error.WriteLine(e); }
                finally
                {
                    mouse.ReleaseAll();
                    try { await mouse.Move(original.X, original.Y, 0); } catch { }
                    form.BeginInvoke(form.Close);
                }
            });
        };
        Application.Run(form);
        return result;
    }
}
