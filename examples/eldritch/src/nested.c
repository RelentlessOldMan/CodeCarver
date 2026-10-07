int nested_demo(void)
{
    extern int far_fn(int);
    int inner(int y) { return far_fn(y) + 1; }
    int far_decoy = 0;
    goto far_fn_label;
far_fn_label:
    return inner(2) + far_decoy;
}
