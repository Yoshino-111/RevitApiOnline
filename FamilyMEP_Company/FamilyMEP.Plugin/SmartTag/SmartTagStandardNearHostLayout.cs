namespace FamilyMEP.Plugin.SmartTag;

// Independent experimental copy of Standard. Keep accepted Standard untouched.
internal static class SmartTagStandardNearHostLayout
{
    private const int MaximumSlotAttempts = 64;
    private const int MaximumLocalGroupSize = 12;
    private const double MaximumSearchSeconds = 4;
    [ThreadStatic] private static System.Diagnostics.Stopwatch? _searchClock;
    [ThreadStatic] private static int _searchWork;
    [ThreadStatic] private static int _searchLimit;
    [ThreadStatic] private static int _railWorkEnd;
    [ThreadStatic] private static double _maximumSearchSeconds;
    private sealed class SearchBudgetExceededException : Exception { }
    private sealed class RailBudgetExceededException : Exception { }

    private static void CheckSearchBudget()
    {
        if (_searchClock is null) return;
        double maximumSeconds = _maximumSearchSeconds > 0
            ? _maximumSearchSeconds
            : MaximumSearchSeconds;
        if (++_searchWork > _searchLimit || _searchClock.Elapsed.TotalSeconds > maximumSeconds)
            throw new SearchBudgetExceededException();
        if (_railWorkEnd > 0 && _searchWork > _railWorkEnd)
            throw new RailBudgetExceededException();
    }

    // Independent final check: optimization scores must never be mistaken for
    // proof that a layout is clear. Include fixed tags and the host itself.
    public static int CountHardClashes(IReadOnlyList<TagLayoutPlacement> moving,
        IReadOnlyList<TagLayoutPlacement> fixedTags, IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame, double clearance, List<string>? details = null)
    {
        int clashes = 0;
        var all = moving.Concat(fixedTags).ToList();
        for (int i = 0; i < moving.Count; i++)
        {
            CheckSearchBudget();
            var tag = moving[i];
            var box = tag.TagBounds;
            if (box.MinU < frame.MinU || box.MaxU > frame.MaxU || box.MinV < frame.MinV || box.MaxV > frame.MaxV ||
                obstacles.Any(o => box.Intersects(o.Bounds.Expand(clearance))))
            {
                clashes++;
                details?.Add($"MODEL/FRAME tag {tag.TagKey}: " + string.Join(",", obstacles
                    .Where(o => box.Intersects(o.Bounds.Expand(clearance))).Select(o => o.ElementKey)));
            }
            var lines = BuildSegments([tag]);
            for (int j = i + 1; j < all.Count; j++)
            {
                var other = all[j];
                var otherLines = BuildSegments([other]);
                if (box.Expand(clearance).Intersects(other.TagBounds) ||
                    lines.Any(l => SegmentIntersects(l, other.TagBounds.Expand(clearance))) ||
                    otherLines.Any(l => SegmentIntersects(l, box.Expand(clearance))) ||
                    lines.Any(l => otherLines.Any(r => SegmentsConflict(l, r, clearance))))
                {
                    clashes++;
                    if (details is not null)
                        details.Add($"ANNOTATION {tag.TagKey}/{other.TagKey}: " +
                            $"text={box.Expand(clearance).Intersects(other.TagBounds)} " +
                            $"leader/text={lines.Any(l => SegmentIntersects(l, other.TagBounds.Expand(clearance))) || otherLines.Any(l => SegmentIntersects(l, box.Expand(clearance)))} " +
                            $"leader/leader={lines.Any(l => otherLines.Any(r => SegmentsConflict(l, r, clearance)))}");
                }
            }
        }
        return clashes;
    }

    // Lightweight leader-only metric used by Standard V2 as a tie-breaker.
    // A candidate must first pass CountHardClashes; this count then chooses
    // the route order with fewer line crossings/overlaps without trading away
    // text or model safety.
    public static int CountLeaderClashes(
        IReadOnlyList<TagLayoutPlacement> moving,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        double clearance)
    {
        int clashes = 0;
        var all = moving.Concat(fixedTags).ToList();
        for (int index = 0; index < moving.Count; index++)
        {
            IReadOnlyList<LayoutSegment> lines = BuildSegments([moving[index]]);
            for (int otherIndex = index + 1; otherIndex < all.Count; otherIndex++)
            {
                IReadOnlyList<LayoutSegment> otherLines = BuildSegments([all[otherIndex]]);
                if (lines.Any(line => otherLines.Any(other =>
                        SegmentsConflict(line, other, clearance))))
                    clashes++;
            }
        }
        return clashes;
    }

    // Text safety is non-negotiable for every Near Host move. A lower/equal
    // aggregate score must never trade an old leader crossing for text placed
    // over model geometry, another tag, or another tag's leader.
    private static int CountTextSafetyClashes(
        IReadOnlyList<TagLayoutPlacement> moving,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        double clearance)
    {
        int clashes = 0;
        var all = moving.Concat(fixedTags).ToList();
        for (int i = 0; i < moving.Count; i++)
        {
            CheckSearchBudget();
            TagLayoutPlacement tag = moving[i];
            LayoutRect box = tag.TagBounds;
            if (box.MinU < frame.MinU || box.MaxU > frame.MaxU ||
                box.MinV < frame.MinV || box.MaxV > frame.MaxV ||
                obstacles.Any(o => box.Intersects(o.Bounds.Expand(clearance))))
                clashes++;

            IReadOnlyList<LayoutSegment> lines = BuildSegments([tag]);
            for (int j = i + 1; j < all.Count; j++)
            {
                TagLayoutPlacement other = all[j];
                IReadOnlyList<LayoutSegment> otherLines = BuildSegments([other]);
                if (box.Expand(clearance).Intersects(other.TagBounds) ||
                    lines.Any(line => SegmentIntersects(line, other.TagBounds.Expand(clearance))) ||
                    otherLines.Any(line => SegmentIntersects(line, box.Expand(clearance))))
                    clashes++;
            }
        }
        return clashes;
    }

    public static SmartTagLayoutResult ComputeClustered(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool autoSide,
        IReadOnlyList<TagLayoutPlacement>? initialReservations = null,
        double? forcedColumnLeft = null,
        int searchCheckLimit = 1000000,
        SmartTagLayoutResult? standardBaseline = null,
        double maximumSearchSeconds = MaximumSearchSeconds)
    {
        var previousClock = _searchClock;
        // Always retain a complete Standard baseline; optional refinement may
        // exhaust its budget, but must never erase the user's preview.
        var baseline = standardBaseline ?? SmartTagLayoutEngine.ComputeClustered(source, obstacles, frame,
            settings, autoSide, initialReservations, forcedColumnLeft);
        int previousWork = _searchWork;
        int previousLimit = _searchLimit;
        double previousMaximumSeconds = _maximumSearchSeconds;
        _searchClock = System.Diagnostics.Stopwatch.StartNew();
        _searchWork = 0;
        _searchLimit = Math.Max(1, searchCheckLimit);
        _maximumSearchSeconds = Math.Max(0.05, maximumSearchSeconds);
        try
        {
            var priorities = new Dictionary<long, int>();
            var best = baseline;
            int bestClashes = int.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var candidate = RepairOnlyDistantStandardTags(source, obstacles, frame, settings, autoSide, best,
                    initialReservations ?? [], forcedColumnLeft, priorities, out var unplaced);
                var clock = _searchClock;
                _searchClock = null;
                int clashes;
                try { clashes = CountHardClashes(candidate.Placements, initialReservations ?? [], obstacles, frame, settings.Clearance); }
                finally { _searchClock = clock; }
                if (clashes < bestClashes || clashes == bestClashes &&
                    candidate.Placements.Sum(p => Math.Abs(p.Head.U - p.End.U)) < best.Placements.Sum(p => Math.Abs(p.Head.U - p.End.U)))
                {
                    best = candidate;
                    bestClashes = clashes;
                }
                if (clashes == 0 || unplaced.Count == 0 ||
                    _searchClock.Elapsed.TotalSeconds >= _maximumSearchSeconds ||
                    _searchWork >= _searchLimit)
                    break;
                // Retain accepted clean columns; prioritize remaining difficult
                // groups in the next bounded refinement of the complete result.
                foreach (long key in unplaced)
                    priorities[key] = priorities.GetValueOrDefault(key) + 1;
            }
            return best;
        }
        catch (SearchBudgetExceededException)
        {
            return baseline with { Diagnostic = "Near Host search limit reached; showing complete Standard fallback. Actual family clash validation is still required." };
        }
        finally
        {
            _searchClock = previousClock;
            _searchWork = previousWork;
            _searchLimit = previousLimit;
            _maximumSearchSeconds = previousMaximumSeconds;
        }
    }

    // Near Host is a conservative post-process over Standard. Clean, nearby
    // Standard tags remain untouched. Only distant tags and tags whose text is
    // on model geometry are repacked, in small host-local groups. Repacking a
    // group (rather than translating each tag independently) gives it one text
    // rail, stable host order and uniform row spacing.
    private static SmartTagLayoutResult RepairOnlyDistantStandardTags(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool autoSide,
        SmartTagLayoutResult baseline,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        double? forcedRail,
        IReadOnlyDictionary<long, int> priorities,
        out HashSet<long> unplaced)
    {
        unplaced = [];
        var inputs = source.ToDictionary(item => item.TagKey);
        var accepted = baseline.Placements.Select(placement =>
        {
            LayoutTagInput input = inputs[placement.TagKey];
            if (Math.Abs(input.TextOffsetU) < 1e-9 && Math.Abs(input.TextOffsetV) < 1e-9 &&
                Math.Abs(placement.Elbow.U - placement.End.U) < 1e-9)
                return placement;
            double headV = (placement.TagBounds.MinV + placement.TagBounds.MaxV) * 0.5 -
                           input.TextOffsetV;
            return placement with
            {
                Head = new LayoutPoint((placement.TagBounds.MinU + placement.TagBounds.MaxU) * 0.5 - input.TextOffsetU, headV),
                Elbow = new LayoutPoint(placement.End.U, headV),
                UsesElbow = Math.Abs(headV - placement.End.V) > 1e-7,
                UsesFreeEnd = true
            };
        }).ToList();
        var standardByKey = accepted.ToDictionary(item => item.TagKey);
        var relocatedKeys = new HashSet<long>();
        var relocatedBatches = new List<HashSet<long>>();
        double farThreshold = Math.Max(
            settings.OffsetFromElements * 4.0,
            settings.ColumnWidth * 0.4);
        int farCount = 0;
        int modelOverlapCount = 0;
        int annotationConflictCount = 0;
        int repairedGroups = 0;
        int repairedTags = 0;
        int keptStandard = 0;
        int railTimeouts = 0, hardRejected = 0, distanceRejected = 0, restoredTags = 0;
        bool limited = false;
        HashSet<long> problemKeys = [];

        try
        {
            // Candidate detection and final validation must use the same real
            // clearance. Previously a tag touching a fitting/duct was treated
            // as clean here, then reported as a model clash only after Write.
            bool TextOverlapsModel(TagLayoutPlacement placement) =>
                obstacles.Any(obstacle => placement.TagBounds.Intersects(
                    obstacle.Bounds.Expand(settings.Clearance), 0.0));

            HashSet<long> annotationConflictKeys = FindAnnotationConflictKeys(
                accepted,
                fixedTags,
                settings.Clearance);

            Dictionary<long, TagLayoutPlacement> companionByKey = accepted
                .Concat(fixedTags)
                .Where(item => item.CanAnchorDuctFollowers)
                .GroupBy(item => item.TagKey)
                .ToDictionary(group => group.Key, group => group.First());
            bool FollowsPreferredCompanionRail(
                TagLayoutPlacement placement,
                LayoutTagInput input) =>
                input.PreferLocalClustering &&
                input.PreferredFollowerAnchorTagKey != 0 &&
                companionByKey.TryGetValue(
                    input.PreferredFollowerAnchorTagKey,
                    out TagLayoutPlacement? companion) &&
                Math.Abs(placement.TagBounds.MinU - companion.TagBounds.MinU) <=
                    Math.Max(settings.Clearance, 0.0025);

            problemKeys = accepted
                .Where(item => inputs.TryGetValue(item.TagKey, out LayoutTagInput? input) &&
                    (HorizontalHostGap(item, input) > farThreshold &&
                         !FollowsPreferredCompanionRail(item, input) ||
                     TextOverlapsModel(item) ||
                     annotationConflictKeys.Contains(item.TagKey)))
                .Select(item => item.TagKey)
                .ToHashSet();
            farCount = accepted.Count(item => problemKeys.Contains(item.TagKey) &&
                HorizontalHostGap(item, inputs[item.TagKey]) > farThreshold &&
                !FollowsPreferredCompanionRail(item, inputs[item.TagKey]));
            modelOverlapCount = accepted.Count(item => problemKeys.Contains(item.TagKey) &&
                TextOverlapsModel(item));
            annotationConflictCount = accepted.Count(item =>
                problemKeys.Contains(item.TagKey) && annotationConflictKeys.Contains(item.TagKey));

            var initialGroups = BuildHostLocalGroups(
                    source.Where(item => problemKeys.Contains(item.TagKey)).ToList(),
                    settings)
                .OrderByDescending(group => group.Sum(item => priorities.GetValueOrDefault(item.TagKey)))
                .ThenByDescending(group => group.Max(item => item.Anchor.V))
                .ThenBy(group => group.Min(item => item.Anchor.U))
                .ToList();

            foreach (List<LayoutTagInput> initialGroup in initialGroups)
                RepairGroupOrSplit(initialGroup);

            void RepairGroupOrSplit(List<LayoutTagInput> group)
            {
                CheckSearchBudget();
                if (group.Count == 0) return;
                var keys = group.Select(item => item.TagKey).ToHashSet();
                var old = accepted.Where(item => keys.Contains(item.TagKey)).ToList();
                // All repair candidates are movable in this solve. Their old
                // cross-view leaders cannot reserve the very pockets that the
                // other groups need. Reserve clean Standard tags and completed
                // new groups; reconcile unsolved Standard fallbacks below.
                var reserved = accepted.Where(item => !keys.Contains(item.TagKey) &&
                    (!problemKeys.Contains(item.TagKey) || relocatedKeys.Contains(item.TagKey)))
                    .Concat(fixedTags).ToList();
                bool currentLeft = old.Count(item => item.Head.U < item.End.U) * 2 >= old.Count;
                double oldTotalGap = TotalHorizontalHostGap(old, inputs);
                bool repairsModelOverlap = old.Any(TextOverlapsModel);
                bool repairsAnnotationConflict = old.Any(item =>
                    annotationConflictKeys.Contains(item.TagKey));
                var options = new List<(IReadOnlyList<TagLayoutPlacement> Placements, double Score)>();
                var reservedKeys = reserved.Select(item => item.TagKey).ToHashSet();
                var railHints = reserved.Concat(standardByKey.Values.Where(item =>
                    !keys.Contains(item.TagKey) && !reservedKeys.Contains(item.TagKey))).ToList();

                foreach ((bool left, double? columnLeft) in BuildLocalRailCandidates(
                             group, old, railHints, settings, autoSide,
                             currentLeft, forcedRail))
                {
                    CheckSearchBudget();
                    SmartTagLayoutResult? local = null;
                    // A blocked first rail must not consume the budget for the
                    // opposite side and every remaining host in a full view.
                    int previousRailEnd = _railWorkEnd;
                    _railWorkEnd = _searchWork + 2000 + group.Count * 500;
                    try
                    {
                        local = ComputeColumn(group, obstacles, frame,
                            settings with { PlaceLeft = left }, reserved,
                            BuildSegments(reserved), columnLeft);
                    }
                    catch (RailBudgetExceededException) { railTimeouts++; }
                    finally { _railWorkEnd = previousRailEnd; }
                    if (local is null) continue;
                    if (local.Placements.Count != group.Count ||
                        CountHardClashes(local.Placements, reserved, obstacles,
                            frame, settings.Clearance) != 0)
                    {
                        hardRejected++;
                        continue;
                    }

                    // The ideal near radius is a preference, not a wall. A duct
                    // bank can occupy it entirely. Allow a clear neighboring
                    // column within the user's group-search width, but require
                    // meaningful shortening when moving a distant Standard tag.
                    // Short labels need width slack to share the same LEFT edge
                    // as longer labels rather than forming a separate column.
                    double widest = group.Max(item => item.TagWidth);
                    double localReach = Math.Max(farThreshold, settings.ColumnWidth);
                    bool remainsLocal = local.Placements.All(item =>
                    {
                        var input = inputs[item.TagKey];
                        double oldGap = HorizontalHostGap(standardByKey[item.TagKey], input);
                        double limit = localReach + Math.Max(0, widest - input.TagWidth);
                        if (oldGap > farThreshold && !repairsModelOverlap && !repairsAnnotationConflict)
                            limit = Math.Min(limit, oldGap * 0.75);
                        return HorizontalHostGap(item, input) <= Math.Max(farThreshold, limit) + 1e-7;
                    });
                    if (!remainsLocal) { distanceRejected++; continue; }

                    double newTotalGap = TotalHorizontalHostGap(local.Placements, inputs);
                    if (!repairsModelOverlap && !repairsAnnotationConflict &&
                        newTotalGap >= oldTotalGap - 1e-7)
                        continue;
                    int leaderModelCrossings = local.Placements.Sum(item =>
                        CountLeaderModelCrossings(item, inputs[item.TagKey],
                            obstacles, settings.Clearance));
                    double score = NearHostChoiceScore(
                                       local.Placements,
                                       old,
                                       group,
                                       railHints,
                                       settings) +
                                   leaderModelCrossings * farThreshold * 4.0;
                    options.Add((local.Placements, score));
                }

                IReadOnlyList<TagLayoutPlacement>? chosen = options
                    .OrderBy(option => option.Score)
                    .ThenBy(option => TotalHorizontalHostGap(option.Placements, inputs))
                    .Select(option => option.Placements)
                    .FirstOrDefault();
                if (chosen is not null)
                {
                    foreach (TagLayoutPlacement placement in chosen)
                    {
                        int index = accepted.FindIndex(item => item.TagKey == placement.TagKey);
                        accepted[index] = placement with { HasClash = false };
                    }
                    repairedGroups++;
                    repairedTags += chosen.Count;
                    relocatedKeys.UnionWith(keys);
                    relocatedBatches.Add(keys);
                    return;
                }

                // A single blocked fitting must not prevent the remaining local
                // tags from forming clean smaller rails. Split by host order;
                // the first accepted subgroup becomes a rail reservation for
                // the next, so both subgroups still tend to align together.
                if (group.Count > 1)
                {
                    var ordered = group.OrderByDescending(item => item.Anchor.V)
                        .ThenBy(item => item.Anchor.U)
                        .ToList();
                    int middle = ordered.Count / 2;
                    RepairGroupOrSplit(ordered.Take(middle).ToList());
                    RepairGroupOrSplit(ordered.Skip(middle).ToList());
                    return;
                }
                keptStandard++;
            }
        }
        catch (SearchBudgetExceededException)
        {
            limited = true;
        }

        var clock = _searchClock;
        unplaced = problemKeys.Except(relocatedKeys).ToHashSet();
        _searchClock = null;
        try
        {
            // Restore unresolved candidates, then reject entire new batches
            // that collide with those restored routes. Repeat because restoring
            // one batch can invalidate another. No tag is dropped and no new
            // route may rely on an old tag having silently disappeared.
            bool restored;
            do
            {
                restored = false;
                foreach (var batch in relocatedBatches)
                {
                    if (!batch.Any(relocatedKeys.Contains)) continue;
                    var members = accepted.Where(p => batch.Contains(p.TagKey)).ToList();
                    var others = accepted.Where(p => !batch.Contains(p.TagKey)).Concat(fixedTags).ToList();
                    if (CountHardClashes(members, others, obstacles, frame, settings.Clearance) == 0)
                        continue;
                    for (int index = 0; index < accepted.Count; index++)
                        if (batch.Contains(accepted[index].TagKey))
                            accepted[index] = standardByKey[accepted[index].TagKey];
                    relocatedKeys.ExceptWith(batch);
                    restoredTags += batch.Count;
                    repairedGroups--;
                    restored = true;
                }
            } while (restored);
            repairedTags = relocatedKeys.Count;
            var marked = accepted.Select(item => item with
            {
                HasClash = CountHardClashes(
                    [item],
                    accepted.Where(other => other.TagKey != item.TagKey).Concat(fixedTags).ToList(),
                    obstacles,
                    frame,
                    settings.Clearance) > 0
            }).ToList();
            var bounds = marked.Count == 0
                ? frame
                : new LayoutRect(
                    marked.Min(item => item.TagBounds.MinU),
                    marked.Min(item => item.TagBounds.MinV),
                    marked.Max(item => item.TagBounds.MaxU),
                    marked.Max(item => item.TagBounds.MaxV));
            int unchangedCandidates = Math.Max(keptStandard, problemKeys.Count - repairedTags);
            return baseline with
            {
                Placements = marked,
                ElbowCount = marked.Count(item => item.UsesElbow),
                ClashCount = marked.Count(item => item.HasClash),
                ColumnBounds = bounds,
                Diagnostic = $"Standard local repair: preserved {marked.Count - problemKeys.Count} clean nearby Standard tag(s); " +
                    $"{problemKeys.Count} candidate tag(s) ({farCount} distant, {modelOverlapCount} touching model, " +
                    $"{annotationConflictCount} annotation conflict): " +
                    $"{repairedTags} packed on {repairedGroups} clean local rail(s), {unchangedCandidates} kept unchanged." +
                    (limited ? " Search budget reached; completed moves retained." : "") +
                    $" Checks {_searchWork}; rail limits {railTimeouts}; rejected hard {hardRejected}, distance {distanceRejected}; restored {restoredTags}."
            };
        }
        finally
        {
            _searchClock = clock;
        }
    }

    private static HashSet<long> FindAnnotationConflictKeys(
        IReadOnlyList<TagLayoutPlacement> moving,
        IReadOnlyList<TagLayoutPlacement> fixedTags,
        double clearance)
    {
        var result = new HashSet<long>();
        var movingSegments = moving.ToDictionary(
            item => item.TagKey,
            item => (IReadOnlyList<LayoutSegment>)BuildSegments([item]));

        bool Conflicts(
            TagLayoutPlacement first,
            IReadOnlyList<LayoutSegment> firstLines,
            TagLayoutPlacement second,
            IReadOnlyList<LayoutSegment> secondLines) =>
            first.TagBounds.Expand(clearance).Intersects(second.TagBounds) ||
            firstLines.Any(line => SegmentIntersects(
                line, second.TagBounds.Expand(clearance))) ||
            secondLines.Any(line => SegmentIntersects(
                line, first.TagBounds.Expand(clearance))) ||
            firstLines.Any(line => secondLines.Any(other =>
                SegmentsConflict(line, other, clearance)));

        for (int firstIndex = 0; firstIndex < moving.Count; firstIndex++)
        {
            TagLayoutPlacement first = moving[firstIndex];
            IReadOnlyList<LayoutSegment> firstLines = movingSegments[first.TagKey];
            for (int secondIndex = firstIndex + 1; secondIndex < moving.Count; secondIndex++)
            {
                TagLayoutPlacement second = moving[secondIndex];
                if (!Conflicts(first, firstLines, second, movingSegments[second.TagKey]))
                    continue;
                result.Add(first.TagKey);
                result.Add(second.TagKey);
            }
            foreach (TagLayoutPlacement fixedTag in fixedTags)
            {
                if (Conflicts(first, firstLines, fixedTag, BuildSegments([fixedTag])))
                    result.Add(first.TagKey);
            }
        }
        return result;
    }

    private static double HorizontalHostGap(
        TagLayoutPlacement placement,
        LayoutTagInput host)
    {
        bool left = placement.Head.U < placement.End.U;
        return Math.Max(0, left
            ? host.ElementBounds.MinU - placement.TagBounds.MaxU
            : placement.TagBounds.MinU - host.ElementBounds.MaxU);
    }

    private static IReadOnlyList<(bool Left, double InnerEdge)> FindNearbyHostRails(
        LayoutTagInput host,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings,
        double farThreshold)
    {
        double minV = host.ElementBounds.MinV - settings.ColumnWidth;
        double maxV = host.ElementBounds.MaxV + settings.ColumnWidth;
        double resolution = Math.Max(settings.Clearance, 0.01);
        return reservations
            .Where(item => item.TagBounds.MaxV >= minV && item.TagBounds.MinV <= maxV)
            .Select(item =>
            {
                if (item.TagBounds.MaxU < host.ElementBounds.MinU - settings.Clearance)
                    return (Valid: true, Left: true, InnerEdge: item.TagBounds.MaxU,
                        Gap: host.ElementBounds.MinU - item.TagBounds.MaxU);
                if (item.TagBounds.MinU > host.ElementBounds.MaxU + settings.Clearance)
                    return (Valid: true, Left: false, InnerEdge: item.TagBounds.MinU,
                        Gap: item.TagBounds.MinU - host.ElementBounds.MaxU);
                return (Valid: false, Left: false, InnerEdge: 0.0, Gap: double.MaxValue);
            })
            .Where(item => item.Valid && item.Gap <= farThreshold)
            .GroupBy(item => (item.Left, Rail: (long)Math.Round(item.InnerEdge / resolution)))
            .Select(group => new
            {
                group.Key.Left,
                InnerEdge = group.Average(item => item.InnerEdge),
                Gap = group.Average(item => item.Gap),
                Count = group.Count()
            })
            .OrderBy(item => item.Gap)
            .ThenByDescending(item => item.Count)
            .Take(8)
            .Select(item => (item.Left, item.InnerEdge))
            .ToList();
    }

    private static TagLayoutPlacement? TranslateStandardTagToRail(
        TagLayoutPlacement placement,
        LayoutTagInput host,
        bool left,
        double innerEdge,
        SmartTagLayoutSettings settings)
    {
        if (left && innerEdge >= host.ElementBounds.MinU - settings.Clearance ||
            !left && innerEdge <= host.ElementBounds.MaxU + settings.Clearance)
            return null;
        double desiredMinU = left
            ? innerEdge - placement.TagBounds.Width
            : innerEdge;
        double du = desiredMinU - placement.TagBounds.MinU;
        LayoutRect bounds = placement.TagBounds with
        {
            MinU = placement.TagBounds.MinU + du,
            MaxU = placement.TagBounds.MaxU + du
        };
        LayoutPoint head = placement.Head with { U = placement.Head.U + du };
        if (left && head.U >= placement.End.U - settings.Clearance ||
            !left && head.U <= placement.End.U + settings.Clearance)
            return null;
        return placement with { Head = head, TagBounds = bounds };
    }

    private static bool PreservesStandardHostOrder(
        TagLayoutPlacement candidate,
        LayoutTagInput host,
        IReadOnlyList<TagLayoutPlacement> reservations,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        double clearance)
    {
        bool left = candidate.Head.U < candidate.End.U;
        double innerEdge = left ? candidate.TagBounds.MaxU : candidate.TagBounds.MinU;
        double railTolerance = Math.Max(clearance * 2.0, 0.01);
        foreach (TagLayoutPlacement other in reservations)
        {
            if (!inputs.TryGetValue(other.TagKey, out LayoutTagInput? otherHost)) continue;
            bool otherLeft = other.Head.U < other.End.U;
            if (otherLeft != left) continue;
            double otherEdge = left ? other.TagBounds.MaxU : other.TagBounds.MinU;
            if (Math.Abs(otherEdge - innerEdge) > railTolerance) continue;
            if (otherHost.Anchor.V > host.Anchor.V + 1e-7 &&
                other.Head.V <= candidate.Head.V + 1e-7)
                return false;
            if (otherHost.Anchor.V < host.Anchor.V - 1e-7 &&
                other.Head.V >= candidate.Head.V - 1e-7)
                return false;
        }
        return true;
    }

    private static int CountLeaderModelCrossings(
        TagLayoutPlacement placement,
        LayoutTagInput host,
        IReadOnlyList<LayoutObstacle> obstacles,
        double clearance)
    {
        IReadOnlyList<LayoutSegment> leader = BuildSegments([placement]);
        int count = 0;
        foreach (LayoutObstacle obstacle in obstacles)
        {
            if (obstacle.Kind == LayoutObstacleKind.Mep &&
                obstacle.ElementKey == host.ElementKey)
                continue;
            LayoutRect expanded = obstacle.Bounds.Expand(clearance);
            if (leader.Any(segment => SegmentIntersects(segment, expanded))) count++;
        }
        return count;
    }

    private static SmartTagLayoutResult RepairStandard(
        IReadOnlyList<LayoutTagInput> source, IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame, SmartTagLayoutSettings settings, bool autoSide,
        SmartTagLayoutResult baseline, IReadOnlyList<TagLayoutPlacement> fixedTags, double? forcedRail)
    {
        var inputs = source.ToDictionary(t => t.TagKey);
        var accepted = baseline.Placements.Select(p =>
        {
            if (Math.Abs(inputs[p.TagKey].TextOffsetV) < 1e-9 &&
                Math.Abs(p.Elbow.U - p.End.U) < 1e-9) return p;
            // Standard ignores the measured family offset. Preserve its text
            // rail but normalize the insertion/leader row before validation.
            double v = (p.TagBounds.MinV + p.TagBounds.MaxV) * 0.5 - inputs[p.TagKey].TextOffsetV;
            return p with { Head = p.Head with { V = v }, Elbow = new LayoutPoint(p.End.U, v),
                UsesElbow = Math.Abs(v - p.End.V) > 1e-7, UsesFreeEnd = true };
        }).ToList();
        var repairedKeys = new HashSet<long>();
        var translatedKeys = new HashSet<long>();
        var locallyReflowedKeys = new HashSet<long>();
        int standardColumnsConsidered = 0;
        int standardColumnsFar = 0;
        int standardColumnsBlocked = 0;
        bool limited = false;
        try
        {
            // Repair only offending tags. Valid placements remain reservations,
            // including their leaders, throughout all later local repairs.
            for (int pass = 0; pass < 4; pass++)
            {
                bool preserveStandardRows = pass == 0;
                bool alignHostLocalGroups = pass == 1;
                bool proximityPass = preserveStandardRows || alignHostLocalGroups;
                // Pulling clean Standard columns nearer does not need the
                // expensive per-tag baseline clash scan. On a dense plan that
                // scan consumed most of the four-second budget before distant
                // middle/bottom columns were ever considered.
                var bad = proximityPass
                    ? new HashSet<long>()
                    : accepted.Where(p => CountHardClashes([p],
                        accepted.Where(q => q.TagKey != p.TagKey).Concat(fixedTags).ToList(),
                        obstacles, frame, settings.Clearance) > 0).Select(p => p.TagKey).ToHashSet();
                if (bad.Count == 0 && !proximityPass) continue;
                var groups = preserveStandardRows
                    ? BuildNearbyColumnGroups(accepted, inputs, settings)
                        .OrderByDescending(group =>
                        {
                            var ids = group.Select(tag => tag.TagKey).ToHashSet();
                            return TotalHorizontalHostGap(
                                accepted.Where(p => ids.Contains(p.TagKey)).ToList(),
                                inputs);
                        })
                        .ThenBy(group => group.Count)
                        .ToList()
                    : alignHostLocalGroups
                    ? BuildHostLocalGroups(source, settings)
                        .OrderByDescending(group =>
                        {
                            var ids = group.Select(tag => tag.TagKey).ToHashSet();
                            return TotalHorizontalHostGap(
                                accepted.Where(p => ids.Contains(p.TagKey)).ToList(),
                                inputs);
                        })
                        .ThenBy(group => group.Count)
                        .ToList()
                    : pass == 2
                    ? BuildClusters(source.Where(t => bad.Contains(t.TagKey)).ToList(), settings.ColumnWidth)
                    : source.Where(t => bad.Contains(t.TagKey)).OrderByDescending(t => t.Anchor.V)
                        .Select(t => new List<LayoutTagInput> { t }).ToList();
                foreach (var group in groups)
                {
                    CheckSearchBudget();
                    var ids = group.Select(t => t.TagKey).ToHashSet();
                    var old = accepted.Where(p => ids.Contains(p.TagKey)).ToList();
                    var reserved = accepted.Where(p => !ids.Contains(p.TagKey)).Concat(fixedTags).ToList();
                    int oldClashes = CountHardClashes(
                        old,
                        reserved,
                        obstacles,
                        frame,
                        settings.Clearance);
                    int oldTextSafetyClashes = CountTextSafetyClashes(
                        old,
                        reserved,
                        obstacles,
                        frame,
                        settings.Clearance);
                    if (!proximityPass && oldClashes == 0) continue;
                    var segments = BuildSegments(reserved);
                    bool currentLeft = old[0].Head.U < old[0].End.U;
                    if (preserveStandardRows)
                    {
                        standardColumnsConsidered++;
                        bool needsPull = StandardColumnPullDistance(old, group, settings) >
                            Math.Max(settings.OffsetFromElements, settings.Clearance * 2.0);
                        if (needsPull) standardColumnsFar++;
                        var pulled = PullStandardColumn(
                            old,
                            group,
                            reserved,
                            obstacles,
                            frame,
                            settings,
                            forcedRail,
                            oldClashes,
                            oldTextSafetyClashes);
                        if (pulled is not null)
                        {
                            var pulledByKey = pulled.ToDictionary(p => p.TagKey);
                            accepted = accepted.Select(p =>
                                pulledByKey.GetValueOrDefault(p.TagKey, p)).ToList();
                            repairedKeys.UnionWith(ids);
                            translatedKeys.UnionWith(ids);
                        }
                        else if (needsPull)
                        {
                            standardColumnsBlocked++;
                        }
                        continue;
                    }
                    if (alignHostLocalGroups)
                    {
                        double oldScore = NearHostBaselineScore(
                            old,
                            group,
                            reserved,
                            settings);
                        var options = new List<IReadOnlyList<TagLayoutPlacement>>();
                        bool oneExistingSide = old.All(item =>
                            (item.Head.U < item.End.U) == currentLeft);
                        var pulled = oneExistingSide
                            ? PullStandardColumn(
                                old,
                                group,
                                reserved,
                                obstacles,
                                frame,
                                settings,
                                forcedRail,
                                oldClashes,
                                oldTextSafetyClashes)
                            : null;
                        bool hasOppositeLocalRail = HasNearbyOppositeTagRail(
                            group,
                            reserved,
                            currentLeft,
                            settings);
                        if (pulled is not null && !hasOppositeLocalRail)
                        {
                            // This is the preferred Near Host operation: move
                            // the complete accepted Standard column horizontally
                            // without changing rows, order, endpoints or elbows.
                            // Do not replace it with a more aggressive reflow.
                            var pulledByKey = pulled.ToDictionary(p => p.TagKey);
                            accepted = accepted.Select(p =>
                                pulledByKey.GetValueOrDefault(p.TagKey, p)).ToList();
                            repairedKeys.UnionWith(ids);
                            translatedKeys.UnionWith(ids);
                            continue;
                        }
                        if (pulled is not null) options.Add(pulled);

                        // A straight translation can be rejected when one of
                        // the retained Standard rows crosses a nearby leader or
                        // element. Reflow this small host-local group on both
                        // sides in Auto mode. Standard still supplies row order;
                        // Near Host may change side only when the local rail is
                        // closer, text-safe and no worse than Standard.
                        foreach ((bool left, double? localRail) in BuildLocalRailCandidates(
                                     group,
                                     old,
                                     reserved,
                                     settings,
                                     autoSide,
                                     currentLeft,
                                     forcedRail))
                        {
                            var local = ComputeColumn(
                                group,
                                obstacles,
                                frame,
                                settings with { PlaceLeft = left },
                                reserved,
                                segments,
                                localRail);
                            int localClashes = CountHardClashes(
                                local.Placements,
                                reserved,
                                obstacles,
                                frame,
                                settings.Clearance);
                            double localScore = NearHostChoiceScore(
                                local.Placements,
                                old,
                                group,
                                reserved,
                                settings);
                            if (localClashes <= oldClashes &&
                                CountTextSafetyClashes(local.Placements, reserved, obstacles, frame, settings.Clearance) <= oldTextSafetyClashes &&
                                localScore < oldScore - 1e-7)
                                options.Add(local.Placements);
                        }

                        IReadOnlyList<TagLayoutPlacement>? chosen = options
                            .OrderBy(option => NearHostChoiceScore(option, old, group, reserved, settings))
                            .ThenBy(option => CountHardClashes(option, reserved, obstacles, frame, settings.Clearance))
                            .FirstOrDefault();
                        if (chosen is not null)
                        {
                            var replacements = chosen.ToDictionary(p => p.TagKey);
                            accepted = accepted.Select(p => replacements.GetValueOrDefault(p.TagKey, p)).ToList();
                            repairedKeys.UnionWith(ids);
                            locallyReflowedKeys.UnionWith(ids);
                        }
                        continue;
                    }
                    foreach (bool left in autoSide ? new[] { currentLeft, !currentLeft } : new[] { settings.PlaceLeft })
                    {
                        var candidate = ComputeColumn(group, obstacles, frame, settings with { PlaceLeft = left },
                            reserved, segments, forcedRail);
                        int next = CountHardClashes(candidate.Placements, reserved, obstacles, frame, settings.Clearance);
                        // Never introduce a new kind of clash on an already-valid
                        // tag: only accept complete local repairs with zero conflicts.
                        if (next != 0) continue;
                        var replacements = candidate.Placements.ToDictionary(p => p.TagKey);
                        accepted = accepted.Select(p => replacements.TryGetValue(p.TagKey, out var replacement) ? replacement : p).ToList();
                        repairedKeys.UnionWith(ids);
                        locallyReflowedKeys.UnionWith(ids);
                        break;
                    }
                }
            }
        }
        catch (SearchBudgetExceededException) { limited = true; }
        // Exhaustion preserves all successful repairs, rather than discarding
        // them in favour of the original conflicting fallback.
        var clock = _searchClock;
        _searchClock = null;
        try
        {
            var marked = accepted.Select(p => p with { HasClash = CountHardClashes([p],
                accepted.Where(q => q.TagKey != p.TagKey).Concat(fixedTags).ToList(),
                obstacles, frame, settings.Clearance) > 0 }).ToList();
            return baseline with { Placements = marked, ElbowCount = marked.Count(p => p.UsesElbow),
                ColumnBounds = marked.Count == 0 ? frame : new LayoutRect(marked.Min(p => p.TagBounds.MinU),
                    marked.Min(p => p.TagBounds.MinV), marked.Max(p => p.TagBounds.MaxU), marked.Max(p => p.TagBounds.MaxV)),
                ClashCount = marked.Count(p => p.HasClash),
                Diagnostic = $"Standard local repair: {repairedKeys.Count} unique tag(s) repaired " +
                    $"({translatedKeys.Count} translated with Standard rows; " +
                    $"{locallyReflowedKeys.Count} locally reflowed); " +
                    $"columns {standardColumnsConsidered} checked, {standardColumnsFar} far, " +
                    $"{standardColumnsBlocked} blocked; " +
                    $"{marked.Count(p => p.HasClash)} unresolved tag(s)." +
                    (limited ? " Search budget reached; completed repairs retained." : "") };
        }
        finally { _searchClock = clock; }
    }

    private static List<List<LayoutTagInput>> BuildNearbyColumnGroups(
        IReadOnlyList<TagLayoutPlacement> placements, IReadOnlyDictionary<long, LayoutTagInput> inputs,
        SmartTagLayoutSettings settings)
    {
        var result = new List<List<LayoutTagInput>>();
        foreach (var rail in placements.GroupBy(p =>
        {
            bool left = p.Head.U < p.End.U;
            double innerTextEdge = left ? p.TagBounds.MaxU : p.TagBounds.MinU;
            return (Math.Round(innerTextEdge, 6), left);
        }))
        {
            List<LayoutTagInput>? group = null;
            foreach (var tag in rail.Select(p => inputs[p.TagKey]).OrderByDescending(t => t.Anchor.V).ThenBy(t => t.TagKey))
            {
                double span = Math.Max(settings.ColumnWidth * 0.5, tag.TagHeight * 4);
                if (group is null || group.Max(t => t.Anchor.V) - tag.Anchor.V > span ||
                    Math.Max(group.Max(t => t.Anchor.U), tag.Anchor.U) - Math.Min(group.Min(t => t.Anchor.U), tag.Anchor.U) > span)
                {
                    group = [];
                    result.Add(group);
                }
                group.Add(tag);
            }
        }
        return result;
    }

    // Pass zero must not inherit Standard's old left/right rail assignment.
    // Hosts that are physically near each other form one bounded local group,
    // even when Standard previously sent one of their tags to the opposite
    // side. The whole group can then compare both sides and reuse the closest
    // clean local tag rail.
    private static List<List<LayoutTagInput>> BuildHostLocalGroups(
        IReadOnlyList<LayoutTagInput> source,
        SmartTagLayoutSettings settings)
    {
        if (source.Count == 0) return [];
        double span = Math.Max(
            settings.ColumnWidth,
            source.Max(item => Math.Max(item.TagHeight, 0.01)) * 6.0);
        var groups = new List<List<LayoutTagInput>>();
        foreach (LayoutTagInput tag in source
                     .OrderByDescending(item => item.Anchor.V)
                     .ThenBy(item => item.Anchor.U)
                     .ThenBy(item => item.TagKey))
        {
            List<LayoutTagInput>? nearest = groups
                .Where(group =>
                {
                    double minU = Math.Min(group.Min(item => item.Anchor.U), tag.Anchor.U);
                    double maxU = Math.Max(group.Max(item => item.Anchor.U), tag.Anchor.U);
                    double minV = Math.Min(group.Min(item => item.Anchor.V), tag.Anchor.V);
                    double maxV = Math.Max(group.Max(item => item.Anchor.V), tag.Anchor.V);
                    return group.Count < MaximumLocalGroupSize &&
                           maxU - minU <= span && maxV - minV <= span;
                })
                .OrderBy(group => group.Min(item =>
                    Math.Abs(item.Anchor.U - tag.Anchor.U) +
                    Math.Abs(item.Anchor.V - tag.Anchor.V)))
                .FirstOrDefault();
            if (nearest is null) groups.Add([tag]);
            else nearest.Add(tag);
        }
        return groups;
    }

    // Translation only: retain Standard rows, text dimensions, endpoints and
    // vertical leader legs. Only the horizontal head segment becomes shorter.
    private static IReadOnlyList<TagLayoutPlacement>? PullStandardColumn(
        IReadOnlyList<TagLayoutPlacement> old, IReadOnlyList<LayoutTagInput> hosts,
        IReadOnlyList<TagLayoutPlacement> reserved, IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame, SmartTagLayoutSettings settings, double? forcedRail,
        int maximumClashes, int maximumTextSafetyClashes)
    {
        if (old.Count == 0) return null;
        bool left = old[0].Head.U < old[0].End.U;
        double width = old.Max(p => p.TagBounds.Width);
        double original = left ? old[0].TagBounds.MaxU : old[0].TagBounds.MinU;
        double target = left ? hosts.Min(t => t.ElementBounds.MinU) - settings.OffsetFromElements
            : hosts.Max(t => t.ElementBounds.MaxU) + settings.OffsetFromElements;
        if (Math.Abs(target - original) <= Math.Max(settings.OffsetFromElements, settings.Clearance * 2)) return null;
        var rails = forcedRail.HasValue
            ? new[] { left ? forcedRail.Value + width : forcedRail.Value }.AsEnumerable()
            : new[] { target }
                .Concat(obstacles.Select(o => left
                    ? o.Bounds.MinU - settings.Clearance
                    : o.Bounds.MaxU + settings.Clearance))
                .Concat(reserved.Select(p => left
                    ? p.TagBounds.MinU - settings.Clearance
                    : p.TagBounds.MaxU + settings.Clearance))
                // Reuse the inner text edge of nearby Standard tags whenever
                // their rows do not overlap. This produces one clean local rail
                // instead of another almost-parallel column a few millimetres
                // away.
                .Concat(reserved.Select(p => left
                    ? p.TagBounds.MaxU
                    : p.TagBounds.MinU));
        foreach (double rail in rails.Distinct().Where(x => left ? x > original + 1e-7 && x <= target : x < original - 1e-7 && x >= target)
                     .OrderBy(x => Math.Abs(x - target)).Take(24))
        {
            CheckSearchBudget();
            double du = rail - original;
            var candidate = old.Select(p => p with
            {
                Head = p.Head with { U = p.Head.U + du },
                TagBounds = p.TagBounds with { MinU = p.TagBounds.MinU + du, MaxU = p.TagBounds.MaxU + du }
            }).ToList();
            if (CountTextSafetyClashes(candidate, reserved, obstacles, frame, settings.Clearance) <= maximumTextSafetyClashes &&
                CountHardClashes(candidate, reserved, obstacles, frame, settings.Clearance) <= maximumClashes)
                return candidate;
        }
        return null;
    }

    private static double StandardColumnPullDistance(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<LayoutTagInput> hosts,
        SmartTagLayoutSettings settings)
    {
        if (placements.Count == 0 || hosts.Count == 0) return 0;
        bool left = placements[0].Head.U < placements[0].End.U;
        double original = left
            ? placements.Average(item => item.TagBounds.MaxU)
            : placements.Average(item => item.TagBounds.MinU);
        double target = left
            ? hosts.Min(item => item.ElementBounds.MinU) - settings.OffsetFromElements
            : hosts.Max(item => item.ElementBounds.MaxU) + settings.OffsetFromElements;
        return left ? target - original : original - target;
    }

    private static double TotalHorizontalHostGap(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyDictionary<long, LayoutTagInput> inputs)
    {
        double total = 0;
        foreach (TagLayoutPlacement placement in placements)
        {
            LayoutTagInput host = inputs[placement.TagKey];
            bool left = placement.Head.U < placement.End.U;
            double gap = left
                ? host.ElementBounds.MinU - placement.TagBounds.MaxU
                : placement.TagBounds.MinU - host.ElementBounds.MaxU;
            total += Math.Max(0, gap);
        }
        return total;
    }

    private static IReadOnlyList<(bool Left, double? ColumnLeft)> BuildLocalRailCandidates(
        IReadOnlyList<LayoutTagInput> hosts,
        IReadOnlyList<TagLayoutPlacement> standardPlacements,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings,
        bool autoSide,
        bool currentLeft,
        double? forcedRail)
    {
        var result = new List<(bool Left, double? ColumnLeft)>();
        var seen = new HashSet<(bool Left, long Rail)>();
        double resolution = Math.Max(settings.Clearance, 0.01);
        double maximumWidth = hosts.Max(item => Math.Max(item.TagWidth, 0.01));
        double verticalReach = Math.Max(
            settings.ColumnWidth,
            hosts.Sum(item => item.TagHeight + settings.RowSpacing));
        double minV = hosts.Min(item => item.ElementBounds.MinV) - verticalReach;
        double maxV = hosts.Max(item => item.ElementBounds.MaxV) + verticalReach;
        double hostMinU = hosts.Min(item => item.ElementBounds.MinU);
        double hostMaxU = hosts.Max(item => item.ElementBounds.MaxU);
        double maximumLocalGap = Math.Max(
            settings.OffsetFromElements * 4.0,
            settings.ColumnWidth);

        IEnumerable<bool> sides = autoSide
            ? new[] { currentLeft, !currentLeft }
            : new[] { currentLeft };
        foreach (bool left in sides)
        {
            // Null means the natural rail immediately outside this host group.
            result.Add((left, left == currentLeft ? forcedRail : null));
            // Include the group's own Standard rails. They are temporarily
            // removed from reservations while repacking, but their established
            // local text edge is still a valuable candidate. This is what lets
            // one distant right tag join two nearby left tags instead of being
            // routed across the plan again.
            var nearbyRails = reservations.Concat(standardPlacements)
                .Where(item => (item.Head.U < item.End.U) == left)
                .Where(item => item.TagBounds.MaxV >= minV && item.TagBounds.MinV <= maxV)
                // Standard aligns the visible LEFT edge on either side. Use
                // this group's maximum width only to check host clearance.
                .Select(item => left ? item.TagBounds.MinU + maximumWidth : item.TagBounds.MinU)
                .Where(edge => left
                    ? edge < hostMinU - settings.Clearance
                    : edge > hostMaxU + settings.Clearance)
                .GroupBy(edge => (long)Math.Round(edge / resolution))
                .Select(group => new
                {
                    InnerEdge = group.Average(),
                    Count = group.Count(),
                    HostDistance = left
                        ? hostMinU - group.Average()
                        : group.Average() - hostMaxU
                })
                .Where(item => item.HostDistance <= maximumLocalGap + 1e-7)
                .OrderBy(item => item.HostDistance)
                .ThenByDescending(item => item.Count)
                .Take(6);
            foreach (var rail in nearbyRails)
            {
                double columnLeft = left
                    ? rail.InnerEdge - maximumWidth
                    : rail.InnerEdge;
                var key = (left, (long)Math.Round(columnLeft / resolution));
                if (seen.Add(key)) result.Add((left, columnLeft));
            }
        }
        return result;
    }

    private static bool HasNearbyOppositeTagRail(
        IReadOnlyList<LayoutTagInput> hosts,
        IReadOnlyList<TagLayoutPlacement> reservations,
        bool currentLeft,
        SmartTagLayoutSettings settings)
    {
        bool oppositeLeft = !currentLeft;
        double verticalReach = Math.Max(
            settings.ColumnWidth,
            hosts.Sum(item => item.TagHeight + settings.RowSpacing));
        double minV = hosts.Min(item => item.ElementBounds.MinV) - verticalReach;
        double maxV = hosts.Max(item => item.ElementBounds.MaxV) + verticalReach;
        double hostMinU = hosts.Min(item => item.ElementBounds.MinU);
        double hostMaxU = hosts.Max(item => item.ElementBounds.MaxU);
        return reservations.Any(item =>
        {
            bool itemLeft = item.Head.U < item.End.U;
            if (itemLeft != oppositeLeft ||
                item.TagBounds.MaxV < minV || item.TagBounds.MinV > maxV)
                return false;
            double edge = itemLeft ? item.TagBounds.MaxU : item.TagBounds.MinU;
            return itemLeft
                ? edge < hostMinU - settings.Clearance
                : edge > hostMaxU + settings.Clearance;
        });
    }

    private static double NearHostChoiceScore(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<TagLayoutPlacement> standardPlacements,
        IReadOnlyList<LayoutTagInput> hosts,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
    {
        var byKey = hosts.ToDictionary(item => item.TagKey);
        double hostGap = TotalHorizontalHostGap(placements, byKey);
        double rowTravel = placements.Sum(item =>
            Math.Abs(item.Head.V - byKey[item.TagKey].Anchor.V));
        bool left = placements[0].Head.U < placements[0].End.U;
        double candidateEdge = placements.Average(item => item.TagBounds.MinU);
        double verticalReach = Math.Max(
            settings.ColumnWidth * 0.5,
            hosts.Max(item => item.TagHeight) * 4.0);
        double minV = hosts.Min(item => item.ElementBounds.MinV) - verticalReach;
        double maxV = hosts.Max(item => item.ElementBounds.MaxV) + verticalReach;
        // A nearby but different X is not an aligned text column. Use a tiny
        // numeric tolerance; model clearance must not reward staircase rails.
        const double alignTolerance = 1e-6;
        int alignedNeighbors = reservations.Count(item =>
        {
            bool itemLeft = item.Head.U < item.End.U;
            if (itemLeft != left || item.TagBounds.MaxV < minV || item.TagBounds.MinV > maxV)
                return false;
            double edge = item.TagBounds.MinU;
            return Math.Abs(edge - candidateEdge) <= alignTolerance;
        });

        // Host distance dominates. When left/right are similarly close, reuse
        // the side and text edge already established by nearby clean tags.
        double localRailUnit = Math.Max(
            settings.OffsetFromElements * 2.0,
            Math.Max(settings.Clearance * 8.0, settings.ColumnWidth * 0.25));
        double alignmentReward = placements.Count * Math.Min(2, alignedNeighbors) * localRailUnit;
        int internallyAligned = Math.Max(0, placements.Count(item =>
        {
            double edge = item.TagBounds.MinU;
            return Math.Abs(edge - candidateEdge) <= alignTolerance;
        }) - 1);
        double internalRailReward = internallyAligned * localRailUnit;
        // Do not reward the old Standard side for a tag selected for repair.
        // That bias made a clear, closer left side lose merely because Standard
        // had originally sent the tag to the right. Fixed nearby rails still
        // receive the alignment reward above.
        return hostGap + rowTravel * 0.15 - alignmentReward - internalRailReward;
    }

    private static double NearHostBaselineScore(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<LayoutTagInput> hosts,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
    {
        var hostsByKey = hosts.ToDictionary(item => item.TagKey);
        return placements
            .GroupBy(item => item.Head.U < item.End.U)
            .Sum(side =>
            {
                var sidePlacements = side.ToList();
                var sideHosts = sidePlacements
                    .Select(item => hostsByKey[item.TagKey])
                    .ToList();
                return NearHostChoiceScore(
                    sidePlacements,
                    placements,
                    sideHosts,
                    reservations,
                    settings);
            });
    }

    private static SmartTagLayoutResult ComputeClusteredCore(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool autoSide,
        IReadOnlyList<TagLayoutPlacement>? initialReservations = null,
        double? forcedColumnLeft = null)
    {
        if (source.Count == 0)
        {
            return new SmartTagLayoutResult([], 0, 0, frame);
        }

        var placements = new List<TagLayoutPlacement>(source.Count);
        var reservations = initialReservations?.ToList() ?? [];
        var reservedSegments = BuildSegments(reservations);
        // A rail belongs to a local horizontal host cluster, not to the entire
        // viewport. Distant branches therefore align with their own nearby rail.
        foreach (List<LayoutTagInput> cluster in BuildClusters(source, settings.ColumnWidth))
        {
            if (!autoSide)
            {
                AddResult(ComputeColumn(
                    cluster,
                    obstacles,
                    frame,
                    settings,
                    reservations,
                    reservedSegments,
                    forcedColumnLeft));
                continue;
            }

            // Choose one side for the complete local host group. Per-tag
            // side/load decisions fragment a Standard column into scattered labels.
            var leftResult = ComputeColumn(cluster, obstacles, frame,
                settings with { PlaceLeft = true }, reservations, reservedSegments, forcedColumnLeft);
            var rightResult = ComputeColumn(cluster, obstacles, frame,
                settings with { PlaceLeft = false }, reservations, reservedSegments, forcedColumnLeft);
            double Travel(SmartTagLayoutResult result) => result.Placements.Sum(p =>
                Math.Abs(p.Head.U - p.End.U) + Math.Abs(p.Head.V - p.End.V));
            int leftHard = CountHardClashes(leftResult.Placements, reservations, obstacles, frame, settings.Clearance);
            int rightHard = CountHardClashes(rightResult.Placements, reservations, obstacles, frame, settings.Clearance);
            AddResult(leftHard < rightHard || leftHard == rightHard &&
                (leftResult.ClashCount < rightResult.ClashCount ||
                leftResult.ClashCount == rightResult.ClashCount && Travel(leftResult) <= Travel(rightResult))
                ? leftResult : rightResult);
        }

        void AddResult(SmartTagLayoutResult result)
        {
            placements.AddRange(result.Placements);
            reservations.AddRange(result.Placements);
            reservedSegments.AddRange(BuildSegments(result.Placements));
        }

        placements = placements
            .OrderByDescending(item => item.End.V)
            .ThenBy(item => item.Head.U)
            .ToList();
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

    public static SmartTagLayoutResult Compute(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings) =>
        ComputeColumn(source, obstacles, frame, settings, [], [], null);

    private static SmartTagLayoutResult ComputeColumn(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement> initialReservations,
        IReadOnlyList<LayoutSegment> initialSegments,
        double? forcedColumnLeft)
    {
        var first = ComputeColumnAtRail(source, obstacles, frame, settings,
            initialReservations, initialSegments, forcedColumnLeft);
        if (source.Count == 0 || forcedColumnLeft.HasValue ||
            CountHardClashes(first.Placements, initialReservations, obstacles, frame, settings.Clearance) == 0)
            return first;
        double width = source.Max(t => t.TagWidth);
        double origin = first.ColumnBounds.MinU;
        double travel = Math.Max(source.Sum(t => t.TagHeight + Math.Max(settings.RowSpacing, settings.Clearance)),
            source.Max(t => t.TagHeight) * 3 + settings.Clearance * 4);
        double minV = source.Min(t => t.Anchor.V) - travel - source.Max(t => t.TagHeight);
        double maxV = source.Max(t => t.Anchor.V) + travel + source.Max(t => t.TagHeight);
        // Full-view ducts often have almost identical X bounds (floating-point
        // differences only). Eight raw edges used to exhaust every alternate
        // rail on the same blocked column, including edges from unrelated rows.
        var rails = obstacles.Where(o => o.Bounds.MaxV >= minV && o.Bounds.MinV <= maxV).SelectMany(o => new[]
            { o.Bounds.MinU - width - settings.Clearance, o.Bounds.MaxU + settings.Clearance })
            .Concat(initialReservations.Where(p => p.TagBounds.MaxV >= minV && p.TagBounds.MinV <= maxV).SelectMany(p => new[]
            { p.TagBounds.MinU - width - settings.Clearance, p.TagBounds.MaxU + settings.Clearance }))
            .Concat(new[] { origin - width - settings.OffsetFromElements, origin + width + settings.OffsetFromElements })
            .Where(x => x >= frame.MinU + settings.TopMargin && x + width <= frame.MaxU - settings.TopMargin &&
                (settings.PlaceLeft ? x + width < source.Min(t => t.Anchor.U) : x > source.Max(t => t.Anchor.U)))
            .OrderBy(x => Math.Abs(x - origin))
            .GroupBy(x => (long)Math.Round(x / Math.Max(settings.Clearance * 0.5, 0.01)))
            .Select(g => g.First()).Take(16);
        var best = first;
        foreach (double rail in rails)
        {
            var candidate = ComputeColumnAtRail(source, obstacles, frame, settings,
                initialReservations, initialSegments, rail);
            int candidateHard = CountHardClashes(candidate.Placements, initialReservations, obstacles, frame, settings.Clearance);
            int bestHard = CountHardClashes(best.Placements, initialReservations, obstacles, frame, settings.Clearance);
            if (candidateHard < bestHard || candidateHard == bestHard && candidate.ClashCount < best.ClashCount) best = candidate;
            if (candidateHard == 0) break;
        }
        return best;
    }

    private static SmartTagLayoutResult ComputeColumnAtRail(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement> initialReservations,
        IReadOnlyList<LayoutSegment> initialSegments,
        double? forcedColumnLeft)
    {
        if (source.Count == 0)
        {
            return new SmartTagLayoutResult([], 0, 0, frame);
        }

        // The final validator expands annotations by Clearance. Packing with
        // Tag Gap alone (the UI defaults are 1 mm vs 2 mm) creates dense rows
        // that this same validator rejects, even in a completely empty pocket.
        // Use one effective separation for rows, slot search and collision cost.
        settings = settings with { RowSpacing = Math.Max(settings.RowSpacing, settings.Clearance + 1e-7) };

        List<LayoutTagInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();

        double top = frame.MaxV - settings.TopMargin;
        double bottom = frame.MinV + settings.TopMargin;
        double elementEdge = settings.PlaceLeft
            ? ordered.Min(item => item.ElementBounds.MinU)
            : ordered.Max(item => item.ElementBounds.MaxU);
        double maximumTagWidth = ordered.Max(item => Math.Max(item.TagWidth, 0.01));
        double computedColumnLeft = settings.PlaceLeft
            ? elementEdge - settings.OffsetFromElements - maximumTagWidth
            : elementEdge + settings.OffsetFromElements;
        double proposedInnerEdge = settings.PlaceLeft
            ? computedColumnLeft + maximumTagWidth
            : computedColumnLeft;
        double snappedInnerEdge = forcedColumnLeft.HasValue
            ? proposedInnerEdge
            : SnapToReservedRail(
                proposedInnerEdge,
                settings.PlaceLeft,
                initialReservations,
                ordered,
                settings);
        double columnLeft = forcedColumnLeft ?? (settings.PlaceLeft
            ? snappedInnerEdge - maximumTagWidth
            : snappedInnerEdge);
        var placements = new List<TagLayoutPlacement>(ordered.Count);
        var collisionPlacements = initialReservations.ToList();
        var reservedSegments = initialSegments.ToList();
        IReadOnlyDictionary<long, double> balancedRows = ComputeBalancedRows(
            ordered,
            bottom,
            top,
            settings.RowSpacing);

        foreach (LayoutTagInput tag in ordered)
        {
            double width = Math.Max(tag.TagWidth, Math.Min(settings.ColumnWidth, 0.01));
            double tagColumnLeft = columnLeft;
            double height = Math.Max(tag.TagHeight, 0.01);
            double halfHeight = height * 0.5;
            // The rail is the visible left text edge. Use the measured family
            // insertion offset here so applying the result needs only the same
            // translation, never another alignment or routing pass.
            double baseColumnHead = tagColumnLeft + width * 0.5 - tag.TextOffsetU;
            double preferred = balancedRows[tag.TagKey];
            double rowCeiling = placements.Count == 0 ? top - halfHeight - tag.TextOffsetV
                : Math.Min(top - halfHeight - tag.TextOffsetV,
                    placements[^1].TagBounds.MinV - settings.RowSpacing - halfHeight - tag.TextOffsetV);
            preferred = Math.Min(preferred, rowCeiling);
            // Search in small local increments. Full-row jumps leave only one or
            // two legal choices inside the bounded travel window and can force
            // several unresolved tags onto the exact same point.
            double step = Math.Max(
                settings.RowSpacing + 0.0025,
                height * 0.25);
            double maximumVerticalTravel = Math.Max(
                ordered.Sum(t => t.TagHeight + settings.RowSpacing),
                height * 3.0 + settings.Clearance * 4.0);
            Candidate? best = null;
            double bestQuality = double.MaxValue;
            // Broad phase once per tag/rail, not for every row and endpoint.
            // This envelope contains every reachable text box and host lane.
            // Do not extend it to the whole view: that made every candidate in
            // a full scan compare against every tag, leader and model element.
            double searchMinV = Math.Max(
                bottom,
                Math.Min(tag.Anchor.V, tag.ElementBounds.MinV) - maximumVerticalTravel - height);
            double searchMaxV = Math.Min(
                top,
                Math.Max(tag.Anchor.V, tag.ElementBounds.MaxV) + maximumVerticalTravel + height);
            var searchBounds = new LayoutRect(
                Math.Min(tagColumnLeft, Math.Min(tag.Anchor.U, tag.ElementBounds.MinU)),
                searchMinV,
                Math.Max(tagColumnLeft + width, Math.Max(tag.Anchor.U, tag.ElementBounds.MaxU)),
                searchMaxV).Expand(settings.Clearance + 1e-8);
            var localObstacles = obstacles.Where(o => searchBounds.Intersects(o.Bounds.Expand(1e-8))).ToArray();
            var localPlacements = collisionPlacements
                .Where(item => searchBounds.Intersects(item.TagBounds.Expand(settings.Clearance)))
                .ToArray();
            var localReservedSegments = reservedSegments
                .Where(segment => searchBounds.Intersects(SegmentBounds(segment).Expand(settings.Clearance)))
                .ToArray();

            for (int attempt = 0; attempt < MaximumSlotAttempts; attempt++)
            {
                CheckSearchBudget();
                int distance = (attempt + 1) / 2;
                if (best is not null && distance > bestQuality + 1e-8) break;
                double direction = attempt == 0 || attempt % 2 == 1 ? -1.0 : 1.0;
                double candidateV = attempt == 0 ? preferred : preferred + direction * distance * step;
                if (candidateV > rowCeiling + 1e-8) continue;
                if (candidateV + tag.TextOffsetV - halfHeight < bottom || candidateV + tag.TextOffsetV + halfHeight > top) continue;
                if (Math.Abs(candidateV - tag.Anchor.V) > maximumVerticalTravel + 1e-8) continue;

                bool usesElbow = Math.Abs(candidateV - tag.Anchor.V) > 1e-7;
                var head = new LayoutPoint(baseColumnHead, candidateV);
                var tagBounds = new LayoutRect(
                    tagColumnLeft,
                    candidateV + tag.TextOffsetV - halfHeight,
                    tagColumnLeft + width,
                    candidateV + tag.TextOffsetV + halfHeight);
                LayoutPoint end = CreateHostEnd(tag, candidateV, usesElbow, settings);
                // Try parallel vertical lanes on the host, without moving text
                // off the common rail or changing the top-to-bottom row order.
                for (int lane = 0; lane < (usesElbow ? 5 : 1); lane++)
                {
                double laneStep = Math.Max(settings.Clearance * 2.1, 0.01);
                double laneDelta = lane == 0 ? 0 : ((lane + 1) / 2) * laneStep * (lane % 2 == 1 ? -1 : 1);
                end = CreateHostEnd(tag, candidateV, usesElbow, settings);
                double laneU = PortableMath.Clamp(end.U + laneDelta, tag.ElementBounds.MinU, tag.ElementBounds.MaxU);
                end = end with { U = laneU };
                var elbow = new LayoutPoint(end.U, candidateV);
                var edge = new LayoutPoint(settings.PlaceLeft ? tagBounds.MaxU : tagBounds.MinU, candidateV);
                var horizontal = new LayoutSegment(edge, elbow);
                var tail = new LayoutSegment(end, elbow);
                bool outsideFrame = tagBounds.MinU < frame.MinU || tagBounds.MaxU > frame.MaxU ||
                                    tagBounds.MinV < frame.MinV || tagBounds.MaxV > frame.MaxV;
                double travelRows = Math.Abs(candidateV - preferred) / Math.Max(step, 1e-8);
                int exactSlotReuse = placements.Count(item => Math.Abs(item.Head.V - candidateV) <= 1e-8);
                double baseQuality = travelRows + lane * 0.05 + (usesElbow ? 2.0 : 0.0) +
                    exactSlotReuse * 1_000_000_000.0;
                if (best is not null && baseQuality >= bestQuality) continue;
                int collisionScore = (outsideFrame ? 10000 : 0) + CollisionScore(
                    tag,
                    tagBounds,
                    horizontal,
                    usesElbow ? tail : null,
                    localPlacements,
                    localReservedSegments,
                    localObstacles,
                    settings);
                var option = new Candidate(
                    head,
                    end,
                    elbow,
                    tagBounds,
                    usesElbow,
                    usesElbow,
                    collisionScore,
                    horizontal,
                    tail);

                // Never stack several unresolved tags at precisely the same
                // head point. In an over-constrained local group it is clearer
                // to keep distinct nearby rows and report their remaining clash.
                double quality = collisionScore + baseQuality;
                if (best is null || quality < bestQuality)
                {
                    best = option;
                    bestQuality = quality;
                }
                }
            }

            Candidate candidate = best ?? CreateFallback(
                tag,
                tagColumnLeft,
                preferred,
                width,
                halfHeight,
                settings);
            bool unresolved = candidate.CollisionScore > 0;
            placements.Add(new TagLayoutPlacement(
                tag.TagKey,
                candidate.Head,
                candidate.End,
                candidate.Elbow,
                candidate.TagBounds,
                candidate.UsesElbow,
                candidate.UsesFreeEnd,
                unresolved,
                tag.Label,
                tag.Group)
            {
                CanAnchorDuctFollowers = tag.CanAnchorDuctFollowers,
                PreferredFollowerAnchorTagKey = tag.PreferredFollowerAnchorTagKey
            });
            collisionPlacements.Add(placements[^1]);
            reservedSegments.Add(candidate.Horizontal);
            if (candidate.UsesElbow)
            {
                reservedSegments.Add(candidate.Tail);
            }
        }

        var columnBounds = new LayoutRect(
            placements.Min(item => item.TagBounds.MinU),
            placements.Min(item => item.TagBounds.MinV),
            placements.Max(item => item.TagBounds.MaxU),
            placements.Max(item => item.TagBounds.MaxV));
        return new SmartTagLayoutResult(
            placements,
            placements.Count(item => item.UsesElbow),
            placements.Count(item => item.HasClash),
            columnBounds);
    }

    private static IReadOnlyDictionary<long, double> ComputeBalancedRows(
        IReadOnlyList<LayoutTagInput> ordered,
        double bottom,
        double top,
        double rowSpacing)
    {
        int count = ordered.Count;
        var offsets = new double[count];
        for (int index = 1; index < count; index++)
        {
            double previousHalf = Math.Max(ordered[index - 1].TagHeight, 0.01) * 0.5;
            double currentHalf = Math.Max(ordered[index].TagHeight, 0.01) * 0.5;
            offsets[index] = offsets[index - 1] + previousHalf + currentHalf + rowSpacing;
        }

        var blocks = new List<IsotonicBlock>(count);
        for (int index = 0; index < count; index++)
        {
            blocks.Add(new IsotonicBlock(index, index, ordered[index].Anchor.V + offsets[index], 1));
            while (blocks.Count >= 2 && blocks[^2].Mean < blocks[^1].Mean - 1e-10)
            {
                IsotonicBlock right = blocks[^1];
                IsotonicBlock left = blocks[^2];
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add(new IsotonicBlock(
                    left.Start,
                    right.End,
                    left.Sum + right.Sum,
                    left.Count + right.Count));
            }
        }

        var centers = new double[count];
        foreach (IsotonicBlock block in blocks)
        {
            // For the common A/B case, keep the upper tag exactly straight and
            // move only the lower tag down. Larger dense blocks are balanced to
            // avoid accumulating all displacement at the bottom of the view.
            double blockLevel = block.Count == 2
                ? ordered[block.Start].Anchor.V + offsets[block.Start]
                : block.Mean;
            for (int index = block.Start; index <= block.End; index++)
            {
                centers[index] = blockLevel - offsets[index];
            }
        }

        double minimumShift = double.NegativeInfinity;
        double maximumShift = double.PositiveInfinity;
        for (int index = 0; index < count; index++)
        {
            double halfHeight = Math.Max(ordered[index].TagHeight, 0.01) * 0.5;
            minimumShift = Math.Max(minimumShift, bottom + halfHeight - centers[index]);
            maximumShift = Math.Min(maximumShift, top - halfHeight - centers[index]);
        }
        double shift = minimumShift <= maximumShift
            ? PortableMath.Clamp(0.0, minimumShift, maximumShift)
            : 0.0;

        return Enumerable.Range(0, count).ToDictionary(
            index => ordered[index].TagKey,
            index => ClampOrUseRangeMidpoint(
                centers[index] + shift,
                bottom + Math.Max(ordered[index].TagHeight, 0.01) * 0.5,
                top - Math.Max(ordered[index].TagHeight, 0.01) * 0.5));
    }

    private static double ClampOrUseRangeMidpoint(double value, double minimum, double maximum)
    {
        // A narrow/cropped view can leave less vertical room than the selected
        // project Tag Family requires. There is then no center that keeps the
        // whole tag inside the frame (minimum > maximum). Preserve the original
        // clamp for every feasible view; for an impossible range, use the unique
        // minimax center and let the existing clash flag report the overflow.
        return minimum <= maximum
            ? PortableMath.Clamp(value, minimum, maximum)
            : (minimum + maximum) * 0.5;
    }

    private static int CollisionScore(
        LayoutTagInput tag,
        LayoutRect tagBounds,
        LayoutSegment horizontal,
        LayoutSegment? tail,
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<LayoutSegment> reservedSegments,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        CheckSearchBudget();
        int score = settings.AvoidTagText
            ? placements.Count(item => tagBounds.Expand(settings.RowSpacing).Intersects(item.TagBounds)) * 10000
            : 0;

        if (settings.AvoidElements)
        {
            foreach (LayoutObstacle obstacle in obstacles)
            {
                LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                bool ownMepHost = obstacle.Kind == LayoutObstacleKind.Mep &&
                                  obstacle.ElementKey == tag.ElementKey;
                bool textClash = tagBounds.Intersects(expanded, 0.0);
                bool leaderClash = !ownMepHost &&
                    (SegmentIntersects(horizontal, expanded) ||
                    (tail is LayoutSegment tailSegment && SegmentIntersects(tailSegment, expanded)))
                    ;
                if (textClash || leaderClash)
                {
                    // Architectural text overlap is strongly discouraged. A
                    // leader crossing a wall is only a soft cost because some
                    // hosts physically sit inside/behind that wall.
                    score += textClash
                        ? 10000
                        : 0;
                    score += leaderClash
                        ? obstacle.Kind == LayoutObstacleKind.Architecture ? 1 : 2
                        : 0;
                }
            }
        }

        if (settings.AvoidLeaders)
        {
            foreach (TagLayoutPlacement placed in placements)
            {
                var box = placed.TagBounds.Expand(settings.Clearance);
                if (SegmentIntersects(horizontal, box) ||
                    (tail is LayoutSegment t && SegmentIntersects(t, box))) score += 10000;
            }
            foreach (LayoutSegment reserved in reservedSegments)
            {
                if (SegmentIntersects(reserved, tagBounds.Expand(settings.Clearance))) score += 10000;
                if (SegmentsConflict(horizontal, reserved, settings.Clearance) ||
                    (tail is LayoutSegment tailSegment &&
                     SegmentsConflict(tailSegment, reserved, settings.Clearance)))
                {
                    // A leader crossing/overlap is a routing failure, not a soft
                    // visual preference. Moving several rows or using a nearby
                    // attached elbow offset is always preferable to accepting the cut.
                    score += 3000;
                }
            }
        }

        return score;
    }

    private static bool SegmentIntersects(LayoutSegment segment, LayoutRect rect)
    {
        if (PointInside(segment.Start, rect) || PointInside(segment.End, rect)) return true;
        var bottomLeft = new LayoutPoint(rect.MinU, rect.MinV);
        var bottomRight = new LayoutPoint(rect.MaxU, rect.MinV);
        var topRight = new LayoutPoint(rect.MaxU, rect.MaxV);
        var topLeft = new LayoutPoint(rect.MinU, rect.MaxV);
        return SegmentsIntersectExact(segment, new LayoutSegment(bottomLeft, bottomRight)) ||
               SegmentsIntersectExact(segment, new LayoutSegment(bottomRight, topRight)) ||
               SegmentsIntersectExact(segment, new LayoutSegment(topRight, topLeft)) ||
               SegmentsIntersectExact(segment, new LayoutSegment(topLeft, bottomLeft));
    }

    private static bool PointInside(LayoutPoint point, LayoutRect rect) =>
        point.U >= rect.MinU && point.U <= rect.MaxU &&
        point.V >= rect.MinV && point.V <= rect.MaxV;

    private static bool SegmentsIntersectExact(LayoutSegment first, LayoutSegment second)
    {
        const double tolerance = 1e-9;
        double d1 = Cross(first.Start, first.End, second.Start);
        double d2 = Cross(first.Start, first.End, second.End);
        double d3 = Cross(second.Start, second.End, first.Start);
        double d4 = Cross(second.Start, second.End, first.End);
        if (((d1 > tolerance && d2 < -tolerance) || (d1 < -tolerance && d2 > tolerance)) &&
            ((d3 > tolerance && d4 < -tolerance) || (d3 < -tolerance && d4 > tolerance)))
        {
            return true;
        }
        return Math.Abs(d1) <= tolerance && PointOnSegment(second.Start, first) ||
               Math.Abs(d2) <= tolerance && PointOnSegment(second.End, first) ||
               Math.Abs(d3) <= tolerance && PointOnSegment(first.Start, second) ||
               Math.Abs(d4) <= tolerance && PointOnSegment(first.End, second);
    }

    private static double Cross(LayoutPoint a, LayoutPoint b, LayoutPoint point) =>
        (b.U - a.U) * (point.V - a.V) - (b.V - a.V) * (point.U - a.U);

    private static bool PointOnSegment(LayoutPoint point, LayoutSegment segment) =>
        point.U >= Math.Min(segment.Start.U, segment.End.U) - 1e-9 &&
        point.U <= Math.Max(segment.Start.U, segment.End.U) + 1e-9 &&
        point.V >= Math.Min(segment.Start.V, segment.End.V) - 1e-9 &&
        point.V <= Math.Max(segment.Start.V, segment.End.V) + 1e-9;

    private static bool SegmentsConflict(LayoutSegment first, LayoutSegment second, double clearance)
    {
        if (SegmentsIntersectExact(first, second)) return true;
        double distance = Math.Min(
            Math.Min(PointToSegmentDistance(first.Start, second), PointToSegmentDistance(first.End, second)),
            Math.Min(PointToSegmentDistance(second.Start, first), PointToSegmentDistance(second.End, first)));
        return distance < Math.Max(clearance, 1e-9);
    }

    private static double PointToSegmentDistance(LayoutPoint point, LayoutSegment segment)
    {
        double du = segment.End.U - segment.Start.U;
        double dv = segment.End.V - segment.Start.V;
        double lengthSquared = du * du + dv * dv;
        if (lengthSquared <= 1e-16)
        {
            return Math.Sqrt(
                Math.Pow(point.U - segment.Start.U, 2) +
                Math.Pow(point.V - segment.Start.V, 2));
        }
        double parameter = ((point.U - segment.Start.U) * du +
                            (point.V - segment.Start.V) * dv) / lengthSquared;
        parameter = PortableMath.Clamp(parameter, 0.0, 1.0);
        double nearestU = segment.Start.U + parameter * du;
        double nearestV = segment.Start.V + parameter * dv;
        return Math.Sqrt(Math.Pow(point.U - nearestU, 2) + Math.Pow(point.V - nearestV, 2));
    }

    private static LayoutRect SegmentBounds(LayoutSegment segment) => new(
        Math.Min(segment.Start.U, segment.End.U),
        Math.Min(segment.Start.V, segment.End.V),
        Math.Max(segment.Start.U, segment.End.U),
        Math.Max(segment.Start.V, segment.End.V));

    private static Candidate CreateFallback(
        LayoutTagInput tag,
        double columnLeft,
        double candidateV,
        double width,
        double halfHeight,
        SmartTagLayoutSettings settings)
    {
        var head = new LayoutPoint(columnLeft + width * 0.5 - tag.TextOffsetU, candidateV);
        bool usesElbow = Math.Abs(candidateV - tag.Anchor.V) > 1e-7;
        LayoutPoint end = CreateHostEnd(tag, candidateV, usesElbow, settings);
        var elbow = new LayoutPoint(end.U, candidateV);
        var bounds = new LayoutRect(
            columnLeft,
            candidateV + tag.TextOffsetV - halfHeight,
            columnLeft + width,
            candidateV + tag.TextOffsetV + halfHeight);
        return new Candidate(
            head,
            end,
            elbow,
            bounds,
            usesElbow,
            usesElbow,
            int.MaxValue,
            new LayoutSegment(head, elbow),
            new LayoutSegment(end, elbow));
    }

    private static LayoutPoint CreateHostEnd(
        LayoutTagInput tag,
        double candidateV,
        bool usesElbow,
        SmartTagLayoutSettings settings)
    {
        if (!usesElbow)
        {
            return tag.Anchor;
        }

        // A Free End may slide only a very small distance inside the host's
        // visible bounds. It remains visually attached to the correct element,
        // while separating two leaders that start at the same row.
        double direction = Math.Sign(candidateV - tag.Anchor.V);
        double availableOnHost = direction > 0
            ? tag.ElementBounds.MaxV - tag.Anchor.V
            : tag.Anchor.V - tag.ElementBounds.MinV;
        double hostShift = Math.Min(
            Math.Max(0.0, availableOnHost) * 0.5,
            Math.Max(settings.Clearance * 0.75, 0.005));
        return new LayoutPoint(tag.Anchor.U, tag.Anchor.V + direction * hostShift);
    }

    private static List<LayoutSegment> BuildSegments(
        IEnumerable<TagLayoutPlacement> placements)
    {
        var result = new List<LayoutSegment>();
        foreach (TagLayoutPlacement placement in placements)
        {
            var edge = new LayoutPoint(placement.Head.U < placement.End.U
                ? placement.TagBounds.MaxU : placement.TagBounds.MinU, placement.Head.V);
            result.Add(new LayoutSegment(edge, placement.Elbow));
            if (placement.UsesElbow)
            {
                result.Add(new LayoutSegment(placement.End, placement.Elbow));
            }
        }
        return result;
    }

    private static double SnapToReservedRail(
        double proposedInnerEdge,
        bool placeLeft,
        IReadOnlyList<TagLayoutPlacement> reservations,
        IReadOnlyList<LayoutTagInput> hosts,
        SmartTagLayoutSettings settings)
    {
        double tolerance = Math.Max(settings.Clearance, 0.02);
        double maximumSnapDistance = Math.Max(
            Math.Max(settings.ColumnWidth * 0.5, settings.OffsetFromElements * 4.0),
            0.05);
        double verticalReach = Math.Max(
            settings.ColumnWidth * 0.5,
            hosts.Max(item => item.TagHeight) * 4.0);
        double minV = hosts.Min(item => item.ElementBounds.MinV) - verticalReach;
        double maxV = hosts.Max(item => item.ElementBounds.MaxV) + verticalReach;
        var nearbyRails = reservations
            .Where(item => placeLeft ? item.Head.U < item.End.U : item.Head.U > item.End.U)
            .Where(item => item.TagBounds.MaxV >= minV && item.TagBounds.MinV <= maxV)
            .Select(item => placeLeft
                ? item.TagBounds.MinU + hosts.Max(host => Math.Max(host.TagWidth, 0.01))
                : item.TagBounds.MinU)
            .Where(rail => Math.Abs(rail - proposedInnerEdge) <= maximumSnapDistance)
            .ToList();
        if (nearbyRails.Count == 0) return proposedInnerEdge;

        return nearbyRails
            .GroupBy(rail => Math.Round(rail / tolerance))
            .Select(group => new
            {
                Rail = group.Average(),
                Count = group.Count(),
                Distance = Math.Abs(group.Average() - proposedInnerEdge)
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Distance)
            .First()
            .Rail;
    }

    private static List<List<LayoutTagInput>> BuildClusters(
        IReadOnlyList<LayoutTagInput> source,
        double maximumGap)
    {
        double span = Math.Max(maximumGap, source.Max(t => t.TagHeight) * 4.0);
        var groups = new List<List<LayoutTagInput>>();
        // Bound the whole group's span: a chain of nearby hosts must not merge
        // into one viewport-wide column.
        foreach (var tag in source.OrderByDescending(t => t.Anchor.V).ThenBy(t => t.Anchor.U).ThenBy(t => t.TagKey))
        {
            var group = groups.Where(g =>
                    Math.Max(g.Max(t => t.Anchor.U), tag.Anchor.U) - Math.Min(g.Min(t => t.Anchor.U), tag.Anchor.U) <= span &&
                    g.Min(t => Math.Abs(t.Anchor.V - tag.Anchor.V)) <= span)
                .OrderBy(g => g.Min(t => Math.Abs(t.Anchor.U - tag.Anchor.U) + Math.Abs(t.Anchor.V - tag.Anchor.V)))
                .FirstOrDefault();
            if (group is null) groups.Add([tag]);
            else group.Add(tag);
        }
        return groups;
    }

    private static double EstimateSideObstacleCost(
        LayoutTagInput tag,
        double innerEdge,
        bool placeLeft,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        if (!settings.AvoidElements) return 0.0;
        double centerU = placeLeft
            ? innerEdge - tag.TagWidth * 0.5
            : innerEdge + tag.TagWidth * 0.5;
        var proposedText = new LayoutRect(
            centerU - tag.TagWidth * 0.5,
            tag.Anchor.V - tag.TagHeight * 0.5,
            centerU + tag.TagWidth * 0.5,
            tag.Anchor.V + tag.TagHeight * 0.5);
        var leader = new LayoutSegment(new LayoutPoint(centerU, tag.Anchor.V), tag.Anchor);
        double conflicts = 0.0;
        foreach (LayoutObstacle obstacle in obstacles)
        {
            if (obstacle.Kind == LayoutObstacleKind.Mep &&
                obstacle.ElementKey == tag.ElementKey) continue;
            LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
            if (proposedText.Intersects(expanded, 0.0))
            {
                // Text over a model element is a hard side-selection failure.
                // This must dominate both leader length and left/right load;
                // otherwise a dense view can choose the shorter side even
                // though the opposite nearby rail is visibly empty.
                conflicts += obstacle.Kind == LayoutObstacleKind.Architecture
                    ? 20_000.0
                    : 10_000.0;
            }
            if (SegmentIntersects(leader, expanded))
            {
                conflicts += obstacle.Kind == LayoutObstacleKind.Architecture
                    ? 4.0
                    : 25.0;
            }
        }
        return conflicts;
    }

    private sealed record IsotonicBlock(int Start, int End, double Sum, int Count)
    {
        public double Mean => Sum / Count;
    }

    private sealed record Candidate(
        LayoutPoint Head,
        LayoutPoint End,
        LayoutPoint Elbow,
        LayoutRect TagBounds,
        bool UsesElbow,
        bool UsesFreeEnd,
        int CollisionScore,
        LayoutSegment Horizontal,
        LayoutSegment Tail);
}
