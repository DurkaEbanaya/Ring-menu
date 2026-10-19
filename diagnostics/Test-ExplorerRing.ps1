$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'ExplorerRing-v04.exe'))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$static = [Reflection.BindingFlags]'Static,NonPublic'
$type = $assembly.GetType('RingLab', $true)
$native = $assembly.GetType('ExplorerRingNative', $true)
$event = $assembly.GetType('ExplorerRingNative+Event', $true)
function Assert($ok,$message) { if (-not $ok) { throw $message } }
function Set-Field($name,$value) { $type.GetField($name,$flags).SetValue($form,$value) }
function Get-Field($name) { $type.GetField($name,$flags).GetValue($form) }
function Invoke-Method($name,[object[]]$values) { $type.GetMethod($name,$flags).Invoke($form,$values) }
$form = $type.GetMethod('CreateOverlay',$static).Invoke($null,@())
try {
    Assert ([Runtime.InteropServices.Marshal]::SizeOf([Activator]::CreateInstance($event)) -eq 72) 'Event ABI mismatch'
    Assert (-not $form.Visible) 'Overlay unexpectedly visible'
    Assert (-not (Get-Field 'timer').Enabled) 'Overlay hold timer must be disabled'
    Assert (Get-Field 'integrated') 'Not integration build'
    $form.ClientSize = [Drawing.Size]::new(3000,1920)
    Set-Field 'scale' ([single]2)
    Set-Field 'radius' ([single]296)
    Set-Field 'inner' ([single]116)
    Set-Field 'margin' ([single]28)
    Set-Field 'guard' ([single]52)
    $cases=0
    foreach($x in @(1,68,324,1500,2676,2932,2999)) {
        foreach($y in @(1,96,324,960,1596,1824,1919)) {
            Set-Field 'anchor' ([Drawing.PointF]::new($x,$y))
            Invoke-Method 'OpenRing' @() | Out-Null
            $c=Get-Field 'center'
            Assert (Get-Field 'visibleRing') 'Ring did not open'
            Assert ($c.X -ge 324 -and $c.X -le 2676 -and $c.Y -ge 324 -and $c.Y -le 1596) 'Clipping'
            if(Get-Field 'fixedMode') {
                Assert ([math]::Sqrt([math]::Pow($c.X-$x,2)+[math]::Pow($c.Y-$y,2)) -ge 347.98) 'Finger exclusion'
            }
            for($i=0;$i -lt 6;$i++) {
                $a=(-90+60*$i)*[math]::PI/180
                $p=[Drawing.PointF]::new($c.X+206*[math]::Cos($a),$c.Y+206*[math]::Sin($a))
                Assert ((Invoke-Method 'Hit' @($p)) -eq $i) 'Wrong sector'
            }
            Invoke-Method 'Cancel' @('test') | Out-Null
            $cases++
        }
    }
    Set-Field 'anchor' ([Drawing.PointF]::new(68,96))
    Set-Field 'active' ([uint32]42)
    Invoke-Method 'OpenRing' @() | Out-Null
    Invoke-Method 'TouchUp' @([uint32]42,[Drawing.PointF]::new(68,96)) | Out-Null
    Assert (Get-Field 'visibleRing') 'Fixed ring closed on initial up'
    Assert (-not (Get-Field 'waitInitialUp')) 'Initial up not acknowledged'
    Invoke-Method 'TouchDown' @([uint32]43,[Drawing.PointF]::new(1,1)) | Out-Null
    Assert (-not (Get-Field 'visibleRing')) 'Outside cancellation failed'
    # Input message guards: no install for thread zero or same-process windows.
    $begin=$native.GetMethod('BeginBridge',$static)
    Assert ($begin.Invoke($null,@([uint32]0,[uint64]0,[uint64]0)) -eq 87) 'Unsafe target accepted'
    Write-Output "PASS: overlay build; 72-byte ABI; no autonomous timer; $cases placements; 294 hits; fixed initial release; outside cancel; invalid bridge target refused. No Explorer connection, no interactive window."
} finally {
    $native.GetMethod('EndObservation',$static).Invoke($null,@()) | Out-Null
    (Get-Field 'timer').Stop()
    (Get-Field 'log').Dispose()
    $form.Dispose()
}
