using System.Linq;
using System.Text.RegularExpressions;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>
/// Runtime-trace parsing (next week's third input: functions a real run executed). The upstream format is
/// not fixed, so the parser is a configurable regex with a named 'fn' group (+ optional file/line). These
/// pin the default shapes we expect and prove a custom format is a one-line override, not a code change.
/// </summary>
public class TraceFileTests
{
    [Fact]
    public void Default_ParsesBareNames_AndNameFileLine()
    {
        const string trace = """
            # a sample run
            init_system
            read_sensor src/sensor.c:42
            process   drivers/proc.c:117
            shutdown
            """;
        var recs = TraceFile.Parse(trace);
        Assert.Equal(new[] { "init_system", "read_sensor", "process", "shutdown" },
                     recs.Select(r => r.Function).ToArray());
        var rs = recs.First(r => r.Function == "read_sensor");
        Assert.Equal("src/sensor.c", rs.File);
        Assert.Equal(42, rs.Line);
        Assert.Null(recs.First(r => r.Function == "init_system").File); // bare name -> no file/line
    }

    [Fact]
    public void SkipsBlanksAndComments_AndDedupes()
    {
        const string trace = "foo\n\n# comment\nfoo\nbar\n";
        var recs = TraceFile.Parse(trace);
        Assert.Equal(new[] { "foo", "bar" }, recs.Select(r => r.Function).ToArray()); // 'foo' once
    }

    [Fact]
    public void CustomFormat_ViaRegex_ExtractsFnFileLine()
    {
        // A made-up "real" format: "<ts> [TRACE] file.c:line fn(...)". A regex with named groups adapts it
        // without touching code -- exactly the --trace-format escape hatch for when the friend's format lands.
        const string trace = "1000 [TRACE] core/main.c:10 boot_main(void)\n1001 [TRACE] hal/tim.c:88 tim_isr(void)\n";
        var pat = new Regex(@"\[TRACE\]\s+(?<file>\S+):(?<line>\d+)\s+(?<fn>[A-Za-z_]\w*)");
        var recs = TraceFile.Parse(trace, pat);
        Assert.Equal(new[] { "boot_main", "tim_isr" }, recs.Select(r => r.Function).ToArray());
        Assert.Equal("hal/tim.c", recs[1].File);
        Assert.Equal(88, recs[1].Line);
    }

    [Fact]
    public void FunctionNames_AreDistinct()
    {
        var names = TraceFile.FunctionNames(TraceFile.Parse("a\nb a.c:1\na b.c:2\n"));
        Assert.Equal(new[] { "a", "b" }, names.OrderBy(n => n).ToArray()); // 'a' collapsed across file:line variants
    }
}
