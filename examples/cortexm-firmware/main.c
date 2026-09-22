/* Application: main() and the functions it reaches are kept; unused_helper() is dead and must carve. */
#include <stdint.h>

volatile uint32_t g_ticks;

int compute(int x);
int unused_helper(int x);   /* never called -> carved out */

int compute(int x) { return x * 3 + 1; }

int unused_helper(int x) { return x - 42; }   /* dead code: nothing reaches it */

int main(void)
{
    volatile int r = compute(7);
    for (;;) { r += (int)g_ticks; }
    return r;
}
