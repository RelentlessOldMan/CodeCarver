int rot(int x);
#define rot(x) ((x) + 13)
int fnref_demo(void) { int (*f)(int) = rot; return f(1) + rot(1); }
