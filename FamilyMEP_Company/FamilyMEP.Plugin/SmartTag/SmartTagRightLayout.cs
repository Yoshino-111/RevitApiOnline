namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Entry point for the dedicated all-right layout mode.
/// Keep one-sided changes here so the accepted Auto left/right solver remains untouched.
/// </summary>
internal static class SmartTagRightLayout
{
    public const string AlignmentDescription = "one exact right rail";

    public static SmartTagLayoutSettings PrepareSettings(SmartTagLayoutSettings settings) =>
        settings with
        {
            PlaceLeft = false,
            AutoSide = false
        };

    public static SmartTagLayoutResult Compute(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement>? initialReservations = null)
    {
        SmartTagLayoutSettings prepared = PrepareSettings(settings);
        List<LayoutTagInput> established = source
            .Where(item => !item.PreferLocalClustering)
            .ToList();
        List<LayoutTagInput> followers = source
            .Where(item => item.PreferLocalClustering)
            .ToList();
        // Mirrored all-right pass: one ordered rail and one global set of text,
        // model and leader reservations for all selected categories.
        SmartTagLayoutResult baseline = SmartTagLayoutEngine.Compute(
            established,
            obstacles,
            frame,
            prepared,
            initialReservations);
        if (followers.Count == 0) return baseline;

        List<TagLayoutPlacement> followerReservations = (initialReservations ?? [])
            .Concat(baseline.Placements)
            .ToList();

        SmartTagLayoutResult duct = SmartTagLayoutEngine.ComputeClustered(
            followers,
            obstacles,
            frame,
            prepared,
            autoSide: false,
            initialReservations: followerReservations);
        return Combine(baseline, duct, frame);
    }

    private static SmartTagLayoutResult Combine(
        SmartTagLayoutResult established,
        SmartTagLayoutResult followers,
        LayoutRect frame)
    {
        List<TagLayoutPlacement> placements = established.Placements
            .Concat(followers.Placements)
            .ToList();
        LayoutRect bounds = placements.Count == 0
            ? frame
            : new LayoutRect(
                placements.Min(item => item.TagBounds.MinU),
                placements.Min(item => item.TagBounds.MinV),
                placements.Max(item => item.TagBounds.MaxU),
                placements.Max(item => item.TagBounds.MaxV));
        return new SmartTagLayoutResult(
            placements,
            placements.Count(item => item.UsesElbow),
            placements.Count(item => item.HasClash),
            bounds);
    }
}
