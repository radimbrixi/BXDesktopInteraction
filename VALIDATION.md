# Initial validation — 2026-10-23

## Multi-instance update — 2026-10-24, version 0.2.0

- Release solution build passed with zero warnings and errors. Updated executable is at the existing `src/BX.DesktopInteraction/bin/Release/net10.0-windows/BX.DesktopInteraction.exe` path.
- Isolated multi-process coordination test passed startup takeover, old-owner pause and stale-lease rejection, explicit reclaim, non-stealing status, stale button-cleanup protection, registrar departure/recovery, session-wide stop propagation, stop persistence through new instances and claims, and reset only after the last shared mapping closes. This test injected no desktop input.
- Real stdio MCP smoke test passed twelve-tool discovery, initialization, status, invalid-input rejection and disabled HID guard on the Windows desktop.
- Own-window mouse integration test passed movement, single/double clicks, drag and wheel; down=4, up=4, double=1, wheel=1. Pointer restored.
- `scripts/Test-MultiInstance.ps1` passed two simultaneously connected real MCP servers: latest-started takeover, paused peer, explicit baton return, shared Windows emergency hotkey, hotkey registrar exit/recovery and graceful shutdown. This check injected no mouse/keyboard input.
- Existing Goose/other host sessions must reconnect once to load the new executable. Old 0.1.0 processes cannot participate in the new shared protocol. No Goose conversation or CAPTCHA interaction was resumed by the tests.
- Experimental HID driver handoff was not hardware-tested; its exclusive handle can need a retry while the paused client's timer closes it. The verified high-level backend remains SendInput.

## Original baseline

Environment: Windows desktop, .NET SDK 10.0.402. Build target net10.0-windows.

- Release solution build succeeded with zero warnings and zero errors.
- Stdio smoke test passed initialization/negotiation, initialized notification, ten-tool discovery, actual desktop/backend status, invalid move rejection, invalid click-count rejection and disabled HID-transport rejection.
- Primary monitor PNG capture passed signature/content checks (10,001,903 bytes in the observed run). No desktop image was saved.
- Foreground integration test on BX's own test form passed real motion, single click, double click, drag and vertical wheel. Observed event counts: 4 down, 4 up, 1 double-click, 1 wheel. Pointer restored and test process exited normally.
- Combined desktop exceeded the 40-million-pixel capture limit; capture now defaults to primary monitor. Full desktop capture remains explicit and bounded.
- Windows SDK include versions present: 10.0.10240.0, 10.0.22621.0, 10.0.26100.0, 10.0.28000.0. No km/ntddk.h or km/vhf.h found in these SDKs. Kernel driver build, INF validation, signing, installation and runtime verification were not performed.

Not established: exact cursor inclusion by every third-party capture API; arbitrary app drag/drop acceptance; elevated/secure desktop operation; independent pointers; horizontal wheel and side-button event delivery in arbitrary target apps; kernel-driver correctness. No MCP host configuration was changed and no kernel component was installed.
