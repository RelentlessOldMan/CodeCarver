using CodeCarver.Core.Preprocess;
using Xunit;

namespace CodeCarver.Tests.Preprocess;

public class MacroTableTests
{
    // Review CPU C22: after its first full pass AfterInclude checks only the names set or certainly #undef'd since.
    // A clone always makes the full pass, so random directive sequences must leave both tables in the same state.
    [Fact]
    public void AfterInclude_Incremental_MatchesFullPass()
    {
        var names = Enumerable.Range(0, 12).Select(i => "M" + i).ToArray();
        var rng = new Random(22);
        for (var trial = 0; trial < 2000; trial++)
        {
            var ambient = names.Where(_ => rng.Next(2) == 0).ToHashSet(StringComparer.Ordinal);
            var undefs = ambient.Where(_ => rng.Next(2) == 0).ToHashSet(StringComparer.Ordinal);
            var t = MacroTable.FromDefines(names.Where(_ => rng.Next(3) == 0));
            t.Ambient = ambient; t.UndefinedSomewhere = undefs;
            var full = t.Clone();
            for (var step = 0; step < 30; step++)
            {
                var n = names[rng.Next(names.Length)];
                switch (rng.Next(7))
                {
                    case 0: t.Set(n, "1"); full.Set(n, "1"); break;
                    case 1: t.UndefCertain(n); full.UndefCertain(n); break;
                    case 2: t.Undef(n); full.Undef(n); break;
                    case 3: t.ForceUnknown(n); full.ForceUnknown(n); break;
                    case 4: t.MarkUnknown(n); full.MarkUnknown(n); break;
                    default: t.AfterInclude(); full = full.Clone(); full.AfterInclude(); break;
                }
                foreach (var m in names)
                {
                    Assert.Equal(full.IsDefined(m), t.IsDefined(m));
                    Assert.Equal(full.IsUnknown(m), t.IsUnknown(m));
                }
            }
        }
    }
}
