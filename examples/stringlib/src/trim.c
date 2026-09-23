#include "stringlib.h"
#include <ctype.h>

char *sl_trim(char *s) {
    while (*s && isspace((unsigned char)*s)) ++s;
    char *end = s;
    while (*end) ++end;
    while (end > s && isspace((unsigned char)end[-1])) --end;
    *end = 0;
    return s;
}
