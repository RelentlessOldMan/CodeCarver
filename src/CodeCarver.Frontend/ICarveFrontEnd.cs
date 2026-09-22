using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;

namespace CodeCarver.Frontend;

/// <summary>
/// A language front-end: turns source files into a <see cref="CodeGraph"/> the reachability engine
/// carves. C and C++ share <see cref="TreeSitterFrontEnd"/> (calls, macros, globals, #include closure,
/// #ifdef resolution). Other languages that don't fit the C-family model (C#, TRACE32 .cmm) implement
/// this directly. <paramref name="defines"/>/<paramref name="closedWorldDefines"/> are honoured only by
/// front-ends with a preprocessor; others ignore them.
///
/// A file passed with empty <c>Text</c> is a "too big to parse" input (e.g. a multi-GB auto-generated
/// register header): the caller deliberately does not read it, so it is registered as a
/// <see cref="NodeKind.File"/> node only — never a string or a parse tree — and is still kept whole via
/// #include-closure and copied verbatim by the emitter, but never explodes memory. See
/// <c>--max-parse-bytes</c>.
/// </summary>
public interface ICarveFrontEnd : IDisposable
{
    CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files,
                         MacroTable? defines = null, bool closedWorldDefines = false);

    /// <summary>
    /// Non-fatal diagnostics from the most recent <see cref="BuildGraph"/> — a file kept whole because it
    /// looked like an #include fragment or blew a parse budget, a <c>.cmm</c> <c>DO</c> that resolved to
    /// nothing or to an ambiguous basename. Surfacing these avoids the "silent 100% smaller" trap where a
    /// carve looks great only because it resolved nothing. Empty when all was clean.
    /// </summary>
    IReadOnlyList<string> Warnings { get; }
}
