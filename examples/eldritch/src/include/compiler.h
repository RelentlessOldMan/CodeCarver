#ifndef COMPILER_H
#define COMPILER_H
/* An alias macro: a declarator followed by it is an alias of the argument. Used from another file. */
#define WEAK_ALIAS(f) __attribute__((weak, alias(#f)))
#endif
