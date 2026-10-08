using System.Diagnostics;
using CodeCarver.Core.Diagnostics;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>tools/builderrors/count-build-errors.ps1 counts a build's errors by kind without a carve. It mirrors
/// <see cref="BuildErrors"/>; this keeps the two in step.</summary>
public sealed class CountBuildErrorsScriptTests
{
    const string Log =
        "make[1]: Entering directory '/x'\n"
        + "src/a.c:3:13: error: 'modd_helper' declared 'static' but never defined [-Werror=unused-function]\n"
        + "src/a.c:9:1: error: expected ';' before '}' token\n"
        + "src/a.c:9:1: error: expected ';' before '}' token\n"
        + "src/b.c:4:5: error: redefinition of 'modd_x'\n"
        + "src/b.c:7:5: error: \u2018modd_y\u2019 undeclared (first use in this function)\n"
        + "src/b.c:8:5: error: unknown type name 'modd_t'\n"
        + "src/c.c:1:10: fatal error: modd.h: No such file or directory\n"
        + "src/d.c:2:1: warning: unused variable 'q' [-Wunused-variable]\n"
        + "src/d.c:5:1: error: something else entirely\n"
        + "/usr/bin/ld: obj/e.o:(.text+0x9): undefined reference to `modd_z'\n"
        + "C:/tc/bin/arm-none-eabi-ld.exe: obj/f.o:(.text+0x4): undefined reference to `modd_w'\n"
        + "lnk.obj : error LNK2019: unresolved external symbol modd_v referenced in function main\n";

    [Fact]
    public void Script_CountsLikeTheCarve()
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        var script = FindScript();
        Assert.NotNull(script);
        var path = Path.Combine(Path.GetTempPath(), "cc-be-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, Log);
        try
        {
            var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script!, "-Path", path }) psi.ArgumentList.Add(a);
            Process? p;
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { return; }   // no PowerShell on this machine
            using var proc = p!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, output + proc.StandardError.ReadToEnd());

            var got = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split(" = "))
                            .Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
            var errors = BuildErrors.Parse(Log);
            var want = errors.GroupBy(e => e.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count());
            foreach (var g in errors.Select(BuildErrors.WerrorFlagOf).Where(f => f is not null).GroupBy(f => f!))
                want[$"WarningAsError.{g.Key}"] = g.Count();
            want["errors"] = errors.Count;
            Assert.Equal(want.OrderBy(k => k.Key, StringComparer.Ordinal), got.OrderBy(k => k.Key, StringComparer.Ordinal));
            Assert.Equal(3, want["UndefinedReference"]);
        }
        finally { File.Delete(path); }
    }

    static string? FindScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "builderrors", "count-build-errors.ps1");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
