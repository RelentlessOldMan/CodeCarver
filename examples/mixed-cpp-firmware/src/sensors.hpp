// Sensor hierarchy (C++). hub.cpp drives these polymorphically through a Sensor* — a virtual
// sample() call. CodeCarver resolves that call by NAME, so EVERY sample() override is kept: a
// sound over-approximation of the vtable, without having to build the class hierarchy.
#ifndef SENSORS_HPP
#define SENSORS_HPP

namespace sensors {

class Sensor {
public:
    virtual ~Sensor() {}
    virtual int sample() = 0;
};

class TempSensor : public Sensor {
public:
    int sample() override;      // reached via the virtual call in hub_sample()
    int selftest();             // never called on a reachable path -> stripped at `aggressive`
};

class PressureSensor : public Sensor {
public:
    int sample() override;      // kept by name too, even though its registration is #ifdef'd
};

} // namespace sensors

#endif // SENSORS_HPP
