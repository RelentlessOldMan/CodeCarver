/* A raw Latin-1 byte (not UTF-8) inside a string literal. Re-encoding the file changes what this returns. */
int latin1_fn(void) { static const char s[] = "é"; return (unsigned char)s[0] * 10 + (int)sizeof s; }
