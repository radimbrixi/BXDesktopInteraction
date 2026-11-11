using System.Text;
using System.Text.Json;

namespace BX.DesktopInteraction;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--coordination-self-test")) { Environment.ExitCode = CoordinationSelfTest.Run(); return; }
        if (args.Length == 2 && args[0] == "--coordination-worker") { CoordinationSelfTest.Worker(args[1]); return; }
        if (args.Contains("--self-test")) { Environment.ExitCode = InteractionSelfTest.Run(); return; }
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        using var overlay = new CursorOverlay();
        var mouse = new MouseController(overlay, args.Contains("--enable-hid"));
        overlay.EmergencyStop = mouse.ReleaseAll;
        Application.ApplicationExit += (_, _) => mouse.ReleaseAll();
        Console.CancelKeyPress += (_, _) => mouse.ReleaseAll();
        overlay.Shown += (_, _) => _ = Task.Run(async () =>
        {
            try { if (args.Contains("--demo")) await Task.Delay(Timeout.Infinite); else await Serve(mouse); }
            finally { mouse.ReleaseAll(); if (!overlay.IsDisposed) overlay.BeginInvoke(overlay.Close); }
        });
        Console.Error.WriteLine("BX Desktop Interaction: local stdio MCP; Ctrl+Alt+F12 emergency stop; Escape cancels motion.");
        Application.Run(overlay);
    }
    private static object Text(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } };
    private static async Task Serve(MouseController mouse)
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            JsonElement id = default;
            try
            {
                if (line.Length > 65536) throw new ArgumentException("Request exceeds 64 KiB.");
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement;
                bool hasId = request.TryGetProperty("id", out id);
                if (hasId) id = id.Clone();
                var method = request.GetProperty("method").GetString();
                if (!hasId) continue;
                object result;
                if (method == "initialize")
                {
                    string? requested = request.GetProperty("params").GetProperty("protocolVersion").GetString();
                    string negotiated = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? requested : "2025-06-18";
                    result = new { protocolVersion = negotiated, capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "bx-desktop-interaction", version = "0.2.0" }, instructions = "Local Windows mouse control. One shared system pointer. Coordinates are physical virtual-desktop pixels. Capture again after layout changes. Ctrl+Alt+F12 latches emergency stop. Newest instance takes control. Input requests automatically reclaim the baton; bx_take_control does so explicitly. Status and capture do not take control. Superseded input cancels. Ctrl+Alt+F12 stops all compatible instances and is never cleared by takeover. Mouse control and printable text entry; text entry does not press Enter." };
                }
                else if (method == "ping") result = new { };
                else if (method == "tools/list") result = new { tools = Tools() };
                else if (method == "tools/call")
                {
                    try
                    {
                        var p = request.GetProperty("params");
                        var a = p.TryGetProperty("arguments", out var arg) ? arg : JsonSerializer.SerializeToElement(new { });
                        string name = p.GetProperty("name").GetString()!;
                        int I(string key, int? fallback = null) => a.TryGetProperty(key, out var v) ? v.GetInt32() : fallback ?? throw new ArgumentException($"Missing {key}");
                        string S(string key, string fallback) => a.TryGetProperty(key, out var v) ? v.GetString() ?? throw new ArgumentException(key) : fallback;
                        if (name is "bx_take_control" or "bx_type_text" or "bx_mouse_move" or "bx_mouse_click" or "bx_mouse_button" or "bx_mouse_drag" or "bx_mouse_scroll" or "bx_hid_report")
                            mouse.TakeControl();
                        switch (name)
                        {
                            case "bx_take_control": result = Text(mouse.Status()); break;
                            case "bx_status": result = Text(mouse.Status()); break;
                            case "bx_type_text": string text = S("text", ""); mouse.TypeText(text); result = Text(new { typedCharacters = text.Length }); break;
                            case "bx_capture": result = new { content = mouse.Capture(S("scope", "primary"), I("monitorIndex",0)) }; break;
                            case "bx_mouse_position": result = Text(MouseController.Position()); break;
                            case "bx_mouse_move": await mouse.Move(I("x"), I("y"), I("durationMs", 250)); result = Text(MouseController.Position()); break;
                            case "bx_mouse_click": await mouse.Click(S("button", "left"), I("count", 1)); result = Text(MouseController.Position()); break;
                            case "bx_mouse_button":
                                string action = S("action", ""); if (action is not ("down" or "up")) throw new ArgumentException("action must be down or up");
                                mouse.Button(S("button", "left"), action == "down"); result = Text(new { action }); break;
                            case "bx_mouse_drag": await mouse.Drag(I("fromX"), I("fromY"), I("toX"), I("toY"), I("durationMs", 600), S("button", "left")); result = Text(MouseController.Position()); break;
                            case "bx_mouse_scroll": string axis = S("axis", "vertical"); if (axis is not ("vertical" or "horizontal")) throw new ArgumentException("Invalid axis"); mouse.Scroll(I("delta"), axis == "horizontal"); result = Text(new { scrolled = true }); break;
                            case "bx_release_buttons": mouse.ReleaseAll(); result = Text(new { released = true }); break;
                            case "bx_hid_report": mouse.HidReport(I("dx",0),I("dy",0),I("buttons",0),I("wheel",0),I("horizontalWheel",0)); result = Text(MouseController.Position()); break;
                            default: throw new ArgumentException("Unknown BX tool.");
                        }
                    }
                    catch (Exception e) { result = new { isError = true, content = new[] { new { type = "text", text = e.Message } } }; }
                }
                else { await Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } }); continue; }
                await Write(new { jsonrpc = "2.0", id, result });
            }
            catch (Exception e)
            {
                await Write(new { jsonrpc = "2.0", id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : id, error = new { code = e is JsonException ? -32700 : -32600, message = e.Message } });
            }
        }
    }
    private static Task Write(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
    private static object[] Tools()
    {
        object Int(int min, int max) => new { type = "integer", minimum = min, maximum = max };
        object Enum(params string[] values) => new { type = "string", @enum = values };
        var buttons = Enum("left", "right", "middle", "x1", "x2");
        object Tool(string name, string description, Dictionary<string, object>? properties = null, string[]? required = null) => new
        {
            name, description, inputSchema = new { type = "object", properties = properties ?? [], required = required ?? [], additionalProperties = false }
        };
        return [
            Tool("bx_status", "Read backend, desktop geometry and emergency-stop status."),
            Tool("bx_take_control", "Take the interaction baton from another instance, pausing its in-flight input. Does not reset a session-wide emergency stop. Other input tools take the baton automatically; status and capture do not."),
            Tool("bx_type_text", "Type printable Unicode text into the focused control. Does not press Enter or other control keys. Stops if foreground window changes.", new() { ["text"] = new { type = "string", minLength = 1, maxLength = 1000 } }, ["text"]),
            Tool("bx_capture", "Capture primary monitor (default), a monitor by monitorIndex from bx_status, or desktop as PNG, explicitly compositing the cyan BX cursor if in frame. Image origin is returned bounds x/y. Maximum 40 million pixels.", new() { ["scope"] = Enum("primary","monitor","desktop"), ["monitorIndex"] = Int(0,63) }),
            Tool("bx_mouse_position", "Read the actual shared Windows pointer position."),
            Tool("bx_mouse_move", "Move the shared pointer smoothly to physical desktop coordinates.", new() { ["x"] = Int(-100000,100000), ["y"] = Int(-100000,100000), ["durationMs"] = Int(0,10000) }, ["x","y"]),
            Tool("bx_mouse_click", "Click at current pointer position. count=2 performs a double click.", new() { ["button"] = buttons, ["count"] = Int(1,2) }),
            Tool("bx_mouse_button", "Press or release a button for a multi-step drag. Always release after down.", new() { ["button"] = buttons, ["action"] = Enum("down","up") }, ["action"]),
            Tool("bx_mouse_drag", "Move to start, hold button, move to destination, release even on cancellation.", new() { ["fromX"] = Int(-100000,100000), ["fromY"] = Int(-100000,100000), ["toX"] = Int(-100000,100000), ["toY"] = Int(-100000,100000), ["durationMs"] = Int(0,10000), ["button"] = buttons }, ["fromX","fromY","toX","toY"]),
            Tool("bx_mouse_scroll", "Scroll at pointer: 120 units per wheel notch; positive vertical is up, positive horizontal is right.", new() { ["delta"] = Int(-12000,12000), ["axis"] = Enum("vertical","horizontal") }, ["delta"]),
            Tool("bx_release_buttons", "Release all mouse buttons held by BX."),
            Tool("bx_hid_report", "Optional development virtual-HID transport. Requires --enable-hid and installed signed driver with authorized device access. Relative motion is subject to Windows pointer acceleration. Buttons bitmask is the complete held state: left=1,right=2,middle=4,x1=8,x2=16. Wheels are notches.", new() { ["dx"] = Int(-32767,32767), ["dy"] = Int(-32767,32767), ["buttons"] = Int(0,31), ["wheel"] = Int(-127,127), ["horizontalWheel"] = Int(-127,127) })
        ];
    }
}
