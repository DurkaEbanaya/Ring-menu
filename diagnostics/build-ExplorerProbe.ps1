$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /out:ExplorerProbe.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ExplorerProbe.cs
    if ($LASTEXITCODE -ne 0) { throw 'ExplorerProbe compilation failed.' }
} finally { Pop-Location }
