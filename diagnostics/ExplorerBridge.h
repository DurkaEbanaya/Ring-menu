/* Only compiled into ExplorerBridge.dll, never the passive observer.
 * Experimental cleanup: substitute WM_POINTERCAPTURECHANGED at takeover, then
 * consume the remainder of that contact. This is not an OS capture transfer.
 * Physical-tablet testing must establish whether Explorer cleans up correctly.
 */
#include "BridgePolicy.h"
API I32 IsChild(PTR, PTR);
API PTR GetAncestor(PTR, U32);
API PTR GetParent(PTR);
API PTR GetForegroundWindow(void);
API U32 GetDpiForWindow(PTR);
API U32 GetWindowThreadProcessId(PTR, U32 *);
API U32 GetCurrentProcessId(void);
static BRIDGE_POLICY policy;
static U32 tailUntil, tailId, ownedAt;
static PTR ownedWindow;
static U64 policyRoot;
static U32 lastBridgeMove, lastSuppressedMove;
EXPORT U64 ResolveBridgeView(U64 root, U64 hwnd) {
    PTR h = (PTR)hwnd;
    if (!root || !h || GetAncestor(h, 2) != (PTR)root) return 0;
    U32 rootPid = 0, childPid = 0;
    U32 thread = GetWindowThreadProcessId((PTR)root, &rootPid);
    if (!thread || GetWindowThreadProcessId(h, &childPid) != thread || rootPid != childPid) return 0;
    const WCHAR expected[] = L"SHELLDLL_DefView";
    /* Bounded ancestor walk, never sibling/global enumeration or titles. */
    for (U32 depth = 0; h && h != (PTR)root && depth < 32; depth++, h = GetParent(h)) {
        WCHAR name[32];
        I32 equal = GetClassNameW(h, name, 32) == 16;
        for (I32 i = 0; equal && i < 16; i++) if (name[i] != expected[i]) equal = 0;
        if (equal) return (U64)h;
    }
    return 0;
}
static int withinView(SHARED *s, PTR hwnd) {
    return ResolveBridgeView(s->root, (U64)hwnd) != 0;
}
static int liveBridge(SHARED *s, U32 now) {
    return s && s->magic == 0x544d5331 && s->active && s->enabled &&
        s->thread == GetCurrentThreadId() && (I32)(now - s->deadline) < 0 &&
        now - s->heartbeat < 1500;
}
static void bridgeEvent(U32 kind, PTR hwnd, U32 id, U32 flags, I32 x, I32 y, U32 now) {
    SHARED *s = shared;
    /* Coordinates at touch-screen refresh rate suffice for hover. The real
     * UP position is always sent unthrottled and decides the final selection. */
    if (kind == 1) lastBridgeMove = now - 16;
    if (kind == 2 && !(flags & 0x8000)) {
        if (now - lastBridgeMove < 16) return;
        lastBridgeMove = now;
    }
    int diagnostic = kind == 5 || kind == 6 || kind == 7;
    if (kind == 5 && flags == 0x200) {
        if (now - lastSuppressedMove < 60) return;
        lastSuppressedMove = now;
    }
    if (diagnostic) {
        if (!s) return;
        if (!tryLock(&s->lock)) { diagnosticLoss(s); return; }
    } else
    if (!s || !tryLock(&s->lock)) { if (s) __sync_fetch_and_add(&s->dropped, 1); return; }
    EVENT e = {0};
    e.tick = now; e.thread = GetCurrentThreadId(); e.stage = 3;
    e.message = kind; e.hwnd = (U64)hwnd; e.id = id;
    e.type = 2; e.flags = flags; e.x = x; e.y = y;
    if (!queueEvent(s->events, &s->write, s->read, &e, diagnostic)) {
        if (diagnostic) diagnosticLoss(s);
        else __sync_fetch_and_add(&s->dropped, 1);
    }
    unlock(&s->lock);
}
static void bridgeMessage(MSG *m) {
    SHARED *s = connect();
    U32 now = GetTickCount();
    if (!s || !s->active || s->thread != GetCurrentThreadId()) return;
    if (policyRoot != s->root) {
        policyRoot = s->root;
        policy.id = policy.phase = 0; tailUntil = 0;
    }
    U32 msg = m->message;
    int live = liveBridge(s, now);
    /* Briefly drain an owned contact even if heartbeat expires, but never
     * suppress indefinitely if a release notification is lost. */
    if (policy.phase == 2 && now - ownedAt >= 15000) {
        bridgeEvent(4, ownedWindow, policy.id, 0x8000, 0, 0, now);
        policy.id = policy.phase = 0; s->owned = 0; tailUntil = 0;
    }
    if (!live && policy.phase != 2 && !(tailUntil && (I32)(now-tailUntil)<0))
    { policy.id = policy.phase = 0; return; }
    if ((msg >= 0x201 && msg <= 0x209) || msg == 0x200) {
        U64 extra = (U64)GetMessageExtraInfo();
        int touch = (extra & 0xffffff80ULL) == 0xff515780ULL;
        if (touch && withinView(s, m->hwnd) &&
            (policy.phase == 2 || (tailUntil && (I32)(now - tailUntil) < 0))) {
            bridgeEvent(5, m->hwnd, tailId, msg, 0, 0, now);
            m->message = 0; m->wp = 0; m->lp = 0;
        } else if (!touch && msg != 0x200) {
            tailUntil = 0; /* Never swallow untagged/physical mouse buttons. */
        }
        return;
    }
    if (msg != 0x246 && msg != 0x245 && msg != 0x247 && msg != 0x24c && msg != 0x24a) return;
    U32 id = (U32)m->wp & 0xffff;
    POINTER_INFO p = {0};
    int ok = GetPointerInfo(id, &p);
    if ((!ok || p.type != 2) && !(id == policy.id && policy.id &&
        (policy.phase == 2 || (!ok && (msg == 0x24c || msg == 0x24a))))) return;
    if (msg == 0x246) tailUntil = 0;
    if (!ok) { p.pixel = m->pt; p.flags = 0x8000; }
    int eligible = live && withinView(s, m->hwnd) && GetForegroundWindow() == (PTR)s->root;
    if (msg == 0x246) {
        WCHAR cls[32];
        const WCHAR expected[] = L"DirectUIHWND";
        I32 len = GetClassNameW(m->hwnd, cls, 32), equal = len == 12;
        for (I32 i = 0; equal && i < 12; i++) if (cls[i] != expected[i]) equal = 0;
        const WCHAR desktop[] = L"SysListView32";
        I32 list = len == 13;
        for (I32 i = 0; list && i < 13; i++) if (cls[i] != desktop[i]) list = 0;
        equal = equal || list;
        eligible = eligible && equal;
        if (eligible) {
            U64 current = ResolveBridgeView(s->root, (U64)m->hwnd);
            if (current && current != s->view) {
                s->view = current;
                bridgeEvent(6, (PTR)current, id, 0, 0, 0, now);
            }
        }
    }
    U32 dpi = GetDpiForWindow((PTR)s->root);
    U32 slop = (14 * (dpi ? dpi : 96) + 48) / 96;
    U32 priorId = policy.id, priorPhase = policy.phase;
    int action = bridgePolicy(&policy, msg, id, p.flags, p.pixel.x, p.pixel.y, now, slop, eligible);
    if (priorId && priorPhase != 2 &&
        ((msg == 0x246 && priorId != id && policy.id == id) ||
         ((msg == 0x24a || msg == 0x24c) && !policy.id)))
        bridgeEvent(7, m->hwnd, priorId, msg, p.pixel.x, p.pixel.y, now);
    if (action == 1) {
        ownedWindow = m->hwnd; tailId = id; ownedAt = now;
        s->owned = id; s->generation++;
        bridgeEvent(1, m->hwnd, id, p.flags, policy.x, policy.y, now);
        /* Preserve a real cleanup dispatch to the original target. Do not
         * claim this grants/changes capture; only a bounded experiment. */
        m->message = 0x24c; m->wp = id; m->lp = (I64)ownedWindow;
    } else if (action == 4) {
        bridgeEvent(4, m->hwnd, id, p.flags, p.pixel.x, p.pixel.y, now);
        /* Secondary contact passes as a complete separate lifecycle. The
         * primary owned contact continues draining without selecting. */
    } else if (action == 2 || action == 3) {
        /* Missing UP/capture loss finishes draining, never commits a command.
         * Only the real UP with a noncancelled pointer may select a sector. */
        U32 flags = p.flags | ((action == 3 && msg != 0x247) ? 0x8000 : 0);
        bridgeEvent(action == 3 ? 3 : 2, m->hwnd, id, flags, p.pixel.x, p.pixel.y, now);
        if (action == 3) { s->owned = 0; tailUntil = now + 250; }
        m->message = 0; m->wp = 0; m->lp = 0;
    }
}
EXPORT U32 BeginBridge(U32 thread, U64 root, U64 view);
/* Implemented after BeginObservation so no guessed cross-module ABI. */
