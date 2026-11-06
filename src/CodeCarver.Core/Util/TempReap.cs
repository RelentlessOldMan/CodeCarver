namespace CodeCarver.Core.Util;

/// <summary>
/// Deletes what an earlier carve left in the temp folder when it was stopped or killed before its own cleanup ran:
/// the check's spill folder, pp-trace's databases and stub headers, the compiler's response files. A spill can be
/// gigabytes. Only entries a day old go, so a carve running beside this one keeps its own. Never throws.
/// </summary>
public static class TempReap
{
    static readonly string[] Prefixes = { "codecarver-spill-", "codecarver-ppt-", "codecarver-stub-", "codecarver-pp-" };

    public static int Old(TimeSpan? age = null)
    {
        var cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromDays(1));
        var gone = 0;
        try
        {
            foreach (var e in new DirectoryInfo(Path.GetTempPath()).EnumerateFileSystemInfos("codecarver-*"))
            {
                if (!Prefixes.Any(p => e.Name.StartsWith(p, StringComparison.Ordinal)) || e.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    if (e is DirectoryInfo d) d.Delete(recursive: true); else e.Delete();
                    gone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use or not ours: leave it */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return gone;
    }
}
