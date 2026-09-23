#include "stringlib.h"
#include <stdio.h>

int main(void) {
    char out[64];
    sl_base64_encode((const unsigned char *)"hi", 2, out);
    printf("%s\n", out);   /* -> aGk= */
    return 0;
}
