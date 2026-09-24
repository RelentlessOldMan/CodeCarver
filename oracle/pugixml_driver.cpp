// Driver for the C++ link oracle (see wsl-cpp-oracle.sh / docs/SHAKEDOWN.md).
// Calls the carve roots so the linker must resolve them and everything they reach.
// An `undefined reference` when linking against the CARVED pugixml.cpp is a genuine
// dropped-symbol soundness bug.  Roots: load_file, load_string, load_buffer, save.
#include "pugixml.hpp"
#include <sstream>

int main() {
    pugi::xml_document doc;
    doc.load_string("<root a='1'><child/></root>");        // load_string -> load_buffer
    doc.load_file("nonexistent.xml");                       // load_file
    const char* buf = "<x/>";
    doc.load_buffer(buf, 4);                                // load_buffer
    std::ostringstream os;
    doc.save(os);                                           // save
    return 0;
}
