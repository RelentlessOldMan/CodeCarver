using CodeCarver.Cli;
using CodeCarver.Core.Frontend;
using CodeCarver.Core.Preprocess;
using CodeCarver.Core.Reachability;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Silent misses the 1.0.174 independent review found: each carve dropped a file the build needs and verify said OK.
/// </summary>
public sealed class ReviewFindings174Tests
{
    const string Cat = "#define CAT2(a,b) a##b\n#define CAT(a,b) CAT2(a,b)\n";

    static void KeepsInst(TreeCarve t, string call, string extraToml = "")
    {
        t.W("main.c", $"int {call}(void);\nint main(void) {{ return {call}(); }}\n");
        var (code, _, err) = t.Carve(log: false, extraToml: extraToml);
        Assert.True(code == 0, err);
        Assert.True(t.Kept("inst.c"), "inst.c dropped\n" + t.VerifyLog);
    }

    // A template name defined in both #if branches: either branch may be the build's.
    [Theory]
    [InlineData("red_get")]
    [InlineData("blue_get")]
    public void TemplateName_DefinedInBothBranches(string call)
    {
        using var t = new TreeCarve();
        t.W("inst.c", Cat + "#ifdef USE_RED\n#define TNAME red\n#else\n#define TNAME blue\n#endif\n#include \"tmpl.h\"\n")
         .W("tmpl.h", "int CAT(TNAME,_get)(void) { return 1; }\n");
        KeepsInst(t, call);
    }

    // The macros come from an ordinary header the unit includes before the template.
    [Fact]
    public void TemplateName_FromAConfigHeader()
    {
        using var t = new TreeCarve();
        t.W("inst.c", "#include \"cfg.h\"\n#include \"tmpl.h\"\n")
         .W("cfg.h", "#ifndef CFG_H\n#define CFG_H\n" + Cat + "#define TNAME red\n#endif\n")
         .W("tmpl.h", "int CAT(TNAME,_get)(void) { return 1; }\n");
        KeepsInst(t, "red_get");
    }

    // A head after an #if block: its line must be its own, not one inside the (dead) block.
    [Fact]
    public void TemplateHead_AfterAnIfBlock_HasItsOwnLine()
    {
        using var t = new TreeCarve();
        t.W("inst.c", Cat + "#define TNAME red\nint inst_marker;\n#if 0\nold one\nold two\n#endif\nint CAT(TNAME,_get)(void) { return 1; }\n");
        KeepsInst(t, "red_get", "[builds.b]\ndefines = [\"SOMETHING=1\"]\n");
        Assert.Equal(new[] { ("red_get", 9) }, TemplateInstances.Find(File.ReadAllText(t.CarvedPath("inst.c")).Replace("\r", ""), _ => null));
    }

    // Two builds rename one definition two ways: both names are defined.
    [Fact]
    public void CommandLineRename_EveryValueCounts()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int blue_rite(void);\nint main(void) { return blue_rite(); }\n")
         .W("rite.c", "int secret_rite(void) { return 7; }\n")
         .W("Makefile", "red:\n\tcc -Dsecret_rite=red_rite -o red main.c rite.c\nblue:\n\tcc -Dsecret_rite=blue_rite -o blue main.c rite.c\n");
        var (code, _, err) = t.Carve(log: false);
        Assert.True(code == 0, err);
        Assert.True(t.Kept("rite.c"), "rite.c dropped\n" + t.VerifyLog);
    }

    // #ifndef X / #define X / #else is not an include guard: the #else branch is live when X is defined.
    [Fact]
    public void IfndefDefineElse_IsNotAnIncludeGuard()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int fast_impl(void);\nint main(void) { return fast_impl(); }\n")
         .W("compat.c", "#ifndef USE_FAST\n#define USE_FAST 0\n#else\nint fast_impl(void) { return 1; }\n#endif\n#if 0\nold\n#endif\n");
        var (code, _, err) = t.Carve(log: false);
        Assert.True(code == 0, err);
        Assert.True(t.Kept("compat.c"), "compat.c dropped\n" + t.VerifyLog);
        Assert.Contains("fast_impl", PreprocessorScanner.BlankAlwaysDead(File.ReadAllText(Path.Combine(t.Src, "compat.c"))));
    }

    // An "#if 0" inside a C++ raw string is not a directive; an #if that never closes blanks nothing.
    [Fact]
    public void IfInsideARawString_BlanksNothing()
    {
        var text = "const char* s = R\"(\n#if 0\nprecision mediump float;\n)\";\nint later_fn() { return 3; }\n";
        Assert.Contains("later_fn", PreprocessorScanner.BlankAlwaysDead(text));
        var root = Path.Combine(Path.GetTempPath(), "cc-rv-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "main.cpp"), "int later_fn();\nint main() { return later_fn(); }\n");
            File.WriteAllText(Path.Combine(root, "src", "shader.cpp"), text);
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace('\\', '/')}\"\n[common]\nentryPoints = [\"main\"]\nlanguages = [\"cpp\"]\n");
            var so = new StringWriter(); var se = new StringWriter();
            Assert.Equal(0, CarveCommand.Run(new[] { "carve", Path.Combine(root, "src"), "--config", cfg }, so, se));
            Assert.True(File.Exists(Path.Combine(root, "out", "carved", "shader.cpp")), "shader.cpp dropped");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- Second review: build inputs, generated code, link flags, verify. ---

    // An incremental build: the log compiles only main.c and the trace opens main.c and helper.o. helper.c was
    // compiled by an earlier build, so the trace is not complete, and helper.c must still be read.
    [Fact]
    public void IncrementalBuild_ObjectWhoseSourceWasNeverOpened_MakesTheTraceIncomplete()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int helper(void);\nint main(void) { return helper(); }\n")
         .W("helper.c", "int helper(void) { return 1; }\n")
         .W("Makefile", "app: main.o helper.o\n\tgcc -o app main.o helper.o\n");
        t.LogLines.Add($"cd {t.S("")} && gcc -c {t.S("main.c")} -o main.o");
        t.LogLines.Add($"cd {t.S("")} && gcc -o app main.o helper.o");
        t.TracePaths = new List<string> { t.S("Makefile"), t.S("main.c"), t.S("main.o"), t.S("helper.o") };
        var (code, _, err) = t.Carve();
        Assert.True(code == 0, err);
        Assert.True(t.Kept("helper.c"), "helper.c dropped\n" + t.VerifyLog + err);
        Assert.Contains("trace is incomplete", err);
    }

    // C written by a script through a quoted here-document (no $ expansion): the usual way.
    [Theory]
    [InlineData("gen.sh", "#!/bin/sh\ncat > \"$1\" <<'EOF'\nint gen_q(void) { return omen_gen(); }\nEOF\n")]
    [InlineData("gen.sh", "#!/bin/sh\ncat > \"$1\" <<\"EOF\"\nint gen_q(void) { return omen_gen(); }\nEOF\n")]
    [InlineData("gen.pl", "#!/usr/bin/perl\nprint OUT <<'END';\nint gen_p(void) { return omen_gen(); }\nEND\n")]
    [InlineData("mkgen", "#!/usr/bin/env perl\nprint OUT <<'END';\nint gen_p(void) { return omen_gen(); }\nEND\n")]
    [InlineData("gen.awk", "BEGIN { print \"int gen_a(void) { return omen_gen(); }\" }\n")]
    public void GeneratorScript_CallsAreRoots(string script, string text)
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void) { return 0; }\n").W("omen.c", "int omen_gen(void) { return 3; }\n").W(script, text);
        var (code, _, err) = t.Carve(log: false);
        Assert.True(code == 0, err);
        Assert.True(t.Kept("omen.c"), "omen.c dropped\n" + err);
    }

    // Every spelling of "the link needs this symbol".
    [Fact]
    public void LinkFlags_EverySpelling()
    {
        var line = "gcc -Xlinker --require-defined -Xlinker omen_x -u omen_u -Wl,--require-defined -Wl,omen_w "
                 + "-Xlinker --defsym -Xlinker omen_a=omen_d -Wl,-e,omen_e -o app main.o\n";
        var names = LinkFlags.RequiredSymbols(line, linkerScript: false).Select(s => s.Name).ToList();
        foreach (var n in new[] { "omen_x", "omen_u", "omen_w", "omen_d", "omen_e" }) Assert.Contains(n, names);
        // Not a link line: `set -e` / `grep -e` name nothing.
        Assert.Empty(LinkFlags.RequiredSymbols("set -e\ngrep -e pattern file\n", linkerScript: false));
    }

    [Fact]
    public void LinkFlags_LinearInTheLogSize()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 40_000; i++) sb.Append("gcc -Wl,--undefined=probe_").Append(i).Append(" -o app main.o\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LinkFlags.RequiredSymbols(sb.ToString(), linkerScript: false);
        Assert.Equal(40_000, r.Count);
        Assert.Equal(40_000, r[^1].Line);
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"{sw.ElapsedMilliseconds} ms");
    }

    // A unit-test mock __wrap_X in an unbuilt file is not a wrapper the link uses unless something says --wrap=X.
    [Fact]
    public void WrapMock_WithoutAWrapFlag_IsNoVerifyFailure()
    {
        using var t = new TreeCarve();
        t.W("main.c", "void uart_put(int c);\nint main(void) { uart_put(1); return 0; }\n")
         .W("uart.c", "void uart_put(int c) { (void)c; }\n")
         .W("tests/test_uart.c", "void __wrap_uart_put(int c) { (void)c; }\n");
        t.TracePaths = new List<string> { t.S("main.c"), t.S("uart.c") };
        var (code, _, err) = t.Carve(log: false);
        Assert.True(code == 0, err + t.VerifyLog);
    }

    // A placeholder's identifier is ASCII whatever the file is called: older compilers reject UTF-8 identifiers.
    [Fact]
    public void Placeholder_IdentifierIsAscii()
    {
        using var t = new TreeCarve();
        t.W("main.c", "int main(void) { return 0; }\n").W("café.c", "int unused_cafe(void) { return 1; }\n")
         .W("Makefile", "all:\n\tgcc main.c café.c\n");
        var (code, _, err) = t.Carve(log: false);
        Assert.True(code == 0, err);
        Assert.True(t.Placeholder("café.c"));
        var typedef = File.ReadAllLines(t.CarvedPath("café.c")).Single(l => l.StartsWith("typedef"));
        Assert.All(typedef, c => Assert.True(c < 128, typedef));
    }

    // One file's weak declaration does not make another file's reference weak.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WeakReference_IsPerFile(bool strongUserToo, bool weak)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-weak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string, string) W(string rel, string text) { var p = Path.Combine(root, rel); File.WriteAllText(p, text); return (rel, p); }
            var emitted = new List<(string, string)> { W("a.c", "__attribute__((weak)) void hook(void);\nvoid a(void) { if (hook) hook(); }\n") };
            if (strongUserToo) emitted.Add(W("b.c", "void hook(void);\nvoid b(void) { hook(); }\n"));
            var dropped = new[] { W("hook.c", "void hook(void) { }\n") };
            var v = Assert.Single(EmittedLinkCheck.Run(emitted, dropped).Violations, x => x.Name == "hook");
            Assert.Equal(weak, v.Weak);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // A string continued with a backslash before CR LF still ends at its closing quote.
    [Fact]
    public void CodeOnly_StringContinuedOverCrLf()
    {
        var text = "const char *s = \"abc\\\r\ndef\"; helper();\r\n";
        var code = SourceText.CodeOnly(text);
        Assert.Equal(text.Length, code.Length);
        Assert.Contains("helper();", code);
    }

    // A head split across an #ifdef leaves the parenthesis count where the #if found it, so a later #else branch at
    // file scope is not read as a parameter-list conditional and blanked.
    [Fact]
    public void ParamListSplit_DoesNotLeakIntoLaterConditionals()
    {
        var text = "int h(int, int);\n#ifdef WIDE\nint f(int a, long b,\n#else\nint f(int a,\n#endif\n      int c) { return a + c; }\n\n"
                 + "int g(void) {\n    return h(1,\n#ifdef Q\n      2\n#else\n      3\n#endif\n    );\n}\n\n"
                 + "#ifdef USE_X\nint x_only(void) { return 1; }\n#else\nint y_only(void) { return 2; }\n#endif\n";
        var blanked = ParamListConditionals.Blank(text, out _);
        Assert.Contains("y_only", blanked);
        Assert.Contains("x_only", blanked);
    }
}
