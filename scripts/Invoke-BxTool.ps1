param([string]$Tool, [string]$ArgumentsJson='{}', [string]$ImagePath, [string]$ActionsJson)
$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot '..\src\BX.DesktopInteraction\bin\Release\net10.0-windows\BX.DesktopInteraction.exe'
$start=[Diagnostics.ProcessStartInfo]::new((Resolve-Path $exe).Path)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
$stderr=$process.StandardError.ReadToEndAsync()
function Rpc($id,$method,$parameters){
    $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$id;method=$method;params=$parameters}|ConvertTo-Json -Depth 12 -Compress))
    $read=$process.StandardOutput.ReadLineAsync()
    if(-not $read.Wait(30000)){throw 'BX response timeout'}
    $r=$read.Result|ConvertFrom-Json
    if($r.error){throw ($r.error|ConvertTo-Json -Compress)}
    return $r.result
}
try {
    $null=Rpc 1 initialize @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='bx-local-client';version='0.1'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $actions=if($ActionsJson){$ActionsJson|ConvertFrom-Json -AsHashtable}else{@(@{name=$Tool;arguments=($ArgumentsJson|ConvertFrom-Json -AsHashtable)})}
    $requestId=1
    foreach($action in $actions){
    $result=Rpc (++$requestId) tools/call $action
    if($result.isError){throw $result.content[0].text}
    foreach($content in $result.content){
        if($content.type -eq 'text'){Write-Output $content.text}
        elseif($content.type -eq 'image' -and $ImagePath){
            [IO.File]::WriteAllBytes($ImagePath,[Convert]::FromBase64String($content.data))
            Write-Output "PNG: $ImagePath"
        }
    }
    }
} finally {
    $process.StandardInput.Close()
    if(-not $process.WaitForExit(5000)){$process.Kill()}
    $errorOutput=$stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0){Write-Warning $errorOutput}
    $process.Dispose()
}
