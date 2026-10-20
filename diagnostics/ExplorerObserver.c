/* Temporary x64 WH_GETMESSAGE / WH_CALLWNDPROC observer. No CRT or SDK needed.
 * Never changes a message, subclasses a window, performs file I/O, or calls
 * SendMessage. All callbacks chain. Only one explicitly selected thread.
 * The small shared queue uses a nonblocking lock: contention drops an event.
 */
typedef unsigned int U32;
typedef int I32;
typedef unsigned long long U64;
typedef long long I64;
typedef void *PTR;
typedef unsigned short WCHAR;
#define API __declspec(dllimport)
#define EXPORT __declspec(dllexport)
typedef struct { I32 x, y; } POINT;
typedef struct { PTR hwnd; U32 message; U64 wp; I64 lp; U32 time; POINT pt; U32 privateData; } MSG;
typedef struct { I64 lp; U64 wp; U32 message; PTR hwnd; } CWP;
typedef struct {
    U32 type, id, frame, flags;
    PTR device, target;
    POINT pixel, himetric, rawPixel, rawHimetric;
    U32 time, history;
    I32 inputData;
    U32 keyStates;
    U64 performanceCount;
    U32 buttonChange;
} POINTER_INFO;
typedef struct { U32 device, origin; } SOURCE;
#include "EventQueue.h"
typedef struct {
    volatile I32 lock;
    U32 magic, active, thread, deadline, read, write, dropped;
    EVENT events[512];
#ifdef EXPLORER_BRIDGE
    U64 root, view;
    U32 heartbeat, owned, generation, enabled;
    U32 diagnosticDropped;
    U64 notifyWindow;
    volatile I32 notifyPending;
#endif
} SHARED;
_Static_assert(sizeof(MSG) == 48, "MSG x64 ABI");
_Static_assert(sizeof(CWP) == 32, "CWP x64 ABI");
_Static_assert(sizeof(POINTER_INFO) == 96, "POINTER_INFO x64 ABI");
_Static_assert(sizeof(EVENT) == 72, "EVENT x64 ABI");
_Static_assert(__builtin_offsetof(SHARED, events) == 32, "SHARED ABI");
API PTR CreateFileMappingW(PTR, PTR, U32, U32, U32, const WCHAR *);
API PTR OpenFileMappingW(U32, I32, const WCHAR *);
API PTR MapViewOfFile(PTR, U32, U32, U32, U64);
API I32 UnmapViewOfFile(PTR);
API I32 CloseHandle(PTR);
API U32 GetLastError(void);
API U32 GetTickCount(void);
API U32 GetCurrentThreadId(void);
API I32 DisableThreadLibraryCalls(PTR);
API PTR SetWindowsHookExW(I32, PTR, PTR, U32);
API I32 UnhookWindowsHookEx(PTR);
API I64 CallNextHookEx(PTR, I32, U64, I64);
API I32 GetPointerInfo(U32, POINTER_INFO *);
API I32 GetCurrentInputMessageSource(SOURCE *);
API I64 GetMessageExtraInfo(void);
API I32 GetClassNameW(PTR, WCHAR *, I32);
API I32 GetMessageTime(void);
API I32 GetMenuItemCount(PTR);
#ifdef EXPLORER_BRIDGE
static const WCHAR mappingName[] = L"Local\\TouchMenuStudy.ExplorerBridge.v1";
#else
static const WCHAR mappingName[] = L"Local\\TouchMenuStudy.ExplorerObserver.v1";
#endif
static PTR module, mapping, getHook, callHook;
static SHARED *shared;
static U32 lastUpdate;
static I32 tryLock(volatile I32 *p) { return __sync_val_compare_and_swap(p, 0, 1) == 0; }
static void unlock(volatile I32 *p) { __sync_lock_release(p); }
static void diagnosticLoss(SHARED *s) {
#ifdef EXPLORER_BRIDGE
    __sync_fetch_and_add(&s->diagnosticDropped, 1);
#else
    __sync_fetch_and_add(&s->dropped, 1);
#endif
}

static SHARED *connect(void) {
    if (!shared) {
        mapping = OpenFileMappingW(6, 0, mappingName);
        if (!mapping) return 0;
        shared = (SHARED *)MapViewOfFile(mapping, 6, 0, 0, sizeof(SHARED));
        if (!shared) { CloseHandle(mapping); mapping = 0; }
    }
    return shared;
}

static void record(U32 stage, PTR hwnd, U32 message, U64 wp, I64 lp, U32 time) {
    int pointer = message >= 0x245 && message <= 0x24f;
    int relevant = pointer || (message >= 0x201 && message <= 0x209) ||
        message == 0x7b || message == 0x119 || message == 0x240 || message == 0x2cc ||
        message == 0x211 || message == 0x212 || message == 0x117;
    if (!relevant) return; /* No keyboard or arbitrary message parameters. */
    SHARED *s = connect();
    U32 now = GetTickCount();
    if (!s || s->magic != 0x544d5331 || !s->active || s->thread != GetCurrentThreadId() ||
        (I32)(now - s->deadline) >= 0) return;
    if (message == 0x245 && now - lastUpdate < 60) return;
    if (message == 0x245) lastUpdate = now;
    EVENT e = {0};
    e.tick = now; e.thread = GetCurrentThreadId(); e.stage = stage; e.message = message;
    e.hwnd = (U64)hwnd; e.osTime = time;
    /* Only counts/position, never menu text, command IDs or object names. */
    if (message == 0x117) {
        I32 count = GetMenuItemCount((PTR)wp);
        e.reserved = count < 0 ? 0xffffffffU : (U32)count;
        e.id = (U32)lp & 0xffff;
    }
    e.extra = (U64)GetMessageExtraInfo();
    SOURCE src = {0};
    GetCurrentInputMessageSource(&src);
    e.sourceDevice = src.device; e.sourceOrigin = src.origin;
    if (pointer) {
        POINTER_INFO p = {0};
        e.id = (U32)wp & 0xffff;
        if (GetPointerInfo(e.id, &p)) {
            e.type = p.type; e.flags = p.flags; e.x = p.pixel.x; e.y = p.pixel.y;
            e.osTime = p.time;
        } else {
            e.pointerError = GetLastError();
            e.x = (short)(lp & 0xffff); e.y = (short)((lp >> 16) & 0xffff);
        }
    }
    if (!tryLock(&s->lock)) { diagnosticLoss(s); return; }
    if (!queueEvent(s->events, &s->write, s->read, &e, 1)) diagnosticLoss(s);
    unlock(&s->lock);
}

#ifdef EXPLORER_BRIDGE
#include "ExplorerBridge.h"
#endif
EXPORT I64 ObserveGetMessage(I32 code, U64 wp, I64 lp) {
#ifdef EXPLORER_BRIDGE
    /* Let other hooks inspect original input first. The experiment only edits
     * the final returned MSG; never tries to suppress via a hook return value. */
    I64 result = CallNextHookEx(0, code, wp, lp);
    if (code == 0 && wp == 1 && lp) {
        MSG *m = (MSG *)lp;
        record(1, m->hwnd, m->message, m->wp, m->lp, m->time);
        bridgeMessage(m);
    }
    return result;
#else
    if (code == 0 && wp == 1 && lp) {
        const MSG *m = (const MSG *)lp;
        record(1, m->hwnd, m->message, m->wp, m->lp, m->time);
    }
    return CallNextHookEx(0, code, wp, lp);
#endif
}
EXPORT I64 ObserveCallWndProc(I32 code, U64 wp, I64 lp) {
    if (code == 0 && lp) {
        const CWP *m = (const CWP *)lp;
        record(2, m->hwnd, m->message, m->wp, m->lp, (U32)GetMessageTime());
    }
    return CallNextHookEx(0, code, wp, lp);
}
EXPORT void EndObservation(void) {
    if (shared) shared->active = 0;
    if (getHook) UnhookWindowsHookEx(getHook);
    if (callHook) UnhookWindowsHookEx(callHook);
    getHook = callHook = 0;
}
EXPORT U32 BeginObservation(U32 thread) {
    if (!thread || mapping || shared) return 87;
    mapping = CreateFileMappingW((PTR)-1, 0, 4, 0, sizeof(SHARED), mappingName);
    U32 error = GetLastError();
    if (!mapping) return error;
    if (error == 183) { CloseHandle(mapping); mapping = 0; return 183; }
    shared = (SHARED *)MapViewOfFile(mapping, 6, 0, 0, sizeof(SHARED));
    if (!shared) { error = GetLastError(); CloseHandle(mapping); mapping = 0; return error; }
    shared->magic = 0x544d5331; shared->thread = thread;
    shared->deadline = GetTickCount() + 180000; shared->active = 1;
    getHook = SetWindowsHookExW(3, (PTR)ObserveGetMessage, module, thread);
    if (!getHook) { error = GetLastError(); EndObservation(); return error; }
    callHook = SetWindowsHookExW(4, (PTR)ObserveCallWndProc, module, thread);
    if (!callHook) { error = GetLastError(); EndObservation(); return error; }
    return 0;
}
EXPORT U32 DrainObservation(EVENT *output, U32 capacity, U32 *dropped) {
    SHARED *s = shared;
    if (!s || !output || !dropped || !tryLock(&s->lock)) return 0;
    U32 count = 0;
    while (s->read != s->write && count < capacity) {
        output[count++] = s->events[s->read % 512]; s->read++;
    }
    *dropped = s->dropped;
#ifdef EXPLORER_BRIDGE
    /* Ack under the queue lock: producer cannot enqueue between empty check
     * and acknowledgement. Repost if a bounded drain left queued records. */
    __sync_lock_release(&s->notifyPending);
    int more = s->read != s->write;
#endif
    unlock(&s->lock);
#ifdef EXPLORER_BRIDGE
    if (more) notifyBridge(s);
#endif
    return count;
}
EXPORT I32 ObserverClass(U64 hwnd, WCHAR *name, I32 capacity) {
    return GetClassNameW((PTR)hwnd, name, capacity);
}
I32 DllMain(PTR instance, U32 reason, PTR reserved) {
    (void)reserved;
    if (reason == 1) { module = instance; DisableThreadLibraryCalls(instance); }
    if (reason == 0) {
        if (shared) UnmapViewOfFile(shared);
        if (mapping) CloseHandle(mapping);
    }
    return 1;
}
