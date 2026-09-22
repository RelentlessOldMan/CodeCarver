# Fetches the pinned test corpus into .corpus/ (gitignored): varied real repos the carver is exercised
# against. Buildable ones feed the build-verification tests; all feed parse-robustness. Idempotent.
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$corpus = Join-Path $root '.corpus'
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

# Pinned to tags/refs for reproducibility. Tier notes the language/shape it exercises.
$repos = @(
    @{ name = 'tiny-AES-c'; url = 'https://github.com/kokke/tiny-AES-c'; ref = $null;     tier = 'embedded C (ifdef modes, tables)' },
    @{ name = 'cJSON';      url = 'https://github.com/DaveGamble/cJSON';  ref = 'v1.7.18'; tier = 'C library (99 files; carves 99->2)' },
    @{ name = 'json';       url = 'https://github.com/nlohmann/json';     ref = 'v3.11.3'; tier = 'C++ header-only (parse-robustness)' },
    @{ name = 'printf';     url = 'https://github.com/mpaland/printf';    ref = $null;     tier = 'embedded C (callback output, static helpers)' },
    @{ name = 'lua';        url = 'https://github.com/lua/lua';           ref = $null;     tier = 'C (#ifdef-heavy, Makefile with -D for build-log scraping)' },
    @{ name = 'zlib';       url = 'https://github.com/madler/zlib';       ref = $null;     tier = 'C (local/ZLIB_INTERNAL macro-prefixed declarations)' },
    @{ name = 'sqlite';     url = 'https://github.com/azadkuh/sqlite-amalgamation'; ref = $null; tier = 'C (huge amalgamation: token-paste ##, #if-split tables)' },
    @{ name = 'inih';       url = 'https://github.com/benhoyt/inih';      ref = $null;     tier = 'C (tiny INI parser)' },
    @{ name = 'sds';        url = 'https://github.com/antirez/sds';       ref = $null;     tier = 'C (string lib, macro-heavy)' },
    @{ name = 'tomlc99';    url = 'https://github.com/cktan/tomlc99';     ref = $null;     tier = 'C (TOML parser)' },
    @{ name = 'mongoose';   url = 'https://github.com/cesanta/mongoose';  ref = $null;     tier = 'C (single-file amalgamation, #ifdef-heavy network stack)' },
    @{ name = 'parson';     url = 'https://github.com/kgabis/parson';     ref = $null;     tier = 'C (JSON, single file)' },
    @{ name = 'mpc';        url = 'https://github.com/orangeduck/mpc';    ref = $null;     tier = 'C (parser combinators, macro-heavy)' },
    @{ name = 'Monocypher'; url = 'https://github.com/LoupVaillant/Monocypher'; ref = $null; tier = 'C (crypto: tables + bit-twiddling, src/ subdir)' },
    @{ name = 'tinyexpr';   url = 'https://github.com/codeplea/tinyexpr'; ref = $null;     tier = 'C (math expression parser)' },
    @{ name = 'mimalloc';   url = 'https://github.com/microsoft/mimalloc'; ref = $null;    tier = 'C (allocator: macro-wrapped conditions `if mi_likely(..){`, #include-only TUs)' },
    @{ name = 'tiny-regex-c'; url = 'https://github.com/kokke/tiny-regex-c'; ref = $null;  tier = 'C (regex state machine, char-literal-heavy)' },
    @{ name = 'qrcodegen';  url = 'https://github.com/nayuki/QR-Code-generator'; ref = $null; tier = 'C (QR encoder, big data tables; src in c/)' },
    @{ name = 'heatshrink'; url = 'https://github.com/atomicobject/heatshrink'; ref = $null; tier = 'C (embedded compression state machine, static config header)' },
    @{ name = 'rax';        url = 'https://github.com/antirez/rax';       ref = $null;     tier = 'C (radix tree, goto-heavy)' },
    @{ name = 'log.c';      url = 'https://github.com/rxi/log.c';         ref = $null;     tier = 'C (logger with file-scope callback dispatch table; src/ subdir)' },
    @{ name = 'wren';       url = 'https://github.com/wren-lang/wren';    ref = $null;     tier = 'C (scripting VM, computed-goto interpreter loop; exposed the phantom nested-function misparse)' },
    @{ name = 'janet';      url = 'https://github.com/janet-lang/janet';  ref = $null;     tier = 'C (Lisp VM; macro-defined JANET_CORE_FN bodies + macro-soup method tables; exposed pass-4/ERROR-table ref losses)' }
)

foreach ($r in $repos) {
    $dest = Join-Path $corpus $r.name
    if (Test-Path $dest) { Write-Host "$($r.name) already present."; continue }
    Write-Host "Cloning $($r.name)  [$($r.tier)]..."
    if ($r.ref) { git clone --depth 1 --branch $r.ref $r.url $dest }
    else        { git clone --depth 1 $r.url $dest }
}

Write-Host "Corpus ready in $corpus"
