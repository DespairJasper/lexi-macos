using Lexi;
using Lexi.Social;

/// <summary>Pure curve and interpolation math. No UI, no clock, no I/O.</summary>
internal static class SocialCurveTests
{
    internal static void Run(Action<bool, string> check)
    {
        // ---- C1: the shared builder must reproduce the existing statistics formula exactly ----
        var stabilities = new[] { 1.0, 3.5, 12.25, 40.0, 0.5 };
        // The exact expression the statistics page used before extraction.
        var scheduler = new Fsrs6Scheduler();
        var expected = Enumerable.Range(0, 121)
            .Select(i => (Days: i / 4d, Rate: stabilities.Average(s => scheduler.Retrievability(s, i / 4d))))
            .ToArray();
        var built = SocialCurve.Build(stabilities, scheduler.Retrievability);
        check(built.Count == 121, "the builder produces the 121-point protocol");
        var identical = built.Count == expected.Length;
        for (var i = 0; identical && i < built.Count; i++)
            identical = built[i].Days == expected[i].Days && built[i].Rate == expected[i].Rate;
        check(identical, "the extracted builder reproduces the existing page formula bit for bit");
        check(built[0].Days == 0 && built[^1].Days == 30, "the grid runs from 0 to 30 days");
        check(built[1].Days == 0.25 && built[2].Days == 0.5, "the grid steps by a quarter day");
        check(built.All(p => double.IsFinite(p.Rate) && p.Rate is >= 0 and <= 1), "every rate stays a finite probability");
        check(built[0].Rate > built[^1].Rate, "recall decays as the interval grows");

        // Filtering matches the page: only finite, strictly positive stabilities take part.
        var filtered = SocialCurve.Build([1.0, double.NaN, 0, -2, double.PositiveInfinity], scheduler.Retrievability);
        var onlyOne = SocialCurve.Build([1.0], scheduler.Retrievability);
        check(filtered.Count == onlyOne.Count && filtered[60].Rate == onlyOne[60].Rate,
            "non-finite, zero and negative stabilities are excluded, exactly as the page did");
        check(SocialCurve.Build([], scheduler.Retrievability).Count == 0, "no usable card means no curve");
        check(SocialCurve.Build([double.NaN, 0], scheduler.Retrievability).Count == 0,
            "a curve made only of unusable cards stays empty rather than becoming a flat zero line");
        check(SocialCurve.Build([1.0], scheduler.Retrievability, 121).Count == 121, "the sample count is explicit");
        try { SocialCurve.Build([1.0], scheduler.Retrievability, 0); check(false, "a zero sample count must be rejected"); }
        catch (ArgumentOutOfRangeException) { check(true, "a zero sample count is rejected"); }

        // Personalised weights must flow through the same builder unchanged.
        // Retrievability depends only on stability, elapsed days, the decay (w20) and the factor
        // derived from it — not on the S0/D0 weights. So a personalisation only changes the curve
        // when the decay term moves; anything else must leave the drawn curve bit for bit identical.
        var tuned = Fsrs6Weights.OfficialDefaults.ToArray();
        tuned[20] *= 1.2;
        var personalised = new Fsrs6Scheduler(new SchedulerWeights(Fsrs6Weights.Create(tuned)));
        var tunedCurve = SocialCurve.Build(stabilities, personalised.Retrievability);
        var expectedTuned = Enumerable.Range(0, 121)
            .Select(i => stabilities.Average(s => personalised.Retrievability(s, i / 4d))).ToArray();
        check(tunedCurve.Select(p => p.Rate).SequenceEqual(expectedTuned), "personalised weights produce their own curve through the same builder");
        check(!tunedCurve.Select(p => p.Rate).SequenceEqual(built.Select(p => p.Rate)),
            "a personalised decay actually produces a different drawn curve");
        var irrelevant = Fsrs6Weights.OfficialDefaults.ToArray();
        irrelevant[0] += 0.05; irrelevant[2] += 0.4;
        var sameCurve = SocialCurve.Build(stabilities,
            new Fsrs6Scheduler(new SchedulerWeights(Fsrs6Weights.Create(irrelevant))).Retrievability);
        check(sameCurve.Select(p => p.Rate).SequenceEqual(built.Select(p => p.Rate)),
            "weights that do not enter retrievability leave the curve unchanged");

        // ---- friend curves: independent legends, no ranking ----
        var own = new SocialCurveView(Guid.NewGuid(), "我", DateTimeOffset.Parse("2026-10-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            12, built.Select(p => p.Rate).ToArray(), SocialCurve.Protocol);
        var visible = SocialCurveLegend.Build(own, [
            new SocialCurveView(Guid.NewGuid(), "小明", own.TakenAt, 30, built.Select(p => p.Rate).ToArray(), SocialCurve.Protocol),
            new SocialCurveView(Guid.NewGuid(), "小红", own.TakenAt, 5, built.Select(p => p.Rate).ToArray(), SocialCurve.Protocol)
        ]);
        check(visible.Count == 3 && visible[0].IsSelf, "the legend lists the current user first, then every authorized friend");
        check(visible[0].Label.StartsWith("自己") || visible[0].Label.Contains("我"), "the self entry is labelled as the current user");
        check(visible.Skip(1).All(e => !e.IsSelf), "friend entries are never marked as self");
        check(visible.Select(e => e.SampleCount).Distinct().Count() == 3, "each entry carries its own sample count");
        check(visible.Select(e => e.Color).Distinct().Count() == 3, "each entry gets its own colour");
        check(SocialCurveLegend.Build(own, []).Count == 1, "with no authorized friend only the self curve is listed");
        // No ranking: the order is self-then-server-order, not sorted by any score.
        check(visible[1].Label.Contains("小明") && visible[2].Label.Contains("小红"),
            "friend order follows the server order rather than any ranking");
        try { SocialCurveLegend.Build(null!, []); check(false, "a missing self curve must be rejected"); }
        catch (ArgumentNullException) { check(true, "a missing self curve is rejected"); }

        // ---- C3: curve list decoding ----
        var points = Enumerable.Range(0, 121).Select(i => (object)(1.0 - i / 240d)).ToArray();
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new object[]
        {
            new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 12, points, protocol = SocialCurve.Protocol }
        });
        var decoded = SocialDirectory.CurveList(payload);
        check(decoded.Count == 1 && decoded[0].Points.Count == 121 && decoded[0].SampleCount == 12, "a curve row decodes its 121 points");
        check(decoded[0].Protocol == SocialCurve.Protocol, "the protocol string is preserved for disclosure");
        Reject(check, "a curve with the wrong point count is rejected", () => SocialDirectory.CurveList(
            System.Text.Json.JsonSerializer.SerializeToElement(new object[] { new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 1, points = new[] { 0.5 }, protocol = SocialCurve.Protocol } })));
        Reject(check, "a curve point outside 0..1 is rejected", () => SocialDirectory.CurveList(
            System.Text.Json.JsonSerializer.SerializeToElement(new object[] { new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 1, points = points.Select((p, i) => (object)(i == 7 ? 1.5 : 0.5)).ToArray(), protocol = SocialCurve.Protocol } })));
        Reject(check, "a curve with a zero sample count is rejected", () => SocialDirectory.CurveList(
            System.Text.Json.JsonSerializer.SerializeToElement(new object[] { new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 0, points, protocol = SocialCurve.Protocol } })));
        Reject(check, "a duplicate user in one curve list is rejected", () => SocialDirectory.CurveList(
            System.Text.Json.JsonSerializer.SerializeToElement(new object[]
            {
                new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 1, points, protocol = SocialCurve.Protocol },
                new { user_id = own.UserId, nickname = "我", taken_at = "2026-10-10T00:00:00+00:00", sample_count = 2, points, protocol = SocialCurve.Protocol }
            })));
        Reject(check, "a curve list that is not an array is rejected", () => SocialDirectory.CurveList(System.Text.Json.JsonSerializer.SerializeToElement(new { })));

        // ---- C2: monotone interpolation ----
        var rising = new[] { (X: 0d, Y: 1d), (X: 1, Y: 1), (X: 2, Y: 4), (X: 3, Y: 4), (X: 4, Y: 9) };
        var smooth = MonotoneCurve.Smooth(rising, 8);
        check(smooth.Count > rising.Length, "smoothing densifies the series");
        check(smooth[0].Y == 1 && smooth[^1].Y == 9, "smoothing preserves both endpoints exactly");
        check(smooth.Zip(smooth.Skip(1)).All(p => p.Second.Y >= p.First.Y - 1e-12), "a rising series stays monotone after smoothing");
        check(smooth.All(p => p.Y >= 1 - 1e-9 && p.Y <= 9 + 1e-9), "smoothing never overshoots the data range");
        // A run between two equal values must stay flat: that is where a naive spline bulges.
        // The series is 1,1,4,4,9, so the flat runs are [0,1] and [2,3].
        var leading = smooth.Where(p => p.X is >= 0 and <= 1).ToArray();
        check(leading.All(p => Math.Abs(p.Y - 1) < 1e-9), "a leading flat run is not inflated by the interpolation");
        var interior = smooth.Where(p => p.X is >= 2 and <= 3).ToArray();
        check(interior.All(p => Math.Abs(p.Y - 4) < 1e-9), "an interior flat run stays exactly flat");
        var constant = MonotoneCurve.Smooth([(X: 0d, Y: 5d), (X: 1, Y: 5), (X: 2, Y: 5)], 4);
        check(constant.All(p => p.Y == 5), "a constant series is safe and stays constant");
        var falling = MonotoneCurve.Smooth([(X: 0d, Y: 9d), (X: 1, Y: 4), (X: 2, Y: 1)], 6);
        check(falling.Zip(falling.Skip(1)).All(p => p.Second.Y <= p.First.Y + 1e-12), "a falling series stays monotone");
        check(MonotoneCurve.Smooth([(X: 1d, Y: 2d)], 4).Count == 1, "a single node is returned unchanged");
        check(MonotoneCurve.Smooth([], 4).Count == 0, "an empty series is returned unchanged");
        var twoNodes = MonotoneCurve.Smooth([(X: 0d, Y: 0d), (X: 1, Y: 2)], 4);
        check(twoNodes.All(p => p.Y >= 0 - 1e-12 && p.Y <= 2 + 1e-12), "two nodes never overshoot either");
        try { MonotoneCurve.Smooth([(X: 1d, Y: 1d), (X: 1, Y: 2)], 4); check(false, "repeated x must be rejected"); }
        catch (ArgumentException) { check(true, "repeated x values are rejected rather than divided by zero"); }
        try { MonotoneCurve.Smooth([(X: 0d, Y: 0d), (X: 1, Y: double.NaN)], 4); check(false, "a non-finite value must be rejected"); }
        catch (ArgumentException) { check(true, "non-finite values are rejected"); }
    }

    private static void Reject(Action<bool, string> check, string message, Func<object?> action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException or KeyNotFoundException) { check(true, message); return; }
        check(false, message);
    }
}
