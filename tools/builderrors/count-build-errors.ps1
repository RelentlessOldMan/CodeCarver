# CodeCarver build-error counter: reads the output of building a carved tree and prints how many errors of each
# kind it holds, numbers only (safe to send). The same kinds as summary.txt's <stage>.buildErrors.<kind>, without
# running the carve again.
#
#   powershell -ExecutionPolicy Bypass -File tools\builderrors\count-build-errors.ps1 -Path <out>\<stage>\build-output.txt
#
# Kinds: UndefinedReference, Undeclared, UnknownType, MissingHeader, WarningAsError (also per warning flag),
# Redefinition, Syntax, Other. The same error at the same place counts once. Keep this file ASCII (Windows
# PowerShell 5.1 misreads non-ASCII without a BOM); the rules mirror src/CodeCarver.Core/Diagnostics/BuildErrors.cs.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { Write-Host "no such file: $Path"; exit 2 }

$gnu    = '^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?<line>\d+)(?::\d+)?:\s*(?:fatal\s+)?error:\s*(?<msg>.*)$'
$gnuLd  = '^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?:(?<line>\d+)|\([^)]*\)):\s*(?<msg>undefined reference to .*)$'
$msvc   = '^(?<file>.+?)\((?<line>\d+)(?:,\d+)?\)\s*:\s*(?:fatal\s+)?error\s+\w+\s*:\s*(?<msg>.*)$'
$armIar = '^"(?<file>[^"]+)",\s*(?:line\s+)?(?<line>\d+)\s*:?\s*(?:Fatal\s+)?[Ee]rror(?:\[\w+\])?:\s*(?:#\d+:\s*)?(?<msg>.*)$'
$linkers = @('error:\s*undefined symbol:\s*(?<name>\S+)', 'error LNK\d+:\s*unresolved external symbol\s+(?<name>[^\s(]+)',
             'L6218E:\s*Undefined symbol\s+(?<name>[^\s(]+)', 'Error\[Li005\]:\s*no definition for\s+"(?<name>[^"]+)"')

function Classify([string]$msg) {
    $l = $msg.ToLowerInvariant()
    if ($l.Contains('no such file') -or $l.Contains('cannot open include file') -or $l.Contains('cannot open source file') -or $l.Contains('file not found')) { return 'MissingHeader' }
    if ($l.Contains('[-werror')) { return 'WarningAsError' }
    if ($l.Contains('undefined reference')) { return 'UndefinedReference' }
    if ($l.Contains('unknown type name') -or $l.Contains('is not a type') -or $l.Contains('does not name a type')) { return 'UnknownType' }
    if ($l.Contains('undeclared') -or $l.Contains('implicit declaration') -or $l.Contains('is undefined') -or
        $l.Contains('was not declared') -or $l.Contains('not declared in this scope')) { return 'Undeclared' }
    if ($l.Contains('redefinition') -or $l.Contains('conflicting types') -or $l.Contains('multiple definition') -or $l.Contains('redeclared')) { return 'Redefinition' }
    if ($l.StartsWith('expected') -or $l.Contains(' expected ') -or $l.Contains('stray ') -or $l.Contains('unterminated') -or
        $l.Contains('without #if') -or $l.Contains('missing terminating') -or $l.Contains('unbalanced') -or $l.Contains('syntax error')) { return 'Syntax' }
    return 'Other'
}

# gcc quotes names with typographic quotes in a UTF-8 locale (kept as code points: this file stays ASCII).
$open = [string][char]0x2018
$close = [string][char]0x2019
$counts = @{}
$seen = @{}
function Add([string]$key, [string]$kind, [string]$flag) {
    if ($seen.ContainsKey($key)) { return }
    $seen[$key] = 1
    $counts[$kind] = 1 + [int]$counts[$kind]
    if ($flag) { $counts["$kind.$flag"] = 1 + [int]$counts["$kind.$flag"] }
}

$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = [regex]::Replace("$($lines[$i])".Trim(), '^(?:\S*[/\\])?(?:[\w.+-]*-)?ld(?:\.exe)?:\s+', '')
    if ($line.Length -eq 0) { continue }
    $done = $false
    foreach ($re in $linkers) {
        $m = [regex]::Match($line, $re)
        if ($m.Success) { Add "link|$($m.Groups['name'].Value)|$i" 'UndefinedReference' $null; $done = $true; break }
    }
    if ($done) { continue }
    foreach ($re in @($gnuLd, $armIar, $msvc, $gnu)) {
        $m = [regex]::Match($line, $re)
        if (-not $m.Success) { continue }
        $msg = $m.Groups['msg'].Value
        $kind = Classify $msg
        $flag = $null
        if ($kind -eq 'WarningAsError') { $f = [regex]::Match($msg, '\[-Werror=(?<flag>[\w+-]+)\]'); if ($f.Success) { $flag = $f.Groups['flag'].Value } }
        $name = ''
        $q = [regex]::Match($msg, "[``'""$open](?<q>[A-Za-z_$][\w$]*)[``'""$close]")
        if ($q.Success) { $name = $q.Groups['q'].Value }
        Add "$($m.Groups['file'].Value.Trim())|$($m.Groups['line'].Value)|$name|$kind" $kind $flag
        break
    }
}

$total = 0
foreach ($k in $counts.Keys) { if (-not $k.Contains('.')) { $total += $counts[$k] } }
Write-Host "errors = $total"
foreach ($k in ($counts.Keys | Sort-Object)) { Write-Host "$k = $($counts[$k])" }
exit 0
