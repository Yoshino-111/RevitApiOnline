namespace FamilyMEP.Plugin.SmartTag;

internal static class SmartTagManualAvoidance
{
    internal static double? FindNearestVerticalShift(
        LayoutRect moving,
        IReadOnlyList<LayoutRect> obstacles,
        IReadOnlyList<LayoutRect> reservedTags,
        double clearance)
    {
        double safeClearance = Math.Max(0.0, clearance);
        LayoutRect[] blocked = obstacles
            .Concat(reservedTags)
            .Select(item => item.Expand(safeClearance))
            .Where(item => moving.MaxU > item.MinU + 1e-9 &&
                           moving.MinU < item.MaxU - 1e-9)
            .ToArray();
        if (blocked.Length == 0 || IsClear(0.0)) return 0.0;

        double[] candidates = blocked
            .SelectMany(item => new[]
            {
                item.MaxV - moving.MinV,
                item.MinV - moving.MaxV
            })
            .Where(value => Math.Abs(value) > 1e-9)
            .DistinctBy(value => Math.Round(value, 9))
            .OrderBy(Math.Abs)
            // If up/down are equally short, prefer up so the result is stable.
            .ThenByDescending(value => value)
            .ToArray();
        foreach (double shift in candidates)
        {
            if (IsClear(shift)) return shift;
        }
        return null;

        bool IsClear(double shift)
        {
            LayoutRect shifted = Shift(moving, shift);
            return blocked.All(item => !shifted.Intersects(item));
        }
    }

    internal static LayoutRect Shift(LayoutRect bounds, double verticalShift) => new(
        bounds.MinU,
        bounds.MinV + verticalShift,
        bounds.MaxU,
        bounds.MaxV + verticalShift);
}
