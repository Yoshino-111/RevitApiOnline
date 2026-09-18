namespace FamilyMEP.Plugin.SmartTag;

internal static class SmartTagMepClearance
{
    public static double FindNearestHorizontalShift(
        IReadOnlyList<LayoutRect> groupBounds,
        IReadOnlyList<double> hostAnchorUs,
        bool rightSide,
        IReadOnlyList<LayoutRect> otherTagBounds,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        double clearance,
        double offsetFromElements,
        double columnWidth)
    {
        if (groupBounds.Count == 0 || groupBounds.Count != hostAnchorUs.Count)
        {
            return 0.0;
        }

        double maximumWidth = groupBounds.Max(bounds => Math.Max(bounds.Width, 0.005));
        double step = Math.Max(
            Math.Max(clearance, offsetFromElements * 0.5),
            maximumWidth * 0.15);
        double maximumTravel = Math.Max(
            maximumWidth * 3.0,
            Math.Min(columnWidth * 0.55, maximumWidth * 8.0));
        int maximumLevel = PortableMath.Clamp(
            (int)Math.Ceiling(maximumTravel / Math.Max(step, 1e-8)),
            1,
            24);

        (int Model, int Tags, double Travel) best =
            (int.MaxValue, int.MaxValue, double.MaxValue);
        double bestShift = 0.0;
        for (int level = 0; level <= maximumLevel; level++)
        {
            IEnumerable<double> candidates = level == 0
                ? [0.0]
                : rightSide
                    ? [level * step, -level * step]
                    : [-level * step, level * step];
            foreach (double shift in candidates)
            {
                List<LayoutRect> shifted = groupBounds
                    .Select(bounds => new LayoutRect(
                        bounds.MinU + shift,
                        bounds.MinV,
                        bounds.MaxU + shift,
                        bounds.MaxV))
                    .ToList();
                if (shifted.Any(bounds =>
                        bounds.MinU < frame.MinU + clearance ||
                        bounds.MaxU > frame.MaxU - clearance))
                {
                    continue;
                }
                bool crossedHostSide = shifted
                    .Select((bounds, index) => (bounds, anchorU: hostAnchorUs[index]))
                    .Any(item => rightSide
                        ? (item.bounds.MinU + item.bounds.MaxU) * 0.5 < item.anchorU
                        : (item.bounds.MinU + item.bounds.MaxU) * 0.5 > item.anchorU);
                if (crossedHostSide) continue;

                int modelConflicts = shifted.Sum(bounds => obstacles.Count(obstacle =>
                    obstacle.Kind == LayoutObstacleKind.Mep &&
                    bounds.Intersects(obstacle.Bounds.Expand(clearance), 0.0)));
                int tagConflicts = shifted.Sum(bounds => otherTagBounds.Count(other =>
                    bounds.Intersects(other.Expand(clearance), 0.0)));
                var quality = (Model: modelConflicts, Tags: tagConflicts, Travel: Math.Abs(shift));
                if (quality.Model < best.Model ||
                    quality.Model == best.Model && quality.Tags < best.Tags ||
                    quality.Model == best.Model && quality.Tags == best.Tags &&
                    quality.Travel < best.Travel - 1e-10)
                {
                    best = quality;
                    bestShift = shift;
                }
                if (best.Model == 0 && best.Tags == 0)
                {
                    return bestShift;
                }
            }
        }
        return bestShift;
    }
}
