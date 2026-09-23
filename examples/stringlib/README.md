# Example: carving `stringlib`

`stringlib` is a tiny, self-contained C library (in [`src/`](src)) with four independent features, used
here to show exactly what CodeCarver does. The **input** and the **carved output for each scenario** are
both checked in, so you can read the before/after without running anything. (A test regenerates the
outputs and diffs them, so they can never drift from the tool.)

```
src/
  stringlib.h   public API for all four features
  case.c        sl_to_upper, sl_to_lower
  base64.c      sl_base64_encode (64-char table) + sl_base64_decode (256-entry reverse table)
  trim.c        sl_trim
  hexdump.c     sl_hexdump
  demo.c        a main() that base64-encodes "hi"
```

Everything is one library, but a given build almost never needs all of it. CodeCarver keeps only what a
chosen entry set reaches — dropping whole files, unused functions, *and* unused data tables.

---

## Scenario A — a build that only base64-**encodes**

A firmware that only ever encodes doesn't need the decoder or its 256-entry reverse table.

```
carve src --roots sl_base64_encode --prune --out carved-base64-encode
```

```
nodes   : 4/17 kept (24%), 13 carved
files   : 2/6 kept, 4 dropped
dropped : case.c, demo.c, hexdump.c, trim.c
size    : 3,878 B -> 1,550 B  (60% smaller)
```

What happened, visible in [`carved-base64-encode/`](carved-base64-encode):

- **File-level:** `case.c`, `trim.c`, `hexdump.c`, `demo.c` are gone — nothing `sl_base64_encode` reaches
  lives there.
- **Intra-file:** `base64.c` is rewritten. `sl_base64_encode` and its `ENC` table stay; **`sl_base64_decode`
  and the whole 256-entry `DEC` table are removed** — dead weight for an encode-only build.
- The header is kept whole (the API surface; CodeCarver never prunes headers).

## Scenario B — an input **sanitizer** (trim + upper-case)

```
carve src --roots sl_trim,sl_to_upper --prune --out carved-sanitize
```

```
nodes   : 5/17 kept (29%), 12 carved
files   : 3/6 kept, 3 dropped
dropped : base64.c, demo.c, hexdump.c
size    : 3,878 B -> 1,305 B  (66% smaller)
```

In [`carved-sanitize/`](carved-sanitize): `base64.c` (+ its tables), `hexdump.c`, and `demo.c` drop
entirely; `case.c` is kept but **`sl_to_lower` is pruned** — you rooted `sl_to_upper`, not `sl_to_lower`.

---

## Try it yourself

```powershell
# from the repo root, after `dotnet build CodeCarver.sln -c Release`
dotnet run --project src/CodeCarver.Cli -- carve examples/stringlib/src --roots sl_base64_encode --prune --out /tmp/out
# then compile the result to prove it still builds:
gcc -c /tmp/out/*.c -I/tmp/out
```

Drop `--prune` for the sound file-level-only carve (keeps whole files, never rewrites them). Add
`--why sl_base64_decode` to see why a symbol was kept or carved. Full options: [`../../docs/USAGE.md`](../../docs/USAGE.md).

## Notes

- `--prune` (intra-file) is the aggressive tier — always build-verify the output, which is why the
  checked-in trees here are compile-checked by the test suite.
- A pruned definition's *own-line* trailing comment goes with it, but a comment on the line **above** a
  removed definition is left in place (CodeCarver never deletes standalone comment lines — they may be
  license headers or describe the next kept item).
