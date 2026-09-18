namespace FamilyMEP.Plugin.SmartTag;

internal sealed record SmartTagGuidedZoneEvaluation(
    SmartTagLayoutResult Layout,
    int Required,
    int Capacity,
    bool Fits,
    bool PlacesOnLeft,
    string Message);

internal sealed record SmartTagGuidedZoneSuggestion(
    LayoutRect Zone,
    SmartTagGuidedZoneEvaluation Evaluation,
    double Score,
    string Message);

/// <summary>
/// Places one explicitly selected host set into one explicitly selected empty
/// text zone. The solver is intentionally separate from Standard so the
/// accepted automatic layout can never regress when Guided Zones is changed.
/// </summary>
internal static class SmartTagGuidedZoneLayout
{
    private const double Epsilon = 1e-8;

    /// <summary>
    /// Searches the current view for a tag-text zone that is large enough for
    /// the complete scanned set.  Candidate columns are tested with the same
    /// solver used by Analyze, so a suggestion is not merely a free-looking
    /// rectangle: real Tag Family sizes, row capacity, MEP obstacles and
    /// leader crossings all participate in the score.
    /// </summary>
    public static SmartTagGuidedZoneSuggestion SuggestZone(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        if (source.Count == 0)
        {
            throw new InvalidOperationException(
                "Scan at least one checked MEP category before suggesting a tag zone.");
        }

        List<LayoutTagInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();
        double maximumWidth = ordered.Max(item => Math.Max(item.TagWidth, 0.01));
        double maximumHeight = ordered.Max(item => Math.Max(item.TagHeight, 0.01));
        double clearance = Math.Max(settings.Clearance, 0.01);
        double margin = Math.Max(settings.TopMargin, clearance);
        double pitch = maximumHeight + settings.RowSpacing;
        var innerFrame = new LayoutRect(
            frame.MinU + margin,
            frame.MinV + margin,
            frame.MaxU - margin,
            frame.MaxV - margin);
        if (innerFrame.Width <= maximumWidth + clearance * 2.0 ||
            innerFrame.Height <= maximumHeight + clearance * 2.0)
        {
            throw new InvalidOperationException(
                "The active view does not contain enough usable space for the selected project Tag Family size.");
        }

        double hostMinU = ordered.Min(item => item.ElementBounds.MinU);
        double hostMaxU = ordered.Max(item => item.ElementBounds.MaxU);
        double hostCenterV = ordered.Average(item => item.Anchor.V);
        double singleWidth = maximumWidth + clearance * 2.0;
        int singleRows = ordered.Count;
        int splitRows = (ordered.Count + 1) / 2;

        var candidates = new List<LayoutRect>();
        if (!settings.AutoSide && settings.PlaceLeft)
        {
            AddSingleSideCandidates(
                candidates,
                placeLeft: true,
                singleRows,
                singleWidth,
                maximumHeight,
                pitch,
                clearance,
                hostMinU,
                hostMaxU,
                hostCenterV,
                innerFrame,
                obstacles,
                settings.OffsetFromElements);
        }
        if (!settings.AutoSide && !settings.PlaceLeft)
        {
            AddSingleSideCandidates(
                candidates,
                placeLeft: false,
                singleRows,
                singleWidth,
                maximumHeight,
                pitch,
                clearance,
                hostMinU,
                hostMaxU,
                hostCenterV,
                innerFrame,
                obstacles,
                settings.OffsetFromElements);
        }
        if (settings.AutoSide)
        {
            AddSplitCandidates(
                candidates,
                splitRows,
                singleWidth,
                maximumHeight,
                pitch,
                clearance,
                hostMinU,
                hostMaxU,
                hostCenterV,
                innerFrame,
                obstacles,
                settings.OffsetFromElements);
        }

        SmartTagGuidedZoneSuggestion? best = null;
        // Candidate generation deliberately over-samples obstacle edges. Run
        // the real solver only on the best bounded shortlist so a 100+ tag
        // scan does not repeatedly execute the expensive global-order pass.
        foreach (LayoutRect candidate in candidates
                     .Distinct()
                     .OrderBy(item => QuickCandidateScore(
                         item,
                         obstacles,
                         maximumWidth,
                         hostMinU,
                         hostMaxU,
                         hostCenterV,
                         settings))
                     .Take(48))
        {
            try
            {
                SmartTagGuidedZoneEvaluation evaluation = ComputeCore(
                    ordered,
                    obstacles,
                    frame,
                    settings with { GuidedZone = candidate },
                    optimizeOrder: false);
                double score = SuggestionScore(
                    evaluation.Layout.Placements,
                    ordered,
                    obstacles,
                    candidate,
                    settings);
                if (best is null || score < best.Score - Epsilon)
                {
                    best = new SmartTagGuidedZoneSuggestion(
                        candidate,
                        evaluation,
                        score,
                        $"Suggested clear zone fits all {ordered.Count} tag(s); " +
                        $"{evaluation.Layout.ClashCount} leader warning(s) remain before the final Analyze pass.");
                }
            }
            catch (InvalidOperationException)
            {
                // A candidate can have enough geometric height but still be
                // blocked on one or more real text rows. Continue searching.
            }
        }

        if (best is not null)
        {
            // Only the winning zone receives the full tag-order optimization.
            SmartTagGuidedZoneEvaluation optimized = Compute(
                ordered,
                obstacles,
                frame,
                settings with { GuidedZone = best.Zone });
            return best with
            {
                Evaluation = optimized,
                Score = SuggestionScore(
                    optimized.Layout.Placements,
                    ordered,
                    obstacles,
                    best.Zone,
                    settings),
                Message = $"Suggested clear zone fits all {ordered.Count} tag(s); " +
                          $"{optimized.Layout.ClashCount} leader warning(s) remain before the final Analyze pass."
            };
        }
        throw new InvalidOperationException(
            "No single clear zone can hold every scanned tag in the current view. " +
            "Try Auto Left + Right, reduce Tag Gap, or scan a smaller local MEP group.");
    }

    private static void AddSingleSideCandidates(
        List<LayoutRect> result,
        bool placeLeft,
        int requiredRows,
        double zoneWidth,
        double maximumHeight,
        double pitch,
        double clearance,
        double hostMinU,
        double hostMaxU,
        double hostCenterV,
        LayoutRect frame,
        IReadOnlyList<LayoutObstacle> obstacles,
        double offset)
    {
        if (zoneWidth > frame.Width + Epsilon) return;
        var starts = new List<double>();
        if (placeLeft)
        {
            starts.Add(hostMinU - offset - zoneWidth);
            starts.Add(frame.MinU);
            foreach (LayoutObstacle obstacle in obstacles)
            {
                starts.Add(obstacle.Bounds.MinU - clearance - zoneWidth);
                starts.Add(obstacle.Bounds.MaxU + clearance);
            }
        }
        else
        {
            starts.Add(hostMaxU + offset);
            starts.Add(frame.MaxU - zoneWidth);
            foreach (LayoutObstacle obstacle in obstacles)
            {
                starts.Add(obstacle.Bounds.MaxU + clearance);
                starts.Add(obstacle.Bounds.MinU - clearance - zoneWidth);
            }
        }

        foreach (double start in starts
                     .Select(value => ClampStart(value, zoneWidth, frame.MinU, frame.MaxU))
                     .Distinct()
                     .OrderBy(value => placeLeft
                         ? Math.Abs(value + zoneWidth - (hostMinU - offset))
                         : Math.Abs(value - (hostMaxU + offset)))
                     .Take(14))
        {
            double end = start + zoneWidth;
            if (placeLeft && end > hostMinU - Math.Min(offset, clearance) + Epsilon) continue;
            if (!placeLeft && start < hostMaxU + Math.Min(offset, clearance) - Epsilon) continue;
            AddVerticalCandidates(
                result,
                start,
                end,
                requiredRows,
                maximumHeight,
                pitch,
                clearance,
                hostCenterV,
                frame,
                obstacles);
        }
    }

    private static void AddSplitCandidates(
        List<LayoutRect> result,
        int requiredRows,
        double columnZoneWidth,
        double maximumHeight,
        double pitch,
        double clearance,
        double hostMinU,
        double hostMaxU,
        double hostCenterV,
        LayoutRect frame,
        IReadOnlyList<LayoutObstacle> obstacles,
        double offset)
    {
        if (frame.Width < columnZoneWidth * 2.0 + clearance) return;
        var leftStarts = new List<double>
        {
            hostMinU - offset - columnZoneWidth,
            frame.MinU
        };
        var rightEnds = new List<double>
        {
            hostMaxU + offset + columnZoneWidth,
            frame.MaxU
        };
        foreach (LayoutObstacle obstacle in obstacles)
        {
            leftStarts.Add(obstacle.Bounds.MinU - clearance - columnZoneWidth);
            leftStarts.Add(obstacle.Bounds.MaxU + clearance);
            rightEnds.Add(obstacle.Bounds.MaxU + clearance + columnZoneWidth);
            rightEnds.Add(obstacle.Bounds.MinU - clearance);
        }

        double[] left = leftStarts
            .Select(value => ClampStart(value, columnZoneWidth, frame.MinU, frame.MaxU))
            .Where(value => value + columnZoneWidth <= hostMinU - Math.Min(offset, clearance) + Epsilon)
            .Distinct()
            .OrderBy(value => Math.Abs(value + columnZoneWidth - (hostMinU - offset)))
            .Take(10)
            .ToArray();
        double[] right = rightEnds
            .Select(value => PortableMath.Clamp(
                value,
                Math.Min(frame.MaxU, frame.MinU + columnZoneWidth),
                frame.MaxU))
            .Where(value => value - columnZoneWidth >= hostMaxU + Math.Min(offset, clearance) - Epsilon)
            .Distinct()
            .OrderBy(value => Math.Abs(value - columnZoneWidth - (hostMaxU + offset)))
            .Take(10)
            .ToArray();

        foreach (double leftStart in left)
        foreach (double rightEnd in right)
        {
            if (rightEnd - leftStart < columnZoneWidth * 2.0 + clearance) continue;
            AddVerticalCandidates(
                result,
                leftStart,
                rightEnd,
                requiredRows,
                maximumHeight,
                pitch,
                clearance,
                hostCenterV,
                frame,
                obstacles);
        }
    }

    private static void AddVerticalCandidates(
        List<LayoutRect> result,
        double minU,
        double maxU,
        int requiredRows,
        double maximumHeight,
        double pitch,
        double clearance,
        double hostCenterV,
        LayoutRect frame,
        IReadOnlyList<LayoutObstacle> obstacles)
    {
        double exactHeight = requiredRows * maximumHeight +
                             Math.Max(0, requiredRows - 1) * (pitch - maximumHeight) +
                             clearance * 2.0 + 1e-5;
        var heights = new List<double> { exactHeight };
        if (exactHeight + pitch <= frame.Height + Epsilon) heights.Add(exactHeight + pitch);
        if (exactHeight + pitch * 2.0 <= frame.Height + Epsilon) heights.Add(exactHeight + pitch * 2.0);

        foreach (double height in heights.Distinct())
        {
            if (height > frame.Height + Epsilon) continue;
            var starts = new List<double>
            {
                hostCenterV - height * 0.5,
                frame.MinV,
                frame.MaxV - height
            };
            foreach (LayoutObstacle obstacle in obstacles)
            {
                starts.Add(obstacle.Bounds.MaxV + clearance);
                starts.Add(obstacle.Bounds.MinV - clearance - height);
            }
            foreach (double start in starts
                         .Select(value => ClampStart(value, height, frame.MinV, frame.MaxV))
                         .Distinct()
                         .OrderBy(value => Math.Abs(value + height * 0.5 - hostCenterV))
                         .Take(24))
            {
                result.Add(new LayoutRect(minU, start, maxU, start + height));
            }
        }
    }

    private static double ClampStart(double value, double size, double minimum, double maximum) =>
        PortableMath.Clamp(value, minimum, Math.Max(minimum, maximum - size));

    private static double QuickCandidateScore(
        LayoutRect zone,
        IReadOnlyList<LayoutObstacle> obstacles,
        double tagWidth,
        double hostMinU,
        double hostMaxU,
        double hostCenterV,
        SmartTagLayoutSettings settings)
    {
        bool spansHosts = settings.AutoSide &&
                          zone.MinU < hostMinU &&
                          zone.MaxU > hostMaxU &&
                          zone.Width >= tagWidth * 2.0 + settings.Clearance;
        LayoutRect[] textColumns = spansHosts
            ?
            [
                new LayoutRect(zone.MinU, zone.MinV, zone.MinU + tagWidth, zone.MaxV),
                new LayoutRect(zone.MaxU - tagWidth, zone.MinV, zone.MaxU, zone.MaxV)
            ]
            : [zone];
        int blocked = textColumns.Sum(column => obstacles.Count(obstacle =>
            column.Intersects(obstacle.Bounds.Expand(settings.Clearance))));
        double horizontalTravel = zone.MaxU <= hostMinU
            ? hostMinU - zone.MaxU
            : zone.MinU >= hostMaxU
                ? zone.MinU - hostMaxU
                : Math.Min(
                    Math.Abs(hostMinU - (zone.MinU + tagWidth)),
                    Math.Abs(zone.MaxU - tagWidth - hostMaxU));
        double verticalTravel = Math.Abs((zone.MinV + zone.MaxV) * 0.5 - hostCenterV);
        return blocked * 1_000_000.0 +
               horizontalTravel * 10.0 +
               verticalTravel * 2.0 +
               zone.Width * zone.Height * 0.001;
    }

    private static double SuggestionScore(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect zone,
        SmartTagLayoutSettings settings)
    {
        IReadOnlyDictionary<long, LayoutTagInput> byKey = inputs.ToDictionary(item => item.TagKey);
        double score = placements.Count(item => item.HasClash) * 10_000_000.0;
        for (int index = 0; index < placements.Count; index++)
        {
            TagLayoutPlacement placement = placements[index];
            LayoutTagInput input = byKey[placement.TagKey];
            score += Math.Abs(placement.Head.V - input.Anchor.V) * 4.0;
            score += Math.Abs(placement.Head.U - input.Anchor.U);
            foreach (LayoutObstacle obstacle in obstacles)
            {
                if (obstacle.ElementKey == input.ElementKey) continue;
                LayoutRect blocked = obstacle.Bounds.Expand(settings.Clearance);
                if (placement.TagBounds.Intersects(blocked)) score += 50_000_000.0;
                foreach (LayoutSegment segment in Segments(placement))
                {
                    if (SegmentIntersectsRect(segment, blocked)) score += 100_000.0;
                }
            }
            for (int otherIndex = index + 1; otherIndex < placements.Count; otherIndex++)
            {
                TagLayoutPlacement other = placements[otherIndex];
                if (placement.TagBounds.Intersects(other.TagBounds.Expand(settings.Clearance)))
                {
                    score += 50_000_000.0;
                }
                foreach (LayoutSegment first in Segments(placement))
                foreach (LayoutSegment second in Segments(other))
                {
                    if (SegmentsIntersect(first, second)) score += 1_000_000.0;
                }
            }
        }
        // When collision quality is equal, prefer a compact nearby zone.
        score += zone.Width * zone.Height * 0.001;
        return score;
    }

    public static SmartTagGuidedZoneEvaluation Compute(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings) =>
        ComputeCore(source, obstacles, frame, settings, optimizeOrder: true);

    private static SmartTagGuidedZoneEvaluation ComputeCore(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        bool optimizeOrder)
    {
        if (source.Count == 0)
        {
            return new SmartTagGuidedZoneEvaluation(
                new SmartTagLayoutResult([], 0, 0, frame),
                0,
                0,
                true,
                settings.PlaceLeft,
                "No selected tags.");
        }

        LayoutRect zone = settings.GuidedZone
            ?? throw new InvalidOperationException(
                "Guided Zones requires a tag zone. Click PICK TAG ZONE and drag the empty Revit area first.");
        LayoutRect usable = Intersect(
            new LayoutRect(
                zone.MinU + settings.Clearance,
                zone.MinV + settings.Clearance,
                zone.MaxU - settings.Clearance,
                zone.MaxV - settings.Clearance),
            frame);
        if (usable.Width <= Epsilon || usable.Height <= Epsilon)
        {
            throw new InvalidOperationException(
                "The selected tag zone is outside the active view or too small after clearance.");
        }

        List<LayoutTagInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();
        double maximumWidth = ordered.Max(item => Math.Max(item.TagWidth, 0.01));
        double maximumHeight = ordered.Max(item => Math.Max(item.TagHeight, 0.01));
        if (maximumWidth > usable.Width + Epsilon)
        {
            throw new InvalidOperationException(
                $"Tag zone width is too small. Required {maximumWidth:0.###} view ft, available {usable.Width:0.###} view ft.");
        }

        double pitch = maximumHeight + settings.RowSpacing;
        int naturalRowCapacity = Math.Max(
            0,
            (int)Math.Floor((usable.Height + settings.RowSpacing + Epsilon) /
                            Math.Max(pitch, 0.01)));
        if (usable.Height + Epsilon < maximumHeight)
        {
            throw new InvalidOperationException(
                $"The picked zone is physically shorter than the tallest project Tag Family " +
                $"({usable.Height:0.###} available, {maximumHeight:0.###} view ft required). " +
                "Increase only the zone height; the scanned element set is preserved.");
        }

        double hostMinU = ordered.Min(item => item.ElementBounds.MinU);
        double hostMaxU = ordered.Max(item => item.ElementBounds.MaxU);
        bool zoneIsLeftOfHosts = usable.MaxU <= hostMinU + settings.Clearance;
        bool zoneIsRightOfHosts = usable.MinU >= hostMaxU - settings.Clearance;
        bool forcedSingleColumn = settings.AutoSide &&
                                  usable.Width < maximumWidth * 2.0 + settings.Clearance;
        bool splitAcrossBothSides = settings.AutoSide && !forcedSingleColumn;

        bool singleColumnOnLeft = settings.AutoSide
            ? zoneIsLeftOfHosts || (!zoneIsRightOfHosts &&
                                    (usable.MinU + usable.MaxU) * 0.5 <
                                    (hostMinU + hostMaxU) * 0.5)
            : settings.PlaceLeft;
        int columnCount = splitAcrossBothSides ? 2 : 1;
        int naturalCapacity = naturalRowCapacity * columnCount;
        int requiredRowsPerColumn = splitAcrossBothSides
            ? (ordered.Count + 1) / 2
            : ordered.Count;

        var leftTags = new List<LayoutTagInput>();
        var rightTags = new List<LayoutTagInput>();
        if (!splitAcrossBothSides)
        {
            (singleColumnOnLeft ? leftTags : rightTags).AddRange(ordered);
        }
        else
        {
            SplitByNearestSide(
                ordered,
                usable,
                maximumWidth,
                requiredRowsPerColumn,
                leftTags,
                rightTags);
        }

        int forcedRowCount = Math.Max(leftTags.Count, rightTags.Count);
        // Generate every natural row that physically fits in the picked zone,
        // not only exactly one row per tag. The assignment solver can then
        // leave rows crossing ducts/fittings unused and move an ordered stack
        // into the clear space above or below the model cluster.
        int availableRowCount = Math.Max(forcedRowCount, naturalRowCapacity);
        double[] naturalSlotRows = CreateRowsInsideZone(
            usable,
            maximumHeight,
            pitch,
            availableRowCount);

        LayoutRect? leftColumn = leftTags.Count == 0
            ? null
            : splitAcrossBothSides
                ? new LayoutRect(usable.MinU, usable.MinV,
                    usable.MinU + maximumWidth, usable.MaxV)
                : usable;
        LayoutRect? rightColumn = rightTags.Count == 0
            ? null
            : splitAcrossBothSides
                ? new LayoutRect(usable.MaxU - maximumWidth, usable.MinV,
                    usable.MaxU, usable.MaxV)
                : usable;
        // Keep one compact, uniformly pitched row block for the whole Guided
        // group. The old independent DP assignment could skip different free
        // rows in the left and right columns, producing ragged high/low tags
        // even though every individual row was valid. We may move the complete
        // block above or below MEP, but never stretch its internal spacing.
        double[] slotRows = SelectAlignedRowBlock(
            naturalSlotRows,
            forcedRowCount,
            leftTags,
            rightTags,
            leftColumn,
            rightColumn,
            obstacles,
            settings);

        var placements = new List<TagLayoutPlacement>(ordered.Count);
        if (leftColumn is LayoutRect actualLeftColumn)
        {
            placements.AddRange(BuildColumn(
                leftTags, slotRows, actualLeftColumn, true, obstacles, settings, optimizeOrder));
        }
        if (rightColumn is LayoutRect actualRightColumn)
        {
            placements.AddRange(BuildColumn(
                rightTags, slotRows, actualRightColumn, false, obstacles, settings, optimizeOrder));
        }

        IReadOnlyDictionary<long, LayoutTagInput> inputsByKey =
            ordered.ToDictionary(item => item.TagKey);
        if (optimizeOrder)
        {
            placements = RepairGuidedColumnRails(
                placements, inputsByKey, obstacles, usable, settings);
        }
        placements = ResolveLeaderConflicts(
            placements,
            inputsByKey,
            obstacles,
            settings);

        // A picked Guided Zone is authoritative, but a fixed rail at each
        // zone edge can still run straight through MEP geometry. Perform a
        // bounded repair inside the same zone: keep the assigned row/order,
        // then test additional local text rails and host-end positions. This
        // removes model/text and leader/leader conflicts during Analyze rather
        // than postponing every correction until Write.
        if (optimizeOrder)
        {
            placements = RepairInsideGuidedZone(
                placements,
                inputsByKey,
                obstacles,
                usable,
                settings);
        }

        placements = placements
            .Select(item => item with
            {
                HasClash = PlacementCollisionScore(
                    item,
                    ordered.First(input => input.TagKey == item.TagKey).ElementKey,
                    placements.Where(other => other.TagKey != item.TagKey).ToList(),
                    obstacles,
                    settings) > 0
            })
            .ToList();
        var columnBounds = new LayoutRect(
            placements.Min(item => item.TagBounds.MinU),
            placements.Min(item => item.TagBounds.MinV),
            placements.Max(item => item.TagBounds.MaxU),
            placements.Max(item => item.TagBounds.MaxV));
        var layout = new SmartTagLayoutResult(
            placements,
            placements.Count(item => item.UsesElbow),
            placements.Count(item => item.HasClash),
            columnBounds);
        string side = splitAcrossBothSides
            ? "left + right"
            : singleColumnOnLeft ? "left" : "right";
        bool fitsNaturally = naturalCapacity >= ordered.Count;
        string packing = fitsNaturally
            ? $"Guided zone contains {ordered.Count}/{naturalCapacity} natural positions"
            : $"Forced all {ordered.Count} tag(s) into a zone with {naturalCapacity} natural positions";
        if (forcedSingleColumn)
            packing += "; Auto used one compressed column because two real tag widths do not fit";
        return new SmartTagGuidedZoneEvaluation(
            layout,
            ordered.Count,
            naturalCapacity,
            fitsNaturally,
            !splitAcrossBothSides && singleColumnOnLeft,
            $"{packing} on {side}; " +
            $"{leftTags.Count} left, {rightTags.Count} right, {layout.ClashCount} remaining clash(es). " +
            "The picked zone was kept; all tag text remains inside it.");
    }

    private static double[] CreateRowsInsideZone(
        LayoutRect usable,
        double maximumHeight,
        double naturalPitch,
        int count)
    {
        if (count <= 0) return [];
        double top = usable.MaxV - maximumHeight * 0.5;
        double bottom = usable.MinV + maximumHeight * 0.5;
        if (count == 1) return [(top + bottom) * 0.5];

        double availableCenterSpan = Math.Max(0.0, top - bottom);
        double actualPitch = Math.Min(naturalPitch, availableCenterSpan / (count - 1));
        double usedSpan = actualPitch * (count - 1);
        double first = (top + bottom + usedSpan) * 0.5;
        return Enumerable.Range(0, count)
            .Select(index => first - index * actualPitch)
            .ToArray();
    }

    private static double[] SelectAlignedRowBlock(
        IReadOnlyList<double> naturalRows,
        int requiredRowCount,
        IReadOnlyList<LayoutTagInput> leftTags,
        IReadOnlyList<LayoutTagInput> rightTags,
        LayoutRect? leftColumn,
        LayoutRect? rightColumn,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        if (requiredRowCount <= 0 || naturalRows.Count <= requiredRowCount)
            return naturalRows.ToArray();

        IReadOnlyDictionary<long, LayoutTagInput> inputs = leftTags
            .Concat(rightTags)
            .ToDictionary(item => item.TagKey);
        double[]? bestRows = null;
        long bestCritical = long.MaxValue;
        double bestTravel = double.MaxValue;
        for (int start = 0; start + requiredRowCount <= naturalRows.Count; start++)
        {
            double[] rows = naturalRows
                .Skip(start)
                .Take(requiredRowCount)
                .ToArray();
            var trial = new List<TagLayoutPlacement>(inputs.Count);
            if (leftColumn is LayoutRect actualLeft)
            {
                trial.AddRange(BuildColumn(
                    leftTags, rows, actualLeft, true, obstacles, settings, optimizeOrder: false));
            }
            if (rightColumn is LayoutRect actualRight)
            {
                trial.AddRange(BuildColumn(
                    rightTags, rows, actualRight, false, obstacles, settings, optimizeOrder: false));
            }

            long critical = 0;
            double travel = 0.0;
            foreach (TagLayoutPlacement placement in trial)
            {
                LayoutTagInput input = inputs[placement.TagKey];
                critical += PlacementCollisionScore(
                    placement,
                    input.ElementKey,
                    trial.Where(other => other.TagKey != placement.TagKey).ToList(),
                    obstacles,
                    settings);
                travel += Math.Abs(placement.Head.V - input.Anchor.V);
            }
            if (critical < bestCritical ||
                critical == bestCritical && travel < bestTravel - Epsilon)
            {
                bestRows = rows;
                bestCritical = critical;
                bestTravel = travel;
            }
        }
        return bestRows ?? naturalRows.Take(requiredRowCount).ToArray();
    }

    private static void SplitByNearestSide(
        IReadOnlyList<LayoutTagInput> ordered,
        LayoutRect usable,
        double maximumWidth,
        int rowCapacity,
        List<LayoutTagInput> left,
        List<LayoutTagInput> right)
    {
        double leftRail = usable.MinU + maximumWidth;
        double rightRail = usable.MaxU - maximumWidth;
        int minimumLeft = Math.Max(0, ordered.Count - rowCapacity);
        int maximumLeft = Math.Min(rowCapacity, ordered.Count);
        int desiredLeft = PortableMath.Clamp((ordered.Count + 1) / 2, minimumLeft, maximumLeft);

        // Rank by actual horizontal travel, then take a capacity-safe quota.
        // This preserves nearest-side intent without allowing Auto to collapse
        // all tags onto one rail merely because the host set is off-centre.
        List<LayoutTagInput> leftChoice = ordered
            .OrderBy(tag =>
                Math.Abs(tag.Anchor.U - leftRail) -
                Math.Abs(rightRail - tag.Anchor.U))
            .ThenByDescending(tag => tag.Anchor.V)
            .ThenBy(tag => tag.TagKey)
            .Take(desiredLeft)
            .ToList();
        HashSet<long> leftKeys = leftChoice.Select(tag => tag.TagKey).ToHashSet();
        foreach (LayoutTagInput tag in ordered)
            (leftKeys.Contains(tag.TagKey) ? left : right).Add(tag);
    }

    private static IReadOnlyList<TagLayoutPlacement> BuildColumn(
        IReadOnlyList<LayoutTagInput> tags,
        IReadOnlyList<double> slotRows,
        LayoutRect column,
        bool placesOnLeft,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings,
        bool optimizeOrder)
    {
        List<LayoutTagInput> ordered = tags
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();
        int[] assignment = AssignRows(
            ordered,
            slotRows,
            column,
            placesOnLeft,
            obstacles,
            settings.Clearance,
            analyzeLeaderRoutes: optimizeOrder);
        if (optimizeOrder)
        {
            assignment = OptimizeAssignments(
                ordered,
                assignment,
                slotRows,
                column,
                obstacles,
                settings);
        }
        return BuildRawPlacements(ordered, assignment, slotRows, column, settings);
    }

    private static List<TagLayoutPlacement> BuildRawPlacements(
        IReadOnlyList<LayoutTagInput> ordered,
        IReadOnlyList<int> assignment,
        IReadOnlyList<double> slotRows,
        LayoutRect column,
        SmartTagLayoutSettings settings)
    {
        var result = new List<TagLayoutPlacement>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            LayoutTagInput tag = ordered[index];
            double row = slotRows[assignment[index]];
            double width = Math.Max(tag.TagWidth, 0.01);
            double height = Math.Max(tag.TagHeight, 0.01);
            // Guided Zones are user-picked text columns.  Lock the visible
            // left edge for both the left and right columns so long project
            // Tag Family labels do not protrude and make an otherwise ordered
            // stack look ragged.  Side still controls the leader direction;
            // it must not change the text-edge alignment rule.
            double left = column.MinU;
            var bounds = new LayoutRect(
                left, row - height * 0.5, left + width, row + height * 0.5);
            bool usesElbow = Math.Abs(row - tag.Anchor.V) > 1e-7;
            var head = new LayoutPoint(left + width * 0.5, row);
            LayoutPoint end = CreateHostEnd(tag, row, usesElbow, settings.Clearance);
            var elbow = usesElbow ? new LayoutPoint(end.U, row) : end;
            result.Add(new TagLayoutPlacement(
                tag.TagKey, head, end, elbow, bounds, usesElbow, usesElbow,
                false, tag.Label, tag.Group)
            {
                CanAnchorDuctFollowers = tag.CanAnchorDuctFollowers,
                PreferredFollowerAnchorTagKey = tag.PreferredFollowerAnchorTagKey
            });
        }
        return result;
    }

    private static int[] OptimizeAssignments(
        IReadOnlyList<LayoutTagInput> tags,
        IReadOnlyList<int> initial,
        IReadOnlyList<double> rows,
        LayoutRect column,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        if (tags.Count < 2) return initial.ToArray();
        int[] best = initial.ToArray();
        AssignmentQuality bestQuality = EvaluateAssignments(
            tags, best, rows, column, obstacles, settings);
        if (bestQuality.Critical == 0) return best;

        // Bounded local search: the host order remains the default, but tags
        // within a small neighbourhood may exchange rows when that removes an
        // actual text/model or leader/leader collision. This is the explicit
        // "which tag goes first" pass; it runs before Free End repair.
        for (int pass = 0; pass < 3 && bestQuality.Critical > 0; pass++)
        {
            bool improved = false;
            for (int distance = 1; distance <= Math.Min(4, tags.Count - 1); distance++)
            for (int first = 0; first + distance < tags.Count; first++)
            {
                int second = first + distance;
                (best[first], best[second]) = (best[second], best[first]);
                AssignmentQuality candidate = EvaluateAssignments(
                    tags, best, rows, column, obstacles, settings);
                if (IsBetter(candidate, bestQuality))
                {
                    bestQuality = candidate;
                    improved = true;
                }
                else
                {
                    (best[first], best[second]) = (best[second], best[first]);
                }
            }
            if (!improved) break;
        }
        return best;
    }

    private static AssignmentQuality EvaluateAssignments(
        IReadOnlyList<LayoutTagInput> tags,
        IReadOnlyList<int> assignment,
        IReadOnlyList<double> rows,
        LayoutRect column,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        List<TagLayoutPlacement> placements = BuildRawPlacements(
            tags, assignment, rows, column, settings);
        int critical = 0;
        double travel = 0.0;
        for (int index = 0; index < placements.Count; index++)
        {
            TagLayoutPlacement placement = placements[index];
            LayoutTagInput input = tags[index];
            travel += Math.Abs(placement.Head.V - input.Anchor.V);
            if (settings.AvoidElements)
            {
                foreach (LayoutObstacle obstacle in obstacles)
                {
                    if (obstacle.ElementKey == input.ElementKey) continue;
                    LayoutRect blocked = obstacle.Bounds.Expand(settings.Clearance);
                    if (placement.TagBounds.Intersects(blocked)) critical += 1000;
                    foreach (LayoutSegment segment in Segments(placement))
                    {
                        if (SegmentIntersectsRect(segment, blocked)) critical += 3;
                    }
                }
            }
            for (int otherIndex = index + 1; otherIndex < placements.Count; otherIndex++)
            {
                TagLayoutPlacement other = placements[otherIndex];
                if (settings.AvoidTagText &&
                    placement.TagBounds.Intersects(other.TagBounds.Expand(settings.Clearance)))
                {
                    critical += 1000;
                }
                if (!settings.AvoidLeaders) continue;
                foreach (LayoutSegment first in Segments(placement))
                {
                    if (SegmentIntersectsRect(first, other.TagBounds.Expand(settings.Clearance)))
                    {
                        critical += 50;
                    }
                    foreach (LayoutSegment second in Segments(other))
                    {
                        if (SegmentsIntersect(first, second)) critical += 10;
                    }
                }
            }
        }
        int inversions = 0;
        for (int first = 0; first < assignment.Count; first++)
        for (int second = first + 1; second < assignment.Count; second++)
        {
            if (assignment[first] > assignment[second]) inversions++;
        }
        return new AssignmentQuality(critical, inversions, travel);
    }

    private static bool IsBetter(AssignmentQuality candidate, AssignmentQuality current) =>
        candidate.Critical < current.Critical ||
        candidate.Critical == current.Critical && candidate.Inversions < current.Inversions ||
        candidate.Critical == current.Critical && candidate.Inversions == current.Inversions &&
        candidate.Travel < current.Travel - Epsilon;

    private readonly record struct AssignmentQuality(
        int Critical,
        int Inversions,
        double Travel);

    private static int[] AssignRows(
        IReadOnlyList<LayoutTagInput> tags,
        IReadOnlyList<double> rows,
        LayoutRect usable,
        bool placesOnLeft,
        IReadOnlyList<LayoutObstacle> obstacles,
        double clearance,
        bool analyzeLeaderRoutes)
    {
        int tagCount = tags.Count;
        int rowCount = rows.Count;
        var costs = new double[tagCount + 1, rowCount + 1];
        var take = new bool[tagCount + 1, rowCount + 1];
        const double infinity = 1e100;
        for (int tag = 1; tag <= tagCount; tag++) costs[tag, 0] = infinity;

        for (int tagIndex = 1; tagIndex <= tagCount; tagIndex++)
        {
            LayoutTagInput tag = tags[tagIndex - 1];
            for (int rowIndex = 1; rowIndex <= rowCount; rowIndex++)
            {
                costs[tagIndex, rowIndex] = costs[tagIndex, rowIndex - 1];
                double width = Math.Max(tag.TagWidth, 0.01);
                double height = Math.Max(tag.TagHeight, 0.01);
                // BuildColumn locks the visible left text edge on both sides.
                // Capacity/obstacle analysis must inspect that exact rectangle,
                // otherwise Analyze can approve a row whose rendered tag later
                // lands on a different MEP element.
                double left = usable.MinU;
                var bounds = new LayoutRect(
                    left,
                    rows[rowIndex - 1] - height * 0.5,
                    left + width,
                    rows[rowIndex - 1] + height * 0.5);
                LayoutSegment[] candidateSegments = [];
                if (analyzeLeaderRoutes)
                {
                    double row = rows[rowIndex - 1];
                    bool usesElbow = Math.Abs(row - tag.Anchor.V) > Epsilon;
                    LayoutPoint end = CreateHostEnd(tag, row, usesElbow, clearance);
                    var head = new LayoutPoint(left + width * 0.5, row);
                    LayoutPoint elbow = usesElbow ? new LayoutPoint(end.U, row) : end;
                    candidateSegments = usesElbow
                        ? [new LayoutSegment(head, elbow), new LayoutSegment(end, elbow)]
                        : [new LayoutSegment(head, end)];
                }
                int blockedText = 0;
                int blockedLeaders = 0;
                foreach (LayoutObstacle obstacle in obstacles)
                {
                    if (obstacle.ElementKey == tag.ElementKey) continue;
                    LayoutRect blocked = obstacle.Bounds.Expand(clearance);
                    if (blocked.Intersects(bounds)) blockedText++;
                    if (analyzeLeaderRoutes)
                    {
                        blockedLeaders += candidateSegments.Count(segment =>
                            SegmentIntersectsRect(segment, blocked));
                    }
                }
                if (costs[tagIndex - 1, rowIndex - 1] >= infinity * 0.5)
                {
                    continue;
                }

                double distance = Math.Abs(rows[rowIndex - 1] - tag.Anchor.V);
                // A manual Guided Zone is authoritative. A blocked row carries
                // a very large cost so clear rows are still preferred, but it
                // remains available when that is the only way to keep every
                // selected tag inside the user's rectangle.
                double candidate = costs[tagIndex - 1, rowIndex - 1] +
                                   distance +
                                   blockedText * 1_000_000.0 +
                                   blockedLeaders * 10_000.0;
                if (candidate < costs[tagIndex, rowIndex] - Epsilon)
                {
                    costs[tagIndex, rowIndex] = candidate;
                    take[tagIndex, rowIndex] = true;
                }
            }
        }

        if (costs[tagCount, rowCount] >= infinity * 0.5)
            throw new InvalidOperationException("Guided Zone internal row assignment failed.");

        var result = new int[tagCount];
        int currentTag = tagCount;
        int currentRow = rowCount;
        while (currentTag > 0 && currentRow > 0)
        {
            if (take[currentTag, currentRow])
            {
                result[currentTag - 1] = currentRow - 1;
                currentTag--;
            }
            currentRow--;
        }
        return result;
    }

    private static LayoutPoint CreateHostEnd(
        LayoutTagInput tag,
        double row,
        bool usesElbow,
        double clearance)
    {
        if (!usesElbow) return tag.Anchor;
        double direction = Math.Sign(row - tag.Anchor.V);
        double available = direction > 0
            ? tag.ElementBounds.MaxV - tag.Anchor.V
            : tag.Anchor.V - tag.ElementBounds.MinV;
        double shift = Math.Min(
            Math.Max(0.0, available) * 0.5,
            Math.Max(clearance * 0.75, 0.005));
        return new LayoutPoint(tag.Anchor.U, tag.Anchor.V + direction * shift);
    }

    private static List<TagLayoutPlacement> ResolveLeaderConflicts(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        var accepted = new List<TagLayoutPlacement>(source.Count);
        foreach (TagLayoutPlacement original in source
                     .OrderByDescending(item => item.Head.V)
                     .ThenBy(item => item.Head.U)
                     .ThenBy(item => item.TagKey))
        {
            if (!inputs.TryGetValue(original.TagKey, out LayoutTagInput? tag))
            {
                accepted.Add(original);
                continue;
            }

            TagLayoutPlacement best = original;
            int bestScore = PlacementCollisionScore(
                best, tag.ElementKey, accepted, obstacles, settings);
            if (bestScore > 0)
            {
                foreach (LayoutPoint end in FreeEndCandidates(tag, original.Head.V, settings.Clearance))
                {
                    bool usesElbow = Math.Abs(original.Head.V - end.V) > Epsilon;
                    LayoutPoint elbow = usesElbow
                        ? new LayoutPoint(end.U, original.Head.V)
                        : end;
                    var candidate = original with
                    {
                        End = end,
                        Elbow = elbow,
                        UsesElbow = usesElbow,
                        UsesFreeEnd = true,
                        HasClash = false
                    };
                    int score = PlacementCollisionScore(
                        candidate, tag.ElementKey, accepted, obstacles, settings);
                    if (score < bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                    if (score == 0) break;
                }
            }
            accepted.Add(best with { HasClash = bestScore > 0 });
        }
        return accepted;
    }

    private static List<TagLayoutPlacement> RepairInsideGuidedZone(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect usable,
        SmartTagLayoutSettings settings)
    {
        var repaired = source.ToList();
        for (int pass = 0; pass < 4; pass++)
        {
            bool improved = false;
            int[] order = Enumerable.Range(0, repaired.Count).ToArray();

            foreach (int index in order)
            {
                TagLayoutPlacement current = repaired[index];
                if (!inputs.TryGetValue(current.TagKey, out LayoutTagInput? input)) continue;
                List<TagLayoutPlacement> others = repaired
                    .Where((_, other) => other != index)
                    .ToList();
                var corridor = new LayoutRect(
                    Math.Min(usable.MinU, input.ElementBounds.MinU),
                    Math.Min(usable.MinV, input.ElementBounds.MinV),
                    Math.Max(usable.MaxU, input.ElementBounds.MaxU),
                    Math.Max(usable.MaxV, input.ElementBounds.MaxV))
                    .Expand(settings.Clearance * 2.0);
                List<LayoutObstacle> relevantObstacles = obstacles
                    .Where(obstacle => obstacle.Bounds.Intersects(corridor))
                    .ToList();
                int bestScore = PlacementCollisionScore(
                    current, input.ElementKey, others, relevantObstacles, settings);
                if (bestScore == 0) continue;

                TagLayoutPlacement best = current;
                double bestTravel = RepairTravel(current, input);
                foreach (TagLayoutPlacement candidate in GuidedRepairCandidates(
                             current, input, settings))
                {
                    int score = PlacementCollisionScore(
                        candidate, input.ElementKey, others, relevantObstacles, settings);
                    double travel = RepairTravel(candidate, input);
                    if (score < bestScore ||
                        score == bestScore && travel < bestTravel - Epsilon)
                    {
                        best = candidate;
                        bestScore = score;
                        bestTravel = travel;
                    }
                    if (bestScore == 0 && bestTravel <= settings.Clearance) break;
                }

                if (best != current)
                {
                    repaired[index] = best;
                    improved = true;
                }
            }
            if (!improved) break;
        }
        return repaired;
    }

    private static List<TagLayoutPlacement> RepairGuidedColumnRails(
        IReadOnlyList<TagLayoutPlacement> source,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect usable,
        SmartTagLayoutSettings settings)
    {
        var repaired = source.ToList();
        foreach (bool leftSide in new[] { true, false })
        {
            int[] indices = Enumerable.Range(0, repaired.Count)
                .Where(index => (repaired[index].Head.U <= repaired[index].End.U) == leftSide)
                .ToArray();
            if (indices.Length == 0) continue;

            double currentLeft = indices
                .Select(index => repaired[index].TagBounds.MinU)
                .OrderBy(value => value)
                .ElementAt(indices.Length / 2);
            double maximumWidth = indices.Max(index => repaired[index].TagBounds.Width);
            double minLeft = usable.MinU;
            double maxLeft = usable.MaxU - maximumWidth;
            if (maxLeft < minLeft - Epsilon) continue;
            if (maxLeft < minLeft) maxLeft = minLeft;

            var railCandidates = new List<double> { currentLeft, minLeft, maxLeft };
            const int samples = 18;
            for (int sample = 1; sample < samples; sample++)
                railCandidates.Add(minLeft + (maxLeft - minLeft) * sample / samples);
            foreach (LayoutObstacle obstacle in obstacles)
            {
                LayoutRect blocked = obstacle.Bounds.Expand(settings.Clearance);
                railCandidates.Add(PortableMath.Clamp(blocked.MinU - maximumWidth, minLeft, maxLeft));
                railCandidates.Add(PortableMath.Clamp(blocked.MaxU, minLeft, maxLeft));
            }

            int currentScore = ColumnCollisionScore(
                repaired, indices, inputs, obstacles, settings);
            List<TagLayoutPlacement>? bestColumn = null;
            int bestScore = currentScore;
            foreach (double left in railCandidates
                         .DistinctBy(value => Math.Round(value, 5))
                         .OrderBy(value => Math.Abs(value - currentLeft))
                         .Take(32))
            {
                double delta = left - currentLeft;
                List<TagLayoutPlacement> shifted = indices
                    .Select(index => ShiftColumnPlacement(repaired[index], delta))
                    .ToList();
                bool remainsOnRequestedSide = shifted.All(item => leftSide
                    ? item.Head.U < item.End.U - Epsilon
                    : item.Head.U > item.End.U + Epsilon);
                bool remainsInsideZone = shifted.All(item =>
                    item.TagBounds.MinU >= usable.MinU - Epsilon &&
                    item.TagBounds.MaxU <= usable.MaxU + Epsilon);
                if (!remainsOnRequestedSide || !remainsInsideZone) continue;

                var trial = repaired.ToList();
                for (int item = 0; item < indices.Length; item++)
                    trial[indices[item]] = shifted[item];
                int score = ColumnCollisionScore(
                    trial, indices, inputs, obstacles, settings);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestColumn = shifted;
                    if (bestScore == 0) break;
                }
            }

            if (bestColumn is null) continue;
            for (int item = 0; item < indices.Length; item++)
                repaired[indices[item]] = bestColumn[item];
        }
        return repaired;
    }

    private static int ColumnCollisionScore(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyList<int> columnIndices,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        long score = 0;
        foreach (int index in columnIndices)
        {
            TagLayoutPlacement placement = placements[index];
            if (!inputs.TryGetValue(placement.TagKey, out LayoutTagInput? input)) continue;
            List<TagLayoutPlacement> others = placements
                .Where((_, other) => other != index)
                .ToList();
            score += PlacementCollisionScore(
                placement, input.ElementKey, others, obstacles, settings);
            if (score >= int.MaxValue) return int.MaxValue;
        }
        return (int)score;
    }

    private static TagLayoutPlacement ShiftColumnPlacement(
        TagLayoutPlacement placement,
        double delta)
    {
        var head = new LayoutPoint(placement.Head.U + delta, placement.Head.V);
        var bounds = new LayoutRect(
            placement.TagBounds.MinU + delta,
            placement.TagBounds.MinV,
            placement.TagBounds.MaxU + delta,
            placement.TagBounds.MaxV);
        LayoutPoint elbow = placement.UsesElbow
            ? new LayoutPoint(placement.End.U, head.V)
            : placement.End;
        return placement with
        {
            Head = head,
            Elbow = elbow,
            TagBounds = bounds,
            HasClash = false
        };
    }

    private static IEnumerable<TagLayoutPlacement> GuidedRepairCandidates(
        TagLayoutPlacement original,
        LayoutTagInput input,
        SmartTagLayoutSettings settings)
    {
        List<LayoutPoint> ends = new[] { original.End }
            .Concat(FreeEndCandidates(input, original.Head.V, settings.Clearance).Take(6))
            .Distinct()
            .ToList();
        foreach (LayoutPoint end in ends)
        {
            bool elbow = Math.Abs(original.Head.V - end.V) > Epsilon;
            LayoutPoint elbowPoint = elbow ? new LayoutPoint(end.U, original.Head.V) : end;
            yield return original with
            {
                End = end,
                Elbow = elbowPoint,
                UsesElbow = elbow,
                UsesFreeEnd = true,
                HasClash = false
            };
        }
    }

    private static double RepairTravel(TagLayoutPlacement placement, LayoutTagInput input) =>
        Math.Abs(placement.Head.U - input.Anchor.U) +
        Math.Abs(placement.Head.V - input.Anchor.V) * 0.25;

    private static IEnumerable<LayoutPoint> FreeEndCandidates(
        LayoutTagInput tag,
        double row,
        double clearance)
    {
        double step = Math.Max(clearance * 0.6, 0.01);
        double minU = tag.ElementBounds.MinU + Epsilon;
        double maxU = tag.ElementBounds.MaxU - Epsilon;
        double minV = tag.ElementBounds.MinV + Epsilon;
        double maxV = tag.ElementBounds.MaxV - Epsilon;
        if (maxU < minU)
        {
            minU = maxU = (tag.ElementBounds.MinU + tag.ElementBounds.MaxU) * 0.5;
        }
        if (maxV < minV)
        {
            minV = maxV = (tag.ElementBounds.MinV + tag.ElementBounds.MaxV) * 0.5;
        }
        double rowOnHost = PortableMath.Clamp(row, minV, maxV);

        // First try moving only the host end vertically. This often preserves
        // one exact horizontal leader while separating two coincident ends.
        yield return new LayoutPoint(
            PortableMath.Clamp(tag.Anchor.U, minU, maxU),
            rowOnHost);
        for (int lane = 1; lane <= 6; lane++)
        {
            double delta = lane * step;
            yield return new LayoutPoint(
                PortableMath.Clamp(tag.Anchor.U - delta, minU, maxU),
                rowOnHost);
            yield return new LayoutPoint(
                PortableMath.Clamp(tag.Anchor.U + delta, minU, maxU),
                rowOnHost);
            yield return new LayoutPoint(
                PortableMath.Clamp(tag.Anchor.U - delta, minU, maxU),
                PortableMath.Clamp(tag.Anchor.V - delta, minV, maxV));
            yield return new LayoutPoint(
                PortableMath.Clamp(tag.Anchor.U + delta, minU, maxU),
                PortableMath.Clamp(tag.Anchor.V + delta, minV, maxV));
        }
    }

    private static int PlacementCollisionScore(
        TagLayoutPlacement candidate,
        long hostElementKey,
        IReadOnlyList<TagLayoutPlacement> accepted,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        int score = 0;
        foreach (TagLayoutPlacement other in accepted)
        {
            if (settings.AvoidTagText &&
                candidate.TagBounds.Intersects(other.TagBounds.Expand(settings.Clearance)))
            {
                score += 100;
            }
            if (!settings.AvoidLeaders) continue;
            foreach (LayoutSegment first in Segments(candidate))
            {
                if (SegmentIntersectsRect(
                        first,
                        other.TagBounds.Expand(settings.Clearance)))
                {
                    score += 20;
                }
                foreach (LayoutSegment second in Segments(other))
                {
                    if (SegmentsIntersect(first, second)) score += 10;
                }
            }
            foreach (LayoutSegment second in Segments(other))
            {
                if (SegmentIntersectsRect(
                        second,
                        candidate.TagBounds.Expand(settings.Clearance)))
                {
                    score += 20;
                }
            }
        }
        if (settings.AvoidElements)
        {
            foreach (LayoutObstacle obstacle in obstacles)
            {
                if (obstacle.ElementKey == hostElementKey) continue;
                LayoutRect blocked = obstacle.Bounds.Expand(settings.Clearance);
                if (candidate.TagBounds.Intersects(blocked))
                {
                    score += 100;
                }
                foreach (LayoutSegment segment in Segments(candidate))
                {
                    if (SegmentIntersectsRect(segment, blocked)) score += 3;
                }
            }
        }
        return score;
    }

    private static bool SegmentIntersectsRect(LayoutSegment segment, LayoutRect rect)
    {
        if (PointInside(segment.Start, rect) || PointInside(segment.End, rect)) return true;
        var topLeft = new LayoutPoint(rect.MinU, rect.MaxV);
        var topRight = new LayoutPoint(rect.MaxU, rect.MaxV);
        var bottomLeft = new LayoutPoint(rect.MinU, rect.MinV);
        var bottomRight = new LayoutPoint(rect.MaxU, rect.MinV);
        return SegmentsIntersect(segment, new LayoutSegment(topLeft, topRight)) ||
               SegmentsIntersect(segment, new LayoutSegment(topRight, bottomRight)) ||
               SegmentsIntersect(segment, new LayoutSegment(bottomRight, bottomLeft)) ||
               SegmentsIntersect(segment, new LayoutSegment(bottomLeft, topLeft));
    }

    private static bool PointInside(LayoutPoint point, LayoutRect rect) =>
        point.U >= rect.MinU - Epsilon && point.U <= rect.MaxU + Epsilon &&
        point.V >= rect.MinV - Epsilon && point.V <= rect.MaxV + Epsilon;

    private static bool LeaderClashes(
        TagLayoutPlacement target,
        IReadOnlyList<TagLayoutPlacement> all,
        double clearance)
    {
        foreach (TagLayoutPlacement other in all)
        {
            if (other.TagKey == target.TagKey) continue;
            if (target.TagBounds.Intersects(other.TagBounds.Expand(clearance))) return true;
            foreach (LayoutSegment first in Segments(target))
            foreach (LayoutSegment second in Segments(other))
            {
                if (SegmentsIntersect(first, second)) return true;
            }
        }
        return false;
    }

    private static int CountLeaderClashes(
        IReadOnlyList<TagLayoutPlacement> placements,
        double clearance) => placements.Count(item => LeaderClashes(item, placements, clearance));

    private static IEnumerable<LayoutSegment> Segments(TagLayoutPlacement placement)
    {
        yield return new LayoutSegment(placement.Head, placement.Elbow);
        if (placement.UsesElbow) yield return new LayoutSegment(placement.End, placement.Elbow);
    }

    private static bool SegmentsIntersect(LayoutSegment first, LayoutSegment second)
    {
        static double Cross(LayoutPoint a, LayoutPoint b, LayoutPoint c) =>
            (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        static bool Within(LayoutPoint a, LayoutPoint b, LayoutPoint p) =>
            p.U >= Math.Min(a.U, b.U) - Epsilon && p.U <= Math.Max(a.U, b.U) + Epsilon &&
            p.V >= Math.Min(a.V, b.V) - Epsilon && p.V <= Math.Max(a.V, b.V) + Epsilon;
        double c1 = Cross(first.Start, first.End, second.Start);
        double c2 = Cross(first.Start, first.End, second.End);
        double c3 = Cross(second.Start, second.End, first.Start);
        double c4 = Cross(second.Start, second.End, first.End);
        if (((c1 > Epsilon && c2 < -Epsilon) || (c1 < -Epsilon && c2 > Epsilon)) &&
            ((c3 > Epsilon && c4 < -Epsilon) || (c3 < -Epsilon && c4 > Epsilon))) return true;
        return Math.Abs(c1) <= Epsilon && Within(first.Start, first.End, second.Start) ||
               Math.Abs(c2) <= Epsilon && Within(first.Start, first.End, second.End) ||
               Math.Abs(c3) <= Epsilon && Within(second.Start, second.End, first.Start) ||
               Math.Abs(c4) <= Epsilon && Within(second.Start, second.End, first.End);
    }

    private static LayoutRect Intersect(LayoutRect first, LayoutRect second) => new(
        Math.Max(first.MinU, second.MinU),
        Math.Max(first.MinV, second.MinV),
        Math.Min(first.MaxU, second.MaxU),
        Math.Min(first.MaxV, second.MaxV));
}
