$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$a=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'ExplorerRing-v015.exe'))
$native=$a.GetType('ExplorerRingNative')
$flags=[Reflection.BindingFlags]'Static,NonPublic'
function Invoke-Native($name,[object[]]$values) { $native.GetMethod($name,$flags).Invoke($null,$values) }
$form=New-Object System.Windows.Forms.Form
try {
    $h=$form.Handle
    $thread=Invoke-Native 'GetCurrentThreadId' @()
    if((Invoke-Native 'BeginObservation' @([uint32]$thread)) -ne 0) { throw 'Begin failed; close prior versions before test' }
    if((Invoke-Native 'ConfigureBridge' @([uint32]200,[uint32]14,[uint32]1)) -ne 0) { throw 'Configure failed' }
    $until=[DateTime]::UtcNow.AddSeconds(185)
    while([DateTime]::UtcNow -lt $until) {
        [void](Invoke-Native 'BridgePulse' @([uint32]1))
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
    $eventType=$a.GetType('ExplorerRingNative+Event')
    $items=[Array]::CreateInstance($eventType,128)
    $args=[object[]]@($items,[uint32]128,[uint32]0)
    [void]$native.GetMethod('DrainObservation',$flags).Invoke($null,$args)
    [void](Invoke-Native 'SendMessage' @($h,[uint32]0x2cc,[IntPtr]::Zero,[IntPtr]::Zero))
    $args=[object[]]@($items,[uint32]128,[uint32]0)
    $n=$native.GetMethod('DrainObservation',$flags).Invoke($null,$args)
    if($n -ne 1 -or $args[2] -ne 0) { throw 'Resident lease expired or lost input after 185 seconds' }
    'PASS: resident heartbeat renews bounded native lease beyond 180 seconds; own hidden window only; no Explorer connection.'
} finally { [void](Invoke-Native 'EndObservation' @()); $form.Dispose() }
