using System;

namespace Tiny.Reporting
{
    // Nothing reachable from Main references this type, so the whole .cs file is carved away.
    public sealed class LegacyReport
    {
        public void Print() => Console.WriteLine("legacy report");
        public string Render() => "...";
    }
}
