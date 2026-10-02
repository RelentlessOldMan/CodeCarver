using System;

namespace Tiny.Services
{
    // Reached from Program.Main via `new Greeter().Greet(...)` -> the whole file is kept.
    public sealed class Greeter
    {
        public void Greet(string name) => Console.WriteLine($"Hello, {name}!");
    }
}
