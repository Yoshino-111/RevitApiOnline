namespace FamilyMEP.Plugin.SmartTag;

// Isolated Standard experiment requested for field testing. The accepted
// Standard engine remains untouched. V2 keeps DA/AT and every Duct host end
// fixed, slides Duct text onto its host-nearest DA/AT rail, and may reorder only
// a small local Duct row group to untangle orthogonal leaders. A proposal is
// accepted only when neither hard clashes nor leader clashes increase.
internal static class SmartTagStandardV2Layout
{
    private const int MaximumBatchSize = 10;

    public static SmartTagLayoutResult ComputeClustered(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool autoSide,
        IReadOnlyList<TagLayoutPlacement>? initialReservations = null,
        double? forcedColumnLeft = null,
        int searchCheckLimit = 1000000,
        SmartTagLayoutResult? standardBaseline = null)
    {
        IReadOnlyList<TagLayoutPlacement> fixedTags = initialReservations ?? [];
        // A density-selected Duct without a local DA/AT owner must still be
        // present in V2. Accepted Standard intentionally omits ownerless Duct
        // followers, so only for this isolated V2 baseline treat those records
        // as ordinary Standard tags. If a visible companion is later found,
        // the alignment pass can still adopt its rail.
        IReadOnlyList<LayoutTagInput> baselineSource = source
            .Select(item => item.PreferLocalClustering &&
                            item.PreferredFollowerAnchorTagKey == 0
                ? item with { PreferLocalClustering = false }
                : item)
            .ToList();
        SmartTagLayoutResult baseline = standardBaseline ??
            SmartTagLayoutEngine.ComputeClustered(
                baselineSource,
                obstacles,
                frame,
                settings,
                autoSide,
                fixedTags,
                forcedColumnLeft);
        SmartTagLayoutResult alignedProposal = AlignDuctFollowersOnExistingRows(
            source,
            baseline,
            fixedTags,
            settings);
        Dictionary<long, TagLayoutPlacement> proposalByKey = alignedProposal.Placements
            .ToDictionary(item => item.TagKey);
        Dictionary<long, TagLayoutPlacement> baselineByKey = baseline.Placements
            .ToDictionary(item => item.TagKey);
        List<LayoutTagInput> movableDucts = source
            .Where(item => item.PreferLocalClustering &&
                proposalByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? proposal) &&
                baselineByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? original) &&
                (Math.Abs(proposal.Head.U - original.Head.U) > 1e-7 ||
                 Math.Abs(proposal.Head.V - original.Head.V) > 1e-7))
            .ToList();
        if (movableDucts.Count == 0)
        {
            return baseline with
            {
                Diagnostic = "Standard V2 local rail align: Standard leader rows/endpoints retained; " +
                    "no Duct tag had a nearby visible DA/AT rail to join."
            };
        }

        // Evaluate one local rail group at a time. Each group has two bounded
        // proposals: keep its existing rows, or repack/reorder only those rows
        // around the same local center. The latter uses the accepted AUTO route
        // ordering to remove leader crossings without sending the stack to a
        // distant top/bottom boundary.
        List<TagLayoutPlacement> proposedDuctPlacements = movableDucts
            .Select(item => proposalByKey[item.TagKey])
            .ToList();
        List<List<LayoutTagInput>> batches = BuildLocalBatches(
            movableDucts, proposedDuctPlacements, settings);
        var accepted = baseline.Placements.ToList();
        int standardHard = SmartTagStandardNearHostLayout.CountHardClashes(
            accepted, fixedTags, obstacles, frame, settings.Clearance);
        int standardLeaderClashes = SmartTagStandardNearHostLayout.CountLeaderClashes(
            accepted, fixedTags, settings.Clearance);
        int acceptedGroups = 0;
        int acceptedDucts = 0;
        int reorderedDucts = 0;

        foreach (List<LayoutTagInput> batch in batches)
        {
            HashSet<long> keys = batch.Select(item => item.TagKey).ToHashSet();
            List<TagLayoutPlacement> currentBatch = accepted
                .Where(item => keys.Contains(item.TagKey))
                .ToList();
            if (currentBatch.Count == 0) continue;
            List<TagLayoutPlacement> reservations = accepted
                .Where(item => !keys.Contains(item.TagKey))
                .Concat(fixedTags)
                .GroupBy(item => item.TagKey)
                .Select(group => group.First())
                .ToList();
            List<TagLayoutPlacement> horizontalBatch = currentBatch
                .Select(item => proposalByKey.GetValueOrDefault(item.TagKey, item))
                .ToList();
            Dictionary<long, TagLayoutPlacement> localTargets = horizontalBatch
                .ToDictionary(item => item.TagKey);

            int currentLocalHard = SmartTagStandardNearHostLayout.CountHardClashes(
                currentBatch, reservations, obstacles, frame, settings.Clearance);
            int currentLocalLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                currentBatch, reservations, settings.Clearance);
            var candidates = new List<(string Name,
                IReadOnlyList<TagLayoutPlacement> Placements,
                int Hard, int Leaders, double VerticalTravel, int Priority)>
            {
                ScoreBatch("horizontal", horizontalBatch, priority: 0),
                (Name: "current", Placements: currentBatch,
                    Hard: currentLocalHard, Leaders: currentLocalLeaders,
                    VerticalTravel: currentBatch.Sum(item =>
                        localTargets.TryGetValue(item.TagKey,
                            out TagLayoutPlacement? target)
                            ? Math.Abs(item.Head.V - target.Head.V)
                            : 0.0), Priority: 2)
            };
            double averagePitch = horizontalBatch.Average(item => item.TagBounds.Height) +
                Math.Max(settings.RowSpacing, settings.Clearance + 1e-7);
            double maximumCenterShift = Math.Min(
                settings.ColumnWidth * 0.25,
                averagePitch * 3.0);
            var testedOffsets = new HashSet<long>();
            int orderedPriority = 10;
            for (int step = 0; step <= 3; step++)
            {
                foreach (int direction in step == 0 ? new[] { 0 } : new[] { -1, 1 })
                {
                    double offset = direction * Math.Min(step * averagePitch, maximumCenterShift);
                    long offsetKey = (long)Math.Round(offset / 1e-6);
                    if (!testedOffsets.Add(offsetKey)) continue;
                    List<TagLayoutPlacement> ordered = CreateLocallyOrderedBatch(
                        horizontalBatch, frame, settings, offset);
                    candidates.Add(ScoreBatch($"ordered:{offset:F4}", ordered,
                        priority: orderedPriority++));
                }
            }
            var best = candidates
                // Never buy alignment by adding a text/model/annotation clash
                // or by adding a leader-line conflict to this local group.
                .Where(item => item.Hard <= currentLocalHard &&
                               item.Leaders <= currentLocalLeaders)
                .OrderBy(item => item.Leaders)
                .ThenBy(item => item.Hard)
                .ThenBy(item => item.VerticalTravel)
                .ThenBy(item => item.Priority)
                .First();
            if (best.Name == "current")
            {
                int individualAccepted = 0;
                foreach (TagLayoutPlacement horizontal in horizontalBatch
                             .OrderByDescending(item => item.End.V)
                             .ThenBy(item => item.End.U))
                {
                    int currentIndex = accepted.FindIndex(item =>
                        item.TagKey == horizontal.TagKey);
                    if (currentIndex < 0) continue;
                    TagLayoutPlacement current = accepted[currentIndex];
                    TagLayoutPlacement individual = FindBestSingleAlignment(
                        current, horizontal, accepted, fixedTags, obstacles,
                        frame, settings);
                    if (individual == current) continue;
                    accepted[currentIndex] = individual;
                    individualAccepted++;
                    acceptedDucts++;
                    if (baselineByKey.TryGetValue(individual.TagKey,
                            out TagLayoutPlacement? original) &&
                        Math.Abs(individual.Head.V - original.Head.V) > 1e-7)
                        reorderedDucts++;
                }
                if (individualAccepted > 0) acceptedGroups++;
                continue;
            }

            Dictionary<long, TagLayoutPlacement> replacements = best.Placements
                .ToDictionary(item => item.TagKey);
            accepted = accepted
                .Select(item => replacements.GetValueOrDefault(item.TagKey, item))
                .ToList();
            acceptedGroups++;
            acceptedDucts += keys.Count;
            reorderedDucts += best.Placements.Count(item =>
                baselineByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? original) &&
                Math.Abs(item.Head.V - original.Head.V) > 1e-7);

            (string Name, IReadOnlyList<TagLayoutPlacement> Placements,
                int Hard, int Leaders, double VerticalTravel, int Priority) ScoreBatch(
                    string name,
                    IReadOnlyList<TagLayoutPlacement> candidate,
                    int priority) =>
                (name,
                    candidate,
                    SmartTagStandardNearHostLayout.CountHardClashes(
                        candidate, reservations, obstacles, frame, settings.Clearance),
                    SmartTagStandardNearHostLayout.CountLeaderClashes(
                        candidate, reservations, settings.Clearance),
                    candidate.Sum(item => localTargets.TryGetValue(
                            item.TagKey, out TagLayoutPlacement? target)
                        ? Math.Abs(item.Head.V - target.Head.V)
                        : 0.0),
                    priority);
        }

        accepted = RepairAlignedDuctRows(
            accepted,
            movableDucts.Select(item => item.TagKey).ToHashSet(),
            proposalByKey,
            fixedTags,
            obstacles,
            frame,
            settings);
        accepted = SnapNearlyAlignedDuctRails(
            accepted,
            movableDucts.Select(item => item.TagKey).ToHashSet(),
            fixedTags,
            obstacles,
            frame,
            settings,
            out int exactRailSnaps);
        accepted = OptimizeAlignedDuctLeaderOrder(
            accepted,
            movableDucts.Select(item => item.TagKey).ToHashSet(),
            fixedTags,
            obstacles,
            frame,
            settings,
            out int leaderOrderSwaps);
        Dictionary<long, TagLayoutPlacement> finalByKey = accepted
            .ToDictionary(item => item.TagKey);
        HashSet<long> finalPreferredKeys = accepted
            .Where(item => item.PreferredFollowerAnchorTagKey != 0)
            .Select(item => item.PreferredFollowerAnchorTagKey)
            .ToHashSet();
        Dictionary<long, TagLayoutPlacement> finalAnchors = baseline.Placements
            .Concat(fixedTags)
            .Where(item => item.CanAnchorDuctFollowers ||
                           finalPreferredKeys.Contains(item.TagKey))
            .GroupBy(item => item.TagKey)
            .ToDictionary(group => group.Key, group => group.First());
        acceptedDucts = movableDucts.Count(item =>
            finalByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? placement) &&
            placement.PreferredFollowerAnchorTagKey != 0 &&
            finalAnchors.TryGetValue(placement.PreferredFollowerAnchorTagKey,
                out TagLayoutPlacement? anchor) &&
            Math.Abs(placement.TagBounds.MinU - anchor.TagBounds.MinU) <= 1e-7);
        reorderedDucts = movableDucts.Count(item =>
            finalByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? placement) &&
            baselineByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? original) &&
            Math.Abs(placement.Head.V - original.Head.V) > 1e-7);
        acceptedGroups = batches.Count(batch => batch.Any(item =>
            finalByKey.TryGetValue(item.TagKey, out TagLayoutPlacement? placement) &&
            placement.PreferredFollowerAnchorTagKey != 0 &&
            finalAnchors.TryGetValue(placement.PreferredFollowerAnchorTagKey,
                out TagLayoutPlacement? anchor) &&
            Math.Abs(placement.TagBounds.MinU - anchor.TagBounds.MinU) <= 1e-7));

        int currentHard = SmartTagStandardNearHostLayout.CountHardClashes(
            accepted, fixedTags, obstacles, frame, settings.Clearance);
        int currentLeaderClashes = SmartTagStandardNearHostLayout.CountLeaderClashes(
            accepted, fixedTags, settings.Clearance);
        SmartTagLayoutResult result = ResultFrom(accepted, frame);
        return result with
        {
            Diagnostic = $"Standard V2 local rail align: {acceptedDucts}/{movableDucts.Count} " +
                $"Duct tag(s) joined {acceptedGroups}/{batches.Count} local DA/AT rail group(s); " +
                $"{reorderedDucts} local row(s) reordered, {exactRailSnaps} final rail snap(s), " +
                $"{leaderOrderSwaps} safe row swap(s); " +
                $"hard clashes {standardHard} -> {currentHard}, " +
                $"leader clashes {standardLeaderClashes} -> {currentLeaderClashes}. " +
                "DA/AT and Duct host endpoints were retained."
        };
    }

    // Group construction intentionally merges rails that differ by less than
    // the paper clearance. Before returning, remove that last visible offset
    // and use the exact owned DA/AT text edge whenever the measured model,
    // text and leader checks all remain no worse.
    private static List<TagLayoutPlacement> SnapNearlyAlignedDuctRails(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlySet<long> ductKeys,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        out int acceptedSnaps)
    {
        var result = source.ToList();
        acceptedSnaps = 0;
        double mergeTolerance = Math.Max(
            settings.Clearance * 2.0,
            Math.Max(settings.OffsetFromElements * 0.75, 0.02));
        foreach (long key in ductKeys.OrderBy(item => item))
        {
            int index = result.FindIndex(item => item.TagKey == key);
            if (index < 0) continue;
            TagLayoutPlacement current = result[index];
            if (current.PreferredFollowerAnchorTagKey == 0) continue;
            TagLayoutPlacement? owner = result
                .Concat(fixedTags)
                .FirstOrDefault(item =>
                    item.TagKey == current.PreferredFollowerAnchorTagKey);
            if (owner is null) continue;
            double shiftU = owner.TagBounds.MinU - current.TagBounds.MinU;
            if (Math.Abs(shiftU) <= 1e-7 ||
                Math.Abs(shiftU) > mergeTolerance)
                continue;
            TagLayoutPlacement candidate = current with
            {
                Head = new LayoutPoint(current.Head.U + shiftU, current.Head.V),
                TagBounds = Shift(current.TagBounds, shiftU, 0.0),
                HasClash = false
            };
            List<TagLayoutPlacement> reservations = result
                .Where(item => item.TagKey != key)
                .Concat(fixedTags)
                .GroupBy(item => item.TagKey)
                .Select(group => group.First())
                .ToList();
            int currentHard = SmartTagStandardNearHostLayout.CountHardClashes(
                [current], reservations, obstacles, frame, settings.Clearance);
            int candidateHard = SmartTagStandardNearHostLayout.CountHardClashes(
                [candidate], reservations, obstacles, frame, settings.Clearance);
            int currentLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                [current], reservations, settings.Clearance);
            int candidateLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                [candidate], reservations, settings.Clearance);
            if (candidateHard > currentHard || candidateLeaders > currentLeaders)
                continue;
            result[index] = candidate;
            acceptedSnaps++;
        }
        return result;
    }

    // A gap repair can move two labels onto individually clear rows while
    // leaving their host ends in the opposite order. That produces the last
    // long leader/leader crosses seen in dense project captures. Reuse the
    // existing rows, rail and host ends, and only exchange two rows when the
    // fully measured group gets strictly better without adding any other
    // clash. This is deliberately a bounded local pass, not a new layout.
    private static List<TagLayoutPlacement> OptimizeAlignedDuctLeaderOrder(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlySet<long> ductKeys,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        out int acceptedSwaps)
    {
        var result = source.ToList();
        acceptedSwaps = 0;
        double railTolerance = Math.Max(settings.Clearance * 0.5, 0.0025);
        double localSpan = Math.Max(
            settings.ColumnWidth * 0.35,
            result.Where(item => ductKeys.Contains(item.TagKey))
                .Select(item => item.TagBounds.Height)
                .DefaultIfEmpty(0.01)
                .Max() * 6.0);
        var groups = new List<List<long>>();
        foreach (TagLayoutPlacement placement in result
                     .Where(item => ductKeys.Contains(item.TagKey) &&
                                    item.PreferredFollowerAnchorTagKey != 0)
                     .OrderBy(item => item.TagBounds.MinU)
                     .ThenByDescending(item => item.End.V)
                     .ThenBy(item => item.TagKey))
        {
            bool left = placement.Head.U < placement.End.U;
            List<long>? group = groups
                .Where(candidate => candidate.Count < MaximumBatchSize)
                .Where(candidate => candidate.Any(key =>
                {
                    TagLayoutPlacement other = result.First(item => item.TagKey == key);
                    return (other.Head.U < other.End.U) == left &&
                           Math.Abs(other.TagBounds.MinU - placement.TagBounds.MinU) <=
                           railTolerance;
                }))
                .Where(candidate =>
                {
                    double minimum = candidate
                        .Select(key => result.First(item => item.TagKey == key).End.V)
                        .Append(placement.End.V)
                        .Min();
                    double maximum = candidate
                        .Select(key => result.First(item => item.TagKey == key).End.V)
                        .Append(placement.End.V)
                        .Max();
                    return maximum - minimum <= localSpan;
                })
                .OrderBy(candidate => candidate.Min(key =>
                    Math.Abs(result.First(item => item.TagKey == key).End.V -
                             placement.End.V)))
                .FirstOrDefault();
            if (group is null) groups.Add([placement.TagKey]);
            else group.Add(placement.TagKey);
        }

        foreach (List<long> keys in groups.Where(item => item.Count > 1))
        {
            HashSet<long> keySet = keys.ToHashSet();
            List<TagLayoutPlacement> reservations = result
                .Where(item => !keySet.Contains(item.TagKey))
                .Concat(fixedTags)
                .GroupBy(item => item.TagKey)
                .Select(group => group.First())
                .ToList();

            // At most n accepted exchanges per local group. In practice the
            // best host/row order settles after one or two exchanges.
            for (int pass = 0; pass < keys.Count; pass++)
            {
                List<TagLayoutPlacement> current = keys
                    .Select(key => result.First(item => item.TagKey == key))
                    .ToList();
                int currentHard = SmartTagStandardNearHostLayout.CountHardClashes(
                    current, reservations, obstacles, frame, settings.Clearance);
                int currentLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                    current, reservations, settings.Clearance);
                var candidates = new List<(List<TagLayoutPlacement> Placements,
                    int Hard, int Leaders, double Travel, long First, long Second)>();

                for (int first = 0; first < current.Count - 1; first++)
                for (int second = first + 1; second < current.Count; second++)
                {
                    TagLayoutPlacement a = current[first];
                    TagLayoutPlacement b = current[second];
                    if (Math.Abs(a.Head.V - b.Head.V) <= 1e-7) continue;
                    TagLayoutPlacement movedA = AtExistingRow(a, b.Head.V);
                    TagLayoutPlacement movedB = AtExistingRow(b, a.Head.V);
                    var proposal = current
                        .Select(item => item.TagKey == a.TagKey
                            ? movedA
                            : item.TagKey == b.TagKey ? movedB : item)
                        .ToList();
                    int hard = SmartTagStandardNearHostLayout.CountHardClashes(
                        proposal, reservations, obstacles, frame, settings.Clearance);
                    int leaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                        proposal, reservations, settings.Clearance);
                    bool improves = leaders < currentLeaders ||
                                    leaders == currentLeaders && hard < currentHard;
                    if (!improves || leaders > currentLeaders || hard > currentHard)
                        continue;
                    candidates.Add((proposal, hard, leaders,
                        Math.Abs(a.Head.V - b.Head.V) * 2.0,
                        Math.Min(a.TagKey, b.TagKey), Math.Max(a.TagKey, b.TagKey)));
                }

                var best = candidates
                    .OrderBy(item => item.Leaders)
                    .ThenBy(item => item.Hard)
                    .ThenBy(item => item.Travel)
                    .ThenBy(item => item.First)
                    .ThenBy(item => item.Second)
                    .FirstOrDefault();
                if (best.Placements is null) break;
                Dictionary<long, TagLayoutPlacement> replacements = best.Placements
                    .ToDictionary(item => item.TagKey);
                result = result
                    .Select(item => replacements.GetValueOrDefault(item.TagKey, item))
                    .ToList();
                acceptedSwaps++;
            }
        }
        return result;

        static TagLayoutPlacement AtExistingRow(
            TagLayoutPlacement placement,
            double row)
        {
            double shiftV = row - placement.Head.V;
            bool usesElbow = Math.Abs(row - placement.End.V) > 1e-7;
            return placement with
            {
                Head = new LayoutPoint(placement.Head.U, row),
                TagBounds = Shift(placement.TagBounds, 0.0, shiftV),
                Elbow = usesElbow
                    ? new LayoutPoint(placement.End.U, row)
                    : placement.End,
                UsesElbow = usesElbow,
                UsesFreeEnd = usesElbow,
                HasClash = false
            };
        }
    }

    private static List<TagLayoutPlacement> RepairAlignedDuctRows(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlySet<long> ductKeys,
        IReadOnlyDictionary<long, TagLayoutPlacement> horizontalByKey,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        var result = source.ToList();
        double rowGap = Math.Max(settings.RowSpacing, settings.Clearance + 1e-7);
        double followRadius = SmartTagLayoutEngine.NearbyFollowRadius(settings);
        HashSet<long> preferredAnchorKeys = horizontalByKey.Values
            .Where(item => item.PreferredFollowerAnchorTagKey != 0)
            .Select(item => item.PreferredFollowerAnchorTagKey)
            .ToHashSet();
        List<TagLayoutPlacement> anchors = source
            .Concat(fixedTags)
            .Where(item => item.CanAnchorDuctFollowers ||
                           preferredAnchorKeys.Contains(item.TagKey))
            .GroupBy(item => item.TagKey)
            .Select(group => group.First())
            .ToList();

        // Two deterministic passes let later tags reuse spaces
        // released by earlier repairs, without turning Analyze into an open-
        // ended global search.
        for (int pass = 0; pass < 2; pass++)
        {
            bool changed = false;
            long[] orderedKeys = ductKeys
                .Where(horizontalByKey.ContainsKey)
                .OrderByDescending(key => LocalHard(key))
                .ThenByDescending(key => LocalLeaders(key))
                .ThenBy(key => key)
                .ToArray();
            foreach (long key in orderedKeys)
            {
                int index = result.FindIndex(item => item.TagKey == key);
                if (index < 0) continue;
                TagLayoutPlacement current = result[index];
                TagLayoutPlacement horizontal = horizontalByKey[key];
                List<TagLayoutPlacement> reservations = ReservationsFor(key);
                int currentHard = SmartTagStandardNearHostLayout.CountHardClashes(
                    [current], reservations, obstacles, frame, settings.Clearance);
                int currentLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
                    [current], reservations, settings.Clearance);
                var candidates = new List<(TagLayoutPlacement Placement,
                    int Hard, int Leaders, int Spacing, double Travel, int Priority)>
                {
                    (current, currentHard, currentLeaders,
                        CountTagTextSpacingClashes([current], reservations, rowGap),
                        Math.Abs(current.Head.V - horizontal.Head.V), 1)
                };
                bool currentLeft = horizontal.Head.U < horizontal.End.U;
                double railTolerance = Math.Max(settings.Clearance * 0.5, 0.0025);
                double maximumRailReach = Math.Max(
                    followRadius * 2.0,
                    settings.ColumnWidth * 2.0);
                var templates = new List<TagLayoutPlacement> { horizontal };
                foreach (TagLayoutPlacement anchor in anchors
                             .Where(item =>
                                 (item.Head.U < item.End.U) == currentLeft &&
                                 PointDistance(item.End, horizontal.End) <= followRadius &&
                                 Distance(horizontal.End, item.TagBounds) <= maximumRailReach)
                             .OrderBy(item => PointDistance(item.End, horizontal.End))
                             .ThenBy(item => item.TagKey)
                             .Take(6))
                {
                    if (templates.Any(item => Math.Abs(
                            item.TagBounds.MinU - anchor.TagBounds.MinU) <= railTolerance))
                        continue;
                    double shiftU = anchor.TagBounds.MinU - horizontal.TagBounds.MinU;
                    LayoutRect shiftedBounds = Shift(horizontal.TagBounds, shiftU, 0.0);
                    if (shiftedBounds.MinU < frame.MinU ||
                        shiftedBounds.MaxU > frame.MaxU)
                        continue;
                    templates.Add(horizontal with
                    {
                        Head = new LayoutPoint(horizontal.Head.U + shiftU,
                            horizontal.Head.V),
                        TagBounds = shiftedBounds,
                        PreferredFollowerAnchorTagKey = anchor.TagKey
                    });
                }

                int templatePriority = 0;
                foreach (TagLayoutPlacement template in templates)
                {
                    double pitch = template.TagBounds.Height + rowGap;
                    double maximumTravel = Math.Max(
                        settings.ColumnWidth * 0.45,
                        pitch * 4.0);
                    maximumTravel = Math.Min(maximumTravel, settings.ColumnWidth * 0.6);
                    var rows = new Dictionary<long, double>();
                    AddRow(template.Head.V);
                    AddRow(current.Head.V);
                    AddRow(template.End.V);
                    for (int step = 1; step <= 6; step++)
                    {
                        AddRow(template.Head.V - step * pitch);
                        AddRow(template.Head.V + step * pitch);
                    }
                    foreach (TagLayoutPlacement other in reservations)
                    {
                        // Place the real text bounds exactly one clearance gap
                        // above or below an occupied text row. These are the
                        // clear slots missed by a uniform pitch-only search.
                        AddRow(template.Head.V +
                            other.TagBounds.MinV - rowGap - template.TagBounds.MaxV);
                        AddRow(template.Head.V +
                            other.TagBounds.MaxV + rowGap - template.TagBounds.MinV);
                    }
                    foreach (double row in rows.Values)
                    {
                        TagLayoutPlacement candidate = AtRow(template, row);
                        candidates.Add((candidate,
                            SmartTagStandardNearHostLayout.CountHardClashes(
                                [candidate], reservations, obstacles, frame, settings.Clearance),
                            SmartTagStandardNearHostLayout.CountLeaderClashes(
                                [candidate], reservations, settings.Clearance),
                            CountTagTextSpacingClashes(
                                [candidate], reservations, rowGap),
                            Math.Abs(row - horizontal.Head.V),
                            templatePriority));
                    }

                    void AddRow(double row)
                    {
                        double travel = Math.Abs(row - horizontal.Head.V);
                        double shiftV = row - template.Head.V;
                        double minV = template.TagBounds.MinV + shiftV;
                        double maxV = template.TagBounds.MaxV + shiftV;
                        if (travel > maximumTravel + 1e-7 ||
                            minV < frame.MinV || maxV > frame.MaxV)
                            return;
                        rows.TryAdd((long)Math.Round(row / 1e-6), row);
                    }
                    templatePriority++;
                }
                TagLayoutPlacement best = candidates
                    .Where(item => item.Hard <= currentHard &&
                                   item.Leaders <= currentLeaders)
                    .OrderBy(item => item.Leaders)
                    .ThenBy(item => item.Hard)
                    .ThenBy(item => item.Spacing)
                    .ThenBy(item => item.Travel)
                    .ThenBy(item => item.Priority)
                    .First()
                    .Placement;
                if (best == current) continue;
                result[index] = best;
                changed = true;
            }
            if (!changed) break;

            int LocalHard(long key)
            {
                TagLayoutPlacement placement = result.First(item => item.TagKey == key);
                return SmartTagStandardNearHostLayout.CountHardClashes(
                    [placement], ReservationsFor(key), obstacles, frame, settings.Clearance);
            }

            int LocalLeaders(long key)
            {
                TagLayoutPlacement placement = result.First(item => item.TagKey == key);
                return SmartTagStandardNearHostLayout.CountLeaderClashes(
                    [placement], ReservationsFor(key), settings.Clearance);
            }

            List<TagLayoutPlacement> ReservationsFor(long key) => result
                .Where(item => item.TagKey != key)
                .Concat(fixedTags)
                .GroupBy(item => item.TagKey)
                .Select(group => group.First())
                .ToList();
        }
        return result;

        static TagLayoutPlacement AtRow(
            TagLayoutPlacement horizontal,
            double row)
        {
            double shiftV = row - horizontal.Head.V;
            LayoutPoint head = new(horizontal.Head.U, row);
            bool usesElbow = Math.Abs(row - horizontal.End.V) > 1e-7;
            return horizontal with
            {
                Head = head,
                TagBounds = Shift(horizontal.TagBounds, 0.0, shiftV),
                Elbow = usesElbow
                    ? new LayoutPoint(horizontal.End.U, row)
                    : horizontal.End,
                UsesElbow = usesElbow,
                UsesFreeEnd = usesElbow,
                HasClash = false
            };
        }
    }

    private static int CountTagTextSpacingClashes(
        IReadOnlyList<TagLayoutPlacement> moving,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        double requestedGap)
    {
        int clashes = 0;
        double gap = Math.Max(0.0, requestedGap - 1e-7);
        List<TagLayoutPlacement> all = moving.Concat(fixedTags).ToList();
        for (int index = 0; index < moving.Count; index++)
        for (int other = index + 1; other < all.Count; other++)
            if (moving[index].TagBounds.Expand(gap).Intersects(all[other].TagBounds))
                clashes++;
        return clashes;
    }

    private static double PointDistance(LayoutPoint first, LayoutPoint second)
    {
        double du = first.U - second.U;
        double dv = first.V - second.V;
        return Math.Sqrt(du * du + dv * dv);
    }

    private static List<List<LayoutTagInput>> BuildLocalBatches(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<TagLayoutPlacement> routedPlacements,
        SmartTagLayoutSettings settings)
    {
        Dictionary<long, TagLayoutPlacement> routedByKey = routedPlacements
            .ToDictionary(item => item.TagKey);
        double verticalSpan = Math.Max(settings.ColumnWidth * 0.35,
            source.Max(item => Math.Max(item.TagHeight, 0.01)) * 5.0);
        double railMergeDistance = Math.Max(
            settings.Clearance * 2.0,
            Math.Max(settings.OffsetFromElements * 0.75, 0.02));
        var batches = new List<List<LayoutTagInput>>();
        foreach (LayoutTagInput item in source
                     .Where(item => routedByKey.ContainsKey(item.TagKey))
                     .OrderByDescending(tag => tag.Anchor.V)
                     .ThenBy(tag => routedByKey[tag.TagKey].TagBounds.MinU)
                     .ThenBy(tag => tag.TagKey))
        {
            double rail = routedByKey[item.TagKey].TagBounds.MinU;
            List<LayoutTagInput>? nearest = batches
                .Where(batch => batch.Count < MaximumBatchSize &&
                    batch.Any(tag => Math.Abs(
                        routedByKey[tag.TagKey].TagBounds.MinU - rail) <= railMergeDistance) &&
                    AxisSpan(batch.Select(tag => tag.Anchor.V), item.Anchor.V) <= verticalSpan)
                .OrderBy(batch => batch.Min(tag =>
                    SquaredDistance(tag.ElementBounds, item.ElementBounds)))
                .FirstOrDefault();
            if (nearest is null) batches.Add([item]);
            else nearest.Add(item);
        }

        return batches
            .OrderByDescending(batch => batch.Max(item => item.Anchor.V))
            .ThenBy(batch => batch.Min(item => item.Anchor.U))
            .ToList();
    }

    private static double AxisSpan(IEnumerable<double> existing, double value)
    {
        double minimum = Math.Min(existing.Min(), value);
        double maximum = Math.Max(existing.Max(), value);
        return maximum - minimum;
    }

    private static List<TagLayoutPlacement> CreateLocallyOrderedBatch(
        IReadOnlyList<TagLayoutPlacement> horizontalBatch,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        double centerOffset)
    {
        if (horizontalBatch.Count <= 1) return horizontalBatch.ToList();

        double rowGap = Math.Max(settings.RowSpacing, settings.Clearance + 1e-7);
        double targetRail = horizontalBatch
            .Select(item => item.TagBounds.MinU)
            .OrderBy(value => value)
            .ElementAt(horizontalBatch.Count / 2);
        double[] centers = horizontalBatch
            .Select(item => (item.TagBounds.MinV + item.TagBounds.MaxV) * 0.5)
            .OrderBy(value => value)
            .ToArray();
        double localCenter = (centers.Length % 2 == 0
            ? (centers[centers.Length / 2 - 1] + centers[centers.Length / 2]) * 0.5
            : centers[centers.Length / 2]) + centerOffset;
        double stackHeight = horizontalBatch.Sum(item => item.TagBounds.Height) +
            rowGap * (horizontalBatch.Count - 1);
        double topEdge = localCenter + stackHeight * 0.5;
        double bottomEdge = localCenter - stackHeight * 0.5;
        if (topEdge > frame.MaxV)
        {
            bottomEdge -= topEdge - frame.MaxV;
            topEdge = frame.MaxV;
        }
        if (bottomEdge < frame.MinV)
            topEdge += frame.MinV - bottomEdge;

        SmartTagStackRoutePlan plan = SmartTagStackRouting.FindBestOrder(
            horizontalBatch.Select(item => new SmartTagStackRouteInput(
                    item.TagKey,
                    item.TagBounds,
                    item.Head,
                    item.End))
                .ToArray(),
            new LayoutRect(targetRail, topEdge + rowGap,
                targetRail, topEdge + rowGap),
            targetRail,
            alignLeftEdge: true,
            rowGap);

        Dictionary<long, TagLayoutPlacement> byKey = horizontalBatch
            .ToDictionary(item => item.TagKey);
        var result = new List<TagLayoutPlacement>(horizontalBatch.Count);
        double boundary = topEdge;
        foreach (long key in plan.OrderedKeys)
        {
            TagLayoutPlacement current = byKey[key];
            double currentCenter = (current.TagBounds.MinV + current.TagBounds.MaxV) * 0.5;
            double desiredCenter = boundary - current.TagBounds.Height * 0.5;
            double shiftV = desiredCenter - currentCenter;
            LayoutPoint head = new(current.Head.U, current.Head.V + shiftV);
            bool usesElbow = Math.Abs(head.V - current.End.V) > 1e-7;
            result.Add(current with
            {
                Head = head,
                TagBounds = Shift(current.TagBounds, 0.0, shiftV),
                Elbow = usesElbow
                    ? new LayoutPoint(current.End.U, head.V)
                    : current.End,
                UsesElbow = usesElbow,
                UsesFreeEnd = usesElbow,
                HasClash = plan.CrossingCount > 0
            });
            boundary = desiredCenter - current.TagBounds.Height * 0.5 - rowGap;
        }
        return result;
    }

    private static TagLayoutPlacement FindBestSingleAlignment(
        TagLayoutPlacement current,
        TagLayoutPlacement horizontal,
        IReadOnlyList<TagLayoutPlacement> accepted,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        List<TagLayoutPlacement> reservations = accepted
            .Where(item => item.TagKey != current.TagKey)
            .Concat(fixedTags)
            .GroupBy(item => item.TagKey)
            .Select(group => group.First())
            .ToList();
        int currentHard = SmartTagStandardNearHostLayout.CountHardClashes(
            [current], reservations, obstacles, frame, settings.Clearance);
        int currentLeaders = SmartTagStandardNearHostLayout.CountLeaderClashes(
            [current], reservations, settings.Clearance);
        double pitch = horizontal.TagBounds.Height +
            Math.Max(settings.RowSpacing, settings.Clearance + 1e-7);
        double maximumShift = Math.Min(settings.ColumnWidth * 0.25, pitch * 3.0);
        var candidates = new List<(TagLayoutPlacement Placement,
            int Hard, int Leaders, double Travel, int Priority)>
        {
            Score(horizontal, scorePriority: 0),
            (current, currentHard, currentLeaders,
                Math.Abs(current.Head.V - horizontal.Head.V), 2)
        };
        var testedOffsets = new HashSet<long> { 0 };
        int priority = 10;
        for (int step = 1; step <= 3; step++)
        foreach (int direction in new[] { -1, 1 })
        {
            double shiftV = direction * Math.Min(step * pitch, maximumShift);
            long offsetKey = (long)Math.Round(shiftV / 1e-6);
            if (!testedOffsets.Add(offsetKey)) continue;
            LayoutRect bounds = Shift(horizontal.TagBounds, 0.0, shiftV);
            if (bounds.MinV < frame.MinV || bounds.MaxV > frame.MaxV) continue;
            LayoutPoint head = new(horizontal.Head.U, horizontal.Head.V + shiftV);
            bool usesElbow = Math.Abs(head.V - horizontal.End.V) > 1e-7;
            TagLayoutPlacement shifted = horizontal with
            {
                Head = head,
                TagBounds = bounds,
                Elbow = usesElbow
                    ? new LayoutPoint(horizontal.End.U, head.V)
                    : horizontal.End,
                UsesElbow = usesElbow,
                UsesFreeEnd = usesElbow
            };
            candidates.Add(Score(shifted, priority++));
        }

        return candidates
            .Where(item => item.Hard <= currentHard &&
                           item.Leaders <= currentLeaders)
            .OrderBy(item => item.Leaders)
            .ThenBy(item => item.Hard)
            .ThenBy(item => item.Travel)
            .ThenBy(item => item.Priority)
            .First()
            .Placement;

        (TagLayoutPlacement Placement, int Hard, int Leaders,
            double Travel, int Priority) Score(
                TagLayoutPlacement placement,
                int scorePriority) =>
            (placement,
                SmartTagStandardNearHostLayout.CountHardClashes(
                    [placement], reservations, obstacles, frame, settings.Clearance),
                SmartTagStandardNearHostLayout.CountLeaderClashes(
                    [placement], reservations, settings.Clearance),
                Math.Abs(placement.Head.V - horizontal.Head.V),
                scorePriority);
    }

    private static SmartTagLayoutResult ResultFrom(
        IReadOnlyList<TagLayoutPlacement> placements,
        LayoutRect frame)
    {
        if (placements.Count == 0) return new SmartTagLayoutResult([], 0, 0, frame);
        var bounds = new LayoutRect(
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

    private static double SquaredDistance(LayoutRect first, LayoutRect second)
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

    private static SmartTagLayoutResult AlignDuctFollowersOnExistingRows(
        IReadOnlyList<LayoutTagInput> source,
        SmartTagLayoutResult baseline,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        SmartTagLayoutSettings settings)
    {
        if (baseline.Placements.Count == 0) return baseline;
        var placements = baseline.Placements.ToList();
        HashSet<long> preferredCompanionKeys = source
            .Where(item => item.PreferLocalClustering &&
                           item.PreferredFollowerAnchorTagKey != 0)
            .Select(item => item.PreferredFollowerAnchorTagKey)
            .ToHashSet();
        Dictionary<long, TagLayoutPlacement> companions = placements
            .Concat(fixedTags)
            // Older captured/existing reservations do not carry their host
            // category flag, but the controller's preferred ownership key is
            // already the nearest DA/AT host selected for this Duct.
            .Where(item => item.CanAnchorDuctFollowers ||
                           preferredCompanionKeys.Contains(item.TagKey))
            .GroupBy(item => item.TagKey)
            .ToDictionary(group => group.Key, group => group.First());
        if (companions.Count == 0) return baseline;

        double followRadius = SmartTagLayoutEngine.NearbyFollowRadius(settings);
        double visibleRailRadius = followRadius;
        var assignments = new List<(LayoutTagInput Input, TagLayoutPlacement Companion)>();
        foreach (LayoutTagInput input in source.Where(item => item.PreferLocalClustering))
        {
            TagLayoutPlacement? current = placements.FirstOrDefault(item =>
                item.TagKey == input.TagKey);
            if (current is null) continue;
            TagLayoutPlacement? companion = null;
            if (input.PreferredFollowerAnchorTagKey != 0)
                companions.TryGetValue(input.PreferredFollowerAnchorTagKey, out companion);
            // Trust explicit host-nearest ownership even when that tag's text
            // sits at the far end of a long existing leader. Testing the text
            // box against the Duct host here was the reason most fixed DA/AT
            // rails were incorrectly ignored.
            companion ??= companions.Values
                .Where(item => Distance(item.TagBounds, input.ElementBounds) <= visibleRailRadius)
                .OrderBy(item => Distance(item.TagBounds, input.ElementBounds))
                .ThenBy(item => Distance(item.End, input.ElementBounds))
                .ThenBy(item => item.TagKey)
                .FirstOrDefault();
            if (companion is null) continue;
            assignments.Add((input, companion));
        }
        if (assignments.Count == 0) return baseline;

        double railMergeDistance = Math.Max(
            settings.Clearance * 2.0,
            Math.Max(settings.OffsetFromElements * 0.75, 0.02));
        var followerGroups = new List<List<(LayoutTagInput Input, TagLayoutPlacement Companion)>>();
        foreach ((LayoutTagInput Input, TagLayoutPlacement Companion) assignment in assignments
                     .OrderByDescending(item => item.Input.Anchor.V)
                     .ThenBy(item => item.Companion.TagBounds.MinU)
                     .ThenBy(item => item.Input.TagKey))
        {
            bool left = assignment.Companion.Head.U < assignment.Companion.End.U;
            List<(LayoutTagInput Input, TagLayoutPlacement Companion)>? local = followerGroups
                .Where(group => group.Count < MaximumBatchSize && group.Any(item =>
                    (item.Companion.Head.U < item.Companion.End.U) == left &&
                    Math.Abs(item.Companion.TagBounds.MinU -
                             assignment.Companion.TagBounds.MinU) <= railMergeDistance))
                .OrderBy(group => group.Min(item =>
                    SquaredDistance(item.Input.ElementBounds,
                        assignment.Input.ElementBounds)))
                .FirstOrDefault();
            if (local is null) followerGroups.Add([assignment]);
            else local.Add(assignment);
        }

        foreach (List<(LayoutTagInput Input, TagLayoutPlacement Companion)> group in followerGroups)
        {
            double[] visibleRails = group.Select(item => item.Companion.TagBounds.MinU).ToArray();
            double targetRail = visibleRails
                .Distinct()
                .OrderBy(rail => visibleRails.Sum(other => Math.Abs(other - rail)))
                .ThenBy(rail => rail)
                .First();
            foreach ((LayoutTagInput Input, TagLayoutPlacement Companion) follower in group)
            {
                int index = placements.FindIndex(item => item.TagKey == follower.Input.TagKey);
                if (index < 0) continue;
                TagLayoutPlacement current = placements[index];
                double shiftU = targetRail - current.TagBounds.MinU;
                // Standard's global column may already have assigned this
                // sparse Duct a row many metres away. That old row must not be
                // carried into V2. Seed the local search at the retained host
                // end; subsequent packing may move it only to a nearby clear
                // row on this DA/AT rail.
                double targetRow = current.End.V;
                double shiftV = targetRow - current.Head.V;
                LayoutPoint head = new(
                    current.Head.U + shiftU,
                    targetRow);
                placements[index] = current with
                {
                    Head = head,
                    TagBounds = Shift(current.TagBounds, shiftU, shiftV),
                    Elbow = current.End,
                    UsesElbow = false,
                    UsesFreeEnd = false,
                    HasClash = false,
                    PreferredFollowerAnchorTagKey = follower.Companion.TagKey
                };
            }
        }

        if (placements.Count == 0) return baseline;
        return baseline with
        {
            Placements = placements,
            ElbowCount = placements.Count(item => item.UsesElbow),
            ClashCount = placements.Count(item => item.HasClash),
            ColumnBounds = new LayoutRect(
                placements.Min(item => item.TagBounds.MinU),
                placements.Min(item => item.TagBounds.MinV),
                placements.Max(item => item.TagBounds.MaxU),
                placements.Max(item => item.TagBounds.MaxV))
        };
    }

    private static LayoutRect Shift(LayoutRect bounds, double du, double dv) =>
        new(bounds.MinU + du, bounds.MinV + dv, bounds.MaxU + du, bounds.MaxV + dv);

    private static double Distance(LayoutPoint point, LayoutRect bounds)
    {
        double du = point.U < bounds.MinU
            ? bounds.MinU - point.U
            : point.U > bounds.MaxU
                ? point.U - bounds.MaxU
                : 0.0;
        double dv = point.V < bounds.MinV
            ? bounds.MinV - point.V
            : point.V > bounds.MaxV
                ? point.V - bounds.MaxV
                : 0.0;
        return Math.Sqrt(du * du + dv * dv);
    }

    private static double Distance(LayoutRect first, LayoutRect second)
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
        return Math.Sqrt(du * du + dv * dv);
    }
}
