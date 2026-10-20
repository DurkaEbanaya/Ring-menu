#include <assert.h>
#include <stdio.h>
#include "BridgePolicy.h"
int main(void) {
    BRIDGE_POLICY p = {0};
    assert(bridgePolicy(&p,0x246,1,0x2004,100,100,1000,28,1)==0);
    assert(bridgePolicy(&p,0x245,1,0x2004,101,100,1299,28,1)==0);
    assert(bridgePolicy(&p,0x245,1,0x2004,101,100,1300,28,1)==1);
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
    /* Desktop drag regression: contact 290, no UP, terminal LEAVE, then a
     * stationary hold 291. Ticks/coordinates/flags mirror the captured trace. */
    bridgePolicy(&p,0x246,290,0x16017,2753,502,43349562,28,1);
    bridgePolicy(&p,0x245,290,0x26016,2738,500,43349625,28,1);
    assert(bridgePolicy(&p,0x24a,290,0x46000,1958,498,43350015,28,1)==0);
    assert(!p.id && !p.phase);
    bridgePolicy(&p,0x246,291,0x16017,2022,479,43350671,28,1);
    assert(bridgePolicy(&p,0x245,291,0x26016,2015,473,43351375,28,1)==1);
    bridgePolicy(&p,0x247,291,0x46000,2015,473,43351400,28,1);
    /* In-contact LEAVE is not release, nor is another pointer's LEAVE. */
    bridgePolicy(&p,0x246,310,0x16017,100,100,1000,28,1);
    assert(bridgePolicy(&p,0x24a,999,0x46000,100,100,1100,28,1)==0 && p.id==310);
    assert(bridgePolicy(&p,0x24a,310,0x26016,100,100,1200,28,1)==0 && p.id==310);
    assert(bridgePolicy(&p,0x245,310,0x26016,100,100,1650,28,1)==1);
    assert(bridgePolicy(&p,0x24a,310,0x26016,100,100,1700,28,1)==0 && p.phase==2);
    assert(bridgePolicy(&p,0x24a,310,0x46000,100,100,1750,28,1)==3 && !p.id);
    /* No terminal message at all: a new primary recovers an unowned candidate,
     * but a secondary finger does not. Owned contacts are never replaced. */
    bridgePolicy(&p,0x246,320,0x16017,100,100,2000,28,1);
    bridgePolicy(&p,0x245,320,0x26016,200,100,2100,28,1);
    bridgePolicy(&p,0x246,321,0x16017,100,100,2300,28,1);
    assert(p.id==321 && p.phase==1);
    assert(bridgePolicy(&p,0x245,321,0x26016,100,100,2950,28,1)==1);
    assert(bridgePolicy(&p,0x246,322,0x16017,100,100,3000,28,1)==4 && p.id==321);
    bridgePolicy(&p,0x247,321,0x46000,100,100,3100,28,1);
    bridgePolicy(&p,0x246,330,0x16017,100,100,4000,28,1);
    bridgePolicy(&p,0x246,331,0x10017,100,100,4100,28,1);
    assert(p.id==330 && p.phase==3);
    assert(bridgePolicy(&p,0x245,330,0x26016,100,100,4700,28,1)==0);
    bridgePolicy(&p,0x24c,330,0,100,100,4800,28,1);
    assert(!p.id);
    puts("PASS: threshold, continuation, release, short tap, pre-hold drag, multi-touch pre-hold, eligibility loss, nonprimary, tick wrap, cancellation; desktop drag LEAVE replay, in-contact/unrelated LEAVE, missing-UP recovery, owned-contact preservation");
    return 0;
}
