$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { throw 'The .NET Framework x64 compiler was not found.' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /out:TouchProbe.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll TouchProbe.cs
    if ($LASTEXITCODE -ne 0) { throw 'TouchProbe compilation failed.' }
    if (Test-Path 'RingLab.cs') {
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /out:RingLab.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll RingLab.cs
        if ($LASTEXITCODE -ne 0) { throw 'RingLab compilation failed.' }
    }
} finally {
    Pop-Location
}
