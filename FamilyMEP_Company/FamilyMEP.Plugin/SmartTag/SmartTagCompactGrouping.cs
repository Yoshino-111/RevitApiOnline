namespace FamilyMEP.Plugin.SmartTag;

internal sealed record CompactTagGroupingInput(
    long TagKey,
    LayoutPoint Anchor,
    LayoutRect ElementBounds,
    double TagWidth,
    double TagHeight);

/// <summary>
/// Builds deterministic proximity components from nearby host geometry.
/// Component size is intentionally not capped here: the adaptive layout pass
/// decides where a component must be split from the real free space and model
/// obstacles. This helper remains independent from Revit so preview and the
/// actual project-family pass start from the same nearby-host components.
/// </summary>
internal static class SmartTagCompactGrouping
{
    public static Dictionary<long, int> BuildGroupMap(
        IReadOnlyList<CompactTagGroupingInput> source,
        SmartTagLayoutSettings settings)
    {
        var result = new Dictionary<long, int>(source.Count);
        int groupId = 0;
        foreach (IReadOnlyList<CompactTagGroupingInput> component in
                 BuildComponents(source, settings))
        {
            foreach (CompactTagGroupingInput item in component)
            {
                result[item.TagKey] = groupId;
            }
            groupId++;
        }
        return result;
    }

    public static IReadOnlyList<IReadOnlyList<CompactTagGroupingInput>> BuildComponents(
        IReadOnlyList<CompactTagGroupingInput> source,
        SmartTagLayoutSettings settings)
    {
        if (source.Count == 0) return [];

        double medianWidth = Median(source.Select(item => Math.Max(item.TagWidth, 0.005)));
        double medianHeight = Median(source.Select(item => Math.Max(item.TagHeight, 0.005)));
        double horizontalGapLimit = Math.Max(
            medianWidth * 2.5,
            settings.ColumnWidth * 0.20);
        double verticalGapLimit = Math.Max(
            medianHeight * 4.0 + settings.RowSpacing * 3.0,
            settings.ColumnWidth * 0.22);
        double maximumHorizontalSpan = Math.Max(
            medianWidth * 7.0,
            settings.ColumnWidth * 0.60);
        double maximumVerticalSpan = Math.Max(
            medianHeight * 12.0 + settings.RowSpacing * 11.0,
            settings.ColumnWidth * 0.75);

        List<CompactTagGroupingInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();
        int[] parents = Enumerable.Range(0, ordered.Count).ToArray();
        double[] minimumU = ordered.Select(item => item.Anchor.U).ToArray();
        double[] maximumU = minimumU.ToArray();
        double[] minimumV = ordered.Select(item => item.Anchor.V).ToArray();
        double[] maximumV = minimumV.ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot) return;

            double mergedMinimumU = Math.Min(minimumU[firstRoot], minimumU[secondRoot]);
            double mergedMaximumU = Math.Max(maximumU[firstRoot], maximumU[secondRoot]);
            double mergedMinimumV = Math.Min(minimumV[firstRoot], minimumV[secondRoot]);
            double mergedMaximumV = Math.Max(maximumV[firstRoot], maximumV[secondRoot]);
            if (mergedMaximumU - mergedMinimumU > maximumHorizontalSpan ||
                mergedMaximumV - mergedMinimumV > maximumVerticalSpan)
            {
                return;
            }

            parents[secondRoot] = firstRoot;
            minimumU[firstRoot] = mergedMinimumU;
            maximumU[firstRoot] = mergedMaximumU;
            minimumV[firstRoot] = mergedMinimumV;
            maximumV[firstRoot] = mergedMaximumV;
        }

        for (int first = 0; first < ordered.Count; first++)
        {
            LayoutRect firstHost = ordered[first].ElementBounds;
            for (int second = first + 1; second < ordered.Count; second++)
            {
                LayoutRect secondHost = ordered[second].ElementBounds;
                double horizontalGap = AxisGap(
                    firstHost.MinU,
                    firstHost.MaxU,
                    secondHost.MinU,
                    secondHost.MaxU);
                double verticalGap = AxisGap(
                    firstHost.MinV,
                    firstHost.MaxV,
                    secondHost.MinV,
                    secondHost.MaxV);
                if (horizontalGap <= horizontalGapLimit &&
                    verticalGap <= verticalGapLimit)
                {
                    Union(first, second);
                }
            }
        }

        return Enumerable.Range(0, ordered.Count)
            .GroupBy(Find)
            .Select(component => component
                .Select(index => ordered[index])
                .OrderByDescending(item => item.Anchor.V)
                .ThenBy(item => item.Anchor.U)
                .ThenBy(item => item.TagKey)
                .ToList())
            .OrderByDescending(component => component.Max(item => item.Anchor.V))
            .ThenBy(component => component.Min(item => item.Anchor.U))
            .ThenBy(component => component.Min(item => item.TagKey))
            .Cast<IReadOnlyList<CompactTagGroupingInput>>()
            .ToList();
    }

    public static Dictionary<long, int> BuildGroupMap(
        IReadOnlyList<LayoutTagInput> source,
        SmartTagLayoutSettings settings) =>
        BuildGroupMap(
            source.Select(item => new CompactTagGroupingInput(
                    item.TagKey,
                    item.Anchor,
                    item.ElementBounds,
                    item.TagWidth,
                    item.TagHeight))
                .ToList(),
            settings);

    private static double AxisGap(
        double firstMinimum,
        double firstMaximum,
        double secondMinimum,
        double secondMaximum)
    {
        if (firstMaximum < secondMinimum) return secondMinimum - firstMaximum;
        if (secondMaximum < firstMinimum) return firstMinimum - secondMaximum;
        return 0.0;
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> ordered = values.OrderBy(value => value).ToList();
        if (ordered.Count == 0) return 0.0;
        int middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }
}
