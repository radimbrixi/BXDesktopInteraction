using System.Diagnostics;
using System.Text.Json;

namespace BX.DesktopInteraction;

// Isolated processes and named objects; never registers a hotkey or injects desktop input.
internal static class CoordinationSelfTest
{
    internal static void Worker(string name)
    {
        using var control = new SharedControl(name);
        Console.WriteLine(JsonSerializer.Serialize(control.Status()));
        string? command;
        while ((command = Console.ReadLine()) is not null)
        {
            try
            {
                if (command == "claim") control.Claim();
                if (command == "ready") control.HotkeyRegistered();
                if (command == "stop") control.Stop();
                if (command == "check") control.Execute(() => { });
                if (command == "hold-simulated") control.Execute(() => control.SetButton(1, true));
                if (command == "clear-simulated") control.Execute(() => control.SetButton(1, false));
                if (command == "stale-release") control.ReleaseButton(1, () => throw new Exception("Stale cleanup touched the new owner's button."));
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, state = control.Status() }));
            }
            catch (Exception e) { Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = e.Message, state = control.Status() })); }
        }
    }
    private sealed class Client : IDisposable
    {
        internal readonly Process Process;
        internal JsonElement Initial;
        internal Client(string name)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--coordination-worker"); start.ArgumentList.Add(name);
            Process = Process.Start(start)!;
            Initial = Read();
        }
        private JsonElement Read()
        {
            var pending = Process.StandardOutput.ReadLineAsync();
            if (!pending.Wait(10000)) throw new TimeoutException("Coordination worker did not respond.");
            return JsonDocument.Parse(pending.Result ?? throw new Exception("Worker exited.")).RootElement.Clone();
        }
        internal JsonElement Call(string command) { Process.StandardInput.WriteLine(command); return Read(); }
        public void Dispose() { Process.StandardInput.Close(); if (!Process.WaitForExit(5000)) Process.Kill(); Process.Dispose(); }
    }
    internal static int Run()
    {
        try
        {
            string name = @"Local\BXDesktopInteraction.Test." + Guid.NewGuid().ToString("N");
            void Require(bool value, string message) { if (!value) throw new Exception(message); }
            bool Flag(JsonElement r, string key) => r.GetProperty("state").GetProperty(key).GetBoolean();
            using (var a = new Client(name))
            {
                Require(a.Initial.GetProperty("active").GetBoolean(), "First instance is not active.");
                var unavailable = a.Call("check");
                Require(!unavailable.GetProperty("ok").GetBoolean() && !Flag(unavailable,"stopped"), "Unavailable hotkey incorrectly latches emergency stop.");
                a.Call("ready");
                Require(a.Call("check").GetProperty("ok").GetBoolean(), "First owner cannot execute.");
                using (var b = new Client(name))
                {
                    Require(b.Initial.GetProperty("active").GetBoolean() && b.Initial.GetProperty("hotkeyReady").GetBoolean(), "New instance did not take control/share hotkey readiness.");
                    Require(Flag(a.Call("status"), "paused"), "Old instance did not pause.");
                    Require(!a.Call("check").GetProperty("ok").GetBoolean(), "Old lease still permits input.");
                    Require(b.Call("check").GetProperty("ok").GetBoolean(), "Read-only status stole ownership.");
                    b.Call("hold-simulated");
                    Require(a.Call("stale-release").GetProperty("ok").GetBoolean(), "Old cleanup can release the new owner's button.");
                    b.Call("clear-simulated");
                    a.Call("claim");
                    Require(Flag(b.Call("status"), "paused"), "Baton return did not pause second instance.");
                    Require(a.Call("check").GetProperty("ok").GetBoolean(), "Original instance cannot reclaim.");
                    b.Call("stop");
                    Require(Flag(a.Call("status"), "stopped"), "Emergency stop did not propagate across processes.");
                    Require(!a.Call("check").GetProperty("ok").GetBoolean(), "Stopped owner still executes.");
                    a.Call("claim"); b.Call("claim");
                    Require(Flag(a.Call("status"), "stopped") && Flag(b.Call("status"), "stopped"), "Claim cleared emergency stop.");
                    using var c = new Client(name);
                    Require(c.Initial.GetProperty("stopped").GetBoolean(), "New instance cleared emergency stop.");
                }
                Require(Flag(a.Call("status"), "stopped"), "Closing peer cleared emergency stop.");
            }
            using (var fresh = new Client(name))
                Require(!fresh.Initial.GetProperty("stopped").GetBoolean(), "Fresh session retained closed mapping.");
            var registrar = new Client(name);
            registrar.Call("ready");
            using (var peer = new Client(name))
            {
                registrar.Dispose();
                Require(!Flag(peer.Call("status"), "hotkeyReady"), "Closed registrar is still marked ready.");
                Require(!peer.Call("check").GetProperty("ok").GetBoolean(), "Input accepted without any hotkey registrar.");
                peer.Call("ready");
                Require(peer.Call("check").GetProperty("ok").GetBoolean(), "Peer could not take over hotkey readiness.");
            }
            Console.WriteLine("PASS: multi-process startup takeover, pause, explicit reclaim, non-stealing status, shared hotkey readiness, distinct hotkey error, global emergency-stop persistence, fresh-session reset. No desktop input injected.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
