namespace Tiny.Services
{
    // Add() is reached from Main; Subtract() is not. But C# carving is FILE-LEVEL (no Roslyn), so the
    // whole file is kept once ANY method in it is reached — sound: it never drops a method that a call
    // we couldn't resolve might need. Intra-file method pruning would require real semantic analysis.
    public sealed class Calculator
    {
        public int Add(int a, int b) => a + b;
        public int Subtract(int a, int b) => a - b;
    }
}
