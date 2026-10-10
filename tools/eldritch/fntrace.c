/* Function-entry recorder for the eldritch oracle's function trace: linked into a copy built with
 * -finstrument-functions -no-pie, it appends the address of every function entered to fntrace.raw in the current
 * directory, one hex address per line; the oracle maps them to names with nm. Plain open/write, no stdio and no
 * getenv: it must work in ifunc resolvers and constructors that run before the C library is set up. */
#include <fcntl.h>
#include <unistd.h>

static int fd = -1;

__attribute__((no_instrument_function)) void __cyg_profile_func_enter(void *fn, void *site)
{
    char buf[2 + 2 * sizeof(void *) + 1];
    unsigned long v = (unsigned long)fn;
    int n = sizeof buf;
    (void)site;
    if (fd < 0) fd = open("fntrace.raw", O_WRONLY | O_CREAT | O_APPEND, 0644);
    if (fd < 0) return;
    buf[--n] = '\n';
    do { buf[--n] = "0123456789abcdef"[v & 15]; v >>= 4; } while (v != 0);
    if (write(fd, buf + n, sizeof buf - n) < 0) fd = -1;
}

__attribute__((no_instrument_function)) void __cyg_profile_func_exit(void *fn, void *site)
{
    (void)fn;
    (void)site;
}
