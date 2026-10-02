using System;
using Tiny.Services;

namespace Tiny
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var greeter = new Greeter();
            greeter.Greet(args.Length > 0 ? args[0] : "world");

            var calc = new Calculator();
            Console.WriteLine($"2 + 3 = {calc.Add(2, 3)}");
            return 0;
        }
    }
}
