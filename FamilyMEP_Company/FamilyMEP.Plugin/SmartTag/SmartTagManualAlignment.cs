namespace FamilyMEP.Plugin.SmartTag;

internal static class SmartTagManualAlignment
{
    public static IReadOnlyDictionary<long, double> ComputeHorizontalShifts(
        IReadOnlyDictionary<long, LayoutRect> tagBounds,
        double referenceEdge,
        bool alignLeftEdge) => tagBounds.ToDictionary(
            item => item.Key,
            item => alignLeftEdge
                ? referenceEdge - item.Value.MinU
                : referenceEdge - item.Value.MaxU);

    public static IReadOnlyDictionary<long, double> ComputeHorizontalShifts(
        IReadOnlyDictionary<long, LayoutRect> tagBounds,
        bool alignLeftEdge)
    {
        if (tagBounds.Count == 0)
        {
            return new Dictionary<long, double>();
        }

        double targetEdge = alignLeftEdge
            ? tagBounds.Values.Min(bounds => bounds.MinU)
            : tagBounds.Values.Max(bounds => bounds.MaxU);
        return ComputeHorizontalShifts(tagBounds, targetEdge, alignLeftEdge);
    }
}
