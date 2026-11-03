using System;
using CS462.StalkerAI.Tests;

internal static class Program
{
    static int Main()
    {
        try
        {
            CoreVerification.RunAll();
            Console.WriteLine("PASS: six verification groups, including 1,000 seeded Dijkstra comparisons.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
