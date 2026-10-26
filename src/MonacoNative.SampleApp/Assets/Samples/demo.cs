using System;
using System.Collections.Generic;
using System.Linq;

namespace MonacoNative.Demo;

/// <summary>Preformatted C# sample: type Console. inside Run to test Roslyn member completion.</summary>
public static class DemoProgram
{
    public static void Run()
    {
        var numbers = new List<int> { 1, 2, 3, 4, 5 };
        var doubled = numbers.Where(n => n % 2 != 0)
                             .Select(n => n * 2);

        foreach (var number in doubled)
        {
            Console.WriteLine($"Value: {number}");
        }
    }
}
