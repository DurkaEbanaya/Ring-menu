$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'ExplorerRing-v09.exe'))
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
    # Dynamic context labels and disabled sectors share the same geometry.
    Invoke-Method 'Configure' @([string[]]@('Paste','Paste shortcut','Refresh','New folder','Properties','More'),[bool[]]@($false,$false,$true,$true,$true,$true),$true) | Out-Null
    Set-Field 'anchor' ([Drawing.PointF]::new(1500,960))
    Invoke-Method 'OpenRing' @() | Out-Null
    $c=Get-Field 'center'
    for($i=0;$i -lt 6;$i++) {
        $a=(-90+60*$i)*[math]::PI/180
        $p=[Drawing.PointF]::new($c.X+206*[math]::Cos($a),$c.Y+206*[math]::Sin($a))
        $expected=if($i -lt 2) { -1 } else { $i }
        Assert ((Invoke-Method 'Hit' @($p)) -eq $expected) 'Disabled sector selectable'
    }
    Assert ((Get-Field 'contextLabel')[0] -eq [char]0x041f) 'Wrong background context label'
    # Exercise the actual PArgb renderer, Reveal and both material palettes.
    $bitmap=[Drawing.Bitmap]::new(3000,1920,[Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        Set-Field 'active' ([uint32]99)
        Set-Field 'waitInitialUp' $false
        Set-Field 'last' ([Drawing.PointF]::new(1500,754))
        Set-Field 'hover' 2
        foreach($dark in @($false,$true)) {
            Set-Field 'darkMaterial' $dark
            foreach($blur in @($false,$true)) {
                Set-Field 'materialBlur' $blur
                $graphics.Clear([Drawing.Color]::Transparent)
                Invoke-Method 'DrawFluentRing' @($graphics) | Out-Null
                Assert ($bitmap.GetPixel(1500,960).A -gt 0) 'Material center missing'
                Assert ($bitmap.GetPixel(0,0).A -eq 0) 'Overlay background must be transparent'
            }
        }
        Set-Field 'materialBlur' $false
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    # Exercise UpdateLayeredWindow only on our own hidden window (never Show).
    $form.ClientSize=[Drawing.Size]::new(640,640)
    Set-Field 'center' ([Drawing.PointF]::new(320,320))
    Set-Field 'last' ([Drawing.PointF]::new(320,114))
    $null=$form.Handle
    Invoke-Method 'PresentOverlay' @() | Out-Null
    Assert (-not $form.Visible) 'Layered surface test showed a window'
    Invoke-Method 'Cancel' @('test') | Out-Null
    # Input message guards: no install for thread zero or same-process windows.
    $begin=$native.GetMethod('BeginBridge',$static)
    Assert ($begin.Invoke($null,@([uint32]0,[uint64]0,[uint64]0)) -eq 87) 'Unsafe target accepted'
    Write-Output "PASS: overlay build; 72-byte ABI; no autonomous timer; $cases placements; 294 hits; fixed initial release; outside cancel; background labels; disabled sector hit rejection; PArgb renderer light/dark solid/translucent Reveal; invalid bridge target refused. No Explorer connection, no interactive window."
} finally {
    $native.GetMethod('EndObservation',$static).Invoke($null,@()) | Out-Null
    (Get-Field 'timer').Stop()
    (Get-Field 'log').Dispose()
    $form.Dispose()
}
