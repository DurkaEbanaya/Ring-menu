#define EXPLORER_BRIDGE
#include "ExplorerObserver.c"

EXPORT U32 BeginBridge(U32 thread, U64 root, U64 view) {
    U32 rootPid = 0, viewPid = 0;
    if (!root || !view || GetWindowThreadProcessId((PTR)root, &rootPid) != thread ||
        GetWindowThreadProcessId((PTR)view, &viewPid) != thread || rootPid != viewPid ||
        rootPid == GetCurrentProcessId() || GetAncestor((PTR)view, 2) != (PTR)root)
        return 87;
    U32 error = BeginObservation(thread);
    if (error) return error;
    shared->root = root; shared->view = view;
    shared->heartbeat = GetTickCount(); shared->enabled = 1;
    return 0;
}
EXPORT U32 BridgePulse(U32 enabled) {
    if (!shared) return 0;
    shared->heartbeat = GetTickCount(); shared->enabled = enabled;
    return shared->owned;
}
EXPORT U32 BridgeDiagnosticDrops(void) {
    return shared ? shared->diagnosticDropped : 0;
}
/* Retarget only between complete contacts. Keep the original session deadline
 * and exclusive mapping; desktop and folder views can use different threads. */
EXPORT U32 RetargetBridge(U32 thread, U64 root, U64 view) {
    U32 rootPid = 0, viewPid = 0;
    if (!shared || shared->owned || !thread || !root || !view ||
        GetWindowThreadProcessId((PTR)root, &rootPid) != thread ||
        GetWindowThreadProcessId((PTR)view, &viewPid) != thread || rootPid != viewPid ||
        rootPid == GetCurrentProcessId() || GetAncestor((PTR)view, 2) != (PTR)root)
        return 87;
    shared->enabled = 0;
    if (getHook) UnhookWindowsHookEx(getHook);
    if (callHook) UnhookWindowsHookEx(callHook);
    getHook = callHook = 0;
    shared->thread = thread; shared->root = root; shared->view = view;
    getHook = SetWindowsHookExW(3, (PTR)ObserveGetMessage, module, thread);
    if (!getHook) { U32 e = GetLastError(); EndObservation(); return e; }
    callHook = SetWindowsHookExW(4, (PTR)ObserveCallWndProc, module, thread);
    if (!callHook) { U32 e = GetLastError(); EndObservation(); return e; }
    shared->heartbeat = GetTickCount(); shared->enabled = 1;
    return 0;
}
