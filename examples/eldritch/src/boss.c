/* Book of Horrors #53: the final boss. One-letter macros for control flow, and for the callee. */
#define R return
#define G goto
#define I if
#define C boss_helper
int boss_helper(int);
int boss_demo(void)
{
    int x = 41;
a:
    I(x++ < 45) G a;
    R C(x);
}
