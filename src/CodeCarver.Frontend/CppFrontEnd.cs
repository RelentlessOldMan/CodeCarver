namespace CodeCarver.Frontend;

/// <summary>
/// C++ front-end. On top of the C-family machinery it recognises methods (in-class and out-of-line
/// definitions), classes, and namespaces, and resolves method/qualified calls.
///
/// Virtual dispatch is handled by the base's name-based resolution: a call to <c>area()</c> resolves
/// to every <c>area</c> method, so all overrides are conservatively kept without building the class
/// hierarchy — sound (over-approximate) by construction.
///
/// Limits today: templates/lambdas/operators are parsed loosely (calls inside a lambda attribute to
/// the enclosing method); no overload resolution (names are enough for a sound file/function carve).
/// </summary>
public sealed class CppFrontEnd() : TreeSitterFrontEnd("tree-sitter-cpp.dll", "tree_sitter_cpp", DefsQuery, CallsQuery)
{
    private const string DefsQuery = """
        (function_declarator declarator: (identifier) @function)
        (function_declarator declarator: (parenthesized_declarator (identifier) @function))
        (function_declarator declarator: (field_identifier) @function)
        (function_declarator declarator: (qualified_identifier name: (identifier) @function))
        (class_specifier name: (type_identifier) @class)
        (struct_specifier name: (type_identifier) @struct)
        (enum_specifier name: (type_identifier) @enum)
        (namespace_definition name: (namespace_identifier) @namespace)
        (preproc_def name: (identifier) @macro)
        (preproc_function_def name: (identifier) @macro)
        """;

    private const string CallsQuery = """
        (call_expression function: (identifier) @callee)
        (call_expression function: (field_expression field: (field_identifier) @callee))
        (call_expression function: (qualified_identifier name: (identifier) @callee))
        """;
}
