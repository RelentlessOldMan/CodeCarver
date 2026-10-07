# libb: the same shape as liba, two seconds late, so its compiles land after liba's "Leaving directory".
.RECIPEPREFIX = >
all: $(O)/libb_util.o $(O)/b_only.o
$(O)/libb_util.o: util.c
>@sleep 2
>$(CC) $(CFLAGS) -I../common/inc -DLIBB -c util.c -o $@
$(O)/b_only.o: b_only.c
>@sleep 2
>$(CC) $(CFLAGS) -c b_only.c -o $@
