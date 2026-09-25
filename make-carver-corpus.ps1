<#
.SYNOPSIS
  Fabricate a synthetic, COMPILABLE, firmware-shaped C repo with a KNOWN call graph + known implicit
  roots, for ground-truth carve testing. Adapted from the CodeCompass corpus generator: CodeCompass only
  indexed its corpus, so it never had to compile -- but CodeCarver's contract is "the carve must still
  build", so here every file is real C that gcc compiles and links. Because the generator knows exactly
  what it emitted, it writes a manifest of the true reachable set from each root and the true dead set,
  turning a carve into a pass/fail assertion (soundness + precision) with NO compiler -- and the compilable
  output then feeds the authoritative build+size oracle (see carver-oracle.ps1).

.DESCRIPTION
  Structure (all deterministic given -Seed):
   - REACHABLE tree from main: main -> app_main -> stage_0..stage_{S-1} (chain across dirs); every stage
     also calls util_common -> util_leaf. These MUST survive a carve rooted at main.
   - DEAD chain: dead_0..dead_{D-1} -> dead_leaf, referenced by nothing reachable. These MUST be pruned.
   - IMPLICIT roots (firmware-critical, auto-discovered by CodeCarver, not reachable from main by any C
     call): a vector table g_vectors[] in section .isr_vector KEEP()'d by the linker script -> isr_0..
     isr_{P-1} -> isr_util; a __attribute__((constructor)) ctor_init -> ctor_helper; a weak alias
     weak_handler -> real_handler. Their closures MUST survive; dropping any would fail to link/boot.
   - Firmware STRUCTURE knobs (reused from the CodeCompass generator): giant register headers
     (-GiantHeaders/-MacroDensity/-MaxHeaderMB), zero-symbol data blobs (-BlobFiles), build output beside
     sources (-BuildOutput: .o/.lst/.bak), tiny files (-TinyFiles), wide/deep dirs (-Dirs/-Depth). Scaled
     by COUNT via -Scale; per-file sizes are never scaled (the header bytes ARE the pathology).

  The manifest (<out>-manifest.json) records: roots to carve, expectedKept (reachable-from-main UNION the
  implicit-root closures), expectedDropped (the dead set), and allFuncs. carver-oracle.ps1 consumes it.

.PARAMETER Out          Output dir (created; cleared if it exists).
.PARAMETER Scale        Count multiplier (default 1.0 -- this corpus is small by design; scale up for stress).
.PARAMETER Seed         RNG seed (default 1337).
.PARAMETER Stages       Reachable stage functions (default 40).
.PARAMETER DeadChains   Dead functions (default 30) -- must all be pruned.
.PARAMETER Isrs         ISR functions behind the vector table (default 16).
.PARAMETER GiantHeaders Giant register headers (default 0 -- off for the fast correctness/build oracle).
.PARAMETER MacroDensity #defines per giant header (default 200000).
.PARAMETER MaxHeaderMB  Cap per giant header (default 24).
.PARAMETER BlobFiles    Zero-symbol data blobs (default 0).
.PARAMETER TinyFiles    Tiny .csv files for count pressure (default 0).
.PARAMETER BuildOutput  Emit .o/.lst/.bak beside sources (default $false; on to test --exclude noise).
.PARAMETER Dirs         Approx directory count (default 24).
.PARAMETER Depth        Approx max path depth (default 5).
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [double]$Scale = 1.0,
    [int]$Seed = 1337,
    [int]$Stages = 40,
    [int]$DeadChains = 30,
    [int]$Isrs = 16,
    [int]$GiantHeaders = 0,
    [int]$MacroDensity = 200000,
    [int]$MaxHeaderMB = 24,
    [int]$BlobFiles = 0,
    [int]$TinyFiles = 0,
    [bool]$BuildOutput = $false,
    [int]$Dirs = 24,
    [int]$Depth = 5
)
$ErrorActionPreference = 'Stop'
$rng = [System.Random]::new($Seed)
function Scaled([int]$knob) { if ($knob -le 0) { return 0 }; return [Math]::Max(1, [int][Math]::Round($knob * $Scale)) }

$nStage = Scaled $Stages; $nDead = Scaled $DeadChains; $nIsr = Scaled $Isrs
$nGiant = if ($GiantHeaders -gt 0) { Scaled $GiantHeaders } else { 0 }
$nBlob = if ($BlobFiles -gt 0) { Scaled $BlobFiles } else { 0 }
$nCsv = if ($TinyFiles -gt 0) { Scaled $TinyFiles } else { 0 }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$outFull = (New-Item -ItemType Directory -Force -Path $Out).FullName
$srcRoot = (New-Item -ItemType Directory -Force -Path (Join-Path $outFull 'src')).FullName
Write-Host "Fabricating COMPILABLE carver corpus at $outFull (scale $Scale, seed $Seed) ..."

# --- directory tree (wide/deep, for path handling) ---
$dirList = New-Object System.Collections.Generic.List[string]
$perBlock = [Math]::Max(1, [int][Math]::Round((Scaled $Dirs) / 6))
for ($b = 0; $b -lt $perBlock; $b++) {
    for ($s = 0; $s -lt 2; $s++) {
        $seg = @("block$b", "sub$s")
        while ($seg.Count -lt [Math]::Min($Depth, 10)) { $seg += "lvl$($seg.Count)" }
        $d = Join-Path $srcRoot ($seg -join [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        $dirList.Add($d)
    }
}
if ($dirList.Count -eq 0) { $dirList.Add($srcRoot) }
function PickDir { return $dirList[$rng.Next(0, $dirList.Count)] }

$allFuncs = New-Object System.Collections.Generic.List[string]
$expectedKept = New-Object System.Collections.Generic.List[string]
$expectedDropped = New-Object System.Collections.Generic.List[string]

function WriteC([string]$dir, [string]$name, [string[]]$body) {
    Set-Content (Join-Path $dir "$name.c") ($body -join "`n") -Encoding ascii
    if ($BuildOutput) {
        Set-Content (Join-Path $dir "$name.o")   ([byte[]]::new(32)) -Encoding Byte
        Set-Content (Join-Path $dir "$name.lst") "   1 0000 ${name}:`n" -Encoding ascii
        Set-Content (Join-Path $dir "$name.bak") ($body -join "`n") -Encoding ascii
    }
}
# A dense-but-valid function body so pruning it saves real bytes (the size-delta signal).
function Filler { $ls = @(); for ($k = 0; $k -lt 40; $k++) { $ls += "    acc = (acc * 1664525u + 1013904223u) ^ (acc >> 3);" }; return $ls }

# A giant register-map header: MacroDensity object-like #defines, capped at MaxHeaderMB. This is the byte
# pathology; it's generated into src/ with fixed names and #included by util.c below, so it becomes part of
# a REACHABLE translation unit -- exercising the big-file parse path (--parse-timeout / max-parse-bytes)
# and the stream-copy-whole emit on a file the carve must keep.
function New-RegHeader([string]$path, [int]$defines, [long]$maxBytes, [int]$fam) {
    $guard = "REGMAP_" + [IO.Path]::GetFileNameWithoutExtension($path).ToUpper() + "_H"
    $sw = [IO.StreamWriter]::new($path, $false, [Text.Encoding]::ASCII, 1MB)
    try {
        $sw.Write("#ifndef $guard`n#define $guard`n")
        $reg = 0; $emitted = 0; $sb = [Text.StringBuilder]::new(6MB)
        while ($emitted -lt $defines -and $sw.BaseStream.Length -lt $maxBytes) {
            [void]$sb.Clear()
            for ($k = 0; $k -lt 16384 -and $emitted -lt $defines; $k++) {
                [void]$sb.Append("#define HWIO_BLK${fam}_REG${reg}_ADDR (0x40000000u + 0x$(($reg*4).ToString('x6')))`n#define HWIO_BLK${fam}_REG${reg}_MSK 0xffu`n"); $reg++; $emitted += 2
            }
            $sw.Write($sb.ToString())
        }
        $sw.Write("#endif`n")
    } finally { $sw.Close() }
}
$giantInclude = @()
if ($nGiant -gt 0) {
    Write-Host "  $nGiant giant register header(s) (<=${MaxHeaderMB}MB, $MacroDensity defines) ..."
    for ($i = 0; $i -lt $nGiant; $i++) { New-RegHeader (Join-Path $srcRoot "regmap_$i.h") $MacroDensity ([long]$MaxHeaderMB * 1MB) $i }
    $giantInclude = @("#include ""regmap_0.h""")   # util.c is in src/ (same dir) -> resolves; pulls the giant into a kept TU
}

# --- REACHABLE tree: main -> app_main -> stage_0..N; each stage -> util_common -> util_leaf ---
Write-Host "  reachable tree: $nStage stages + util ..."
WriteC $srcRoot "util" ($giantInclude + @(
    "unsigned util_leaf(unsigned x) { return x ^ 0x5a5a5a5au; }",
    "unsigned util_common(unsigned x) {",
    "    unsigned acc = util_leaf(x);") + (Filler) + @("    return acc;", "}"))
$expectedKept.Add("util_leaf"); $expectedKept.Add("util_common"); $allFuncs.Add("util_leaf"); $allFuncs.Add("util_common")

for ($i = 0; $i -lt $nStage; $i++) {
    $next = if ($i -lt $nStage - 1) { "    acc += stage_$($i+1)(x - 1);" } else { "" }
    $decls = @("unsigned util_common(unsigned);")
    if ($i -lt $nStage - 1) { $decls += "unsigned stage_$($i+1)(unsigned);" }
    WriteC (PickDir) "stage_$i" ($decls + @(
        "unsigned stage_$i(unsigned x) {",
        "    unsigned acc = util_common(x);") + (Filler) + @($next, "    return acc;", "}"))
    $expectedKept.Add("stage_$i"); $allFuncs.Add("stage_$i")
}
WriteC $srcRoot "app" @(
    "unsigned stage_0(unsigned);",
    "unsigned app_main(unsigned x) { return stage_0(x); }")
$expectedKept.Add("app_main"); $allFuncs.Add("app_main")

# --- DEAD chain: dead_0..N -> dead_leaf, referenced by nothing reachable. MUST be pruned. ---
Write-Host "  dead chain: $nDead functions (must be pruned) ..."
WriteC $srcRoot "dead_leaf" @("unsigned dead_leaf(unsigned x) { return x + 1u; }")
$expectedDropped.Add("dead_leaf"); $allFuncs.Add("dead_leaf")
for ($i = 0; $i -lt $nDead; $i++) {
    $callee = if ($i -lt $nDead - 1) { "dead_$($i+1)" } else { "dead_leaf" }
    WriteC (PickDir) "dead_$i" (@("unsigned $callee(unsigned);",
        "unsigned dead_$i(unsigned x) {",
        "    unsigned acc = $callee(x);") + (Filler) + @("    return acc;", "}"))
    $expectedDropped.Add("dead_$i"); $allFuncs.Add("dead_$i")
}

# --- IMPLICIT roots: vector table (KEEP section) -> isr_*, constructor, weak alias. ---
Write-Host "  implicit roots: vector table ($nIsr ISRs) + constructor + weak alias ..."
$isrDecls = @("unsigned isr_util(unsigned);")
$isrDefs = @("unsigned isr_util(unsigned x) { return x * 2654435761u; }")
$isrNames = @()
for ($i = 0; $i -lt $nIsr; $i++) {
    $isrDefs += @("void isr_$i(void) { volatile unsigned v = isr_util($i); (void)v; }")
    $isrNames += "isr_$i"
    $expectedKept.Add("isr_$i"); $allFuncs.Add("isr_$i")
}
$expectedKept.Add("isr_util"); $allFuncs.Add("isr_util")
# constructor + helper (AttributeRootProvider auto-roots the constructor; its callee must follow)
$ctor = @(
    "unsigned ctor_helper(unsigned x) { return x + 7u; }",
    "__attribute__((constructor)) void ctor_init(void) { volatile unsigned v = ctor_helper(3); (void)v; }")
$expectedKept.Add("ctor_helper"); $expectedKept.Add("ctor_init"); $allFuncs.Add("ctor_helper"); $allFuncs.Add("ctor_init")
# weak alias: weak_handler aliases real_handler (AliasAttr auto-roots -> keeping alias keeps target)
$alias = @(
    "void real_handler(void) { volatile unsigned v = isr_util(99); (void)v; }",
    "void weak_handler(void) __attribute__((weak, alias(""real_handler"")));")
$expectedKept.Add("real_handler"); $expectedKept.Add("weak_handler"); $allFuncs.Add("real_handler"); $allFuncs.Add("weak_handler")
# the vector table: an initialized array in .isr_vector naming Reset_Handler + every ISR + the weak handler.
$vecEntries = @("app_reset") + $isrNames + @("weak_handler")
$vec = @("extern void app_reset(void);") + ($isrNames | ForEach-Object { "void $_(void);" }) + @(
    "void weak_handler(void);",
    "void (* const g_vectors[])(void) __attribute__((section("".isr_vector""), used)) = {",
    "    " + (($vecEntries) -join ", ") + "",
    "};")
Set-Content (Join-Path $srcRoot "vectors.c") (($isrDecls + $isrDefs + $ctor + $alias + $vec) -join "`n") -Encoding ascii
# app_reset is the startup entry named by the vector table (kept via the table). It calls app_main.
WriteC $srcRoot "startup" @("unsigned app_main(unsigned);",
    "void app_reset(void) { volatile unsigned v = app_main(1); (void)v; }")
$expectedKept.Add("app_reset"); $allFuncs.Add("app_reset")

# --- main: the explicit root. Calls app_main; the vector table / ctor / alias are implicit. ---
WriteC $srcRoot "main" @("unsigned app_main(unsigned);",
    "int main(void) { return (int)(app_main(5) & 0x7fu); }")
$expectedKept.Add("main"); $allFuncs.Add("main")

# --- linker script with KEEP(.isr_vector) + .init_array (roots the vector table + constructors) ---
Set-Content (Join-Path $outFull "firmware.ld") @"
/* synthetic linker script: KEEP the vector table + init_array so a from-main closure can't drop them */
SECTIONS {
  .isr_vector : { KEEP(*(.isr_vector)) }
  .init_array : { KEEP(*(.init_array*)) }
}
"@ -Encoding ascii

# --- firmware STRUCTURE knobs (data blobs / tiny files); giant headers generated earlier ---
if ($nBlob -gt 0) {
    Write-Host "  $nBlob data blob(s) ..."
    for ($i = 0; $i -lt $nBlob; $i++) {
        $sb = [Text.StringBuilder]::new(); [void]$sb.AppendLine("const unsigned char blob_$i[] = {")
        for ($l = 0; $l -lt 20000; $l++) { [void]$sb.AppendLine("0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),") }
        [void]$sb.AppendLine("};"); Set-Content (Join-Path (PickDir) "blob_$i.c") $sb.ToString() -Encoding ascii
    }
}
if ($nCsv -gt 0) {
    for ($i = 0; $i -lt $nCsv; $i++) { Set-Content (Join-Path (PickDir) "data_$i.csv") "id,val`n$i,$($rng.Next())" -Encoding ascii }
}

# --- ground-truth manifest ---
$manifest = [ordered]@{
    roots           = @("main")
    expectedKept    = @($expectedKept | Sort-Object -Unique)
    expectedDropped = @($expectedDropped | Sort-Object -Unique)
    allFuncs        = @($allFuncs | Sort-Object -Unique)
}
$mpath = Join-Path (Split-Path $outFull) ((Split-Path $outFull -Leaf) + "-manifest.json")
($manifest | ConvertTo-Json -Depth 5) | Set-Content $mpath -Encoding ascii

$stats = Get-ChildItem $srcRoot -Recurse -File -Filter *.c
Write-Host ("Done: {0} .c files, {1} funcs ({2} reachable, {3} dead), manifest {4}" -f `
    $stats.Count, $allFuncs.Count, $expectedKept.Count, $expectedDropped.Count, $mpath)
