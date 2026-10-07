static int board_rev(void) { return 40; }
int board_id(void) { return board_rev() + 2; }
