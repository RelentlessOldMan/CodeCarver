using CodeCarver.Core.Diagnostics;
using CodeCarver.Core.Graph;
using CodeCarver.Core.Reachability;
using CodeCarver.Core.Roots;
using Xunit;

namespace CodeCarver.Tests.Diagnostics;

/// <summary>
/// The repro bundle is only shareable if anonymization is TOTAL: a single leaked real name or path would
/// defeat the whole point (a repro you can send without exposing proprietary IP). These pin that no real
/// identifier survives, that the structure needed to replay a carve does, and that output is deterministic.
/// </summary>
public sealed class ReproBundleTests
{
    private static (CodeGraph Graph, CarvePlan Plan) SampleCarve()
    {
        var b = new GraphBuilder();
        var main = b.Func("main", "main.c", line: 1);
        var helper = b.Func("secret_helper", "secret_dir/secret_impl.c", line: 5);
        var t = b.Type("secret_config_t", "secret_dir/secret.h", line: 2);
        _ = b.Func("unused_dead_fn", "dead.c", line: 9); // dropped by the carve
        b.Calls(main, helper);
        b.Refs(helper, t);

        var roots = new List<Root> { new(main, RootKind.EntryPoint, "IMAGE_ENTRY_secret_note") };
        var plan = ReachabilityEngine.Compute(b.Graph, roots, ReachabilityOptions.Safe);
        return (b.Graph, plan);
    }

    [Fact]
    public void Build_LeaksNoRealNamesOrPaths()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);

        // No symbol names, no path segments, and no root NOTE text may appear anywhere.
        foreach (var secret in new[]
                 {
                     "secret_helper", "secret_config_t", "unused_dead_fn", "main.c",
                     "secret_dir", "secret_impl", "secret.h", "dead.c", "IMAGE_ENTRY_secret_note",
                 })
            Assert.DoesNotContain(secret, json);

        // The tokenized name "main" must not survive as a bare identifier either.
        Assert.DoesNotContain("\"main\"", json);
    }

    [Fact]
    public void Build_KeepsStructureNeededToReplay()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);

        Assert.Contains("reproFormatVersion", json);
        Assert.Contains("\"nodes\"", json);
        Assert.Contains("\"edges\"", json);
        Assert.Contains("\"roots\"", json);
        Assert.Contains("\"reached\"", json);
        Assert.Contains("EntryPoint", json);   // root KIND is structural, not sensitive
        Assert.Contains("Calls", json);         // edge KIND is structural
        Assert.Contains(".c", json);            // file tokens keep the extension (carve behavior is ext-sensitive)
        Assert.Contains(".h", json);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var (graph, plan) = SampleCarve();
        Assert.Equal(ReproBundle.Build(graph, plan), ReproBundle.Build(graph, plan));
    }

    [Fact]
    public void Build_NodeAndEdgeCountsMatchGraph()
    {
        var (graph, plan) = SampleCarve();
        var json = ReproBundle.Build(graph, plan);
        // 4 defined functions/types + the file nodes their defs create; assert the reported node AND edge counts
        // equal the graph's (the snapshot is complete, not a sample), and that the arrays hold that many entries.
        var graphEdges = graph.Nodes.Sum(n => graph.OutEdges(n.Id).Count);
        Assert.True(graphEdges >= 2, "the sample graph must have edges for this to mean anything");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var counts = root.GetProperty("counts");
        Assert.Equal(graph.NodeCount, counts.GetProperty("nodes").GetInt32());
        Assert.Equal(graphEdges, counts.GetProperty("edges").GetInt32());
        Assert.Equal(graph.NodeCount, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(graphEdges, root.GetProperty("edges").GetArrayLength());
        // Each serialized edge is one of the graph's edges (by endpoint ids and kind).
        var expected = graph.Nodes.SelectMany(n => graph.OutEdges(n.Id))
            .Select(e => $"{e.From.Value}->{e.To.Value}:{e.Kind}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        var actual = root.GetProperty("edges").EnumerateArray()
            .Select(e => $"{e.GetProperty("from").GetInt32()}->{e.GetProperty("to").GetInt32()}:{e.GetProperty("kind").GetString()}")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Write_StreamsToOutput_EquivalentToBuild()
    {
        // The streaming writer (used by the CLI straight-to-file) must produce the same snapshot as Build,
        // so lifting the old node cap doesn't change the artifact — just how it's written.
        var (graph, plan) = SampleCarve();
        using var ms = new MemoryStream();
        ReproBundle.Write(ms, graph, plan, indented: true);
        var streamed = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Equal(ReproBundle.Build(graph, plan), streamed);
    }

    [Fact]
    public void Write_LargeChainGraph_CompletesWithCorrectCounts()
    {
        // Review TS7: this used to be named "..._WithoutMaterializing", which it cannot observe. What it does
        // check: a 60k-node chain is written completely, with the right node and edge counts. Whether the writer
        // streams is pinned separately by Write_StreamsIncrementally_DoesNotBufferWholeDocument.
        var b = new GraphBuilder();
        const int n = 60_000;
        var prev = b.Func("f0", "f0.c", line: 1);
        for (var i = 1; i < n; i++)
        {
            var cur = b.Func($"f{i}", $"f{i}.c", line: 1);
            b.Calls(prev, cur);
            prev = cur;
        }
        var plan = ReachabilityEngine.Compute(b.Graph,
            new List<Root> { new(b.Graph.Nodes.First().Id, RootKind.EntryPoint) }, ReachabilityOptions.Safe);

        using var ms = new MemoryStream();
        ReproBundle.Write(ms, b.Graph, plan, indented: false);  // compact: this is a machine artifact
        ms.Position = 0;
        using var doc = System.Text.Json.JsonDocument.Parse(ms);
        var counts = doc.RootElement.GetProperty("counts");
        Assert.Equal(b.Graph.NodeCount, counts.GetProperty("nodes").GetInt32());
        Assert.Equal(n - 1, counts.GetProperty("edges").GetInt32());        // a chain of n nodes has n-1 edges
    }

    [Fact]
    public void Write_StreamsIncrementally_DoesNotBufferWholeDocument()
    {
        // What "without materializing" means observably: the bytes reach the output stream in many bounded
        // writes while the document is produced, not as one write of the whole document at the final Flush.
        var b = new GraphBuilder();
        const int n = 20_000;
        var prev = b.Func("f0", "f0.c", line: 1);
        for (var i = 1; i < n; i++)
        {
            var cur = b.Func($"f{i}", $"f{i}.c", line: 1);
            b.Calls(prev, cur);
            prev = cur;
        }
        var plan = ReachabilityEngine.Compute(b.Graph,
            new List<Root> { new(b.Graph.Nodes.First().Id, RootKind.EntryPoint) }, ReachabilityOptions.Safe);

        using var sink = new WriteRecordingStream();
        ReproBundle.Write(sink, b.Graph, plan, indented: false);
        Assert.True(sink.Length > 1_000_000, $"expected a multi-MB document, got {sink.Length} B");
        Assert.True(sink.LargestWrite < sink.Length / 4,
            $"the whole {sink.Length:N0} B document reached the stream in writes of up to {sink.LargestWrite:N0} B " +
            $"({sink.Writes} write(s)) — it was buffered in memory, not streamed");
    }

    /// <summary>A write-only sink that records how the bytes arrive.</summary>
    private sealed class WriteRecordingStream : Stream
    {
        public int Writes { get; private set; }
        public long LargestWrite { get; private set; }
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            _length += buffer.Length;
        }
    }
}
