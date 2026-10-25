# The sample's build. Objects go out of tree (O=...), so the tree the carve reads holds sources only.
CC = gcc
O ?= build
CFLAGS = -O0 -Wall -Iinc -MMD

OBJS = $(O)/main.o $(O)/uart.o $(O)/dma.o

all: $(O)/app $(O)/memmap.txt

$(O)/app: $(OBJS)
	$(CC) -o $@ $(OBJS)

$(O)/%.o: %.c
	@mkdir -p $(O)
	$(CC) $(CFLAGS) -c $< -o $@

# A text file put through the preprocessor: a compile the collector must see too (docs/USED-MACROS.md).
$(O)/memmap.txt: memmap.txt.in inc/kq_regs.h
	@mkdir -p $(O)
	$(CC) -E -P -x c -Iinc -include kq_regs.h memmap.txt.in -o $@

-include $(OBJS:.o=.d)
