/* Pure gesture policy. No OS calls. Used by the DLL and host unit tests.
 * 0 pass; 1 takeover+cleanup; 2 consume; 3 consume+finish; 4 second-contact pass.
 */
typedef struct {
    unsigned id, start, phase;
    int x, y;
} BRIDGE_POLICY;
static int bridgePolicy(BRIDGE_POLICY *p, unsigned msg, unsigned id,
    unsigned flags, int x, int y, unsigned now, unsigned slop, int eligible)
{
    if (msg == 0x246) {
        if (p->id) { if (p->phase == 2) return 4; p->phase = 3; return 0; }
        if (!eligible || !(flags & 0x2000)) return 0; /* primary touch only */
        p->id = id; p->start = now; p->x = x; p->y = y; p->phase = 1;
        return 0;
    }
    if (!p->id || p->id != id) return 0;
    if (p->phase == 2) {
        if (msg == 0x247 || (flags & 0x8000) || msg == 0x24c) {
            p->id = 0; p->phase = 0; return 3;
        }
        return 2;
    }
    if (msg == 0x247 || msg == 0x24c || (flags & 0x8000)) {
        p->id = 0; p->phase = 0; return 0;
    }
    long long dx = (long long)x - p->x, dy = (long long)y - p->y;
    if (!eligible || dx*dx + dy*dy > (long long)slop*slop) p->phase = 3;
    if (p->phase == 1 && msg == 0x245 && now - p->start >= 650) {
        p->phase = 2; return 1;
    }
    return 0;
}
