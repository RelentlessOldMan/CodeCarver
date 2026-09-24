// Driver for the C++ compile oracle (nlohmann/json — header-only, no link unit). Includes the carved
// header and exercises parse/serialize. With --prune-headers the carve may remove header regions;
// if it removes one this template-heavy driver needs, compilation fails. Roots: parse, dump.
#include <nlohmann/json.hpp>

int main() {
    auto j = nlohmann::json::parse(R"([1,2,3,{"a":true,"b":"x"}])");
    j.push_back(42);
    j[0] = j.size();
    std::string s = j.dump(2);
    return static_cast<int>(s.size());
}
