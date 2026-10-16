namespace Lexi.Social;

/// <summary>Shape-preserving interpolation for the weekly charts (Fritsch–Carlson).
/// The curve passes through every node, never leaves the interval spanned by its neighbours
/// (so a flat run stays flat and a peak is never inflated), and stays monotone between nodes.
/// Gaps are the caller's business: a missing day must split the series into separate segments
/// rather than being interpolated across.</summary>
public static class MonotoneCurve
{
    public static IReadOnlyList<(double X, double Y)> Smooth(IReadOnlyList<(double X, double Y)> nodes, int subdivisionsPerInterval)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (subdivisionsPerInterval < 1) throw new ArgumentOutOfRangeException(nameof(subdivisionsPerInterval));
        for (var index = 0; index < nodes.Count; index++)
        {
            if (!double.IsFinite(nodes[index].X) || !double.IsFinite(nodes[index].Y))
                throw new ArgumentException("Curve nodes must be finite.", nameof(nodes));
            if (index > 0 && nodes[index].X <= nodes[index - 1].X)
                throw new ArgumentException("Curve nodes must strictly increase in x.", nameof(nodes));
        }
        if (nodes.Count <= 2)
        {
            // One or two nodes cannot bulge; a straight line is already shape preserving.
            if (nodes.Count < 2) return nodes.ToArray();
            var line = new List<(double, double)>();
            for (var step = 0; step <= subdivisionsPerInterval; step++)
            {
                var t = (double)step / subdivisionsPerInterval;
                line.Add((nodes[0].X + (nodes[1].X - nodes[0].X) * t, nodes[0].Y + (nodes[1].Y - nodes[0].Y) * t));
            }
            return line;
        }

        var count = nodes.Count;
        var slopes = new double[count - 1];
        for (var index = 0; index < count - 1; index++)
            slopes[index] = (nodes[index + 1].Y - nodes[index].Y) / (nodes[index + 1].X - nodes[index].X);

        // Tangents: start with the average of the neighbouring slopes, then rein in any tangent
        // that would let the cubic leave the monotone region (Fritsch–Carlson).
        var tangents = new double[count];
        tangents[0] = slopes[0];
        tangents[^1] = slopes[^1];
        for (var index = 1; index < count - 1; index++)
        {
            if (slopes[index - 1] * slopes[index] <= 0) tangents[index] = 0;
            else tangents[index] = (slopes[index - 1] + slopes[index]) / 2;
        }
        for (var index = 0; index < count - 1; index++)
        {
            if (slopes[index] == 0)
            {
                // A flat run must not be given any slope at all, or the spline lifts it off.
                tangents[index] = 0;
                tangents[index + 1] = 0;
                continue;
            }
            var alpha = tangents[index] / slopes[index];
            var beta = tangents[index + 1] / slopes[index];
            if (alpha < 0) { tangents[index] = 0; alpha = 0; }
            if (beta < 0) { tangents[index + 1] = 0; beta = 0; }
            var magnitude = alpha * alpha + beta * beta;
            if (magnitude > 9)
            {
                var scale = 3 / Math.Sqrt(magnitude);
                tangents[index] = scale * alpha * slopes[index];
                tangents[index + 1] = scale * beta * slopes[index];
            }
        }

        var smooth = new List<(double X, double Y)>();
        for (var index = 0; index < count - 1; index++)
        {
            var width = nodes[index + 1].X - nodes[index].X;
            var limit = index == count - 2 ? subdivisionsPerInterval : subdivisionsPerInterval - 1;
            for (var step = 0; step <= limit; step++)
            {
                var t = (double)step / subdivisionsPerInterval;
                var t2 = t * t; var t3 = t2 * t;
                var y = (2 * t3 - 3 * t2 + 1) * nodes[index].Y
                    + (t3 - 2 * t2 + t) * width * tangents[index]
                    + (-2 * t3 + 3 * t2) * nodes[index + 1].Y
                    + (t3 - t2) * width * tangents[index + 1];
                // Clamp to the node interval: the tangent limiting above is exact in theory, but a
                // clamp guarantees the no-overshoot promise for the drawn line regardless of rounding.
                var low = Math.Min(nodes[index].Y, nodes[index + 1].Y);
                var high = Math.Max(nodes[index].Y, nodes[index + 1].Y);
                smooth.Add((nodes[index].X + width * t, Math.Clamp(y, low, high)));
            }
        }
        smooth.Add((nodes[^1].X, nodes[^1].Y));
        return smooth;
    }
}
