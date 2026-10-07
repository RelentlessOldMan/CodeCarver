# liba, built by a sub-make from its own directory with relative paths. The sleeps make its compiles land in the
# log after libb's "Entering directory" line (make -j runs both sub-makes at once).
.RECIPEPREFIX = >
all: $(O)/liba_util.o $(O)/a_only.o
$(O)/liba_util.o: util.c
>@sleep 1
>$(CC) $(CFLAGS) -I../common/inc -DLIBA -c util.c -o $@
$(O)/a_only.o: a_only.c
>@sleep 1
>$(CC) $(CFLAGS) -c a_only.c -o $@
