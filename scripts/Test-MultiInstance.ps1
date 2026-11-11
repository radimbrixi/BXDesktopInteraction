param([string]$Executable="$PSScriptRoot\..\src\BX.DesktopInteraction\bin\Release\net10.0-windows\BX.DesktopInteraction.exe")
$ErrorActionPreference='Stop'
function Request($client,$id,$method,$parameters){
 $client.StandardInput.WriteLine((@{jsonrpc='2.0';id=$id;method=$method;params=$parameters}|ConvertTo-Json -Depth 8 -Compress))
 $pending=$client.StandardOutput.ReadLineAsync()
 if(-not $pending.Wait(10000)){throw 'MCP response timeout'}
 $reply=$pending.Result|ConvertFrom-Json
 if($reply.error){throw ($reply.error|ConvertTo-Json -Compress)}
 return $reply.result
}
function Start-Client {
 $start=[Diagnostics.ProcessStartInfo]::new((Resolve-Path $Executable).Path)
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $client=[Diagnostics.Process]::Start($start)
 $null=Request $client 1 initialize @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='multi-instance-check';version='1'}}
 $client.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
 return $client
}
function Read-Control($client,$tool='bx_status'){
 $reply=Request $client 2 tools/call @{name=$tool;arguments=@{}}
 if($reply.isError){throw $reply.content[0].text}
 return ($reply.content[0].text|ConvertFrom-Json).control
}
function Close-Client($client){
 if($null -eq $client){return}
 $client.StandardInput.Close()
 if(-not $client.WaitForExit(5000)){$client.Kill();throw 'MCP process failed graceful shutdown'}
 if($client.ExitCode -ne 0){throw $client.StandardError.ReadToEnd()}
 $client.Dispose()
}
$first=$null;$second=$null
try {
 $first=Start-Client
 $one=Read-Control $first
 if(-not $one.active -or -not $one.hotkeyReady){throw 'First desktop client did not become active with hotkey ready'}
 $second=Start-Client
 $two=Read-Control $second
 if(-not $two.active -or -not $two.hotkeyReady -or $two.stopped){throw 'Second client did not share the emergency hotkey and take control'}
 if(-not (Read-Control $first).paused){throw 'First client did not pause'}
 $one=Read-Control $first 'bx_take_control'
 if(-not $one.active -or -not (Read-Control $second).paused){throw 'Explicit baton return failed'}
 Close-Client $first;$first=$null
 $timer=[Diagnostics.Stopwatch]::StartNew()
 do {
  $two=Read-Control $second
  if($two.hotkeyReady){break}
  Start-Sleep -Milliseconds 100
 } while($timer.ElapsedMilliseconds -lt 3000)
 if(-not $two.hotkeyReady){throw 'Survivor did not register emergency hotkey after registrar shutdown'}
 if(-not (Read-Control $second 'bx_take_control').active){throw 'Survivor cannot claim vacant baton'}
 Write-Output 'PASS: two live stdio MCP clients, automatic takeover, paused peer, explicit return, shared Windows hotkey, registrar exit/recovery and graceful shutdown. No mouse/keyboard input injected.'
} finally {
 Close-Client $first
 Close-Client $second
}
