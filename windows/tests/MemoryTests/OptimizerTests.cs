using System.Diagnostics;
using System.Text.Json;

namespace Lexi.Tests;

internal static class OptimizerTests
{
    internal static void Run()
    {
        Console.WriteLine("--- 1. 契约基础与不可变性断言 ---");
        TestContractAndDefaultsImmutability();

        Console.WriteLine("--- 2. Helper 缺失与调用方回退断言 ---");
        TestMissingHelperThrowsAndPreservesFallback();

        Console.WriteLine("--- 3. 历史数据清洗、去重与防泄漏截断断言 ---");
        TestInputFilteringAndCutoffProtection();

        Console.WriteLine("--- 4. 时序切分与时间戳 tie 保护断言 ---");
        TestTimestampTieSplitNoLeakage();
        TestCancellationDuringCpuScan();
        TestTrainingItemsRespectGlobalRecency();

        Console.WriteLine("--- 5. JSON 契约、Seed 2023 与严格响应元数据核验断言 ---");
        TestJsonContractAndMetadataVerification();

        Console.WriteLine("--- 6. 留出验证样本与类别平衡门槛断言 ---");
        TestClassBalanceAndSampleThresholds();

        Console.WriteLine("--- 7. 双基线评估（Defaults 与 LastGood）与改善防劣化断言 ---");
        TestDualBaselineEvaluationAndGating();

        Console.WriteLine("--- 8. 权重 Clip 严禁任意 Clamp：超界拒绝与极小舍入容差断言 ---");
        TestWeightsDimensionAndClippingRefusal();

        Console.WriteLine("--- 9. 受控子进程生命周期、超时终止、真实取消与预算安全断言 ---");
        TestSubprocessControlTimeoutAndCancellation();

        Console.WriteLine("--- 10. 序列长度严格限制、<=64完整前缀保留、反例证明第65条不重置D/S断言 ---");
        TestBudgetsAndSequenceLengthPrefixExclusion();

        Console.WriteLine("--- 11. 真实 Native Helper 探测、零梯度拒绝与端到端成功优化实测 ---");
        TestNativeProbeExecutionWhenAvailable();
    }

    private static void TestContractAndDefaultsImmutability()
    {
        IFSRSParameterOptimizer optimizer = new FsrsParameterOptimizer();
        Program.Check(optimizer.IsImplemented, "个人 FSRS 参数训练能力明确标记为已实现 (IsImplemented == true)");

        var defaultsBefore = Fsrs6Weights.Defaults.ToArray();
        var history = new List<CanonicalReview>
        {
            new() { CanonicalId = "cr:test:1", WordKey = "archive:test", SessionId = "s1", Revision = 3, ReviewedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow }
        };

        var caught = false;
        try
        {
            optimizer.OptimizeAsync(history).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is FsrsOptimizationException or FileNotFoundException)
        {
            caught = true;
        }
        Program.Check(caught, "样本不足或 helper 缺失抛明确异常，不能伪装成功");

        Program.Check(history.Count == 1 && history[0].Revision == 3, "优化器不修改输入历史集合");
        Program.Check(defaultsBefore.SequenceEqual(Fsrs6Weights.Defaults.Weights), "优化器不修改官方默认权重");
        Program.Check(Fsrs6Weights.Defaults.Source == Fsrs6Weights.SourceDefaults && Fsrs6Weights.Defaults.OptimizedAtUtc is null,
            "defaults 保持官方标识，不伪装成训练参数");
    }

    private static void TestMissingHelperThrowsAndPreservesFallback()
    {
        var nonexistentPath = Path.Combine(AppContext.BaseDirectory, "nonexistent_fsrs_helper_" + Guid.NewGuid().ToString("N"));
        var optimizer = new FsrsParameterOptimizer(nonexistentPath);

        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);

        var caughtFileNotFound = false;
        try
        {
            optimizer.OptimizeAsync(history).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException ex)
        {
            caughtFileNotFound = true;
            Program.Check(ex.Message.Contains(nonexistentPath), "FileNotFoundException 包含明确缺失路径");
        }
        Program.Check(caughtFileNotFound, "helper 文件缺失必须抛出明确 FileNotFoundException，保留调用方 fallback");
    }

    private static void TestInputFilteringAndCutoffProtection()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        // 标记部分为 Invalidated
        history[1].Invalidated = true;
        history[2].Invalidated = true;

        // 构造跨 cutoff 定稿泄漏反例：reviewed 在 split 之前，但 completed 落在未来
        var leakKey = "archive:leak-test";
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var splitPoint = new DateTime(2026, 1, 25, 0, 0, 0, DateTimeKind.Utc);

        // 首条正常
        history.Add(new CanonicalReview
        {
            CanonicalId = $"cr:{leakKey}:0", WordKey = leakKey, SessionId = "s0",
            Origin = CanonicalOrigin.FirstLearnAggregate, Rating = StudyRating.Known,
            ReviewedAtUtc = baseDate, CompletedAtUtc = baseDate, Revision = 1
        });
        // 第二条：reviewed 早于 splitPoint，但 completedAt 落在未来（跨 cutoff 定稿）
        history.Add(new CanonicalReview
        {
            CanonicalId = $"cr:{leakKey}:1", WordKey = leakKey, SessionId = "s1",
            Origin = CanonicalOrigin.FirstRetrieval, Rating = StudyRating.Forgot,
            ReviewedAtUtc = baseDate.AddDays(10), CompletedAtUtc = baseDate.AddDays(45), Revision = 1
        });

        // 构造重复 revision 检查去重
        history.Add(new CanonicalReview
        {
            CanonicalId = $"cr:{leakKey}:0-dup", WordKey = leakKey, SessionId = "s0",
            Origin = CanonicalOrigin.FirstLearnAggregate, Rating = StudyRating.Known,
            ReviewedAtUtc = baseDate, CompletedAtUtc = baseDate, Revision = 2
        });

        // 构造 DateTime.MinValue 保守拒绝
        history.Add(new CanonicalReview
        {
            CanonicalId = "cr:bad:min", WordKey = "archive:bad", SessionId = "sbad",
            ReviewedAtUtc = DateTime.MinValue, CompletedAtUtc = DateTime.MinValue
        });

        var options = new FsrsOptimizerOptions
        {
            CompletedAtCutoff = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc),
            MinimumTrainReviews = 5,
            MinimumValidationReviews = 5,
        };

        FsrsOptimizerRequest? capturedRequest = null;
        var optimizer = new FsrsParameterOptimizer(options)
        {
            TestInvoker = (req, token) =>
            {
                capturedRequest = req;
                return Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()));
            }
        };

        try { optimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException) { }

        Program.Check(capturedRequest is not null, "历史清洗与去重后成功构建请求");

        // 验证跨 cutoff 定稿的记录没有混入训练 items 前缀
        var leakedItem = capturedRequest!.Items.FirstOrDefault(it => it.Reviews.Count == 2 && it.Reviews[1].Rating == 1);
        Program.Check(leakedItem is null, "跨 cutoff 在未来定稿的记录未泄漏至训练前缀中");

        // 验证非法评分抛 ArgumentException
        var invalidHistory = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        invalidHistory[0].Rating = (StudyRating)99;
        var caughtArg = false;
        try
        {
            optimizer.OptimizeAsync(invalidHistory).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            caughtArg = true;
        }
        Program.Check(caughtArg, "非法评级必须抛出 ArgumentException");
    }

    private static void TestTimestampTieSplitNoLeakage()
    {
        var baseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var history = new List<CanonicalReview>();

        for (int i = 0; i < 20; i++)
        {
            var key = $"archive:tie-w{i:D2}";
            history.Add(new CanonicalReview
            {
                CanonicalId = $"cr:{key}:0", WordKey = key, SessionId = "s0",
                Origin = CanonicalOrigin.FirstLearnAggregate, Rating = StudyRating.Known,
                ReviewedAtUtc = baseDate, CompletedAtUtc = baseDate
            });
            history.Add(new CanonicalReview
            {
                CanonicalId = $"cr:{key}:1", WordKey = key, SessionId = "s1",
                Origin = CanonicalOrigin.FirstRetrieval, Rating = StudyRating.Known,
                ReviewedAtUtc = baseDate.AddDays(10), CompletedAtUtc = baseDate.AddDays(10)
            });
            history.Add(new CanonicalReview
            {
                CanonicalId = $"cr:{key}:2", WordKey = key, SessionId = "s2",
                Origin = CanonicalOrigin.FirstRetrieval, Rating = StudyRating.Forgot,
                ReviewedAtUtc = baseDate.AddDays(20), CompletedAtUtc = baseDate.AddDays(20)
            });
        }

        FsrsOptimizerRequest? captured = null;
        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
            TrainFraction = 0.5,
        })
        {
            TestInvoker = (req, token) =>
            {
                captured = req;
                return Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()));
            }
        };

        try { optimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException) { }

        Program.Check(captured is not null && captured.Items.Count == 20,
            "同刻20条目标整体留在过去训练侧，不按数量拆分");
        Program.Check(captured!.Items.All(item => item.Reviews.Count == 2 && item.Reviews[^1].Rating == 3),
            "未来同刻遗忘目标未混入训练前缀");
        captured = null;
        optimizer.Options.TrainFraction = 0.625;
        var rejected = false;
        try { optimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException) { rejected = true; }
        Program.Check(rejected && captured is null,
            "切点落在同刻组内部时不拆组：未来侧不足则拒绝调用helper");
    }

    private static void TestCancellationDuringCpuScan()
    {
        using var stop = new CancellationTokenSource();
        var input = new CancelDuringEnumeration(CreateSyntheticHistory(), stop);
        var invoked = false;
        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 5, MinimumValidationReviews = 5,
        })
        {
            TestInvoker = (_, _) =>
            {
                invoked = true;
                return Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()));
            }
        };
        var canceled = false;
        try { optimizer.OptimizeAsync(input, stop.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        catch (Exception) { }
        Program.Check(canceled && !invoked,
            "CPU扫描过程中取消立即停止枚举，不继续建prefix或调用helper");
    }

    private sealed class CancelDuringEnumeration : IReadOnlyList<CanonicalReview>
    {
        private readonly IReadOnlyList<CanonicalReview> _rows;
        private readonly CancellationTokenSource _stop;
        internal CancelDuringEnumeration(IReadOnlyList<CanonicalReview> rows, CancellationTokenSource stop)
        { _rows = rows; _stop = stop; }
        public int Count => _rows.Count;
        public CanonicalReview this[int index] => _rows[index];
        public IEnumerator<CanonicalReview> GetEnumerator()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (i > 10) throw new InvalidOperationException("CPU扫描取消后仍继续枚举");
                if (i == 10) _stop.Cancel();
                yield return _rows[i];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void TestTrainingItemsRespectGlobalRecency()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        FsrsOptimizerRequest Capture(IReadOnlyList<CanonicalReview> rows)
        {
            FsrsOptimizerRequest? request = null;
            var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
            {
                MinimumTrainReviews = 5, MinimumValidationReviews = 5, TrainFraction = 0.8,
            })
            {
                TestInvoker = (req, _) =>
                {
                    request = req;
                    return Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()));
                }
            };
            try { optimizer.OptimizeAsync(rows).GetAwaiter().GetResult(); }
            catch (FsrsOptimizationException) { }
            Program.Check(request is not null, "交错多词历史构建真实训练请求");
            return request!;
        }
        var actual = Capture(history);
        var targets = history.Where(r => r.Origin == CanonicalOrigin.FirstRetrieval)
            .OrderBy(r => r.ReviewedAtUtc).ToList();
        var cutoff = targets[(int)Math.Floor(targets.Count * 0.8) - 1].ReviewedAtUtc;
        var expected = targets.Where(r => r.ReviewedAtUtc <= cutoff)
            .Select(target => new
            {
                Target = target,
                Prefix = history.Where(r => r.WordKey == target.WordKey && r.ReviewedAtUtc <= target.ReviewedAtUtc)
                    .OrderBy(r => r.ReviewedAtUtc).ToList(),
            })
            .Where(x => x.Prefix.All(r => r.CompletedAtUtc <= cutoff))
            .Select(x => string.Join(";", x.Prefix.Select((r, i) =>
                $"{Fsrs6Model.ToFsrsRating(r.Rating)}:{(i == 0 ? 0 : Fsrs6Model.ElapsedWholeDays(x.Prefix[i - 1].ReviewedAtUtc, r.ReviewedAtUtc))}")))
            .ToArray();
        var signatures = actual.Items.Select(item => string.Join(";", item.Reviews.Select(r => $"{r.Rating}:{r.DeltaT}"))).ToArray();
        Program.Check(signatures.SequenceEqual(expected),
            "上游recency输入按跨词目标时间全局排序，不能把词key顺序当近期权重");
        foreach (var row in history)
        {
            var index = int.Parse(row.WordKey.Split('-')[^1]);
            row.WordKey = $"archive:renamed-{14 - index:D3}";
        }
        var renamed = Capture(history);
        Program.Check(JsonSerializer.Serialize(actual.Items) == JsonSerializer.Serialize(renamed.Items),
            "仅重命名词key不改变近期加权训练顺序或payload");
    }

    private static void TestJsonContractAndMetadataVerification()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        FsrsOptimizerRequest? captured = null;

        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
        })
        {
            TestInvoker = (req, token) =>
            {
                captured = req;
                return Task.FromResult(new FsrsOptimizerResponse
                {
                    Version = 1,
                    Algorithm = "FSRS-6",
                    Protocol = "fsrs-optimizer-v1",
                    ModelVersion = "5.2.0",
                    GitSha = "aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c",
                    ParameterCount = 21,
                    EffectiveSeed = 2023,
                    Weights = Fsrs6Weights.Defaults.ToArray(),
                });
            }
        };

        try { optimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException) { }

        // 验证完整有效响应通过元数据核验
        Program.Check(captured is not null, "成功捕获 helper JSON 请求");
        Program.Check(captured!.Version == 1, "请求 version 字段严格等于 1");
        Program.Check(captured.Algorithm == "FSRS-6", "请求 algorithm 字段严格等于 'FSRS-6'");
        Program.Check(captured.EnableShortTerm, "请求 enable_short_term 严格等于 true");
        Program.Check(captured.Threads == 1, "请求 threads 严格限制为 1");
        Program.Check(captured.Seed == 2023, "请求 seed 严格按官方 5.2 API 固定为 2023");

        var allFirstReviewsZero = captured.Items.All(item => item.Reviews.Count > 0 && item.Reviews[0].DeltaT == 0);
        Program.Check(allFirstReviewsZero, "每个 item 前缀首条 delta_t 严格为 0");

        // 验证元数据 protocol 缺失或不匹配时拒绝
        var badProtocolOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.Protocol = "wrong-protocol";
                return Task.FromResult(resp);
            }
        };
        var caughtBadProtocol = false;
        try { badProtocolOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtBadProtocol = ex.Message.Contains("protocol"); }
        Program.Check(caughtBadProtocol, "响应 protocol 不匹配时严格抛出 FsrsOptimizationException 拒绝");

        // 验证元数据 git_sha 缺失或不匹配时拒绝
        var badShaOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.GitSha = "wrong-sha";
                return Task.FromResult(resp);
            }
        };
        var caughtBadSha = false;
        try { badShaOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtBadSha = ex.Message.Contains("git_sha"); }
        Program.Check(caughtBadSha, "响应 git_sha 不匹配时严格抛出 FsrsOptimizationException 拒绝");

        // 验证 effective_seed 缺失或不匹配 (例如 20260301) 时拒绝
        var missingSeedOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.EffectiveSeed = null;
                return Task.FromResult(resp);
            }
        };
        var caughtMissingSeed = false;
        try { missingSeedOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtMissingSeed = ex.Message.Contains("effective_seed"); }
        Program.Check(caughtMissingSeed, "响应缺失 effective_seed 时严格拒绝，绝不使用虚假默认值填补");

        var badSeedOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.EffectiveSeed = 20260301;
                return Task.FromResult(resp);
            }
        };
        var caughtBadSeed = false;
        try { badSeedOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtBadSeed = ex.Message.Contains("effective_seed"); }
        Program.Check(caughtBadSeed, "响应 effective_seed 不等于 2023 时严格抛 FsrsOptimizationException 拒绝");

        // 验证 max_sequence_length 缺失或不为 64 时拒绝
        var missingMaxSeqOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.MaxSequenceLength = null;
                return Task.FromResult(resp);
            }
        };
        var caughtMissingMaxSeq = false;
        try { missingMaxSeqOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtMissingMaxSeq = ex.Message.Contains("max_sequence_length"); }
        Program.Check(caughtMissingMaxSeq, "响应缺失 max_sequence_length 时严格拒绝");

        // 验证 compatibility 兼容补丁标识缺失或不匹配时拒绝（旧未修 helper 拒绝）
        var missingCompatOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.CompatibilityId = null;
                resp.CompatibilityRevision = null;
                return Task.FromResult(resp);
            }
        };
        var caughtMissingCompat = false;
        try { missingCompatOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtMissingCompat = ex.Message.Contains("compatibility"); }
        Program.Check(caughtMissingCompat, "响应缺失 compatibility 兼容补丁标识时拒绝（拒绝旧版未修复 helper）");

        // 验证 patch_sha256 缺失或不匹配时拒绝
        var missingPatchShaOptimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions { MinimumTrainReviews = 10, MinimumValidationReviews = 10 })
        {
            TestInvoker = (req, token) =>
            {
                var resp = CreateValidResponse(Fsrs6Weights.Defaults.ToArray());
                resp.PatchSha256 = null;
                return Task.FromResult(resp);
            }
        };
        var caughtMissingPatchSha = false;
        try { missingPatchShaOptimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { caughtMissingPatchSha = ex.Message.Contains("patch_sha256"); }
        Program.Check(caughtMissingPatchSha, "响应缺失 patch_sha256 补丁哈希时严格抛 FsrsOptimizationException 拒绝");
    }

    private static void TestClassBalanceAndSampleThresholds()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        foreach (var r in history) r.Rating = StudyRating.Known;

        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
            MinimumPositiveReviews = 5,
            MinimumNegativeReviews = 5,
        })
        {
            TestInvoker = (req, token) => Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()))
        };

        var caughtBalance = false;
        try
        {
            optimizer.OptimizeAsync(history).GetAwaiter().GetResult();
        }
        catch (FsrsOptimizationException ex)
        {
            caughtBalance = ex.Message.Contains("类别平衡") || ex.Message.Contains("负例");
        }
        Program.Check(caughtBalance, "留出验证集负例不足门槛时拒绝并抛出 FsrsOptimizationException");
    }

    private static void TestDualBaselineEvaluationAndGating()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);

        // 构造有效的合成改善权重（在含较高遗忘比例的数据上降低稳定性初始值）
        var betterWeights = Fsrs6Weights.Defaults.ToArray();
        betterWeights[0] = 0.15;
        betterWeights[1] = 0.80;
        betterWeights[2] = 1.20;

        var lastGood = Fsrs6Weights.Create(betterWeights, Fsrs6Weights.SourceLastGood);

        // 测试点 1：候选相比 Defaults 有改善，但相比已有的 LastGood 出现劣化
        var worseThanLastGood = Fsrs6Weights.Defaults.ToArray();
        worseThanLastGood[0] = 0.18; // 优于 Defaults (0.212)，但劣于 LastGood (0.15)
        worseThanLastGood[1] = 1.00;
        worseThanLastGood[2] = 1.80;

        var optDeterioration = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
            MinimumPositiveReviews = 2,
            MinimumNegativeReviews = 2,
            LastGoodWeights = lastGood,
            MinimumLogLossImprovement = 0.0001,
            MinimumBrierImprovement = 0.0,
        })
        {
            TestInvoker = (req, token) => Task.FromResult(CreateValidResponse(worseThanLastGood))
        };

        var caughtDeterioration = false;
        try
        {
            optDeterioration.OptimizeAsync(history).GetAwaiter().GetResult();
        }
        catch (FsrsOptimizationException ex)
        {
            caughtDeterioration = ex.Message.Contains("LastGood");
        }
        Program.Check(caughtDeterioration, "候选即使优于 Defaults，但劣于 LastGood 时拒绝并抛异常（防性能劣化）");

        // 测试点 2：双基线同时取得改善与指标记录
        var optSuccess = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
            MinimumPositiveReviews = 2,
            MinimumNegativeReviews = 2,
            LastGoodWeights = Fsrs6Weights.Defaults,
            MinimumLogLossImprovement = 0.0001,
            MinimumBrierImprovement = 0.0,
        })
        {
            TestInvoker = (req, token) => Task.FromResult(CreateValidResponse(betterWeights))
        };

        var result = optSuccess.OptimizeAsync(history).GetAwaiter().GetResult();
        Program.Check(result is not null, "双基线均获得改善时成功返回参数");
        Program.Check(optSuccess.LastMetrics is not null, "成功记录双基线统计指标");
        Program.Check(optSuccess.LastMetrics!.DefaultsLogLossImprovement > 0, "记录了 Defaults LogLoss 改善量");
        Program.Check(optSuccess.LastMetrics.LastGoodLogLossImprovement >= 0, "记录了 LastGood LogLoss 改善量");
        Program.Check(optSuccess.LastMetrics.DefaultsBrierImprovement >= 0, "记录了 Defaults Brier 改善量");
    }

    private static void TestWeightsDimensionAndClippingRefusal()
    {
        var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);

        // 1. 测试 2 倍上限越界值（例如 200.0，上限 100.0）：必须直接拒绝，严禁任意 Math.Clamp 掩盖
        var bad2x = Fsrs6Weights.Defaults.ToArray();
        bad2x[0] = 200.0;

        var optBad = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 5, MinimumValidationReviews = 5,
            MinimumPositiveReviews = 1, MinimumNegativeReviews = 1,
        })
        {
            TestInvoker = (req, token) => Task.FromResult(CreateValidResponse(bad2x))
        };

        var caughtBad2x = false;
        try
        {
            optBad.OptimizeAsync(history).GetAwaiter().GetResult();
        }
        catch (FsrsOptimizationException ex)
        {
            caughtBad2x = ex.Message.Contains("clip") || ex.Message.Contains("超出");
        }
        Program.Check(caughtBad2x, "候选参数超界 2 倍时严格拒绝，严禁以 Math.Clamp 掩盖损坏响应");

        var nearLower = Fsrs6Weights.Defaults.ToArray();
        nearLower[0] = 0.001 - 1e-5;
        var optLower = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 5, MinimumValidationReviews = 5,
            MinimumPositiveReviews = 1, MinimumNegativeReviews = 1,
            MinimumLogLossImprovement = -100, MinimumBrierImprovement = -100,
        }) { TestInvoker = (_, _) => Task.FromResult(CreateValidResponse(nearLower)) };
        var lowerRejected = false;
        try { optLower.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { lowerRejected = ex.Message.Contains("clip"); }
        Program.Check(lowerRejected, "w0 下限0.001减1e-5不是f32舍入，必须拒绝");

        // 2. 真正两个 f32 ULP 内才允许边界投影，配置只能进一步收紧。
        var minorRounding = Fsrs6Weights.Defaults.ToArray();
        minorRounding[0] = (double)float.BitIncrement(100.0f);

        var optRounding = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 5, MinimumValidationReviews = 5,
            MinimumPositiveReviews = 1, MinimumNegativeReviews = 1,
            RoundingTolerance = 1e-4,
            MinimumLogLossImprovement = -100.0,
            MinimumBrierImprovement = -100.0,
        })
        {
            TestInvoker = (req, token) => Task.FromResult(CreateValidResponse(minorRounding))
        };

        var roundingResult = optRounding.OptimizeAsync(history).GetAwaiter().GetResult();
        Program.Check(roundingResult[0] == 100.0, "一个 f32 ULP 的超界投影至边界 (100.0)");
        var lowerF32 = Fsrs6Weights.Defaults.ToArray();
        lowerF32[0] = (double)float.BitDecrement(0.001f);
        optRounding.TestInvoker = (_, _) => Task.FromResult(CreateValidResponse(lowerF32));
        Program.Check(optRounding.OptimizeAsync(history).GetAwaiter().GetResult()[0] == 0.001,
            "真实 f32 下边界一个 ULP 的舍入可投影");
        var outsideUlps = Fsrs6Weights.Defaults.ToArray();
        outsideUlps[0] = (double)float.BitIncrement(float.BitIncrement(float.BitIncrement(100.0f)));
        optRounding.TestInvoker = (_, _) => Task.FromResult(CreateValidResponse(outsideUlps));
        var threeUlpsRejected = false;
        try { optRounding.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { threeUlpsRejected = ex.Message.Contains("clip"); }
        Program.Check(threeUlpsRejected, "三个 f32 ULP 超界即使小于1e-4也拒绝");
        optRounding.Options.RoundingTolerance = 0;
        optRounding.TestInvoker = (_, _) => Task.FromResult(CreateValidResponse(minorRounding));
        var zeroToleranceRejected = false;
        try { optRounding.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException ex) { zeroToleranceRejected = ex.Message.Contains("clip"); }
        Program.Check(zeroToleranceRejected, "配置容差零进一步收紧，不允许一个 ULP 超界");

    }

    private static void TestSubprocessControlTimeoutAndCancellation()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, $"mock_helper_{Guid.NewGuid():N}.sh");
        try
        {
            File.WriteAllText(scriptPath, "#!/bin/sh\nread -r line\necho '{\"error\":\"mock-fail\"}' >&2\nexit 42\n");
            MakeExecutable(scriptPath);

            var history = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
            var optExit = new FsrsParameterOptimizer(scriptPath, new FsrsOptimizerOptions
            {
                MinimumTrainReviews = 5,
                MinimumValidationReviews = 5,
            });

            var caughtExit = false;
            try
            {
                optExit.OptimizeAsync(history).GetAwaiter().GetResult();
            }
            catch (FsrsOptimizationException ex)
            {
                caughtExit = ex.Message.Contains("42");
            }
            Program.Check(caughtExit, "子进程非零退出码 (42) 正确被捕获为 FsrsOptimizationException");

            // 超时与有限清理等待预算测试
            File.WriteAllText(scriptPath, "#!/bin/sh\nsleep 10\nexit 0\n");
            MakeExecutable(scriptPath);

            var optTimeout = new FsrsParameterOptimizer(scriptPath, new FsrsOptimizerOptions
            {
                Timeout = TimeSpan.FromMilliseconds(500),
                CleanupWaitBudget = TimeSpan.FromSeconds(2),
                MinimumTrainReviews = 5,
                MinimumValidationReviews = 5,
            });

            var caughtTimeout = false;
            try
            {
                optTimeout.OptimizeAsync(history).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                caughtTimeout = true;
            }
            Program.Check(caughtTimeout, "子进程超时在有限清理预算内终止子进程树并抛 TimeoutException");

            // 真实子进程取消测试：子进程运行中时由外部 CancellationToken 取消
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var optCancel = new FsrsParameterOptimizer(scriptPath, new FsrsOptimizerOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                CleanupWaitBudget = TimeSpan.FromSeconds(2),
                MinimumTrainReviews = 5,
                MinimumValidationReviews = 5,
            });

            var caughtCancel = false;
            try
            {
                optCancel.OptimizeAsync(history, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                caughtCancel = true;
            }
            Program.Check(caughtCancel, "外部 CancellationToken 取消真实子进程抛 OperationCanceledException 并受控清理");

            // 真实子进程管道输出超界测试：输出超过 MaxStdoutBytes
            var bigStdoutScript = Path.Combine(AppContext.BaseDirectory, $"mock_big_stdout_{Guid.NewGuid():N}.sh");
            try
            {
                File.WriteAllText(bigStdoutScript, "#!/bin/sh\nhead -c 131072 /dev/zero | tr '\\0' 'A'\nexit 0\n");
                MakeExecutable(bigStdoutScript);
                var optBigStdout = new FsrsParameterOptimizer(bigStdoutScript, new FsrsOptimizerOptions
                {
                    MaxStdoutBytes = 4096,
                    MinimumTrainReviews = 5,
                    MinimumValidationReviews = 5,
                });
                var caughtBigStdout = false;
                try
                {
                    optBigStdout.OptimizeAsync(history).GetAwaiter().GetResult();
                }
                catch (FsrsOptimizationException ex)
                {
                    caughtBigStdout = ex.Message.Contains("字节预算上限");
                }
                Program.Check(caughtBigStdout, "子进程 stdout 输出超出字节预算上限时严格抛 FsrsOptimizationException 拒绝");
            }
            finally
            {
                try { File.Delete(bigStdoutScript); } catch { }
            }

            // 真实子进程输出缺失关键元数据（旧版未打补丁 helper 响应）测试
            var corruptMetaScript = Path.Combine(AppContext.BaseDirectory, $"mock_corrupt_meta_{Guid.NewGuid():N}.sh");
            try
            {
                File.WriteAllText(corruptMetaScript, "#!/bin/sh\necho '{\"version\":1,\"protocol\":\"fsrs-optimizer-v1\",\"algorithm\":\"FSRS-6\",\"model_version\":\"5.2.0\",\"git_sha\":\"aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c\",\"parameter_count\":21,\"weights\":[0.212,1.2931,2.3065,8.2956,6.4133,0.8334,3.0194,0.001,1.8722,0.1666,0.796,1.4835,0.0614,0.2629,1.6483,0.6014,1.8729,0.5425,0.0912,0.0658,0.1542]}'\nexit 0\n");
                MakeExecutable(corruptMetaScript);
                var optCorruptMeta = new FsrsParameterOptimizer(corruptMetaScript, new FsrsOptimizerOptions
                {
                    MinimumTrainReviews = 5,
                    MinimumValidationReviews = 5,
                });
                var caughtCorruptMeta = false;
                try
                {
                    optCorruptMeta.OptimizeAsync(history).GetAwaiter().GetResult();
                }
                catch (FsrsOptimizationException ex)
                {
                    caughtCorruptMeta = ex.Message.Contains("effective_seed") || ex.Message.Contains("compatibility") || ex.Message.Contains("patch_sha256");
                }
                Program.Check(caughtCorruptMeta, "真实子进程输出缺失关键元数据（effective_seed/补丁标识）时被严格拒绝，旧版 helper 不可冒用");
            }
            finally
            {
                try { File.Delete(corruptMetaScript); } catch { }
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static void TestBudgetsAndSequenceLengthPrefixExclusion()
    {
        // 反例 1：直接在 Fsrs6Scheduler 上验证第 65 次复习绝不重置 D/S
        var scheduler = new Fsrs6Scheduler(Fsrs6Weights.Defaults);
        var baseDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FsrsCardState? card = null;

        for (int i = 0; i < 70; i++)
        {
            var reviewDate = baseDate.AddDays(i * 10);
            var outcome = scheduler.Review(card, StudyRating.Known, reviewDate);
            card = outcome.Card;

            if (i == 63)
            {
                // 第 64 次复习后卡片稳定性已演进累积至极高值
                Program.Check(card.Stability > 100.0, "经过 64 次连续成功复习后稳定性累积远大于初始值");
            }
            else if (i == 64)
            {
                // 第 65 次复习（索引 64）：
                // 严禁将 D/S 重置回初始学习值 S0 (2.3065) 或 D0
                var defaultsInitialGoodS = Fsrs6Weights.Defaults[2]; // 2.3065
                Program.Check(card.Stability > 100.0 && Math.Abs(card.Stability - defaultsInitialGoodS) > 10.0,
                    "反例证明：第 65 次复习状态由历史持续演进推进，绝不重置为初始学习稳定性 S0");
                Program.Check(card.Difficulty < 5.0, "第 65 次复习难度正常维持，绝不重置为初学初始难度 D0");
            }
        }

        // 反例 2：在 FsrsParameterOptimizer 中验证 > 64 历史处理机制
        // 构造包含 70 条复习的真实历史词卡
        var longKey = "archive:long-seq-70";
        var history = new List<CanonicalReview>();

        for (int i = 0; i < 70; i++)
        {
            history.Add(new CanonicalReview
            {
                CanonicalId = $"cr:{longKey}:{i}",
                WordKey = longKey,
                SessionId = $"s{i}",
                Origin = (i == 0) ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval,
                Rating = (i % 6 == 0) ? StudyRating.Forgot : StudyRating.Known,
                ReviewedAtUtc = baseDate.AddDays(i * 5),
                CompletedAtUtc = baseDate.AddDays(i * 5).AddMinutes(5),
                Revision = 1
            });
        }

        // 补足验证所需的其他词卡
        var additional = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 5, baseDate: baseDate.AddDays(360));
        history.AddRange(additional);

        FsrsOptimizerRequest? captured = null;
        var optimizer = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            MaxSequenceLength = 64,
            MinimumTrainReviews = 5,
            MinimumValidationReviews = 5,
        })
        {
            TestInvoker = (req, token) =>
            {
                captured = req;
                return Task.FromResult(CreateValidResponse(Fsrs6Weights.Defaults.ToArray()));
            }
        };

        try { optimizer.OptimizeAsync(history).GetAwaiter().GetResult(); }
        catch (FsrsOptimizationException) { }

        Program.Check(captured is not null, "超长序列历史下成功完成前缀处理并构建请求");
        var allItemsUnderOrEqual64 = captured!.Items.All(it => it.Reviews.Count <= 64);
        Program.Check(allItemsUnderOrEqual64, "所有训练 items 序列长度严格不超过官方限制 MaxSequenceLength (64)");

        var allItemsStartWithZeroDeltaT = captured.Items.All(it => it.Reviews.Count > 0 && it.Reviews[0].DeltaT == 0);
        Program.Check(allItemsStartWithZeroDeltaT, "所有训练 items 首条均为真正初始复习且 delta_t 严格为 0");

        // 验证没有将窗口首条冒充初学：所有 item 的第 0 条对应真正初始记录，而不是截断窗口的首条
        // 且前缀超过 64 的目标被明确排除并记录计数
        Program.Check(optimizer.LastMetrics is not null && optimizer.LastMetrics.ExcludedLongSequenceTargetCount > 0,
            "超过 64 限制的训练目标被明确排除并在 ExcludedLongSequenceTargetCount 中真实记录");

        // 验证长历史卡片未被全局拒绝，其早期 <= 64 的有效前缀训练目标仍然参与了训练
        var longCardItems = captured.Items.Where(it => it.Reviews.Count > 1).ToList();
        Program.Check(longCardItems.Count > 0, "长历史卡片未被全局拒绝，其 <= 64 的早期前缀依然参与训练");
    }

    private static void TestNativeProbeExecutionWhenAvailable()
    {
        var possiblePaths = new[]
        {
            Environment.GetEnvironmentVariable("LEXI_FSRS_OPTIMIZER_BIN"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../native/fsrs-optimizer/bin/fsrs-optimizer")),
        };

        var realHelperPath = possiblePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (realHelperPath is null)
        {
            Console.WriteLine("SKIP: 未探测到原生 helper 可执行文件，跳过原生可执行文件直接测试。");
            return;
        }

        Console.WriteLine($"Found native helper: {realHelperPath}");

        // 实测 A：负对照（零梯度/无收益时拒绝伪造参数，保留 fallback）
        Console.WriteLine("--- 11.A 真实 Native Helper 探测与零梯度拒绝实测（不冒充收益）---");
        var optRefusal = new FsrsParameterOptimizer(realHelperPath, new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 10,
        });

        var refusalHistory = CreateSyntheticHistory(wordCount: 15, reviewsPerWord: 6);
        var caught = false;
        try
        {
            optRefusal.OptimizeAsync(refusalHistory).GetAwaiter().GetResult();
        }
        catch (FsrsOptimizationException ex)
        {
            caught = true;
            Program.Check(ex.Message.Contains("helper") || ex.Message.Contains("退出码") || ex.Message.Contains("Defaults 基线"),
                "真实 helper 在数据不足/零梯度时拒绝返回伪造参数，或由于无留出收益被受控拒绝");
        }
        Program.Check(caught, "真实 helper 实测：不冒充收益，真实失败或受控拦截并保留 fallback");

        // 实测 B：真实 patched helper 端到端成功优化实测（非 mock，真正 21 参数演化 + 留出验证通过）
        Console.WriteLine("--- 11.B 真实 Native Helper 端到端成功优化实测（固定偏离合成机制证明）---");
        var optSuccess = new FsrsParameterOptimizer(realHelperPath, new FsrsOptimizerOptions
        {
            MinimumTrainReviews = 10,
            MinimumValidationReviews = 20,
            MinimumPositiveReviews = 5,
            MinimumNegativeReviews = 5,
            MinimumLogLossImprovement = 0.001,
            MinimumBrierImprovement = 0.0,
            TrainFraction = 0.65,
        });

        // 采用基于 Fsrs6Scheduler 固定合理偏离 defaults 的合成数据生成机制
        var validHistory = CreateSchedulerGeneratedHistory(wordCount: 220, reviewsPerWord: 5, seed: 2026);
        var trainedWeights = optSuccess.OptimizeAsync(validHistory).GetAwaiter().GetResult();

        Program.Check(trainedWeights is not null, "真实 helper 在合理偏离数据上端到端成功返回优化参数");
        Program.Check(trainedWeights!.Source == Fsrs6Weights.SourceOptimized, "训练返回权重标记为 SourceOptimized");
        Program.Check(trainedWeights.Weights.Count == 21, "训练返回 21 维 FSRS-6 完整权重");

        // 验证 21 参数发生了真实变化（非 defaults）
        var defaultWeights = Fsrs6Weights.Defaults;
        var evolvedParamCount = 0;
        for (int i = 0; i < 21; i++)
        {
            if (Math.Abs(trainedWeights[i] - defaultWeights[i]) > 1e-4)
            {
                evolvedParamCount++;
            }
        }
        Program.Check(evolvedParamCount > 0, $"真实 helper 产生参数真实进化（实际变动 {evolvedParamCount}/21 维）");

        // 验证严格元数据核验通过并记录在 LastMetrics
        var metrics = optSuccess.LastMetrics;
        Program.Check(metrics is not null, "成功记录端到端指标 LastMetrics");
        Program.Check(metrics!.EffectiveSeed == 2023, "真实 helper 报告 effective_seed == 2023 校验通过");
        Program.Check(metrics.MaxSequenceLength == 64, "真实 helper 报告 max_sequence_length == 64 校验通过");
        Program.Check(metrics.CompatibilityId == "fsrs6-hard-floor-2-v1", "真实 helper 兼容性补丁标识 fsrs6-hard-floor-2-v1 校验通过");
        Program.Check(string.Equals(metrics.PatchSha256, "4c52a7583e4c939a93641b1655872acc026186a9b5a5524afd7b83a5d1eed5bb", StringComparison.OrdinalIgnoreCase),
            "真实 helper 补丁 SHA-256 哈希校验通过");

        // 验证未来留出验证双基线真实超越门槛（未弱化门槛）
        Program.Check(metrics.DefaultsLogLossImprovement >= 0.001,
            $"留出验证 LogLoss 真实改善量 ({metrics.DefaultsLogLossImprovement:F6}) 严格超越门槛 (0.001)");
        Program.Check(metrics.DefaultsBrierImprovement >= 0.0,
            $"留出验证 Brier 真实改善量 ({metrics.DefaultsBrierImprovement:F6}) 严格不劣于基线");

        Console.WriteLine($"[真实 Native Helper E2E 指标]");
        Console.WriteLine($"  - 训练集 Item 数量: {metrics.TrainReviews}");
        Console.WriteLine($"  - 留出验证集样本数: {metrics.ValidationReviews} (正例 {metrics.ValidationPositives}, 负例 {metrics.ValidationNegatives})");
        Console.WriteLine($"  - Defaults LogLoss: {metrics.DefaultsLogLoss:F6} -> Candidate: {metrics.CandidateLogLoss:F6} (改善 +{metrics.DefaultsLogLossImprovement:F6})");
        Console.WriteLine($"  - Defaults Brier:   {metrics.DefaultsBrier:F6} -> Candidate: {metrics.CandidateBrier:F6} (改善 +{metrics.DefaultsBrierImprovement:F6})");
        Console.WriteLine($"  - 耗时: {metrics.Duration.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  - 提示: 本测试合成数据仅证明优化与验证机制通路，非用户真实收益。");
    }

    private static FsrsOptimizerResponse CreateValidResponse(double[] weights) => new()
    {
        Version = 1,
        Protocol = FsrsParameterOptimizer.ExpectedProtocol,
        Algorithm = FsrsParameterOptimizer.ExpectedAlgorithm,
        ModelVersion = FsrsParameterOptimizer.ExpectedModelVersion,
        GitSha = FsrsParameterOptimizer.ExpectedGitSha,
        ParameterCount = 21,
        Weights = weights,
        EffectiveSeed = FsrsParameterOptimizer.OfficialApiSeed,
        MaxSequenceLength = FsrsParameterOptimizer.ExpectedMaxSequenceLength,
        CompatibilityId = FsrsParameterOptimizer.ExpectedCompatibilityId,
        CompatibilityRevision = FsrsParameterOptimizer.ExpectedCompatibilityId,
        PatchSha256 = FsrsParameterOptimizer.ExpectedPatchSha256,
    };

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) { WindowsOptimizerFixture.Materialize(path); return; }
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit();
        }
        catch { }
    }

    private static List<CanonicalReview> CreateSchedulerGeneratedHistory(
        int wordCount = 220,
        int reviewsPerWord = 5,
        DateTime? baseDate = null,
        int seed = 2026)
    {
        var baseUtc = baseDate ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rng = new Random(seed);

        // 真实模拟：学习者初始稳定性低于默认、稳定性增长偏高、遗忘衰减更快
        var targetWeights = Fsrs6Weights.Defaults.ToArray();
        targetWeights[0] = 0.10;
        targetWeights[1] = 0.60;
        targetWeights[2] = 1.20; // 默认 2.3065，学习者 Good 初始稳定性偏低
        targetWeights[3] = 4.00; // 默认 8.2956
        targetWeights[8] = 2.40; // 默认 1.8722
        targetWeights[20] = 0.25; // 默认 0.1542，学习者遗忘衰减更快

        var generatorWeights = Fsrs6Weights.Create(targetWeights, Fsrs6Weights.SourceOptimized);
        var generatorScheduler = new Fsrs6Scheduler(generatorWeights);

        var list = new List<CanonicalReview>();

        for (int w = 0; w < wordCount; w++)
        {
            var wordKey = $"archive:sim-word-{w:D4}";
            FsrsCardState? card = null;
            var startDays = (w % 30) * 3;
            var currentUtc = baseUtc.AddDays(startDays).AddMinutes(w % 60);

            for (int r = 0; r < reviewsPerWord; r++)
            {
                if (r == 0)
                {
                    var rating = (w % 5 == 0) ? StudyRating.Unsure : StudyRating.Known;
                    var outcome = generatorScheduler.Review(null, rating, currentUtc);
                    card = outcome.Card;

                    list.Add(new CanonicalReview
                    {
                        CanonicalId = $"cr:{wordKey}:0",
                        WordKey = wordKey,
                        SessionId = $"sess-{w}-0",
                        Origin = CanonicalOrigin.FirstLearnAggregate,
                        Rating = rating,
                        ReviewedAtUtc = currentUtc,
                        CompletedAtUtc = currentUtc.AddMinutes(2),
                        Revision = 1,
                        Invalidated = false,
                    });
                }
                else
                {
                    // 排期：真实梯度间隔 [1, 2, 4, 8, 15]
                    long deltaDays = r switch
                    {
                        1 => 1,
                        2 => 2,
                        3 => 4,
                        4 => 8,
                        _ => 15
                    };
                    currentUtc = currentUtc.AddDays(deltaDays).AddMinutes(1);

                    var actualR = generatorScheduler.Retrievability(card!.Stability, deltaDays);
                    var roll = rng.NextDouble();
                    StudyRating rating;
                    if (roll < actualR)
                    {
                        rating = (roll < actualR * 0.8) ? StudyRating.Known : StudyRating.Unsure;
                    }
                    else
                    {
                        rating = StudyRating.Forgot;
                    }

                    var outcome = generatorScheduler.Review(card, rating, currentUtc);
                    card = outcome.Card;

                    list.Add(new CanonicalReview
                    {
                        CanonicalId = $"cr:{wordKey}:{r}",
                        WordKey = wordKey,
                        SessionId = $"sess-{w}-{r}",
                        Origin = CanonicalOrigin.FirstRetrieval,
                        Rating = rating,
                        ReviewedAtUtc = currentUtc,
                        CompletedAtUtc = currentUtc.AddMinutes(3),
                        Revision = 1,
                        Invalidated = false,
                    });
                }
            }
        }

        return list;
    }

    private static List<CanonicalReview> CreateSyntheticHistory(
        int wordCount = 15,
        int reviewsPerWord = 6,
        DateTime? baseDate = null)
    {
        var baseUtc = baseDate ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<CanonicalReview>();
        for (int w = 0; w < wordCount; w++)
        {
            var wordKey = $"archive:word-{w:D3}";
            for (int r = 0; r < reviewsPerWord; r++)
            {
                var origin = (r == 0) ? CanonicalOrigin.FirstLearnAggregate : CanonicalOrigin.FirstRetrieval;
                var days = r * 10;
                var reviewedAt = baseUtc.AddDays(days).AddMinutes(w);
                var completedAt = reviewedAt.AddMinutes(5);

                StudyRating rating;
                if (r == 0) rating = StudyRating.Known;
                else if (r == reviewsPerWord - 1 && w % 3 == 0) rating = StudyRating.Forgot;
                else if (r == reviewsPerWord - 2 && w % 4 == 0) rating = StudyRating.Forgot;
                else if (w % 2 == 1 && r % 2 == 1) rating = StudyRating.Unsure;
                else rating = StudyRating.Known;

                list.Add(new CanonicalReview
                {
                    CanonicalId = $"cr:{wordKey}:session-{r}",
                    WordKey = wordKey,
                    SessionId = $"session-{r}",
                    Origin = origin,
                    Rating = rating,
                    ReviewedAtUtc = reviewedAt,
                    CompletedAtUtc = completedAt,
                    Revision = 1,
                    Invalidated = false,
                });
            }
        }
        return list;
    }
}

