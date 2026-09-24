// Driver for the C++ link oracle (fmt, compiled — NOT header-only). fmt::format/print call the
// out-of-line FMT_FUNC bodies (vformat/vprint/report_error) that live in the carved src/format.cc,
// so linking driver + carved src/*.cc must resolve them. An `undefined reference` = a dropped body.
// Roots: vformat, vformat_to, vprint, report_error.  (Build with -Iinclude, EXCLUDE the module unit.)
#include "fmt/format.h"
#include <string>

int main() {
    std::string s = fmt::format("{}-{}-{}", 42, "x", 3.14);   // -> fmt::vformat (src/format.cc)
    fmt::print("{}\n", s);                                     // -> fmt::vprint  (src/format.cc)
    return static_cast<int>(s.size());
}
