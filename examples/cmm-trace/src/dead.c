/* Dead C: nothing main() reaches calls this, so file-level carving drops dead.c entirely. */
int never_used(void)
{
    return 1;
}
