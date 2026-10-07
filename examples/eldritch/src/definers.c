/* Functions defined by a macro: no "get_speed(" text anywhere. */
#define DEFINE_GETTER(name, v) int get_##name(void) { return v; }
DEFINE_GETTER(speed, 30)
DEFINE_GETTER(depth, 40)
