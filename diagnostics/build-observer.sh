#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
build=$(mktemp -d /tmp/opencode/observer-build.XXXXXX)
cat > "$build/kernel32.def" <<'EOF'
LIBRARY KERNEL32.dll
EXPORTS
CreateFileMappingW
OpenFileMappingW
MapViewOfFile
UnmapViewOfFile
CloseHandle
GetLastError
GetTickCount
GetCurrentThreadId
DisableThreadLibraryCalls
GetCurrentProcessId
EOF
cat > "$build/user32.def" <<'EOF'
LIBRARY USER32.dll
EXPORTS
SetWindowsHookExW
UnhookWindowsHookEx
CallNextHookEx
GetPointerInfo
GetCurrentInputMessageSource
GetMessageExtraInfo
GetMessageTime
GetClassNameW
IsChild
GetAncestor
GetParent
GetForegroundWindow
GetDpiForWindow
GetWindowThreadProcessId
GetMenuItemCount
PostMessageW
EOF
llvm-dlltool -m i386:x86-64 -d "$build/kernel32.def" -l "$build/kernel32.lib"
llvm-dlltool -m i386:x86-64 -d "$build/user32.def" -l "$build/user32.lib"
clang --target=x86_64-pc-windows-msvc -std=c11 -O2 -Wall -Wextra -Werror -fno-builtin -fno-stack-protector -c ExplorerObserver.c -o "$build/observer.obj"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerObserver.dll "$build/observer.obj" "$build/kernel32.lib" "$build/user32.lib"
llvm-readobj --file-headers --coff-imports --coff-exports ExplorerObserver.dll
clang --target=x86_64-pc-windows-msvc -std=c11 -O2 -Wall -Wextra -Werror -fno-builtin -fno-stack-protector -c ExplorerBridge.c -o "$build/bridge.obj"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v02.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v03.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v04.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v05.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v06.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v07.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v08.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v09.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
lld-link /dll /machine:x64 /entry:DllMain /nodefaultlib /dynamicbase /nxcompat /out:ExplorerBridge-v012.dll "$build/bridge.obj" "$build/kernel32.lib" "$build/user32.lib"
clang -std=c11 -O2 -Wall -Wextra -Werror Test-BridgePolicy.c -o "$build/test-policy"
"$build/test-policy"
clang -std=c11 -O2 -Wall -Wextra -Werror Test-EventQueue.c -o "$build/test-queue"
"$build/test-queue"
