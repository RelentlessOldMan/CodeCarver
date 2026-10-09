using CodeCarver.Cli;
using CodeCarver.Frontend;
using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// Function definitions tree-sitter cannot parse as definitions, found by the 1.0.159 link check on a real
/// firmware tree (work eval): the function never became a graph node, so its file was dropped while a kept file
/// still called it (exit 3). Pattern 1 is a pre-C99 implicit-int definition, pattern 2 a function head split
/// across #ifdef/#else/#endif with the body's '{' after the #endif.
/// </summary>
public sealed class HiddenDefinitionTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-hid-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work() => Directory.CreateDirectory(Src);
        public void W(string rel, string text) => File.WriteAllText(Path.Combine(Src, rel), text);
        public (int Code, string Out) Carve(string extra = "")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so + "\n" + se);
        }
        public string Out(string rel) => Path.Combine(Root, "out", rel);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void ImplicitIntDefinition_IsAFunction_AndItsFileIsKept()
    {
        using var w = new Work();
        w.W("main.c", "void helper(int x, int y);\nint main(void){ helper(1,2); return 0; }\n");
        w.W("knr.c", "helper(int x, int y) { (void)x; (void)y; leaf(); }\n");
        w.W("leaf.c", "void leaf(void) { }\n");
        w.W("dead.c", "void dead(void) { }\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/knr.c")), o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);   // the recovered body's callee is reached
        Assert.False(File.Exists(w.Out("carved/dead.c")), o);
    }

    [Fact]
    public void KAndRParameterDeclarations_AreRecovered()
    {
        using var w = new Work();
        w.W("main.c", "int helper();\nint main(void){ return helper(1, \"x\"); }\n");
        w.W("knr.c", "/* legacy */\nstatic int unused;\nhelper(a, b)\n  int a;\n  char *b;\n{\n  return a + leaf();\n}\n");
        w.W("leaf.c", "int leaf(void) { return 0; }\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/knr.c")), o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);
    }

    [Fact]
    public void HeadSplitAcrossIfdef_DefinesEveryBranchName()
    {
        using var w = new Work();
        w.W("main.c", "void helper(int x);\nint main(void){ helper(1); return 0; }\n");
        w.W("split.c",
            "#ifdef VARIANT_SDI\nvoid helper_sdi(int x)\n#else\nvoid helper(int x)\n#endif\n{\n  (void)x;\n  leaf();\n}\n");
        w.W("leaf.c", "void leaf(void) { }\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/split.c")), o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);
    }

    [Fact]
    public void HeadSplitAcrossIfdef_ReachedThroughTheOtherBranchName_StillKeepsTheBody()
    {
        using var w = new Work();
        // main calls the #if branch's name; the body's callee is attributed to one of the twins, so the twins
        // must be linked or leaf.c would be lost.
        w.W("main.c", "void helper_sdi(int x);\nint main(void){ helper_sdi(1); return 0; }\n");
        w.W("split.c",
            "#ifdef VARIANT_SDI\nvoid helper_sdi(int x)\n#elif defined(OTHER)\nstatic void helper_o(int x)\n#else\nvoid helper(int x)\n#endif\n{\n  (void)x;\n  leaf();\n}\n");
        w.W("leaf.c", "void leaf(void) { }\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);
    }

    [Fact]
    public void UnusedSplitHead_IsRemovedWhole_ByIntraFileCarving()
    {
        using var w = new Work();
        w.W("main.c", "int other(void);\nint main(void){ return other(); }\n");
        w.W("split.c",
            "int other(void) { return 1; }\n"
            + "#ifdef VARIANT_SDI\nvoid helper_sdi(int x)\n#else\nvoid helper(int x)\n#endif\n{\n  (void)x;\n}\n"
            + "int tail(void) { return other(); }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        var text = File.ReadAllText(w.Out("aggressive/carved/split.c"));
        Assert.Contains("int other(void)", text);
        Assert.DoesNotContain("helper", text);    // both heads go with the body, not just the body
        Assert.DoesNotContain("(void)x", text);
        Assert.Equal(text.Count(c => c == '{'), text.Count(c => c == '}'));
    }

    [Fact]
    public void KAndRWithUndeclaredParameters_DefinesTheName_AndKeepsItsFile()
    {
        // Work eval, 1.0.162: `add(a, b) { ... }`, every parameter an implicit int, no declarations.
        using var w = new Work();
        w.W("main.c", "int add();\nint main(void){ return add(1, 2); }\n");
        w.W("add.c", "/* legacy */\nadd(a, b)\n{\n  return a + leaf();\n}\n");
        w.W("leaf.c", "int leaf(void) { return 0; }\n");
        w.W("dead.c", "void dead(void) { }\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/add.c")), o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);   // the body's calls count (as its file's)
        Assert.False(File.Exists(w.Out("carved/dead.c")), o);
    }

    [Fact]
    public void BareHeadThatIsReallyAMacro_IsNeverCut_ByIntraFileCarving()
    {
        // The same text can be a macro-headed body from a header outside the tree. Its "definition" is never called,
        // so it must stay with its file rather than be carved as an unreached function.
        using var w = new Work();
        w.W("main.c", "void task_body(void);\nint main(void){ task_body(); return 0; }\n");
        w.W("task.c", "void task_body(void) { }\nportTASK_FUNCTION(prvIdle, pvParameters)\n{\n  idle_hook();\n}\n");
        w.W("hook.c", "void idle_hook(void) { }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        var text = File.ReadAllText(w.Out("aggressive/carved/task.c"));
        Assert.Contains("idle_hook();", text);
        Assert.True(File.Exists(w.Out("aggressive/carved/hook.c")), o);
    }

    [Theory]
    [InlineData("int\nhelper(int x) { return x; }\n")]                 // return type on the line above
    [InlineData("FRAMEWORK_FN(os_shell, \"doc\") { run(); }\n")]        // macro-headed body
    [InlineData("Framework_fn(os_shell, \"doc\") { run(); }\n")]        // ... even in mixed case: a literal
    [InlineData("REGISTER(a, b) { run(); }\n")]                         // all-caps name
    [InlineData("static portTASK_FUNCTION( prvIdleTask, pvParameters ) { run(); }\n")]   // out-of-tree head macro (review)
    [InlineData("int f(void) {\n  helper(a, b) ;\n}\n")]                // inside a body
    [InlineData("helper(a, b);\nint x;\nstruct s { int y; };\n")]       // a prototype, not a definition
    [InlineData("x = helper(a);\n")]
    [InlineData("/* helper(a) { */\n// helper(b) {\n")]                 // comments
    [InlineData("#define helper(a) { a }\n")]                           // a directive
    [InlineData("__attribute__((used))\nhelper(int x) { }\n")]          // previous token is ')'
    public void ImplicitInt_LeavesOtherShapesAlone(string src)
    {
        Assert.Same(src, ImplicitInt.Rewrite(src, _ => false, out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void KAndR_InAMixedCAndCppTree_IsRecovered()
    {
        // languages c + cpp reads .c files with the C++ grammar, which rejects K&R parameter lists.
        using var w = new Work();
        w.W("main.cpp", "extern \"C\" int helper(int, const char *);\nint main(){ return helper(1, \"x\"); }\n");
        w.W("knr.c", "static int unused;\nint\nhelper(a, b)\n  int a;\n  char *b;\n{\n  return a + leaf();\n}\n");
        w.W("leaf.c", "int leaf(void) { return 0; }\n");
        w.W("dead.c", "int dead(void) { return 0; }\n");
        var (code, o) = CarveMixed(w);
        Assert.True(code == 0, o);
        Assert.True(File.Exists(w.Out("carved/knr.c")), o);
        Assert.True(File.Exists(w.Out("carved/leaf.c")), o);
        Assert.False(File.Exists(w.Out("carved/dead.c")), o);
    }

    static (int, string) CarveMixed(Work w)
    {
        var cfg = Path.Combine(w.Root, "carve.toml");
        File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(w.Root, "out").Replace('\\', '/')}\"\n"
                               + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\", \"cpp\"]\n");
        var so = new StringWriter(); var se = new StringWriter();
        var code = CarveCommand.Run(new[] { "carve", w.Src, "--config", cfg }, so, se);
        return (code, so + "\n" + se);
    }

    [Theory]
    [InlineData("int f(a, b) int a; char *b; { }\n", "int f(int a, char *b) { }")]
    [InlineData("f(a, b, c)\n  unsigned long a, *b;\n{ }\n", "f(unsigned long a, unsigned long *b, int c) { }")]
    [InlineData("int g(n,\n  m) char m[4]; { }\n", "int g(int n, char m[4] ) { }")]
    public void KAndRToPrototype_RewritesTheHead_AndKeepsLines(string src, string expected)
    {
        var got = ImplicitInt.KAndRToPrototype(src, out var n);
        Assert.Equal(1, n);
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.Replace(got, @"\s+", " ").Trim());
        Assert.Equal(src.Count(ch => ch == '\n'), got.Count(ch => ch == '\n'));
    }

    [Theory]
    [InlineData("int f(void) { g(a, b); int x; { } }\n")]   // inside a body
    [InlineData("f(a, b);\nint x;\nstruct s { int y; };\n")] // a call / prototype, not a definition
    [InlineData("int f(a) int z; { }\n")]                     // declares a non-parameter
    public void KAndRToPrototype_LeavesOtherShapesAlone(string src)
    {
        Assert.Same(src, ImplicitInt.KAndRToPrototype(src, out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void ImplicitInt_SkipsAFunctionLikeMacroName_AndKeepsLines()
    {
        var src = "x;\nhelper(int x) { }\nstatic other(a) int a; { }\nmacro_fn(int x) { }\n";
        var got = ImplicitInt.Rewrite(src, n => n == "macro_fn", out var count);
        Assert.Equal(2, count);
        Assert.Equal("x;\nint helper(int x) { }\nstatic int other(a) int a; { }\nmacro_fn(int x) { }\n", got);
    }
}

/// <summary>Pattern 3 of the 1.0.159 work eval: a macro use that DEFINES a symbol named after an argument.</summary>
public sealed class DefinerMacroTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-def-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work() => Directory.CreateDirectory(Src);
        public void W(string rel, string text) => File.WriteAllText(Path.Combine(Src, rel), text);
        public (int Code, string Out) Carve(string extra = "")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so + "\n" + se);
        }
        public bool Kept(string rel, string stage = "") => File.Exists(Path.Combine(Root, "out", stage, "carved", rel));
        public string Read(string rel, string stage = "") => File.ReadAllText(Path.Combine(Root, "out", stage, "carved", rel));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    const string Fw =
        "struct fw_desc { void (*init)(void); void (*fini)(void); };\n"
        + "#define FW_DECLARE(n, i, u) const struct fw_desc n##_desc = { i, u }\n";

    [Fact]
    public void PastedName_DefinedByAMacroUse_KeepsTheDeclaringFile_AndItsArguments()
    {
        using var w = new Work();
        w.W("fw.h", Fw);
        w.W("main.c", "#include \"fw.h\"\nextern const struct fw_desc uart_desc;\nint main(void){ uart_desc.init(); return 0; }\n");
        w.W("uart.c", "#include \"fw.h\"\nvoid uart_init(void);\nvoid uart_fini(void);\nFW_DECLARE(uart, uart_init, uart_fini);\n");
        w.W("uart_impl.c", "void uart_init(void) { }\nvoid uart_fini(void) { }\n");
        w.W("spi.c", "#include \"fw.h\"\nvoid spi_init(void) { }\nFW_DECLARE(spi, spi_init, spi_init);\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(w.Kept("uart.c"), o);
        Assert.True(w.Kept("uart_impl.c"), o);   // the use's arguments are referenced by the defined symbol
        Assert.False(w.Kept("spi.c"), o);        // nothing names spi_desc
    }

    [Fact]
    public void WrapperMacro_PassingItsParameterOn_IsADefinerToo()
    {
        using var w = new Work();
        w.W("fw.h", Fw + "#define FW_DRIVER(n) FW_DECLARE(drv_##n, n##_init, n##_init)\n");
        w.W("main.c", "#include \"fw.h\"\nextern const struct fw_desc drv_led_desc;\nint main(void){ drv_led_desc.init(); return 0; }\n");
        w.W("led.c", "#include \"fw.h\"\nvoid led_init(void) { }\nFW_DRIVER(led);\n");
        var (code, o) = w.Carve();
        Assert.True(code == 0, o);
        Assert.True(w.Kept("led.c"), o);
    }

    [Fact]
    public void FunctionHeadMacro_DefinesTheFunction_AndOwnsItsBody()
    {
        using var w = new Work();
        w.W("task.h", "#define DEFINE_TASK(name) void name(void)\n");
        w.W("main.c", "#include \"task.h\"\nvoid blink(void);\nint main(void){ blink(); return 0; }\n");
        w.W("tasks.c", "#include \"task.h\"\nDEFINE_TASK(blink)\n{\n  led_on();\n}\nDEFINE_TASK(idle)\n{\n  sleep_now();\n}\n");
        w.W("led.c", "void led_on(void) { }\n");
        w.W("sleep.c", "void sleep_now(void) { }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        Assert.True(w.Kept("led.c", "aggressive"), o);
        Assert.False(w.Kept("sleep.c", "aggressive"), o);   // idle's body calls belong to idle, which is unused
        var tasks = w.Read("tasks.c", "aggressive");
        Assert.Contains("DEFINE_TASK(blink)", tasks);
        Assert.DoesNotContain("idle", tasks);
        Assert.DoesNotContain("sleep_now", tasks);
    }

    [Fact]
    public void Build_FindsDeclaratorPositionsOnly()
    {
        var bodies = new Dictionary<string, (bool, List<string>)>
        {
            ["FW_DECLARE"] = (true, new() { "n, i, u) const struct fw_desc n##_desc = { i, u }" }),
            ["DEFINE_TASK"] = (true, new() { "name) void name(void)" }),
            ["DECL"] = (true, new() { "type, name) type name;" }),
            ["MAX"] = (true, new() { "a, b) ((a) > (b) ? (a) : (b))" }),
            ["CALL"] = (true, new() { "f, x) f(x)" }),
            ["CONTAINER_OF"] = (true, new() { "ptr, type, member) ((type *)((char *)(ptr) - offsetof(type, member)))" }),
            ["STR"] = (true, new() { "x) #x" }),
            ["TAG"] = (true, new() { "n) struct n" }),
            ["SET"] = (true, new() { "n) g = n" }),
            ["OBJ"] = (false, new() { " int obj" }),
        };
        var d = DefinerMacros.Build(bodies);
        Assert.Equal(new[] { "DECL", "DEFINE_TASK", "FW_DECLARE" }, d.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new DefinerMacros.Template(0, "", "_desc", false), Assert.Single(d["FW_DECLARE"]));
        Assert.Equal(new DefinerMacros.Template(0, "", "", true), Assert.Single(d["DEFINE_TASK"]));
        Assert.Equal(new DefinerMacros.Template(1, "", "", false), Assert.Single(d["DECL"]));
    }

    [Fact]
    public void Uses_InsideAFunctionOrClass_AreNotDefinitions()
    {
        var d = new Dictionary<string, List<DefinerMacros.Template>> { ["DECL"] = new() { new(1, "", "", false) } };
        var re = new System.Text.RegularExpressions.Regex(@"\b(DECL)\s*\(");
        var src = "DECL(int, a);\nvoid f(void) { DECL(int, b); }\nnamespace n { DECL(int, c); }\nextern \"C\" { DECL(int, e); }\n"
                  + "struct s { DECL(int, x); };\n/* DECL(int, y); */\n#define Z DECL(int, z)\n";
        var names = DefinerMacros.Uses(src, re, d).SelectMany(u => u.Defines).Select(x => x.Name).ToList();
        Assert.Equal(new[] { "a", "c", "e" }, names);
    }
}

/// <summary>verify names the CAUSE of each failure as a count in summary.txt, so a remote eval can report which
/// part of the tool missed without sending a name or path (work eval, 1.0.159: 3 failures of an unknown class).</summary>
public sealed class VerifyCauseTests
{
    [Fact]
    public void UnrecognizedDefinition_IsCountedByCause_InTheSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-cause-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            // A header over the symbol budget is kept whole unparsed (no definitions in the graph). Nothing includes it,
            // so it is dropped, while main calls a function it defines.
            File.WriteAllText(Path.Combine(src, "main.c"), "int helper(void);\nint main(void){ return helper(); }\n");
            File.WriteAllText(Path.Combine(src, "lib.h"), "int helper(void) { return 1; }\nint other(void) { return 2; }\nint third(void) { return 3; }\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n[advanced]\nmaxSymbolsPerFile = 2\n");
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            Assert.True(code == 3, so + "\n" + se);
            var summary = File.ReadAllText(Path.Combine(root, "out", "codecarver", "summary.txt"));
            Assert.Contains("verify.failed.definitionNotRecognized = 1", summary);
            // ... and what the definition looks like, as fixed shape names.
            Assert.Contains("verify.failed.definitionNotRecognized.fileNotParsed = 1", summary);
            Assert.DoesNotContain("helper", summary);   // still numbers only
            var verify = File.ReadAllText(Path.Combine(root, "out", "codecarver", "verify.txt"));
            Assert.Contains("cause definitionNotRecognized", verify);
            Assert.Contains("lib.h:1", verify);
            Assert.Contains("shape fileNotParsed", verify);
        }
        finally { TempDir.Delete(root); }
    }

    [Theory]
    [InlineData("int f(int a) reentrant { return a + helper(); }\n", true)]   // Keil, C++ grammar: a parse error
    [InlineData("F(int x) { return x + helper(); }\n", false)]                  // all-caps implicit int, C grammar
    public void ParseErrorDefinitions_AreRecoveredByTheScanBackstop(string source, bool mixed)
    {
        // Work eval, 1.0.167: three ordinary definitions inside parse errors. The link check's token scanner finds
        // them without a parse; the carve now runs it on files whose parse has errors and defines what was missed.
        var root = Path.Combine(Path.GetTempPath(), "cc-scan-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            var name = source.StartsWith("F(") ? "F" : "f";
            File.WriteAllText(Path.Combine(src, "main.c"), $"int {name}(int);\nint main(void) {{ return {name}(1); }}\n");
            File.WriteAllText(Path.Combine(src, "f.c"), source);
            File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void) { return 0; }\n");
            File.WriteAllText(Path.Combine(src, "dead.c"), "int dead(void) { return 0; }\n");
            if (mixed) File.WriteAllText(Path.Combine(src, "x.cpp"), "int unused_cpp() { return 0; }\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = " + (mixed ? "[\"c\", \"cpp\"]" : "[\"c\"]") + "\n");
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            Assert.True(code == 0, so + "\n" + se);
            Assert.True(File.Exists(Path.Combine(root, "out", "carved", "f.c")));
            Assert.True(File.Exists(Path.Combine(root, "out", "carved", "helper.c")));   // the body's calls count
            Assert.False(File.Exists(Path.Combine(root, "out", "carved", "dead.c")));
            Assert.Contains("parse.definitionsRecoveredByScan = 1", File.ReadAllText(Path.Combine(root, "out", "codecarver", "summary.txt")));
        }
        finally { TempDir.Delete(root); }
    }

    [Fact]
    public void RegistrationMacroUse_IsNotReadAsADefinition()
    {
        // Work eval, 1.0.162: `REGISTER_DRIVER(a, a_init, a_fini)` with no ';', then the next function's '{', read as
        // a definition of REGISTER_DRIVER in the dropped module, "used" by the same macro's call in the kept one.
        var root = Path.Combine(Path.GetTempPath(), "cc-regm-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "reg.h"), "struct drv { const char *n; int (*i)(void); void (*u)(void); };\n"
                + "#define REGISTER_DRIVER(name, init, uninit) const struct drv name##_drv = { #name, init, uninit };\n");
            File.WriteAllText(Path.Combine(src, "main.c"), "#include \"reg.h\"\nint b_init(void) { return 0; }\nvoid b_fini(void) { }\n"
                + "REGISTER_DRIVER(b, b_init, b_fini)\nint b_work(void);\nint main(void) { return b_work(); }\nint b_work(void) { return 1; }\n");
            File.WriteAllText(Path.Combine(src, "mod_a.c"), "#include \"reg.h\"\nREGISTER_DRIVER(a, a_init, a_fini)\n"
                + "int a_init(void) { return 0; }\nvoid a_fini(void) { }\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n");
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            Assert.True(code == 0, so + "\n" + se);
            Assert.False(File.Exists(Path.Combine(root, "out", "carved", "mod_a.c")));
        }
        finally { TempDir.Delete(root); }
    }

    [Fact]
    public void LinkCheck_StillReportsATypedFunctionNamedLikeAMacro()
    {
        // A real definition with a return type that shares a function-like macro's name is still checked when that
        // macro belongs to another target: the calling file doesn't include it, so the call is a real call. (Where the
        // macro IS visible at the call, the preprocessor expands it and no link reference exists: NameBindingTests.)
        var root = Path.Combine(Path.GetTempPath(), "cc-regt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kept = Path.Combine(root, "kept.c");
            var other = Path.Combine(root, "other.h");
            var gone = Path.Combine(root, "gone.c");
            File.WriteAllText(kept, "int helper(int);\nint main(void) { return helper(1); }\n");
            File.WriteAllText(other, "#define helper(x) other_helper(x)\n");
            File.WriteAllText(gone, "int helper(int x) { return x; }\n");
            var r = CodeCarver.Core.Reachability.EmittedLinkCheck.Run(new[] { ("kept.c", kept), ("other.h", other) }, new[] { ("gone.c", gone) });
            Assert.Contains(r.Violations, v => v.Name == "helper");
        }
        finally { TempDir.Delete(root); }
    }
}

/// <summary>A file kept whole without extraction (symbol budget, parse timeout, fragment, extraction failure) is
/// written whole, so what it uses must stay. It used to contribute no uses at all.</summary>
public sealed class KeptWholeUsesTests
{
    [Fact]
    public void AFunctionOnlyAKeptWholeFileCalls_IsKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-keptwhole-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){ return a(); }\n");   // one symbol: under the budget
            // Over a 1-symbol budget: kept whole (force-rooted), never extracted.
            File.WriteAllText(Path.Combine(src, "big.c"), "int a(void){ return 1; }\nint b(void){ return leaf(); }\n");
            File.WriteAllText(Path.Combine(src, "leaf.c"), "int leaf(void){ return 0; }\n");
            File.WriteAllText(Path.Combine(src, "dead.c"), "int dead(void){ return 0; }\n");
            var cfg = Path.Combine(root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(root, "out").Replace("\\", "/")}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n[advanced]\nmaxSymbolsPerFile = 1\n");
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            Assert.True(code == 0, so + "\n" + se);
            Assert.True(File.Exists(Path.Combine(root, "out", "carved", "leaf.c")), so + "\n" + se);
            Assert.True(File.Exists(Path.Combine(root, "out", "carved", "big.c")), so + "\n" + se);
        }
        finally { TempDir.Delete(root); }
    }
}

/// <summary>Names built by a macro that pastes its own parameters (see PasteMacros).</summary>
public sealed class GluePasteTests
{
    sealed class Work : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cc-glue-" + Guid.NewGuid().ToString("N"));
        public string Src => Path.Combine(Root, "src");
        public Work() => Directory.CreateDirectory(Src);
        public void W(string rel, string text) => File.WriteAllText(Path.Combine(Src, rel), text);
        public (int Code, string Out) Carve(string extra = "")
        {
            var cfg = Path.Combine(Root, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{Path.Combine(Root, "out").Replace('\\', '/')}\"\n"
                                   + "[common]\nentryPoints = [\"main\"]\nlanguages = [\"c\"]\n" + extra);
            var so = new StringWriter(); var se = new StringWriter();
            var code = CarveCommand.Run(new[] { "carve", Src, "--config", cfg }, so, se);
            return (code, so + "\n" + se);
        }
        public bool Kept(string rel, string stage = "") => File.Exists(Path.Combine(Root, "out", stage, "carved", rel));
        public string Read(string rel, string stage = "") => File.ReadAllText(Path.Combine(Root, "out", stage, "carved", rel));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    // A macro that pastes its own parameters builds names no body scan sees: `CAT(a, b) a##b` reached through a
    // wrapper that supplies the literal piece, or called with it in code. The carve inside the file removed the
    // definition and the build said "undeclared" (work eval, 1.0.189).
    const string Cat = "#define CAT(a, b) a##b\n#define CAT3(a, b, c) a##b##c\n";

    [Fact]
    public void GluePaste_ThroughAWrapper_KeepsWhatItBuilds()
    {
        using var w = new Work();
        w.W("m.h", Cat + "#define DESC(n) CAT(n, _desc)\n#define CFG(n) CAT3(dev_, n, _cfg)\n");
        w.W("main.c", "#include \"m.h\"\nint main(void){ return DESC(foo) + CFG(foo); }\n"
                      + "static const int foo_desc = 1;\nstatic const int dev_foo_cfg = 2;\n"
                      + "static int unused_fn(void) { return 3; }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        var text = w.Read("main.c", "aggressive");
        Assert.Contains("foo_desc = 1", text);
        Assert.Contains("dev_foo_cfg = 2", text);
        Assert.DoesNotContain("unused_fn", text);
    }

    [Fact]
    public void GluePaste_CalledInCode_KeepsTheFunctionItNames()
    {
        using var w = new Work();
        w.W("m.h", Cat);
        w.W("main.c", "#include \"m.h\"\ntypedef int (*fp)(void);\nstatic int foo_isr(void) { return 1; }\n"
                      + "static int bar_isr(void) { return 2; }\nint main(void){ fp f = CAT(foo, _isr); return f(); }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        var text = w.Read("main.c", "aggressive");
        Assert.Contains("foo_isr(void)", text);
        Assert.DoesNotContain("bar_isr", text);   // the use names foo_isr exactly
    }

    [Fact]
    public void PasteMacros_FollowParametersThroughWrappers()
    {
        var bodies = new Dictionary<string, (bool, List<string>)>
        {
            ["CAT"] = (true, new() { "a, b) a##b" }),
            ["MID"] = (true, new() { "x) CAT(x, _mid)" }),
            ["OUT"] = (true, new() { "y) MID(pre_##y)" }),
            ["FIXED"] = (true, new() { ") CAT(fixed, _name)" }),
            ["STR"] = (true, new() { "a, b) #a ## b" }),
            ["ONE"] = (true, new() { "n) n##_one" }),
        };
        var objectMacros = new HashSet<string> { "DEV" };
        var t = PasteMacros.Build(bodies, objectMacros.Contains);
        IEnumerable<(PasteMacros.Kind, string)> Use(string m, string args) => PasteMacros.UseFragments(t, m, args, objectMacros.Contains);
        Assert.Equal(new[] { (PasteMacros.Kind.Exact, "uart_rx") }, Use("CAT", "uart, _rx"));
        Assert.Equal(new[] { (PasteMacros.Kind.Exact, "uart_mid") }, Use("MID", "uart"));
        Assert.Equal(new[] { (PasteMacros.Kind.Exact, "pre_uart_mid") }, Use("OUT", "uart"));
        Assert.Equal(new[] { "fixed_name" }, PasteMacros.BodyNames(t, "FIXED"));
        // Not a plain name: only the literal pieces around it, for this use.
        Assert.Equal(new[] { (PasteMacros.Kind.Prefix, "uart") }, Use("CAT", "uart, (x)"));
        // An object-like macro may expand before it is pasted.
        Assert.Equal(new[] { (PasteMacros.Kind.Suffix, "_mid") }, Use("MID", "DEV"));
        Assert.False(t.Templates.ContainsKey("STR"));
        Assert.False(t.Templates.ContainsKey("ONE"));   // one parameter: the body's own literal already says it
    }

    [Fact]
    public void GluePaste_LinksOnlyWhatEachUseBuilds()
    {
        using var w = new Work();
        w.W("m.h", Cat + "#define DESC(n) CAT(n, _desc)\n");
        w.W("main.c", "#include \"m.h\"\nstatic int foo_desc(void) { return 1; }\nstatic int bar_desc(void) { return 2; }\n"
                      + "int main(void){ return DESC(foo)(); }\n");
        var (code, o) = w.Carve("[stages.aggressive]\ncarveSourceFileContents = true\n");
        Assert.True(code == 0, o);
        var text = w.Read("main.c", "aggressive");
        Assert.Contains("foo_desc(void)", text);
        Assert.DoesNotContain("bar_desc", text);   // ends in _desc, but no use builds it
    }
}
