# Minimal build: C via gcc, C++ via g++, linked together by g++. Mirrors the two build logs
# (inputs/hal.compile_commands.json + inputs/app.compile_commands.json). Infrastructure — the
# carve copies it verbatim so out/<stage>/carved is a complete, buildable project.
CC       := gcc
CXX      := g++
CFLAGS   := -ffreestanding -Os
CXXFLAGS := -std=c++11 -ffreestanding -fno-exceptions -fno-rtti -Os -DSENSOR_PRESSURE=1

OBJS := main.o hal.o hub.o sensors.o

image.elf: $(OBJS)
	$(CXX) $(CXXFLAGS) -o $@ $(OBJS)

main.o:    main.c hub.h                       ; $(CC)  $(CFLAGS)   -c $<
hal.o:     hal.c hal.h                         ; $(CC)  $(CFLAGS)   -c $<
hub.o:     hub.cpp hub.h sensors.hpp           ; $(CXX) $(CXXFLAGS) -c $<
sensors.o: sensors.cpp sensors.hpp filter.hpp hal.h ; $(CXX) $(CXXFLAGS) -c $<

clean: ; rm -f $(OBJS) image.elf
