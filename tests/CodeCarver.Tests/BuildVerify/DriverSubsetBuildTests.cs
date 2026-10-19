using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeCarver.Cli;
using Xunit;
using Xunit.Abstractions;

namespace CodeCarver.Tests.BuildVerify;

/// <summary>
/// A small image over a big driver tree: 40 drivers, and an app that links 7. Carved from the app's entry alone, the
/// carve must keep those 7 drivers and no other, and the carved tree must build, with the tree's own makefile, into
/// an image with the same functions as the original. The 7 come in each way real drivers do: called directly, through
/// an ops struct, through a table the app fills, and by registering themselves in a linker section the image's linker
/// script gathers (only the ones the link line names; the rest of that family sit unlinked in an archive). The build
/// is the kind a carve meets: a makefile that compiles every driver it finds, a code-generation step, an archive.
/// Built with make and gcc from <c>.toolchains/w64devkit</c> and arm-none-eabi-gcc; skipped without them. All names
/// are invented.
/// </summary>
public class DriverSubsetBuildTests
{
    readonly ITestOutputHelper _out;
    public DriverSubsetBuildTests(ITestOutputHelper output) => _out = output;

    const int Drivers = 40;
    static readonly int[] Direct = { 3, 11 }, ViaOps = { 7 }, ViaTable = { 15, 22 }, Registered = { 30, 36 };
    static int[] Used => Direct.Concat(ViaOps).Concat(ViaTable).Concat(Registered).Order().ToArray();
    /// <summary>Drivers 30..39 register themselves; only <see cref="Registered"/> are linked.</summary>
    static bool SelfRegisters(int d) => d >= 30;
    static string D(int d) => $"drv_{d:00}";

    static void WriteTree(string root)
    {
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text.Replace("\r\n", "\n"));
        }

        W("include/drv_api.h", """
            #ifndef DRV_API_H
            #define DRV_API_H
            struct drv_ops { int (*init)(void); int (*read)(int reg); };
            /* A driver that registers itself: the linker script gathers .drv_init into one table. */
            #define DRV_REGISTER(ops) \
                static const struct drv_ops *const drv_reg_##ops __attribute__((used, section(".drv_init"))) = &ops
            #endif
            """);
        for (var d = 0; d < Drivers; d++)
        {
            var n = D(d);
            W($"drivers/{n}/{n}.h", $"#ifndef {n.ToUpperInvariant()}_H\n#define {n.ToUpperInvariant()}_H\n#include \"drv_api.h\"\n"
                + $"int {n}_init(void);\nint {n}_read(int reg);\nint {n}_selftest(void);\nextern const struct drv_ops {n}_ops;\n#endif\n");
            var sb = new StringBuilder();
            sb.Append($"#include \"{n}.h\"\n");
            // Some unused drivers lean on a used one, as drivers share helpers.
            var leansOn = d is 5 or 18 ? 3 : -1;
            if (leansOn >= 0) sb.Append($"#include \"../{D(leansOn)}/{D(leansOn)}.h\"\n");
            sb.Append($"static volatile int {n}_regs[4];\n");
            sb.Append($"static int {n}_wait(int r) {{ return {n}_regs[r & 3] + {d}; }}\n");
            sb.Append($"int {n}_init(void) {{ {n}_regs[0] = {d}; return {(leansOn >= 0 ? $"{D(leansOn)}_read(1)" : "0")}; }}\n");
            sb.Append($"int {n}_read(int reg) {{ return {n}_wait(reg); }}\n");
            sb.Append($"int {n}_selftest(void) {{ return {n}_read(2) == {d} ? 0 : -1; }}\n");
            sb.Append($"const struct drv_ops {n}_ops = {{ {n}_init, {n}_read }};\n");
            if (SelfRegisters(d)) sb.Append($"DRV_REGISTER({n}_ops);\n");
            W($"drivers/{n}/{n}.c", sb.ToString());
        }

        W("app/startup.c", """
            void Reset_Handler(void);
            void Default_Handler(void) { for (;;) { } }
            void NMI_Handler(void) __attribute__((weak, alias("Default_Handler")));
            void SysTick_Handler(void) __attribute__((weak, alias("Default_Handler")));
            int main(void);
            __attribute__((section(".isr_vector"), used))
            void (*const vectors[])(void) = { (void (*)(void))0x20001000u, Reset_Handler, NMI_Handler, SysTick_Handler };
            void Reset_Handler(void) { main(); for (;;) { } }
            """);
        var direct = string.Concat(Direct.Select(d => $"    sum += {D(d)}_init() + {D(d)}_read(1);\n"));
        var ops = string.Concat(ViaOps.Select(d => $"    sum += {D(d)}_ops.init();\n"));
        W("app/main.c", "#include \"drv_api.h\"\n#include \"drv_ids.h\"\n"
            + string.Concat(Direct.Concat(ViaOps).Concat(ViaTable).Select(d => $"#include \"../drivers/{D(d)}/{D(d)}.h\"\n"))
            + "extern const struct drv_ops *const __drv_init_start[], *const __drv_init_end[];\n"
            + "static const struct drv_ops *const app_drivers[] = { " + string.Join(", ", ViaTable.Select(d => $"&{D(d)}_ops")) + " };\n"
            + "int main(void)\n{\n    int sum = DRV_ID_COUNT;\n" + direct + ops
            + "    for (unsigned i = 0; i < sizeof app_drivers / sizeof app_drivers[0]; i++) sum += app_drivers[i]->init();\n"
            + "    for (const struct drv_ops *const *p = __drv_init_start; p < __drv_init_end; p++) sum += (*p)->init();\n"
            + "    return sum;\n}\n");

        W("link/tiny.ld", """
            ENTRY(Reset_Handler)
            MEMORY { FLASH (rx) : ORIGIN = 0x08000000, LENGTH = 256K  RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 32K }
            SECTIONS
            {
                .text : {
                    KEEP(*(.isr_vector))
                    *(.text*)
                    *(.rodata*)
                    . = ALIGN(4);
                    __drv_init_start = .;
                    KEEP(*(.drv_init))
                    __drv_init_end = .;
                } > FLASH
                .data : { *(.data*) } > RAM AT > FLASH
                .bss : { *(.bss*) *(COMMON) } > RAM
            }
            """);
        // Code generation: the driver ids header comes from a list, in the build directory.
        W("config/drivers.txt", string.Concat(Used.Select(d => D(d) + "\n")));
        W("tools/gen_ids.sh", """
            #!/bin/sh
            # gen_ids.sh <list> <out.h>: one id per driver in the list
            n=0
            { echo "/* generated */"; while read d; do echo "#define DRV_ID_$d $n"; n=$((n+1)); done < "$1"; echo "#define DRV_ID_COUNT $n"; } > "$2"
            """);
        W("docs/README.txt", "Tiny image over the driver tree. make BUILD=<dir> CC=<arm gcc>\n");
        // The makefile compiles every driver it finds into an archive; the app links its own objects, the registered
        // drivers it names, and the archive (which gives only what is referenced).
        W("Makefile", """
            BUILD ?= build
            CC ?= arm-none-eabi-gcc
            AR ?= arm-none-eabi-ar
            CFLAGS = -mcpu=cortex-m3 -mthumb -ffreestanding -O1 -ffunction-sections -fdata-sections -Iinclude -I$(BUILD)/gen
            DRV_SRCS := $(wildcard drivers/*/*.c)
            DRV_OBJS := $(patsubst drivers/%.c,$(BUILD)/drivers/%.o,$(DRV_SRCS))
            REG_OBJS := $(BUILD)/drivers/drv_30/drv_30.o $(BUILD)/drivers/drv_36/drv_36.o
            APP_OBJS := $(BUILD)/app/startup.o $(BUILD)/app/main.o

            $(BUILD)/tiny.axf: $(APP_OBJS) $(REG_OBJS) $(BUILD)/libdrv.a link/tiny.ld
            	cd $(CURDIR) && $(CC) -mcpu=cortex-m3 -mthumb -nostdlib -Wl,--gc-sections -Wl,-T,link/tiny.ld -o $@ $(APP_OBJS) $(REG_OBJS) -L$(BUILD) -ldrv

            $(BUILD)/libdrv.a: $(DRV_OBJS)
            	$(AR) rcs $@ $^

            $(BUILD)/gen/drv_ids.h: config/drivers.txt tools/gen_ids.sh
            	mkdir -p $(dir $@)
            	sh tools/gen_ids.sh config/drivers.txt $@

            $(BUILD)/app/main.o: $(BUILD)/gen/drv_ids.h

            $(BUILD)/%.o: %.c
            	mkdir -p $(dir $@)
            	cd $(CURDIR) && $(CC) $(CFLAGS) -c $< -o $@
            """);
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(p) || File.Exists(p)) return p;
        }
        return null;
    }

    static (int Code, string Output) Run(string exe, IEnumerable<string> args, string cwd, string path)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>The functions in an image (text symbols), by name.</summary>
    static SortedSet<string> Functions(string nm, string axf, string path)
    {
        var (code, text) = Run(nm, new[] { axf }, Path.GetDirectoryName(axf)!, path);
        Assert.True(code == 0, text);
        return new SortedSet<string>(text.Split('\n').Select(l => l.Trim().Split(' ')).Where(f => f.Length == 3 && f[1] is "T" or "t").Select(f => f[2]), StringComparer.Ordinal);
    }

    [SkippableFact]
    public void TinyImage_OverManyDrivers_KeepsOnlyTheDriversItLinks_AndBuildsTheSameImage_Builds()
    {
        var w64 = FindUp(Path.Combine(".toolchains", "w64devkit", "bin"));
        var tools = FindUp(".toolchains");
        var armGcc = tools is null ? null : Directory.EnumerateFiles(tools, "arm-none-eabi-gcc.exe", SearchOption.AllDirectories).FirstOrDefault();
        Skip.If(w64 is null || armGcc is null, "make (.toolchains/w64devkit) or arm-none-eabi-gcc not fetched");
        var armBin = Path.GetDirectoryName(armGcc)!;
        var path = w64 + Path.PathSeparator + armBin;
        string F(string p) => p.Replace('\\', '/');
        var make = Path.Combine(w64!, "make.exe");
        var nm = Path.Combine(armBin, "arm-none-eabi-nm.exe");
        string[] MakeArgs(string build) => new[] { $"BUILD={F(build)}", $"CC={F(armGcc!)}", $"AR={F(Path.Combine(armBin, "arm-none-eabi-ar.exe"))}" };

        var work = Path.Combine(Path.GetTempPath(), "cc-drv-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "tiny");
        try
        {
            WriteTree(src);
            // The original build, outside the tree (a build directory inside it would be copied as it is).
            var (code, log) = Run(make, MakeArgs(Path.Combine(work, "build-orig")), src, path);
            Assert.True(code == 0, "the original tree does not build:\n" + log);
            File.WriteAllText(Path.Combine(work, "build.log"), log);
            var orig = Functions(nm, Path.Combine(work, "build-orig", "tiny.axf"), path);
            // The fixture is what it says: the image has the used drivers' code and no other driver's.
            foreach (var d in Enumerable.Range(0, Drivers))
                Assert.True(orig.Contains($"{D(d)}_init") == Used.Contains(d), $"fixture: {D(d)}_init in the original image = {orig.Contains($"{D(d)}_init")}");

            var outDir = Path.Combine(work, "out");
            var cfg = Path.Combine(work, "carve.toml");
            File.WriteAllText(cfg, $"outputDirectory = \"{F(outDir)}\"\n[common]\nentryPoints = [\"Reset_Handler\"]\nlanguages = [\"c\"]\n"
                + $"[builds.tiny]\nbuildLogs = [\"{F(Path.Combine(work, "build.log"))}\"]\n[advanced]\nmemoryStopMb = 4000\n");
            var so = new StringWriter(); var se = new StringWriter();
            code = CarveCommand.Run(new[] { "carve", src, "--config", cfg }, so, se);
            _out.WriteLine(se.ToString());
            Assert.True(code == 0, "carve failed:\n" + so + se);

            using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "codecarver", "manifest.json")));
            var kept = m.RootElement.GetProperty("keptFiles").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
            var keptDrivers = Enumerable.Range(0, Drivers).Where(d => kept.Contains($"drivers/{D(d)}/{D(d)}.c")).ToArray();
            _out.WriteLine($"drivers kept: {string.Join(" ", keptDrivers.Select(D))}");

            var carved = Path.Combine(outDir, "carved");
            (code, log) = Run(make, MakeArgs(Path.Combine(work, "build-carved")), carved, path);
            Assert.True(code == 0, "the carved tree does not build:\n" + log);
            var after = Functions(nm, Path.Combine(work, "build-carved", "tiny.axf"), path);
            Assert.True(orig.SetEquals(after), $"image functions differ. only in the original: [{string.Join(", ", orig.Except(after))}]; "
                + $"only in the carve: [{string.Join(", ", after.Except(orig))}]");

            Assert.True(Used.SequenceEqual(keptDrivers), $"drivers kept: [{string.Join(", ", keptDrivers.Select(D))}], "
                + $"the image links: [{string.Join(", ", Used.Select(D))}]");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
