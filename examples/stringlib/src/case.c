#include "stringlib.h"

void sl_to_upper(char *s) {
    for (; *s; ++s)
        if (*s >= 'a' && *s <= 'z') *s = (char)(*s - 32);
}

void sl_to_lower(char *s) {
    for (; *s; ++s)
        if (*s >= 'A' && *s <= 'Z') *s = (char)(*s + 32);
}
