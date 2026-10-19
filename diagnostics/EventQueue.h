/* OS-free bounded transport. Caller owns the queue lock. Diagnostic records
 * cannot consume the last 64 slots reserved for gesture lifecycle events.
 * Adjacent MOVE records for the same contact coalesce to the latest position;
 * no DOWN/UP/CANCEL or cancelled MOVE is replaced. */
typedef struct {
    U32 tick, thread, stage, message;
    U64 hwnd, extra;
    U32 sourceDevice, sourceOrigin, id, type, flags, pointerError;
    I32 x, y;
    U32 osTime, reserved;
} EVENT;
static int queueEvent(EVENT *events, U32 *write, U32 read, const EVENT *e, int diagnostic) {
    U32 depth = *write - read;
    if (!diagnostic && depth && e->stage == 3 && e->message == 2 && !(e->flags & 0x8000)) {
        EVENT *last = &events[(*write - 1) % 512];
        if (last->stage == 3 && last->message == 2 && last->id == e->id &&
            last->hwnd == e->hwnd && !(last->flags & 0x8000)) {
            *last = *e;
            return 2;
        }
    }
    if (depth >= (diagnostic ? 448U : 512U)) return 0;
    events[*write % 512] = *e;
    (*write)++;
    return 1;
}
