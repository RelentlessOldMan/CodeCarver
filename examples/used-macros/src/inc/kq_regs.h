/* kq_regs.h: a register header shaped like a vendor one: block bases, per-register offsets, per-field
 * shifts and masks, all reached through paste macros. Most of it is never used by this build. */
#ifndef KQ_REGS_H
#define KQ_REGS_H

#define KQ_CAT(a, b) a##b
#define KQ_REG(blk, reg) (KQ_CAT(KQ_BLK_, blk) + KQ_##blk##_##reg##_OFF)
#define KQ_SHIFT(blk, reg, fld) KQ_##blk##_##reg##_##fld##_SHFT
#define KQ_MASK(blk, reg, fld) KQ_##blk##_##reg##_##fld##_BMSK
#define KQ_FIELD(blk, reg, fld, v) (((v) << KQ_SHIFT(blk, reg, fld)) & KQ_MASK(blk, reg, fld))

#define KQ_BLK_UART 0x4000u
#define KQ_BLK_DMA 0x5000u
#define KQ_BLK_TMR 0x6000u
#define KQ_BLK_SPI 0x7000u
#define KQ_BLK_I2C 0x8000u
#define KQ_BLK_GPIO 0x9000u
#define KQ_BLK_ADC 0xa000u
#define KQ_BLK_PWM 0xb000u

#define KQ_UART_CTRL_OFF 0x0u
#define KQ_UART_CTRL_EN_SHFT 0
#define KQ_UART_CTRL_EN_BMSK (0x7u << 0)
#define KQ_UART_CTRL_MODE_SHFT 3
#define KQ_UART_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_UART_CTRL_BURST_SHFT 6
#define KQ_UART_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_UART_CTRL_LEN_SHFT 9
#define KQ_UART_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_UART_CTRL_ERR_SHFT 12
#define KQ_UART_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_UART_STAT_OFF 0x4u
#define KQ_UART_STAT_EN_SHFT 0
#define KQ_UART_STAT_EN_BMSK (0x7u << 0)
#define KQ_UART_STAT_MODE_SHFT 3
#define KQ_UART_STAT_MODE_BMSK (0x7u << 3)
#define KQ_UART_STAT_BURST_SHFT 6
#define KQ_UART_STAT_BURST_BMSK (0x7u << 6)
#define KQ_UART_STAT_LEN_SHFT 9
#define KQ_UART_STAT_LEN_BMSK (0x7u << 9)
#define KQ_UART_STAT_ERR_SHFT 12
#define KQ_UART_STAT_ERR_BMSK (0x7u << 12)
#define KQ_UART_CFG_OFF 0x8u
#define KQ_UART_CFG_EN_SHFT 0
#define KQ_UART_CFG_EN_BMSK (0x7u << 0)
#define KQ_UART_CFG_MODE_SHFT 3
#define KQ_UART_CFG_MODE_BMSK (0x7u << 3)
#define KQ_UART_CFG_BURST_SHFT 6
#define KQ_UART_CFG_BURST_BMSK (0x7u << 6)
#define KQ_UART_CFG_LEN_SHFT 9
#define KQ_UART_CFG_LEN_BMSK (0x7u << 9)
#define KQ_UART_CFG_ERR_SHFT 12
#define KQ_UART_CFG_ERR_BMSK (0x7u << 12)
#define KQ_UART_DATA_OFF 0xcu
#define KQ_UART_DATA_EN_SHFT 0
#define KQ_UART_DATA_EN_BMSK (0x7u << 0)
#define KQ_UART_DATA_MODE_SHFT 3
#define KQ_UART_DATA_MODE_BMSK (0x7u << 3)
#define KQ_UART_DATA_BURST_SHFT 6
#define KQ_UART_DATA_BURST_BMSK (0x7u << 6)
#define KQ_UART_DATA_LEN_SHFT 9
#define KQ_UART_DATA_LEN_BMSK (0x7u << 9)
#define KQ_UART_DATA_ERR_SHFT 12
#define KQ_UART_DATA_ERR_BMSK (0x7u << 12)
#define KQ_UART_IRQ_OFF 0x10u
#define KQ_UART_IRQ_EN_SHFT 0
#define KQ_UART_IRQ_EN_BMSK (0x7u << 0)
#define KQ_UART_IRQ_MODE_SHFT 3
#define KQ_UART_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_UART_IRQ_BURST_SHFT 6
#define KQ_UART_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_UART_IRQ_LEN_SHFT 9
#define KQ_UART_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_UART_IRQ_ERR_SHFT 12
#define KQ_UART_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_UART_BAUD_OFF 0x14u
#define KQ_UART_BAUD_EN_SHFT 0
#define KQ_UART_BAUD_EN_BMSK (0x7u << 0)
#define KQ_UART_BAUD_MODE_SHFT 3
#define KQ_UART_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_UART_BAUD_BURST_SHFT 6
#define KQ_UART_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_UART_BAUD_LEN_SHFT 9
#define KQ_UART_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_UART_BAUD_ERR_SHFT 12
#define KQ_UART_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_DMA_CTRL_OFF 0x0u
#define KQ_DMA_CTRL_EN_SHFT 0
#define KQ_DMA_CTRL_EN_BMSK (0x7u << 0)
#define KQ_DMA_CTRL_MODE_SHFT 3
#define KQ_DMA_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_DMA_CTRL_BURST_SHFT 6
#define KQ_DMA_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_DMA_CTRL_LEN_SHFT 9
#define KQ_DMA_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_DMA_CTRL_ERR_SHFT 12
#define KQ_DMA_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_DMA_STAT_OFF 0x4u
#define KQ_DMA_STAT_EN_SHFT 0
#define KQ_DMA_STAT_EN_BMSK (0x7u << 0)
#define KQ_DMA_STAT_MODE_SHFT 3
#define KQ_DMA_STAT_MODE_BMSK (0x7u << 3)
#define KQ_DMA_STAT_BURST_SHFT 6
#define KQ_DMA_STAT_BURST_BMSK (0x7u << 6)
#define KQ_DMA_STAT_LEN_SHFT 9
#define KQ_DMA_STAT_LEN_BMSK (0x7u << 9)
#define KQ_DMA_STAT_ERR_SHFT 12
#define KQ_DMA_STAT_ERR_BMSK (0x7u << 12)
#define KQ_DMA_CFG_OFF 0x8u
#define KQ_DMA_CFG_EN_SHFT 0
#define KQ_DMA_CFG_EN_BMSK (0x7u << 0)
#define KQ_DMA_CFG_MODE_SHFT 3
#define KQ_DMA_CFG_MODE_BMSK (0x7u << 3)
#define KQ_DMA_CFG_BURST_SHFT 6
#define KQ_DMA_CFG_BURST_BMSK (0x7u << 6)
#define KQ_DMA_CFG_LEN_SHFT 9
#define KQ_DMA_CFG_LEN_BMSK (0x7u << 9)
#define KQ_DMA_CFG_ERR_SHFT 12
#define KQ_DMA_CFG_ERR_BMSK (0x7u << 12)
#define KQ_DMA_DATA_OFF 0xcu
#define KQ_DMA_DATA_EN_SHFT 0
#define KQ_DMA_DATA_EN_BMSK (0x7u << 0)
#define KQ_DMA_DATA_MODE_SHFT 3
#define KQ_DMA_DATA_MODE_BMSK (0x7u << 3)
#define KQ_DMA_DATA_BURST_SHFT 6
#define KQ_DMA_DATA_BURST_BMSK (0x7u << 6)
#define KQ_DMA_DATA_LEN_SHFT 9
#define KQ_DMA_DATA_LEN_BMSK (0x7u << 9)
#define KQ_DMA_DATA_ERR_SHFT 12
#define KQ_DMA_DATA_ERR_BMSK (0x7u << 12)
#define KQ_DMA_IRQ_OFF 0x10u
#define KQ_DMA_IRQ_EN_SHFT 0
#define KQ_DMA_IRQ_EN_BMSK (0x7u << 0)
#define KQ_DMA_IRQ_MODE_SHFT 3
#define KQ_DMA_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_DMA_IRQ_BURST_SHFT 6
#define KQ_DMA_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_DMA_IRQ_LEN_SHFT 9
#define KQ_DMA_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_DMA_IRQ_ERR_SHFT 12
#define KQ_DMA_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_DMA_BAUD_OFF 0x14u
#define KQ_DMA_BAUD_EN_SHFT 0
#define KQ_DMA_BAUD_EN_BMSK (0x7u << 0)
#define KQ_DMA_BAUD_MODE_SHFT 3
#define KQ_DMA_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_DMA_BAUD_BURST_SHFT 6
#define KQ_DMA_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_DMA_BAUD_LEN_SHFT 9
#define KQ_DMA_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_DMA_BAUD_ERR_SHFT 12
#define KQ_DMA_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_TMR_CTRL_OFF 0x0u
#define KQ_TMR_CTRL_EN_SHFT 0
#define KQ_TMR_CTRL_EN_BMSK (0x7u << 0)
#define KQ_TMR_CTRL_MODE_SHFT 3
#define KQ_TMR_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_TMR_CTRL_BURST_SHFT 6
#define KQ_TMR_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_TMR_CTRL_LEN_SHFT 9
#define KQ_TMR_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_TMR_CTRL_ERR_SHFT 12
#define KQ_TMR_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_TMR_STAT_OFF 0x4u
#define KQ_TMR_STAT_EN_SHFT 0
#define KQ_TMR_STAT_EN_BMSK (0x7u << 0)
#define KQ_TMR_STAT_MODE_SHFT 3
#define KQ_TMR_STAT_MODE_BMSK (0x7u << 3)
#define KQ_TMR_STAT_BURST_SHFT 6
#define KQ_TMR_STAT_BURST_BMSK (0x7u << 6)
#define KQ_TMR_STAT_LEN_SHFT 9
#define KQ_TMR_STAT_LEN_BMSK (0x7u << 9)
#define KQ_TMR_STAT_ERR_SHFT 12
#define KQ_TMR_STAT_ERR_BMSK (0x7u << 12)
#define KQ_TMR_CFG_OFF 0x8u
#define KQ_TMR_CFG_EN_SHFT 0
#define KQ_TMR_CFG_EN_BMSK (0x7u << 0)
#define KQ_TMR_CFG_MODE_SHFT 3
#define KQ_TMR_CFG_MODE_BMSK (0x7u << 3)
#define KQ_TMR_CFG_BURST_SHFT 6
#define KQ_TMR_CFG_BURST_BMSK (0x7u << 6)
#define KQ_TMR_CFG_LEN_SHFT 9
#define KQ_TMR_CFG_LEN_BMSK (0x7u << 9)
#define KQ_TMR_CFG_ERR_SHFT 12
#define KQ_TMR_CFG_ERR_BMSK (0x7u << 12)
#define KQ_TMR_DATA_OFF 0xcu
#define KQ_TMR_DATA_EN_SHFT 0
#define KQ_TMR_DATA_EN_BMSK (0x7u << 0)
#define KQ_TMR_DATA_MODE_SHFT 3
#define KQ_TMR_DATA_MODE_BMSK (0x7u << 3)
#define KQ_TMR_DATA_BURST_SHFT 6
#define KQ_TMR_DATA_BURST_BMSK (0x7u << 6)
#define KQ_TMR_DATA_LEN_SHFT 9
#define KQ_TMR_DATA_LEN_BMSK (0x7u << 9)
#define KQ_TMR_DATA_ERR_SHFT 12
#define KQ_TMR_DATA_ERR_BMSK (0x7u << 12)
#define KQ_TMR_IRQ_OFF 0x10u
#define KQ_TMR_IRQ_EN_SHFT 0
#define KQ_TMR_IRQ_EN_BMSK (0x7u << 0)
#define KQ_TMR_IRQ_MODE_SHFT 3
#define KQ_TMR_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_TMR_IRQ_BURST_SHFT 6
#define KQ_TMR_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_TMR_IRQ_LEN_SHFT 9
#define KQ_TMR_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_TMR_IRQ_ERR_SHFT 12
#define KQ_TMR_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_TMR_BAUD_OFF 0x14u
#define KQ_TMR_BAUD_EN_SHFT 0
#define KQ_TMR_BAUD_EN_BMSK (0x7u << 0)
#define KQ_TMR_BAUD_MODE_SHFT 3
#define KQ_TMR_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_TMR_BAUD_BURST_SHFT 6
#define KQ_TMR_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_TMR_BAUD_LEN_SHFT 9
#define KQ_TMR_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_TMR_BAUD_ERR_SHFT 12
#define KQ_TMR_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_SPI_CTRL_OFF 0x0u
#define KQ_SPI_CTRL_EN_SHFT 0
#define KQ_SPI_CTRL_EN_BMSK (0x7u << 0)
#define KQ_SPI_CTRL_MODE_SHFT 3
#define KQ_SPI_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_SPI_CTRL_BURST_SHFT 6
#define KQ_SPI_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_SPI_CTRL_LEN_SHFT 9
#define KQ_SPI_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_SPI_CTRL_ERR_SHFT 12
#define KQ_SPI_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_SPI_STAT_OFF 0x4u
#define KQ_SPI_STAT_EN_SHFT 0
#define KQ_SPI_STAT_EN_BMSK (0x7u << 0)
#define KQ_SPI_STAT_MODE_SHFT 3
#define KQ_SPI_STAT_MODE_BMSK (0x7u << 3)
#define KQ_SPI_STAT_BURST_SHFT 6
#define KQ_SPI_STAT_BURST_BMSK (0x7u << 6)
#define KQ_SPI_STAT_LEN_SHFT 9
#define KQ_SPI_STAT_LEN_BMSK (0x7u << 9)
#define KQ_SPI_STAT_ERR_SHFT 12
#define KQ_SPI_STAT_ERR_BMSK (0x7u << 12)
#define KQ_SPI_CFG_OFF 0x8u
#define KQ_SPI_CFG_EN_SHFT 0
#define KQ_SPI_CFG_EN_BMSK (0x7u << 0)
#define KQ_SPI_CFG_MODE_SHFT 3
#define KQ_SPI_CFG_MODE_BMSK (0x7u << 3)
#define KQ_SPI_CFG_BURST_SHFT 6
#define KQ_SPI_CFG_BURST_BMSK (0x7u << 6)
#define KQ_SPI_CFG_LEN_SHFT 9
#define KQ_SPI_CFG_LEN_BMSK (0x7u << 9)
#define KQ_SPI_CFG_ERR_SHFT 12
#define KQ_SPI_CFG_ERR_BMSK (0x7u << 12)
#define KQ_SPI_DATA_OFF 0xcu
#define KQ_SPI_DATA_EN_SHFT 0
#define KQ_SPI_DATA_EN_BMSK (0x7u << 0)
#define KQ_SPI_DATA_MODE_SHFT 3
#define KQ_SPI_DATA_MODE_BMSK (0x7u << 3)
#define KQ_SPI_DATA_BURST_SHFT 6
#define KQ_SPI_DATA_BURST_BMSK (0x7u << 6)
#define KQ_SPI_DATA_LEN_SHFT 9
#define KQ_SPI_DATA_LEN_BMSK (0x7u << 9)
#define KQ_SPI_DATA_ERR_SHFT 12
#define KQ_SPI_DATA_ERR_BMSK (0x7u << 12)
#define KQ_SPI_IRQ_OFF 0x10u
#define KQ_SPI_IRQ_EN_SHFT 0
#define KQ_SPI_IRQ_EN_BMSK (0x7u << 0)
#define KQ_SPI_IRQ_MODE_SHFT 3
#define KQ_SPI_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_SPI_IRQ_BURST_SHFT 6
#define KQ_SPI_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_SPI_IRQ_LEN_SHFT 9
#define KQ_SPI_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_SPI_IRQ_ERR_SHFT 12
#define KQ_SPI_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_SPI_BAUD_OFF 0x14u
#define KQ_SPI_BAUD_EN_SHFT 0
#define KQ_SPI_BAUD_EN_BMSK (0x7u << 0)
#define KQ_SPI_BAUD_MODE_SHFT 3
#define KQ_SPI_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_SPI_BAUD_BURST_SHFT 6
#define KQ_SPI_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_SPI_BAUD_LEN_SHFT 9
#define KQ_SPI_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_SPI_BAUD_ERR_SHFT 12
#define KQ_SPI_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_I2C_CTRL_OFF 0x0u
#define KQ_I2C_CTRL_EN_SHFT 0
#define KQ_I2C_CTRL_EN_BMSK (0x7u << 0)
#define KQ_I2C_CTRL_MODE_SHFT 3
#define KQ_I2C_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_I2C_CTRL_BURST_SHFT 6
#define KQ_I2C_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_I2C_CTRL_LEN_SHFT 9
#define KQ_I2C_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_I2C_CTRL_ERR_SHFT 12
#define KQ_I2C_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_I2C_STAT_OFF 0x4u
#define KQ_I2C_STAT_EN_SHFT 0
#define KQ_I2C_STAT_EN_BMSK (0x7u << 0)
#define KQ_I2C_STAT_MODE_SHFT 3
#define KQ_I2C_STAT_MODE_BMSK (0x7u << 3)
#define KQ_I2C_STAT_BURST_SHFT 6
#define KQ_I2C_STAT_BURST_BMSK (0x7u << 6)
#define KQ_I2C_STAT_LEN_SHFT 9
#define KQ_I2C_STAT_LEN_BMSK (0x7u << 9)
#define KQ_I2C_STAT_ERR_SHFT 12
#define KQ_I2C_STAT_ERR_BMSK (0x7u << 12)
#define KQ_I2C_CFG_OFF 0x8u
#define KQ_I2C_CFG_EN_SHFT 0
#define KQ_I2C_CFG_EN_BMSK (0x7u << 0)
#define KQ_I2C_CFG_MODE_SHFT 3
#define KQ_I2C_CFG_MODE_BMSK (0x7u << 3)
#define KQ_I2C_CFG_BURST_SHFT 6
#define KQ_I2C_CFG_BURST_BMSK (0x7u << 6)
#define KQ_I2C_CFG_LEN_SHFT 9
#define KQ_I2C_CFG_LEN_BMSK (0x7u << 9)
#define KQ_I2C_CFG_ERR_SHFT 12
#define KQ_I2C_CFG_ERR_BMSK (0x7u << 12)
#define KQ_I2C_DATA_OFF 0xcu
#define KQ_I2C_DATA_EN_SHFT 0
#define KQ_I2C_DATA_EN_BMSK (0x7u << 0)
#define KQ_I2C_DATA_MODE_SHFT 3
#define KQ_I2C_DATA_MODE_BMSK (0x7u << 3)
#define KQ_I2C_DATA_BURST_SHFT 6
#define KQ_I2C_DATA_BURST_BMSK (0x7u << 6)
#define KQ_I2C_DATA_LEN_SHFT 9
#define KQ_I2C_DATA_LEN_BMSK (0x7u << 9)
#define KQ_I2C_DATA_ERR_SHFT 12
#define KQ_I2C_DATA_ERR_BMSK (0x7u << 12)
#define KQ_I2C_IRQ_OFF 0x10u
#define KQ_I2C_IRQ_EN_SHFT 0
#define KQ_I2C_IRQ_EN_BMSK (0x7u << 0)
#define KQ_I2C_IRQ_MODE_SHFT 3
#define KQ_I2C_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_I2C_IRQ_BURST_SHFT 6
#define KQ_I2C_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_I2C_IRQ_LEN_SHFT 9
#define KQ_I2C_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_I2C_IRQ_ERR_SHFT 12
#define KQ_I2C_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_I2C_BAUD_OFF 0x14u
#define KQ_I2C_BAUD_EN_SHFT 0
#define KQ_I2C_BAUD_EN_BMSK (0x7u << 0)
#define KQ_I2C_BAUD_MODE_SHFT 3
#define KQ_I2C_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_I2C_BAUD_BURST_SHFT 6
#define KQ_I2C_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_I2C_BAUD_LEN_SHFT 9
#define KQ_I2C_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_I2C_BAUD_ERR_SHFT 12
#define KQ_I2C_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_GPIO_CTRL_OFF 0x0u
#define KQ_GPIO_CTRL_EN_SHFT 0
#define KQ_GPIO_CTRL_EN_BMSK (0x7u << 0)
#define KQ_GPIO_CTRL_MODE_SHFT 3
#define KQ_GPIO_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_CTRL_BURST_SHFT 6
#define KQ_GPIO_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_CTRL_LEN_SHFT 9
#define KQ_GPIO_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_CTRL_ERR_SHFT 12
#define KQ_GPIO_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_GPIO_STAT_OFF 0x4u
#define KQ_GPIO_STAT_EN_SHFT 0
#define KQ_GPIO_STAT_EN_BMSK (0x7u << 0)
#define KQ_GPIO_STAT_MODE_SHFT 3
#define KQ_GPIO_STAT_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_STAT_BURST_SHFT 6
#define KQ_GPIO_STAT_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_STAT_LEN_SHFT 9
#define KQ_GPIO_STAT_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_STAT_ERR_SHFT 12
#define KQ_GPIO_STAT_ERR_BMSK (0x7u << 12)
#define KQ_GPIO_CFG_OFF 0x8u
#define KQ_GPIO_CFG_EN_SHFT 0
#define KQ_GPIO_CFG_EN_BMSK (0x7u << 0)
#define KQ_GPIO_CFG_MODE_SHFT 3
#define KQ_GPIO_CFG_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_CFG_BURST_SHFT 6
#define KQ_GPIO_CFG_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_CFG_LEN_SHFT 9
#define KQ_GPIO_CFG_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_CFG_ERR_SHFT 12
#define KQ_GPIO_CFG_ERR_BMSK (0x7u << 12)
#define KQ_GPIO_DATA_OFF 0xcu
#define KQ_GPIO_DATA_EN_SHFT 0
#define KQ_GPIO_DATA_EN_BMSK (0x7u << 0)
#define KQ_GPIO_DATA_MODE_SHFT 3
#define KQ_GPIO_DATA_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_DATA_BURST_SHFT 6
#define KQ_GPIO_DATA_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_DATA_LEN_SHFT 9
#define KQ_GPIO_DATA_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_DATA_ERR_SHFT 12
#define KQ_GPIO_DATA_ERR_BMSK (0x7u << 12)
#define KQ_GPIO_IRQ_OFF 0x10u
#define KQ_GPIO_IRQ_EN_SHFT 0
#define KQ_GPIO_IRQ_EN_BMSK (0x7u << 0)
#define KQ_GPIO_IRQ_MODE_SHFT 3
#define KQ_GPIO_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_IRQ_BURST_SHFT 6
#define KQ_GPIO_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_IRQ_LEN_SHFT 9
#define KQ_GPIO_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_IRQ_ERR_SHFT 12
#define KQ_GPIO_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_GPIO_BAUD_OFF 0x14u
#define KQ_GPIO_BAUD_EN_SHFT 0
#define KQ_GPIO_BAUD_EN_BMSK (0x7u << 0)
#define KQ_GPIO_BAUD_MODE_SHFT 3
#define KQ_GPIO_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_GPIO_BAUD_BURST_SHFT 6
#define KQ_GPIO_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_GPIO_BAUD_LEN_SHFT 9
#define KQ_GPIO_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_GPIO_BAUD_ERR_SHFT 12
#define KQ_GPIO_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_ADC_CTRL_OFF 0x0u
#define KQ_ADC_CTRL_EN_SHFT 0
#define KQ_ADC_CTRL_EN_BMSK (0x7u << 0)
#define KQ_ADC_CTRL_MODE_SHFT 3
#define KQ_ADC_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_ADC_CTRL_BURST_SHFT 6
#define KQ_ADC_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_ADC_CTRL_LEN_SHFT 9
#define KQ_ADC_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_ADC_CTRL_ERR_SHFT 12
#define KQ_ADC_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_ADC_STAT_OFF 0x4u
#define KQ_ADC_STAT_EN_SHFT 0
#define KQ_ADC_STAT_EN_BMSK (0x7u << 0)
#define KQ_ADC_STAT_MODE_SHFT 3
#define KQ_ADC_STAT_MODE_BMSK (0x7u << 3)
#define KQ_ADC_STAT_BURST_SHFT 6
#define KQ_ADC_STAT_BURST_BMSK (0x7u << 6)
#define KQ_ADC_STAT_LEN_SHFT 9
#define KQ_ADC_STAT_LEN_BMSK (0x7u << 9)
#define KQ_ADC_STAT_ERR_SHFT 12
#define KQ_ADC_STAT_ERR_BMSK (0x7u << 12)
#define KQ_ADC_CFG_OFF 0x8u
#define KQ_ADC_CFG_EN_SHFT 0
#define KQ_ADC_CFG_EN_BMSK (0x7u << 0)
#define KQ_ADC_CFG_MODE_SHFT 3
#define KQ_ADC_CFG_MODE_BMSK (0x7u << 3)
#define KQ_ADC_CFG_BURST_SHFT 6
#define KQ_ADC_CFG_BURST_BMSK (0x7u << 6)
#define KQ_ADC_CFG_LEN_SHFT 9
#define KQ_ADC_CFG_LEN_BMSK (0x7u << 9)
#define KQ_ADC_CFG_ERR_SHFT 12
#define KQ_ADC_CFG_ERR_BMSK (0x7u << 12)
#define KQ_ADC_DATA_OFF 0xcu
#define KQ_ADC_DATA_EN_SHFT 0
#define KQ_ADC_DATA_EN_BMSK (0x7u << 0)
#define KQ_ADC_DATA_MODE_SHFT 3
#define KQ_ADC_DATA_MODE_BMSK (0x7u << 3)
#define KQ_ADC_DATA_BURST_SHFT 6
#define KQ_ADC_DATA_BURST_BMSK (0x7u << 6)
#define KQ_ADC_DATA_LEN_SHFT 9
#define KQ_ADC_DATA_LEN_BMSK (0x7u << 9)
#define KQ_ADC_DATA_ERR_SHFT 12
#define KQ_ADC_DATA_ERR_BMSK (0x7u << 12)
#define KQ_ADC_IRQ_OFF 0x10u
#define KQ_ADC_IRQ_EN_SHFT 0
#define KQ_ADC_IRQ_EN_BMSK (0x7u << 0)
#define KQ_ADC_IRQ_MODE_SHFT 3
#define KQ_ADC_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_ADC_IRQ_BURST_SHFT 6
#define KQ_ADC_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_ADC_IRQ_LEN_SHFT 9
#define KQ_ADC_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_ADC_IRQ_ERR_SHFT 12
#define KQ_ADC_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_ADC_BAUD_OFF 0x14u
#define KQ_ADC_BAUD_EN_SHFT 0
#define KQ_ADC_BAUD_EN_BMSK (0x7u << 0)
#define KQ_ADC_BAUD_MODE_SHFT 3
#define KQ_ADC_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_ADC_BAUD_BURST_SHFT 6
#define KQ_ADC_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_ADC_BAUD_LEN_SHFT 9
#define KQ_ADC_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_ADC_BAUD_ERR_SHFT 12
#define KQ_ADC_BAUD_ERR_BMSK (0x7u << 12)

#define KQ_PWM_CTRL_OFF 0x0u
#define KQ_PWM_CTRL_EN_SHFT 0
#define KQ_PWM_CTRL_EN_BMSK (0x7u << 0)
#define KQ_PWM_CTRL_MODE_SHFT 3
#define KQ_PWM_CTRL_MODE_BMSK (0x7u << 3)
#define KQ_PWM_CTRL_BURST_SHFT 6
#define KQ_PWM_CTRL_BURST_BMSK (0x7u << 6)
#define KQ_PWM_CTRL_LEN_SHFT 9
#define KQ_PWM_CTRL_LEN_BMSK (0x7u << 9)
#define KQ_PWM_CTRL_ERR_SHFT 12
#define KQ_PWM_CTRL_ERR_BMSK (0x7u << 12)
#define KQ_PWM_STAT_OFF 0x4u
#define KQ_PWM_STAT_EN_SHFT 0
#define KQ_PWM_STAT_EN_BMSK (0x7u << 0)
#define KQ_PWM_STAT_MODE_SHFT 3
#define KQ_PWM_STAT_MODE_BMSK (0x7u << 3)
#define KQ_PWM_STAT_BURST_SHFT 6
#define KQ_PWM_STAT_BURST_BMSK (0x7u << 6)
#define KQ_PWM_STAT_LEN_SHFT 9
#define KQ_PWM_STAT_LEN_BMSK (0x7u << 9)
#define KQ_PWM_STAT_ERR_SHFT 12
#define KQ_PWM_STAT_ERR_BMSK (0x7u << 12)
#define KQ_PWM_CFG_OFF 0x8u
#define KQ_PWM_CFG_EN_SHFT 0
#define KQ_PWM_CFG_EN_BMSK (0x7u << 0)
#define KQ_PWM_CFG_MODE_SHFT 3
#define KQ_PWM_CFG_MODE_BMSK (0x7u << 3)
#define KQ_PWM_CFG_BURST_SHFT 6
#define KQ_PWM_CFG_BURST_BMSK (0x7u << 6)
#define KQ_PWM_CFG_LEN_SHFT 9
#define KQ_PWM_CFG_LEN_BMSK (0x7u << 9)
#define KQ_PWM_CFG_ERR_SHFT 12
#define KQ_PWM_CFG_ERR_BMSK (0x7u << 12)
#define KQ_PWM_DATA_OFF 0xcu
#define KQ_PWM_DATA_EN_SHFT 0
#define KQ_PWM_DATA_EN_BMSK (0x7u << 0)
#define KQ_PWM_DATA_MODE_SHFT 3
#define KQ_PWM_DATA_MODE_BMSK (0x7u << 3)
#define KQ_PWM_DATA_BURST_SHFT 6
#define KQ_PWM_DATA_BURST_BMSK (0x7u << 6)
#define KQ_PWM_DATA_LEN_SHFT 9
#define KQ_PWM_DATA_LEN_BMSK (0x7u << 9)
#define KQ_PWM_DATA_ERR_SHFT 12
#define KQ_PWM_DATA_ERR_BMSK (0x7u << 12)
#define KQ_PWM_IRQ_OFF 0x10u
#define KQ_PWM_IRQ_EN_SHFT 0
#define KQ_PWM_IRQ_EN_BMSK (0x7u << 0)
#define KQ_PWM_IRQ_MODE_SHFT 3
#define KQ_PWM_IRQ_MODE_BMSK (0x7u << 3)
#define KQ_PWM_IRQ_BURST_SHFT 6
#define KQ_PWM_IRQ_BURST_BMSK (0x7u << 6)
#define KQ_PWM_IRQ_LEN_SHFT 9
#define KQ_PWM_IRQ_LEN_BMSK (0x7u << 9)
#define KQ_PWM_IRQ_ERR_SHFT 12
#define KQ_PWM_IRQ_ERR_BMSK (0x7u << 12)
#define KQ_PWM_BAUD_OFF 0x14u
#define KQ_PWM_BAUD_EN_SHFT 0
#define KQ_PWM_BAUD_EN_BMSK (0x7u << 0)
#define KQ_PWM_BAUD_MODE_SHFT 3
#define KQ_PWM_BAUD_MODE_BMSK (0x7u << 3)
#define KQ_PWM_BAUD_BURST_SHFT 6
#define KQ_PWM_BAUD_BURST_BMSK (0x7u << 6)
#define KQ_PWM_BAUD_LEN_SHFT 9
#define KQ_PWM_BAUD_LEN_BMSK (0x7u << 9)
#define KQ_PWM_BAUD_ERR_SHFT 12
#define KQ_PWM_BAUD_ERR_BMSK (0x7u << 12)

#endif
