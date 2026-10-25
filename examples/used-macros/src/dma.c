#include "kq_config.h"
#include "kq_regs.h"

unsigned kq_dma_setup(void)
{
#if KQ_HAS_DMA
    return KQ_REG(DMA, CFG) | KQ_FIELD(DMA, CFG, BURST, 4) | KQ_FIELD(DMA, CFG, EN, 1);
#else
    return 0;
#endif
}
