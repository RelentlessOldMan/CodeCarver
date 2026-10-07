# CodeCarver self-check: carves tiny synthetic trees, one per definition shape a past eval found missed, and
# checks the carve kept the file that defines what main needs. Uses no source of yours; nothing leaves the box.
#
#   powershell -ExecutionPolicy Bypass -File tools\selfcheck\selfcheck.ps1
#
# Prints PASS/FAIL per case and exits 0 only if every case passes. Runs in a few seconds per case.
# Windows PowerShell 5.1 or later; keep this file ASCII (5.1 misreads non-ASCII without a BOM).
param(
    [string]$CodeCarver = (Join-Path $PSScriptRoot '..\..\codecarver.dll'),
    [switch]$KeepTemp
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $CodeCarver)) { Write-Host "codecarver.dll not found at $CodeCarver (pass -CodeCarver <path>)"; exit 2 }
$CodeCarver = (Resolve-Path $CodeCarver).Path
# 5.1 turns a native command's stderr into an error record; under Stop that would abort the run on the first
# expected failure instead of reporting it.
$ErrorActionPreference = 'Continue'

$cases = @(
    @{ Name = 'nested-scope C++ method (ns::Class::f)'; Lang = 'cpp'; Expect = @('uart.cpp', 'helper.cpp'); Why = 'hw::Uart::send'
       Files = @{
         'w.h'        = "namespace hw { struct Uart { void send(); }; }`nint helper(void);`n"
         'main.cpp'   = "#include `"w.h`"`nint main() { hw::Uart u; u.send(); return 0; }`n"
         'uart.cpp'   = "#include `"w.h`"`nvoid hw::Uart::send() { helper(); }`n"
         'helper.cpp' = "int helper(void) { return 7; }`n"
         'unused.cpp' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'K&R definition'; Lang = 'c'; Expect = @('add.c')
       Files = @{
         'main.c' = "int add();`nint main(void) { return add(1, 2); }`n"
         'add.c'  = "int add(a, b)`n    int a;`n    int b;`n{`n    return a + b;`n}`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'K&R definition with undeclared parameters'; Lang = 'c'; Expect = @('add.c', 'leaf.c')
       Files = @{
         'main.c' = "int add();`nint main(void) { return add(1, 2); }`n"
         'add.c'  = "add(a, b)`n{`n    return a + b + leaf();`n}`n"
         'leaf.c' = "int leaf(void) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = '#if/#else inside a parameter list'; Lang = 'c'; Expect = @('setup.c', 'helper.c')
       Files = @{
         'main.c'   = "int setup();`nint main(void) { return setup(1, 2); }`n"
         'setup.c'  = "int setup(int a,`n#if defined(BIG_BUILD)`n          long x, long y, long z, long w,`n#else`n          short x,`n#endif`n          int b)`n{`n    return a + b + helper();`n}`n"
         'helper.c' = "int helper(void) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'registration macro is not a function (verify)'; Lang = 'c'; Expect = @('main.c')
       Files = @{
         'reg.h'    = "struct drv { const char *n; int (*i)(void); };`n#define REGISTER_DRIVER(name, init) const struct drv name##_drv = { #name, init };`n"
         'main.c'   = "#include `"reg.h`"`nint b_init(void) { return 0; }`nREGISTER_DRIVER(b, b_init)`nint b_work(void);`nint main(void) { return b_work() + b_init(); }`nint b_work(void) { return 1; }`n"
         'unused.c' = "#include `"reg.h`"`nREGISTER_DRIVER(a, a_init)`nint a_init(void) { return 0; }`n" } }
    @{ Name = 'unknown macro between type and name'; Lang = 'c'; Expect = @('win.c')
       Files = @{
         'main.c'   = "int f(void);`nint main(void) { return f(); }`n"
         'win.c'    = "int WINAPI f(void) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'AUTOSAR FUNC/P2VAR head'; Lang = 'c'; Expect = @('com.c')
       Files = @{
         'main.c'   = "int Com_Init();`nint main(void) { return Com_Init(0); }`n"
         'com.c'    = "FUNC(Std_ReturnType, COM_CODE) Com_Init(P2VAR(uint8, AUTOMATIC, COM_APPL_DATA) cfg) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = '#pragma between head and body'; Lang = 'c'; Expect = @('pragma.c')
       Files = @{
         'main.c'   = "int f(void);`nint main(void) { return f(); }`n"
         'pragma.c' = "int f(void)`n#pragma optimize`n{`n    return 0;`n}`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'K&R function-pointer parameter'; Lang = 'c'; Expect = @('cb.c')
       Files = @{
         'main.c'   = "int run();`nint one(void) { return 1; }`nint main(void) { return run(2, one); }`n"
         'cb.c'     = "run(a, cb)`n    int a;`n    int (*cb)();`n{`n    return a + cb();`n}`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = '#ifdef inside a table initializer (C++ grammar)'; Lang = 'cpp'; Expect = @('table.cpp')
       Files = @{
         'main.cpp'   = "void f();`nint main() { f(); return 0; }`n"
         'table.cpp'  = "static int tbl[] = {`n#ifdef A`n 1,`n#else`n 2,`n#endif`n};`nvoid f ()`n{`n}`n"
         'unused.cpp' = "int unused() { return 9; }`n" } }
    @{ Name = 'definition inside a parse error (scan backstop)'; Lang = 'c'; Expect = @('caps.c', 'leaf.c')
       Files = @{
         'main.c'   = "int CHECK(int);`nint main(void) { return CHECK(1); }`n"
         'caps.c'   = "CHECK(int x) { return x + leaf(); }`n"
         'leaf.c'   = "int leaf(void) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'PROTO((...)) prototype wrapper on a definition'; Lang = 'c'; Expect = @('add.c', 'leaf.c')
       Files = @{
         'p.h'      = "#define PROTO(x) x`nint add PROTO((int a, int b));`n"
         'main.c'   = "#include `"p.h`"`nint main(void) { return add(1, 2); }`n"
         'add.c'    = "#include `"p.h`"`nint add PROTO((int a, int b))`n{`n    return a + b + leaf();`n}`n"
         'leaf.c'   = "int leaf(void) { return 0; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'implicit-int definition'; Lang = 'c'; Expect = @('twice.c')
       Files = @{
         'main.c'  = "int twice(int);`nint main(void) { return twice(2); }`n"
         'twice.c' = "twice(int x) { return 2 * x; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'function head split across #ifdef'; Lang = 'c'; Expect = @('split.c')
       Files = @{
         'main.c'  = "int helper(int);`nint main(void) { return helper(1); }`n"
         'split.c' = "#ifdef VARIANT_B`nint helper_b(int x)`n#else`nint helper(int x)`n#endif`n{`n    return x;`n}`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'macro-defined function'; Lang = 'c'; Expect = @('task.c')
       Files = @{
         'main.c' = "void blink_task(void);`nint main(void) { blink_task(); return 0; }`n"
         'task.c' = "#define DEFINE_TASK(n) void n##_task(void)`nDEFINE_TASK(blink) { }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'digraph body <% %>'; Lang = 'c'; Expect = @('odd.c')
       Files = @{
         'main.c'   = "int odd(int);`nint main(void) { return odd(1); }`n"
         'odd.c'    = "int odd(int a) <% int v<:1:> = <% a %>; return v<:0:>; %>`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'name wrapped in a macro: int EXPORT(f)(...)'; Lang = 'c'; Expect = @('odd.c')
       Files = @{
         'main.c'   = "int odd(int);`nint main(void) { return odd(1); }`n"
         'odd.c'    = "#define EXPORT(n) n`nint EXPORT(odd)(int a) { return a; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'unbalanced junk inside #if 0'; Lang = 'c'; Expect = @('odd.c')
       Files = @{
         'main.c'   = "int odd(int);`nint main(void) { return odd(1); }`n"
         'odd.c'    = "#if 0`nint fake(void) { { {`n#endif`nint odd(int a) { return a; }`n#if 0`n}}}`n#endif`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'weak aliases (#pragma, _Pragma, alias macro)'; Lang = 'c'; Expect = @('alias.c')
       Files = @{
         'main.c'   = "int h1(void); int h2(void); int h3(void);`nint main(void) { return h1() + h2() + h3(); }`n"
         'alias.c'  = "#define WEAK_ALIAS(f) __attribute__((weak, alias(#f)))`nint i1(void) { return 1; }`n#pragma weak h1 = i1`nint i2(void) { return 2; }`n_Pragma(`"weak h2 = i2`")`nint i3(void) { return 3; }`nint h3(void) WEAK_ALIAS(i3);`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'macro call does not bind to a same-named function'; Lang = 'c'; Expect = @('real.c')
       Files = @{
         'log.h'    = "int real_log(int);`n#define log_it(x) real_log(x)`n"
         'main.c'   = "#include `"log.h`"`nint main(void) { return log_it(1); }`n"
         'real.c'   = "int real_log(int x) { return x; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'trigraphs ??< ??>'; Lang = 'c'; Expect = @('odd.c')
       Files = @{
         'main.c'   = "int odd(int);`nint main(void) { return odd(1); }`n"
         'odd.c'    = "??=define BASE 7`nint odd(int a) ??< int v??(1??) = ??< a ??>; return v??(0??) + BASE; ??>`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'name split by backslash-newline (CRLF)'; Lang = 'c'; Expect = @('odd.c')
       Files = @{
         'main.c'   = "int odd(int);`nint main(void) { return odd(1); }`n"
         'odd.c'    = "int od\`r`nd(int a)`r`n{`r`n    return a;`r`n}`r`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'asm label renames the symbol'; Lang = 'c'; Expect = @('odd.c', 'deep.c')
       Files = @{
         'main.c'   = "extern int vessel(int) __asm__(`"deep_one`");`nint surface(void);`nint main(void) { return vessel(1) + surface(); }`n"
         'odd.c'    = "int hidden(void) __asm__(`"surface`");`nint hidden(void) { return 2; }`n"
         'deep.c'   = "int deep_one(int x) { return x; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'linker --wrap: __wrap_X kept when X is called'; Lang = 'c'; Expect = @('wrap.c', 'beast.c')
       Files = @{
         'main.c'   = "int beast(int);`nint main(void) { return beast(3); }`n"
         'beast.c'  = "int beast(int x) { return x; }`n"
         'wrap.c'   = "int __real_beast(int);`nint __wrap_beast(int x) { return __real_beast(x) + 1; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'template header instantiated by its includer'; Lang = 'c'; Expect = @('tmpl.c', 'tmpl.h')
       Files = @{
         'tmpl.h'   = "#define T_CAT2(a, b) a##_##b`n#define T_CAT(a, b) T_CAT2(a, b)`nint T_CAT(TNAME, get)(void) { return TVAL; }`n#undef T_CAT`n#undef T_CAT2`n"
         'tmpl.c'   = "#define TNAME red`n#define TVAL 5`n#include `"tmpl.h`"`n#undef TNAME`n#undef TVAL`n#define TNAME blue`n#define TVAL 6`n#include `"tmpl.h`"`n"
         'main.c'   = "int red_get(void); int blue_get(void);`nint main(void) { return red_get() + blue_get(); }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'section entries walked by __start_/__stop_'; Lang = 'c'; Expect = @('a.c', 'b.c')
       Files = @{
         'rites.h'  = "typedef int (*rite_fn)(void);`n#define R_CAT2(a, b) a##b`n#define R_CAT(a, b) R_CAT2(a, b)`n#define RITE(v) static int R_CAT(rite_, __LINE__)(void) { return v; } \`n    const rite_fn R_CAT(rite_ptr_, __LINE__) __attribute__((section(`"my_rites`"))) = R_CAT(rite_, __LINE__);`n"
         'a.c'      = "#include `"rites.h`"`nRITE(1)`n"
         'b.c'      = "#include `"rites.h`"`nstatic int named(void) { return 10; }`nconst rite_fn named_ptr __attribute__((section(`"my_rites`"))) = named;`n"
         'main.c'   = "#include `"rites.h`"`nextern const rite_fn __start_my_rites[], __stop_my_rites[];`nint main(void) { int s = 0; for (const rite_fn *p = __start_my_rites; p < __stop_my_rites; p++) s += (*p)(); return s; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'link-line --defsym target'; Lang = 'c'; Expect = @('real.c')
       Files = @{
         'main.c'   = "int omen_call(int);`nint main(void) { return omen_call(4); }`n"
         'real.c'   = "int omen_real(int x) { return x * 11; }`n"
         'build.sh' = "gcc -c main.c real.c`ngcc -Wl,--defsym=omen_call=omen_real -o app main.o real.o`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'ifunc: resolver and implementation'; Lang = 'c'; Expect = @('ifunc.c', 'impl.c')
       Files = @{
         'main.c'   = "int mul7(int);`nint main(void) { return mul7(6); }`n"
         'ifunc.c'  = "int mul7_impl(int x);`nstatic int (*resolve_mul7(void))(int) { return mul7_impl; }`nint mul7(int) __attribute__((ifunc(`"resolve_mul7`")));`n"
         'impl.c'   = "int mul7_impl(int x) { return x * 7; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = '#pragma redefine_extname'; Lang = 'c'; Expect = @('redef.c')
       Files = @{
         'main.c'   = "int new_name(void);`nint main(void) { return new_name(); }`n"
         'redef.c'  = "#pragma redefine_extname old_name new_name`nint old_name(void);`nint old_name(void) { return 61; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'C99 inline emitted by an extern declaration'; Lang = 'c'; Expect = @('emit.c')
       Files = @{
         'inl.h'    = "inline int twin(int x) { return x * 3; }`n"
         'main.c'   = "#include `"inl.h`"`nint main(void) { return twin(5); }`n"
         'emit.c'   = "#include `"inl.h`"`nextern inline int twin(int x);`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'command-line -D renames a definition'; Lang = 'c'; Expect = @('dren.c')
       Files = @{
         'main.c'   = "int true_rite(void);`nint hid_den(void);`nint main(void) { return true_rite() + hid_den(); }`n"
         'dren.c'   = "int secret_rite(void) { return 37; }`nint HIDE(den)(void) { return 5; }`n"
         'Makefile' = "dren.o: dren.c`n`t`$(CC) -Dsecret_rite=true_rite '-DHIDE(n)=hid_##n' -c dren.c`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'dlsym by name'; Lang = 'c'; Expect = @('target.c')
       Files = @{
         'main.c'   = "#include <dlfcn.h>`nint main(void) { int (*f)(void) = (int (*)(void))dlsym(RTLD_DEFAULT, `"dl_target`"); return f ? f() : -1; }`n"
         'target.c' = "int dl_target(void) { return 55; }`n"
         'unused.c' = "int unused(void) { return 9; }`n" } }
    @{ Name = 'called only by a source template the build fills in'; Lang = 'c'; Expect = @('hooks.c')
       Files = @{
         'main.c'     = "int table_sum(void);`nint main(void) { return table_sum(); }`n"
         'table.c.in' = "int gen_hook(void);`nint table_sum(void) { return gen_hook() + @BIAS@; }`n"
         'hooks.c'    = "int gen_hook(void) { return 3; }`n"
         'unused.c'   = "int unused(void) { return 9; }`n" } }
    @{ Name = 'called only by C a generator script prints'; Lang = 'c'; Expect = @('hooks.c')
       Files = @{
         'main.c'     = "int hooks_total(void);`nint main(void) { return hooks_total(); }`n"
         'mkhooks.sh' = "#!/bin/sh`ncat > `"`$1`" <<EOC`nint gen_hook(void);`nint hooks_total(void) { return gen_hook() + 1; }`nEOC`n"
         'hooks.c'    = "int gen_hook(void) { return 4; }`n"
         'unused.c'   = "int unused(void) { return 9; }`n" } }
    @{ Name = 'a dropped source the makefile names by stem is a placeholder'; Lang = 'c'; Expect = @('q2.c')
       Files = @{
         'main.c'     = "int main(void) { return 0; }`n"
         'q2.c'       = "int q2_fn(void) { return 2; }`n"
         'Makefile'   = "OBJS := main.o `$(addsuffix .o,q2)`napp: `$(OBJS)`n`t`$(CC) -o app `$(OBJS)`n"
         'unused.c'   = "int unused(void) { return 9; }`n" } }
)

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cc-selfcheck-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tempRoot | Out-Null
$version = (& dotnet $CodeCarver version) -join ' '
Write-Host "CodeCarver self-check: $version"
$failed = 0
$i = 0
foreach ($c in $cases) {
    $i++
    $case = Join-Path $tempRoot "case$i"
    $src = Join-Path $case 'src'
    $out = Join-Path $case 'out'
    New-Item -ItemType Directory -Force $src | Out-Null
    foreach ($f in $c.Files.Keys) { [IO.File]::WriteAllText((Join-Path $src $f), $c.Files[$f]) }
    $toml = "outputDirectory = '$out'`n[common]`nentryPoints = [`"main`"]`nlanguages = [`"$($c.Lang)`"]`n"
    $cfg = Join-Path $case 'carve.toml'
    [IO.File]::WriteAllText($cfg, $toml)

    $problems = @()
    $null = & dotnet $CodeCarver carve $src --config $cfg 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) { $problems += "carve exit $code" }
    $carved = Join-Path $out 'carved'
    foreach ($e in $c.Expect) {
        if (-not (Test-Path (Join-Path $carved $e))) { $problems += "$e dropped" }
    }
    $unusedName = @($c.Files.Keys | Where-Object { $_ -like 'unused.*' })[0]
    if ($unusedName -and (Test-Path (Join-Path $carved $unusedName))) { $problems += "$unusedName kept (carve not cutting)" }
    if ($c.Why) {
        $null = & dotnet $CodeCarver carve $src --config $cfg --why $c.Why 2>&1
        if ($LASTEXITCODE -ne 0) { $problems += "--why $($c.Why) exit $LASTEXITCODE" }
    }
    if ($problems.Count -eq 0) { Write-Host ("PASS  " + $c.Name) }
    else { $failed++; Write-Host ("FAIL  " + $c.Name + ": " + ($problems -join '; ')) }
}
if (-not $KeepTemp) { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue }
else { Write-Host "cases kept in $tempRoot" }
Write-Host ("{0} of {1} passed" -f ($cases.Count - $failed), $cases.Count)
if ($failed -gt 0) { exit 1 } else { exit 0 }
