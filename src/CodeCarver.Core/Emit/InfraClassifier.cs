namespace CodeCarver.Core.Emit;

/// <summary>The build role of a non-code (infrastructure) file, for the carve report's three-way split that
/// mirrors the user's mental model: what's there to BUILD, what's data/resources (maybe needed to RUN), and
/// everything else.</summary>
public enum InfraRole
{
    /// <summary>Build-system / toolchain orchestration: makefiles, CMake, linker scripts, startup asm, project
    /// files, TRACE32 scripts. Needed to build.</summary>
    BuildSystem,
    /// <summary>Data / resources: config, generated tables, device trees, blobs, prebuilt binaries. Needed to
    /// build and/or run; cannot be proven dead statically.</summary>
    Data,
    /// <summary>Anything else passed through for safety (docs, images, unknown).</summary>
    Other,
}

/// <summary>
/// Classifies non-code files for the keep-by-default emitter and the carve report. Two concerns:
///
/// <para><b>Garbage</b> (<see cref="IsGarbage"/>) — the ONLY category the emitter may drop without evidence from
/// the carve. The contract is deliberately AIRTIGHT: a file is garbage iff it cannot be a build or run input by
/// universal, toolchain-neutral convention — VCS metadata, compiler/IDE scratch dirs, dependency/coverage
/// artifacts, editor/OS junk, and logs/temp. Ambiguous binaries (<c>.o/.a/.so/.lib/.dll/.exe</c>, <c>bin/</c>,
/// <c>build/</c>) are expressly NOT garbage: they may be vendored prebuilts the build links, so dropping them
/// would break the "keep-by-default, evidence-based removal only" guarantee. The user restores anything with
/// <c>--keep-garbage</c> (all of it) or <c>--aux GLOB</c> (one file).</para>
///
/// <para><b>Role</b> (<see cref="RoleOf"/>, <see cref="LooksLikeBuildOutput"/>) — purely informational grouping
/// for the report; never drives keep/drop.</para>
/// </summary>
public static class InfraClassifier
{
    // Directory segments that are, by universal convention, generated/metadata — never a build input. Matched
    // against any segment of the relative path (case-insensitive), so the whole subtree is covered at once.
    private static readonly HashSet<string> GarbageDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".bzr", "_darcs",            // VCS metadata
        "CMakeFiles", "__pycache__", ".pytest_cache", ".mypy_cache", ".vs", // compiler / IDE scratch
    };

    // Extensions produced BY a build or an editor — never consumed by one. (.d dep-files, coverage, compiled
    // Python, editor backups, logs/temp.) Deliberately excludes .o/.a/.so/.lib/.obj/.dll/.exe: those may be
    // vendored prebuilts, so they are kept and only flagged for review (LooksLikeBuildOutput).
    private static readonly HashSet<string> GarbageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".d", ".gcda", ".gcno", ".gcov", ".pyc", ".pyo",    // dep / coverage / compiled-python
        ".bak", ".orig", ".swp", ".tmp", ".log",            // editor / build junk
    };

    private static readonly HashSet<string> GarbageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db",
    };

    /// <summary>True iff <paramref name="rel"/> is provably not a build/run input (safe to drop by default).
    /// <paramref name="rel"/> is a '/'-separated relative path.</summary>
    public static bool IsGarbage(string rel)
    {
        var segs = rel.Split('/', '\\');
        for (var i = 0; i < segs.Length - 1; i++)       // directory segments only (not the filename)
            if (GarbageDirs.Contains(segs[i])) return true;

        var name = segs[^1];
        if (GarbageNames.Contains(name)) return true;
        if (name.EndsWith("~", StringComparison.Ordinal)) return true;   // emacs/editor backup
        return GarbageExts.Contains(Path.GetExtension(name));
    }

    private static readonly HashSet<string> BuildSystemExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mk", ".mak", ".make", ".cmake",                   // make / cmake fragments
        ".ld", ".lds", ".ldscript", ".cmd", ".scat", ".sct", ".icf", // linker scripts / scatter
        ".s", ".asm",                                       // startup assembly
        ".cmm",                                             // TRACE32
        ".sln", ".vcxproj", ".vcproj", ".ninja",            // project / generator files
    };

    private static readonly HashSet<string> BuildSystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Makefile", "makefile", "GNUmakefile", "CMakeLists.txt", "Kconfig", "configure", "meson.build",
    };

    private static readonly HashSet<string> DataExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".inc", ".def", ".tab",                             // generated tables / data
        ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", // config
        ".dts", ".dtsi",                                    // device tree
        ".bin", ".hex", ".dat", ".img",                     // blobs / images
        ".a", ".o", ".lib", ".obj", ".so", ".dll",          // prebuilt binaries
    };

    /// <summary>The build role of a kept infrastructure file, for the report's grouping. Informational only.</summary>
    public static InfraRole RoleOf(string rel)
    {
        var name = Path.GetFileName(rel);
        if (BuildSystemNames.Contains(name)) return InfraRole.BuildSystem;
        var ext = Path.GetExtension(name);
        if (BuildSystemExts.Contains(ext)) return InfraRole.BuildSystem;
        if (DataExts.Contains(ext)) return InfraRole.Data;
        return InfraRole.Other;
    }

    // Extensions that usually denote a BUILD OUTPUT or a prebuilt binary. We keep these (they may be load-bearing
    // vendored artifacts) but flag them in the report so a user whose tree checks in a build/ dir can --exclude it.
    private static readonly HashSet<string> OutputExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".o", ".obj", ".a", ".lib", ".so", ".dll", ".exe", ".elf", ".bin", ".hex", ".map",
    };

    /// <summary>True if the file looks like a build output / prebuilt binary — kept, but worth a review note.</summary>
    public static bool LooksLikeBuildOutput(string rel) => OutputExts.Contains(Path.GetExtension(rel));
}
