using CodeCarver.Core.Graph;
using CodeCarver.Core.Preprocess;

namespace CodeCarver.Frontend;

/// <summary>
/// A language front-end: turns source files into a <see cref="CodeGraph"/> the reachability engine
/// carves. C and C++ share <see cref="TreeSitterFrontEnd"/> (calls, macros, globals, #include closure,
/// #ifdef resolution). Other languages that don't fit the C-family model (C#, TRACE32 .cmm) implement
/// this directly. <paramref name="defines"/>/<paramref name="closedWorldDefines"/> are honoured only by
/// front-ends with a preprocessor; others ignore them.
/// </summary>
public interface ICarveFrontEnd : IDisposable
{
    CodeGraph BuildGraph(IEnumerable<(string Path, string Text)> files,
                         MacroTable? defines = null, bool closedWorldDefines = false);
}
