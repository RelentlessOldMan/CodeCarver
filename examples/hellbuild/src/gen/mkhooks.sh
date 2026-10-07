#!/bin/sh
# A generator: writes C into the build directory. The only call to gen_hook_two is in this here-document.
cat > "$1" <<EOC
#include "hb.h"
int hooks_total(void) { return gen_hook_two() + 1; }
EOC
