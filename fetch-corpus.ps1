# Fetches the test corpus into .corpus/ (gitignored): varied real repos the carver is exercised
# against. Buildable ones feed the build-verification tests; all feed parse-robustness. Idempotent.
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$corpus = Join-Path $root '.corpus'
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

# Every repo is PINNED to a commit SHA (a commit is immutable, so this is byte-exact reproducible;
# see docs/REPRODUCE.md). `git clone --branch` can't take a raw SHA, so we shallow-FETCH the commit.
# To advance a pin, replace its sha with a newer one (git ls-remote <url> HEAD). Tier notes the
# language/shape each exercises (and any bug it exposed).
$repos = @(
    @{ name = 'tiny-AES-c'; url = 'https://github.com/kokke/tiny-AES-c';           sha = '23856752fbd139da0b8ca6e471a13d5bcc99a08d'; tier = 'embedded C (ifdef modes, tables)' },
    @{ name = 'cJSON';      url = 'https://github.com/DaveGamble/cJSON';           sha = 'acc76239bee01d8e9c858ae2cab296704e52d916'; tier = 'C library (99 files; carves 99->2) [v1.7.18]' },
    @{ name = 'json';       url = 'https://github.com/nlohmann/json';              sha = '9cca280a4d0ccf0c08f47a99aa71d1b0e52f8d03'; tier = 'C++ header-only (parse-robustness) [v3.11.3]' },
    @{ name = 'printf';     url = 'https://github.com/mpaland/printf';             sha = 'd3b984684bb8a8bdc48cc7a1abecb93ce59bbe3e'; tier = 'embedded C (callback output, static helpers)' },
    @{ name = 'lua';        url = 'https://github.com/lua/lua';                    sha = '0b29f408433e92953cc72b1d3e06c7ac8139e439'; tier = 'C (#ifdef-heavy, Makefile with -D for build-log scraping)' },
    @{ name = 'zlib';       url = 'https://github.com/madler/zlib';                sha = '767c4c947852e143f582c85f14cf573411df1b35'; tier = 'C (local/ZLIB_INTERNAL macro-prefixed declarations)' },
    @{ name = 'sqlite';     url = 'https://github.com/azadkuh/sqlite-amalgamation'; sha = '15d0ff10ebc7e7225eced1de84bb52137000899b'; tier = 'C (huge amalgamation: token-paste ##, #if-split tables)' },
    @{ name = 'inih';       url = 'https://github.com/benhoyt/inih';               sha = '577ae2dee1f0d9c2d11c7f10375c1715f3d6940c'; tier = 'C (tiny INI parser)' },
    @{ name = 'sds';        url = 'https://github.com/antirez/sds';                sha = '5347739b1581fcba74fd5cab1fc21d2aef317d71'; tier = 'C (string lib, macro-heavy)' },
    @{ name = 'tomlc99';    url = 'https://github.com/cktan/tomlc99';              sha = '29076dfd095bbbbd50a3c1b2760d29f4b83e74ac'; tier = 'C (TOML parser)' },
    @{ name = 'mongoose';   url = 'https://github.com/cesanta/mongoose';           sha = '002c9df3deb5388db0532bcef3487a328bcab6de'; tier = 'C (single-file amalgamation, #ifdef-heavy network stack)' },
    @{ name = 'parson';     url = 'https://github.com/kgabis/parson';              sha = 'ec53fb6528b45811df9db0db22cab96a94a96a11'; tier = 'C (JSON, single file)' },
    @{ name = 'mpc';        url = 'https://github.com/orangeduck/mpc';             sha = '1049534fc56b1971345c7aaa792dea55d6f9b7bc'; tier = 'C (parser combinators, macro-heavy)' },
    @{ name = 'Monocypher'; url = 'https://github.com/LoupVaillant/Monocypher';    sha = '1830c06d5910fba451cec329c8f30f348fc607db'; tier = 'C (crypto: tables + bit-twiddling, src/ subdir)' },
    @{ name = 'tinyexpr';   url = 'https://github.com/codeplea/tinyexpr';          sha = 'c3b2f32eee61762f4c9d89c2c08cf34556a4a780'; tier = 'C (math expression parser)' },
    @{ name = 'mimalloc';   url = 'https://github.com/microsoft/mimalloc';         sha = '31d034d94cdb8e22f7d7ed55967f581a2d6e831d'; tier = 'C (allocator: macro-wrapped conditions, #include-only TUs)' },
    @{ name = 'tiny-regex-c'; url = 'https://github.com/kokke/tiny-regex-c';       sha = 'f2632c6d9ed25272987471cdb8b70395c2460bdb'; tier = 'C (regex state machine, char-literal-heavy)' },
    @{ name = 'qrcodegen';  url = 'https://github.com/nayuki/QR-Code-generator';   sha = '3c6d0b3cefb4e049dc337e82237c9644399716a8'; tier = 'C (QR encoder, big data tables; src in c/)' },
    @{ name = 'heatshrink'; url = 'https://github.com/atomicobject/heatshrink';    sha = '7d419e1fa4830d0b919b9b6a91fe2fb786cf3280'; tier = 'C (embedded compression state machine, static config header)' },
    @{ name = 'rax';        url = 'https://github.com/antirez/rax';                sha = '1927550cb218ec3c3dda8b39d82d1d019bf0476d'; tier = 'C (radix tree, goto-heavy)' },
    @{ name = 'log.c';      url = 'https://github.com/rxi/log.c';                  sha = 'f9ea34994bd58ed342d2245cd4110bb5c6790153'; tier = 'C (logger with file-scope callback dispatch table; src/ subdir)' },
    @{ name = 'wren';       url = 'https://github.com/wren-lang/wren';             sha = '99d2f0b8fc2686134b32b18166e037639f7e9f2c'; tier = 'C (scripting VM, computed-goto loop; exposed the phantom nested-function misparse)' },
    @{ name = 'janet';      url = 'https://github.com/janet-lang/janet';           sha = 'd763d0d51afddceeb251bc6dd6e9458cc4ec0484'; tier = 'C (Lisp VM; macro-defined JANET_CORE_FN bodies + macro-soup tables; pass-4/ERROR-table ref losses)' },
    @{ name = 'quickjs';    url = 'https://github.com/quickjs-ng/quickjs';         sha = '6d46d07d04041b40f4f49eaa7fdebe44c314c699'; tier = 'C (JS engine, computed-goto; symbols from a macro-call global initializer)' },
    @{ name = 'capstone';   url = 'https://github.com/capstone-engine/capstone';   sha = '2b25a5bf77806b6507c3e54f477b69b2f3ac788b'; tier = 'C (disassembler; generated .inc tables #included into .c; reference-only-include gap)' },
    @{ name = 'stm32_samples'; url = 'https://github.com/dwelch67/stm32_samples';  sha = '5e580687b0639b2ffb656a300441cdc37d4de25d'; tier = 'C (bare-metal Cortex-M: real .s startup + .ld; build-support-file emission + arm-none-eabi-gcc)' },
    @{ name = 'fmt';        url = 'https://github.com/fmtlib/fmt';                 sha = '6d71f74624be5daa548073ff8e4e0c8aa5476010'; tier = 'C++ (macro-opened namespace FMT_BEGIN_NAMESPACE + FMT_TRY/CATCH; scope-macro-expansion + macro-function gaps)' },
    @{ name = 'tinyxml2';   url = 'https://github.com/leethomason/tinyxml2';       sha = '8224e427b655b83dae5e2298f1e6919523a78737'; tier = 'C++ (classes; class-first-member brace shatter + header inline pruning)' },
    @{ name = 'simdjson';   url = 'https://github.com/simdjson/simdjson';          sha = '82d0b8ef5068557221639cbc6494de4a9b9cf700'; tier = 'C++ (template/amalgamation; constructor pruning + orphaned template<...> prefix)' },
    @{ name = 'pugixml';    url = 'https://github.com/zeux/pugixml';               sha = '27b68329de32cf9c601ca8eb6c588fd639960c40'; tier = 'C++ (macro-opened namespaces PUGI_IMPL_NS_BEGIN; constructor init-list callee dropped)' }
)

# A repo is "present" only if it fully checked out - sentinel .git/HEAD. A dir with no .git is a partial
# from an interrupted/failed fetch; treat it as absent and re-fetch (otherwise a broken partial would be
# skipped forever and silently poison every downstream test).
function FullyFetched([string]$d) { (Test-Path $d) -and (Test-Path (Join-Path $d '.git\HEAD')) }

$failed = @()
foreach ($r in $repos) {
    $dest = Join-Path $corpus $r.name
    if (FullyFetched $dest) { Write-Host "$($r.name) already present."; continue }
    if (Test-Path $dest) { Write-Host "$($r.name): partial/incomplete - re-fetching."; Remove-Item -Recurse -Force $dest }
    Write-Host "Fetching $($r.name)@$($r.sha.Substring(0,10))  [$($r.tier)]..."
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Push-Location $dest
    try {
        # Gate each step on $LASTEXITCODE (git writes progress to stderr; don't rely on throw). Any
        # failure removes the partial dir so the next run retries cleanly rather than skipping it.
        & git init -q;                                      if ($LASTEXITCODE) { throw "git init" }
        & git remote add origin $r.url;                     if ($LASTEXITCODE) { throw "git remote add" }
        & git fetch -q --depth 1 origin $r.sha;             if ($LASTEXITCODE) { throw "git fetch $($r.sha)" }
        & git -c advice.detachedHead=false checkout -q FETCH_HEAD; if ($LASTEXITCODE) { throw "git checkout" }
    }
    catch {
        Pop-Location
        Write-Host "  FAILED ($($r.name)): $_ - removing partial dir." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
        $failed += $r.name
        continue
    }
    Pop-Location
}
if ($failed.Count -gt 0) {
    Write-Host "`n$($failed.Count) repo(s) failed to fetch (network?): $($failed -join ', '). Re-run to retry just those." -ForegroundColor Yellow
}

Write-Host "Corpus ready in $corpus (all commit-pinned)"
