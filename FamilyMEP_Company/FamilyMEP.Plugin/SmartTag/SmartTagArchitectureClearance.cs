namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Architecture is applied only after the accepted MEP tag layout is complete.
/// A clashing cluster receives one common translation, preserving every tag's
/// relative row, spacing, ordering, and selected side.
/// </summary>
internal static class SmartTagArchitectureClearance
{
    public static LayoutPoint FindClusterShift(
        IReadOnlyList<TagLayoutPlacement> cluster,
        IReadOnlyDictionary<long, LayoutTagInput> inputs,
        IReadOnlyList<TagLayoutPlacement> fixedPlacements,
        IReadOnlyList<LayoutObstacle> obstacles,
        LayoutRect frame,
        SmartTagLayoutSettings settings)
    {
        if (!settings.AvoidElements || cluster.Count == 0)
        {
            return new LayoutPoint(0.0, 0.0);
        }
        List<LayoutObstacle> architecture = obstacles
            .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Architecture)
            .ToList();
        if (architecture.Count == 0)
        {
            return new LayoutPoint(0.0, 0.0);
        }

        bool rightSide = cluster.Count(item => inputs.TryGetValue(item.TagKey, out LayoutTagInput? input) &&
                                               item.Head.U >= input.Anchor.U) * 2 >= cluster.Count;
        double outward = rightSide ? 1.0 : -1.0;
        double maximumWidth = cluster.Max(item => Math.Max(item.TagBounds.Width, 0.005));
        double maximumHeight = cluster.Max(item => Math.Max(item.TagBounds.Height, 0.005));
        double stepU = Math.Max(
            Math.Max(settings.Clearance * 2.0, maximumWidth * 0.18),
            0.005);
        double stepV = Math.Max(
            maximumHeight + settings.RowSpacing,
            settings.Clearance * 2.0 + 0.0025);
        double maximumShiftU = Math.Max(
            Math.Max(settings.OffsetFromElements * 2.0, maximumWidth * 1.5),
            settings.Clearance * 10.0);
        double maximumShiftV = Math.Max(
            stepV * 6.0,
            settings.ColumnWidth * 0.25);
        int levelsU = Math.Clamp((int)Math.Ceiling(maximumShiftU / stepU), 1, 12);
        int levelsV = Math.Clamp((int)Math.Ceiling(maximumShiftV / stepV), 1, 8);

        LayoutPoint zero = new(0.0, 0.0);
        ClusterQuality baseline = Measure(zero);
        // Architectural leader crossings alone must not move an accepted MEP
        // layout: most hosts sit behind/across walls, so that rule would move
        // nearly every cluster. Only visible tag text covering architecture
        // activates this post-process.
        if (baseline.ArchitectureText == 0)
        {
            return zero;
        }

        LayoutPoint bestShift = zero;
        ClusterQuality best = baseline;
        var candidates = new List<LayoutPoint>();
        for (int level = 1; level <= levelsU; level++)
        {
            double du = level * stepU;
            candidates.Add(new LayoutPoint(outward * du, 0.0));
            if (du <= maximumShiftU * 0.5)
            {
                candidates.Add(new LayoutPoint(-outward * du, 0.0));
            }
        }
        for (int level = 1; level <= levelsV; level++)
        {
            double dv = level * stepV;
            candidates.Add(new LayoutPoint(0.0, dv));
            candidates.Add(new LayoutPoint(0.0, -dv));
        }
        // A corner pocket is sometimes the nearest clear location around a wall
        // junction. Keep the search small and move the complete cluster together.
        for (int uLevel = 1; uLevel <= Math.Min(levelsU, 5); uLevel++)
        for (int vLevel = 1; vLevel <= Math.Min(levelsV, 4); vLevel++)
        {
            double du = outward * uLevel * stepU;
            double dv = vLevel * stepV;
            candidates.Add(new LayoutPoint(du, dv));
            candidates.Add(new LayoutPoint(du, -dv));
        }

        foreach (LayoutPoint shift in candidates)
        {
            if (!Fits(shift)) continue;
            ClusterQuality quality = Measure(shift);
            if (quality.IsBetterThan(best))
            {
                best = quality;
                bestShift = shift;
            }
        }
        return bestShift;

        bool Fits(LayoutPoint shift)
        {
            foreach (TagLayoutPlacement placement in cluster)
            {
                LayoutRect movedBounds = ShiftRect(placement.TagBounds, shift);
                if (movedBounds.MinU < frame.MinU + settings.Clearance ||
                    movedBounds.MaxU > frame.MaxU - settings.Clearance ||
                    movedBounds.MinV < frame.MinV + settings.Clearance ||
                    movedBounds.MaxV > frame.MaxV - settings.Clearance)
                {
                    return false;
                }
                if (!inputs.TryGetValue(placement.TagKey, out LayoutTagInput? input)) continue;
                double movedHeadU = placement.Head.U + shift.U;
                if (rightSide && movedHeadU < input.Anchor.U + settings.Clearance ||
                    !rightSide && movedHeadU > input.Anchor.U - settings.Clearance)
                {
                    return false;
                }
            }
            return true;
        }

        ClusterQuality Measure(LayoutPoint shift)
        {
            List<TagLayoutPlacement> moved = cluster
                .Select(item => Translate(item, inputs[item.TagKey], shift))
                .ToList();
            int architectureText = 0;
            int architectureLeader = 0;
            int mepModel = 0;
            foreach (TagLayoutPlacement placement in moved)
            {
                LayoutTagInput input = inputs[placement.TagKey];
                foreach (LayoutObstacle obstacle in obstacles)
                {
                    LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                    if (placement.TagBounds.Intersects(expanded))
                    {
                        if (obstacle.Kind == LayoutObstacleKind.Architecture) architectureText++;
                        else if (obstacle.ElementKey != input.ElementKey) mepModel++;
                    }
                    bool ownsStart = Contains(input.Anchor, expanded);
                    if (obstacle.Kind == LayoutObstacleKind.Architecture && !ownsStart &&
                        LeaderSegments(placement).Any(segment => Intersects(segment, expanded)))
                    {
                        architectureLeader++;
                    }
                }
            }

            int textConflicts = 0;
            int leaderConflicts = 0;
            for (int first = 0; first < moved.Count; first++)
            {
                for (int second = first + 1; second < moved.Count; second++)
                {
                    CountPair(moved[first], moved[second], ref textConflicts, ref leaderConflicts);
                }
                foreach (TagLayoutPlacement fixedPlacement in fixedPlacements)
                {
                    CountPair(moved[first], fixedPlacement, ref textConflicts, ref leaderConflicts);
                }
            }
            double travel = Math.Sqrt(shift.U * shift.U + shift.V * shift.V * 1.25);
            return new ClusterQuality(
                architectureText,
                textConflicts,
                mepModel,
                architectureLeader,
                leaderConflicts,
                travel);
        }

        void CountPair(
            TagLayoutPlacement first,
            TagLayoutPlacement second,
            ref int text,
            ref int leader)
        {
            if (first.TagBounds.Intersects(second.TagBounds.Expand(settings.Clearance))) text++;
            List<LayoutSegment> firstSegments = LeaderSegments(first);
            List<LayoutSegment> secondSegments = LeaderSegments(second);
            if (firstSegments.Any(segment => Intersects(segment, second.TagBounds.Expand(settings.Clearance))) ||
                secondSegments.Any(segment => Intersects(segment, first.TagBounds.Expand(settings.Clearance))) ||
                firstSegments.Any(a => secondSegments.Any(b => SegmentsIntersect(a, b))))
            {
                leader++;
            }
        }
    }

    public static TagLayoutPlacement Translate(
        TagLayoutPlacement placement,
        LayoutTagInput input,
        LayoutPoint shift)
    {
        LayoutPoint head = new(
            placement.Head.U + shift.U,
            placement.Head.V + shift.V);
        bool usesElbow = Math.Abs(head.V - input.Anchor.V) > 1e-7;
        LayoutPoint elbow = usesElbow
            ? new LayoutPoint(placement.End.U, head.V)
            : placement.End;
        return placement with
        {
            Head = head,
            Elbow = elbow,
            TagBounds = ShiftRect(placement.TagBounds, shift),
            UsesElbow = usesElbow,
            UsesFreeEnd = usesElbow
        };
    }

    private static LayoutRect ShiftRect(LayoutRect rect, LayoutPoint shift) => new(
        rect.MinU + shift.U,
        rect.MinV + shift.V,
        rect.MaxU + shift.U,
        rect.MaxV + shift.V);

    private static bool Contains(LayoutPoint point, LayoutRect rect) =>
        point.U >= rect.MinU && point.U <= rect.MaxU &&
        point.V >= rect.MinV && point.V <= rect.MaxV;

    private static List<LayoutSegment> LeaderSegments(TagLayoutPlacement placement)
    {
        var result = new List<LayoutSegment>
        {
            new(placement.Head, placement.Elbow)
        };
        if (placement.UsesElbow)
        {
            result.Add(new LayoutSegment(placement.End, placement.Elbow));
        }
        return result;
    }

    private static bool Intersects(LayoutSegment segment, LayoutRect rect)
    {
        if (segment.IsHorizontal)
        {
            double minU = Math.Min(segment.Start.U, segment.End.U);
            double maxU = Math.Max(segment.Start.U, segment.End.U);
            return segment.Start.V >= rect.MinV && segment.Start.V <= rect.MaxV &&
                   maxU >= rect.MinU && minU <= rect.MaxU;
        }
        if (segment.IsVertical)
        {
            double minV = Math.Min(segment.Start.V, segment.End.V);
            double maxV = Math.Max(segment.Start.V, segment.End.V);
            return segment.Start.U >= rect.MinU && segment.Start.U <= rect.MaxU &&
                   maxV >= rect.MinV && minV <= rect.MaxV;
        }
        return new LayoutRect(
                Math.Min(segment.Start.U, segment.End.U),
                Math.Min(segment.Start.V, segment.End.V),
                Math.Max(segment.Start.U, segment.End.U),
                Math.Max(segment.Start.V, segment.End.V))
            .Intersects(rect);
    }

    private static bool SegmentsIntersect(LayoutSegment first, LayoutSegment second)
    {
        if (first.IsHorizontal && second.IsVertical)
        {
            return Between(second.Start.U, first.Start.U, first.End.U) &&
                   Between(first.Start.V, second.Start.V, second.End.V);
        }
        if (first.IsVertical && second.IsHorizontal) return SegmentsIntersect(second, first);
        if (first.IsHorizontal && second.IsHorizontal)
        {
            return Math.Abs(first.Start.V - second.Start.V) <= 1e-8 &&
                   RangesOverlap(first.Start.U, first.End.U, second.Start.U, second.End.U);
        }
        if (first.IsVertical && second.IsVertical)
        {
            return Math.Abs(first.Start.U - second.Start.U) <= 1e-8 &&
                   RangesOverlap(first.Start.V, first.End.V, second.Start.V, second.End.V);
        }
        return false;
    }

    private static bool Between(double value, double first, double second) =>
        value >= Math.Min(first, second) - 1e-8 &&
        value <= Math.Max(first, second) + 1e-8;

    private static bool RangesOverlap(double a1, double a2, double b1, double b2) =>
        Math.Max(Math.Min(a1, a2), Math.Min(b1, b2)) <=
        Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) + 1e-8;

    private readonly record struct ClusterQuality(
        int ArchitectureText,
        int TextConflicts,
        int MepModel,
        int ArchitectureLeader,
        int LeaderConflicts,
        double Travel)
    {
        public bool IsBetterThan(ClusterQuality other) =>
            ArchitectureText < other.ArchitectureText ||
            ArchitectureText == other.ArchitectureText &&
            (TextConflicts < other.TextConflicts ||
             TextConflicts == other.TextConflicts &&
             (MepModel < other.MepModel ||
              MepModel == other.MepModel &&
              (LeaderConflicts < other.LeaderConflicts ||
               LeaderConflicts == other.LeaderConflicts &&
               (Travel < other.Travel - 1e-10 ||
                Math.Abs(Travel - other.Travel) <= 1e-10 &&
                ArchitectureLeader < other.ArchitectureLeader))));
    }
}
