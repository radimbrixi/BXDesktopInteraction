param([string]$Executable = "$PSScriptRoot\..\src\BX.DesktopInteraction\bin\Release\net10.0-windows\BX.DesktopInteraction.exe", [switch]$Capture)
$ErrorActionPreference = 'Stop'
$start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $Executable).Path)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$stderr = $process.StandardError.ReadToEndAsync()
function Request($id, $method, $params) {
    $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$id;method=$method;params=$params} | ConvertTo-Json -Depth 8 -Compress))
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(15000)) { throw "Timed out: $method" }
    if (-not $read.Result) { throw 'Server exited without response' }
    $response = $read.Result | ConvertFrom-Json
    if ($response.id -ne $id) { throw 'Response ID mismatch' }
    if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
    return $response.result
}
try {
    $init = Request 1 initialize @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='bx-smoke';version='1'}}
    if ($init.serverInfo.name -ne 'bx-desktop-interaction') { throw 'Wrong server' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $tools = Request 2 tools/list @{}
    if ($tools.tools.Count -ne 12) { throw 'Expected twelve mouse/text/capture/control tools' }
    $status = Request 3 tools/call @{name='bx_status';arguments=@{}}
    $details = $status.content[0].text | ConvertFrom-Json
    if ($details.backend -ne 'SendInput') { throw 'Unexpected backend' }
    if ($details.stopped) { throw 'A session-wide emergency stop is latched' }
    if (-not $details.control.hotkeyReady) { Write-Output 'Emergency hotkey is unavailable; input is disabled, read-only MCP checks remain available.' }
    $bad = Request 4 tools/call @{name='bx_mouse_move';arguments=@{x=100000;y=100000;durationMs=0}}
    if (-not $bad.isError) { throw 'Out-of-bounds move accepted' }
    $bad = Request 5 tools/call @{name='bx_mouse_click';arguments=@{count=3}}
    if (-not $bad.isError) { throw 'Invalid click count accepted' }
    $bad = Request 6 tools/call @{name='bx_hid_report';arguments=@{}}
    if (-not $bad.isError) { throw 'Unenabled HID transport accepted' }
    if ($Capture) {
        $shot = Request 7 tools/call @{name='bx_capture';arguments=@{}}
        if ($shot.isError) { throw "Capture failed: $($shot.content[0].text)" }
        if ($shot.content[1].mimeType -ne 'image/png') { throw 'Capture did not return PNG content' }
        $bytes = [Convert]::FromBase64String($shot.content[1].data)
        if ($bytes.Length -lt 100 -or $bytes[0] -ne 137 -or $bytes[1] -ne 80) { throw 'Invalid PNG' }
        Write-Output "Desktop PNG verified ($($bytes.Length) bytes); image not saved."
    }
    Write-Output 'PASS: initialize, notification, tool discovery, desktop status, invalid-input rejection and HID opt-in guard.'
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill() }
    $diagnostics = $stderr.GetAwaiter().GetResult()
    if ($diagnostics) { Write-Output $diagnostics.Trim() }
    if ($process.ExitCode -ne 0) { throw "Server exit code $($process.ExitCode)" }
    $process.Dispose()
}
