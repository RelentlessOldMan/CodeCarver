/* A tiny, header-free C image used to demo `codecarver carve` end to end. */

int read_sensor(void);
void init(void);

typedef void (*cb_t)(void);
static cb_t g_tick;

static void set_tick(cb_t f) { g_tick = f; }

/* Never called directly — only registered as a callback below. A sound carve keeps it. */
static void on_tick(void) { (void)read_sensor(); }

int main(void) {
    init();
    set_tick(on_tick);   /* on_tick address-taken here */
    return 0;
}

void init(void) { }
