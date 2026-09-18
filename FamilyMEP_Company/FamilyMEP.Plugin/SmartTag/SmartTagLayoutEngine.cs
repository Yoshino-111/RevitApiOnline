namespace FamilyMEP.Plugin.SmartTag;

internal readonly record struct LayoutPoint(double U, double V);

internal readonly record struct LayoutRect(double MinU, double MinV, double MaxU, double MaxV)
{
    public double Width => Math.Max(0.0, MaxU - MinU);
    public double Height => Math.Max(0.0, MaxV - MinV);
    public LayoutRect Expand(double value) =>
        new(MinU - value, MinV - value, MaxU + value, MaxV + value);

    public bool Intersects(LayoutRect other, double tolerance = 1e-9) =>
        MinU < other.MaxU - tolerance && MaxU > other.MinU + tolerance &&
        MinV < other.MaxV - tolerance && MaxV > other.MinV + tolerance;
}

internal readonly record struct LayoutSegment(LayoutPoint Start, LayoutPoint End)
{
    public bool IsHorizontal => Math.Abs(Start.V - End.V) <= 1e-8;
    public bool IsVertical => Math.Abs(Start.U - End.U) <= 1e-8;
}

internal enum LayoutObstacleKind
{
    Mep,
    Architecture
}

internal sealed record LayoutObstacle(
    long ElementKey,
    LayoutRect Bounds,
    LayoutObstacleKind Kind = LayoutObstacleKind.Mep);

internal sealed record LayoutTagInput(
    long TagKey,
    long ElementKey,
    string Label,
    string Group,
    LayoutPoint Anchor,
    LayoutPoint CurrentHead,
    double TagWidth,
    double TagHeight,
    LayoutRect ElementBounds)
{
    // Used only by the experimental planner after measuring leader-free text.
    public double TextOffsetU { get; init; }
    public double TextOffsetV { get; init; }

    // Duct-change tagging can cover a very large plan while producing only a
    // handful of relevant hosts in each local area. Keep those hosts in 2-D
    // neighbourhoods so a remote change cannot pull the whole annotation rail
    // across the view. Existing tag modes deliberately retain their accepted
    // horizontal clustering policy.
    public bool PreferLocalClustering { get; init; }
    public bool CanAnchorDuctFollowers { get; init; }

    // Set after comparing real host bounds. A sparse Duct label must stay with
    // its own nearest Air Terminal / Duct Accessory tag; otherwise several
    // nearby Ducts can inherit the rail of an unrelated companion.
    public long PreferredFollowerAnchorTagKey { get; init; }
}

internal enum SmartTagLayoutStyle
{
    Standard,
    CompactGroups,
    GuidedZones,
    StandardNearHost
}

internal sealed record SmartTagLayoutSettings(
    bool PlaceLeft,
    double ColumnWidth,
    double OffsetFromElements,
    double RowSpacing,
    double TopMargin,
    double Clearance,
    bool AvoidElements,
    bool AvoidLeaders,
    bool AvoidTagText,
    bool LockUpperLeaders,
    bool MoveLowerTagFirst,
    bool AutoSide)
{
    // Keep the accepted layout as the default. Additional layout policies are
    // opt-in so every existing caller and saved workflow remains Standard.
    public SmartTagLayoutStyle LayoutStyle { get; init; } = SmartTagLayoutStyle.Standard;
    public LayoutRect? GuidedZone { get; init; }
}

internal sealed record TagLayoutPlacement(
    long TagKey,
    LayoutPoint Head,
    LayoutPoint End,
    LayoutPoint Elbow,
    LayoutRect TagBounds,
    bool UsesElbow,
    bool UsesFreeEnd,
    bool HasClash,
    string Label,
    string Group)
{
    public bool CanAnchorDuctFollowers { get; init; }
    // Persist the controller's Duct -> DA/AT ownership through preview and the
    // temporary real-family measurement pass. SmartTagRecord comes from the
    // immutable view snapshot and does not contain the density selection's
    // late-bound companion key.
    public long PreferredFollowerAnchorTagKey { get; init; }
}

internal sealed record SmartTagLayoutResult(
    IReadOnlyList<TagLayoutPlacement> Placements,
    int ElbowCount,
    int ClashCount,
    LayoutRect ColumnBounds)
{
    public string? Diagnostic { get; init; }
}

internal static class SmartTagLayoutEngine
{
    private const int MaximumSlotAttempts = 180;

    public static SmartTagLayoutResult ComputeClustered(
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
                TagLayoutPlacement? fixedSideEstablishedTag = FindNearbyFollowPlacement(
                    cluster,
                    settings.PlaceLeft,
                    reservations,
                    settings);
                if (cluster.All(item => item.PreferLocalClustering) &&
                    fixedSideEstablishedTag is null)
                {
                    continue;
                }
                double? followerTop = fixedSideEstablishedTag is null
                    ? null
                    : FindFollowerTopEdge(
                        cluster,
                        fixedSideEstablishedTag,
                        reservations,
                        settings);
                if (followerTop is double requiredTop)
                {
                    double requiredHeight = cluster.Sum(item => Math.Max(item.TagHeight, 0.01)) +
                                            settings.RowSpacing * Math.Max(0, cluster.Count - 1);
                    if (requiredTop - (frame.MinV + settings.TopMargin) < requiredHeight)
                        continue;
                }
                double? localFollowRail = forcedColumnLeft ?? fixedSideEstablishedTag?.TagBounds.MinU;
                AddResult(ComputeColumn(
                    cluster,
                    obstacles,
                    frame,
                    settings,
                    reservations,
                    reservedSegments,
                    localFollowRail,
                    followerTop));
                continue;
            }

            TagLayoutPlacement? nearbyEstablishedTag = FindNearbyFollowPlacement(
                cluster,
                placeLeft: null,
                reservations,
                settings);
            if (cluster.All(item => item.PreferLocalClustering) &&
                nearbyEstablishedTag is null)
            {
                continue;
            }
            if (nearbyEstablishedTag is not null)
            {
                bool followLeft = nearbyEstablishedTag.Head.U < nearbyEstablishedTag.End.U;
                double followerTop = FindFollowerTopEdge(
                    cluster,
                    nearbyEstablishedTag,
                    reservations,
                    settings);
                double requiredFollowerHeight = cluster.Sum(item =>
                    Math.Max(item.TagHeight, 0.01)) +
                    settings.RowSpacing * Math.Max(0, cluster.Count - 1);
                if (followerTop - (frame.MinV + settings.TopMargin) < requiredFollowerHeight)
                {
                    // Duct is the optional category. Never move the established
                    // Air Terminal / Accessory stack just to make it fit.
                    continue;
                }
                AddResult(ComputeColumn(
                    cluster,
                    obstacles,
                    frame,
                    settings with { PlaceLeft = followLeft },
                    reservations,
                    reservedSegments,
                    nearbyEstablishedTag.TagBounds.MinU,
                    followerTop));
                continue;
            }

            double clusterMinU = cluster.Min(item => item.ElementBounds.MinU);
            double clusterMaxU = cluster.Max(item => item.ElementBounds.MaxU);
            double viewCenterU = (frame.MinU + frame.MaxU) * 0.5;
            double clusterCenterU = (clusterMinU + clusterMaxU) * 0.5;
            double outwardBias = Math.Abs(clusterCenterU - viewCenterU) * 0.25;
            double leftLoad = 0.0;
            double rightLoad = 0.0;
            var left = new List<LayoutTagInput>();
            var right = new List<LayoutTagInput>();
            foreach (LayoutTagInput tag in cluster
                         .OrderByDescending(item => item.Anchor.V)
                         .ThenBy(item => item.Anchor.U))
            {
                double leftInnerEdge = clusterMinU - settings.OffsetFromElements;
                double rightInnerEdge = clusterMaxU + settings.OffsetFromElements;
                bool leftFits = leftInnerEdge - tag.TagWidth >= frame.MinU + settings.TopMargin;
                bool rightFits = rightInnerEdge + tag.TagWidth <= frame.MaxU - settings.TopMargin;
                double leftDistance = Math.Abs(tag.Anchor.U - leftInnerEdge);
                double rightDistance = Math.Abs(rightInnerEdge - tag.Anchor.U);
                double leftCost = leftDistance + leftLoad * 0.15 + EstimateSideObstacleCost(
                    tag,
                    leftInnerEdge,
                    placeLeft: true,
                    obstacles,
                    settings);
                double rightCost = rightDistance + rightLoad * 0.15 + EstimateSideObstacleCost(
                    tag,
                    rightInnerEdge,
                    placeLeft: false,
                    obstacles,
                    settings);
                if (clusterCenterU < viewCenterU) rightCost += outwardBias;
                if (clusterCenterU > viewCenterU) leftCost += outwardBias;
                if (!leftFits) leftCost += 1_000_000.0;
                if (!rightFits) rightCost += 1_000_000.0;
                bool chooseLeft = leftCost < rightCost - 1e-8 ||
                                  Math.Abs(leftCost - rightCost) <= 1e-8 && leftLoad <= rightLoad;
                if (chooseLeft)
                {
                    left.Add(tag);
                    leftLoad += tag.TagHeight + settings.RowSpacing;
                }
                else
                {
                    right.Add(tag);
                    rightLoad += tag.TagHeight + settings.RowSpacing;
                }
            }

            if (left.Count > 0)
            {
                double? leftFollowRail = forcedColumnLeft ?? FindNearbyFollowRail(
                    left,
                    placeLeft: true,
                    reservations,
                    settings);
                AddResult(ComputeColumn(
                    left,
                    obstacles,
                    frame,
                    settings with { PlaceLeft = true },
                    reservations,
                    reservedSegments,
                    leftFollowRail));
            }
            if (right.Count > 0)
            {
                double? rightFollowRail = forcedColumnLeft ?? FindNearbyFollowRail(
                    right,
                    placeLeft: false,
                    reservations,
                    settings);
                AddResult(ComputeColumn(
                    right,
                    obstacles,
                    frame,
                    settings with { PlaceLeft = false },
                    reservations,
                    reservedSegments,
                    rightFollowRail));
            }
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
        if (placements.Count == 0)
        {
            return new SmartTagLayoutResult([], 0, 0, frame);
        }
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
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement>? initialReservations = null)
    {
        IReadOnlyList<TagLayoutPlacement> reservations = initialReservations ?? [];
        return ComputeColumn(
            source,
            obstacles,
            frame,
            settings,
            reservations,
            BuildSegments(reservations),
            null);
    }

    private static SmartTagLayoutResult ComputeColumn(
        IReadOnlyList<LayoutTagInput> source,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement> initialReservations,
        IReadOnlyList<LayoutSegment> initialSegments,
        double? forcedColumnLeft,
        double? forcedTopEdge = null)
    {
        if (source.Count == 0)
        {
            return new SmartTagLayoutResult([], 0, 0, frame);
        }

        List<LayoutTagInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ThenBy(item => item.TagKey)
            .ToList();

        double top = frame.MaxV - settings.TopMargin;
        double bottom = frame.MinV + settings.TopMargin;
        if (forcedTopEdge is double followerTop)
        {
            top = Math.Min(top, followerTop);
        }
        double elementEdge = settings.PlaceLeft
            ? ordered.Min(item => item.ElementBounds.MinU)
            : ordered.Max(item => item.ElementBounds.MaxU);
        double maximumTagWidth = ordered.Max(item => Math.Max(item.TagWidth, 0.01));
        double computedColumnLeft = settings.PlaceLeft
            ? elementEdge - settings.OffsetFromElements - maximumTagWidth
            : elementEdge + settings.OffsetFromElements;
        double columnLeft = forcedColumnLeft ?? SnapToReservedRail(
            computedColumnLeft,
            settings.PlaceLeft,
            initialReservations,
            settings);
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
            double height = Math.Max(tag.TagHeight, 0.01);
            double halfHeight = height * 0.5;
            // The preview rail represents the visible left edge. Tag heads may
            // differ with family width; Apply performs a second pass against the
            // actual leader-free Revit bounding box to preserve this same edge.
            double baseColumnHead = columnLeft + width * 0.5;
            double preferred = balancedRows[tag.TagKey];
            // Search in small local increments. Full-row jumps leave only one or
            // two legal choices inside the bounded travel window and can force
            // several unresolved tags onto the exact same point.
            double step = Math.Max(
                settings.RowSpacing + 0.0025,
                height * 0.25);
            double maximumVerticalTravel = Math.Max(
                height * 3.0 + settings.RowSpacing * 2.0,
                settings.Clearance * 4.0);
            Candidate? best = null;
            double bestQuality = double.MaxValue;

            for (int attempt = 0; attempt < MaximumSlotAttempts; attempt++)
            {
                int distance = (attempt + 1) / 2;
                double direction = attempt == 0 || attempt % 2 == 1 ? -1.0 : 1.0;
                double candidateV = attempt == 0
                    ? preferred
                    : preferred + direction * distance * step;
                if (candidateV - halfHeight < bottom || candidateV + halfHeight > top)
                {
                    continue;
                }
                if (Math.Abs(candidateV - tag.Anchor.V) > maximumVerticalTravel + 1e-8)
                {
                    continue;
                }

                bool usesElbow = Math.Abs(candidateV - tag.Anchor.V) > 1e-7;
                var head = new LayoutPoint(baseColumnHead, candidateV);
                var tagBounds = new LayoutRect(
                    columnLeft,
                    candidateV - halfHeight,
                    columnLeft + width,
                    candidateV + halfHeight);
                LayoutPoint end = CreateHostEnd(tag, candidateV, usesElbow, settings);
                var elbow = new LayoutPoint(tag.Anchor.U, candidateV);
                var horizontal = new LayoutSegment(head, elbow);
                var tail = new LayoutSegment(end, elbow);
                bool outsideFrame = tagBounds.MinU < frame.MinU || tagBounds.MaxU > frame.MaxU ||
                                    tagBounds.MinV < frame.MinV || tagBounds.MaxV > frame.MaxV;
                int collisionScore = (outsideFrame ? 10000 : 0) + CollisionScore(
                    tag,
                    tagBounds,
                    horizontal,
                    usesElbow ? tail : null,
                    collisionPlacements,
                    reservedSegments,
                    obstacles,
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

                double travelRows = Math.Abs(candidateV - preferred) / Math.Max(step, 1e-8);
                int exactSlotReuse = placements.Count(item =>
                    Math.Abs(item.Head.V - candidateV) <= 1e-8);
                // Never stack several unresolved tags at precisely the same
                // head point. In an over-constrained local group it is clearer
                // to keep distinct nearby rows and report their remaining clash.
                double quality = collisionScore + travelRows + exactSlotReuse * 1_000_000_000.0;
                if (best is null || quality < bestQuality)
                {
                    best = option;
                    bestQuality = quality;
                }
            }

            Candidate candidate = best ?? CreateFallback(
                tag,
                columnLeft,
                ClampOrUseRangeMidpoint(
                    preferred,
                    Math.Max(bottom + halfHeight, tag.Anchor.V - maximumVerticalTravel),
                    Math.Min(top - halfHeight, tag.Anchor.V + maximumVerticalTravel)),
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
        int score = settings.AvoidTagText
            ? placements.Count(item => tagBounds.Intersects(item.TagBounds)) * 1000
            : 0;

        if (settings.AvoidElements)
        {
            foreach (LayoutObstacle obstacle in obstacles)
            {
                LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                bool ownMepHost = obstacle.Kind == LayoutObstacleKind.Mep &&
                                  obstacle.ElementKey == tag.ElementKey;
                bool textClash = !ownMepHost &&
                                 tagBounds.Intersects(expanded, 0.0);
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
                        ? obstacle.Kind == LayoutObstacleKind.Architecture ? 1500 : 500
                        : 0;
                    score += leaderClash
                        ? obstacle.Kind == LayoutObstacleKind.Architecture ? 8 : 20
                        : 0;
                }
            }
        }

        if (settings.AvoidLeaders)
        {
            foreach (LayoutSegment reserved in reservedSegments)
            {
                if (SegmentsConflict(horizontal, reserved, settings.Clearance) ||
                    (tail is LayoutSegment tailSegment &&
                     SegmentsConflict(tailSegment, reserved, settings.Clearance)))
                {
                    // A leader crossing/overlap is a routing failure, not a soft
                    // visual preference. Moving several rows or using a nearby
                    // attached elbow offset is always preferable to accepting the cut.
                    score += 300;
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
        var head = new LayoutPoint(columnLeft + width * 0.5, candidateV);
        bool usesElbow = Math.Abs(candidateV - tag.Anchor.V) > 1e-7;
        LayoutPoint end = CreateHostEnd(tag, candidateV, usesElbow, settings);
        var elbow = new LayoutPoint(tag.Anchor.U, candidateV);
        var bounds = new LayoutRect(
            columnLeft,
            candidateV - halfHeight,
            columnLeft + width,
            candidateV + halfHeight);
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
            result.Add(new LayoutSegment(placement.Head, placement.Elbow));
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
        SmartTagLayoutSettings settings)
    {
        double tolerance = Math.Max(settings.Clearance, 0.02);
        double maximumSnapDistance = Math.Max(
            Math.Max(settings.OffsetFromElements * 2.0, settings.Clearance * 4.0),
            0.05);
        var nearbyRails = reservations
            .Where(item => placeLeft ? item.Head.U < item.End.U : item.Head.U > item.End.U)
            .Select(item => item.TagBounds.MinU)
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
        var clusters = new List<List<LayoutTagInput>>();
        List<LayoutTagInput> existingPolicy = source
            .Where(item => !item.PreferLocalClustering)
            .ToList();
        List<LayoutTagInput> localPolicy = source
            .Where(item => item.PreferLocalClustering)
            .ToList();

        clusters.AddRange(BuildHorizontalClusters(existingPolicy, maximumGap));
        clusters.AddRange(localPolicy
            .GroupBy(item => item.PreferredFollowerAnchorTagKey)
            .SelectMany(group => BuildBoundedLocalClusters(group.ToList(), maximumGap))
            .OrderByDescending(cluster => cluster.Max(item => item.Anchor.V))
            .ThenBy(cluster => cluster.Min(item => item.Anchor.U)));
        return clusters;
    }

    private static List<List<LayoutTagInput>> BuildHorizontalClusters(
        IReadOnlyList<LayoutTagInput> source,
        double maximumGap)
    {
        double maximumSpan = Math.Max(maximumGap, 0.05);
        var clusters = new List<List<LayoutTagInput>>();
        List<LayoutTagInput> ordered = source
            .OrderBy(item => (item.ElementBounds.MinU + item.ElementBounds.MaxU) * 0.5)
            .ToList();
        List<LayoutTagInput>? current = null;
        double clusterStartU = 0.0;
        foreach (LayoutTagInput item in ordered)
        {
            double centerU = (item.ElementBounds.MinU + item.ElementBounds.MaxU) * 0.5;
            if (current is null || centerU - clusterStartU > maximumSpan)
            {
                current = [];
                clusters.Add(current);
                clusterStartU = centerU;
            }
            current.Add(item);
        }
        return clusters;
    }

    private static List<List<LayoutTagInput>> BuildBoundedLocalClusters(
        IReadOnlyList<LayoutTagInput> source,
        double maximumGap)
    {
        if (source.Count == 0) return [];

        double width = MedianPositive(source.Select(item => item.TagWidth), 0.01);
        double height = MedianPositive(source.Select(item => item.TagHeight), 0.01);
        double horizontalGapLimit = Math.Max(width * 2.5, maximumGap * 0.20);
        double verticalGapLimit = Math.Max(height * 4.0, maximumGap * 0.22);
        double horizontalSpanLimit = Math.Max(width * 7.0, maximumGap * 0.60);
        double verticalSpanLimit = Math.Max(height * 12.0, maximumGap * 0.75);
        List<LayoutTagInput> ordered = source
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
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
            if (mergedMaximumU - mergedMinimumU > horizontalSpanLimit ||
                mergedMaximumV - mergedMinimumV > verticalSpanLimit)
            {
                return;
            }

            parents[secondRoot] = firstRoot;
            minimumU[firstRoot] = mergedMinimumU;
            maximumU[firstRoot] = mergedMaximumU;
            minimumV[firstRoot] = mergedMinimumV;
            maximumV[firstRoot] = mergedMaximumV;
        }

        static double AxisGap(double firstMin, double firstMax, double secondMin, double secondMax)
        {
            if (firstMax < secondMin) return secondMin - firstMax;
            if (secondMax < firstMin) return firstMin - secondMax;
            return 0.0;
        }

        for (int first = 0; first < ordered.Count; first++)
        {
            LayoutRect firstHost = ordered[first].ElementBounds;
            for (int second = first + 1; second < ordered.Count; second++)
            {
                LayoutRect secondHost = ordered[second].ElementBounds;
                if (AxisGap(firstHost.MinU, firstHost.MaxU, secondHost.MinU, secondHost.MaxU) <=
                        horizontalGapLimit &&
                    AxisGap(firstHost.MinV, firstHost.MaxV, secondHost.MinV, secondHost.MaxV) <=
                        verticalGapLimit)
                {
                    Union(first, second);
                }
            }
        }

        var byRoot = new Dictionary<int, List<LayoutTagInput>>();
        for (int index = 0; index < ordered.Count; index++)
        {
            int root = Find(index);
            if (!byRoot.TryGetValue(root, out List<LayoutTagInput>? cluster))
            {
                cluster = [];
                byRoot[root] = cluster;
            }
            cluster.Add(ordered[index]);
        }
        return byRoot.Values.ToList();
    }

    private static double MedianPositive(IEnumerable<double> values, double fallback)
    {
        double[] ordered = values.Where(value => value > 1e-8).OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return fallback;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }

    private static double? FindNearbyFollowRail(
        IReadOnlyList<LayoutTagInput> cluster,
        bool placeLeft,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
        => FindNearbyFollowPlacement(cluster, placeLeft, reservations, settings)
            ?.TagBounds.MinU;

    internal static double NearbyFollowRadius(SmartTagLayoutSettings settings) =>
        Math.Max(
            settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost
                // Standard V2 may use the full user-defined local search width
                // to find the nearest DA/AT host and inherit its visible rail.
                // The accepted Standard radius remains unchanged below.
                ? settings.ColumnWidth
                : settings.ColumnWidth * 0.35,
            settings.OffsetFromElements * 4.0);

    private static TagLayoutPlacement? FindNearbyFollowPlacement(
        IReadOnlyList<LayoutTagInput> cluster,
        bool? placeLeft,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
    {
        if (cluster.Count == 0 || !cluster.All(item => item.PreferLocalClustering) ||
            reservations.Count == 0)
        {
            return null;
        }

        HashSet<string> localGroups = cluster
            .Select(item => item.Group)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<long> preferredAnchorKeys = cluster
            .Select(item => item.PreferredFollowerAnchorTagKey)
            .Where(key => key != 0)
            .ToHashSet();
        double followRadius = NearbyFollowRadius(settings);
        TagLayoutPlacement? nearest = reservations
            // Follow a nearby established category, not another remote Duct
            // cluster. Duct-to-Duct layout remains bounded by the local planner.
            .Where(item => !localGroups.Contains(item.Group) &&
                           item.CanAnchorDuctFollowers)
            .Where(item => preferredAnchorKeys.Count == 0 ||
                           preferredAnchorKeys.Contains(item.TagKey))
            .Where(item => placeLeft is null || (placeLeft.Value
                ? item.Head.U < item.End.U
                : item.Head.U > item.End.U))
            .Select(item => new
            {
                Placement = item,
                HostDistance = cluster.Min(tag => Math.Sqrt(
                    SquaredDistance(item.End, tag.ElementBounds))),
                // A companion host can be close while its text rail is at the
                // far edge of the plan. Require the visible tag bounds to be
                // local too, otherwise omit this optional Duct label.
                TextDistance = cluster.Min(tag => Math.Sqrt(
                    SquaredDistance(item.TagBounds, tag.ElementBounds)))
            })
            .Where(item => item.HostDistance <= followRadius &&
                           item.TextDistance <= followRadius)
            .OrderBy(item => item.TextDistance)
            .ThenBy(item => item.HostDistance)
            .ThenBy(item => Math.Abs(item.Placement.TagBounds.MinU -
                                     cluster.Average(tag => tag.Anchor.U)))
            .Select(item => item.Placement)
            .FirstOrDefault();
        return nearest;
    }

    private static double FindFollowerTopEdge(
        IReadOnlyList<LayoutTagInput> cluster,
        TagLayoutPlacement nearest,
        IReadOnlyList<TagLayoutPlacement> reservations,
        SmartTagLayoutSettings settings)
    {
        double railTolerance = Math.Max(settings.Clearance, 0.02);
        double followRadius = NearbyFollowRadius(settings);
        List<TagLayoutPlacement> establishedColumn = reservations
            .Where(item => item.CanAnchorDuctFollowers &&
                           Math.Abs(item.TagBounds.MinU - nearest.TagBounds.MinU) <= railTolerance)
            .Where(item => cluster.Any(tag => Math.Sqrt(
                SquaredDistance(item.End, tag.ElementBounds)) <= followRadius))
            .ToList();
        double lowerEdge = establishedColumn.Count == 0
            ? nearest.TagBounds.MinV
            : establishedColumn.Min(item => item.TagBounds.MinV);
        return lowerEdge - settings.RowSpacing;
    }

    private static double SquaredDistance(LayoutPoint point, LayoutRect rectangle)
    {
        double du = point.U < rectangle.MinU
            ? rectangle.MinU - point.U
            : point.U > rectangle.MaxU
                ? point.U - rectangle.MaxU
                : 0.0;
        double dv = point.V < rectangle.MinV
            ? rectangle.MinV - point.V
            : point.V > rectangle.MaxV
                ? point.V - rectangle.MaxV
                : 0.0;
        return du * du + dv * dv;
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
