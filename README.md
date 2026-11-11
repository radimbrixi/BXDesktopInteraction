# BX Desktop Interaction

Windows mouse control with a screenshot-visible cyan cursor marker, a local stdio MCP server, and a separate experimental KMDF virtual HID driver project. Open `BXDesktopInteraction.slnx` in Visual Studio 2026.

## Current state

The .NET 10 application is the runnable implementation. Its normal mouse tools use Windows `SendInput`, not a kernel driver. It supports smooth absolute moves, left/right/middle/X1/X2 buttons, single/double clicks, multi-step button holds, drag-and-drop input, vertical/horizontal scrolling, pointer position, and full-desktop PNG capture. A drop completes only if the destination application accepts it.

The kernel project is development source, not an installed or validated driver. `bx_hid_report` is an optional low-level transport to it; the standard high-level mouse tools do not silently switch backends. WDK compilation, INF validation, signing, VM installation and hardware-input testing remain required.

## Run

```powershell
dotnet build BXDesktopInteraction.slnx -c Release
dotnet run --project src/BX.DesktopInteraction --no-launch-profile -- --demo
```

The demo displays the cursor marker without controlling input. Exit through the BX notification-area icon. Visual Studio's default launch profile runs this preview. Compatible instances share an emergency-stop hotkey; only the current interaction owner displays its live marker.

For MCP, use the built executable with no arguments. Copy the entry from `mcp.example.json` into your MCP host configuration and set `command` to the absolute path of the built executable in your checkout. Do not run `dotnet run` over MCP stdio: build output can corrupt the protocol. The server uses newline-delimited JSON-RPC, negotiates initialization, and implements ping, tools/list and tools/call. It has printable Unicode text entry, but no HTTP listener, shell execution or DOM integration. It is a minimal implementation, not a complete implementation of every optional MCP capability. Requests execute serially within each connection. In-flight cancellation notifications are not handled; use the emergency hotkey for interruption.

## Multiple instances: interaction baton

Starting a new compatible instance gives it control. Each subsequent input request (move, click, button, drag, scroll, printable text or HID report) automatically takes the baton for its caller. `bx_take_control` explicitly takes it without injecting input. `bx_status`, `bx_mouse_position`, `bx_capture` and `bx_release_buttons` do not steal control.

The superseded instance stays connected and can read status or capture screenshots. Its ongoing input is cancelled at the next input step; it does not resume the old command automatically. A later input request can take control again. Rapidly competing clients will therefore cancel each other's operations: the mechanism intentionally provides one shared controller, not parallel independent mice.

Ownership and each SendInput operation are synchronized through a session-local named mutex and shared memory. On takeover, buttons held through SendInput by the previous owner are released before the new owner can inject input. A stale operation's cleanup cannot release the new owner's buttons. Experimental HID transport retains exclusive driver access; a paused client's timer releases its handle, so an immediate cross-instance HID request can require retrying after that cleanup.

Any compatible instance may hold the single Windows registration for Ctrl+Alt+F12. It stops the entire shared session, including the active owner. If the registering instance exits, surviving instances retry registration automatically (up to 500 ms); input fails closed while the hotkey is unavailable. A registration conflict does **not** latch an emergency stop. Status reports `control.activePid`, `instancePid`, `active`, `paused`, `generation`, `hotkeyReady` and `stopReason` (`emergency_stop` or `hotkey_unavailable`).

All instances must use version 0.2.0 or later for this coordination. Close old 0.1.0 instances once during upgrade: they do not participate in shared ownership and can occupy the hotkey independently. Already running processes do not load a newly built binary automatically; reconnect the extension in each MCP host after upgrading.

## Tools and coordinates

| Tool | Arguments |
| --- | --- |
| `bx_status` | none; returns backend, bounds, position, shared stop and ownership state |
| `bx_take_control` | none; explicitly claims the interaction baton without clearing emergency stop |
| `bx_type_text` | `text`: 1–1000 printable Unicode characters; no Enter/control keys; stops on foreground-window change |
| `bx_capture` | optional `scope`: `primary` (default), `monitor` with `monitorIndex`, or `desktop`; PNG plus bounds and pointer metadata; limit 40 million pixels |
| `bx_mouse_position` | none |
| `bx_mouse_move` | `x`, `y`, optional `durationMs` (0–10000) |
| `bx_mouse_click` | optional `button`, `count` (1 or 2) |
| `bx_mouse_button` | `action` (`down`/`up`), optional `button` |
| `bx_mouse_drag` | `fromX`, `fromY`, `toX`, `toY`, optional `durationMs`, `button` |
| `bx_mouse_scroll` | `delta`, optional `axis` (`vertical`/`horizontal`) |
| `bx_release_buttons` | none |
| `bx_hid_report` | optional `dx`, `dy`, `buttons`, `wheel`, `horizontalWheel`; requires `--enable-hid` |

Coordinates are physical pixels in the complete Windows virtual desktop; monitors left/above the primary can have negative origins. Screenshot pixel `(u,v)` corresponds to desktop `(bounds.x+u,bounds.y+v)`. Wheel delta is 120 per notch for SendInput. Positive vertical means up; positive horizontal means right. HID wheel fields use signed notches, not SendInput units.

## What the cursor actually is

Windows normally has one shared system pointer. A physical mouse and a virtual HID mouse both influence it: this implementation does not create independent OS pointers or allow simultaneous independent users. Movement can conflict with physical input.

The driver reports mouse input; it does not draw pixels. A topmost, nonactivating, click-through user-mode overlay draws the cyan BX arrow at the actual pointer position, underneath the system cursor. It follows at approximately 60 Hz and can briefly lag fast motion. External screenshot capture may include this overlay depending on the capture API and selected window; that is not universally guaranteed. `bx_capture` explicitly composites the marker at the pointer location so its PNG includes it even if the capture API omits layered windows. Some native system-cursor pixels may also appear depending on capture behavior. Protected content and the secure desktop are not supported. No browser or target-application integration is implied.

## Stop and scope

Ctrl+Alt+F12 latches a session-wide emergency stop and releases held buttons. Taking the baton or starting another instance never clears it. Close **all compatible instances** and start a fresh session to reset it. Holding Escape cancels input. Graceful disconnect releases the owner's held buttons without releasing another owner's input. A surviving peer releases tracked SendInput buttons when it next claims ownership after an abrupt owner exit; immediate release on a crash is not guaranteed. The optional HID driver sends a zero-button report when its client handle is cleaned up. No action crosses into secure desktop intentionally. SendInput remains subject to Windows integrity restrictions; BX does not elevate itself. MCP host consent and application-access restrictions must still be respected; this project is not a way to bypass a refusal by another tool.

`bx_capture` exposes everything visible on the desktop to the connected MCP client. The executable does not persist screenshots or conversation history. Only connect a local client you trust to control the desktop.

## Driver development

Open `driver/BxVirtualMouse/BxVirtualMouse.vcxproj` separately in VS2026 with compatible C++ driver tooling, SDK and WDK. The current workstation has Windows SDK headers, but matching KMDF/VHF build tooling was not found during initial setup. The driver is excluded from the main solution so the usable .NET project builds independently.

The driver is a root-enumerated KMDF HID source using Microsoft's VHF lower filter. It exposes `\\.\BxVirtualMouse` with exclusive access limited to SYSTEM/Administrators. A buffered, write-access IOCTL accepts exactly eight bytes: report ID 1, button bitmask, little-endian signed X/Y, signed vertical/horizontal wheel. Buttons: left=1, right=2, middle=4, X1=8, X2=16. It rejects out-of-descriptor values. Pointer acceleration applies to relative HID movement; it is not an exact absolute-position backend. The source includes an INF and VHF descriptor; no hook into an existing physical-mouse driver is used.

Use an isolated driver-development VM for initial verification and Driver Verifier. Obtain a properly signed package through the supported Windows driver-signing process before host installation. No installer, certificate changes, test-signing changes, automatic elevation or host installation is performed here. Prototype VID/PID values must not be treated as assigned production identifiers.

## Checks

```powershell
./scripts/Smoke-Test.ps1
./scripts/Smoke-Test.ps1 -Capture
./scripts/Test-MultiInstance.ps1
./src/BX.DesktopInteraction/bin/Release/net10.0-windows/BX.DesktopInteraction.exe --self-test
./src/BX.DesktopInteraction/bin/Release/net10.0-windows/BX.DesktopInteraction.exe --coordination-self-test
```

The smoke test checks the real stdio server, discovery, status, invalid request arguments, opt-in protection and optionally a PNG capture without saving private desktop content. `--self-test` opens BX's own foreground test surface, verifies movement, single/double clicks, drag and vertical wheel events, restores the original pointer position and closes. Do not move the physical mouse while it runs. These checks do not prove arbitrary target-application drop acceptance. Kernel-driver verification remains separate.

`--coordination-self-test` uses separate child processes and isolated named objects. It checks startup takeover, pause/reclaim, stale-lease rejection, stale button cleanup, non-stealing read-only status, hotkey registrar departure/recovery, and emergency-stop persistence across takeover and startup. It does not register a real hotkey or inject desktop input.

## References

- [Microsoft SendInput and UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [Microsoft Virtual HID Framework](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/virtual-hid-framework--vhf-)
- [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
