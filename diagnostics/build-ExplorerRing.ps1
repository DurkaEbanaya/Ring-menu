$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /define:EXPLORER_RING /out:ExplorerRing-v05.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll "/reference:$wpf\WindowsBase.dll" "/reference:$wpf\UIAutomationClient.dll" "/reference:$wpf\UIAutomationTypes.dll" RingLab.cs ExplorerRingOverlay.cs ExplorerRing.cs ExplorerRename.cs
    if ($LASTEXITCODE -ne 0) { throw 'ExplorerRing compilation failed.' }
} finally { Pop-Location }
