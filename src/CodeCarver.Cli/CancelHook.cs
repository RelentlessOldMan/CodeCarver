using CodeCarver.Core.Emit;

namespace CodeCarver.Cli;

/// <summary>
/// Ctrl-C cleanup for the staged output (review RB11). One handler for the process, registered on first use,
/// discards the stage being written so an interrupted run leaves no staging directory behind. It shares a
/// lock with <see cref="Promote"/>, so an interrupt can never tear down a stage while it is being promoted;
/// before, every stage added another handler and each could dispose its stage mid-promote.
/// </summary>
static class CancelHook
{
    static readonly object Gate = new();
    static StagedOutput? _active;
    static int _registered;

    public static void Track(StagedOutput staged)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            Console.CancelKeyPress += (_, _) =>
            {
                lock (Gate) { try { _active?.Dispose(); } catch { /* best effort on the way out */ } }
            };
        lock (Gate) _active = staged;
    }

    public static void Promote(StagedOutput staged)
    {
        lock (Gate)
        {
            try { staged.Promote(); }
            finally { if (ReferenceEquals(_active, staged)) _active = null; }
        }
    }
}
