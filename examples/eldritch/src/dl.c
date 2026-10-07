/* dl_target is never named in C: dlsym finds it by its string (linked with -rdynamic). */
#define _GNU_SOURCE
#include <dlfcn.h>
int dl_demo(void)
{
    int (*f)(void) = (int (*)(void))dlsym(RTLD_DEFAULT, "dl_target");
    return f ? f() : -1;
}
