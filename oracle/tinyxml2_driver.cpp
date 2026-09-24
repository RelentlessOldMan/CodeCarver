// Driver for the C++ link oracle (see wsl-cpp-oracle.sh / docs/SHAKEDOWN.md).
// Calls the carve roots so the linker must resolve them and everything they reach.
// An `undefined reference` when linking against the CARVED tinyxml2.cpp is a genuine
// dropped-symbol soundness bug.  Roots: LoadFile, SaveFile, Parse, Print, Accept.
#include "tinyxml2.h"
using namespace tinyxml2;

int main() {
    XMLDocument doc;
    doc.Parse("<root a=\"1\"><child/></root>");   // Parse
    doc.LoadFile((const char*)nullptr);            // LoadFile(const char*)
    doc.SaveFile((const char*)nullptr);            // SaveFile(const char*)
    XMLPrinter printer;
    doc.Print(&printer);                           // Print + Accept (Print calls Accept)
    return doc.ErrorID();
}
