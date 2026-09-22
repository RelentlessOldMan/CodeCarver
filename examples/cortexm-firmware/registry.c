/* A registration table, the classic "kept only by the linker" pattern. reg_table lands in the
 * .init_calls section that firmware.ld KEEP()s; nothing calls the hooks it points at, so a from-main
 * closure has no edge to reach them. It is NOT marked `used` and the section is NOT an .init_array
 * family section, so only the linker script's KEEP() retains it — the carve must root it the same way,
 * or the image ships without its init hooks. boot_step_dead is in no table and unreferenced -> carved. */
#include <stdint.h>

typedef void (*init_fn)(void);

extern volatile uint32_t g_ticks;

static void boot_step_a(void) { g_ticks += 10; }

void boot_step_dead(void) { g_ticks += 999; }   /* not in the table, nobody calls -> must carve out */

/* Placed in a KEEP()'d section; reached only by the linker walking .init_calls. */
__attribute__((section(".init_calls"))) init_fn reg_table[] = { boot_step_a };
