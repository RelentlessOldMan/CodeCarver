using System.Text.Json;
using CodeCarver.Cli;
using Xunit;

namespace CodeCarver.Tests.Cli;

/// <summary>
/// The --config consolidation: one annotated JSON holds every feedable input so the CLI stays lean. These pin
/// the template's two contracts — it must be valid (parse as the real config, comments and all) and COMPLETE
/// (mention every CarveConfig field, so a new input can't be added without surfacing it in the template) — plus
/// the fail-fast / ignore-missing / Phase-2-guard behavior around it.
/// </summary>
public sealed class ConfigTemplateTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var code = CarveCommand.Run(args, so, se);
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public void Template_ParsesAsConfig_CommentsAndTrailingCommasAllowed()
    {
        var text = CarveCommand.EmitConfigTemplate();
        // Same options the real --config loader uses: skip comments, tolerate trailing commas, case-insensitive.
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var ex = Record.Exception(() => JsonSerializer.Deserialize(text,
            typeof(CarveCommand).Assembly.GetType("CodeCarver.Cli.CarveConfig")!, opts));
        Assert.Null(ex);   // the emitted template must always be a loadable config
    }

    [Fact]
    public void Template_MentionsEveryConfigField_NoSilentlyUndocumentedInput()
    {
        var text = CarveCommand.EmitConfigTemplate();
        var cfgType = typeof(CarveCommand).Assembly.GetType("CodeCarver.Cli.CarveConfig")!;
        foreach (var p in cfgType.GetProperties())
            Assert.True(text.Contains(p.Name, StringComparison.OrdinalIgnoreCase),
                $"config field '{p.Name}' is not shown in the emit-config template");
    }

    [Fact]
    public void EmitConfig_WritesFile_ThatLoadsAndCarves()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cfgtpl-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int helper(void);\nint main(void){return helper();}\n");
        File.WriteAllText(Path.Combine(src, "helper.c"), "int helper(void){return 1;}\n");
        var cfg = Path.Combine(work, "carve.json");
        try
        {
            var so = new StringWriter(); var se = new StringWriter();
            Assert.Equal(0, CarveCommand.EmitConfig(new[] { "emit-config", cfg }, so, se));
            Assert.True(File.Exists(cfg));

            // Fill in roots and carve using the generated template verbatim otherwise.
            var filled = File.ReadAllText(cfg).Replace("\"roots\": [],", "\"roots\": [\"main\"],");
            File.WriteAllText(cfg, filled);
            var (code, o, _) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("nodes", o);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Config_BuildLogsArray_MultipleEntriesUnioned()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cfgbl-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
        var logA = Path.Combine(work, "a.log");
        var logB = Path.Combine(work, "b.log");
        File.WriteAllText(logA, "gcc -DFEATURE=1 -c main.c\n");
        File.WriteAllText(logB, "gcc -DOTHER=2 -c main.c\n");
        var cfg = Path.Combine(work, "carve.json");
        try
        {
            File.WriteAllText(cfg, "{ \"roots\": [\"main\"], \"buildLogs\": [\"" +
                logA.Replace("\\", "/") + "\", \"" + logB.Replace("\\", "/") + "\"] }");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);
            Assert.Contains("2 build-log(s)", err);   // both array entries consumed
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Config_MissingInput_FailsFast_Exit2()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cfgmiss-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
        var cfg = Path.Combine(work, "carve.json");
        try
        {
            File.WriteAllText(cfg, "{ \"roots\": [\"main\"], \"buildLogs\": [\"does-not-exist.log\"] }");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(2, code);
            Assert.Contains("missing", err);
            Assert.Contains("does-not-exist.log", err);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Config_MissingInput_IgnoreFlag_WarnsAndContinues()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cfgign-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
        var cfg = Path.Combine(work, "carve.json");
        try
        {
            File.WriteAllText(cfg, "{ \"roots\": [\"main\"], \"buildLogs\": [\"does-not-exist.log\"], \"ignoreMissingInputs\": true }");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(0, code);                                  // continues despite the missing file
            Assert.Contains("input file not found, skipping", err); // but says so
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Config_Phase2FileTrace_Set_ErrorsLoudly_NotSilentlyIgnored()
    {
        var work = Path.Combine(Path.GetTempPath(), "cc-cfgp2-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.c"), "int main(void){return 0;}\n");
        var cfg = Path.Combine(work, "carve.json");
        try
        {
            File.WriteAllText(cfg, "{ \"roots\": [\"main\"], \"runFileTraces\": [\"run.csv\"] }");
            var (code, _, err) = Run("carve", src, "--config", cfg);
            Assert.Equal(2, code);
            Assert.Contains("not supported in this build", err);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
