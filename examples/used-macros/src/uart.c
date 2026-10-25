#include "kq_config.h"
#include "kq_regs.h"

unsigned kq_uart_setup(void)
{
    return KQ_REG(UART, CTRL) | KQ_FIELD(UART, CTRL, MODE, KQ_UART_MODE) | KQ_FIELD(UART, CTRL, EN, 1);
}
