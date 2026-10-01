#ifndef CONFIG_H
#define CONFIG_H
#define SCALE     4
#define RAW_VALUE 42
/* Register map — almost all unused by the carved code; header-content carving strips the dead ones. */
#define REG_CTRL 0x3688
#define REG_STATUS 0x017E
#define REG_DATA 0x6146
#define REG_INTEN 0x4FC1
#define REG_INTCLR 0x02ED
#define REG_BAUD 0x5154
#define REG_FIFO 0x7061
#define REG_DMA 0x4A48
#define REG_GPIOA 0x1BDE
#define REG_GPIOB 0x5060
#define REG_GPIOC 0x539E
#define REG_TIMER 0x3297
#define REG_WDOG 0x7E09
#define REG_PLL 0x34F8
#define REG_FLASHCTL 0x32AF
#define REG_CACHE 0x0040
#define REG_CRC 0x4480
#define REG_RNG 0x60BC
#define REG_USBPHY 0x15F6
#define REG_ADC 0x2B3A
#endif
