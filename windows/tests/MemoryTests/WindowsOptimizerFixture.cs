namespace Lexi.Tests;

internal static class WindowsOptimizerFixture
{
    public static void Materialize(string path)
    {
        var fixture = Environment.GetEnvironmentVariable("LEXI_TEST_PROCESS_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
            throw new InvalidOperationException("Windows 子进程测试需要已编译的 fixture，请先运行 tests/tools/build-process-fixture.ps1。");
        File.WriteAllText(path + ".fixture", File.ReadAllText(path));
        File.Copy(fixture, path, overwrite: true);
    }
}
