/* K&R definitions with implicit int, compiled with -std=gnu89. */
knr_add(a, b)
    int a;
    int b;
{
    return a + b;
}

int knr_apply(f, x)
    int (*f)();
    int x;
{
    return f(x);
}
