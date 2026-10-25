#include <stdio.h>
#include "kq_config.h"
#include "kq_regs.h"

unsigned kq_uart_setup(void);
unsigned kq_dma_setup(void);

int main(void)
{
    printf("uart %x dma %x\n", kq_uart_setup(), kq_dma_setup());
    return 0;
}
