using CodeCarver.Core.Graph;

namespace CodeCarver.Core.Reachability;

/// <summary>A kept function that calls an in-scope function which the carve dropped — it would not link.</summary>
public sealed record CallViolation(string Caller, string Callee, string? File);

/// <summary>
/// A compiler-free soundness gate, from the field: <b>if a function is KEPT, every in-scope function it
/// calls must also be KEPT — else the link fails.</b> This re-checks the carve against the raw call sites
/// (including calls that never resolved to an edge), so it catches an UNDER-reach: a real call our edge
/// model missed (a blind spot — macro/token-paste/inline-asm) that left a needed callee carved out.
///
/// It is deliberately independent of the reachability closure (which by construction keeps every Calls
/// edge's target): the value is in the calls that AREN'T edges. Two field-noted traps are handled for
/// free — names compare case-sensitively (a macro <c>FOO32</c> ≠ a function <c>foo32</c>), and commented
/// code contributes no call sites (tree-sitter ignores comments). "In-scope" = a function defined
/// somewhere in the analyzed input; calls to external/library symbols are ignored.
/// </summary>
public static class SoundnessCheck
{
    public static IReadOnlyList<CallViolation> KeptCallingDropped(
        CodeGraph graph, CarvePlan plan, IReadOnlyList<(NodeId From, string Name)> callSites)
    {
        var byId = new Dictionary<NodeId, Node>();
        var definedFn = new HashSet<string>(StringComparer.Ordinal);
        var keptFn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            byId[n.Id] = n;
            if (n.Kind != NodeKind.Function) continue;
            definedFn.Add(n.Name);
            if (plan.IsKept(n.Id)) keptFn.Add(n.Name);
        }

        var seen = new HashSet<(string, string)>();
        var violations = new List<CallViolation>();
        foreach (var (from, name) in callSites)
        {
            if (!byId.TryGetValue(from, out var caller)) continue;
            if (caller.Kind != NodeKind.Function || !plan.IsKept(from)) continue; // only kept-function callers
            if (!definedFn.Contains(name) || keptFn.Contains(name)) continue;     // external, or a kept def exists
            if (seen.Add((caller.Name, name)))
                violations.Add(new CallViolation(caller.Name, name, caller.FilePath));
        }
        return violations;
    }
}
