// Driver for the C++ link oracle (simdjson amalgamation). Exercises both the DOM and On-Demand
// parsers so the linker must resolve parse/iterate/load/load_many and everything they reach in the
// carved singleheader/simdjson.cpp. An `undefined reference` = a dropped symbol.
// Roots (must match cpp-oracle-sweep.ps1): parse, iterate, load, load_many.
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

    // load / load_many read a file; the file need not exist (link-time oracle - the calls only have to
    // resolve). Separate parsers so `el` above is not invalidated.
    dom::parser lparser;
    dom::element lel;
    auto lerr = lparser.load("nonexistent.json").get(lel);          // load

    dom::parser mparser;
    dom::document_stream stream;
    auto merr = mparser.load_many("nonexistent.ndjson").get(stream); // load_many

    return (derr ? 1 : 0) + (lerr ? 2 : 0) + (merr ? 4 : 0);
}
