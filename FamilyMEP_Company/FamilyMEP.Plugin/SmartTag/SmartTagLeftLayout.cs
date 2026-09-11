namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Entry point for the dedicated all-left layout mode.
/// Keep one-sided changes here so the accepted Auto left/right solver remains untouched.
/// </summary>
internal static class SmartTagLeftLayout
{
    public const string AlignmentDescription = "one exact left rail";

    public static SmartTagLayoutSettings PrepareSettings(SmartTagLayoutSettings settings) =>
        settings with
        {
            PlaceLeft = true,
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
        // Solve every selected category in one shared pass. Earlier one-side
        // behavior solved local clusters separately, so a later cluster could
        // not participate in the same ordered rail. The shared reservation set
        // lets every tag see earlier text boxes and leaders.
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
