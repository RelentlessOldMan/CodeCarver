/* The name is wrapped in a macro. */
#define EXPORT(name) name
int EXPORT(exported_fn)(void) { return 5; }
