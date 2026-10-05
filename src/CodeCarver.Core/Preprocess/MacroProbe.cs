using System.Diagnostics;

namespace CodeCarver.Core.Preprocess;

/// <summary>
/// Asks a real compiler for the macros it has defined — <c>cc -dM -E</c> — so <c>#ifdef</c> resolution
/// can run against the compiler's actual configuration (predefined macros like <c>__GNUC__</c> and the
/// target macros, plus the build's <c>-D</c> flags) rather than a hand-listed guess. The resulting
/// table is meant to be used in CLOSED-WORLD mode: because it is the compiler's complete macro set for
/// these flags, an absent macro really is undefined.
///
/// Limitation: probing empty input captures predefined + command-line macros, not macros a specific
/// header defines only when included. For those, pass a representative source file via
/// <paramref name="throughFile"/> so its includes are processed too. Needs a compiler present — this is
/// an optional tightening step, never a product dependency.
/// </summary>
public static class MacroProbe
{
    /// <summary>Probe <paramref name="compiler"/> for its macro table. <paramref name="extraArgs"/> are
    /// passed through (e.g. "-DFOO=1", "-Iinc", a target flag). If <paramref name="throughFile"/> is a
    /// real path it is preprocessed (so its headers' macros are captured); otherwise empty input is used.</summary>
    /// <summary>Returns the probed macro table, or <c>null</c> if the compiler can't be run — callers
    /// must treat null as "probe failed, don't enable closed-world resolution" (an empty table under
    /// closed-world would wrongly treat every macro as undefined).</summary>
    public static MacroTable? Probe(string compiler, IEnumerable<string>? extraArgs = null, string? throughFile = null)
    {
        try
        {
            var exe = File.Exists(compiler) ? Path.GetFullPath(compiler) : compiler; // resolve relative paths; bare names hit PATH
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-dM");
            psi.ArgumentList.Add("-E");
            if (extraArgs is not null)
                foreach (var a in extraArgs) psi.ArgumentList.Add(a);

            var useStdin = throughFile is null || !File.Exists(throughFile);
            if (useStdin) { psi.ArgumentList.Add("-x"); psi.ArgumentList.Add("c"); psi.ArgumentList.Add("-"); }
            else psi.ArgumentList.Add(Path.GetFullPath(throughFile!));

            using var p = Process.Start(psi);
            if (p is null) return null;
            // Read both pipes concurrently (a full stderr pipe would otherwise deadlock a ReadToEnd on stdout)
            // and enforce the timeout with a kill, so a hung driver can't hang the carve (review RB3).
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (useStdin) p.StandardInput.Close();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            p.WaitForExit();
            var output = outTask.Result;
            _ = errTask.Result;
            // A driver that failed, or printed no #define at all, is not a GCC/Clang-compatible probe: an empty
            // table under closed-world would treat every macro as undefined (review PP4).
            if (p.ExitCode != 0) return null;

            var table = new MacroTable();
            var any = false;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("#define ", StringComparison.Ordinal)) continue;
                any = true;
                var rest = line[8..];
                var sp = rest.IndexOf(' ');
                if (sp < 0) table.Set(ObjectName(rest), "1");
                else table.Set(ObjectName(rest[..sp]), rest[(sp + 1)..].Trim());
            }
            if (!any) return null;
            return table;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The flags of a compile command that change the compiler's predefined macros: language standard,
    /// machine/target, optimisation, code-generation (-f), target triple and sysroot. Order-preserving.</summary>
    public static IReadOnlyList<string> TargetFlags(IReadOnlyList<string> args)
    {
        var r = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-target" or "--target" or "--sysroot" or "-isysroot" or "-x")
            { if (i + 1 < args.Count) { r.Add(a); r.Add(args[++i]); } continue; }
            if (a.StartsWith("-m", StringComparison.Ordinal) || a.StartsWith("-std=", StringComparison.Ordinal)
                || a.StartsWith("-O", StringComparison.Ordinal) || a.StartsWith("-f", StringComparison.Ordinal)
                || a.StartsWith("--target=", StringComparison.Ordinal) || a.StartsWith("--sysroot=", StringComparison.Ordinal)
                || a is "-ansi" or "-pthread")
                r.Add(a);
        }
        return r;
    }

    /// <summary>Probed names whose value depends on flags the probe may not share with every TU (language
    /// standard, target CPU/FPU, optimisation, code model). Treated as unknown when the probe can't be matched
    /// to the build (review PP2).</summary>
    public static IEnumerable<string> FlagDependentNames(MacroTable probed)
    {
        string[] exact = { "__STDC_VERSION__", "__STRICT_ANSI__", "__OPTIMIZE__", "__OPTIMIZE_SIZE__", "__NO_INLINE__",
            "__PIC__", "__pic__", "__PIE__", "__pie__", "__FAST_MATH__", "__CHAR_UNSIGNED__", "__EXCEPTIONS",
            "__GNUC_STDC_INLINE__", "__GNUC_GNU_INLINE__", "__SOFTFP__", "__VFP_FP__", "__ARMEL__", "__ARMEB__",
            "__STDC_HOSTED__", "__SSP__", "__SSP_STRONG__", "__SANITIZE_ADDRESS__" };
        string[] prefixes = { "__ARM", "__thumb", "__aarch64", "__SSE", "__AVX", "__MMX", "__x86", "__i386", "__i486",
            "__i586", "__i686", "__amd64", "__riscv", "__mips", "__AARCH64", "__GXX", "__cpp_", "__STDCPP",
            "__LP64", "__ILP32", "__SIZEOF_", "__FP_FAST", "__GCC_IEC", "__ARM_FEATURE", "__FLT_EVAL" };
        foreach (var n in exact) yield return n;
        foreach (var n in probed.Names)
            if (prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))) yield return n;
    }

    private static string ObjectName(string lhs)
    {
        var paren = lhs.IndexOf('(');
        return (paren < 0 ? lhs : lhs[..paren]).Trim();
    }
}
