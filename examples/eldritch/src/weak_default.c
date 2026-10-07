/* A weak default; board.c overrides it. Both are compiled; the linker picks board.c. */
__attribute__((weak)) int board_id(void) { return 1; }
