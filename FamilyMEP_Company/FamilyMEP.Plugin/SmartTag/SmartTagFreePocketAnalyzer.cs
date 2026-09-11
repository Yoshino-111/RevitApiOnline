namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Describes one vertical annotation pocket beside a local MEP host group.
/// Capacity is conservative: it uses the largest selected project Tag Family
/// height and the configured paper-space gap, so a reported slot is a slot the
/// real tag text can occupy rather than an arbitrary empty rectangle.
/// </summary>
internal sealed record SmartTagFreePocket(
    bool PlaceLeft,
    LayoutRect TextBand,
    int Capacity,
    double DistanceFromHosts,
    int RailLevel);

/// <summary>
/// Builds an explicit free-space map for Adaptive Groups. Model/architecture
/// obstacles and already accepted tag text reserve vertical intervals on the
/// candidate text rail. Leader crossings are intentionally checked by the
/// Standard solver afterwards; this class answers the earlier question:
/// "how many real tags fit in this clear pocket?"
/// </summary>
internal static class SmartTagFreePocketAnalyzer
{
    public static IReadOnlyList<SmartTagFreePocket> Analyze(
        IReadOnlyList<LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        IReadOnlyList<TagLayoutPlacement> reservations,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool placeLeft,
        double? canonicalColumnLeft = null)
    {
        if (inputs.Count == 0) return [];

        double maximumWidth = inputs.Max(item => Math.Max(item.TagWidth, 0.01));
        double maximumHeight = inputs.Max(item => Math.Max(item.TagHeight, 0.01));
        double hostEdge = placeLeft
            ? inputs.Min(item => item.ElementBounds.MinU)
            : inputs.Max(item => item.ElementBounds.MaxU);
        double computedColumnLeft = placeLeft
            ? hostEdge - settings.OffsetFromElements - maximumWidth
            : hostEdge + settings.OffsetFromElements;
        // Every subgroup cut from the same nearby-host component must start
        // from one shared rail.  Without this, each subgroup recomputes its
        // own host edge and the finished view contains many almost-aligned
        // text columns with no visible order.
        double nearestColumnLeft = canonicalColumnLeft ?? computedColumnLeft;
        double clearance = Math.Max(settings.Clearance, 0.0);
        double usableBottom = frame.MinV + settings.TopMargin;
        double usableTop = frame.MaxV - settings.TopMargin;
        if (usableTop - usableBottom < maximumHeight - 1e-9) return [];
        double spacing = Math.Max(settings.RowSpacing, 0.0);
        double rowPitch = maximumHeight + spacing;
        // A blocked near rail must not make the group fail immediately. Probe
        // a small set of outward rails whose pitch guarantees that two real
        // text rectangles cannot touch. The nearest valid rail still wins.
        double railPitch = Math.Max(
            maximumWidth + clearance * 4.0,
            Math.Max(settings.OffsetFromElements * 0.75, 0.01));
        double anchorCenter = inputs.Average(item => item.Anchor.V);
        var pockets = new List<SmartTagFreePocket>();
        const int maximumRailLevels = 6;
        for (int railLevel = 0; railLevel < maximumRailLevels; railLevel++)
        {
            double columnLeft = nearestColumnLeft +
                                (placeLeft ? -1.0 : 1.0) * railLevel * railPitch;
            if (columnLeft < frame.MinU + settings.TopMargin - 1e-9 ||
                columnLeft + maximumWidth > frame.MaxU - settings.TopMargin + 1e-9)
            {
                break;
            }

            var textBand = new LayoutRect(
                columnLeft,
                usableBottom,
                columnLeft + maximumWidth,
                usableTop);
            LayoutRect collisionBand = textBand.Expand(clearance);
            var blocked = new List<(double Min, double Max)>();
            if (settings.AvoidElements)
            {
                foreach (LayoutObstacle obstacle in obstacles)
                {
                    LayoutRect bounds = obstacle.Bounds.Expand(clearance);
                    if (OverlapsHorizontally(collisionBand, bounds))
                    {
                        AddClipped(blocked, bounds.MinV, bounds.MaxV, usableBottom, usableTop);
                    }
                }
            }
            if (settings.AvoidTagText)
            {
                foreach (TagLayoutPlacement placement in reservations)
                {
                    LayoutRect bounds = placement.TagBounds.Expand(clearance);
                    if (OverlapsHorizontally(collisionBand, bounds))
                    {
                        AddClipped(blocked, bounds.MinV, bounds.MaxV, usableBottom, usableTop);
                    }
                }
            }

            List<(double Min, double Max)> merged = Merge(blocked);
            var free = new List<(double Min, double Max)>();
            double cursor = usableBottom;
            foreach ((double minimum, double maximum) in merged)
            {
                if (minimum > cursor + 1e-9) free.Add((cursor, minimum));
                cursor = Math.Max(cursor, maximum);
            }
            if (cursor < usableTop - 1e-9) free.Add((cursor, usableTop));

            foreach ((double minimum, double maximum) in free)
            {
                double height = maximum - minimum;
                int capacity = Math.Max(0, (int)Math.Floor(
                    (height + spacing + 1e-9) / Math.Max(rowPitch, 0.01)));
                if (capacity == 0) continue;
                double center = (minimum + maximum) * 0.5;
                pockets.Add(new SmartTagFreePocket(
                    placeLeft,
                    new LayoutRect(columnLeft, minimum, columnLeft + maximumWidth, maximum),
                    capacity,
                    Math.Abs(center - anchorCenter) + railLevel * railPitch,
                    railLevel));
            }
        }

        return pockets
            .Where(item => item.Capacity > 0)
            .OrderBy(item => item.DistanceFromHosts)
            .ThenBy(item => item.RailLevel)
            .ThenByDescending(item => item.Capacity)
            .ThenByDescending(item => item.TextBand.MaxV)
            .ToList();
    }

    public static LayoutRect CreateSolverFrame(
        SmartTagFreePocket pocket,
        LayoutRect originalFrame,
        SmartTagLayoutSettings settings) =>
        new(
            originalFrame.MinU,
            Math.Max(originalFrame.MinV, pocket.TextBand.MinV - settings.TopMargin),
            originalFrame.MaxU,
            Math.Min(originalFrame.MaxV, pocket.TextBand.MaxV + settings.TopMargin));

    private static bool OverlapsHorizontally(LayoutRect first, LayoutRect second) =>
        first.MinU < second.MaxU - 1e-9 && first.MaxU > second.MinU + 1e-9;

    private static void AddClipped(
        ICollection<(double Min, double Max)> target,
        double minimum,
        double maximum,
        double lower,
        double upper)
    {
        minimum = Math.Max(minimum, lower);
        maximum = Math.Min(maximum, upper);
        if (maximum > minimum + 1e-9) target.Add((minimum, maximum));
    }

    private static List<(double Min, double Max)> Merge(
        IEnumerable<(double Min, double Max)> source)
    {
        List<(double Min, double Max)> ordered = source
            .OrderBy(item => item.Min)
            .ThenBy(item => item.Max)
            .ToList();
        var result = new List<(double Min, double Max)>();
        foreach ((double minimum, double maximum) in ordered)
        {
            if (result.Count == 0 || minimum > result[^1].Max + 1e-9)
            {
                result.Add((minimum, maximum));
                continue;
            }
            (double previousMinimum, double previousMaximum) = result[^1];
            result[^1] = (previousMinimum, Math.Max(previousMaximum, maximum));
        }
        return result;
    }
}
