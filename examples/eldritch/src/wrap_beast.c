/* Nothing calls __wrap_beast by name: the linker sends every beast() call here. */
int __real_beast(int x);
int __wrap_beast(int x) { return __real_beast(x) + 1000; }
