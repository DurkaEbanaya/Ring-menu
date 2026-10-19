$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'RingLab.exe'))
$type = $assembly.GetType('RingLab', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$form = [Activator]::CreateInstance($type, $true)
function Set-Field($name, $value) { $type.GetField($name, $flags).SetValue($form, $value) }
function Get-Field($name) { $type.GetField($name, $flags).GetValue($form) }
function Invoke-Method($name, [object[]]$values) { $type.GetMethod($name, $flags).Invoke($form, $values) }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$cases = 0
try {
    # Uses the compiled prototype without showing a window or hooking input.
    $form.ClientSize = [Drawing.Size]::new(3000, 1920)
    Set-Field 'scale' ([single]2)
    Set-Field 'radius' ([single]296)
    Set-Field 'inner' ([single]116)
    Set-Field 'margin' ([single]28)
    Set-Field 'guard' ([single]52)
    foreach ($x in @(1, 68, 324, 1500, 2676, 2932, 2999)) {
        foreach ($y in @(1, 96, 324, 960, 1596, 1824, 1919)) {
            $anchor = [Drawing.PointF]::new($x, $y)
            Set-Field 'anchor' $anchor
            Invoke-Method 'OpenRing' @() | Out-Null
            Assert (Get-Field 'visibleRing') 'Ring did not open'
            $center = Get-Field 'center'
            Assert ($center.X -ge 324 -and $center.X -le 2676) 'Horizontal clipping'
            Assert ($center.Y -ge 324 -and $center.Y -le 1596) 'Vertical clipping'
            if (Get-Field 'fixedMode') {
                $distance = [math]::Sqrt([math]::Pow($center.X-$x,2)+[math]::Pow($center.Y-$y,2))
                Assert ($distance -ge 347.98) 'Ring overlaps finger exclusion zone'
            } else {
                Assert ($center.X -eq $x -and $center.Y -eq $y) 'Gesture ring is not centered'
            }
            for ($i = 0; $i -lt 6; $i++) {
                $angle = (-90+60*$i)*[math]::PI/180
                $point = [Drawing.PointF]::new($center.X+206*[math]::Cos($angle),$center.Y+206*[math]::Sin($angle))
                Assert ((Invoke-Method 'Hit' @($point)) -eq $i) "Wrong sector $i"
            }
            Assert ((Invoke-Method 'Hit' @($center)) -eq -1) 'Center must cancel'
            Invoke-Method 'Cancel' @('test-reset') | Out-Null
            $cases++
        }
    }
    # Original release must not select or close the displaced ring.
    Set-Field 'anchor' ([Drawing.PointF]::new(68,96))
    Set-Field 'active' ([uint32]42)
    Invoke-Method 'OpenRing' @() | Out-Null
    Invoke-Method 'TouchUp' @([uint32]42,[Drawing.PointF]::new(68,96)) | Out-Null
    Assert (Get-Field 'visibleRing') 'Initial release closed fixed ring'
    Assert (-not (Get-Field 'waitInitialUp')) 'Initial release not acknowledged'
    Assert ((Get-Field 'active') -eq 0) 'Initial contact not released'
    Invoke-Method 'TouchDown' @([uint32]43,[Drawing.PointF]::new(1,1)) | Out-Null
    Assert (-not (Get-Field 'visibleRing')) 'Outside touch did not dismiss'
    Write-Output "PASS: $cases placements, 294 sector hits, center cancellation, fixed-mode initial release and outside dismissal. No interactive window shown."
} finally {
    (Get-Field 'timer').Stop()
    (Get-Field 'log').Dispose()
    $form.Dispose()
}
