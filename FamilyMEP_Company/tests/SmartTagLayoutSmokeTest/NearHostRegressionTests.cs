using FamilyMEP.Plugin.SmartTag;

internal static class NearHostRegressionTests
{
    public static void Run()
    {
        var settings = new SmartTagLayoutSettings(true, 3, 0.5, 0.2, 0.2, 0.05,
            true, true, true, true, true, true)
        { LayoutStyle = SmartTagLayoutStyle.StandardNearHost };
        var frame = new LayoutRect(0, 0, 30, 12);
        LayoutTagInput Host(long key, double u, double v, double width = 1.5) => new(
            key, key + 100, "LABEL", key % 2 == 0 ? "Terminal" : "Accessory",
            new(u, v), new(u, v), width, 0.5, new(u - 0.2, v - 0.15, u + 0.2, v + 0.15));
        TagLayoutPlacement Seed(LayoutTagInput host, double x) => new(host.TagKey,
            new(x + host.TagWidth / 2, host.Anchor.V), host.Anchor, host.Anchor,
            new(x, host.Anchor.V - 0.25, x + host.TagWidth, host.Anchor.V + 0.25),
            false, false, false, host.Label, host.Group);
        var hosts = new[] { Host(71001, 10, 6), Host(71002, 10, 5),
            Host(71003, 20, 6), Host(71004, 20, 5) };
        var old = hosts.Select(h => Seed(h, h.Anchor.U == 10 ? 25 : 2)).ToArray();
        var obstacles = hosts.Select(h => new LayoutObstacle(h.ElementKey, h.ElementBounds)).ToArray();
        var baseline = new SmartTagLayoutResult(old, 0, 0, frame);
        var diagnostic = SmartTagNearHostReplay.Capture(hosts, obstacles, frame, settings, [], baseline, baseline);
        var diagnosticJson = System.Text.Json.JsonSerializer.Serialize(diagnostic, SmartTagNearHostReplay.JsonOptions);
        var diagnosticCopy = System.Text.Json.JsonSerializer.Deserialize<SmartTagNearHostReplay>(diagnosticJson)!;
        Check(diagnosticCopy.Inputs.All(h => h.Label.Length == 0) &&
            diagnosticCopy.Inputs.Select(h => h.ElementBounds).SequenceEqual(hosts.Select(h => h.ElementBounds)) &&
            diagnosticCopy.Standard.Placements.Select(p => p.Head).SequenceEqual(old.Select(p => p.Head)),
            "Local replay must preserve actual geometry without saving the tag's label text.");
        var result = SmartTagStandardNearHostLayout.ComputeClustered(hosts, obstacles, frame,
            settings, true, standardBaseline: baseline);
        Check(result.Placements.All(p => Math.Abs(p.Head.U - p.End.U) < 4),
            "Opposing obsolete leaders must not freeze both host-local groups.");
        Check(SmartTagStandardNearHostLayout.CountHardClashes(result.Placements, [], obstacles, frame, settings.Clearance) == 0,
            "Simultaneous relocation must leave the complete result clear.");
        Check(result.Placements.Select(p => p.TagKey).Order().SequenceEqual(hosts.Select(h => h.TagKey).Order()),
            "Simultaneous relocation must preserve every host/tag identity.");

        var mixed = new[] { Host(72001, 10, 6, 1.5), Host(72002, 10, 5, 2.0), Host(72003, 10, 4, 1.0) };
        var fixedRail = new[] { Seed(Host(72901, 10, 7.2), 7), Seed(Host(72902, 10, 2.8), 7) };
        var mixedObstacles = mixed.Select(h => new LayoutObstacle(h.ElementKey, h.ElementBounds)).ToArray();
        var aligned = SmartTagStandardNearHostLayout.ComputeClustered(mixed, mixedObstacles, frame,
            settings, true, fixedRail,
            standardBaseline: new(mixed.Select(h => Seed(h, 25)).ToArray(), 0, 0, frame));
        Check(aligned.Placements.All(p => p.Head.U < p.End.U && Math.Abs(p.TagBounds.MinU - 7) < 1e-8),
            "Mixed categories/widths must join the nearby LEFT text edge, not align their right edges or retain the remote right rail.");
        Check(SmartTagStandardNearHostLayout.CountHardClashes(aligned.Placements, fixedRail, mixedObstacles, frame, settings.Clearance) == 0,
            "Joining a nearby reference column must clear its existing text and leaders.");

        var rightRail = new[] { Seed(Host(72901, 10, 7.2), 11), Seed(Host(72902, 10, 2.8), 11) };
        var rightAligned = SmartTagStandardNearHostLayout.ComputeClustered(mixed, mixedObstacles, frame,
            settings, true, rightRail,
            standardBaseline: new(mixed.Select(h => Seed(h, 1)).ToArray(), 0, 0, frame));
        Check(rightAligned.Placements.All(p => p.Head.U > p.End.U && Math.Abs(p.TagBounds.MinU - 11) < 1e-8),
            "Side selection must prefer a nearby RIGHT cluster too, not hardcode the user's left-hand example.");
        Check(SmartTagStandardNearHostLayout.CountHardClashes(rightAligned.Placements, rightRail, mixedObstacles, frame, settings.Clearance) == 0,
            "The mirrored right cluster must remain clear.");

        var offsetHosts = mixed.Select((h, i) => h with { TextOffsetU = 0.2 + i * 0.1, TextOffsetV = 0.1 }).ToArray();
        var offsetBaseline = offsetHosts.Select(h =>
        {
            var seed = Seed(h, 25);
            return seed with { Head = new(seed.Head.U - h.TextOffsetU, seed.Head.V - h.TextOffsetV),
                Elbow = new(seed.End.U, seed.Head.V - h.TextOffsetV), UsesElbow = true };
        }).ToArray();
        var offsetResult = SmartTagStandardNearHostLayout.ComputeClustered(offsetHosts, mixedObstacles, frame,
            settings, true, fixedRail, standardBaseline: new(offsetBaseline, 0, 0, frame));
        foreach (var placement in offsetResult.Placements)
        {
            var host = offsetHosts.Single(h => h.TagKey == placement.TagKey);
            Check(Math.Abs(placement.TagBounds.MinU - 7) < 1e-8 &&
                Math.Abs((placement.TagBounds.MinU + placement.TagBounds.MaxU) / 2 - placement.Head.U - host.TextOffsetU) < 1e-8 &&
                Math.Abs((placement.TagBounds.MinV + placement.TagBounds.MaxV) / 2 - placement.Head.V - host.TextOffsetV) < 1e-8,
                "Real-family insertion offsets must preserve the solved visible text edge and head coordinates.");
        }
        Check(SmartTagStandardNearHostLayout.CountHardClashes(offsetResult.Placements, fixedRail, mixedObstacles, frame, settings.Clearance) == 0,
            "Offset-aware real-family routing must remain clear.");

        // A duct bank can fill the ideal near radius. The nearby established
        // column just outside it is still much closer than the old far side.
        var bank = mixedObstacles.Concat([new LayoutObstacle(73999, new(7, 0, 13, 12))]).ToArray();
        var bankRail = new[] { Seed(Host(73901, 10, 7.2), 4.9), Seed(Host(73902, 10, 2.8), 4.9) };
        var bankResult = SmartTagStandardNearHostLayout.ComputeClustered(mixed, bank, frame,
            settings, true, bankRail, standardBaseline: new(mixed.Select(h => Seed(h, 25)).ToArray(), 0, 0, frame));
        Check(bankResult.Placements.All(p => Math.Abs(p.TagBounds.MinU - 4.9) < 1e-8),
            "The ideal near radius must not reject a clean established column beyond a duct bank when it is much closer than Standard.");
        Check(SmartTagStandardNearHostLayout.CountHardClashes(bankResult.Placements, bankRail, bank, frame, settings.Clearance) == 0,
            "A column outside the ideal radius must still pass full clash validation.");

        var closeRows = new[] { Host(74001, 10, 6), Host(74002, 10, 5.6), Host(74003, 10, 5.2) };
        var clearanceSettings = settings with { RowSpacing = 0.05, Clearance = 0.1 };
        var clearanceObstacles = closeRows.Select(h => new LayoutObstacle(h.ElementKey, h.ElementBounds)).ToArray();
        var clearanceResult = SmartTagStandardNearHostLayout.Compute(closeRows, clearanceObstacles, frame, clearanceSettings);
        Check(SmartTagStandardNearHostLayout.CountHardClashes(clearanceResult.Placements, [], clearanceObstacles, frame, clearanceSettings.Clearance) == 0,
            "Tag Gap smaller than Clearance must still produce a valid multi-tag column, not rows rejected by the final validator.");
        var denseView = Enumerable.Range(0, 138).Select(i => Host(75000 + i,
            10 + (i / 3 % 8) * 15, 5 + (i / 24) * 8 + (i % 3) * 0.4)).ToArray();
        var denseFrame = new LayoutRect(0, 0, 130, 60);
        var denseObstacles = denseView.Select(h => new LayoutObstacle(h.ElementKey, h.ElementBounds)).ToArray();
        var denseSeed = new SmartTagLayoutResult(denseView.Select(h => Seed(h, h.Anchor.U + 9)).ToArray(), 0, 0, denseFrame);
        var denseResult = SmartTagStandardNearHostLayout.ComputeClustered(denseView, denseObstacles, denseFrame,
            clearanceSettings, true, standardBaseline: denseSeed);
        Check(denseResult.Placements.Count == 138 &&
            SmartTagStandardNearHostLayout.CountHardClashes(denseResult.Placements, [], denseObstacles, denseFrame, clearanceSettings.Clearance) == 0,
            "A full 138-tag view with Tag Gap < Clearance must retain all tags and form clear local groups.");
        foreach (var group in denseResult.Placements.GroupBy(p => (p.TagKey - 75000) / 3))
            Check(group.Select(p => Math.Round(p.TagBounds.MinU, 6)).Distinct().Count() == 1,
                "Dense full-view tags must stay on group columns, not fragment into unrelated single-tag rails.");

        var blockedObstacles = obstacles.Concat([new LayoutObstacle(79999, new(17, 0, 24, 12))]).ToArray();
        var partial = SmartTagStandardNearHostLayout.ComputeClustered(hosts, blockedObstacles, frame,
            settings, true, standardBaseline: baseline);
        Check(partial.Placements.Count == old.Length, "Unplaceable tags must remain in the complete result.");
        foreach (var moved in partial.Placements.Where(p => p.Head != old.Single(o => o.TagKey == p.TagKey).Head))
            Check(SmartTagStandardNearHostLayout.CountHardClashes([moved],
                partial.Placements.Where(p => p.TagKey != moved.TagKey).ToArray(), blockedObstacles, frame, settings.Clearance) == 0,
                "Restoring an unsolved tag must not leave a new move crossing its restored leader.");
        var limited = SmartTagStandardNearHostLayout.ComputeClustered(hosts, obstacles, frame,
            settings, true, searchCheckLimit: 1, standardBaseline: baseline);
        Check(limited.Placements.Select(p => p with { HasClash = false }).SequenceEqual(old),
            "An exhausted search must retain every Standard identity and coordinate (clash flags may be refreshed).");
        Console.WriteLine("PASS: local groups, both-side alignment, real offsets, duct-bank pockets, 138 dense tags with Gap < Clearance, and safe incomplete repair.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
