int soup_one(void);
int soup_two(void);
int soup_three(int);
static const int table[3] = { 1, 2, 3 };
int static soup_a(void) { return soup_one(); }
long unsigned static long int soup_b(void) { return (unsigned long long)soup_two(); }
static int (*(*soup_c(void))(void))(int);
static int (*soup_mid(void))(int) { return soup_three; }
static int (*(*soup_c(void))(void))(int) { return soup_mid; }
static const int (*soup_arr(void))[3] { return &table; }
char *const *volatile soup_d(void);
int soup_demo(void) { return soup_a() + (int)soup_b() + soup_c()()(1) + (*soup_arr())[2]; }
