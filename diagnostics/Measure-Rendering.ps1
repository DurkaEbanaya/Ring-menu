param([string]$Executable='ExplorerRing-v014.exe',[int]$Frames=30)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot $Executable))
$type=$assembly.GetType('RingLab',$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$static=[Reflection.BindingFlags]'Static,NonPublic'
$form=$type.GetMethod('CreateOverlay',$static).Invoke($null,@())
function Field($name,$value) { $type.GetField($name,$flags).SetValue($form,$value) }
try {
    $form.ClientSize=[Drawing.Size]::new(630,630)
    Field 'scale' ([single]2); Field 'radius' ([single]296); Field 'inner' ([single]116)
    Field 'center' ([Drawing.PointF]::new(315,315)); Field 'last' ([Drawing.PointF]::new(315,109))
    Field 'visibleRing' $true; Field 'active' ([uint32]123); Field 'waitInitialUp' $false
    Field 'materialBlur' $true; Field 'hover' 0
    $null=$form.Handle
    $present=$type.GetMethod('PresentOverlay',$flags)
    $warm=$type.GetMethod('WarmRenderer',$static)
    if($null -ne $warm) {
        $startup=[Diagnostics.Stopwatch]::StartNew(); $warm.Invoke($null,@()) | Out-Null
        'STARTUP_WARMUP executable={0} ms={1:F2} hidden=true' -f $Executable,$startup.Elapsed.TotalMilliseconds
    }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $present.Invoke($form,@()) | Out-Null
    'FIRST_RENDER executable={0} ms={1:F2} hidden=true' -f $Executable,$watch.Elapsed.TotalMilliseconds
    for($i=0;$i -lt 3;$i++) { Field 'last' ([Drawing.PointF]::new(310+$i,109)); $present.Invoke($form,@()) | Out-Null }
    $times=@()
    for($i=0;$i -lt $Frames;$i++) {
        Field 'last' ([Drawing.PointF]::new(150+($i*11)%300,150+($i*7)%300))
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $present.Invoke($form,@()) | Out-Null
        $times+=$watch.Elapsed.TotalMilliseconds
    }
    $sorted=@($times | Sort-Object)
    'RENDER executable={0} frames={1} meanMs={2:F2} medianMs={3:F2} p95Ms={4:F2} hidden=true' -f $Executable,$Frames,($times | Measure-Object -Average).Average,$sorted[[int]($Frames/2)],$sorted[[int][Math]::Floor(($Frames-1)*.95)]
    $opening=@()
    for($i=0;$i -lt 5;$i++) {
        $type.GetMethod('ClearRingCache',$flags).Invoke($form,@()) | Out-Null
        $watch.Restart(); $present.Invoke($form,@()) | Out-Null
        $opening+=$watch.Elapsed.TotalMilliseconds
    }
    'REOPEN_RENDER executable={0} meanMs={1:F2} maxMs={2:F2} hidden=true' -f $Executable,($opening | Measure-Object -Average).Average,($opening | Measure-Object -Maximum).Maximum
    if($form.Visible) { throw 'Benchmark showed a window' }
    # Synthetic background only: no screen capture, same brightness decision.
    $sample=[Drawing.Bitmap]::new(630,630,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g=[Drawing.Graphics]::FromImage($sample)
        try { $g.Clear([Drawing.Color]::FromArgb(117,117,117)) } finally { $g.Dispose() }
        $contrast=$type.GetMethod('WhiteInkFor',$static)
        $contrast.Invoke($null,@($sample,[Drawing.Rectangle]::new(19,19,592,592))) | Out-Null
        $watch.Restart()
        for($i=0;$i -lt 10;$i++) { $contrast.Invoke($null,@($sample,[Drawing.Rectangle]::new(19,19,592,592))) | Out-Null }
        'CONTRAST executable={0} meanMs={1:F2} synthetic=true' -f $Executable,($watch.Elapsed.TotalMilliseconds/10)
    } finally { $sample.Dispose() }
    # Query only our own disposable file; never enumerate user files or execute.
    $path=Join-Path ([IO.Path]::GetTempPath()) ('Ring-menu-perf-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    $file=Join-Path $path 'sample.txt'; [IO.File]::WriteAllText($file,'isolated menu benchmark')
    try {
        $shell=$assembly.GetType('ShellCommands',$true)
        $ctor=$shell.GetConstructor($flags,$null,[type[]]@([string],[bool],[IntPtr]),$null)
        for($i=0;$i -lt 3;$i++) {
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $commands=$ctor.Invoke([object[]]@([string]$file,[bool]$false,[IntPtr]::Zero))
            $build=$watch.Elapsed.TotalMilliseconds
            try {
                $watch.Restart()
                $popup=$shell.GetProperty('Menu',$flags).GetValue($commands,$null)
                $entries=$shell.GetMethod('Entries',$flags).Invoke($commands,@($popup,0))
                'SHELL run={0} buildMs={1:F2} entriesMs={2:F2} count={3}' -f $i,$build,$watch.Elapsed.TotalMilliseconds,$entries.Count
                $targetType=$assembly.GetType('ExplorerRename',$true)
                $take=$targetType.GetMethod('TakePreparedMore',$flags)
                if($null -ne $take) {
                    $target=[Activator]::CreateInstance($targetType,$true)
                    try {
                        $targetType.GetField('preparedMore',$flags).SetValue($target,$commands)
                        $seq=$shell.GetMethod('GetClipboardSequenceNumber',$static).Invoke($null,@())
                        $targetType.GetField('clipboardSequence',$flags).SetValue($target,$seq)
                        $watch.Restart(); $reused=$take.Invoke($target,@())
                        if(-not [object]::ReferenceEquals($commands,$reused)) { throw 'Menu not reused' }
                        'SHELL_REUSE run={0} ms={1:F3} sameTargetOnly=true noCommand=true' -f $i,$watch.Elapsed.TotalMilliseconds
                    } finally { $target.Dispose() }
                }
            } finally { $commands.Dispose() }
        }
    } finally { [IO.File]::Delete($file); [IO.Directory]::Delete($path) }
} finally {
    $type.GetField('log',$flags).GetValue($form).Dispose()
    $form.Dispose()
}
