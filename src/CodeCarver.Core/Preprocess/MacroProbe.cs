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
            if (useStdin) p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            var table = new MacroTable();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("#define ", StringComparison.Ordinal)) continue;
                var rest = line[8..];
                var sp = rest.IndexOf(' ');
                if (sp < 0) table.Set(ObjectName(rest), "1");
                else table.Set(ObjectName(rest[..sp]), rest[(sp + 1)..].Trim());
            }
            return table;
        }
        catch
        {
            return null;
        }
    }

    private static string ObjectName(string lhs)
    {
        var paren = lhs.IndexOf('(');
        return (paren < 0 ? lhs : lhs[..paren]).Trim();
    }
}
