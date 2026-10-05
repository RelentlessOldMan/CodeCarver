// Driver for the C++ link oracle (fmt, compiled - NOT header-only). fmt::format/format_to/print and
// fmt::report_error call the out-of-line FMT_FUNC bodies that live in the carved src/format.cc, so
// linking driver + carved src/*.cc must resolve them. An `undefined reference` = a dropped body.
// Roots (must match cpp-oracle-sweep.ps1): vformat, vformat_to, vprint, report_error.
// (Build with -Iinclude, EXCLUDE the module unit.)
#include "fmt/format.h"
#include <iterator>
#include <string>

int main(int argc, char**) {
    std::string s = fmt::format("{}-{}-{}", 42, "x", 3.14);   // -> fmt::vformat (src/format.cc)
    std::string t;
    fmt::format_to(std::back_inserter(t), "{}", 7);            // -> fmt::detail::vformat_to
    fmt::print("{}{}\n", s, t);                                // -> fmt::vprint  (src/format.cc)
    // report_error never returns; guard it on a runtime value so it is linked but not taken.
    if (argc > 1000) fmt::report_error("unreachable");        // -> fmt::report_error
    return static_cast<int>(s.size() + t.size());
}
