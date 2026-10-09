# CodeCarver build-error counter: reads the output of building a carved tree and prints how many errors of each
# kind it holds, numbers only (safe to send). The same kinds as summary.txt's <stage>.buildErrors.<kind>, without
# running the carve again.
#
#   powershell -ExecutionPolicy Bypass -File tools\builderrors\count-build-errors.ps1 -Path <out>\<stage>\build-output.txt
#
# Kinds: UndefinedReference, Undeclared, UnknownType, MissingHeader, WarningAsError (also per warning flag),
# Redefinition, Syntax, Other. The same error at the same place counts once. Keep this file ASCII (Windows
# PowerShell 5.1 misreads non-ASCII without a BOM); the rules mirror src/CodeCarver.Core/Diagnostics/BuildErrors.cs.
#
# With -Original <the source tree that was carved> -Carved <out>\<stage> it also says, for each error that names
# something, why that name is missing, still as counts only (the why.* lines):
#   why.<kind>.def.lostInUseFile      the name was in the original of the file the error is in, and the carve took
#                                     it out (a removed function or variable, with its prototypes)
#   why.<kind>.def.lostInOtherFile    the carve took it out of another kept file
#   why.<kind>.def.inDroppedFile      it is only in files the carve did not keep at all
#   why.<kind>.def.unchanged          every file that has it was kept as it was (the declaration is behind an #if
#                                     or an include the build does not take)
#   why.<kind>.def.notInOriginal      the name is nowhere in the original source: made by ## token pasting, or
#                                     from a generated or toolchain file
#   why.<kind>.use.nameOnLine / nameNotOnLine   the name is written on the error's line, or comes from a macro
#   why.<kind>.use.inConditional / unconditional   the error's line is inside an #if block (include guard aside)
#   why.<kind>.use.inHeader / fileNotFound / lineNotFound
#   why.Undeclared.function / identifier   an implicitly declared call, or any other name
param([Parameter(Mandatory = $true)][string]$Path, [string]$Original = '', [string]$Carved = '')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { Write-Host "no such file: $Path"; exit 2 }
if (($Original -eq '') -ne ($Carved -eq '')) { Write-Host "-Original and -Carved go together"; exit 2 }
foreach ($d in @($Original, $Carved)) { if ($d -and -not (Test-Path -LiteralPath $d -PathType Container)) { Write-Host "no such folder: $d"; exit 2 } }

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
$found = New-Object System.Collections.ArrayList
function Add([string]$key, [string]$kind, [string]$flag, [string]$file, [string]$line, [string]$name, [string]$msg) {
    if ($seen.ContainsKey($key)) { return }
    $seen[$key] = 1
    $counts[$kind] = 1 + [int]$counts[$kind]
    if ($flag) { $counts["$kind.$flag"] = 1 + [int]$counts["$kind.$flag"] }
    [void]$found.Add(@{ Kind = $kind; File = $file; Line = $line; Name = $name; Msg = $msg })
}

$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = [regex]::Replace("$($lines[$i])".Trim(), '^(?:\S*[/\\])?(?:[\w.+-]*-)?ld(?:\.exe)?:\s+', '')
    if ($line.Length -eq 0) { continue }
    $done = $false
    foreach ($re in $linkers) {
        $m = [regex]::Match($line, $re)
        if ($m.Success) { Add "link|$($m.Groups['name'].Value)|$i" 'UndefinedReference' $null '' '' $m.Groups['name'].Value $line; $done = $true; break }
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
        $file = $m.Groups['file'].Value.Trim()
        Add "$file|$($m.Groups['line'].Value)|$name|$kind" $kind $flag $file $m.Groups['line'].Value $name $msg
        break
    }
}

$why = @{}
function Why([string]$key) { $why[$key] = 1 + [int]$why[$key] }

if ($Original -and $found.Count -gt 0) {
    $orig = (Resolve-Path -LiteralPath $Original).ProviderPath.TrimEnd('\', '/')
    $carv = (Resolve-Path -LiteralPath $Carved).ProviderPath.TrimEnd('\', '/')
    # <out>\<stage> holds the tree in carved\; the tree itself is accepted too.
    if (Test-Path -LiteralPath (Join-Path $carv 'carved') -PathType Container) { $carv = Join-Path $carv 'carved' }
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $srcExt = '^\.(c|h|cc|cpp|cxx|c\+\+|hh|hpp|hxx|h\+\+|inc|inl|ipp|tcc|def)$'
    $hdrExt = '^\.(h|hh|hpp|hxx|h\+\+|inc|inl|ipp|tcc|def)$'

    # Tree-relative '/' paths of the C and C++ files under a root, not following links (a junction loop never ends).
    function SourceFiles([string]$root) {
        $list = New-Object System.Collections.ArrayList
        $stack = New-Object System.Collections.Stack
        $stack.Push($root)
        while ($stack.Count -gt 0) {
            $d = $stack.Pop()
            try {
                foreach ($s in [IO.Directory]::GetDirectories($d)) {
                    if (-not ([IO.File]::GetAttributes($s) -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($s) }
                }
            } catch { }
            try {
                foreach ($f in [IO.Directory]::GetFiles($d)) {
                    if ([IO.Path]::GetExtension($f) -match $srcExt) { [void]$list.Add($f.Substring($root.Length + 1).Replace('\', '/')) }
                }
            } catch { }
        }
        return , $list
    }

    $names = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($e in $found) { if ($e.Name) { [void]$names.Add($e.Name) } }
    $wordRe = $null
    if ($names.Count -gt 0) {
        $alt = ($names | ForEach-Object { [regex]::Escape($_) }) -join '|'
        $wordRe = New-Object regex ('(?<![\w$])(?:' + $alt + ')(?![\w$])'), 'Compiled'
    }
    # How often each name occurs in one file: name -> count (case matters in C).
    function CountIn([string]$full) {
        $c = New-Object 'System.Collections.Generic.Dictionary[string,int]'
        if ($null -eq $wordRe) { return , $c }
        try { $text = [IO.File]::ReadAllText($full, $latin1) } catch { return , $c }
        foreach ($m in $wordRe.Matches($text)) { $c[$m.Value] = 1 + $(if ($c.ContainsKey($m.Value)) { $c[$m.Value] } else { 0 }) }
        return , $c
    }

    # Where each name occurs in the original: name -> (file -> count).
    [Console]::Error.WriteLine("scanning $orig ...")
    $inOrig = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    foreach ($n in $names) { $inOrig[$n] = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([StringComparer]::OrdinalIgnoreCase) }
    if ($null -ne $wordRe) {
        foreach ($rel in (SourceFiles $orig)) {
            $c = CountIn (Join-Path $orig $rel)
            foreach ($n in $c.Keys) { $inOrig[$n][$rel] = $c[$n] }
        }
    }
    $carvedCounts = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    function CarvedCount([string]$rel, [string]$name) {
        if (-not $carvedCounts.ContainsKey($rel)) { $carvedCounts[$rel] = CountIn (Join-Path $carv $rel) }
        $c = $carvedCounts[$rel]
        if ($c.ContainsKey($name)) { return $c[$name] } else { return 0 }
    }

    # The carved file a compiler path names: under the carved tree, or the longest carved path it ends with
    # (the build may have run from a copy, or printed a path relative to its own folder).
    $byName = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($rel in (SourceFiles $carv)) {
        $leaf = [IO.Path]::GetFileName($rel)
        if (-not $byName.ContainsKey($leaf)) { $byName[$leaf] = New-Object System.Collections.ArrayList }
        [void]$byName[$leaf].Add($rel)
    }
    function CarvedRel([string]$printed) {
        if (-not $printed) { return $null }
        $p = $printed.Replace('\', '/')
        $root = $carv.Replace('\', '/') + '/'
        if ($p.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $p = $p.Substring($root.Length) }
        while ($p.StartsWith('./') -or $p.StartsWith('../')) { $p = $p.Substring($p.IndexOf('/') + 1) }
        $leaf = [IO.Path]::GetFileName($p)
        if (-not $byName.ContainsKey($leaf)) { return $null }
        $best = $null
        foreach ($r in $byName[$leaf]) {
            $hit = ($p -ieq $r) -or $p.EndsWith('/' + $r, [StringComparison]::OrdinalIgnoreCase) -or
                   $r.EndsWith('/' + $p, [StringComparison]::OrdinalIgnoreCase)
            if ($hit -and ($null -eq $best -or $r.Length -gt $best.Length)) { $best = $r }
        }
        return $best
    }

    foreach ($e in $found) {
        $k = "why.$($e.Kind)"
        if (-not $e.Name) { Why "$k.noName"; continue }
        if ($e.Kind -eq 'Undeclared') {
            $l = $e.Msg.ToLowerInvariant()
            if ($l.Contains('implicit declaration') -or $l.Contains('undeclared function')) { Why "$k.function" } else { Why "$k.identifier" }
        }
        $use = CarvedRel $e.File

        # The definition side: where the name was in the original, and what the carve did to those files.
        $where = $inOrig[$e.Name]
        if ($where.Count -eq 0) { Why "$k.def.notInOriginal" }
        else {
            $lostUse = $false; $lostOther = $false; $dropped = $false
            foreach ($rel in $where.Keys) {
                if (-not (Test-Path -LiteralPath (Join-Path $carv $rel) -PathType Leaf)) { $dropped = $true }
                elseif ((CarvedCount $rel $e.Name) -lt $where[$rel]) { if ($use -and $rel -ieq $use) { $lostUse = $true } else { $lostOther = $true } }
            }
            if ($lostUse) { Why "$k.def.lostInUseFile" }
            elseif ($lostOther) { Why "$k.def.lostInOtherFile" }
            elseif ($dropped) { Why "$k.def.inDroppedFile" }
            else { Why "$k.def.unchanged" }
        }

        # The use side: the error's own line in the carved tree.
        if (-not $use) { Why "$k.use.fileNotFound"; continue }
        if ([IO.Path]::GetExtension($use) -match $hdrExt) { Why "$k.use.inHeader" }
        try { $text = [IO.File]::ReadAllLines((Join-Path $carv $use), $latin1) } catch { Why "$k.use.lineNotFound"; continue }
        $ln = 0
        if (-not [int]::TryParse($e.Line, [ref]$ln) -or $ln -lt 1 -or $ln -gt $text.Count) { Why "$k.use.lineNotFound"; continue }
        if ([regex]::IsMatch($text[$ln - 1], '(?<![\w$])' + [regex]::Escape($e.Name) + '(?![\w$])')) { Why "$k.use.nameOnLine" } else { Why "$k.use.nameNotOnLine" }
        # Open #if blocks above the line; an include guard (#ifndef X then #define X first thing) does not count.
        $blocks = New-Object System.Collections.ArrayList
        $directives = 0
        $guardName = $null
        for ($i = 0; $i -lt $ln - 1; $i++) {
            $d = [regex]::Match($text[$i], '^\s*#\s*(?<d>\w+)\s*(?<arg>\w*)')
            if (-not $d.Success) { continue }
            $directives++
            $dir = $d.Groups['d'].Value
            if ($directives -eq 2 -and $null -ne $guardName -and $dir -eq 'define' -and $d.Groups['arg'].Value -eq $guardName -and $blocks.Count -eq 1) { $blocks[0] = 'guard' }
            if ($dir -in @('if', 'ifdef', 'ifndef')) {
                if ($directives -eq 1 -and $dir -eq 'ifndef') { $guardName = $d.Groups['arg'].Value }
                [void]$blocks.Add('if')
            }
            elseif ($dir -eq 'endif' -and $blocks.Count -gt 0) { $blocks.RemoveAt($blocks.Count - 1) }
        }
        if (@($blocks | Where-Object { $_ -eq 'if' }).Count -gt 0) { Why "$k.use.inConditional" } else { Why "$k.use.unconditional" }
    }
}

$total = 0
foreach ($k in $counts.Keys) { if (-not $k.Contains('.')) { $total += $counts[$k] } }
Write-Host "errors = $total"
foreach ($k in ($counts.Keys | Sort-Object)) { Write-Host "$k = $($counts[$k])" }
foreach ($k in ($why.Keys | Sort-Object)) { Write-Host "$k = $($why[$k])" }
exit 0
