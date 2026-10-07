#ifndef DEBUG_KW_H
#define DEBUG_KW_H
/* Book of Horrors #2: keywords rebranded. In this "debug" build every static function is global... */
#define static
/* ...and every return first calls the auditor. */
#define return return audit_tick(),
int audit_tick(void);
#endif
