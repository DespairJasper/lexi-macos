using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

// Actual Windows process for the Unix mock scenarios used by MemoryTests.
// No commands from the fixture text are executed.
class OptimizerProcessFixture
{
    static int Main()
    {
        var body = File.ReadAllText(Process.GetCurrentProcess().MainModule.FileName + ".fixture");
        if (body.Contains("read -r line") || body.Contains("echo '")) Console.ReadLine();
        if (body.Contains("sleep 10")) Thread.Sleep(10000);
        if (body.Contains("head -c 131072")) Console.Write(new string('A', 131072));
        var echo = Regex.Match(body, "echo '([^']*)'(.*)");
        if (echo.Success)
        {
            if (echo.Groups[2].Value.Contains(">&2")) Console.Error.WriteLine(echo.Groups[1].Value);
            else Console.WriteLine(echo.Groups[1].Value);
        }
        var exit = Regex.Match(body, @"exit (\d+)");
        return exit.Success ? int.Parse(exit.Groups[1].Value) : 0;
    }
}
