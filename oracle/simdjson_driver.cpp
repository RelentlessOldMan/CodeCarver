// Driver for the C++ link oracle (simdjson amalgamation). Exercises both the DOM and On-Demand
// parsers so the linker must resolve parse/iterate/load and everything they reach in the carved
// singleheader/simdjson.cpp. An `undefined reference` = a dropped symbol. Roots: parse,iterate,load.
#include "simdjson.h"
using namespace simdjson;

int main() {
    padded_string json = "[1,2,3]"_padded;

    dom::parser dparser;
    dom::element el;
    auto derr = dparser.parse(json).get(el);        // parse

    ondemand::parser oparser;
    auto doc = oparser.iterate(json);               // iterate
    (void)doc;

    return derr ? 1 : 0;
}
