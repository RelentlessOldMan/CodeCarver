using Xunit;

namespace CodeCarver.Tests;

/// <summary>
/// The tests that measure the process's memory or time (live heap, allocations, growth from N to 4N, a time bound) run
/// alone, after the parallel ones: run beside other tests, what those allocate and the cores they take count against
/// their bounds (a 5 s template-walk bound took 5.3 s in CI beside the rest).
/// </summary>
[CollectionDefinition(nameof(CodeCarver.Tests.Preprocess.TemplateWalkMemoryTests), DisableParallelization = true)]
public class MemoryTestsCollection { }
