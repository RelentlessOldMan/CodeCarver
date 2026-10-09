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
        var path = Path.Combine(Path.GetTempPath(), "cc-be-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, Log);
        try
        {
            var got = RunScript("-Path", path);
            if (got is null) return;   // no PowerShell on this machine
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

    /// <summary>With the original and carved trees it says why each name is missing: a function the carve took out of
    /// the same file (its use inside an #if), a name made by ## pasting in a macro, and a variable whose only file
    /// was dropped.</summary>
    [Fact]
    public void Script_SaysWhyEachNameIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-bw-" + Guid.NewGuid().ToString("N"));
        void Put(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        const string cfg = "#ifndef CFG_H\n#define CFG_H\n#define PASTE(a,b) a##b\n#endif\n";
        Put("orig/src/cfg.h", cfg);
        Put("orig/src/a.c", "#include \"cfg.h\"\nstatic int helper(int x);\nint keep(void) {\n#if FEATURE\n  return helper(1);\n#endif\n  return 0;\n}\n"
                            + "static int helper(int x) { return x; }\nint tbl_ok(void) { return PASTE(tbl_, gone)(); }\n");
        Put("orig/src/b.c", "int tbl_gone(void) { return 3; }\nint other_var = 5;\n");
        Put("orig/src/c.c", "int user(void) { return other_var; }\n");
        Put("out/aggr/carved/src/cfg.h", cfg);
        Put("out/aggr/carved/src/a.c", "#include \"cfg.h\"\nint keep(void) {\n#if FEATURE\n  return helper(1);\n#endif\n  return 0;\n}\n"
                                       + "int tbl_ok(void) { return PASTE(tbl_, gone)(); }\n");
        Put("out/aggr/carved/src/c.c", "int user(void) { return other_var; }\n");
        Put("out/aggr/build-output.txt",
            "src/a.c:4:10: error: implicit declaration of function 'helper' [-Wimplicit-function-declaration]\n"
            + "src/a.c:8:33: error: implicit declaration of function 'tbl_gone'; did you mean 'tbl_ok'? [-Wimplicit-function-declaration]\n"
            + "/build/copy/src/c.c:1:25: error: 'other_var' undeclared (first use in this function)\n");
        try
        {
            var got = RunScript("-Path", Path.Combine(root, "out/aggr/build-output.txt"),
                                "-Original", Path.Combine(root, "orig"), "-Carved", Path.Combine(root, "out/aggr"));
            if (got is null) return;
            var want = new Dictionary<string, int>
            {
                ["errors"] = 3, ["Undeclared"] = 3,
                ["why.Undeclared.function"] = 2, ["why.Undeclared.identifier"] = 1,
                ["why.Undeclared.def.lostInUseFile"] = 1, ["why.Undeclared.def.inDroppedFile"] = 2,
                ["why.Undeclared.use.nameOnLine"] = 2, ["why.Undeclared.use.nameNotOnLine"] = 1,
                ["why.Undeclared.use.inConditional"] = 1, ["why.Undeclared.use.unconditional"] = 2,
            };
            Assert.Equal(want.OrderBy(k => k.Key, StringComparer.Ordinal), got.OrderBy(k => k.Key, StringComparer.Ordinal));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    /// <summary>The script's "key = N" lines, or null when there is no PowerShell to run it.</summary>
    static Dictionary<string, int>? RunScript(params string[] args)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        var script = FindScript();
        Assert.NotNull(script);
        var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script! }.Concat(args)) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        using var proc = p!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, output + stderr.Result);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split(" = "))
                     .Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
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
