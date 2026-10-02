# carve-build-report.ps1
# One-command demonstration of the whole point: take each repo in .corpus, CARVE it to a chosen entry
# set, BUILD the carved output with a pinned toolchain, and print a table of
#   repo | lang | original -> carved bytes | % smaller | files | builds?
#
# Host C repos are compiled per-file with the pinned w64devkit gcc (-c). The embedded fixture is LINKED
# to a real Cortex-M4 ELF with arm-none-eabi-gcc (pass -Arm; on by default). Repo/root configs mirror the
# BuildVerifyTests set that is known to carve+compile clean.
#
# Usage:  powershell -File carve-build-report.ps1            # full sweep (host + ARM)
#         powershell -File carve-build-report.ps1 -NoArm     # skip the ARM ELF link
#         powershell -File carve-build-report.ps1 -Only cJSON,zlib
[CmdletBinding()]
param([switch]$NoArm, [string[]]$Only)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dll = Join-Path $root 'src\CodeCarver.Cli\bin\Debug\net8.0\CodeCarver.Cli.dll'
if (-not (Test-Path $dll)) { $dll = Join-Path $root 'src\CodeCarver.Cli\bin\Release\net8.0\CodeCarver.Cli.dll' }
if (-not (Test-Path $dll)) { Write-Host 'CLI not built. Run: dotnet build -c Debug'; exit 1 }

$gcc    = Join-Path $root '.toolchains\w64devkit\bin\gcc.exe'
$arm    = Join-Path $root '.toolchains\xpack-arm-none-eabi-gcc-13.3.1-1.1\bin\arm-none-eabi-gcc.exe'
$armSz  = Join-Path $root '.toolchains\xpack-arm-none-eabi-gcc-13.3.1-1.1\bin\arm-none-eabi-size.exe'
$corpus = Join-Path $root '.corpus'
$haveGcc = Test-Path $gcc
$haveArm = Test-Path $arm
if (-not $haveGcc) { Write-Host "note: host gcc not found ($gcc) - size shown, build column will read 'no-gcc'" }

# Proven repo configs (path under .corpus, entry roots, dirs to exclude). See BuildVerifyTests.cs.
$repos = @(
  @{ name='cJSON';       path='cJSON';            roots='cJSON_Parse,cJSON_Print,cJSON_Delete'; exclude='tests,fuzzing' }
  @{ name='printf';      path='printf';           roots='snprintf_,vsnprintf_';                 exclude='' }
  @{ name='zlib';        path='zlib';             roots='compress2,uncompress';                 exclude='test,contrib,examples' }
  @{ name='inih';        path='inih';             roots='ini_parse,ini_parse_string';           exclude='examples,cpp,tests' }
  @{ name='sds';         path='sds';              roots='sdsnew,sdscat,sdsfree,sdscatprintf';   exclude='' }
  @{ name='tomlc99';     path='tomlc99';          roots='toml_parse,toml_free';                 exclude='' }
  @{ name='parson';      path='parson';           roots='json_parse_string,json_value_free,json_serialize_to_string'; exclude='' }
  @{ name='mpc';         path='mpc';              roots='mpc_parse,mpc_new,mpc_delete';         exclude='' }
  @{ name='tinyexpr';    path='tinyexpr';         roots='te_interp,te_compile,te_eval,te_free'; exclude='' }
  @{ name='tiny-regex-c';path='tiny-regex-c';     roots='re_compile,re_match,re_matchp';        exclude='' }
  @{ name='rax';         path='rax';              roots='raxNew,raxInsert,raxRemove,raxFind,raxFree'; exclude='rax-test.c,rax-oom-test.c' }
  @{ name='Monocypher';  path='Monocypher\src';   roots='crypto_blake2b,crypto_x25519,crypto_wipe';   exclude='' }
  @{ name='qrcodegen';   path='qrcodegen\c';      roots='qrcodegen_encodeText,qrcodegen_encodeBinary,qrcodegen_getModule'; exclude='qrcodegen-demo.c,qrcodegen-test.c' }
  @{ name='log.c';       path='log.c\src';        roots='log_log,log_set_level,log_add_callback'; exclude='' }
  @{ name='lua';         path='lua';              roots='luaL_newstate,luaL_openlibs,lua_close,luaL_loadstring,lua_pcallk'; exclude='onelua.c' }
  @{ name='sqlite';      path='sqlite';           roots='sqlite3_open,sqlite3_exec,sqlite3_close,sqlite3_prepare_v2,sqlite3_step,sqlite3_finalize,sqlite3_errmsg'; exclude='' }
)
if ($Only) { $repos = $repos | Where-Object { $Only -contains $_.name } }

function Compile-Carved($outDir) {
  # Compile every carved .c to an object with the pinned gcc. Absolute -I for out and each subdir so cwd
  # doesn't matter. Returns $true only if every file compiles (exit 0).
  $cfiles = @(Get-ChildItem $outDir -Recurse -Filter *.c)
  if ($cfiles.Count -eq 0) { return $false }
  $inc = @("-I$outDir") + (Get-ChildItem $outDir -Recurse -Directory | ForEach-Object { "-I$($_.FullName)" })
  foreach ($c in $cfiles) {
    $a = $inc + @('-w','-c', $c.FullName, '-o', "$($c.FullName).o")
    & $gcc @a *> $null
    if ($LASTEXITCODE -ne 0) { return $false }
  }
  return $true
}

$rows = @()
foreach ($r in $repos) {
  $dir = Join-Path $corpus $r.path
  if (-not (Test-Path $dir)) { Write-Host ("skip {0,-14} (not in .corpus)" -f $r.name); continue }
  Write-Host ("carving {0} ..." -f $r.name) -NoNewline
  $out = Join-Path $env:TEMP ("ccbr-" + $r.name.Replace('\','_') + "-" + [guid]::NewGuid().ToString('N').Substring(0,8))
  $eps = '["' + (($r.roots -split ',') -join '","') + '"]'
  $exc = if ($r.exclude) { '["' + (($r.exclude -split ',') -join '","') + '"]' } else { '[]' }
  $cfg = "$out.toml"
  @"
outputDirectory = "$($out -replace '\\','/')"
[common]
entryPoints = $eps
languages = ["c"]
excludeDirectories = $exc
carveSourceFileContents = true
"@ | Set-Content -Encoding utf8 $cfg
  $o = (& dotnet $dll carve $dir --config $cfg 2>&1 | Out-String)
  Remove-Item $cfg -Force -ErrorAction SilentlyContinue

  $orig = 0; $carved = 0; $pct = 0; $files = 0
  foreach ($line in ($o -split "`n")) {
    if ($line -match '([\d,]+)\s*B\s*->\s*([\d,]+)\s*B\s*\(\s*([\-\d]+)%') {
      $orig = [int64]($matches[1] -replace ',', ''); $carved = [int64]($matches[2] -replace ',', ''); $pct = [int]$matches[3]
    }
    if ($line -match 'emitted\s*:\s*(\d+)\s*files') { $files = [int]$matches[1] }
  }
  $builds = if ($haveGcc) { if (Compile-Carved (Join-Path $out 'carved')) { 'YES' } else { 'NO' } } else { 'no-gcc' }
  Write-Host ("  {0}  ({1}% smaller, {2} files, builds={3})" -f $r.name, $pct, $files, $builds)
  $rows += [pscustomobject]@{ Repo=$r.name; Lang='c'; Orig=$orig; Carved=$carved; Pct=$pct; Files=$files; Builds=$builds }
  Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}

# ARM: carve the embedded fixture and LINK it to a real Cortex-M4 ELF.
if (-not $NoArm -and $haveArm -and (-not $Only -or $Only -contains 'cortexm')) {
  $fix = Join-Path $root 'examples\cortexm-firmware'
  if (Test-Path $fix) {
    Write-Host 'carving cortexm-firmware (ARM) ...' -NoNewline
    $out = Join-Path $env:TEMP ("ccbr-arm-" + [guid]::NewGuid().ToString('N').Substring(0,8))
    $cfg = "$out.toml"
    @"
outputDirectory = "$($out -replace '\\','/')"
[common]
entryPoints = ["Reset_Handler","main"]
languages = ["c"]
carveSourceFileContents = true
"@ | Set-Content -Encoding utf8 $cfg
    $o = (& dotnet $dll carve $fix --config $cfg 2>&1 | Out-String)
    Remove-Item $cfg -Force -ErrorAction SilentlyContinue
    $orig = 0; $carved = 0; $pct = 0; $files = 0
    foreach ($line in ($o -split "`n")) {
      if ($line -match '([\d,]+)\s*B\s*->\s*([\d,]+)\s*B\s*\(\s*([\-\d]+)%') { $orig=[int64]($matches[1] -replace ',',''); $carved=[int64]($matches[2] -replace ',',''); $pct=[int]$matches[3] }
      if ($line -match 'emitted\s*:\s*(\d+)\s*files') { $files = [int]$matches[1] }
    }
    $carvedDir = Join-Path $out 'carved'
    $cfiles = @(Get-ChildItem $carvedDir -Filter *.c | ForEach-Object { $_.FullName })
    $ld = (Get-ChildItem $carvedDir -Filter *.ld | Select-Object -First 1).FullName
    $elf = Join-Path $out 'firmware.elf'
    $aa = @('-mcpu=cortex-m4','-mthumb','-nostartfiles','-ffunction-sections','-Wl,--gc-sections','-T',$ld,'-o',$elf) + $cfiles
    & $arm @aa *> $null
    $builds = if ($LASTEXITCODE -eq 0 -and (Test-Path $elf)) { 'YES(ELF)' } else { 'NO' }
    Write-Host ("  cortexm-firmware  (links={0})" -f $builds)
    if ($builds -eq 'YES(ELF)' -and (Test-Path $armSz)) { & $armSz $elf }
    $rows += [pscustomobject]@{ Repo='cortexm-firmware'; Lang='c/arm'; Orig=$orig; Carved=$carved; Pct=$pct; Files=$files; Builds=$builds }
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
  }
}

Write-Host ''
Write-Host '================ CARVE -> BUILD REPORT ================'
$fmt = '{0,-18} {1,-6} {2,12} {3,12} {4,6} {5,6} {6,-9}'
Write-Host ($fmt -f 'Repo','Lang','Orig B','Carved B','%','Files','Builds')
Write-Host ('-' * 78)
foreach ($row in $rows) {
  Write-Host ($fmt -f $row.Repo, $row.Lang, ('{0:N0}' -f $row.Orig), ('{0:N0}' -f $row.Carved), ("{0}%" -f $row.Pct), $row.Files, $row.Builds)
}
$built = @($rows | Where-Object { $_.Builds -like 'YES*' }).Count
$totalOrig = ($rows | Measure-Object -Property Orig -Sum).Sum
$totalCarved = ($rows | Measure-Object -Property Carved -Sum).Sum
Write-Host ('-' * 78)
Write-Host ("{0}/{1} carved trees build clean.  Total: {2:N0} B -> {3:N0} B" -f $built, $rows.Count, $totalOrig, $totalCarved)
