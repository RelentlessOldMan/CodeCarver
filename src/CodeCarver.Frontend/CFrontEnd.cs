namespace CodeCarver.Frontend;

/// <summary>
/// C front-end. Extracts functions, types, and macros, plus direct calls, conservative address-taken
/// (function-pointer) edges, macro expansion, and #include structure. See <see cref="TreeSitterFrontEnd"/>
/// for the shared machinery and the soundness notes.
///
/// Limits today: no preprocessing (parses source as-is — a precision matter, not correctness);
/// #include resolution by basename. File-scope address-taken (vector/dispatch tables) IS handled —
/// attributed to the file node (see <see cref="TreeSitterFrontEnd"/>).
/// </summary>
public sealed class CFrontEnd() : TreeSitterFrontEnd("tree-sitter-c.dll", "tree_sitter_c", DefsQuery, CallsQuery)
{
    private const string DefsQuery = """
        (function_declarator declarator: (identifier) @function)
        (function_declarator declarator: (parenthesized_declarator (identifier) @function))
        (struct_specifier name: (type_identifier) @struct)
        (union_specifier name: (type_identifier) @struct)
        (enum_specifier name: (type_identifier) @enum)
        (type_definition declarator: (type_identifier) @type)
        (preproc_def name: (identifier) @macro)
        (preproc_function_def name: (identifier) @macro)
        """;

    private const string CallsQuery = "(call_expression function: (identifier) @callee)";
}
