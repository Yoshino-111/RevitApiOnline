using FamilyMEP.Plugin.SmartTag;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static SmartTagLayoutSettings Settings(bool avoidElements = false) => new(
    PlaceLeft: true,
    ColumnWidth: 2.0,
    OffsetFromElements: 0.5,
    RowSpacing: 0.2,
    TopMargin: 0.2,
    Clearance: 0.05,
    AvoidElements: avoidElements,
    AvoidLeaders: true,
    AvoidTagText: true,
    LockUpperLeaders: true,
    MoveLowerTagFirst: true,
    AutoSide: false);

static LayoutTagInput Tag(
    long id,
    double anchorV,
    double height = 0.5,
    double anchorU = 7.0,
    double width = 1.5) => new(
    TagKey: id,
    ElementKey: 100 + id,
    Label: $"TAG-{id}",
    Group: "Pipe",
    Anchor: new LayoutPoint(anchorU, anchorV),
    CurrentHead: new LayoutPoint(2.0, anchorV),
    TagWidth: width,
    TagHeight: height,
    ElementBounds: new LayoutRect(anchorU - 0.2, anchorV - 0.15, anchorU + 0.2, anchorV + 0.15));

static bool LeaderEndStaysOnHost(TagLayoutPlacement placement, LayoutTagInput input) =>
    Math.Abs(placement.End.U - input.Anchor.U) < 1e-8 &&
    placement.End.V >= input.ElementBounds.MinV - 1e-8 &&
    placement.End.V <= input.ElementBounds.MaxV + 1e-8 &&
    (placement.UsesFreeEnd || Math.Abs(placement.End.V - input.Anchor.V) < 1e-8);

static double HorizontalHostGap(TagLayoutPlacement placement, LayoutTagInput input) =>
    placement.Head.U < placement.End.U
        ? input.ElementBounds.MinU - placement.TagBounds.MaxU
        : placement.TagBounds.MinU - input.ElementBounds.MaxU;

Require(SmartTagStackRouting.BuildLeftThenRightPasses(
        [true, false, true], autoSide: true).SequenceEqual(
        new bool?[] { false, true }),
    "Two-sided AUTO must solve and lock Left before routing Right.");
Require(SmartTagStackRouting.BuildLeftThenRightPasses(
        [false, false], autoSide: true).SequenceEqual(
        new bool?[] { null }),
    "One-sided AUTO must retain the accepted single-pass routing behavior.");

LayoutPoint mixedAboveEnd = SmartTagStackRouting.OffsetMixedSideEndInsideHost(
    new LayoutPoint(5.0, 5.0),
    new LayoutRect(4.0, 4.5, 6.0, 5.5),
    preferredDirection: -1,
    requestedOffset: 0.2,
    tolerance: 0.01);
Require(mixedAboveEnd.V < 5.0 && mixedAboveEnd.V >= 4.5,
    "Mixed-side AUTO above the sample must offset the endpoint down inside the host to retain a 90-degree leader.");
LayoutPoint mixedBelowEnd = SmartTagStackRouting.OffsetMixedSideEndInsideHost(
    new LayoutPoint(5.0, 5.0),
    new LayoutRect(4.0, 4.5, 6.0, 5.5),
    preferredDirection: 1,
    requestedOffset: 0.2,
    tolerance: 0.01);
Require(mixedBelowEnd.V > 5.0 && mixedBelowEnd.V <= 5.5,
    "Mixed-side AUTO below the sample must offset the endpoint up inside the host to retain a 90-degree leader.");

double? avoidUp = SmartTagManualAvoidance.FindNearestVerticalShift(
    new LayoutRect(0, 0, 4, 1),
    [new LayoutRect(1, -0.25, 3, 1.25)],
    [],
    clearance: 0.1);
Require(avoidUp is not null && Math.Abs(avoidUp.Value - 1.35) <= 1e-9,
    "Element-clash avoidance must choose the nearest clear vertical boundary and prefer up on a tie.");
double? avoidDownAroundReservedTag = SmartTagManualAvoidance.FindNearestVerticalShift(
    new LayoutRect(0, 0, 4, 1),
    [new LayoutRect(1, -0.2, 3, 0.8)],
    [new LayoutRect(0, 0.85, 4, 2.0)],
    clearance: 0.1);
Require(avoidDownAroundReservedTag is not null && avoidDownAroundReservedTag.Value < 0,
    "Element-clash avoidance must consider existing tag reservations when choosing up or down.");
SmartTagFixedRowRoutePlan fixedRowPlan =
    SmartTagStackRouting.FindBestOrderForFixedRows(
        [
            new SmartTagStackRouteInput(
                21,
                new LayoutRect(0, 9.5, 1, 10.5),
                new LayoutPoint(1, 10),
                new LayoutPoint(8, 0)),
            new SmartTagStackRouteInput(
                22,
                new LayoutRect(0, -0.5, 1, 0.5),
                new LayoutPoint(1, 0),
                new LayoutPoint(8, 10))
        ],
        [10.0, 0.0],
        fixedRoutes: [],
        clearance: 0.01);
Require(
    fixedRowPlan.OrderedKeys.SequenceEqual([22L, 21L]) &&
    fixedRowPlan.CrossingCount == 0,
    "Fixed-row leader analysis must reorder inverted host routes before H-ALIGN/AVOID writes them.");

try
{
    string? replayPath = Environment.GetEnvironmentVariable("SMARTTAG_REPLAY");
    if (!string.IsNullOrWhiteSpace(replayPath))
    {
        var replay = System.Text.Json.JsonSerializer.Deserialize<SmartTagNearHostReplay>(File.ReadAllText(replayPath))
            ?? throw new InvalidOperationException("Empty Near Host replay.");
        if (Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_REASSIGN_COMPANIONS") == "1")
        {
            LayoutTagInput[] companionInputs = replay.Inputs
                .Where(item => item.CanAnchorDuctFollowers)
                .ToArray();
            static double ReplayRectDistanceSquared(LayoutRect first, LayoutRect second)
            {
                double du = first.MaxU < second.MinU
                    ? second.MinU - first.MaxU
                    : second.MaxU < first.MinU
                        ? first.MinU - second.MaxU
                        : 0.0;
                double dv = first.MaxV < second.MinV
                    ? second.MinV - first.MaxV
                    : second.MaxV < first.MinV
                        ? first.MinV - second.MaxV
                        : 0.0;
                return du * du + dv * dv;
            }
            replay = replay with
            {
                Inputs = replay.Inputs.Select(input => input.PreferLocalClustering &&
                                                       companionInputs.Length > 0
                    ? input with
                    {
                        PreferredFollowerAnchorTagKey = companionInputs
                            .OrderBy(companion => ReplayRectDistanceSquared(
                                input.ElementBounds, companion.ElementBounds))
                            .ThenBy(companion => companion.TagKey)
                            .First().TagKey
                    }
                    : input).ToArray()
            };
            Dictionary<long, long> preferredByKey = replay.Inputs
                .ToDictionary(item => item.TagKey,
                    item => item.PreferredFollowerAnchorTagKey);
            replay = replay with
            {
                Standard = replay.Standard with
                {
                    Placements = replay.Standard.Placements.Select(placement => placement with
                    {
                        PreferredFollowerAnchorTagKey =
                            preferredByKey.GetValueOrDefault(placement.TagKey)
                    }).ToArray()
                }
            };
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("Standard hard clashes: " + SmartTagStandardNearHostLayout.CountHardClashes(
            replay.Standard.Placements, replay.Reservations, replay.Obstacles,
            replay.Frame, replay.Settings.Clearance));
        bool replayV2 = Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_V2") == "1";
        var replayResult = Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_CAPTURED") == "1"
            ? replay.Result
            : replayV2
                ? SmartTagStandardV2Layout.ComputeClustered(replay.Inputs, replay.Obstacles,
                    replay.Frame, replay.Settings, replay.Settings.AutoSide, replay.Reservations,
                    searchCheckLimit: int.TryParse(Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_CHECKS"), out int replayV2Checks) ? replayV2Checks : 1000000,
                    standardBaseline: replay.Standard)
                : SmartTagStandardNearHostLayout.ComputeClustered(replay.Inputs, replay.Obstacles,
                    replay.Frame, replay.Settings, replay.Settings.AutoSide, replay.Reservations,
                    searchCheckLimit: int.TryParse(Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_CHECKS"), out int replayChecks) ? replayChecks : 1000000,
                    standardBaseline: replay.Standard);
        Console.WriteLine($"REPLAY: {replay.Inputs.Length} real measured tags; {timer.Elapsed.TotalSeconds:F2}s; " + replayResult.Diagnostic);
        Dictionary<long, TagLayoutPlacement> replayPlacements = replayResult.Placements
            .ToDictionary(item => item.TagKey);
        Dictionary<long, TagLayoutPlacement> replayPlacementsAndReservations =
            replayResult.Placements
                .Concat(replay.Reservations)
                .GroupBy(item => item.TagKey)
                .ToDictionary(group => group.Key, group => group.First());
        int ownedFollowers = replay.Inputs.Count(item => item.PreferLocalClustering &&
            replayPlacements.TryGetValue(item.TagKey, out TagLayoutPlacement? follower) &&
            follower.PreferredFollowerAnchorTagKey != 0);
        int followersOnOwnedRail = replay.Inputs.Count(item => item.PreferLocalClustering &&
            replayPlacements.TryGetValue(item.TagKey, out TagLayoutPlacement? follower) &&
            follower.PreferredFollowerAnchorTagKey != 0 &&
            replayPlacementsAndReservations.TryGetValue(
                follower.PreferredFollowerAnchorTagKey, out TagLayoutPlacement? companion) &&
            Math.Abs(follower.TagBounds.MinU - companion.TagBounds.MinU) <=
                Math.Max(replay.Settings.Clearance * 2.0, 0.0025));
        TagLayoutPlacement[] visibleCompanionPlacements = replayPlacementsAndReservations.Values
            .Where(item => item.CanAnchorDuctFollowers)
            .ToArray();
        int followersOnVisibleRail = replay.Inputs.Count(item => item.PreferLocalClustering &&
            replayPlacements.TryGetValue(item.TagKey, out TagLayoutPlacement? follower) &&
            visibleCompanionPlacements.Any(companion =>
                Math.Abs(follower.TagBounds.MinU - companion.TagBounds.MinU) <=
                Math.Max(replay.Settings.Clearance * 2.0, 0.0025)));
        int stableCompanions = replay.Inputs.Count(item => item.CanAnchorDuctFollowers &&
            replayPlacements.TryGetValue(item.TagKey, out TagLayoutPlacement? actual) &&
            replay.Standard.Placements.Any(original => original.TagKey == item.TagKey &&
                                                       original.Head == actual.Head &&
                                                       original.TagBounds == actual.TagBounds));
        Console.WriteLine($"OWNERSHIP: {followersOnOwnedRail}/{ownedFollowers} Duct follower(s) on their owned DA/AT rail, " +
                          $"{followersOnVisibleRail} on a visible DA/AT rail; " +
                          $"{stableCompanions}/{replay.Inputs.Count(item => item.CanAnchorDuctFollowers)} DA/AT tag(s) unchanged.");
        if (Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_DUMP") == "1")
        {
            foreach (LayoutTagInput input in replay.Inputs
                         .Where(item => item.PreferLocalClustering)
                         .OrderByDescending(item => item.Anchor.V))
            {
                TagLayoutPlacement placement = replayPlacements[input.TagKey];
                replayPlacementsAndReservations.TryGetValue(
                    placement.PreferredFollowerAnchorTagKey,
                    out TagLayoutPlacement? owner);
                Console.WriteLine(
                    $"DUCT {input.TagKey}: host=({input.Anchor.U:F2},{input.Anchor.V:F2}) " +
                    $"row={placement.Head.V:F2} rail={placement.TagBounds.MinU:F2} " +
                    $"owner={placement.PreferredFollowerAnchorTagKey} " +
                    $"ownerRail={(owner is null ? "none" : owner.TagBounds.MinU.ToString("F2"))} " +
                    $"ownerRow={(owner is null ? "none" : owner.Head.V.ToString("F2"))}");
            }
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_PASSES"), out int passes))
            for (int pass = 1; pass < passes; pass++)
            {
                replayResult = SmartTagStandardNearHostLayout.ComputeClustered(replay.Inputs, replay.Obstacles,
                    replay.Frame, replay.Settings, replay.Settings.AutoSide, replay.Reservations,
                    searchCheckLimit: 1000000, standardBaseline: replayResult);
                Console.WriteLine($"PASS {pass + 1}: {timer.Elapsed.TotalSeconds:F2}s; {replayResult.Diagnostic}");
            }
        var details = new List<string>();
        Console.WriteLine("Hard clashes: " + SmartTagStandardNearHostLayout.CountHardClashes(replayResult.Placements,
            replay.Reservations, replay.Obstacles, replay.Frame, replay.Settings.Clearance, details));
        if (Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_BREAKDOWN") == "1")
        {
            HashSet<long> replayDuctKeys = replay.Inputs
                .Where(item => item.PreferLocalClustering)
                .Select(item => item.TagKey)
                .ToHashSet();
            TagLayoutPlacement[] replayDucts = replayResult.Placements
                .Where(item => replayDuctKeys.Contains(item.TagKey))
                .ToArray();
            TagLayoutPlacement[] replayCompanions = replayResult.Placements
                .Where(item => !replayDuctKeys.Contains(item.TagKey))
                .ToArray();
            int fixedCompanionClashes = SmartTagStandardNearHostLayout.CountHardClashes(
                replayCompanions, replay.Reservations, replay.Obstacles,
                replay.Frame, replay.Settings.Clearance);
            int ductClashes = SmartTagStandardNearHostLayout.CountHardClashes(
                replayDucts,
                replayCompanions.Concat(replay.Reservations).ToArray(),
                replay.Obstacles, replay.Frame, replay.Settings.Clearance);
            Console.WriteLine($"BREAKDOWN: fixed DA/AT={fixedCompanionClashes}; Duct-involved={ductClashes}.");
        }
        if (Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_SUMMARY") != "1")
            foreach (string detail in details) Console.WriteLine(detail);
        Require(replayResult.Placements.Select(p => p.TagKey).Order().SequenceEqual(replay.Inputs.Select(p => p.TagKey).Order()),
            "Replay must retain every measured tag.");
        if (Environment.GetEnvironmentVariable("SMARTTAG_REPLAY_ISOLATED") == "1")
        {
            foreach (var input in replay.Inputs)
            {
                var seed = replay.Standard.Placements.Single(p => p.TagKey == input.TagKey);
                var isolated = SmartTagStandardNearHostLayout.ComputeClustered([input], replay.Obstacles, replay.Frame,
                    replay.Settings, true, replay.Reservations, standardBaseline: new([seed], 0, 0, replay.Frame));
                if ((isolated.Placements.Single().Head == seed.Head || isolated.ClashCount > 0) &&
                    !isolated.Diagnostic!.Contains("0 candidate tag(s)"))
                {
                    Console.WriteLine($"ISOLATED {input.TagKey} at {input.Anchor} size {input.TagWidth:F2}/{input.TagHeight:F2}: {isolated.Diagnostic}");
                    foreach (bool left in new[] { true, false })
                    {
                        var option = SmartTagStandardNearHostLayout.Compute([input], replay.Obstacles, replay.Frame, replay.Settings with { PlaceLeft = left });
                        var p = option.Placements.Single();
                        Console.WriteLine($"SIDE {left}: box {p.TagBounds}; head {p.Head}; hard {SmartTagStandardNearHostLayout.CountHardClashes(option.Placements, [], replay.Obstacles, replay.Frame, replay.Settings.Clearance)}");
                    }
                }
            }
        }
        return 0;
    }
    if (Environment.GetEnvironmentVariable("SMARTTAG_BENCHMARK_FULL") == "1")
    {
        var fullTags = Enumerable.Range(0, 997).Select(i =>
        {
            int localGroup = i / 7;
            int column = localGroup % 10;
            int band = localGroup / 10;
            return Tag(60000 + i, 8 + band * 9 + (i % 7) * 0.8,
                height: 0.35, anchorU: 15 + column * 18, width: 1.2);
        }).ToArray();
        var fullPlacements = fullTags.Select((tag, index) =>
        {
            bool left = (index / 2) % 2 == 0;
            double minU = left ? tag.Anchor.U - 9 : tag.Anchor.U + 8;
            return new TagLayoutPlacement(tag.TagKey,
                new LayoutPoint(minU + 0.6, tag.Anchor.V), tag.Anchor, tag.Anchor,
                new LayoutRect(minU, tag.Anchor.V - 0.175, minU + 1.2, tag.Anchor.V + 0.175),
                false, false, false, tag.Label, tag.Group);
        }).ToArray();
        var fullSeed = new SmartTagLayoutResult(fullPlacements, 0, 0,
            new LayoutRect(0, 0, 200, 150));
        var fullWatch = System.Diagnostics.Stopwatch.StartNew();
        var fullResult = SmartTagStandardNearHostLayout.ComputeClustered(
            fullTags,
            fullTags.Select(tag => new LayoutObstacle(tag.ElementKey, tag.ElementBounds)).ToArray(),
            new LayoutRect(0, 0, 200, 150),
            Settings(true) with { AutoSide = true, LayoutStyle = SmartTagLayoutStyle.StandardNearHost },
            true,
            standardBaseline: fullSeed);
        fullWatch.Stop();
        double oldGap = fullPlacements.Sum(item => Math.Abs(item.Head.U - item.End.U));
        double newGap = fullResult.Placements.Sum(item => Math.Abs(item.Head.U - item.End.U));
        Require(fullResult.Placements.Count == 997,
            "Full-model Near Host benchmark must retain every Standard tag.");
        Require(newGap < oldGap * 0.75,
            "Full-model Near Host benchmark must materially shorten distant Standard leaders.");
        Console.WriteLine($"FULL BENCHMARK: {fullWatch.Elapsed.TotalSeconds:F2}s; " +
                          $"leaders {oldGap:F0}->{newGap:F0}; {fullResult.Diagnostic}");
        return 0;
    }
    if (Environment.GetEnvironmentVariable("SMARTTAG_BENCHMARK") == "1")
    {
        var benchTags = Enumerable.Range(0, 48).Select(i => Tag(20000 + i,
            22 - (i % 12) * 1.4, anchorU: 6 + (i / 12) * 8)).ToArray();
        var benchObstacles = benchTags.Select(t => new LayoutObstacle(t.ElementKey, t.ElementBounds))
            .Concat(Enumerable.Range(0, 300).Select(i => new LayoutObstacle(30000 + i,
                new LayoutRect(100 + i, 100, 100.5 + i, 101)))).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bench = SmartTagStandardNearHostLayout.ComputeClustered(benchTags, benchObstacles,
            new LayoutRect(0, 0, 40, 25), Settings(true), true);
        watch.Stop();
        string signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(bench))));
        Console.WriteLine($"BENCHMARK: {watch.Elapsed.TotalMilliseconds:F1} ms; signature={signature}; clashes={bench.ClashCount}");
        return 0;
    }
    NearHostRegressionTests.Run();
    var nearInputs = new[] { Tag(9001, 8), Tag(9002, 7), Tag(9003, 6), Tag(9004, 2, anchorU: 17) };
    var nearSettings = Settings(true) with { LayoutStyle = SmartTagLayoutStyle.StandardNearHost };
    var nearObstacles = nearInputs.Select(t => new LayoutObstacle(t.ElementKey, t.ElementBounds)).ToArray();
    var nearFrame = new LayoutRect(0, 0, 25, 12);
    var standardBefore = SmartTagLayoutEngine.ComputeClustered(nearInputs, nearObstacles, nearFrame, Settings(true), false);
    var timedOut = SmartTagStandardNearHostLayout.ComputeClustered(nearInputs, nearObstacles, nearFrame,
        nearSettings, false, searchCheckLimit: 1);
    Require(timedOut.Placements.SequenceEqual(standardBefore.Placements),
        "Near Host: budget exhaustion must retain the complete Standard preview, not erase tags.");
    Require(timedOut.Diagnostic is not null, "Near Host: fallback must be clearly identified.");
    var actualStandardSeed = standardBefore with
    {
        Placements = standardBefore.Placements.Select(p => p with
        {
            Head = p.Head with { U = p.Head.U - 0.3 },
            TagBounds = p.TagBounds with { MinU = p.TagBounds.MinU - 0.3, MaxU = p.TagBounds.MaxU - 0.3 }
        }).ToList()
    };
    var seeded = SmartTagStandardNearHostLayout.ComputeClustered(nearInputs, nearObstacles, nearFrame,
        nearSettings, false, standardBaseline: actualStandardSeed);
    Require(seeded.Placements.Count == actualStandardSeed.Placements.Count && seeded.ClashCount == 0,
        "Near Host must retain the complete, clean actual-family Standard result while moving local rails nearer.");
    Require(seeded.Placements.OrderByDescending(p => p.Head.V).Select(p => p.TagKey).SequenceEqual(
            actualStandardSeed.Placements.OrderByDescending(p => p.Head.V).Select(p => p.TagKey)),
        "Near Host local reflow must retain Standard top-to-bottom ordering.");
    var farHost = Tag(95001, 8, anchorU: 20);
    var farTag = new TagLayoutPlacement(farHost.TagKey, new LayoutPoint(2.75, 8), farHost.Anchor,
        farHost.Anchor, new LayoutRect(2, 7.75, 3.5, 8.25), false, false, false, farHost.Label, farHost.Group);
    var farSeed = new SmartTagLayoutResult([farTag], 0, 0, farTag.TagBounds);
    var nearer = SmartTagStandardNearHostLayout.ComputeClustered([farHost], [], new LayoutRect(0, 0, 30, 12),
        nearSettings, false, standardBaseline: farSeed);
    Require(nearer.Placements[0].Head.U > farTag.Head.U && nearer.ClashCount == 0,
        "Clean distant Standard group should only move closer when the entire candidate is clear.");
    Require(farSeed.Placements[0] == farTag, "Refinement must not mutate the saved Standard seed.");
    Require(nearer.Placements[0].Head.V == farTag.Head.V && nearer.Placements[0].End == farTag.End &&
        nearer.Placements[0].Elbow == farTag.Elbow && nearer.Placements[0].TagBounds.Height == farTag.TagBounds.Height,
        "Pulling nearer must preserve Standard rows, endpoints, elbows and text height.");
    var monotonicDense = SmartTagStandardNearHostLayout.ComputeClustered(
        [farHost],
        [new LayoutObstacle(95999, new LayoutRect(0, 7.7, 20, 8.3))],
        new LayoutRect(0, 0, 30, 12),
        nearSettings,
        false,
        standardBaseline: farSeed);
    Require(monotonicDense.Placements.Single().Head.U > farTag.Head.U,
        "A pre-existing Standard clash must not freeze a safe nearer translation when the clash count does not increase.");
    var destinationCrossing = new TagLayoutPlacement(
        95998,
        new LayoutPoint(25.5, 10),
        new LayoutPoint(18.5, 8),
        new LayoutPoint(18.5, 10),
        new LayoutRect(25, 9.75, 26, 10.25),
        true, true, false, "FIXED", "Pipe");
    var crossingBlocked = SmartTagStandardNearHostLayout.ComputeClustered(
        [farHost], [], new LayoutRect(0, 0, 30, 12), nearSettings, false,
        [destinationCrossing], standardBaseline: farSeed);
    Require(Math.Abs(crossingBlocked.Placements.Single().Head.U - crossingBlocked.Placements.Single().End.U) <
                Math.Abs(farTag.Head.U - farTag.End.U) &&
            SmartTagStandardNearHostLayout.CountHardClashes(
                crossingBlocked.Placements, [destinationCrossing], [],
                new LayoutRect(0, 0, 30, 12), nearSettings.Clearance) == 0,
        "A distant Standard tag may change row to get nearer, but its new route must clear the existing leader.");
    var terminal = Tag(95100, 5, anchorU: 10);
    var standardRight = new TagLayoutPlacement(
        terminal.TagKey,
        new LayoutPoint(20.75, 5),
        terminal.Anchor,
        terminal.Anchor,
        new LayoutRect(20, 4.75, 21.5, 5.25),
        false,
        false,
        false,
        terminal.Label,
        terminal.Group);
    var nearbyLeftTags = new[]
    {
        new TagLayoutPlacement(95101, new LayoutPoint(7.75, 6), new LayoutPoint(10, 6),
            new LayoutPoint(10, 6), new LayoutRect(7, 5.75, 8.5, 6.25), false, false, false, "LEFT-A", "Pipe"),
        new TagLayoutPlacement(95102, new LayoutPoint(7.75, 4), new LayoutPoint(10, 4),
            new LayoutPoint(10, 4), new LayoutRect(7, 3.75, 8.5, 4.25), false, false, false, "LEFT-B", "Pipe")
    };
    var rightSeed = new SmartTagLayoutResult([standardRight], 0, 0, standardRight.TagBounds);
    var localSide = SmartTagStandardNearHostLayout.ComputeClustered(
        [terminal],
        [new LayoutObstacle(terminal.ElementKey, terminal.ElementBounds)],
        new LayoutRect(0, 0, 25, 12),
        nearSettings with { AutoSide = true },
        true,
        nearbyLeftTags,
        standardBaseline: rightSeed);
    Require(localSide.Placements.Single().Head.U < localSide.Placements.Single().End.U,
        "Near Host Auto must switch a distant Standard-right terminal to the clean established rail on its left.");
    var emptyLeftSide = SmartTagStandardNearHostLayout.ComputeClustered(
        [terminal],
        [new LayoutObstacle(95109, new LayoutRect(10.65, 4.6, 12.4, 5.4))],
        new LayoutRect(0, 0, 25, 12),
        nearSettings with { AutoSide = true },
        true,
        standardBaseline: rightSeed);
    Require(emptyLeftSide.Placements.Single().Head.U < emptyLeftSide.Placements.Single().End.U,
        "Near Host Auto must create a new natural left rail when no left rail exists and the right side is occupied.");
    var nearButUnaligned = new TagLayoutPlacement(
        terminal.TagKey,
        new LayoutPoint(8.95, 5),
        terminal.Anchor,
        terminal.Anchor,
        new LayoutRect(8.2, 4.75, 9.7, 5.25),
        false, false, false, terminal.Label, terminal.Group);
    var alignedLocalRail = SmartTagStandardNearHostLayout.ComputeClustered(
        [terminal],
        [new LayoutObstacle(terminal.ElementKey, terminal.ElementBounds)],
        new LayoutRect(0, 0, 25, 12),
        nearSettings with { AutoSide = true },
        true,
        nearbyLeftTags,
        standardBaseline: new SmartTagLayoutResult([nearButUnaligned], 0, 0, nearButUnaligned.TagBounds));
    Require(alignedLocalRail.Placements.Single().Head == nearButUnaligned.Head &&
            alignedLocalRail.Placements.Single().End == nearButUnaligned.End &&
            alignedLocalRail.Placements.Single().Elbow == nearButUnaligned.Elbow &&
            alignedLocalRail.Placements.Single().TagBounds == nearButUnaligned.TagBounds,
        "A tag already near its host must remain exactly on its accepted Standard placement.");
    var localHosts = new[]
    {
        Tag(95110, 6, anchorU: 10),
        Tag(95111, 5, anchorU: 10),
        Tag(95112, 4, anchorU: 10)
    };
    TagLayoutPlacement Seed(long key, double row, bool left)
    {
        double minU = left ? 7 : 20;
        return new TagLayoutPlacement(key, new LayoutPoint(minU + 0.75, row),
            new LayoutPoint(10, row), new LayoutPoint(10, row),
            new LayoutRect(minU, row - 0.25, minU + 1.5, row + 0.25),
            false, false, false, $"LOCAL-{key}", "Pipe");
    }
    var mixedStandard = new SmartTagLayoutResult(
        [Seed(95110, 6, true), Seed(95111, 5, false), Seed(95112, 4, true)],
        0, 0, new LayoutRect(7, 3.75, 21.5, 6.25));
    var locallyGrouped = SmartTagStandardNearHostLayout.ComputeClustered(
        localHosts,
        localHosts.Select(item => new LayoutObstacle(item.ElementKey, item.ElementBounds)).ToArray(),
        new LayoutRect(0, 0, 25, 12),
        nearSettings with { AutoSide = true },
        true,
        standardBaseline: mixedStandard);
    Require(locallyGrouped.Placements.All(item => item.Head.U < item.End.U),
        "Near Host must group by nearby hosts, not preserve one distant opposite Standard rail.");
    Require(locallyGrouped.ClashCount == 0,
        "Host-local side correction must retain a zero-clash Standard-quality result.");
    var cleanHost = Tag(95120, 9, anchorU: 10);
    var crowdedHosts = new[]
    {
        Tag(95121, 6.0, anchorU: 10),
        Tag(95122, 5.8, anchorU: 10),
        Tag(95123, 5.6, anchorU: 10)
    };
    TagLayoutPlacement CrowdedSeed(LayoutTagInput tag) => new(
        tag.TagKey,
        new LayoutPoint(9.15, tag.Anchor.V),
        tag.Anchor,
        tag.Anchor,
        new LayoutRect(8.4, tag.Anchor.V - 0.25, 9.9, tag.Anchor.V + 0.25),
        false, false, false, tag.Label, tag.Group);
    var cleanSeed = new TagLayoutPlacement(
        cleanHost.TagKey,
        new LayoutPoint(9.05, cleanHost.Anchor.V),
        cleanHost.Anchor,
        cleanHost.Anchor,
        new LayoutRect(8.3, 8.75, 9.8, 9.25),
        false, false, false, cleanHost.Label, cleanHost.Group);
    var crowdedSeedPlacements = crowdedHosts.Select(CrowdedSeed).ToArray();
    var crowdedBaseline = new SmartTagLayoutResult(
        [cleanSeed, .. crowdedSeedPlacements],
        0, 0, new LayoutRect(8.3, 5.35, 9.9, 9.25));
    var modelBlock = new LayoutObstacle(95199, new LayoutRect(7.8, 5.2, 9.65, 6.35));
    var clusteredClearSpace = SmartTagStandardNearHostLayout.ComputeClustered(
        [cleanHost, .. crowdedHosts],
        [modelBlock, .. crowdedHosts.Select(tag => new LayoutObstacle(tag.ElementKey, tag.ElementBounds))],
        new LayoutRect(0, 0, 25, 12),
        nearSettings with { AutoSide = true },
        true,
        standardBaseline: crowdedBaseline);
    TagLayoutPlacement cleanAfter = clusteredClearSpace.Placements.Single(item => item.TagKey == cleanHost.TagKey);
    Require(cleanAfter.Head == cleanSeed.Head && cleanAfter.End == cleanSeed.End &&
            cleanAfter.Elbow == cleanSeed.Elbow && cleanAfter.TagBounds == cleanSeed.TagBounds,
        "A clean nearby Standard tag must remain unchanged while a crowded local group is repaired.");
    var packedLocal = crowdedHosts
        .OrderByDescending(tag => tag.Anchor.V)
        .Select(tag => clusteredClearSpace.Placements.Single(item => item.TagKey == tag.TagKey))
        .ToArray();
    bool packedLeft = packedLocal[0].Head.U < packedLocal[0].End.U;
    double PackedEdge(TagLayoutPlacement item) => packedLeft ? item.TagBounds.MaxU : item.TagBounds.MinU;
    Require(packedLocal.All(item => (item.Head.U < item.End.U) == packedLeft) &&
            packedLocal.Select(PackedEdge).Max() - packedLocal.Select(PackedEdge).Min() <= 1e-8,
        "Nearby problem tags must be aligned on one shared local text rail.");
    for (int index = 1; index < packedLocal.Length; index++)
        Require(packedLocal[index].TagBounds.MaxV + nearSettings.RowSpacing <=
                packedLocal[index - 1].TagBounds.MinV + 1e-8,
            "A repaired local group must use uniform non-overlapping Standard row spacing.");
    Require(packedLocal.All(item => !modelBlock.Bounds.Intersects(item.TagBounds)) &&
            SmartTagStandardNearHostLayout.CountHardClashes(
                packedLocal,
                [cleanSeed],
                [modelBlock, .. crowdedHosts.Select(tag => new LayoutObstacle(tag.ElementKey, tag.ElementBounds))],
                new LayoutRect(0, 0, 25, 12),
                nearSettings.Clearance) == 0,
        "Repaired tag text and leaders must occupy clear space without touching model or fixed tags.");
    var repairInputs = new[] { Tag(92001, 8), Tag(92002, 8, anchorU: 17) };
    var repairObstacles = new[] { new LayoutObstacle(93001, new LayoutRect(4, 0, 5.9, 12)) };
    var repairBaseline = SmartTagLayoutEngine.ComputeClustered(repairInputs, repairObstacles, nearFrame, nearSettings, false);
    var repairedLayout = SmartTagStandardNearHostLayout.ComputeClustered(repairInputs, repairObstacles, nearFrame, nearSettings, false);
    Require(repairedLayout.Placements.All(item =>
    {
        TagLayoutPlacement standardItem = repairBaseline.Placements.Single(p => p.TagKey == item.TagKey);
        return item.Head == standardItem.Head && item.End == standardItem.End &&
               item.Elbow == standardItem.Elbow && item.TagBounds == standardItem.TagBounds;
    }),
        "Near Host must not reflow an already-near Standard column merely to run a separate clash repair.");
    var near = SmartTagStandardNearHostLayout.ComputeClustered(nearInputs, nearObstacles, nearFrame, nearSettings, false);
    Require(near.Placements.Count == 4 && near.ClashCount == 0, "Near Host: simple separated hosts must fit without clashes.");
    var withDistantObstacles = SmartTagStandardNearHostLayout.ComputeClustered(nearInputs,
        nearObstacles.Concat([new LayoutObstacle(99000, new LayoutRect(100, 100, 110, 110))]).ToArray(),
        nearFrame, nearSettings, false);
    Require(near.Placements.SequenceEqual(withDistantObstacles.Placements),
        "Near Host: broad-phase exclusion must not alter layout when distant model obstacles are added.");
    Require(near.Placements.Where(p => p.TagKey < 9004).Select(p => p.TagBounds.MinU).Distinct().Count() == 1,
        "Near Host: a local group must share one text edge.");
    Require(near.Placements.Single(p => p.TagKey == 9004).TagBounds.MinU > 12,
        "Near Host: distant host must retain a nearby local column.");
    foreach (var placement in near.Placements)
    {
        Require(new LayoutSegment(placement.Head, placement.Elbow).IsHorizontal, "Near Host: head leader must be horizontal.");
        Require(new LayoutSegment(placement.End, placement.Elbow).IsVertical, "Near Host: host leader must be vertical.");
        Require(!nearObstacles.Any(o => o.Bounds.Intersects(placement.TagBounds)), "Near Host: text must clear model.");
        Require(!near.Placements.Any(p => p.TagKey != placement.TagKey && p.TagBounds.Intersects(placement.TagBounds)),
            "Near Host: text must clear other tags.");
    }
    var standardAfter = SmartTagLayoutEngine.ComputeClustered(nearInputs, nearObstacles, nearFrame, Settings(true), false);
    Require(standardBefore.Placements.SequenceEqual(standardAfter.Placements), "Experimental style must not mutate Standard state.");
    var offsetInputs = nearInputs.Select(t => t with { TextOffsetV = 0.18 }).ToArray();
    var offsetLayout = SmartTagStandardNearHostLayout.ComputeClustered(offsetInputs, nearObstacles, nearFrame, nearSettings, false);
    Require(offsetLayout.Placements.All(p => Math.Abs((p.TagBounds.MinV + p.TagBounds.MaxV) * 0.5 - p.Head.V - 0.18) < 1e-8),
        "Near Host: actual family text offset must be retained during route checks.");
    Console.WriteLine("PASS: isolated Near Host style, local columns, orthogonal routes and actual text offsets.");
    var denseTimer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var denseHosts = Enumerable.Range(0, 138).Select(i => Tag(40000 + i, 8, anchorU: 7 + (i % 3) * 0.01)).ToArray();
        SmartTagStandardNearHostLayout.ComputeClustered(denseHosts,
            [new LayoutObstacle(50000, new LayoutRect(0, 0, 25, 12))], nearFrame, nearSettings, true);
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("search limit reached")) { }
    Require(denseTimer.Elapsed.TotalSeconds < 6, "Near Host: impossible dense search must stop promptly, not run for minutes.");
    Console.WriteLine($"PASS: 138-tag blocked-region search finished/stopped in {denseTimer.Elapsed.TotalSeconds:F2}s.");
    Require(SmartTagStandardNearHostLayout.CountHardClashes(near.Placements, [], nearObstacles, nearFrame, 0.01) == 0,
        "Near Host: final validation accepts a clean layout.");
    var victim = near.Placements[0];
    Require(SmartTagStandardNearHostLayout.CountHardClashes([victim], [],
        [new LayoutObstacle(99999, victim.TagBounds)], nearFrame, 0.01) > 0,
        "Near Host: final validation must reject text over model, including unselected elements.");
    var crossing = victim with { TagKey = 99999, Head = new LayoutPoint(21, victim.Head.V),
        Elbow = new LayoutPoint(0, victim.Head.V), End = new LayoutPoint(0, victim.Head.V),
        TagBounds = new LayoutRect(20, 10, 22, 11), UsesElbow = false };
    Require(SmartTagStandardNearHostLayout.CountHardClashes([victim], [crossing], [], nearFrame, 0.01) > 0,
        "Near Host: fixed leader crossing moving text must fail final validation.");
    var orderedHosts = Enumerable.Range(0, 7).Select(i => Tag(9100 + i, 10 - i * 0.9)).ToArray();
    var orderedAuto = SmartTagStandardNearHostLayout.ComputeClustered(orderedHosts,
        orderedHosts.Select(t => new LayoutObstacle(t.ElementKey, t.ElementBounds)).ToArray(),
        nearFrame, nearSettings with { AutoSide = true }, true);
    Require(orderedAuto.Placements.Select(p => p.TagBounds.MinU).Distinct().Count() == 1,
        "Near Host: a continuous vertical group must retain one Standard-like column in Auto mode.");
    var orderedRows = orderedHosts.Select(t => orderedAuto.Placements.Single(p => p.TagKey == t.TagKey)).ToArray();
    for (int i = 1; i < orderedRows.Length; i++)
        Require(orderedRows[i].TagBounds.MaxV + nearSettings.RowSpacing <= orderedRows[i - 1].TagBounds.MinV + 1e-8,
            "Near Host: host order and text spacing must not be traded for shorter leaders.");
var frame = new LayoutRect(0, 0, 10, 10);
Require(Settings().LayoutStyle == SmartTagLayoutStyle.Standard,
    "The accepted Standard layout must remain the default for every existing caller.");

var manualBounds = new Dictionary<long, LayoutRect>
{
    [1001] = new LayoutRect(1.0, 8.0, 2.4, 8.5),
    [1002] = new LayoutRect(1.6, 5.0, 3.8, 5.7),
    [1003] = new LayoutRect(0.5, 2.0, 1.3, 2.4)
};
IReadOnlyDictionary<long, double> alignLeft =
    SmartTagManualAlignment.ComputeHorizontalShifts(manualBounds, alignLeftEdge: true);
Require(Math.Abs(manualBounds[1001].MinU + alignLeft[1001] - 0.5) <= 1e-9 &&
        Math.Abs(manualBounds[1002].MinU + alignLeft[1002] - 0.5) <= 1e-9 &&
        Math.Abs(manualBounds[1003].MinU + alignLeft[1003] - 0.5) <= 1e-9,
    "Manual left-edge alignment must support different real tag-family widths.");
IReadOnlyDictionary<long, double> alignRight =
    SmartTagManualAlignment.ComputeHorizontalShifts(manualBounds, alignLeftEdge: false);
Require(Math.Abs(manualBounds[1001].MaxU + alignRight[1001] - 3.8) <= 1e-9 &&
        Math.Abs(manualBounds[1002].MaxU + alignRight[1002] - 3.8) <= 1e-9 &&
        Math.Abs(manualBounds[1003].MaxU + alignRight[1003] - 3.8) <= 1e-9,
    "Manual right-edge alignment must support different real tag-family widths.");
var referenceTargets = manualBounds
    .Where(item => item.Key != 1001)
    .ToDictionary(item => item.Key, item => item.Value);
IReadOnlyDictionary<long, double> alignToReferenceLeft =
    SmartTagManualAlignment.ComputeHorizontalShifts(
        referenceTargets,
        manualBounds[1001].MinU,
        alignLeftEdge: true);
Require(Math.Abs(manualBounds[1002].MinU + alignToReferenceLeft[1002] -
                 manualBounds[1001].MinU) <= 1e-9 &&
        Math.Abs(manualBounds[1003].MinU + alignToReferenceLeft[1003] -
                 manualBounds[1001].MinU) <= 1e-9,
    "Target tags must align to the explicitly picked reference tag, not to a group extreme.");
Require(!alignToReferenceLeft.ContainsKey(1001),
    "The picked reference tag must never be included in the tags that move.");

SmartTagStackRoutePlan crossingAwareStack = SmartTagStackRouting.FindBestOrder(
    [
        new SmartTagStackRouteInput(1, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 0)),
        new SmartTagStackRouteInput(2, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 0)),
        new SmartTagStackRouteInput(3, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(3, 0))
    ],
    new LayoutRect(0, 5.0, 2.0, 5.5),
    referenceEdge: 0.0,
    alignLeftEdge: true,
    rowGap: 0.5);
Require(crossingAwareStack.CrossingCount == 0,
    "Stack routing must reorder same-level hosts to eliminate orthogonal leader crossings.");
SmartTagStackRoutePlan crossingAwareAbove = SmartTagStackRouting.FindBestOrder(
    [
        new SmartTagStackRouteInput(11, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 6)),
        new SmartTagStackRouteInput(12, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 6)),
        new SmartTagStackRouteInput(13, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(3, 6))
    ],
    new LayoutRect(0, 0.0, 2.0, 0.5),
    referenceEdge: 0.0,
    alignLeftEdge: true,
    rowGap: 0.5,
    placeAbove: true);
Require(crossingAwareAbove.CrossingCount == 0,
    "Auto Column must also eliminate crossings for rows placed above the reference.");
LayoutRect multilineBody = new(10, 20, 14, 24);
LayoutRect verticalLeaderPollutedBody = new(10, 10, 14, 24);
LayoutRect trimmedVerticalBody = SmartTagStackRouting.TrimAsymmetricLeaderTail(
    verticalLeaderPollutedBody,
    new LayoutPoint(12, 22),
    minimumBodyHeight: 4);
Require(
    Math.Abs(trimmedVerticalBody.MinV - 20) <= 1e-9 &&
    Math.Abs(trimmedVerticalBody.MaxV - 24) <= 1e-9 &&
    SmartTagStackRouting.TrimAsymmetricLeaderTail(
        multilineBody,
        new LayoutPoint(12, 22),
        minimumBodyHeight: 4) == multilineBody,
    "AUTO/STACK must remove a long one-sided vertical leader tail without trimming a real multiline body.");
Require(
    Math.Abs(SmartTagStackRouting.GetVisibleLeftEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(580, 22),
        new LayoutPoint(300, 22),
        hasLeader: true) - 580) <= 1e-9 &&
    Math.Abs(SmartTagStackRouting.GetVisibleLeftEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(450, 22),
        new LayoutPoint(700, 22),
        hasLeader: true) - 489) <= 1e-9 &&
    Math.Abs(SmartTagStackRouting.GetVisibleLeftEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(580, 22),
        new LayoutPoint(300, 22),
        hasLeader: false) - 489) <= 1e-9,
    "AUTO visible-left alignment must use TagHeadPosition for a left-host leader, " +
    "but retain the measured MinU for right-host or leaderless tags.");
Require(
    Math.Abs(SmartTagStackRouting.GetVisibleRightEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(580, 22),
        new LayoutPoint(700, 22),
        hasLeader: true) - 580) <= 1e-9 &&
    Math.Abs(SmartTagStackRouting.GetVisibleRightEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(580, 22),
        new LayoutPoint(300, 22),
        hasLeader: true) - 650) <= 1e-9 &&
    Math.Abs(SmartTagStackRouting.GetVisibleRightEdge(
        new LayoutRect(489, 20, 650, 24),
        new LayoutPoint(580, 22),
        new LayoutPoint(700, 22),
        hasLeader: false) - 650) <= 1e-9,
    "Manual RIGHT alignment must mirror LEFT using the visible host-facing attachment edge.");
Require(
    SmartTagStackRouting.ShouldUseStraightHorizontalLeader(
        new LayoutPoint(2, 5),
        new LayoutPoint(8, 5.001),
        axisTolerance: 0.01,
        crossesOtherBody: false,
        crossesOtherLeader: false) &&
    !SmartTagStackRouting.ShouldUseStraightHorizontalLeader(
        new LayoutPoint(2, 5),
        new LayoutPoint(8, 6),
        axisTolerance: 0.01,
        crossesOtherBody: false,
        crossesOtherLeader: false) &&
    !SmartTagStackRouting.ShouldUseStraightHorizontalLeader(
        new LayoutPoint(2, 5),
        new LayoutPoint(8, 5),
        axisTolerance: 0.01,
        crossesOtherBody: false,
        crossesOtherLeader: true),
    "AUTO must collapse to one straight horizontal leader only on the same row and with no clash.");
Require(
    SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
        multilineBody,
        new LayoutPoint(0, 30)) == new LayoutPoint(10, 22) &&
    SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
        multilineBody,
        new LayoutPoint(20, 30)) == new LayoutPoint(14, 22),
    "AUTO leader geometry must use the host-facing visible body edge and body center row, not TagHeadPosition.");
LayoutRect leaderlessBodyAtInsertion = new(5, 20, 7, 24);
Require(
    SmartTagStackRouting.AnchorLeaderlessBodyToLiveBounds(
        leaderlessBodyAtInsertion,
        new LayoutRect(0, 18, 9, 26),
        new LayoutPoint(6, 22),
        new LayoutPoint(0, 22)) == new LayoutRect(7, 20, 9, 24) &&
    SmartTagStackRouting.AnchorLeaderlessBodyToLiveBounds(
        leaderlessBodyAtInsertion,
        new LayoutRect(3, 18, 12, 26),
        new LayoutPoint(6, 22),
        new LayoutPoint(12, 22)) == new LayoutRect(3, 20, 5, 24),
    "AUTO must re-anchor an exact leaderless body size to the visible side of the live leader box.");
SmartTagStackRouteInput[] rightSideSharedLaneRoutes =
[
    new SmartTagStackRouteInput(2001, new LayoutRect(10, 8.8, 12, 9.2),
        new LayoutPoint(10, 9), new LayoutPoint(5, 12)),
    new SmartTagStackRouteInput(2002, new LayoutRect(10, 7.8, 12, 8.2),
        new LayoutPoint(10, 8), new LayoutPoint(5, 12)),
    new SmartTagStackRouteInput(2003, new LayoutRect(10, 6.8, 12, 7.2),
        new LayoutPoint(10, 7), new LayoutPoint(5, 12))
];
IReadOnlyList<double> rightSideLaneAssignment =
    SmartTagStackRouting.FindBestLaneAssignment(
        rightSideSharedLaneRoutes,
        [4.0, 5.0, 6.0],
        [],
        clearance: 0.05);
Require(rightSideLaneAssignment.SequenceEqual([6.0, 5.0, 4.0]),
    "A right-side AUTO column must reverse shared endpoint lanes so leaders remain nested instead of crossing.");
SmartTagStackRouteInput[] leftSideSharedLaneRoutes = rightSideSharedLaneRoutes
    .Select(item => item with
    {
        Head = new LayoutPoint(0, item.Head.V),
        Bounds = new LayoutRect(-2, item.Bounds.MinV, 0, item.Bounds.MaxV)
    })
    .ToArray();
IReadOnlyList<double> leftSideLaneAssignment =
    SmartTagStackRouting.FindBestLaneAssignment(
        leftSideSharedLaneRoutes,
        [4.0, 5.0, 6.0],
        [],
        clearance: 0.05);
Require(leftSideLaneAssignment.SequenceEqual([4.0, 5.0, 6.0]),
    "A left-side AUTO column must retain the mirrored non-crossing shared endpoint lane order.");
SmartTagStackRouteInput[] textAwareLaneRoutes =
[
    new SmartTagStackRouteInput(2101, new LayoutRect(10, 8.8, 12, 9.2),
        new LayoutPoint(10, 9), new LayoutPoint(5, 2)),
    new SmartTagStackRouteInput(2102, new LayoutRect(10, 7.8, 12, 8.2),
        new LayoutPoint(10, 8), new LayoutPoint(5, 2))
];
SmartTagStackRouteInput[] reservedTextBodies =
[
    // Its leader is remote and harmless. Only the visible tag body blocks the
    // otherwise preferred U=6 lane of route 2101.
    new SmartTagStackRouteInput(2199, new LayoutRect(5.8, 8.4, 6.2, 8.6),
        new LayoutPoint(20, 20), new LayoutPoint(20, 21))
];
IReadOnlyList<double> textAwareLaneAssignment =
    SmartTagStackRouting.FindBestLaneAssignment(
        textAwareLaneRoutes,
        [4.0, 6.0],
        reservedTextBodies,
        clearance: 0.05);
Require(textAwareLaneAssignment.SequenceEqual([4.0, 6.0]),
    "AUTO lane routing must prefer a clear lane over a shorter/non-crossing leader that cuts through another tag body.");
Require(SmartTagStackRouting.FindBestLaneAssignment(
        [],
        [5.0],
        rightSideSharedLaneRoutes,
        clearance: 0.05).Count == 0,
    "A lane group containing only the fixed AUTO sample must be ignored without aborting the operation.");
SmartTagAutoColumnRoutePlan fullAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    [
        new SmartTagStackRouteInput(21, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 6)),
        new SmartTagStackRouteInput(22, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 6)),
        new SmartTagStackRouteInput(23, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 0)),
        new SmartTagStackRouteInput(24, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 0))
    ],
    new SmartTagStackRouteInput(20, new LayoutRect(0, 2.75, 2, 3.25),
        new LayoutPoint(0, 3), new LayoutPoint(3, 3)),
    new LayoutRect(0, 2.75, 2, 3.25),
    referenceEdge: 0.0,
    alignLeftEdge: true,
    rowGap: 0.5);
Require(fullAutoColumn.CrossingCount == 0,
    "Auto Column must optimize upper, lower, and fixed-reference leaders as one route set.");
SmartTagAutoColumnRoutePlan belowOnlyAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    [
        new SmartTagStackRouteInput(31, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 6)),
        new SmartTagStackRouteInput(32, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 0))
    ],
    new SmartTagStackRouteInput(30, new LayoutRect(0, 2.75, 2, 3.25),
        new LayoutPoint(0, 3), new LayoutPoint(3, 3)),
    new LayoutRect(0, 2.75, 2, 3.25),
    referenceEdge: 0.0,
    alignLeftEdge: true,
    rowGap: 0.5,
    belowOnly: true);
Require(belowOnlyAutoColumn.AboveNearestFirst.Count == 0 &&
        belowOnlyAutoColumn.BelowNearestFirst.Count == 2,
    "Auto Column below-only mode must never move scanned tags above the sample tag.");
SmartTagAutoColumnRoutePlan aboveOnlyAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    [
        new SmartTagStackRouteInput(41, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(1, 6)),
        new SmartTagStackRouteInput(42, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(2, 0))
    ],
    new SmartTagStackRouteInput(40, new LayoutRect(0, 2.75, 2, 3.25),
        new LayoutPoint(0, 3), new LayoutPoint(3, 3)),
    new LayoutRect(0, 2.75, 2, 3.25),
    referenceEdge: 0.0,
    alignLeftEdge: true,
    rowGap: 0.5,
    aboveOnly: true);
Require(aboveOnlyAutoColumn.BelowNearestFirst.Count == 0 &&
        aboveOnlyAutoColumn.AboveNearestFirst.Count == 2,
    "Auto Column above-only mode must never move scanned tags below the sample tag.");
SmartTagStackRouteInput[] denseAutoInputs =
    new[] { 9, 2, 13, 5, 1, 11, 4, 14, 7, 3, 12, 6, 10, 8 }
    .Select((endpointU, index) => new SmartTagStackRouteInput(
        100 + index,
        new LayoutRect(10, 0, 12, 0.35 + index % 3 * 0.12),
        new LayoutPoint(11, 0.2),
        new LayoutPoint(endpointU * 0.5, 0)))
    .ToArray();
SmartTagAutoColumnRoutePlan denseAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    denseAutoInputs,
    new SmartTagStackRouteInput(99, new LayoutRect(10, 12, 12, 12.5),
        new LayoutPoint(10, 12.25), new LayoutPoint(7.5, 12.25)),
    new LayoutRect(10, 12, 12, 12.5),
    referenceEdge: 10,
    alignLeftEdge: true,
    rowGap: 0.25,
    belowOnly: true);
Require(denseAutoColumn.CrossingCount == 0,
    "Dense Auto Column must eliminate crossings after real tag heights change the row pitch.");
SmartTagAutoColumnRoutePlan collinearAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    [
        new SmartTagStackRouteInput(151, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(5, 6))
    ],
    new SmartTagStackRouteInput(150, new LayoutRect(0, 10, 2, 10.5),
        new LayoutPoint(0, 10.25), new LayoutPoint(5, 0)),
    new LayoutRect(0, 10, 2, 10.5),
    referenceEdge: 0,
    alignLeftEdge: true,
    rowGap: 0.5,
    belowOnly: true);
Require(collinearAutoColumn.CrossingCount > 0,
    "Auto Column must detect collinear vertical leader overlap, not only perpendicular crossings.");
SmartTagAutoColumnRoutePlan hostOrderAutoColumn = SmartTagStackRouting.FindBestAroundReference(
    [
        // Deliberately pass the lower host first. The upper host must still get
        // the upper row, otherwise the two orthogonal routes cross each other.
        new SmartTagStackRouteInput(161, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(6, 2)),
        new SmartTagStackRouteInput(162, new LayoutRect(0, 0, 2, 0.5),
            new LayoutPoint(1, 0.25), new LayoutPoint(7, 8))
    ],
    new SmartTagStackRouteInput(160, new LayoutRect(0, 10, 2, 10.5),
        new LayoutPoint(0, 10.25), new LayoutPoint(-2, 10.25)),
    new LayoutRect(0, 10, 2, 10.5),
    referenceEdge: 0,
    alignLeftEdge: true,
    rowGap: 0.5,
    belowOnly: true);
Require(hostOrderAutoColumn.BelowNearestFirst.SequenceEqual([162L, 161L]),
    "Auto Column must order rows by host endpoint height, independent of scan/input order.");
SmartTagAutoColumnRoutePlan geometricPriorityAutoColumn =
    SmartTagStackRouting.FindBestAroundReference(
        [
            // With the nearer endpoint first, its long vertical tail is cut by
            // the next tag's horizontal shoulder. Reversing these two rows is
            // non-monotonic by host V, but it removes the real line crossing.
            new SmartTagStackRouteInput(171, new LayoutRect(0, 0, 2, 0.5),
                new LayoutPoint(1, 0.25), new LayoutPoint(6, 2)),
            new SmartTagStackRouteInput(172, new LayoutRect(0, 0, 2, 0.5),
                new LayoutPoint(1, 0.25), new LayoutPoint(8, 1.5))
        ],
        new SmartTagStackRouteInput(170, new LayoutRect(0, 10, 2, 10.5),
            new LayoutPoint(0, 10.25), new LayoutPoint(-2, 10.25)),
        new LayoutRect(0, 10, 2, 10.5),
        referenceEdge: 0,
        alignLeftEdge: true,
        rowGap: 0.5,
        belowOnly: true);
Require(geometricPriorityAutoColumn.CrossingCount == 0 &&
        geometricPriorityAutoColumn.BelowNearestFirst.SequenceEqual([172L, 171L]),
    "Auto Column must prioritize eliminating a real leader crossing over monotonic host order.");
Require(!SmartTagDuctChangeRules.ShouldTag(
        sizeChanged: false, elevationChanged: false, ductLengthFeet: 1000.0 / 304.8) &&
        !SmartTagDuctChangeRules.ShouldTag(
            sizeChanged: true, elevationChanged: false, ductLengthFeet: 499.9 / 304.8) &&
        SmartTagDuctChangeRules.ShouldTag(
            sizeChanged: true, elevationChanged: false, ductLengthFeet: 500.0 / 304.8) &&
        !SmartTagDuctChangeRules.ShouldTag(
            sizeChanged: false, elevationChanged: true, ductLengthFeet: 499.9 / 304.8) &&
        SmartTagDuctChangeRules.ShouldTag(
            sizeChanged: false, elevationChanged: true, ductLengthFeet: 500.0 / 304.8),
    "Duct filter must skip ordinary/same-size elbows and enforce 500 mm for both size and elevation changes.");

var ductDensityCandidates = Enumerable.Range(0, 24)
    .Select(index => new DuctDensityCandidate(
        Key: 6000 + index,
        Anchor: index < 12
            ? new LayoutPoint(0.5 + index * 0.7, 2.0)
            : new LayoutPoint(10.5 + (index - 12) * 0.7, 2.0),
        Existing: index is 0 or 12,
        NearestCompanionKey: 7000 + index % 3,
        CompanionDistance: index * 0.1))
    .ToList();
IReadOnlySet<long> densitySelection = SmartTagDuctDensity.Select(
    ductDensityCandidates,
    new LayoutRect(0.0, 0.0, 10.0, 10.0));
Require(densitySelection.Count == 20 &&
        densitySelection.Contains(6000) && densitySelection.Contains(6012) &&
        densitySelection.Count(key => key < 6012) == 10 &&
        densitySelection.Count(key => key >= 6012) == 10,
    "The sample grid must retain at most ten eligible Duct tags independently in every repeated area, with existing tags consuming capacity first.");
IReadOnlySet<long> longestDensitySelection = SmartTagDuctDensity.Select(
    [
        new DuctDensityCandidate(6100, new LayoutPoint(1.0, 1.0), false, 7100, 0.1)
            { HostSpan = 1.0 },
        new DuctDensityCandidate(6101, new LayoutPoint(2.0, 1.0), false, 7100, 0.5)
            { HostSpan = 8.0 }
    ],
    new LayoutRect(0.0, 0.0, 10.0, 10.0),
    maximumPerZone: 1);
Require(longestDensitySelection.SetEquals([6101L]),
    "When Duct density is limited, the longer eligible segment must win before a nearer short segment.");

double clearGroupShift = SmartTagMepClearance.FindNearestHorizontalShift(
    [
        new LayoutRect(5.0, 7.5, 6.0, 8.0),
        new LayoutRect(5.0, 6.7, 6.0, 7.2)
    ],
    [4.0, 4.1],
    rightSide: true,
    otherTagBounds: [],
    obstacles:
    [
        new LayoutObstacle(5001, new LayoutRect(5.2, 6.5, 6.3, 8.2))
    ],
    frame,
    clearance: 0.05,
    offsetFromElements: 0.20,
    columnWidth: 4.0);
Require(Math.Abs(clearGroupShift) > 1e-9,
    "A tag group covering existing MEP must move together to the nearest clear horizontal lane.");
Require(!new LayoutRect(5.0 + clearGroupShift, 7.5, 6.0 + clearGroupShift, 8.0)
        .Intersects(new LayoutRect(5.15, 6.45, 6.35, 8.25)) &&
        !new LayoutRect(5.0 + clearGroupShift, 6.7, 6.0 + clearGroupShift, 7.2)
            .Intersects(new LayoutRect(5.15, 6.45, 6.35, 8.25)),
    "The common horizontal shift must clear every tag row without changing group spacing.");

SmartTagLayoutResult straight = SmartTagLayoutEngine.Compute(
    [Tag(1, 8.0), Tag(2, 6.8), Tag(3, 5.6)],
    [],
    frame,
    Settings());
Require(straight.ClashCount == 0, "Separated tags should not clash.");
Require(straight.ElbowCount == 0, "Separated tags should keep one straight leader.");

SmartTagLayoutResult variableWidthLeftEdges = SmartTagLayoutEngine.Compute(
    [
        Tag(24, 8.5) with { TagWidth = 1.0 },
        Tag(25, 7.0) with { TagWidth = 1.8 },
        Tag(26, 5.5) with { TagWidth = 1.3 }
    ],
    [],
    frame,
    Settings());
Require(variableWidthLeftEdges.Placements
        .Select(item => Math.Round(item.TagBounds.MinU, 8))
        .Distinct()
        .Count() == 1,
    "Different tag family widths in one rail must align by their left text edge.");

var localAbInputs = new List<LayoutTagInput>
{
    Tag(31, 9.0, 0.5),
    Tag(32, 7.0, 0.5),
    Tag(33, 6.8, 0.5)
};
SmartTagLayoutResult localAb = SmartTagLayoutEngine.Compute(
    localAbInputs,
    [],
    frame,
    Settings());
TagLayoutPlacement cPlacement = localAb.Placements.Single(item => item.TagKey == 31);
TagLayoutPlacement bPlacement = localAb.Placements.Single(item => item.TagKey == 32);
TagLayoutPlacement aPlacement = localAb.Placements.Single(item => item.TagKey == 33);
Require(!cPlacement.UsesElbow && !bPlacement.UsesElbow,
    "Separated C and upper B must retain their straight Attached leaders.");
Require(aPlacement.UsesElbow && aPlacement.Head.V < bPlacement.Head.V,
    "When A overlaps B, only lower A should move down with one orthogonal elbow.");
Require(localAb.ClashCount == 0,
    "The local A/B fallback must not cross another tag leader.");

SmartTagLayoutResult packed = SmartTagLayoutEngine.Compute(
    [Tag(1, 8.0, 0.8), Tag(2, 7.8, 0.8), Tag(3, 7.6, 0.8)],
    [],
    frame,
    Settings());
Console.WriteLine($"packed clashes={packed.ClashCount}, elbows={packed.ElbowCount}; " +
    string.Join(" | ", packed.Placements.Select(item => $"{item.TagKey}:V={item.Head.V:0.00},E={item.UsesElbow},C={item.HasClash}")));
Require(packed.Placements.SelectMany((item, index) =>
        packed.Placements.Skip(index + 1).Select(other => (item, other)))
    .All(pair => !pair.item.TagBounds.Intersects(pair.other.TagBounds)),
    "Packed tag text must resolve by moving lower rows on the shared rail.");
Require(packed.ElbowCount >= 1, "At least one lower packed tag should receive one elbow.");
Require(packed.Placements[0].Head.V > packed.Placements[1].Head.V &&
        packed.Placements[1].Head.V > packed.Placements[2].Head.V,
    "Tag order must remain top-to-bottom.");
Require(packed.Placements[0].UsesElbow && packed.Placements[^1].UsesElbow,
    "A dense block should share displacement around its hosts instead of pulling only lower tags far away.");
var packedInputs = new Dictionary<long, LayoutTagInput>
{
    [1] = Tag(1, 8.0, 0.8),
    [2] = Tag(2, 7.8, 0.8),
    [3] = Tag(3, 7.6, 0.8)
};
Require(packed.Placements.All(item => LeaderEndStaysOnHost(item, packedInputs[item.TagKey])),
    "Collision handling must keep every leader endpoint inside its correct host.");
Require(packed.ClashCount == 0,
    "Aligned hosts must route attached elbow offsets without leader crossings.");

SmartTagLayoutResult packedRight = SmartTagLayoutEngine.Compute(
    [Tag(21, 8.0, 0.8), Tag(22, 7.8, 0.8), Tag(23, 7.6, 0.8)],
    [],
    frame,
    Settings() with { PlaceLeft = false });
Require(packedRight.ClashCount == 0,
    "The mirrored right rail must also route aligned hosts without crossings.");
Require(packedRight.Placements.All(item => Math.Abs(item.End.U - 7.0) < 1e-8),
    "Right-side collision routing must keep every endpoint on its host.");

var alignedDenseInputs = Enumerable.Range(0, 12)
    .Select(index => Tag(40 + index, 8.4 - index * 0.12, 0.28))
    .ToList();
SmartTagLayoutResult alignedDense = SmartTagLayoutEngine.Compute(
    alignedDenseInputs,
    [],
    frame,
    Settings() with
    {
        ColumnWidth = 3.0,
        RowSpacing = 0.05,
        Clearance = 0.04
    });
Console.WriteLine($"aligned-dense clashes={alignedDense.ClashCount}, elbows={alignedDense.ElbowCount}");
Require(alignedDense.Placements.All(placement => LeaderEndStaysOnHost(
        placement,
        alignedDenseInputs.Single(input => input.TagKey == placement.TagKey))),
    "Even an over-constrained dense column must keep all leader endpoints inside their hosts.");
Require(alignedDense.Placements.Any(placement => placement.UsesFreeEnd),
    "A dense column should move conflicting tags with host-anchored Free End leaders.");

var fullColumnInputs = Enumerable.Range(0, 24)
    .Select(index => Tag(80 + index, 8.8 - index * 0.10, 0.22))
    .ToList();
SmartTagLayoutResult fullColumn = SmartTagLayoutEngine.Compute(
    fullColumnInputs,
    [],
    frame,
    Settings() with
    {
        ColumnWidth = 5.0,
        RowSpacing = 0.04,
        Clearance = 0.035
    });
Require(fullColumn.Placements.All(placement => LeaderEndStaysOnHost(
        placement,
        fullColumnInputs.Single(input => input.TagKey == placement.TagKey))),
    "A 24-tag stress column must never move a leader endpoint outside its host.");
Require(fullColumn.Placements
        .GroupBy(item => Math.Round(item.Head.V, 8))
        .All(group => group.Count() <= 3),
    "An over-constrained local group must remain distributed instead of piling many tags into one slot.");
double maximumFullColumnTravel = fullColumn.Placements.Max(placement =>
    Math.Abs(placement.Head.V - fullColumnInputs.Single(input => input.TagKey == placement.TagKey).Anchor.V));
Console.WriteLine($"full-column clashes={fullColumn.ClashCount}, max-travel={maximumFullColumnTravel:0.00}");
Require(maximumFullColumnTravel <= 0.741,
    "Local routing must not pull a tag far away merely to solve an over-constrained group.");

LayoutObstacle obstacle = new(999, new LayoutRect(5.7, 7.7, 6.1, 8.2));
SmartTagLayoutResult avoided = SmartTagLayoutEngine.Compute(
    [Tag(1, 8.0), Tag(2, 6.8)],
    [obstacle],
    frame,
    Settings(avoidElements: true));
Console.WriteLine($"avoided clashes={avoided.ClashCount}, elbows={avoided.ElbowCount}; " +
    string.Join(" | ", avoided.Placements.Select(item => $"{item.TagKey}:V={item.Head.V:0.00},E={item.UsesElbow},C={item.HasClash}")));
Require(avoided.ClashCount == 0, "Obstacle should be avoided by a lower free row.");
Require(avoided.Placements[0].UsesElbow, "The blocked straight leader should use exactly one elbow.");

LayoutObstacle architecture = new(
    -999,
    new LayoutRect(4.9, 7.72, 6.2, 8.28),
    LayoutObstacleKind.Architecture);
SmartTagLayoutResult architectureAvoided = SmartTagLeftLayout.Compute(
    [Tag(950, 8.0), Tag(951, 6.8)],
    [architecture],
    frame,
    Settings(avoidElements: true));
Require(architectureAvoided.Placements.All(item =>
        !item.TagBounds.Intersects(architecture.Bounds.Expand(Settings().Clearance), 0.0)),
    "Tag text must move away from a visible architectural or linked-model obstacle.");

LayoutTagInput clusterTopInput = Tag(960, 8.0, anchorU: 4.0);
LayoutTagInput clusterBottomInput = Tag(961, 7.0, anchorU: 4.0);
var clusterInputs = new Dictionary<long, LayoutTagInput>
{
    [clusterTopInput.TagKey] = clusterTopInput,
    [clusterBottomInput.TagKey] = clusterBottomInput
};
var baselineCluster = new List<TagLayoutPlacement>
{
    new(960, new LayoutPoint(6.0, 8.0), clusterTopInput.Anchor, clusterTopInput.Anchor,
        new LayoutRect(6.0, 7.75, 7.0, 8.25), false, false, false, "TOP", "Duct"),
    new(961, new LayoutPoint(6.0, 7.0), clusterBottomInput.Anchor, clusterBottomInput.Anchor,
        new LayoutRect(6.0, 6.75, 7.0, 7.25), false, false, false, "BOTTOM", "Duct")
};
LayoutPoint unchangedWithoutArchitecture = SmartTagArchitectureClearance.FindClusterShift(
    baselineCluster,
    clusterInputs,
    [],
    [],
    frame,
    Settings(avoidElements: true));
Require(Math.Abs(unchangedWithoutArchitecture.U) <= 1e-9 &&
        Math.Abs(unchangedWithoutArchitecture.V) <= 1e-9,
    "Architecture-free layout must remain byte-for-byte on its accepted rows and rail.");
LayoutPoint unchangedForLeaderOnlyCrossing = SmartTagArchitectureClearance.FindClusterShift(
    baselineCluster,
    clusterInputs,
    [],
    [new LayoutObstacle(-999, new LayoutRect(4.8, 7.8, 5.2, 8.2), LayoutObstacleKind.Architecture)],
    frame,
    Settings(avoidElements: true));
Require(Math.Abs(unchangedForLeaderOnlyCrossing.U) <= 1e-9 &&
        Math.Abs(unchangedForLeaderOnlyCrossing.V) <= 1e-9,
    "A leader-only architectural crossing must not move or rearrange an accepted tag cluster.");

LayoutPoint completeClusterShift = SmartTagArchitectureClearance.FindClusterShift(
    baselineCluster,
    clusterInputs,
    [],
    [new LayoutObstacle(-1000, new LayoutRect(5.8, 0.0, 7.1, 10.0), LayoutObstacleKind.Architecture)],
    frame,
    Settings(avoidElements: true));
Require(Math.Abs(completeClusterShift.U) > 1e-8 || Math.Abs(completeClusterShift.V) > 1e-8,
    "A cluster covering architecture must receive one common translation.");
TagLayoutPlacement movedTop = SmartTagArchitectureClearance.Translate(
    baselineCluster[0], clusterTopInput, completeClusterShift);
TagLayoutPlacement movedBottom = SmartTagArchitectureClearance.Translate(
    baselineCluster[1], clusterBottomInput, completeClusterShift);
Require(Math.Abs((movedTop.Head.V - movedBottom.Head.V) -
                 (baselineCluster[0].Head.V - baselineCluster[1].Head.V)) <= 1e-9 &&
        Math.Abs((movedTop.Head.U - movedBottom.Head.U) -
                 (baselineCluster[0].Head.U - baselineCluster[1].Head.U)) <= 1e-9,
    "Architecture clearance must preserve cluster row order, alignment, and internal spacing.");
LayoutRect blockingArchitecture = new LayoutRect(5.8, 0.0, 7.1, 10.0)
    .Expand(Settings().Clearance);
Require(!movedTop.TagBounds.Intersects(blockingArchitecture) &&
        !movedBottom.TagBounds.Intersects(blockingArchitecture),
    "The common cluster translation must place all tag text outside the blocking wall.");
Require(movedTop.End == baselineCluster[0].End && movedBottom.End == baselineCluster[1].End,
    "Translating tag text must never detach either leader endpoint from its MEP host.");

SmartTagLayoutResult twoSided = SmartTagLayoutEngine.ComputeClustered(
    [
        Tag(10, 8.0, anchorU: 3.0),
        Tag(11, 6.5, anchorU: 3.2),
        Tag(12, 7.7, anchorU: 7.0),
        Tag(13, 6.2, anchorU: 6.8)
    ],
    [],
    frame,
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true);
Require(twoSided.ClashCount == 0, "Two-sided clustered layout should resolve without clashes.");
Require(twoSided.Placements.Where(item => item.TagKey is 10 or 11).All(item => item.Head.U < item.End.U),
    "Elements on the left of a cluster must use the left tag column.");
Require(twoSided.Placements.Where(item => item.TagKey is 12 or 13).All(item => item.Head.U > item.End.U),
    "Elements on the right of a cluster must use the right tag column.");

var localDuctChanges = new[]
{
    Tag(40, 8.5, anchorU: 3.0) with { PreferLocalClustering = true },
    Tag(41, 1.5, anchorU: 7.0) with { PreferLocalClustering = true }
};
SmartTagLayoutResult localDuctLayout = SmartTagLayoutEngine.ComputeClustered(
    localDuctChanges,
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true);
Require(localDuctLayout.Placements.Count == 0,
    "Duct changes without a nearby Air Terminal/Accessory rail must be omitted instead of creating a distant independent column.");
SmartTagLayoutResult ownerlessV2DuctLayout = SmartTagStandardV2Layout.ComputeClustered(
    localDuctChanges,
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with
    {
        ColumnWidth = 5.0,
        LayoutStyle = SmartTagLayoutStyle.StandardNearHost
    },
    autoSide: true);
Require(ownerlessV2DuctLayout.Placements.Count == localDuctChanges.Length,
    "Density-selected ownerless Duct changes must remain present in Standard V2 instead of disappearing from the write set.");
Require(ownerlessV2DuctLayout.Placements.All(placement =>
        HorizontalHostGap(placement,
            localDuctChanges.Single(input => input.TagKey == placement.TagKey)) <=
        Settings().OffsetFromElements * 1.5 + 1e-8),
    "Every ownerless Standard V2 Duct tag must remain inside its own host-local envelope.");

LayoutTagInput nearbyAccessory = Tag(42, 7.5, anchorU: 3.0) with
{
    Group = "Duct Accessories",
    CanAnchorDuctFollowers = true
};
LayoutTagInput followingDuct = Tag(43, 6.9, anchorU: 3.2) with
{
    Group = "Duct",
    PreferLocalClustering = true
};
SmartTagLayoutResult accessoryOnlyLayout = SmartTagLayoutEngine.ComputeClustered(
    [nearbyAccessory],
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true);
SmartTagLayoutResult followedCategoryLayout = SmartTagLayoutEngine.ComputeClustered(
    [nearbyAccessory, followingDuct],
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true);
TagLayoutPlacement accessoryPlacement = followedCategoryLayout.Placements.Single(item => item.TagKey == 42);
TagLayoutPlacement ductPlacement = followedCategoryLayout.Placements.Single(item => item.TagKey == 43);
TagLayoutPlacement accessoryBaseline = accessoryOnlyLayout.Placements.Single();
Require(Math.Abs(accessoryPlacement.TagBounds.MinU - ductPlacement.TagBounds.MinU) <= 1e-8,
    "A nearby Duct change must follow the established Air Terminal/Accessory text rail.");
Require(ductPlacement.TagBounds.MaxV <= accessoryPlacement.TagBounds.MinV -
        Settings().RowSpacing + 1e-8,
    "A Duct follower must be packed below the established Air Terminal/Accessory stack.");
Require(accessoryPlacement.Head == accessoryBaseline.Head &&
        accessoryPlacement.TagBounds == accessoryBaseline.TagBounds,
    "Adding a Duct follower must not rearrange the established Air Terminal/Accessory tag.");
SmartTagLayoutResult fixedExistingFollower = SmartTagLayoutEngine.ComputeClustered(
    [followingDuct],
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true,
    initialReservations: [accessoryBaseline]);
TagLayoutPlacement fixedExistingDuct = fixedExistingFollower.Placements.Single();
Require(Math.Abs(fixedExistingDuct.TagBounds.MinU - accessoryBaseline.TagBounds.MinU) <= 1e-8 &&
        fixedExistingDuct.TagBounds.MaxV <= accessoryBaseline.TagBounds.MinV -
        Settings().RowSpacing + 1e-8,
    "A Duct must use the current rail of an existing fixed Air Terminal/Accessory tag.");

TagLayoutPlacement remoteTextCompanion = accessoryBaseline with
{
    TagKey = 52,
    Head = new LayoutPoint(-0.8, 7.5),
    End = followingDuct.Anchor,
    Elbow = new LayoutPoint(followingDuct.Anchor.U, 7.5),
    TagBounds = new LayoutRect(-1.5, 7.2, 0.0, 7.7),
    CanAnchorDuctFollowers = true
};
SmartTagLayoutResult remoteTextFollower = SmartTagLayoutEngine.ComputeClustered(
    [followingDuct with { PreferredFollowerAnchorTagKey = 52 }],
    [],
    new LayoutRect(0.0, 0.0, 12.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true,
    initialReservations: [remoteTextCompanion]);
Require(remoteTextFollower.Placements.Count == 0,
    "A nearby companion host with remote tag text must not pull a Duct follower to a distant rail.");

SmartTagLayoutSettings standardV2Settings = Settings() with
{
    ColumnWidth = 5.0,
    OffsetFromElements = 0.2,
    AutoSide = true,
    LayoutStyle = SmartTagLayoutStyle.StandardNearHost
};
SmartTagLayoutResult standardV2Follower = SmartTagStandardV2Layout.ComputeClustered(
    [followingDuct with { PreferredFollowerAnchorTagKey = 52 }],
    [],
    new LayoutRect(-3.0, 0.0, 12.0, 10.0),
    standardV2Settings,
    autoSide: true,
    initialReservations: [remoteTextCompanion]);
TagLayoutPlacement standardV2Duct = standardV2Follower.Placements.Single();
Require(Math.Abs(HorizontalHostGap(standardV2Duct, followingDuct) -
                 standardV2Settings.OffsetFromElements) <= 1e-8,
    $"Standard V2 must reject a remote DA/AT text rail and keep the Duct immediately outside its own host. " +
    $"Duct={standardV2Duct.TagBounds}; companion={remoteTextCompanion.TagBounds}");
Require(standardV2Duct.PreferredFollowerAnchorTagKey == 52,
    "Standard V2 must preserve Duct -> DA/AT ownership through preview and actual-family measurement.");
Require(SmartTagStandardNearHostLayout.CountHardClashes(
        standardV2Follower.Placements,
        [remoteTextCompanion],
        [],
        new LayoutRect(-3.0, 0.0, 12.0, 10.0),
        standardV2Settings.Clearance) == 0,
    "Standard V2 must validate the aligned Duct and DA/AT leader geometry before Write.");

TagLayoutPlacement farGlobalDuctSeed = new(
    followingDuct.TagKey,
    new LayoutPoint(4.75, 1.0),
    followingDuct.Anchor,
    new LayoutPoint(followingDuct.Anchor.U, 1.0),
    new LayoutRect(4.0, 0.75, 5.5, 1.25),
    true, true, false, string.Empty, "Duct")
{
    PreferredFollowerAnchorTagKey = 52
};
SmartTagLayoutResult localRowRepair = SmartTagStandardV2Layout.ComputeClustered(
    [followingDuct with { PreferredFollowerAnchorTagKey = 52 }],
    [],
    new LayoutRect(-3.0, 0.0, 12.0, 10.0),
    standardV2Settings,
    autoSide: true,
    initialReservations: [remoteTextCompanion],
    standardBaseline: new SmartTagLayoutResult(
        [farGlobalDuctSeed], 1, 0, farGlobalDuctSeed.TagBounds));
TagLayoutPlacement localRowDuct = localRowRepair.Placements.Single();
Require(Math.Abs(HorizontalHostGap(localRowDuct, followingDuct) -
                 standardV2Settings.OffsetFromElements) <= 1e-8 &&
        Math.Abs(localRowDuct.Head.V - followingDuct.Anchor.V) < 1.0 &&
        localRowDuct.End == farGlobalDuctSeed.End,
    $"Standard V2 must discard a distant global Standard row and rail, rebuild locally, and retain the Duct host end. " +
    $"actual={localRowDuct}; companion={remoteTextCompanion}");

TagLayoutPlacement standardV2RouteReference = remoteTextCompanion with
{
    TagKey = 55,
    Head = new LayoutPoint(-0.8, 9.5),
    End = new LayoutPoint(7.0, 9.5),
    Elbow = new LayoutPoint(7.0, 9.5),
    TagBounds = new LayoutRect(-1.5, 9.2, 0.0, 9.7),
    CanAnchorDuctFollowers = true
};
LayoutTagInput standardV2NearEnd = Tag(56, 2.0, anchorU: 6.0) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 55
};
LayoutTagInput standardV2FarEnd = Tag(57, 1.5, anchorU: 8.0) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 55
};
SmartTagLayoutSettings standardV2RouteSettings = standardV2Settings with
{
    ColumnWidth = 12.0
};
SmartTagLayoutResult standardV2AutoRoutes = SmartTagStandardV2Layout.ComputeClustered(
    [standardV2NearEnd, standardV2FarEnd],
    [],
    new LayoutRect(-3.0, 0.0, 15.0, 11.0),
    standardV2RouteSettings,
    autoSide: true,
    initialReservations: [standardV2RouteReference]);
SmartTagLayoutResult standardV2AutoBaseline = SmartTagLayoutEngine.ComputeClustered(
    [standardV2NearEnd, standardV2FarEnd],
    [],
    new LayoutRect(-3.0, 0.0, 15.0, 11.0),
    standardV2RouteSettings,
    autoSide: true,
    initialReservations: [standardV2RouteReference]);
TagLayoutPlacement standardV2NearPlacement = standardV2AutoRoutes.Placements
    .Single(item => item.TagKey == 56);
TagLayoutPlacement standardV2FarPlacement = standardV2AutoRoutes.Placements
    .Single(item => item.TagKey == 57);
TagLayoutPlacement standardV2NearBaseline = standardV2AutoBaseline.Placements
    .Single(item => item.TagKey == 56);
TagLayoutPlacement standardV2FarBaseline = standardV2AutoBaseline.Placements
    .Single(item => item.TagKey == 57);
Require(Math.Abs(HorizontalHostGap(standardV2FarPlacement, standardV2FarEnd) -
                 standardV2RouteSettings.OffsetFromElements) <= 1e-8 &&
        Math.Abs(HorizontalHostGap(standardV2NearPlacement, standardV2NearEnd) -
                 standardV2RouteSettings.OffsetFromElements) <= 1e-8 &&
        standardV2NearPlacement.End == standardV2NearBaseline.End &&
        standardV2FarPlacement.End == standardV2FarBaseline.End,
    "Standard V2 must keep each Duct on its own host-local rail and retain both host endpoints.");
Require(SmartTagStandardNearHostLayout.CountHardClashes(
        standardV2AutoRoutes.Placements,
        [standardV2RouteReference],
        [],
        new LayoutRect(-3.0, 0.0, 15.0, 11.0),
        standardV2RouteSettings.Clearance) == 0,
    "Standard V2 row-preserving alignment must not introduce a leader clash against the fixed DA/AT sample.");

LayoutTagInput firstOwnedCompanionInput = Tag(580, 8.0, anchorU: 7.0) with
{
    Group = "Duct Accessory",
    CanAnchorDuctFollowers = true
};
LayoutTagInput secondOwnedCompanionInput = Tag(581, 7.0, anchorU: 7.2) with
{
    Group = "Air Terminal",
    CanAnchorDuctFollowers = true
};
TagLayoutPlacement firstOwnedCompanion = new(
    580, new LayoutPoint(1.75, 8.0), firstOwnedCompanionInput.Anchor,
    new LayoutPoint(7.0, 8.0), new LayoutRect(1.0, 7.75, 2.5, 8.25),
    false, false, false, string.Empty, "Duct Accessory")
{
    CanAnchorDuctFollowers = true
};
TagLayoutPlacement secondOwnedCompanion = new(
    581, new LayoutPoint(1.75, 7.0), secondOwnedCompanionInput.Anchor,
    new LayoutPoint(7.2, 7.0), new LayoutRect(1.0, 6.75, 2.5, 7.25),
    false, false, false, string.Empty, "Air Terminal")
{
    CanAnchorDuctFollowers = true
};
LayoutTagInput firstOwnedDuct = Tag(582, 6.2, anchorU: 7.1) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 580
};
LayoutTagInput secondOwnedDuct = Tag(583, 5.5, anchorU: 7.3) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 581
};
TagLayoutPlacement firstOwnedDuctSeed = new(
    582, new LayoutPoint(2.05, 6.2), firstOwnedDuct.Anchor,
    firstOwnedDuct.Anchor, new LayoutRect(1.30, 5.95, 2.80, 6.45),
    false, false, false, string.Empty, "Duct")
{
    PreferredFollowerAnchorTagKey = 580
};
TagLayoutPlacement secondOwnedDuctSeed = new(
    583, new LayoutPoint(2.15, 5.5), secondOwnedDuct.Anchor,
    secondOwnedDuct.Anchor, new LayoutRect(1.40, 5.25, 2.90, 5.75),
    false, false, false, string.Empty, "Duct")
{
    PreferredFollowerAnchorTagKey = 581
};
SmartTagLayoutResult ownedRailBaseline = new(
    [firstOwnedCompanion, secondOwnedCompanion, firstOwnedDuctSeed, secondOwnedDuctSeed],
    0, 0, new LayoutRect(1.0, 5.25, 2.90, 8.25));
SmartTagLayoutResult ownedRailResult = SmartTagStandardV2Layout.ComputeClustered(
    [firstOwnedCompanionInput, secondOwnedCompanionInput, firstOwnedDuct, secondOwnedDuct],
    [],
    new LayoutRect(0.0, 0.0, 12.0, 10.0),
    standardV2Settings,
    autoSide: true,
    standardBaseline: ownedRailBaseline);
Require(ownedRailResult.Placements.Single(item => item.TagKey == 580) == firstOwnedCompanion &&
        ownedRailResult.Placements.Single(item => item.TagKey == 581) == secondOwnedCompanion,
    "Standard V2 must keep the accepted DA/AT sample stack completely unchanged.");
TagLayoutPlacement firstOwnedDuctResult = ownedRailResult.Placements.Single(item => item.TagKey == 582);
TagLayoutPlacement secondOwnedDuctResult = ownedRailResult.Placements.Single(item => item.TagKey == 583);
Require(Math.Abs(HorizontalHostGap(firstOwnedDuctResult, firstOwnedDuct) -
                 standardV2Settings.OffsetFromElements) <= 1e-8 &&
        Math.Abs(HorizontalHostGap(secondOwnedDuctResult, secondOwnedDuct) -
                 standardV2Settings.OffsetFromElements) <= 1e-8 &&
        firstOwnedDuctResult.Head.V == firstOwnedDuctSeed.Head.V &&
        secondOwnedDuctResult.Head.V == secondOwnedDuctSeed.Head.V &&
        firstOwnedDuctResult.Elbow == firstOwnedDuctSeed.Elbow &&
        secondOwnedDuctResult.Elbow == secondOwnedDuctSeed.Elbow &&
        firstOwnedDuctResult.End == firstOwnedDuctSeed.End &&
        secondOwnedDuctResult.End == secondOwnedDuctSeed.End,
    "Duct followers must stay near their hosts while retaining their Standard rows and leader geometry.");

LayoutTagInput boundedDuctInput = Tag(584, 4.6, anchorU: 7.0) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 580
};
TagLayoutPlacement boundedDuctSeed = new(
    584, new LayoutPoint(4.75, 4.6), boundedDuctInput.Anchor,
    boundedDuctInput.Anchor, new LayoutRect(4.0, 4.35, 5.5, 4.85),
    false, false, false, string.Empty, "Duct")
{
    PreferredFollowerAnchorTagKey = 580
};
SmartTagLayoutResult boundedBaseline = new(
    [firstOwnedCompanion, boundedDuctSeed], 0, 0,
    new LayoutRect(1.0, 4.35, 5.5, 8.25));
SmartTagLayoutResult boundedResult = SmartTagStandardV2Layout.ComputeClustered(
    [firstOwnedCompanionInput, boundedDuctInput],
    [],
    new LayoutRect(0.0, 0.0, 12.0, 10.0),
    standardV2Settings,
    autoSide: true,
    standardBaseline: boundedBaseline);
TagLayoutPlacement boundedPlacement = boundedResult.Placements.Single(item => item.TagKey == 584);
Require(Math.Abs(HorizontalHostGap(boundedPlacement, boundedDuctInput) -
                 standardV2Settings.OffsetFromElements) <= 1e-8 &&
        boundedPlacement.Head.V == boundedDuctSeed.Head.V &&
        boundedPlacement.Elbow == boundedDuctSeed.Elbow &&
        boundedPlacement.End == boundedDuctSeed.End,
    "Standard V2 must retain DA/AT ownership but reject its remote text rail while retaining the Duct leader route.");

var scalableV2Inputs = new List<LayoutTagInput>();
var scalableV2Seed = new List<TagLayoutPlacement>();
for (int area = 0; area < 3; area++)
{
    long companionKey = 7000 + area * 100;
    double hostU = 8.0 + area * 16.0;
    for (int row = 0; row < 10; row++)
    {
        long key = companionKey + row;
        double hostV = 29.0 - row * 0.65;
        scalableV2Inputs.Add(Tag(key, hostV, height: 0.35, anchorU: hostU, width: 1.3) with
        {
            Group = "Duct",
            PreferLocalClustering = true
        });
        scalableV2Seed.Add(new TagLayoutPlacement(
            key,
            new LayoutPoint(hostU - 3.0, hostV),
            new LayoutPoint(hostU, hostV),
            new LayoutPoint(hostU, hostV),
            new LayoutRect(hostU - 3.65, hostV - 0.175, hostU - 2.35, hostV + 0.175),
            false, false, false, string.Empty, "Duct"));
    }
}
var scalableV2Baseline = new SmartTagLayoutResult(
    scalableV2Seed, 0, 0, new LayoutRect(0.0, 0.0, 50.0, 32.0));
SmartTagLayoutSettings scalableV2Settings = standardV2Settings with
{
    ColumnWidth = 6.0,
    RowSpacing = 0.2,
    Clearance = 0.05
};
SmartTagLayoutResult scalableV2 = SmartTagStandardV2Layout.ComputeClustered(
    scalableV2Inputs,
    [],
    new LayoutRect(0.0, 0.0, 50.0, 32.0),
    scalableV2Settings,
    autoSide: true,
    searchCheckLimit: 1,
    standardBaseline: scalableV2Baseline);
Require(scalableV2.Placements.Select(item => item.TagKey).Order().SequenceEqual(
        scalableV2Inputs.Select(item => item.TagKey).Order()),
    "Standard V2 local batching must retain every tag on a full-view solve.");
Require(scalableV2.Diagnostic?.StartsWith("Standard V2 host-local Duct layout:", StringComparison.Ordinal) == true,
    "Standard V2 must use the row-preserving path on a full-view analysis.");
Require(SmartTagStandardNearHostLayout.CountHardClashes(
        scalableV2.Placements, [], [], new LayoutRect(0.0, 0.0, 50.0, 32.0),
        scalableV2Settings.Clearance) <=
    SmartTagStandardNearHostLayout.CountHardClashes(
        scalableV2Baseline.Placements, [], [], new LayoutRect(0.0, 0.0, 50.0, 32.0),
        scalableV2Settings.Clearance),
    "Standard V2 row-preserving alignment must never increase hard clashes over Standard.");

TagLayoutPlacement leftCompanion = accessoryBaseline with
{
    TagKey = 60,
    Head = new LayoutPoint(1.5, 8.0),
    End = new LayoutPoint(3.0, 8.0),
    Elbow = new LayoutPoint(3.0, 8.0),
    TagBounds = new LayoutRect(1.0, 7.75, 2.5, 8.25),
    CanAnchorDuctFollowers = true
};
TagLayoutPlacement rightCompanion = accessoryBaseline with
{
    TagKey = 61,
    Head = new LayoutPoint(9.0, 6.0),
    End = new LayoutPoint(8.0, 6.0),
    Elbow = new LayoutPoint(8.0, 6.0),
    TagBounds = new LayoutRect(8.5, 5.75, 10.0, 6.25),
    CanAnchorDuctFollowers = true
};
LayoutTagInput leftFollower = Tag(62, 7.4, anchorU: 3.0) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 60
};
LayoutTagInput rightFollower = Tag(63, 5.4, anchorU: 8.0) with
{
    Group = "Duct",
    PreferLocalClustering = true,
    PreferredFollowerAnchorTagKey = 61
};
SmartTagLayoutResult individuallyAnchoredFollowers = SmartTagLayoutEngine.ComputeClustered(
    [leftFollower, rightFollower],
    [],
    new LayoutRect(0.0, 0.0, 12.0, 10.0),
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true,
    initialReservations: [leftCompanion, rightCompanion]);
Require(Math.Abs(individuallyAnchoredFollowers.Placements.Single(item => item.TagKey == 62)
                     .TagBounds.MinU - leftCompanion.TagBounds.MinU) <= 1e-8 &&
        Math.Abs(individuallyAnchoredFollowers.Placements.Single(item => item.TagKey == 63)
                     .TagBounds.MinU - rightCompanion.TagBounds.MinU) <= 1e-8,
    "Each Duct cluster must retain its own nearest companion tag instead of sharing a remote rail.");
SmartTagLayoutResult fixedLeftBaseline = SmartTagLeftLayout.Compute(
    [nearbyAccessory],
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 });
SmartTagLayoutResult fixedLeftWithDuct = SmartTagLeftLayout.Compute(
    [nearbyAccessory, followingDuct],
    [],
    new LayoutRect(0.0, 0.0, 9.0, 10.0),
    Settings() with { ColumnWidth = 5.0 });
TagLayoutPlacement fixedLeftAccessory = fixedLeftWithDuct.Placements.Single(item => item.TagKey == 42);
TagLayoutPlacement fixedLeftDuct = fixedLeftWithDuct.Placements.Single(item => item.TagKey == 43);
Require(fixedLeftAccessory.Head == fixedLeftBaseline.Placements.Single().Head &&
        fixedLeftAccessory.TagBounds == fixedLeftBaseline.Placements.Single().TagBounds &&
        Math.Abs(fixedLeftAccessory.TagBounds.MinU - fixedLeftDuct.TagBounds.MinU) <= 1e-8 &&
        fixedLeftDuct.TagBounds.MaxV <= fixedLeftAccessory.TagBounds.MinV -
        Settings().RowSpacing + 1e-8,
    "Fixed Left must preserve the established tag and append the Duct on the same rail below it.");

LayoutTagInput openSideHost = Tag(50, 5.0, anchorU: 5.0);
SmartTagLayoutResult openRightSide = SmartTagLayoutEngine.ComputeClustered(
    [openSideHost],
    [new LayoutObstacle(
        999,
        new LayoutRect(2.6, 4.5, 4.4, 5.5),
        LayoutObstacleKind.Mep)],
    frame,
    Settings(avoidElements: true) with { ColumnWidth = 5.0 },
    autoSide: true);
Require(openRightSide.Placements.Single().Head.U > openRightSide.Placements.Single().End.U,
    "Auto side analysis must use the nearby open right side when left tag text is blocked by MEP.");
SmartTagLayoutResult openLeftSide = SmartTagLayoutEngine.ComputeClustered(
    [openSideHost],
    [new LayoutObstacle(
        998,
        new LayoutRect(5.6, 4.5, 7.4, 5.5),
        LayoutObstacleKind.Mep)],
    frame,
    Settings(avoidElements: true) with { ColumnWidth = 5.0 },
    autoSide: true);
Require(openLeftSide.Placements.Single().Head.U < openLeftSide.Placements.Single().End.U,
    "Auto side analysis must use the nearby open left side when right tag text is blocked by MEP.");

var singleSideInputs = new List<LayoutTagInput>
{
    Tag(900, 8.4, anchorU: 3.0),
    Tag(901, 8.15, anchorU: 7.0),
    Tag(902, 6.9, anchorU: 4.0),
    Tag(903, 6.65, anchorU: 6.5)
};
SmartTagLayoutResult allLeft = SmartTagLeftLayout.Compute(
    singleSideInputs,
    [],
    frame,
    Settings() with { ColumnWidth = 5.0 });
Require(allLeft.Placements.All(item => item.Head.U < item.End.U),
    "All-left mode must place every selected tag on the left side.");
Require(allLeft.Placements.Select(item => Math.Round(item.TagBounds.MinU, 8)).Distinct().Count() == 1,
    "All-left mode must analyze every category on one exact shared rail.");
Require(allLeft.Placements.SelectMany((item, index) =>
        allLeft.Placements.Skip(index + 1).Select(other => (item, other)))
    .All(pair => !pair.item.TagBounds.Intersects(pair.other.TagBounds)),
    "All-left mode must reserve tag text globally across the selected categories.");
Require(allLeft.ClashCount == 0,
    "All-left shared analysis must avoid tag text and leader clashes in the standard case.");

SmartTagLayoutResult allRight = SmartTagRightLayout.Compute(
    singleSideInputs,
    [],
    frame,
    Settings() with { ColumnWidth = 5.0 });
Require(allRight.Placements.All(item => item.Head.U > item.End.U),
    "All-right mode must place every selected tag on the right side.");
Require(allRight.Placements.Select(item => Math.Round(item.TagBounds.MinU, 8)).Distinct().Count() == 1,
    "All-right mode must analyze every category on one exact shared rail.");
Require(allRight.Placements.SelectMany((item, index) =>
        allRight.Placements.Skip(index + 1).Select(other => (item, other)))
    .All(pair => !pair.item.TagBounds.Intersects(pair.other.TagBounds)),
    "All-right mode must reserve tag text globally across the selected categories.");
Require(allRight.ClashCount == 0,
    "All-right shared analysis must avoid tag text and leader clashes in the standard case.");

SmartTagLayoutResult crowdedFallback = SmartTagLayoutEngine.Compute(
    Enumerable.Range(1, 18)
        .Select(index => Tag(100 + index, 1.2 + index * 0.03, height: 0.65))
        .ToList(),
    [],
    frame,
    Settings());
int largestPile = crowdedFallback.Placements
    .GroupBy(item => ($"{item.Head.U:0.00}", $"{item.Head.V:0.00}"))
    .Max(group => group.Count());
Require(largestPile == 1,
    "Unresolved fallback candidates must remain distributed instead of piling into one corner.");

// Some project tag families are taller than the usable crop height after the
// configured top/bottom margin. Analyze must return a warning placement rather
// than aborting the entire preview with Math.Clamp(min > max).
var shortFrame = new LayoutRect(0.0, 0.0, 10.0, 0.6);
SmartTagLayoutResult shortFrameFallback = SmartTagLayoutEngine.Compute(
    [Tag(150, 0.3, height: 0.9)],
    [],
    shortFrame,
    Settings());
Require(shortFrameFallback.Placements.Count == 1 &&
        double.IsFinite(shortFrameFallback.Placements[0].Head.V),
    "A tag taller than the usable crop height must produce a finite fallback placement.");
Require(shortFrameFallback.Placements[0].HasClash,
    "An impossible crop-height placement must remain visible as a clash warning.");

var mostlyRightElements = new List<LayoutTagInput> { Tag(300, 8.8, anchorU: 2.5) };
mostlyRightElements.AddRange(Enumerable.Range(1, 14)
    .Select(index => Tag(300 + index, 8.5 - index * 0.48, anchorU: 6.4)));
SmartTagLayoutResult freeSideBalance = SmartTagLayoutEngine.ComputeClustered(
    mostlyRightElements,
    [],
    frame,
    Settings() with { ColumnWidth = 5.0 },
    autoSide: true);
Require(freeSideBalance.Placements.Single(item => item.TagKey == 300).Head.U <
        freeSideBalance.Placements.Single(item => item.TagKey == 300).End.U,
    "The isolated host near the left edge of a cluster must use the local left rail.");
Require(freeSideBalance.Placements.Count(item => item.TagKey > 300 && item.Head.U > item.End.U) >= 12,
    "Hosts near the right edge must stay on their nearby right rail instead of being load-balanced far left.");

SmartTagLayoutResult firstCategory = SmartTagLayoutEngine.ComputeClustered(
    [Tag(500, 7.5), Tag(501, 6.2)],
    [],
    frame,
    Settings(),
    autoSide: true);
SmartTagLayoutResult addedCategory = SmartTagLayoutEngine.ComputeClustered(
    [Tag(600, 7.5), Tag(601, 6.2)],
    [],
    frame,
    Settings(),
    autoSide: true,
    initialReservations: firstCategory.Placements);
Require(addedCategory.Placements.All(added =>
        firstCategory.Placements.All(existing => !added.TagBounds.Intersects(existing.TagBounds))),
    "A newly selected category must use free rows instead of overlapping the previous category.");

SmartTagLayoutResult firstRail = SmartTagLayoutEngine.ComputeClustered(
    [Tag(700, 8.0, anchorU: 6.8), Tag(701, 6.5, anchorU: 7.0)],
    [],
    frame,
    Settings(),
    autoSide: false);
SmartTagLayoutResult snappedRail = SmartTagLayoutEngine.ComputeClustered(
    [Tag(800, 7.2, anchorU: 7.4)],
    [],
    frame,
    Settings(),
    autoSide: false,
    initialReservations: firstRail.Placements);
double existingInnerEdge = firstRail.Placements[0].TagBounds.MinU;
double addedInnerEdge = snappedRail.Placements[0].TagBounds.MinU;
Require(Math.Abs(existingInnerEdge - addedInnerEdge) < 1e-8,
    "Different tag categories on the same side must snap to one shared text rail.");

SmartTagLayoutResult outwardLocalRails = SmartTagLayoutEngine.ComputeClustered(
    [
        Tag(900, 8.5, anchorU: 2.5),
        Tag(901, 3.0, anchorU: 2.6),
        Tag(902, 8.0, anchorU: 4.2),
        Tag(903, 2.5, anchorU: 4.3)
    ],
    [],
    frame,
    Settings() with { ColumnWidth = 0.5 },
    autoSide: true);
int leftRails = outwardLocalRails.Placements
    .Where(item => item.Head.U < item.End.U)
    .Select(item => Math.Round(item.TagBounds.MinU, 8))
    .Distinct()
    .Count();
Require(leftRails == 2,
    "Horizontally distant host groups must receive separate nearby aligned rails.");
Require(outwardLocalRails.Placements.All(item => Math.Abs(item.Head.U - item.End.U) < 2.0),
    "A local rail must keep each tag a bounded short distance from its own host cluster.");

static IReadOnlyList<LayoutTagInput> CompactFixture(int count, long firstKey = 2000) =>
    Enumerable.Range(0, count)
        .Select(index => Tag(
            firstKey + index,
            anchorV: 8.5 - index * 0.5,
            anchorU: 5.0 + (index % 2) * 0.1))
        .ToList();

static int[] AdaptiveGroupSizes(
    IReadOnlyList<LayoutTagInput> inputs,
    IReadOnlyList<LayoutObstacle> obstacles,
    LayoutRect frame,
    SmartTagLayoutSettings settings) =>
    SmartTagCompactGroupLayout.BuildAdaptiveGroupMap(inputs, obstacles, frame, settings)
        .GroupBy(item => item.Value)
        .Select(group => group.Count())
        .OrderBy(size => size)
        .ToArray();

SmartTagLayoutSettings compactSettings = Settings() with
{
    AutoSide = true,
    LayoutStyle = SmartTagLayoutStyle.CompactGroups
};
IReadOnlyList<SmartTagFreePocket> measuredPockets = SmartTagFreePocketAnalyzer.Analyze(
    CompactFixture(7),
    [new LayoutObstacle(
        ElementKey: -701,
        Bounds: new LayoutRect(2.5, 5.0, 4.5, 8.0),
        Kind: LayoutObstacleKind.Architecture)],
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AvoidElements = true },
    placeLeft: true);
SmartTagFreePocket[] nearestRailPockets = measuredPockets
    .Where(item => item.RailLevel == 0)
    .ToArray();
Require(nearestRailPockets.Length == 2 &&
        nearestRailPockets.Sum(item => item.Capacity) == 12 &&
        nearestRailPockets.All(item => item.TextBand.MaxV <= 4.95 + 1e-8 ||
                                        item.TextBand.MinV >= 8.05 - 1e-8),
    "Free-space analysis must split a blocked rail into measured upper/lower pockets and report safe real-tag capacity.");
int[] openSevenGroups = AdaptiveGroupSizes(
    CompactFixture(7),
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings);
int[] openFiveGroups = AdaptiveGroupSizes(
    CompactFixture(5, firstKey: 2050),
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings);
int[] blockedSevenGroups = AdaptiveGroupSizes(
    CompactFixture(7),
    [new LayoutObstacle(
        ElementKey: -700,
        Bounds: new LayoutRect(0.0, 7.1, 12.0, 7.8),
        Kind: LayoutObstacleKind.Mep)],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AvoidElements = true });
Console.WriteLine(
    $"adaptive groups open={string.Join('+', openSevenGroups)}, blocked={string.Join('+', blockedSevenGroups)}");
Require(openFiveGroups.SequenceEqual(new[] { 5 }) &&
        openSevenGroups.SequenceEqual(new[] { 7 }),
    "Open model space must allow natural groups of five or seven without a fixed four-tag cap.");
Require(blockedSevenGroups.Sum() == 7 &&
        blockedSevenGroups.Length > 1,
    "A constrained model pocket must split the same hosts into capacity-sized local groups.");

SmartTagLayoutResult splitComponentRails = SmartTagCompactGroupLayout.Compute(
    CompactFixture(7, firstKey: 2060),
    [new LayoutObstacle(
        ElementKey: -704,
        Bounds: new LayoutRect(0.0, 7.1, 12.0, 7.8),
        Kind: LayoutObstacleKind.Mep)],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with
    {
        AutoSide = false,
        PlaceLeft = true,
        AvoidElements = true
    });
Require(splitComponentRails.Placements
        .Select(item => Math.Round(item.TagBounds.MinU, 8))
        .Distinct()
        .Count() == 1,
    "Adaptive subgroups cut from one nearby-host component must retain one canonical text rail.");

int[] architectureIgnoredGroups = AdaptiveGroupSizes(
    CompactFixture(7),
    [new LayoutObstacle(
        ElementKey: -702,
        Bounds: new LayoutRect(0.0, 0.0, 12.0, 12.0),
        Kind: LayoutObstacleKind.Architecture)],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AvoidElements = true });
Require(architectureIgnoredGroups.SequenceEqual(openSevenGroups),
    "Adaptive Groups must preserve its MEP layout when an architecture/link obstacle is present.");

var fullHeightMepBlock = new LayoutObstacle(
    ElementKey: -703,
    Bounds: new LayoutRect(2.65, 0.0, 4.45, 12.0),
    Kind: LayoutObstacleKind.Mep);
SmartTagLayoutResult outwardPocketLayout = SmartTagCompactGroupLayout.Compute(
    CompactFixture(3, firstKey: 2070),
    [fullHeightMepBlock],
    new LayoutRect(-5.0, 0.0, 12.0, 12.0),
    compactSettings with
    {
        AutoSide = false,
        PlaceLeft = true,
        AvoidElements = true
    });
Require(outwardPocketLayout.Placements.All(item =>
        item.TagBounds.MaxU < fullHeightMepBlock.Bounds.MinU - compactSettings.Clearance + 1e-8),
    "When the nearest text rail is occupied by MEP, the whole adaptive group must move to the next clear rail.");
for (int first = 0; first < outwardPocketLayout.Placements.Count; first++)
for (int second = first + 1; second < outwardPocketLayout.Placements.Count; second++)
{
    Require(!outwardPocketLayout.Placements[first].TagBounds.Intersects(
            outwardPocketLayout.Placements[second].TagBounds.Expand(compactSettings.Clearance)),
        "A free pocket must never accept overlapping tag text rows.");
}

var compactWithDistantSolo = CompactFixture(4, firstKey: 2100).ToList();
compactWithDistantSolo.Add(Tag(2199, anchorV: 1.0, anchorU: 20.0));
Dictionary<long, int> compactSoloMap =
    SmartTagCompactGrouping.BuildGroupMap(compactWithDistantSolo, compactSettings);
int distantGroup = compactSoloMap[2199];
Require(compactSoloMap.Count(item => item.Value == distantGroup) == 1,
    "A distant host must remain a solo compact group instead of joining a remote stack.");

IReadOnlyList<LayoutTagInput> compactLayoutInputs = CompactFixture(9, firstKey: 2200);
SmartTagLayoutResult compactLayout = SmartTagCompactGroupLayout.Compute(
    compactLayoutInputs,
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings);
Require(compactLayout.Placements.Count == compactLayoutInputs.Count &&
        compactLayout.Placements.Select(item => item.TagKey).Distinct().Count() ==
        compactLayoutInputs.Count,
    "Compact layout must return exactly one placement for every selected tag.");
Dictionary<long, int> compactLayoutMap =
    SmartTagCompactGroupLayout.BuildAdaptiveGroupMap(
        compactLayoutInputs,
        [],
        new LayoutRect(0.0, 0.0, 12.0, 12.0),
        compactSettings);
foreach (IGrouping<int, LayoutTagInput> group in compactLayoutInputs
             .GroupBy(item => compactLayoutMap[item.TagKey]))
{
    Dictionary<long, LayoutTagInput> inputsByKey = group.ToDictionary(item => item.TagKey);
    bool[] sideSigns = compactLayout.Placements
        .Where(item => inputsByKey.ContainsKey(item.TagKey))
        .Select(item => item.Head.U >= inputsByKey[item.TagKey].Anchor.U)
        .Distinct()
        .ToArray();
    Require(sideSigns.Length == 1,
        "Every compact group must remain entirely on one chosen side of its hosts.");
}

SmartTagLayoutResult compactForcedLeft = SmartTagCompactGroupLayout.Compute(
    CompactFixture(4, firstKey: 2300),
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AutoSide = false, PlaceLeft = true });
Require(compactForcedLeft.Placements.All(item => item.Head.U < item.End.U),
    "Adaptive Groups must respect the explicit Left-only mode.");
SmartTagLayoutResult compactForcedRight = SmartTagCompactGroupLayout.Compute(
    CompactFixture(4, firstKey: 2400),
    [],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AutoSide = false, PlaceLeft = false });
Require(compactForcedRight.Placements.All(item => item.Head.U > item.End.U),
    "Adaptive Groups must respect the explicit Right-only mode.");

IReadOnlyList<LayoutTagInput> obstacleChoiceInputs = CompactFixture(3, firstKey: 2500);
SmartTagLayoutResult compactClearSide = SmartTagCompactGroupLayout.Compute(
    obstacleChoiceInputs,
    [new LayoutObstacle(
        ElementKey: -1,
        Bounds: new LayoutRect(2.4, 5.5, 4.5, 9.5),
        Kind: LayoutObstacleKind.Mep)],
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    compactSettings with { AvoidElements = true });
Require(compactClearSide.Placements.All(item => item.Head.U > item.End.U),
    "Compact Auto must keep the whole local stack on the clear side of a model obstacle.");

var guidedInputs = new List<LayoutTagInput>
{
    Tag(3000, 8.2, height: 0.45, width: 1.15),
    Tag(3001, 7.3, height: 0.60, width: 1.85),
    Tag(3002, 6.4, height: 0.50, width: 1.45)
};
var guidedZone = new LayoutRect(0.6, 4.0, 3.4, 9.2);
SmartTagLayoutSettings guidedSettings = Settings(avoidElements: true) with
{
    AutoSide = false,
    PlaceLeft = true,
    LayoutStyle = SmartTagLayoutStyle.GuidedZones,
    GuidedZone = guidedZone,
    RowSpacing = 0.20,
    Clearance = 0.05
};
SmartTagGuidedZoneEvaluation guided = SmartTagGuidedZoneLayout.Compute(
    guidedInputs,
    [],
    frame,
    guidedSettings);
Require(guided.Required == 3 && guided.Capacity >= guided.Required && guided.Fits,
    "Guided Zones must report the picked area's row capacity before writing tags.");
Require(guided.Layout.Placements.All(item =>
        item.TagBounds.MinU >= guidedZone.MinU - 1e-8 &&
        item.TagBounds.MaxU <= guidedZone.MaxU + 1e-8 &&
        item.TagBounds.MinV >= guidedZone.MinV - 1e-8 &&
        item.TagBounds.MaxV <= guidedZone.MaxV + 1e-8),
    "Guided tag text must never spill outside the user-picked empty zone.");
Require(guided.Layout.Placements
        .OrderByDescending(item => item.Head.V)
        .Select(item => item.TagKey)
        .SequenceEqual(guidedInputs.OrderByDescending(item => item.Anchor.V).Select(item => item.TagKey)),
    "Guided Zones must preserve deterministic host top-to-bottom order.");
Require(guided.Layout.Placements
        .Select(item => Math.Round(item.TagBounds.MinU, 8))
        .Distinct()
        .Count() == 1,
    "A picked zone must align all project tag left edges to one rail.");
double[] guidedRows = guided.Layout.Placements
    .Select(item => item.Head.V)
    .OrderByDescending(value => value)
    .ToArray();
Require(guidedRows.Zip(guidedRows.Skip(1), (upper, lower) => Math.Round(upper - lower, 8))
        .Distinct()
        .Count() == 1,
    "A Guided column must use one uniform real-family row pitch without ragged skipped rows.");

var tightGuidedZone = new LayoutRect(0.6, 7.0, 3.4, 8.0);
SmartTagGuidedZoneEvaluation forcedTightGuided = SmartTagGuidedZoneLayout.Compute(
    guidedInputs,
    [],
    frame,
    guidedSettings with { GuidedZone = tightGuidedZone });
Require(!forcedTightGuided.Fits &&
        forcedTightGuided.Layout.Placements.Count == guidedInputs.Count,
    "A guided zone without enough natural rows must retain and force every scanned tag.");
Require(forcedTightGuided.Layout.Placements.All(item =>
        item.TagBounds.MinU >= tightGuidedZone.MinU - 1e-8 &&
        item.TagBounds.MaxU <= tightGuidedZone.MaxU + 1e-8 &&
        item.TagBounds.MinV >= tightGuidedZone.MinV - 1e-8 &&
        item.TagBounds.MaxV <= tightGuidedZone.MaxV + 1e-8),
    "Forced Guided packing must keep every real tag rectangle inside the picked zone.");

SmartTagGuidedZoneEvaluation forcedBlockedGuided = SmartTagGuidedZoneLayout.Compute(
    guidedInputs,
    [new LayoutObstacle(-3300, guidedZone, LayoutObstacleKind.Mep)],
    frame,
    guidedSettings);
Require(forcedBlockedGuided.Layout.Placements.Count == guidedInputs.Count &&
        forcedBlockedGuided.Layout.ClashCount > 0,
    "A model-blocked manual zone must keep every tag and report warnings instead of requesting another scan.");

var splitGuidedInputs = new List<LayoutTagInput>
{
    Tag(3100, 8.2, anchorU: 3.0),
    Tag(3101, 7.2, anchorU: 7.0),
    Tag(3102, 6.2, anchorU: 3.2),
    Tag(3103, 5.2, anchorU: 6.8)
};
SmartTagGuidedZoneEvaluation splitGuided = SmartTagGuidedZoneLayout.Compute(
    splitGuidedInputs,
    [],
    frame,
    guidedSettings with
    {
        AutoSide = true,
        GuidedZone = new LayoutRect(0.5, 3.8, 9.5, 9.2)
    });
Require(splitGuided.Layout.Placements.Any(item => item.Head.U < item.End.U) &&
        splitGuided.Layout.Placements.Any(item => item.Head.U > item.End.U),
    "Guided Auto must split scanned hosts between left and right columns when the picked zone spans both sides.");
Require(splitGuided.Layout.Placements
        .GroupBy(item => item.Head.U < item.End.U)
        .All(column => column
            .Select(item => Math.Round(item.TagBounds.MinU, 8))
            .Distinct()
            .Count() == 1),
    "Each Guided left/right text column must keep one exact visible left edge.");
Require(splitGuided.Layout.Placements
        .GroupBy(item => item.Head.U < item.End.U)
        .Select(column => string.Join("|", column
            .Select(item => Math.Round(item.Head.V, 8))
            .OrderByDescending(value => value)))
        .Distinct()
        .Count() == 1,
    "Balanced Guided left/right columns must share the same aligned row levels.");
Require(splitGuided.Layout.Placements
        .SelectMany((first, index) => splitGuided.Layout.Placements
            .Skip(index + 1)
            .Select(second => first.TagBounds.Intersects(second.TagBounds)))
        .All(overlap => !overlap),
    "Guided left/right columns must not overlap tag text.");
Require(splitGuided.Layout.Placements.All(item =>
        Math.Abs(item.Head.V - item.Elbow.V) <= 1e-8 &&
        (!item.UsesElbow || Math.Abs(item.End.U - item.Elbow.U) <= 1e-8)),
    "Guided leaders must remain exactly horizontal or orthogonal after Free End repair.");

var leftBiasedGuidedInputs = new List<LayoutTagInput>
{
    Tag(3150, 8.2, anchorU: 3.0),
    Tag(3151, 7.2, anchorU: 3.1),
    Tag(3152, 6.2, anchorU: 3.2),
    Tag(3153, 5.2, anchorU: 3.3)
};
SmartTagGuidedZoneEvaluation balancedAutoGuided = SmartTagGuidedZoneLayout.Compute(
    leftBiasedGuidedInputs,
    [],
    frame,
    guidedSettings with
    {
        AutoSide = true,
        GuidedZone = new LayoutRect(0.5, 3.8, 9.5, 9.2)
    });
Require(balancedAutoGuided.Layout.Placements.Any(item => item.Head.U < item.End.U) &&
        balancedAutoGuided.Layout.Placements.Any(item => item.Head.U > item.End.U),
    "Guided Auto must retain both left and right columns even when every host is closer to one side.");

var suggestedInputs = new List<LayoutTagInput>
{
    Tag(3200, 9.0, height: 0.45, anchorU: 5.0, width: 1.20),
    Tag(3201, 8.2, height: 0.60, anchorU: 5.2, width: 1.80),
    Tag(3202, 7.4, height: 0.50, anchorU: 4.9, width: 1.35),
    Tag(3203, 6.6, height: 0.55, anchorU: 5.1, width: 1.60),
    Tag(3204, 5.8, height: 0.45, anchorU: 5.0, width: 1.10)
};
var suggestedObstacles = suggestedInputs
    .Select(item => new LayoutObstacle(
        item.ElementKey,
        item.ElementBounds,
        LayoutObstacleKind.Mep))
    .ToList();
SmartTagGuidedZoneSuggestion suggestedRight = SmartTagGuidedZoneLayout.SuggestZone(
    suggestedInputs,
    suggestedObstacles,
    new LayoutRect(0.0, 0.0, 12.0, 12.0),
    guidedSettings with
    {
        AutoSide = false,
        PlaceLeft = false,
        GuidedZone = null
    });
Require(suggestedRight.Evaluation.Required == suggestedInputs.Count &&
        suggestedRight.Evaluation.Capacity >= suggestedInputs.Count &&
        suggestedRight.Evaluation.Layout.Placements.Count == suggestedInputs.Count,
    "Automatic Guided-zone search must reserve enough rows for every scanned tag.");
Require(suggestedRight.Evaluation.Layout.Placements.All(item =>
        item.TagBounds.MinU >= suggestedRight.Zone.MinU - 1e-8 &&
        item.TagBounds.MaxU <= suggestedRight.Zone.MaxU + 1e-8 &&
        item.TagBounds.MinV >= suggestedRight.Zone.MinV - 1e-8 &&
        item.TagBounds.MaxV <= suggestedRight.Zone.MaxV + 1e-8),
    "An automatically suggested zone must contain every real tag-family rectangle.");
Require(suggestedRight.Evaluation.Layout.Placements.All(item => item.Head.U > item.End.U),
    "A Right-only automatic suggestion must keep every tag on the requested side.");
Require(suggestedRight.Evaluation.Layout.Placements
        .SelectMany((first, index) => suggestedRight.Evaluation.Layout.Placements
            .Skip(index + 1)
            .Select(second => first.TagBounds.Intersects(second.TagBounds)))
        .All(overlap => !overlap),
    "Automatic Guided-zone rows must not overlap tag text.");
Require(suggestedRight.Evaluation.Layout.Placements.All(item =>
        Math.Abs(item.Head.V - item.Elbow.V) <= 1e-8 &&
        (!item.UsesElbow || Math.Abs(item.End.U - item.Elbow.U) <= 1e-8)),
    "Automatic Guided-zone leaders must remain horizontal or exactly 90 degrees.");

Console.WriteLine("SmartTag layout smoke tests passed.");
return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"SmartTag layout smoke tests failed: {exception.Message}");
    return 1;
}
