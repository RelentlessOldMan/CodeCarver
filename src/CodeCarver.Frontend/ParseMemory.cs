using CodeCarver.Core.Util;

namespace CodeCarver.Frontend;

/// <summary>
/// Holds the parses running at once to a share of the machine's memory. tree-sitter's tree of a file with parse errors
/// takes ~150 bytes per character of source (an 8 MB file: 1.25 GB; our own data for it under 100 MB), so one parse per
/// core over a run of big files asked for more memory than the machine had. A file waits until the parses before it
/// leave room; one bigger than the whole budget runs alone. Only when parses run, never what they find, depends on it.
/// </summary>
internal sealed class ParseMemory : MemoryGate
{
    /// <summary>Memory a parse is counted as holding per character of its text.</summary>
    public const long BytesPerChar = 160;

    /// <summary>Files under this many characters are not counted: their trees are small.</summary>
    public const int MinChars = 64 * 1024;

    public ParseMemory(long budgetBytes) : base(budgetBytes, BytesPerChar, MinChars) { }

    /// <summary>A quarter of the memory the machine has, or CODECARVER_PARSE_MEMORY_MB. Not more: what a tree frees stays
    /// with the process (the native heap keeps it for the next parse), so the most the parses ever held is held for
    /// the rest of the run, and the graph and the check need the rest.</summary>
    public static ParseMemory ForMachine()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CODECARVER_PARSE_MEMORY_MB"), out var mb) && mb > 0)
            return new ParseMemory(mb * 1024 * 1024);
        var total = MachineBytes();
        return new ParseMemory(total > 0 ? total / 4 : 4L << 30);
    }
}
