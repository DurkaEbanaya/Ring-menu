#include <assert.h>
#include <stdio.h>
#include "BridgePolicy.h"
int main(void) {
    BRIDGE_POLICY p = {0};
    assert(bridgePolicy(&p,0x246,1,0x2004,100,100,1000,28,1)==0);
    assert(bridgePolicy(&p,0x245,1,0x2004,101,100,1649,28,1)==0);
    assert(bridgePolicy(&p,0x245,1,0x2004,101,100,1650,28,1)==1);
    assert(bridgePolicy(&p,0x245,1,0x2004,300,100,1700,28,0)==2);
    assert(bridgePolicy(&p,0x246,20,0x4,400,100,1710,28,1)==4);
    assert(p.id==1 && p.phase==2);
    assert(bridgePolicy(&p,0x247,1,0,300,100,1800,28,0)==3);
    assert(!p.id && !p.phase);
    bridgePolicy(&p,0x246,2,0x2004,100,100,2000,28,1);
    assert(bridgePolicy(&p,0x247,2,0,100,100,2200,28,1)==0 && !p.id);
    bridgePolicy(&p,0x246,3,0x2004,100,100,3000,28,1);
    assert(bridgePolicy(&p,0x245,3,0x2004,129,100,3100,28,1)==0);
    assert(bridgePolicy(&p,0x245,3,0x2004,100,100,3800,28,1)==0);
    bridgePolicy(&p,0x247,3,0,100,100,3900,28,1);
    bridgePolicy(&p,0x246,4,0x2004,100,100,4000,28,1);
    bridgePolicy(&p,0x246,5,0x4,100,100,4010,28,1);
    assert(bridgePolicy(&p,0x245,4,0x2004,100,100,4700,28,1)==0);
    bridgePolicy(&p,0x247,4,0,100,100,4800,28,1);
    bridgePolicy(&p,0x246,6,0x2004,100,100,5000,28,1);
    assert(bridgePolicy(&p,0x245,6,0x2004,100,100,5700,28,0)==0);
    bridgePolicy(&p,0x247,6,0,100,100,5800,28,1);
    assert(bridgePolicy(&p,0x246,7,0x4,100,100,6000,28,1)==0 && !p.id);
    bridgePolicy(&p,0x246,8,0x2004,100,100,0xfffffff0U,28,1);
    assert(bridgePolicy(&p,0x245,8,0x2004,100,100,0x300,28,1)==1);
    assert(bridgePolicy(&p,0x245,8,0xa004,100,100,0x310,28,1)==3);
    puts("PASS: threshold, continuation, release, short tap, pre-hold drag, multi-touch pre-hold, eligibility loss, nonprimary, tick wrap, cancellation");
    return 0;
}
