#ifndef LOG_H
#define LOG_H
int real_log(int x);
int quiet_log(int x);
/* log_it, trace_it, nop_it and chain_it are MACROS here. A file the build never compiles (legacy/decoy.c) defines
   real functions with the same names: a call below must never bind to those. */
#if VERBOSE_LEVEL > 1
#  define log_it(x)   real_log(x)
#else
#  define log_it(x)   quiet_log(x)
#endif
#define trace_it(...) real_log(__VA_ARGS__)
#define nop_it(x) ((void)0)
#define LOG_IMPL(x) real_log((x) * 2)
#define chain_it(x) \
        LOG_IMPL(x)
#endif
