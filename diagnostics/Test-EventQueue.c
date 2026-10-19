#include <assert.h>
#include <stdio.h>
typedef unsigned int U32;
typedef int I32;
typedef unsigned long long U64;
#include "EventQueue.h"
int main(void) {
    EVENT events[512] = {0}, e = {0};
    U32 read = 0, write = 0;
    e.stage = 1; e.message = 0x245;
    for (unsigned i = 0; i < 448; i++) assert(queueEvent(events,&write,read,&e,1)==1);
    assert(!queueEvent(events,&write,read,&e,1) && write==448);
    e.stage = 3; e.message = 1; e.id = 1;
    assert(queueEvent(events,&write,read,&e,0)==1);
    e.message = 2;
    assert(queueEvent(events,&write,read,&e,0)==1);
    for (int i = 0; i < 100000; i++) {
        e.x = i;
        assert(queueEvent(events,&write,read,&e,0)==2);
    }
    assert(write==450 && events[449].x==99999);
    e.message = 3;
    assert(queueEvent(events,&write,read,&e,0)==1 && events[450].message==3);
    e.message = 2;
    assert(queueEvent(events,&write,read,&e,0)==1); // cannot replace UP
    e.flags = 0x8000;
    assert(queueEvent(events,&write,read,&e,0)==1); // cancel not coalesced
    e.flags = 0;
    assert(queueEvent(events,&write,read,&e,0)==1); // cannot replace cancel
    e.id++;
    assert(queueEvent(events,&write,read,&e,0)==1); // different contact
    e.hwnd++;
    assert(queueEvent(events,&write,read,&e,0)==1); // different target
    e.message = 4;
    while(write<512) assert(queueEvent(events,&write,read,&e,0)==1);
    assert(!queueEvent(events,&write,read,&e,0)); // real critical exhaustion
    read = write; write = read = 0xfffffff0U;
    for (unsigned i = 0; i < 32; i++) assert(queueEvent(events,&write,read,&e,0)==1);
    assert(write-read==32 && events[(write-1)%512].message==4);
    puts("PASS: queue diagnostic reserve; 100000 MOVE coalesces; exact UP; cancellation/contact/window boundaries; critical exhaustion; tick-index wrap");
    return 0;
}
