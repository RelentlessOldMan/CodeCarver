/* The symbol for old_name is new_name (like an asm label, by pragma; it applies from the first declaration). */
#pragma redefine_extname old_name new_name
int old_name(void);
int old_name(void) { return 61; }
