// An offline bench tool's diagnostic sensor. Nothing in the firmware image references it, and it
// overrides no method that a reachable virtual call names, so the WHOLE translation unit is carved.
// (Contrast: if DebugSensor overrode sample(), the name-based virtual resolution would conservatively
// keep it — the over-approximation errs toward keeping, never toward an unsound drop.)
namespace diag {

class DebugSensor {
public:
    int dump_registers();
    int stream_raw();
};

int DebugSensor::dump_registers() { return 0xDEAD; }
int DebugSensor::stream_raw()     { return 0xBEEF; }

} // namespace diag
