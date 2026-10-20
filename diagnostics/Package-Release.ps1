param([string]$Destination=(Join-Path $PSScriptRoot 'Ring-menu-0.15-win-x64.zip'))
$ErrorActionPreference='Stop'
$files=@('ExplorerRing-v015.exe','ExplorerBridge-v015.dll')
foreach($name in $files) {
    if(-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) { throw "Missing build output: $name" }
}
$stage=Join-Path ([IO.Path]::GetTempPath()) ('Ring-menu-package-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try {
    foreach($name in $files) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage }
    $instructions=@'
Ring menu 0.15 - experimental Windows 10 x64 build

Keep ExplorerRing-v015.exe and ExplorerBridge-v015.dll in the same folder.
Fully exit older Ring menu versions, then run the EXE as your normal user.
No installation or administrator access is required.
The program waits for an Explorer folder or desktop automatically.
Closing Settings hides to the notification area; use its icon to pause/exit.
Autostart is OFF by default. Enable it only with the Settings button.
Disable autostart before moving/deleting this folder.
Settings and logs: %LOCALAPPDATA%\RingMenu

This build injects thread-specific touch hooks into Explorer.
Use disposable test files. Native capture cancellation is experimental.
More details and source: https://github.com/DurkaEbanaya/Ring-menu
'@
    [IO.File]::WriteAllText((Join-Path $stage 'START-HERE.txt'),$instructions,[Text.Encoding]::UTF8)
    if(Test-Path -LiteralPath $Destination) { throw 'Destination already exists; choose a new path instead of overwriting an unknown archive.' }
    Compress-Archive -LiteralPath @((Join-Path $stage $files[0]),(Join-Path $stage $files[1]),(Join-Path $stage 'START-HERE.txt')) -DestinationPath $Destination
    Get-FileHash -LiteralPath $Destination -Algorithm SHA256
} finally {
    foreach($name in @($files)+@('START-HERE.txt')) {
        $path=Join-Path $stage $name
        if(Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($stage)
}
