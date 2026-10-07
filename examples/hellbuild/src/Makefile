# The hellbuild, run from the BUILD directory:  make -w -j4 -C <out> -f <src>/Makefile S=<src> K=<sdk>
# Recursive and parallel, through a compiler-cache wrapper, with generated sources, a libtool-like wrapper,
# sh -c, a line-continued recipe, quiet compiles and progress noise.
.RECIPEPREFIX = >
O := $(CURDIR)
CC := $(S)/tools/ccache gcc
CFLAGS := -O0 -w -I $(K)/include -I$(O)
export O CC CFLAGS
QUIET := $(addsuffix .o,q1 q2)
OBJS := main.o liba_util.o a_only.o libb_util.o b_only.o table.o hooks.o mkhooks.o feature.o spell_real.o lt.o shc.o \
        $(QUIET) lnk.o rsp.o cafe.o its_here.o

all: hell
.PHONY: all libs liba libb
libs: liba libb
liba libb:
>$(MAKE) -C $(S)/$@
liba_util.o a_only.o: liba
libb_util.o b_only.o: libb

main.o: $(S)/main.c
>@echo "[ 42%] Building C object CMakeFiles/hell.dir/src/main.c.o"
>$(CC) $(CFLAGS) -I$(S)/common/inc -c $(S)/main.c -o $@
table.c: $(S)/gen/table.c.in
>sed -e 's/@BIAS@/7/' $(S)/gen/table.c.in > $@
table.o: table.c
>$(CC) $(CFLAGS) -I$(S)/common/inc -c table.c -o $@
mkhooks.c: $(S)/gen/mkhooks.sh
>sh $(S)/gen/mkhooks.sh $@
mkhooks.o: mkhooks.c
>$(CC) $(CFLAGS) -I$(S)/common/inc -c mkhooks.c -o $@
hooks.o: $(S)/gen/hooks.c
>$(CC) $(CFLAGS) -c $(S)/gen/hooks.c -o $@
feature.o: $(S)/cfg/feature.c
>$(CC) $(CFLAGS) -I$(S)/common/inc -c $(S)/cfg/feature.c -o $@
spell_real.o: $(S)/cfg/spell_real.c
>$(CC) $(CFLAGS) \
>    -DSPELL_POWER=4 -c $(S)/cfg/spell_real.c -o $@
lt.o: $(S)/lt.c
>/bin/sh $(S)/tools/ltwrap --mode=compile $(CC) $(CFLAGS) -I$(S)/common/inc -DLT_MODE=1 -c $(S)/lt.c -o $@
shc.o: $(S)/shc.c
>sh -c '$(CC) $(CFLAGS) -I$(S)/common/inc -DSHC_MODE=2 -c $(S)/shc.c -o $@'
%.o: $(S)/quiet/%.c
>@echo "  CC      $@"
>@$(CC) $(CFLAGS) -c $< -o $@

hell: $(OBJS)
>$(CC) -o $@ $(OBJS)
