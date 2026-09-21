using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using CodeCarver.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

/// <summary>Proves the C# front-end does a sound file-level carve: a class reached through a call is
/// kept; an unreferenced class's file is dropped.</summary>
public class CSharpFrontEndTests
{
    [Fact]
    public void FileLevel_KeepsCalledClass_DropsUnusedFile()
    {
        const string app = "class Program { static void Main() { Helper.Used(); } }";
        const string helper = "class Helper { public static void Used() {} public static void Unused() {} }";
        const string other = "class Other { public static void Orphan() {} }";

        using var fe = new CSharpFrontEnd();
        var graph = fe.BuildGraph(new[] { ("App.cs", app), ("Helper.cs", helper), ("Other.cs", other) });

        var main = graph.Nodes.First(n => n.Name == "Main").Id;
        var plan = ReachabilityEngine.Compute(graph, new[] { new Root(main, RootKind.EntryPoint) });

        Assert.Contains("App.cs", plan.KeptFiles);
        Assert.Contains("Helper.cs", plan.KeptFiles);    // Used() is reached
        Assert.Contains("Other.cs", plan.DroppedFiles);  // Orphan() is never called
    }

    [Fact]
    public void ExtractsMethodsAndTypes()
    {
        const string src = "namespace N { class C { void A() { B(); } void B() {} } }";
        using var fe = new CSharpFrontEnd();
        var graph = fe.BuildGraph(new[] { ("C.cs", src) });

        Assert.Contains(graph.Nodes, n => n.Kind == CodeCarver.Core.Graph.NodeKind.Function && n.Name == "A");
        Assert.Contains(graph.Nodes, n => n.Kind == CodeCarver.Core.Graph.NodeKind.Function && n.Name == "B");
        Assert.Contains(graph.Nodes, n => n.Kind == CodeCarver.Core.Graph.NodeKind.Type && n.Name == "C");
    }
}
