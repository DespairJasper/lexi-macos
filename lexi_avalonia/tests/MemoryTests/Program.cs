namespace Lexi.Tests;

/// <summary>
/// 长期记忆层（轨迹 / canonical / FSRS / Context / 调度 / 持久化）的独立测试套件。
///
/// 设计：本套件按**文件**切分测试组，每个组是一个自带 <c>Run()</c> 的静态类，
/// 由本文件统一调度。这样多个实现工作流可以各自拥有一个文件而不互相冲突。
/// 用法：<c>dotnet run --project tests/MemoryTests -c Release [组名]</c>（不带参数跑全部）。
/// </summary>
public static class Program
{
    public delegate void TestGroup();

    public static int Main(string[] args)
    {
        var groups = new (string Name, TestGroup Run)[]
        {
            // FSRS-6 核心与轨迹归约的断言位于 tests/LearningTests（那里已有 213 条断言），
            // 本套件不重复；这里的组按"长期记忆层的其余部分"划分。
            ("config", ConfigTests.Run),
            ("optimizer", OptimizerTests.Run),
            // 个人 FSRS 参数的**自动集成**（门槛 / 训练 / 事务重放发布 / Context 重新资格）。
            // 与上面的 optimizer 组分工不同：那一组测训练适配器本身，这一组测"接上去之后整库是否自洽"。
            ("optimizer-integration", OptimizerIntegrationTests.Run),
            ("legacy-due", LegacyDueMigrationTests.Run),
            ("optimizer-boundary", OptimizerBoundaryTests.Run),
            ("optimizer-signature", OptimizerBoundaryTests.SnapshotSignatureDoesNotCollide),
            ("recovery", SessionRecoveryTests.Run),
            ("journal", JournalTests.Run),
            ("storage-journal", StorageTests.Journal),
            ("storage-backups", StorageTests.Backups),
            ("storage-legacy", StorageTests.Legacy),
            ("storage-clock", StorageTests.ClockRollback),
            ("storage-directory", StorageTests.LinkedDirectory),
            ("coordinator", CoordinatorTests.Run),
            ("store", StoreTests.Run),
            // Context 特征/校准尚未实现（P6）。保留登记，让它的缺席在输出里以 SKIP 显式暴露，
            // 而不是悄悄"通过"。
            ("context", ContextTests.Run),
        };

        var selected = args.Length == 0
            ? groups
            : groups.Where(g => args.Contains(g.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"未知测试组。可用：{string.Join(", ", groups.Select(g => g.Name))}");
            return 2;
        }

        var failures = new List<string>();
        var skipped = new List<string>();
        foreach (var (name, run) in selected)
        {
            Console.WriteLine($"=== memory group: {name} ===");
            var before = AssertionsRun;
            try
            {
                run();
                // 一条断言都没跑的组不得计为通过——那正是"空日志算 PASS"的假绿地。
                if (AssertionsRun == before) skipped.Add(name);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"FAIL group {name}: {ex}");
            }
        }

        if (failures.Count > 0)
        {
            Console.WriteLine($"=== {failures.Count} 个测试组失败 ===");
            foreach (var failure in failures) Console.WriteLine("  " + failure);
            return 1;
        }
        // 零断言的组一律算失败：SKIP 不是 PASS，退出码不能让"整组断言被删掉"变成绿灯。
        if (skipped.Count > 0)
        {
            Console.WriteLine($"=== {skipped.Count} 个测试组没有任何断言（SKIP 不是 PASS）===");
            foreach (var name in skipped) Console.WriteLine("  " + name);
            return 1;
        }
        Console.WriteLine($"All memory tests passed ({AssertionsRun} assertions).");
        return 0;
    }

    /// <summary>已执行的断言数。用于识别"空组冒充通过"。</summary>
    public static int AssertionsRun { get; private set; }

    /// <summary>断言辅助：失败即抛，由调度器统一收集为组失败。</summary>
    public static void Check(bool condition, string description)
    {
        AssertionsRun++;
        if (condition) Console.WriteLine("PASS: " + description);
        else throw new InvalidOperationException("断言失败: " + description);
    }

    public static void CheckClose(double actual, double expected, double tolerance, string description)
        => Check(Math.Abs(actual - expected) <= tolerance,
            $"{description} (actual={actual:R}, expected={expected:R}, tol={tolerance:R})");
}
