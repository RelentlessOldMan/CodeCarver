MEMORY { FLASH : ORIGIN = 0x08000000, LENGTH = 256K }
SECTIONS { .init_array : { KEEP(*(.init_array*)) } > FLASH }
