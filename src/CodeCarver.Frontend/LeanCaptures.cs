using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TreeSitter;

namespace CodeCarver.Frontend;

/// <summary>
/// A query's captures read straight from the tree-sitter library: where each starts and ends, and nothing else. The
/// binding's <see cref="QueryCursor.Captures"/> builds, for every capture, a match object, a list of the match's
/// captures, a node object, and a predicate closure: on a macro-heavy tree the identifier pass alone made most of the
/// parse's garbage. Offsets and positions are the binding's own (UTF-16 input: a byte offset / 2 is a char index).
/// </summary>
internal static class LeanCaptures
{
    public readonly record struct Span(int Row, int Column, int Start, int End);

    static readonly object Yes = true, No = false;
    static readonly ConditionalWeakTable<Query, object> Plain = new();

    /// <summary>The query has no text predicates (#eq?, #match?...), which <see cref="Of"/> doesn't evaluate.</summary>
    public static bool Usable(Query query) => Plain.GetValue(query, q =>
    {
        var self = QuerySelf(q);
        for (uint p = 0, n = ts_query_pattern_count(self); p < n; p++)
        {
            ts_query_predicates_for_pattern(self, p, out var steps);
            if (steps > 0) return No;
        }
        return Yes;
    }) == Yes;

    /// <summary>Every capture of <paramref name="query"/> over the whole of <paramref name="tree"/>, in the order
    /// <see cref="QueryCursor.Captures"/> gives them. For queries without text predicates (#eq?, #match?): the binding
    /// drops captures those reject, this doesn't evaluate them.</summary>
    public static IEnumerable<Span> Of(Query query, Tree tree)
    {
        var cursor = ts_query_cursor_new();
        try
        {
            ts_query_cursor_exec(cursor, QuerySelf(query), ts_tree_root_node(TreeSelf(tree)));
            while (Next(cursor, out var span)) yield return span;
        }
        finally { ts_query_cursor_delete(cursor); }
    }

    static unsafe bool Next(IntPtr cursor, out Span span)
    {
        span = default;
        if (!ts_query_cursor_next_capture(cursor, out var match, out var index)) return false;
        var node = ((TSQueryCapture*)match.captures)[index].node;
        var at = ts_node_start_point(node);
        span = new Span(ToInt(at.row), Index(at.column), Index(ts_node_start_byte(node)), Index(ts_node_end_byte(node)));
        return true;
    }

    static int ToInt(uint v) => v >= int.MaxValue ? int.MaxValue : (int)v;
    static int Index(uint bytes) => bytes >= int.MaxValue ? int.MaxValue : (int)(bytes / 2);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_self")] static extern ref IntPtr QuerySelf(Query query);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_self")] static extern ref IntPtr TreeSelf(Tree tree);

    [StructLayout(LayoutKind.Sequential)] struct TSNode { uint c0, c1, c2, c3; IntPtr id, tree; }
    [StructLayout(LayoutKind.Sequential)] struct TSPoint { public uint row, column; }
    [StructLayout(LayoutKind.Sequential)] struct TSQueryCapture { public TSNode node; public uint index; }
    [StructLayout(LayoutKind.Sequential)] struct TSQueryMatch { public uint id; public ushort pattern_index, capture_count; public IntPtr captures; }

    const string Lib = "tree-sitter";
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr ts_query_cursor_new();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void ts_query_cursor_delete(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void ts_query_cursor_exec(IntPtr self, IntPtr query, TSNode node);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool ts_query_cursor_next_capture(IntPtr self, out TSQueryMatch match, out uint captureIndex);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern TSNode ts_tree_root_node(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern uint ts_query_pattern_count(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr ts_query_predicates_for_pattern(IntPtr self, uint patternIndex, out uint stepCount);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern uint ts_node_start_byte(TSNode self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern uint ts_node_end_byte(TSNode self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern TSPoint ts_node_start_point(TSNode self);
}
