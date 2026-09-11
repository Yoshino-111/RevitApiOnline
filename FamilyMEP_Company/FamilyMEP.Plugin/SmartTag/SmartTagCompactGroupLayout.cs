namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Space-adaptive annotation policy. Nearby hosts start as one proximity
/// component, then the component grows one tag at a time while the real model
/// obstacles and all previously accepted tags remain clear. A tight pocket may
/// therefore contain two tags while an open pocket can naturally contain five,
/// seven, or more. The accepted Standard solver still owns all leader geometry.
/// </summary>
internal static class SmartTagCompactGroupLayout
{
    private sealed record PlannedGroup(
        IReadOnlyList<LayoutTagInput> Inputs,
        SmartTagLayoutResult Layout);

    public static SmartTagLayoutResult Compute(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        if (source.Count == 0)
        {
            return new SmartTagLayoutResult([], 0, 0, frame);
        }

        List<PlannedGroup> plan = BuildPlan(source, obstacles, frame, settings);
        List<TagLayoutPlacement> placements = plan
            .SelectMany(group => group.Layout.Placements)
            .OrderByDescending(item => item.End.V)
            .ThenBy(item => item.Head.U)
            .ThenBy(item => item.TagKey)
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

    /// <summary>
    /// Returns the exact adaptive groups used by the layout pass. The actual
    /// Revit-family packing stage calls this again with measured tag sizes so a
    /// preview group is not later merged into a fixed-size or global column.
    /// </summary>
    public static Dictionary<long, int> BuildAdaptiveGroupMap(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        var result = new Dictionary<long, int>(source.Count);
        int groupId = 0;
        foreach (PlannedGroup group in BuildPlan(source, obstacles, frame, settings))
        {
            foreach (LayoutTagInput input in group.Inputs)
            {
                result[input.TagKey] = groupId;
            }
            groupId++;
        }
        return result;
    }

    private static List<PlannedGroup> BuildPlan(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        // Adaptive Groups is a MEP annotation-space solver. Architecture is
        // intentionally excluded: a linked wall must not repartition a layout
        // that was already good without the link. Real MEP geometry still
        // reserves text space and is a hard constraint.
        IReadOnlyList<LayoutObstacle> layoutObstacles = obstacles
            .Where(item => item.Kind == LayoutObstacleKind.Mep)
            .ToList();
        Dictionary<long, LayoutTagInput> byKey = source.ToDictionary(item => item.TagKey);
        IReadOnlyList<IReadOnlyList<CompactTagGroupingInput>> components =
            SmartTagCompactGrouping.BuildComponents(
                source.Select(item => new CompactTagGroupingInput(
                        item.TagKey,
                        item.Anchor,
                        item.ElementBounds,
                        item.TagWidth,
                        item.TagHeight))
                    .ToList(),
                settings);

        var plan = new List<PlannedGroup>();
        var acceptedPlacements = new List<TagLayoutPlacement>(source.Count);
        foreach (IReadOnlyList<CompactTagGroupingInput> component in components)
        {
            List<LayoutTagInput> ordered = component
                .Select(item => byKey[item.TagKey])
                .OrderByDescending(item => item.Anchor.V)
                .ThenBy(item => item.Anchor.U)
                .ThenBy(item => item.TagKey)
                .ToList();
            // Lock the rail system before looking for pockets. All adaptive
            // subgroups belonging to this spatial component share these two
            // canonical text edges. A secondary rail may only be created as a
            // parallel outward overflow rail by SmartTagFreePocketAnalyzer.
            // This makes the final drawing read top-to-bottom instead of as a
            // collection of unrelated local optimisations.
            double componentTagWidth = ordered.Max(item => Math.Max(item.TagWidth, 0.01));
            double minimumColumnLeft = frame.MinU + settings.TopMargin;
            double maximumColumnLeft = frame.MaxU - settings.TopMargin - componentTagWidth;
            double ClampColumn(double candidate) => maximumColumnLeft >= minimumColumnLeft
                ? Math.Clamp(candidate, minimumColumnLeft, maximumColumnLeft)
                : frame.MinU;
            double leftCanonicalColumn = ClampColumn(
                ordered.Min(item => item.ElementBounds.MinU) -
                settings.OffsetFromElements - componentTagWidth);
            double rightCanonicalColumn = ClampColumn(
                ordered.Max(item => item.ElementBounds.MaxU) + settings.OffsetFromElements);
            int offset = 0;
            while (offset < ordered.Count)
            {
                int remaining = ordered.Count - offset;
                var cache = new Dictionary<int,
                    (List<LayoutTagInput> Inputs, SmartTagLayoutResult Layout)>();
                (List<LayoutTagInput> Inputs, SmartTagLayoutResult Layout) Evaluate(int size)
                {
                    if (cache.TryGetValue(size, out var known)) return known;
                    List<LayoutTagInput> inputs = ordered
                        .Skip(offset)
                        .Take(size)
                        .ToList();
                    SmartTagLayoutResult layout = SolveAdaptiveCandidate(
                        inputs,
                        layoutObstacles,
                        frame,
                        settings,
                        acceptedPlacements,
                        leftCanonicalColumn,
                        rightCanonicalColumn);
                    var evaluated = (inputs, layout);
                    cache[size] = evaluated;
                    return evaluated;
                }

                var selected = Evaluate(1);
                int largestFit = 1;
                int failedUpper = 0;
                int probe = Math.Min(2, remaining);
                // Exponential probing keeps large open components fast and has
                // no fixed tag-count limit: 2, 4, 8, 16... up to the component.
                while (largestFit < remaining)
                {
                    var candidate = Evaluate(probe);
                    if (!FitsLocalFreePocket(
                            candidate.Inputs,
                            candidate.Layout,
                            frame,
                            settings,
                            layoutObstacles,
                            acceptedPlacements))
                    {
                        failedUpper = probe;
                        break;
                    }
                    selected = candidate;
                    largestFit = probe;
                    if (largestFit == remaining) break;
                    probe = Math.Min(remaining, largestFit * 2);
                }

                // Refine only the small interval around the first blocked size.
                int low = largestFit + 1;
                int high = failedUpper - 1;
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    var candidate = Evaluate(middle);
                    if (FitsLocalFreePocket(
                            candidate.Inputs,
                            candidate.Layout,
                            frame,
                            settings,
                            layoutObstacles,
                            acceptedPlacements))
                    {
                        selected = candidate;
                        largestFit = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                plan.Add(new PlannedGroup(selected.Inputs, selected.Layout));
                acceptedPlacements.AddRange(selected.Layout.Placements);
                offset += selected.Inputs.Count;
            }
        }
        return plan;
    }

    private static SmartTagLayoutResult SolveAdaptiveCandidate(
        IReadOnlyList<LayoutTagInput> group,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement> reservations,
        double leftCanonicalColumn,
        double rightCanonicalColumn)
    {
        if (!settings.AutoSide)
        {
            return SolveInBestFreePocket(
                group,
                obstacles,
                frame,
                settings,
                settings.PlaceLeft,
                reservations,
                settings.PlaceLeft ? leftCanonicalColumn : rightCanonicalColumn);
        }

        SmartTagLayoutResult left = SolveInBestFreePocket(
            group,
            obstacles,
            frame,
            settings,
            placeLeft: true,
            reservations,
            leftCanonicalColumn);
        SmartTagLayoutResult right = SolveInBestFreePocket(
            group,
            obstacles,
            frame,
            settings,
            placeLeft: false,
            reservations,
            rightCanonicalColumn);
        int leftTextConflicts = HardTextConflictCount(
            left.Placements,
            obstacles,
            reservations,
            settings);
        int rightTextConflicts = HardTextConflictCount(
            right.Placements,
            obstacles,
            reservations,
            settings);
        if (leftTextConflicts != rightTextConflicts)
        {
            return leftTextConflicts < rightTextConflicts ? left : right;
        }
        return SelectAutoCandidate(left, right, settings.PlaceLeft);
    }

    private static SmartTagLayoutResult SolveInBestFreePocket(
        IReadOnlyList<LayoutTagInput> group,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool placeLeft,
        IReadOnlyList<TagLayoutPlacement> reservations,
        double canonicalColumnLeft)
    {
        IReadOnlyList<SmartTagFreePocket> pockets = SmartTagFreePocketAnalyzer.Analyze(
            group,
            obstacles,
            reservations,
            frame,
            settings,
            placeLeft,
            canonicalColumnLeft);
        SmartTagLayoutResult? best = null;
        double bestScore = double.MaxValue;
        int textClearPocketsTested = 0;
        foreach (SmartTagFreePocket pocket in pockets.Where(item => item.Capacity >= group.Count))
        {
            LayoutRect pocketFrame = SmartTagFreePocketAnalyzer.CreateSolverFrame(
                pocket,
                frame,
                settings);
            SmartTagLayoutResult candidate = SolveOnSide(
                group,
                obstacles,
                pocketFrame,
                settings,
                placeLeft,
                reservations,
                pocket.TextBand.MinU);
            int hardTextConflicts = HardTextConflictCount(
                candidate.Placements,
                obstacles,
                reservations,
                settings);
            double score = hardTextConflicts * 1_000_000_000.0 +
                           candidate.ClashCount * 1_000_000.0 +
                           candidate.Placements.Count(item => item.HasClash) * 1_000_000.0 +
                           pocket.DistanceFromHosts * 10.0 +
                           TotalLeaderLength(candidate.Placements);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
            if (hardTextConflicts == 0) textClearPocketsTested++;
            if (hardTextConflicts == 0 &&
                candidate.ClashCount == 0 &&
                candidate.Placements.All(item => !item.HasClash))
            {
                // Pockets are ordered nearest-first. The first completely clear
                // result is therefore the nearest safe real-tag capacity.
                break;
            }
            if (textClearPocketsTested >= 3)
            {
                // Text safety and capacity are hard requirements. Compare a
                // few nearby safe routes for leader quality, but never scan a
                // whole view worth of distant rails for one local group.
                break;
            }
        }

        return best ?? SolveOnSide(
            group,
            obstacles,
            frame,
            settings,
            placeLeft,
            reservations,
            canonicalColumnLeft);
    }

    private static bool FitsLocalFreePocket(
        IReadOnlyList<LayoutTagInput> inputs,
        SmartTagLayoutResult layout,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<LayoutObstacle> obstacles,
        IReadOnlyList<TagLayoutPlacement> reservations)
    {
        if (layout.Placements.Count != inputs.Count ||
            HardTextConflictCount(layout.Placements, obstacles, reservations, settings) > 0)
        {
            return false;
        }

        double rowPitch = Math.Max(
            inputs.Max(item => Math.Max(item.TagHeight, 0.005)) + settings.RowSpacing,
            0.01);
        // Normal stacking needs more vertical travel as the group grows. Only
        // travel beyond that natural allowance indicates that the solver had to
        // escape a blocked pocket and should begin a new local group instead.
        double naturalTravelAllowance = rowPitch * Math.Max(1.5, inputs.Count * 0.65);
        Dictionary<long, LayoutTagInput> byKey = inputs.ToDictionary(item => item.TagKey);
        foreach (TagLayoutPlacement placement in layout.Placements)
        {
            LayoutTagInput input = byKey[placement.TagKey];
            if (Math.Abs(placement.Head.V - input.Anchor.V) > naturalTravelAllowance + 1e-8)
            {
                return false;
            }
            if (placement.TagBounds.MinV < frame.MinV - 1e-8 ||
                placement.TagBounds.MaxV > frame.MaxV + 1e-8)
            {
                return false;
            }
        }
        return true;
    }

    private static SmartTagLayoutResult SolveOnSide(
        IReadOnlyList<LayoutTagInput> group,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool placeLeft,
        IReadOnlyList<TagLayoutPlacement> reservations,
        double? forcedColumnLeft = null) =>
        SmartTagLayoutEngine.ComputeClustered(
            group,
            obstacles,
            frame,
            settings with
            {
                PlaceLeft = placeLeft,
                AutoSide = false
            },
            autoSide: false,
            initialReservations: reservations,
            forcedColumnLeft: forcedColumnLeft);

    private static int HardTextConflictCount(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<LayoutObstacle> obstacles,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
    {
        int conflicts = 0;
        double clearance = Math.Max(settings.Clearance, 0.0);
        for (int index = 0; index < placements.Count; index++)
        {
            LayoutRect bounds = placements[index].TagBounds;
            if (settings.AvoidElements)
            {
                conflicts += obstacles.Count(obstacle =>
                    obstacle.Kind == LayoutObstacleKind.Mep &&
                    bounds.Intersects(obstacle.Bounds.Expand(clearance)));
            }
            if (settings.AvoidTagText)
            {
                conflicts += reservations.Count(reservation =>
                    bounds.Intersects(reservation.TagBounds.Expand(clearance)));
                for (int other = index + 1; other < placements.Count; other++)
                {
                    if (bounds.Intersects(
                            placements[other].TagBounds.Expand(clearance)))
                    {
                        conflicts++;
                    }
                }
            }
        }
        return conflicts;
    }

    private static SmartTagLayoutResult SelectAutoCandidate(
        SmartTagLayoutResult left,
        SmartTagLayoutResult right,
        bool preferLeftOnExactTie)
    {
        if (left.ClashCount != right.ClashCount)
        {
            return left.ClashCount < right.ClashCount ? left : right;
        }

        double leftLength = TotalLeaderLength(left.Placements);
        double rightLength = TotalLeaderLength(right.Placements);
        if (Math.Abs(leftLength - rightLength) > 1e-9)
        {
            return leftLength < rightLength ? left : right;
        }
        return preferLeftOnExactTie ? left : right;
    }

    private static double TotalLeaderLength(IEnumerable<TagLayoutPlacement> placements) =>
        placements.Sum(item =>
            Distance(item.Head, item.Elbow) +
            (item.UsesElbow ? Distance(item.Elbow, item.End) : 0.0));

    private static double Distance(LayoutPoint first, LayoutPoint second) =>
        Math.Abs(first.U - second.U) + Math.Abs(first.V - second.V);
}
