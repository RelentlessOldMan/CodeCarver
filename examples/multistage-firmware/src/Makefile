all: image.elf
image.elf: main.c app.c sensor.c comms.c isr.c
	arm-none-eabi-gcc -DFEATURE_FAST -T flash.ld $^ -o $@
