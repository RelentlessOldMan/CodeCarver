# Corpus-wide compile-diff sweep (the fuzz methodology, whole corpus, via WSL gcc/g++).
# For each configured repo: syntax-check every source file UNCARVED (baseline), carve --prune --out,
# then syntax-check the carved copies. A file that compiled before but FAILS after is a carve soundness
# bug (dropped symbol / broken structure). Files that don't compile standalone (need build config) never
# enter either set, so they're ignored. This re-validates the whole corpus after parse/emit changes AND
# hunts new bugs. Needs: dotnet build -c Release; WSL Ubuntu with gcc+g++; .corpus fetched.
#
#   ./corpus-compile-sweep.ps1            # sweep all configured repos present under .corpus
$ErrorActionPreference = 'Continue'   # native stderr (carve warns) must not abort the sweep
$root = $PSScriptRoot
$cli  = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build -c Release" }
function ToWsl([string]$p) { $f=[IO.Path]::GetFullPath($p); if ($f -notmatch '^([A-Za-z]):[\\/](.*)$'){throw "bad path $p"}; "/mnt/$($Matches[1].ToLower())/$($Matches[2] -replace '\\','/')" }
$sh = ToWsl (Join-Path $root 'wsl-syntax-check.sh')

# repo | lang | roots | include dirs (rel) | inc string for syntax-check
$cases = @(
  @{ n='cJSON';        lang='c';   roots='cJSON_Parse,cJSON_Delete,cJSON_Print';                 inc='' },
  @{ n='sds';          lang='c';   roots='sdsnew,sdscatlen,sdsfree,sdsdup';                       inc='' },
  @{ n='inih';         lang='c';   roots='ini_parse,ini_parse_file';                             inc='' },
  @{ n='parson';       lang='c';   roots='json_parse_string,json_value_free,json_serialize_to_string'; inc='' },
  @{ n='tomlc99';      lang='c';   roots='toml_parse,toml_free,toml_table_in';                   inc='' },
  @{ n='tinyexpr';     lang='c';   roots='te_interp,te_compile,te_eval,te_free';                 inc='' },
  @{ n='log.c';        lang='c';   roots='log_log,log_set_level';                                inc='src' },
  @{ n='rax';          lang='c';   roots='raxNew,raxInsert,raxFind,raxRemove,raxFree';           inc='' },
  @{ n='heatshrink';   lang='c';   roots='heatshrink_encoder_poll,heatshrink_decoder_poll';      inc='' },
  @{ n='qrcodegen';    lang='c';   roots='qrcodegen_encodeText,qrcodegen_encodeBinary';          inc='c' },
  @{ n='tiny-AES-c';   lang='c';   roots='AES_init_ctx,AES_CBC_encrypt_buffer,AES_ECB_encrypt';  inc='' },
  @{ n='tiny-regex-c'; lang='c';   roots='re_compile,re_matchp,re_match';                        inc='' },
  @{ n='printf';       lang='c';   roots='printf_,sprintf_,snprintf_,vsnprintf_';                inc='' },
  @{ n='Monocypher';   lang='c';   roots='crypto_blake2b,crypto_x25519,crypto_eddsa_sign';       inc='src' },
  @{ n='zlib';         lang='c';   roots='deflate,inflate,compress2,adler32,crc32';              inc='' },
  @{ n='cwalk';        lang='c';   roots='cwk_path_get_basename,cwk_path_join,cwk_path_normalize'; inc='include' },
  @{ n='utf8proc';     lang='c';   roots='utf8proc_decompose,utf8proc_map,utf8proc_NFC';         inc='' },
  @{ n='tinyxml2';     lang='cpp'; roots='LoadFile,SaveFile,Parse,Print,Accept';                 inc='' },
  @{ n='pugixml';      lang='cpp'; roots='load_file,load_string,load_buffer,save';               inc='src' },
  @{ n='simdjson';     lang='cpp'; roots='parse,iterate,load,load_many';                         inc='singleheader' }
)

$totalBugs = 0; $tested = 0
foreach ($t in $cases) {
    $src = Join-Path $root ".corpus\$($t.n)"
    if (-not (Test-Path $src)) { Write-Host ("SKIP {0} (absent)" -f $t.n) -ForegroundColor DarkGray; continue }
    $tested++
    Write-Host ("=== {0} ({1}) ===" -f $t.n, $t.lang) -ForegroundColor Cyan
    $srcW = ToWsl $src
    $base = wsl -d Ubuntu -- bash $sh $srcW $t.lang $t.inc
    $baseOK = @($base | Where-Object { $_ -like 'OK *' } | ForEach-Object { $_.Substring(3) })
    if ($baseOK.Count -eq 0) { Write-Host "  (0 baseline compile standalone - needs build config; skipped)" -ForegroundColor DarkGray; continue }

    $out = Join-Path $root ".oracle-cpp\sweep-$($t.n)"
    & dotnet $cli carve $src --lang $t.lang --roots $t.roots --prune --out $out 2>&1 |
        Select-String 'nodes|UNRESOLVED' | ForEach-Object { Write-Host "  $_" }
    $outW = ToWsl $out
    $carved = wsl -d Ubuntu -- bash $sh $outW $t.lang $t.inc
    $carvedOK = @{}; foreach ($l in ($carved | Where-Object { $_ -like 'OK *' })) { $carvedOK[$l.Substring(3)] = $true }

    $bugs = 0
    foreach ($f in $baseOK) {
        # only files still emitted by the carve (dropping a whole file is sound); a KEPT file that
        # compiled before but not after is the bug.
        $carvedPath = Join-Path $out $f.Replace('/','\')
        if (-not (Test-Path $carvedPath)) { continue }
        if (-not $carvedOK.ContainsKey($f)) { Write-Host ("  !!! REGRESSED: {0}" -f $f) -ForegroundColor Red; $bugs++ }
    }
    if ($bugs -eq 0) { Write-Host ("  CLEAN ({0} files compiled both before and after)" -f $baseOK.Count) -ForegroundColor Green }
    $totalBugs += $bugs
}
Write-Host ""
Write-Host ("Swept $tested repo(s). Total carve regressions: $totalBugs") -ForegroundColor $(if ($totalBugs -eq 0) { 'Green' } else { 'Red' })
if ($totalBugs -gt 0) { exit 1 }
