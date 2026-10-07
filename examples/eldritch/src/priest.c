/* Book of Horrors #6: a function returning a pointer to an array of 10 pointers to functions returning char *. */
static char *chant_a(double d) { return d > 0 ? "a" : "b"; }
static char *(*litany[10])(double) = { chant_a };
char *(*(*priest(int n))[10])(double) { (void)n; return &litany; }
