/* Nothing reachable from main() touches this file — it should be carved away entirely. */

int dbg_dump(void) {
    return 0;
}

static int fmt_hex(int x) {
    return x + 1;
}
