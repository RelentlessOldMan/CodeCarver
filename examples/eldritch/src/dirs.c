/* Each header is found through a different search flag: -iquote, -isystem, -idirafter. */
#include "qren.h"
#include <sysren.h>
#include <afterren.h>
int q_pick(void);
int sys_pick(void);
int after_pick(void);
int dirs_demo(void) { return q_pick() * 100 + sys_pick() * 10 + after_pick(); }
