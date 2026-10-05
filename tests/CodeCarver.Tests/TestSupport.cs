using System.Diagnostics;

namespace CodeCarver.Tests;

/// <summary>Locates the repository from the test binary. The checked-in fixtures (examples/, tests/) are part
/// of the repo, so a missing root is a test-environment failure, not a reason to skip.</summary>
public static class TestRepo
{
    private static readonly Lazy<string> _root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeCarver.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"CodeCarver.sln not found above {AppContext.BaseDirectory}; the tests must run from a repo checkout");
    });

    /// <summary>The directory that contains CodeCarver.sln.</summary>
    public static string Root => _root.Value;

    /// <summary><c>examples/&lt;name&gt;</c>; fails the test if the example is not in the checkout.</summary>
    public static string Example(string name)
    {
        var p = Path.Combine(Root, "examples", name);
        Assert.True(Directory.Exists(p), $"examples/{name} is missing from the checkout");
        return p;
    }
}

/// <summary>A per-test temp directory, deleted on dispose. Deletion retries (an AV scanner or indexer can hold a
/// just-written file for a moment) and never throws, so a cleanup problem cannot mask the real test failure.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix = "cc-test-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose() => Delete(Path);

    public static void Delete(string dir)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    /// <summary>Copies a directory tree (used to carve an example from a temp copy so nothing writes into the repo).</summary>
    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, d)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, f)));
    }
}

/// <summary>Finds the pinned toolchains (fetched into <c>.toolchains/</c>, not committed) and runs them.</summary>
public static class Toolchain
{
    public static string? Gcc() => FindUp(System.IO.Path.Combine(".toolchains", "w64devkit", "bin", "gcc.exe"));
    public static string? Gxx() => FindUp(System.IO.Path.Combine(".toolchains", "w64devkit", "bin", "g++.exe"));

    public static string? ArmGcc()
    {
        var tools = System.IO.Path.Combine(TestRepo.Root, ".toolchains");
        return Directory.Exists(tools)
            ? Directory.EnumerateFiles(tools, "arm-none-eabi-gcc.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string? FindUp(string relativeFile)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, relativeFile);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    public static (int Code, string Output) Run(string exe, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, so.Result + se);
    }
}
