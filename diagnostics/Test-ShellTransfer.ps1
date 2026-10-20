$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
# Run ONLY in the isolated SSH/noninteractive Windows session. Never replace
# the clipboard in the user's console/RDP session for an automated test.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SessionGuard {
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
}
'@
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
if($session -ne 0 -or $session -eq [SessionGuard]::WTSGetActiveConsoleSessionId()) { throw 'Requires isolated session 0, not the interactive console.' }
$existing=[Windows.Forms.Clipboard]::GetDataObject()
if($existing -ne $null -and $existing.GetFormats().Length -ne 0) { throw 'Test session clipboard is not empty; refusing to replace it.' }
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'ExplorerRing-v091.exe'))
$type=$assembly.GetType('ShellCommands',$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$constructor=$type.GetConstructor($flags,$null,[type[]]@([string],[bool],[IntPtr]),$null)
$execute=$type.GetMethod('Execute',$flags,$null,[type[]]@([string]),$null)
function Command($path,$verb) {
    $commands=$constructor.Invoke(@([string]$path,$false,[IntPtr]::Zero))
    try {
        if(-not $type.GetMethod('Has',$flags).Invoke($commands,@($verb))) { throw "Command unavailable: $verb" }
        $execute.Invoke($commands,@($verb)) | Out-Null
    } finally { $commands.Dispose() }
}
$dir=Join-Path ([IO.Path]::GetTempPath()) ('Ring-menu-transfer-test-'+[Guid]::NewGuid().ToString('N'))
$made=[Collections.Generic.List[string]]::new()
try {
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $source=Join-Path $dir 'source'; $copy=Join-Path $dir 'copy'; $move=Join-Path $dir 'move'
    foreach($p in @($source,$copy,$move)) { [IO.Directory]::CreateDirectory($p) | Out-Null }
    $file=Join-Path $source 'sample.txt'
    [IO.File]::WriteAllText($file,'isolated-ring-transfer-test')
    $made.Add($file)
    Command $file 'copy'
    Command $copy 'paste'
    $copied=Join-Path $copy 'sample.txt'; $made.Add($copied)
    if(-not [IO.File]::Exists($copied) -or [IO.File]::ReadAllText($copied) -ne 'isolated-ring-transfer-test' -or -not [IO.File]::Exists($file)) { throw 'Copy/paste verification failed' }
    Command $copied 'cut'
    Command $move 'paste'
    $moved=Join-Path $move 'sample.txt'; $made.Add($moved)
    if(-not [IO.File]::Exists($moved) -or [IO.File]::ReadAllText($moved) -ne 'isolated-ring-transfer-test' -or [IO.File]::Exists($copied)) { throw 'Cut/paste verification failed' }
    Write-Output 'PASS: isolated session 0; Shell copy/paste content and source preserved; cut/paste content and source removed; interactive clipboard untouched.'
} finally {
    [Windows.Forms.Clipboard]::Clear()
    foreach($p in $made) { if([IO.File]::Exists($p)) { [IO.File]::Delete($p) } }
    foreach($p in @($source,$copy,$move,$dir)) { if($p -and [IO.Directory]::Exists($p)) { [IO.Directory]::Delete($p) } }
}
