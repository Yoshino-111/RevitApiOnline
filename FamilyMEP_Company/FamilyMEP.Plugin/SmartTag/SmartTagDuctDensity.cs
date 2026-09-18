namespace FamilyMEP.Plugin.SmartTag;

internal sealed record DuctDensityCandidate(
    long Key,
    LayoutPoint Anchor,
    bool Existing,
    long NearestCompanionKey,
    double CompanionDistance)
{
    public double HostSpan { get; init; }
}

internal static class SmartTagDuctDensity
{
    internal const int MaximumTagsPerZone = 10;

    internal static ISet<long> Select(
        IReadOnlyList<DuctDensityCandidate> source,
        LayoutRect sampleZone,
        int maximumPerZone = MaximumTagsPerZone)
    {
        var selected = new HashSet<long>();
        if (source.Count == 0 || maximumPerZone <= 0 ||
            sampleZone.Width <= 1e-8 || sampleZone.Height <= 1e-8)
        {
            return selected;
        }

        foreach (IGrouping<(long Column, long Row), DuctDensityCandidate> zone in source
                     .GroupBy(item => Cell(item.Anchor, sampleZone))
                     .OrderBy(group => group.Key.Row)
                     .ThenBy(group => group.Key.Column))
        {
            List<DuctDensityCandidate> candidates = zone.ToList();
            var local = new List<DuctDensityCandidate>(maximumPerZone);

            // Existing Duct tags consume capacity first. They are never
            // duplicated merely because the density sample was changed.
            AddUntilFull(candidates
                .Where(item => item.Existing)
                .OrderByDescending(item => item.HostSpan)
                .ThenBy(item => item.CompanionDistance)
                .ThenBy(item => item.Key));

            // Prefer one Duct near each established Air Terminal / Duct
            // Accessory host before filling spare slots. This distributes the
            // sparse Duct labels across the already readable local clusters.
            AddUntilFull(candidates
                .Where(item => !item.Existing && item.NearestCompanionKey != 0)
                .GroupBy(item => item.NearestCompanionKey)
                .Select(group => group
                    .OrderByDescending(item => item.HostSpan)
                    .ThenBy(item => item.CompanionDistance)
                    .ThenBy(item => item.Key)
                    .First())
                .OrderByDescending(item => item.HostSpan)
                .ThenBy(item => item.CompanionDistance)
                .ThenBy(item => item.Key));

            LayoutPoint centre = CellCentre(zone.Key, sampleZone);
            AddUntilFull(candidates
                .Where(item => !local.Any(chosen => chosen.Key == item.Key))
                .OrderByDescending(item => item.HostSpan)
                .ThenBy(item => item.CompanionDistance)
                .ThenBy(item => SquaredDistance(item.Anchor, centre))
                .ThenBy(item => item.Key));

            foreach (DuctDensityCandidate item in local)
                selected.Add(item.Key);

            void AddUntilFull(IEnumerable<DuctDensityCandidate> ordered)
            {
                foreach (DuctDensityCandidate item in ordered)
                {
                    if (local.Count >= maximumPerZone) break;
                    if (local.All(chosen => chosen.Key != item.Key)) local.Add(item);
                }
            }
        }
        return selected;
    }

    private static (long Column, long Row) Cell(LayoutPoint point, LayoutRect sampleZone) =>
        (
            (long)Math.Floor((point.U - sampleZone.MinU) / sampleZone.Width),
            (long)Math.Floor((point.V - sampleZone.MinV) / sampleZone.Height)
        );

    private static LayoutPoint CellCentre(
        (long Column, long Row) cell,
        LayoutRect sampleZone) =>
        new(
            sampleZone.MinU + (cell.Column + 0.5) * sampleZone.Width,
            sampleZone.MinV + (cell.Row + 0.5) * sampleZone.Height);

    private static double SquaredDistance(LayoutPoint first, LayoutPoint second)
    {
        double du = first.U - second.U;
        double dv = first.V - second.V;
        return du * du + dv * dv;
    }
}
