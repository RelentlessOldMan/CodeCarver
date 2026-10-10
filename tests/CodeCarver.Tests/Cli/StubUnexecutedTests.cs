using CodeCarver.Core.Emit;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// <c>stubUnexecuted</c>: a C function the run trace never names keeps its signature, its body becomes a stub, and
/// what only that body used falls out of the carve. Entry points and what ran are never stubbed. All names invented.
/// </summary>
public sealed class StubUnexecutedTests
{
    const string Header = "int run(int x);\nint fail_path(int code, int extra);\nint never(void);\n";
    const string Lib = "#include \"lib.h\"\n"
                     + "static int only_in_fail(int x) { return x * 3; }\n"
                     + "int g_fail_count;\n"
                     + "int run(int x) { return x + 1; }\n"
                     + "int fail_path(int code, int extra)\n"
                     + "{\n"
                     + "#ifdef VERBOSE\n"
                     + "    g_fail_count++;\n"
                     + "#endif\n"
                     + "    g_fail_count += extra;\n"
                     + "    return only_in_fail(code);\n"
                     + "}\n"
                     + "int never(void) { return 9; }\n";
    const string Main = "#include \"lib.h\"\nint main(void) { int r = run(1); if (r < 0) r = fail_path(r, 2); return r; }\n";

    static (TreeCarve T, int Code, string Out, string Err) Carve(string trace, string stage = "[stages.t]\ncarveSourceFileContents = true\nstubUnexecuted = true\n")
    {
        var t = new TreeCarve();
        t.W("lib.h", Header).W("lib.c", Lib).W("main.c", Main).Compile("lib.c").Compile("main.c");
        var tracePath = Path.Combine(t.Root, "run.log");
        File.WriteAllText(tracePath, trace);
        var (code, o, e) = t.Carve(extraToml: $"[runs.r]\nrunTraceLogs = [\"{tracePath.Replace('\\', '/')}\"]\n" + stage);
        return (t, code, o, e);
    }

    [Fact]
    public void UnexecutedFunction_IsStubbed_AndWhatOnlyItUsedIsCut()
    {
        var (t, code, o, e) = Carve("run\n");   // main is the entry point: never stubbed, though the trace omits it
        using (t)
        {
            Assert.True(code == 0, o + e);
            var lib = File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "lib.c"));
            Assert.Contains("int fail_path(int code, int extra)", lib);
            Assert.Contains(StubBodies.Marker, lib);
            Assert.Contains("(void)code; (void)extra;", lib);
            Assert.DoesNotContain("only_in_fail", lib);        // used only by the stubbed body
            Assert.DoesNotContain("never", lib);               // unreached anyway
            Assert.Contains("int run(int x) { return x + 1; }", lib);
            var main = File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "main.c"));
            Assert.Contains("fail_path(r, 2)", main);
            Assert.DoesNotContain(StubBodies.Marker, main);
            var summary = File.ReadAllText(Path.Combine(t.Root, "out", "t", "codecarver", "summary.txt"));
            Assert.Contains("stage0.stub.functions = 1", summary);
            Assert.Contains("stage0.verify.failed = 0", summary);

            // It builds, warnings as errors.
            var cc = Toolchain.Gcc();
            if (cc is null) return;
            var carved = Path.Combine(t.Root, "out", "t", "carved");
            var (bc, bo) = Toolchain.Run(cc, new[] { "-Wall", "-Wextra", "-Werror", "-o", "app.exe", "main.c", "lib.c" }, carved);
            Assert.True(bc == 0, bo + "\n---\n" + lib);
        }
    }

    [Fact]
    public void WhatRan_IsNeverStubbed()
    {
        var (t, code, o, e) = Carve("main\nrun\nfail_path\nonly_in_fail\n");
        using (t)
        {
            Assert.True(code == 0, o + e);
            var lib = File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "lib.c"));
            Assert.DoesNotContain(StubBodies.Marker, lib);
            Assert.Contains("return only_in_fail(code);", lib);
            Assert.Contains("stage0.stub.functions = 0", File.ReadAllText(Path.Combine(t.Root, "out", "t", "codecarver", "summary.txt")));
        }
    }

    /// <summary>A trace names linker symbols. What ran under another name than the source writes is still spared: an asm
    /// label, <c>#pragma redefine_extname</c>, an object-like rename macro, a <c>$</c> in the name (eldritch oracle:
    /// all four were stubbed and the run hung in one).</summary>
    [Fact]
    public void WhatRanUnderAnotherSymbol_IsNeverStubbed()
    {
        using var t = new TreeCarve();
        t.W("ren.h", "#define summon_fn real_summon\nint summon_fn(void);\nint hidden_name(void) __asm__(\"surface_name\");\n"
                   + "int old_name(void);\nint dollar$fn(void);\nint idle(void);\n")
         .W("ren.c", "#include \"ren.h\"\nint hidden_name(void) { return 1; }\n#pragma redefine_extname old_name new_name\n"
                   + "int old_name(void) { return 2; }\nint summon_fn(void) { return 3; }\nint dollar$fn(void) { return 4; }\n"
                   + "int idle(void) { return 5; }\n")
         .W("main.c", "#include \"ren.h\"\nint main(void) { return hidden_name() + old_name() + summon_fn() + dollar$fn() + (main == 0 ? idle() : 0); }\n")
         .Compile("ren.c").Compile("main.c");
        var tracePath = Path.Combine(t.Root, "run.log");
        File.WriteAllText(tracePath, "main\nsurface_name\nnew_name\nreal_summon\ndollar$fn\n");
        var (code, o, e) = t.Carve(extraToml: $"[runs.r]\nrunTraceLogs = [\"{tracePath.Replace('\\', '/')}\"]\n"
                                            + "[stages.t]\ncarveSourceFileContents = true\nstubUnexecuted = true\n");
        Assert.True(code == 0, o + e);
        var ren = File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "ren.c"));
        foreach (var body in new[] { "return 1;", "return 2;", "return 3;", "return 4;" }) Assert.Contains(body, ren);
        Assert.DoesNotContain("return 5;", ren);   // idle never ran: the one stub
        var summary = File.ReadAllText(Path.Combine(t.Root, "out", "t", "codecarver", "summary.txt"));
        Assert.Contains("stage0.stub.functions = 1", summary);
        Assert.Contains("stage0.stub.traceNamesWithoutDefinition = 0", summary);
    }

    [Fact]
    public void OtherStages_DoNotStub()
    {
        var (t, code, o, e) = Carve("run\n", "[stages.a]\ncarveSourceFileContents = true\n[stages.t]\ncarveSourceFileContents = true\nstubUnexecuted = true\n");
        using (t)
        {
            Assert.True(code == 0, o + e);
            var a = File.ReadAllText(Path.Combine(t.Root, "out", "a", "carved", "lib.c"));
            Assert.DoesNotContain(StubBodies.Marker, a);
            Assert.Contains("only_in_fail", a);
            Assert.Contains(StubBodies.Marker, File.ReadAllText(Path.Combine(t.Root, "out", "t", "carved", "lib.c")));
        }
    }

    [Fact]
    public void Config_NeedsSourceCarvingAndAFunctionTrace()
    {
        using (var t = new TreeCarve())
        {
            t.W("main.c", Main);
            var (code, o, e) = t.Carve(extraToml: "[stages.t]\ncarveSourceFileContents = true\nstubUnexecuted = true\n");
            Assert.True(code == 2, o + e);
            Assert.Contains("needs a function trace", e);
        }
        var (t2, code2, o2, e2) = Carve("run\n", "[stages.t]\nstubUnexecuted = true\n");
        using (t2)
        {
            Assert.True(code2 == 2, o2 + e2);
            Assert.Contains("needs carveSourceFileContents = true", e2);
        }
    }

    [Theory]
    [InlineData("int f(int a, char *b[], void (*cb)(int))\n{\n    return a;\n}\n", "a,b,cb")]
    [InlineData("int f(int, size_t)\n{\n    return 0;\n}\n", "")]
    [InlineData("int f(void)\n{\n    return 0;\n}\n", "")]
    [InlineData("int f(a, b)\n    int a;\n    char *b;\n{\n    return a;\n}\n", "a,b")]
    public void StubBodies_FindsTheBodyAndItsParameters(string text, string names)
    {
        var lines = text.Split('\n').Length - 1;
        var b = StubBodies.Find(new StubBodies.Source(text), 1, lines, "f");
        Assert.NotNull(b);
        Assert.Equal(names, string.Join(",", b!.Value.Params));
        var stubbed = StubBodies.Apply(text, new[] { b.Value });
        Assert.Equal(text.Split('\n').Length, stubbed.Split('\n').Length);   // every line kept
        Assert.Contains(StubBodies.Marker, stubbed);
        Assert.DoesNotContain("return", stubbed);
    }

    [Theory]
    [InlineData("int f(int a)\n{\n#if X\n    if (a) {\n#else\n    if (!a) {\n#endif\n        a++;\n    }\n    return a;\n}\n")]   // #if splits the braces
    [InlineData("int f(int a)\n{\n#if X\n    return a;\n}\n#endif\n")]                                                              // #if not closed inside
    [InlineData("int g(int a)\n{\n    return a;\n}\n")]                                                                               // not this name
    public void StubBodies_LeavesWhatItCannotReadCleanly(string text)
    {
        var lines = text.Split('\n').Length - 1;
        Assert.Null(StubBodies.Find(new StubBodies.Source(text), 1, lines, "f"));
    }
}
