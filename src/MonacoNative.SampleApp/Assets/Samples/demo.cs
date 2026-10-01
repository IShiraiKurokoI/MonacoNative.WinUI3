using System;
using System.Collections.Generic;
using System.Linq;

namespace MonacoNative.Demo;

/// <summary>Demonstrates C# tokens, code folding and Find / Replace.</summary>
public static class Program
{
    public static void Main()
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
