namespace FamilyMEP.Plugin.SmartTag;

internal readonly record struct SmartTagStackRouteInput(
    long Key,
    LayoutRect Bounds,
    LayoutPoint Head,
    LayoutPoint End);

internal sealed record SmartTagStackRoutePlan(
    IReadOnlyList<long> OrderedKeys,
    int CrossingCount,
    double TotalLength);

internal sealed record SmartTagAutoColumnRoutePlan(
    IReadOnlyList<long> AboveNearestFirst,
    IReadOnlyList<long> BelowNearestFirst,
    int CrossingCount,
    double TotalLength);

internal static class SmartTagStackRouting
{
    internal static double GetVisibleLeftEdge(
        LayoutRect bodyBounds,
        LayoutPoint insertion,
        LayoutPoint leaderEnd,
        bool hasLeader)
    {
        if (!hasLeader) return bodyBounds.MinU;

        // In Revit tag families whose leader approaches from the left, the
        // insertion (TagHeadPosition) is the visible text/leader attachment.
        // A leaderless family bounding box can extend a long way to its left,
        // so using MinU makes AUTO believe the tag is already aligned. When
        // the host is on the right, the insertion is the right attachment and
        // the measured body MinU remains the real visible left edge.
        return leaderEnd.U <= insertion.U + 1e-8
            ? insertion.U
            : bodyBounds.MinU;
    }

    internal static double GetVisibleRightEdge(
        LayoutRect bodyBounds,
        LayoutPoint insertion,
        LayoutPoint leaderEnd,
        bool hasLeader)
    {
        if (!hasLeader) return bodyBounds.MaxU;

        // Mirror GetVisibleLeftEdge. When the host is on the right, Revit's
        // TagHeadPosition is the visible right attachment. For a left-host
        // leader, the measured body's MaxU is the visible right text edge.
        return leaderEnd.U >= insertion.U - 1e-8
            ? insertion.U
            : bodyBounds.MaxU;
    }

    internal static LayoutRect TrimAsymmetricLeaderTail(
        LayoutRect measuredBounds,
        LayoutPoint insertion,
        double minimumBodyHeight)
    {
        if (insertion.V < measuredBounds.MinV - 1e-8 ||
            insertion.V > measuredBounds.MaxV + 1e-8)
            return measuredBounds;

        double lowerSpan = Math.Max(0.0, insertion.V - measuredBounds.MinV);
        double upperSpan = Math.Max(0.0, measuredBounds.MaxV - insertion.V);
        double shorterSpan = Math.Min(lowerSpan, upperSpan);
        double longerSpan = Math.Max(lowerSpan, upperSpan);
        double safeHeight = Math.Max(minimumBodyHeight, 1e-8);

        // A failed leader-off measurement normally has one short text-side
        // span and one much longer vertical tail to the host. Only trim when
        // both the ratio and the absolute excess prove that asymmetry. A real
        // multi-line body therefore keeps its measured height.
        double asymmetricLimit = Math.Max(
            shorterSpan * 2.25,
            shorterSpan + safeHeight);
        if (longerSpan <= asymmetricLimit) return measuredBounds;

        double bodyHeight = Math.Min(
            measuredBounds.Height,
            Math.Max(safeHeight, shorterSpan * 2.0));
        return new LayoutRect(
            measuredBounds.MinU,
            insertion.V - bodyHeight * 0.5,
            measuredBounds.MaxU,
            insertion.V + bodyHeight * 0.5);
    }

    internal static bool ShouldUseStraightHorizontalLeader(
        LayoutPoint head,
        LayoutPoint end,
        double axisTolerance,
        bool crossesOtherBody,
        bool crossesOtherLeader) =>
        Math.Abs(head.V - end.V) <= Math.Max(axisTolerance, 0.0) &&
        !crossesOtherBody &&
        !crossesOtherLeader;

    internal static LayoutPoint GetVisibleLeaderAttachmentPoint(
        LayoutRect bodyBounds,
        LayoutPoint leaderEnd)
    {
        double centerU = (bodyBounds.MinU + bodyBounds.MaxU) * 0.5;
        double centerV = (bodyBounds.MinV + bodyBounds.MaxV) * 0.5;
        return new LayoutPoint(
            leaderEnd.U <= centerU ? bodyBounds.MinU : bodyBounds.MaxU,
            centerV);
    }

    internal static LayoutRect AnchorLeaderlessBodyToLiveBounds(
        LayoutRect leaderlessBody,
        LayoutRect liveFullBounds,
        LayoutPoint insertion,
        LayoutPoint leaderEnd)
    {
        double width = Math.Max(leaderlessBody.Width, 1e-8);
        if (liveFullBounds.Width + 1e-8 < width) return leaderlessBody;
        double centerV = (leaderlessBody.MinV + leaderlessBody.MaxV) * 0.5;
        if (leaderEnd.U <= insertion.U)
        {
            return new LayoutRect(
                liveFullBounds.MaxU - width,
                centerV - leaderlessBody.Height * 0.5,
                liveFullBounds.MaxU,
                centerV + leaderlessBody.Height * 0.5);
        }
        return new LayoutRect(
            liveFullBounds.MinU,
            centerV - leaderlessBody.Height * 0.5,
            liveFullBounds.MinU + width,
            centerV + leaderlessBody.Height * 0.5);
    }

    public static SmartTagStackRoutePlan FindBestOrder(
        IReadOnlyList<SmartTagStackRouteInput> inputs,
        LayoutRect referenceBounds,
        double referenceEdge,
        bool alignLeftEdge,
        double rowGap,
        bool placeAbove = false)
    {
        if (inputs.Count == 0)
            return new SmartTagStackRoutePlan([], 0, 0.0);

        SmartTagStackRoutePlan? best = null;
        if (inputs.Count <= 8)
        {
            var used = new bool[inputs.Count];
            var order = new int[inputs.Count];
            SearchAll(0);

            void SearchAll(int depth)
            {
                if (depth == inputs.Count)
                {
                    Consider(order.Select(index => inputs[index]).ToArray());
                    return;
                }
                for (int index = 0; index < inputs.Count; index++)
                {
                    if (used[index]) continue;
                    used[index] = true;
                    order[depth] = index;
                    SearchAll(depth + 1);
                    used[index] = false;
                }
            }
        }
        else
        {
            IEnumerable<SmartTagStackRouteInput>[] seeds =
            [
                inputs.OrderByDescending(item => item.End.V).ThenBy(item => item.End.U),
                inputs.OrderByDescending(item => item.End.V).ThenByDescending(item => item.End.U),
                inputs.OrderBy(item => item.End.U).ThenByDescending(item => item.End.V),
                inputs.OrderByDescending(item => item.End.U).ThenByDescending(item => item.End.V),
                inputs.OrderBy(item => item.Key)
            ];
            foreach (IEnumerable<SmartTagStackRouteInput> seed in seeds)
            {
                SmartTagStackRouteInput[] improved = ImproveBySwaps(seed.ToArray());
                Consider(improved);
            }
        }

        return best!;

        SmartTagStackRouteInput[] ImproveBySwaps(SmartTagStackRouteInput[] seed)
        {
            SmartTagStackRouteInput[] current = seed;
            RouteScore currentScore = Score(current);
            int maximumPasses = Math.Min(8, Math.Max(2, current.Length / 3));
            for (int pass = 0; pass < maximumPasses; pass++)
            {
                bool changed = false;
                for (int index = 0; index + 1 < current.Length; index++)
                {
                    SmartTagStackRouteInput[] candidate = current.ToArray();
                    (candidate[index], candidate[index + 1]) =
                        (candidate[index + 1], candidate[index]);
                    RouteScore candidateScore = Score(candidate);
                    if (!IsBetter(candidateScore, currentScore)) continue;
                    current = candidate;
                    currentScore = candidateScore;
                    changed = true;
                }
                if (!changed) break;
            }
            return current;
        }

        void Consider(IReadOnlyList<SmartTagStackRouteInput> candidate)
        {
            RouteScore score = Score(candidate);
            if (best is not null &&
                !IsBetter(score, new RouteScore(best.CrossingCount, best.TotalLength)))
                return;
            best = new SmartTagStackRoutePlan(
                candidate.Select(item => item.Key).ToArray(),
                score.Crossings,
                score.Length);
        }

        RouteScore Score(IReadOnlyList<SmartTagStackRouteInput> order)
        {
            var routes = new List<PredictedRoute>(order.Count);
            double nextBoundary = placeAbove
                ? referenceBounds.MaxV + rowGap
                : referenceBounds.MinV - rowGap;
            foreach (SmartTagStackRouteInput item in order)
            {
                double currentCenterV = (item.Bounds.MinV + item.Bounds.MaxV) * 0.5;
                double desiredCenterV = placeAbove
                    ? nextBoundary + item.Bounds.Height * 0.5
                    : nextBoundary - item.Bounds.Height * 0.5;
                double targetEdge = alignLeftEdge ? item.Bounds.MinU : item.Bounds.MaxU;
                var head = new LayoutPoint(
                    item.Head.U + referenceEdge - targetEdge,
                    item.Head.V + desiredCenterV - currentCenterV);
                var elbow = new LayoutPoint(item.End.U, head.V);
                routes.Add(new PredictedRoute(
                    new LayoutSegment(head, elbow),
                    new LayoutSegment(elbow, item.End)));
                nextBoundary = placeAbove
                    ? desiredCenterV + item.Bounds.Height * 0.5 + rowGap
                    : desiredCenterV - item.Bounds.Height * 0.5 - rowGap;
            }

            int crossings = 0;
            for (int first = 0; first < routes.Count; first++)
            {
                for (int second = first + 1; second < routes.Count; second++)
                {
                    if (HorizontalCrossesVertical(
                            routes[first].Horizontal,
                            routes[second].Vertical))
                        crossings++;
                    if (HorizontalCrossesVertical(
                            routes[second].Horizontal,
                            routes[first].Vertical))
                        crossings++;
                }
            }
            double length = routes.Sum(route =>
                SegmentLength(route.Horizontal) + SegmentLength(route.Vertical));
            return new RouteScore(crossings, length);
        }
    }

    public static SmartTagAutoColumnRoutePlan FindBestAroundReference(
        IReadOnlyList<SmartTagStackRouteInput> targets,
        SmartTagStackRouteInput reference,
        LayoutRect referenceBounds,
        double referenceEdge,
        bool alignLeftEdge,
        double rowGap,
        bool belowOnly = false,
        bool aboveOnly = false)
    {
        if (targets.Count == 0)
            return new SmartTagAutoColumnRoutePlan([], [], 0, 0.0);
        if (belowOnly && aboveOnly)
            throw new ArgumentException("AUTO column cannot be both below-only and above-only.");

        SmartTagAutoColumnRoutePlan? best = null;
        AutoRouteScore? bestScore = null;
        if (targets.Count <= 8)
        {
            var used = new bool[targets.Count];
            var order = new int[targets.Count];
            SearchAll(0);

            void SearchAll(int depth)
            {
                if (depth == targets.Count)
                {
                    SmartTagStackRouteInput[] candidate = order
                        .Select(index => targets[index])
                        .ToArray();
                    if (belowOnly)
                    {
                        Consider(candidate, 0);
                    }
                    else if (aboveOnly)
                    {
                        Consider(candidate, candidate.Length);
                    }
                    else
                    {
                        for (int rank = 0; rank <= candidate.Length; rank++)
                            Consider(candidate, rank);
                    }
                    return;
                }
                for (int index = 0; index < targets.Count; index++)
                {
                    if (used[index]) continue;
                    used[index] = true;
                    order[depth] = index;
                    SearchAll(depth + 1);
                    used[index] = false;
                }
            }
        }
        else
        {
            bool singleSide = belowOnly || aboveOnly;
            if (singleSide && targets.Count <= 20)
            {
                // Swap/insert descent can stop at a local minimum: no single
                // edit helps, although a different prefix removes several
                // later leader crossings. Keep a bounded set of the best
                // partial nearest-first orders. Physical clashes and order
                // inversions are monotonic for an already placed prefix, so
                // pruning here is safe and deterministic for normal AUTO
                // selections without attempting an n! exhaustive search.
                int beamWidth = targets.Count <= 14 ? 768 : 384;
                var beam = new List<(SmartTagStackRouteInput[] Sequence,
                    ulong Used, AutoRouteScore Score)>
                {
                    ([], 0UL, new AutoRouteScore(0, 0, 0.0))
                };
                for (int depth = 0; depth < targets.Count; depth++)
                {
                    var next = new List<(SmartTagStackRouteInput[] Sequence,
                        ulong Used, AutoRouteScore Score)>(
                        Math.Min(beamWidth * Math.Max(1, targets.Count - depth), 8192));
                    foreach (var state in beam)
                    for (int index = 0; index < targets.Count; index++)
                    {
                        ulong bit = 1UL << index;
                        if ((state.Used & bit) != 0) continue;
                        SmartTagStackRouteInput[] nearestFirst =
                            [.. state.Sequence, targets[index]];
                        SmartTagStackRouteInput[] scoreOrder = aboveOnly
                            ? nearestFirst.Reverse().ToArray()
                            : nearestFirst;
                        int scoreRank = aboveOnly ? scoreOrder.Length : 0;
                        next.Add((nearestFirst, state.Used | bit,
                            Score(scoreOrder, scoreRank)));
                    }
                    beam = next
                        .OrderBy(item => item.Score.PhysicalClashes)
                        .ThenBy(item => item.Score.OrderInversions)
                        .ThenBy(item => item.Score.Length)
                        .ThenBy(item => item.Sequence[^1].Key)
                        .Take(beamWidth)
                        .ToList();
                }
                foreach (var state in beam.Take(12))
                {
                    SmartTagStackRouteInput[] candidate = aboveOnly
                        ? state.Sequence.Reverse().ToArray()
                        : state.Sequence;
                    Consider(candidate, aboveOnly ? candidate.Length : 0);
                }
            }

            IEnumerable<SmartTagStackRouteInput>[] seeds =
            [
                targets.OrderByDescending(item => item.End.V).ThenBy(item => item.End.U),
                targets.OrderByDescending(item => item.End.V).ThenByDescending(item => item.End.U),
                targets.OrderBy(item => item.End.U).ThenByDescending(item => item.End.V),
                targets.OrderByDescending(item => item.End.U).ThenByDescending(item => item.End.V),
                targets.OrderByDescending(item => item.Head.V).ThenBy(item => item.End.U),
                targets.OrderBy(item => item.Key)
            ];
            foreach (IEnumerable<SmartTagStackRouteInput> seedSource in seeds)
            {
                SmartTagStackRouteInput[] seed = seedSource.ToArray();
                foreach (int rank in CandidateRanks(seed.Length))
                {
                    SmartTagStackRouteInput[] current = seed;
                    AutoRouteScore currentScore = Score(current, rank);
                    int maximumPasses = singleSide
                        ? Math.Min(32, Math.Max(10, current.Length + 4))
                        : Math.Min(8, Math.Max(2, current.Length / 3));
                    for (int pass = 0; pass < maximumPasses; pass++)
                    {
                        if (currentScore.PhysicalClashes == 0 &&
                            currentScore.OrderInversions == 0)
                            break;
                        SmartTagStackRouteInput[]? bestCandidate = null;
                        AutoRouteScore bestCandidateScore = currentScore;
                        if (singleSide && current.Length <= 36)
                        {
                            // A remaining crossing may require moving a tag
                            // across several rows. Search both arbitrary swaps
                            // and remove/insert moves, then take the steepest
                            // improvement. Insertions preserve the relative
                            // order of all intervening leader routes.
                            for (int first = 0; first < current.Length; first++)
                            {
                                for (int second = first + 1; second < current.Length; second++)
                                {
                                    SmartTagStackRouteInput[] candidate = current.ToArray();
                                    (candidate[first], candidate[second]) =
                                        (candidate[second], candidate[first]);
                                    AutoRouteScore candidateScore = Score(candidate, rank);
                                    if (!IsBetterAuto(candidateScore, bestCandidateScore)) continue;
                                    bestCandidate = candidate;
                                    bestCandidateScore = candidateScore;
                                }
                            }
                            for (int from = 0; from < current.Length; from++)
                            {
                                for (int to = 0; to < current.Length; to++)
                                {
                                    if (to == from) continue;
                                    var candidateList = current.ToList();
                                    SmartTagStackRouteInput moving = candidateList[from];
                                    candidateList.RemoveAt(from);
                                    candidateList.Insert(to, moving);
                                    SmartTagStackRouteInput[] candidate = candidateList.ToArray();
                                    AutoRouteScore candidateScore = Score(candidate, rank);
                                    if (!IsBetterAuto(candidateScore, bestCandidateScore)) continue;
                                    bestCandidate = candidate;
                                    bestCandidateScore = candidateScore;
                                }
                            }
                        }
                        else
                        {
                            for (int index = 0; index + 1 < current.Length; index++)
                            {
                                SmartTagStackRouteInput[] candidate = current.ToArray();
                                (candidate[index], candidate[index + 1]) =
                                    (candidate[index + 1], candidate[index]);
                                AutoRouteScore candidateScore = Score(candidate, rank);
                                if (!IsBetterAuto(candidateScore, bestCandidateScore)) continue;
                                bestCandidate = candidate;
                                bestCandidateScore = candidateScore;
                            }
                        }
                        if (bestCandidate is null) break;
                        current = bestCandidate;
                        currentScore = bestCandidateScore;
                    }
                    Consider(current, rank);
                }
                if (bestScore is { PhysicalClashes: 0, OrderInversions: 0 }) break;
            }
        }
        return best!;

        IEnumerable<int> CandidateRanks(int count)
        {
            if (belowOnly) return [0];
            if (aboveOnly) return [count];
            if (count <= 24) return Enumerable.Range(0, count + 1);
            int hostRank = targets.Count(item =>
                item.End.V > reference.End.V ||
                Math.Abs(item.End.V - reference.End.V) <= 1e-8 &&
                item.End.U < reference.End.U);
            return new[]
                {
                    0, count, count / 4, count / 2, count * 3 / 4,
                    Math.Max(0, hostRank - 2), Math.Max(0, hostRank - 1),
                    hostRank, Math.Min(count, hostRank + 1), Math.Min(count, hostRank + 2)
                }
                .Distinct()
                .OrderBy(value => value);
        }

        void Consider(IReadOnlyList<SmartTagStackRouteInput> order, int referenceRank)
        {
            AutoRouteScore score = Score(order, referenceRank);
            if (bestScore is not null && !IsBetterAuto(score, bestScore.Value))
                return;
            bestScore = score;
            best = new SmartTagAutoColumnRoutePlan(
                order.Take(referenceRank).Reverse().Select(item => item.Key).ToArray(),
                order.Skip(referenceRank).Select(item => item.Key).ToArray(),
                score.PhysicalClashes,
                score.Length);
        }

        AutoRouteScore Score(
            IReadOnlyList<SmartTagStackRouteInput> order,
            int referenceRank)
        {
            var routes = new List<PredictedRoute>(order.Count + 1)
            {
                CreateFixedRoute(reference)
            };
            double upperBoundary = referenceBounds.MaxV + rowGap;
            for (int index = referenceRank - 1; index >= 0; index--)
            {
                SmartTagStackRouteInput item = order[index];
                double center = upperBoundary + item.Bounds.Height * 0.5;
                routes.Add(CreateMovedRoute(item, center));
                upperBoundary = center + item.Bounds.Height * 0.5 + rowGap;
            }
            double lowerBoundary = referenceBounds.MinV - rowGap;
            for (int index = referenceRank; index < order.Count; index++)
            {
                SmartTagStackRouteInput item = order[index];
                double center = lowerBoundary - item.Bounds.Height * 0.5;
                routes.Add(CreateMovedRoute(item, center));
                lowerBoundary = center - item.Bounds.Height * 0.5 - rowGap;
            }

            int physicalClashes = 0;
            int orderInversions = 0;
            double routeClearance = Math.Max(rowGap * 0.35, 1e-8);
            for (int first = 0; first < routes.Count; first++)
            {
                for (int second = first + 1; second < routes.Count; second++)
                {
                    if (OrthogonalSegmentsClash(
                            routes[first].Horizontal,
                            routes[second].Vertical,
                            routeClearance))
                        physicalClashes++;
                    if (OrthogonalSegmentsClash(
                            routes[second].Horizontal,
                            routes[first].Vertical,
                            routeClearance))
                        physicalClashes++;
                    if (ParallelSegmentsClash(
                            routes[first].Vertical,
                            routes[second].Vertical,
                            vertical: true,
                            routeClearance))
                        physicalClashes++;
                    if (ParallelSegmentsClash(
                            routes[first].Horizontal,
                            routes[second].Horizontal,
                            vertical: false,
                            routeClearance))
                        physicalClashes++;
                    if (RouteOrderIsInverted(routes[first], routes[second]))
                        orderInversions++;
                }
            }
            double length = routes.Sum(route =>
                SegmentLength(route.Horizontal) + SegmentLength(route.Vertical));
            return new AutoRouteScore(physicalClashes, orderInversions, length);
        }

        PredictedRoute CreateFixedRoute(SmartTagStackRouteInput item)
        {
            var elbow = new LayoutPoint(item.End.U, item.Head.V);
            return new PredictedRoute(
                new LayoutSegment(item.Head, elbow),
                new LayoutSegment(elbow, item.End));
        }

        PredictedRoute CreateMovedRoute(SmartTagStackRouteInput item, double desiredCenterV)
        {
            double currentCenterV = (item.Bounds.MinV + item.Bounds.MaxV) * 0.5;
            double targetEdge = alignLeftEdge ? item.Bounds.MinU : item.Bounds.MaxU;
            var head = new LayoutPoint(
                item.Head.U + referenceEdge - targetEdge,
                item.Head.V + desiredCenterV - currentCenterV);
            var elbow = new LayoutPoint(item.End.U, head.V);
            return new PredictedRoute(
                new LayoutSegment(head, elbow),
                new LayoutSegment(elbow, item.End));
        }
    }

    internal static IReadOnlyList<double> FindBestLaneAssignment(
        IReadOnlyList<SmartTagStackRouteInput> movableRoutes,
        IReadOnlyList<double> candidateLanes,
        IReadOnlyList<SmartTagStackRouteInput>? fixedRoutes,
        double clearance)
    {
        if (movableRoutes.Count == 0) return [];
        if (movableRoutes.Count != candidateLanes.Count)
            throw new ArgumentException("AUTO lane count must match the movable route count.");

        double[] lanes = candidateLanes.OrderBy(value => value).ToArray();
        SmartTagStackRouteInput[] fixedItems = fixedRoutes?.ToArray() ?? [];
        double[]? bestAssignment = null;
        LaneScore? bestScore = null;

        if (movableRoutes.Count <= 8)
        {
            var used = new bool[movableRoutes.Count];
            var order = new int[movableRoutes.Count];
            SearchAll(0);

            void SearchAll(int depth)
            {
                if (depth == movableRoutes.Count)
                {
                    Consider(order);
                    return;
                }
                for (int index = 0; index < movableRoutes.Count; index++)
                {
                    if (used[index]) continue;
                    used[index] = true;
                    order[depth] = index;
                    SearchAll(depth + 1);
                    used[index] = false;
                }
            }
        }
        else
        {
            IEnumerable<int>[] seeds =
            [
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderBy(index => movableRoutes[index].Head.V)
                    .ThenBy(index => movableRoutes[index].Key),
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderByDescending(index => movableRoutes[index].Head.V)
                    .ThenBy(index => movableRoutes[index].Key),
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderBy(index => movableRoutes[index].End.V)
                    .ThenBy(index => movableRoutes[index].Key),
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderByDescending(index => movableRoutes[index].End.V)
                    .ThenBy(index => movableRoutes[index].Key),
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderBy(index => movableRoutes[index].End.U)
                    .ThenBy(index => movableRoutes[index].Key),
                Enumerable.Range(0, movableRoutes.Count)
                    .OrderByDescending(index => movableRoutes[index].End.U)
                    .ThenBy(index => movableRoutes[index].Key)
            ];
            foreach (IEnumerable<int> seed in seeds)
            {
                int[] current = seed.ToArray();
                LaneScore currentScore = Score(ToAssignment(current));
                int maximumPasses = Math.Min(24, movableRoutes.Count + 6);
                for (int pass = 0; pass < maximumPasses; pass++)
                {
                    int[]? next = null;
                    LaneScore nextScore = currentScore;
                    for (int first = 0; first < current.Length; first++)
                    for (int second = first + 1; second < current.Length; second++)
                    {
                        int[] candidate = current.ToArray();
                        (candidate[first], candidate[second]) =
                            (candidate[second], candidate[first]);
                        LaneScore candidateScore = Score(ToAssignment(candidate));
                        if (!IsBetterLane(candidateScore, nextScore)) continue;
                        next = candidate;
                        nextScore = candidateScore;
                    }
                    if (next is null) break;
                    current = next;
                    currentScore = nextScore;
                }
                Consider(current);
            }
        }

        return bestAssignment!;

        void Consider(IReadOnlyList<int> routeOrderByLane)
        {
            double[] assignment = ToAssignment(routeOrderByLane);
            LaneScore score = Score(assignment);
            if (bestScore is not null && !IsBetterLane(score, bestScore.Value)) return;
            bestScore = score;
            bestAssignment = assignment;
        }

        double[] ToAssignment(IReadOnlyList<int> routeOrderByLane)
        {
            var assignment = new double[movableRoutes.Count];
            for (int laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
                assignment[routeOrderByLane[laneIndex]] = lanes[laneIndex];
            return assignment;
        }

        LaneScore Score(IReadOnlyList<double> assignment)
        {
            var routes = new List<(PredictedRoute Route, bool Movable)>(
                movableRoutes.Count + fixedItems.Length);
            double movement = 0.0;
            for (int index = 0; index < movableRoutes.Count; index++)
            {
                SmartTagStackRouteInput item = movableRoutes[index];
                double lane = assignment[index];
                var elbow = new LayoutPoint(lane, item.Head.V);
                var end = new LayoutPoint(lane, item.End.V);
                routes.Add((new PredictedRoute(
                    new LayoutSegment(item.Head, elbow),
                    new LayoutSegment(elbow, end)), true));
                movement += Math.Abs(lane - item.End.U);
            }
            foreach (SmartTagStackRouteInput item in fixedItems)
            {
                var elbow = new LayoutPoint(item.End.U, item.Head.V);
                routes.Add((new PredictedRoute(
                    new LayoutSegment(item.Head, elbow),
                    new LayoutSegment(elbow, item.End)), false));
            }

            int clashes = 0;
            for (int first = 0; first < routes.Count; first++)
            for (int second = first + 1; second < routes.Count; second++)
            {
                if (!routes[first].Movable && !routes[second].Movable) continue;
                PredictedRoute firstRoute = routes[first].Route;
                PredictedRoute secondRoute = routes[second].Route;
                if (OrthogonalSegmentsClash(
                        firstRoute.Horizontal, secondRoute.Vertical, clearance))
                    clashes++;
                if (OrthogonalSegmentsClash(
                        secondRoute.Horizontal, firstRoute.Vertical, clearance))
                    clashes++;
                if (ParallelSegmentsClash(
                        firstRoute.Vertical, secondRoute.Vertical, true, clearance))
                    clashes++;
                if (ParallelSegmentsClash(
                        firstRoute.Horizontal, secondRoute.Horizontal, false, clearance))
                    clashes++;
            }
            double length = routes
                .Where(item => item.Movable)
                .Sum(item => SegmentLength(item.Route.Horizontal) +
                             SegmentLength(item.Route.Vertical));
            return new LaneScore(clashes, movement, length);
        }
    }

    private static bool IsBetter(RouteScore candidate, RouteScore current)
    {
        if (candidate.Crossings != current.Crossings)
            return candidate.Crossings < current.Crossings;
        return candidate.Length < current.Length - 1e-9;
    }

    private static bool IsBetterAuto(AutoRouteScore candidate, AutoRouteScore current)
    {
        // A real segment intersection is always worse than a non-monotonic
        // host order. Some one-elbow routes can only avoid a crossing by
        // placing a farther endpoint one row earlier; host order is therefore
        // a tie-breaker, never a substitute for the actual geometry test.
        if (candidate.PhysicalClashes != current.PhysicalClashes)
            return candidate.PhysicalClashes < current.PhysicalClashes;
        if (candidate.OrderInversions != current.OrderInversions)
            return candidate.OrderInversions < current.OrderInversions;
        return candidate.Length < current.Length - 1e-9;
    }

    private static bool IsBetterLane(LaneScore candidate, LaneScore current)
    {
        if (candidate.PhysicalClashes != current.PhysicalClashes)
            return candidate.PhysicalClashes < current.PhysicalClashes;
        if (Math.Abs(candidate.EndpointMovement - current.EndpointMovement) > 1e-9)
            return candidate.EndpointMovement < current.EndpointMovement;
        return candidate.Length < current.Length - 1e-9;
    }

    private static bool HorizontalCrossesVertical(
        LayoutSegment horizontal,
        LayoutSegment vertical)
    {
        const double tolerance = 1e-8;
        double minU = Math.Min(horizontal.Start.U, horizontal.End.U);
        double maxU = Math.Max(horizontal.Start.U, horizontal.End.U);
        double minV = Math.Min(vertical.Start.V, vertical.End.V);
        double maxV = Math.Max(vertical.Start.V, vertical.End.V);
        return vertical.Start.U > minU + tolerance &&
               vertical.Start.U < maxU - tolerance &&
               horizontal.Start.V > minV + tolerance &&
               horizontal.Start.V < maxV - tolerance;
    }

    private static bool OrthogonalSegmentsClash(
        LayoutSegment horizontal,
        LayoutSegment vertical,
        double clearance)
    {
        double minU = Math.Min(horizontal.Start.U, horizontal.End.U) - clearance;
        double maxU = Math.Max(horizontal.Start.U, horizontal.End.U) + clearance;
        double minV = Math.Min(vertical.Start.V, vertical.End.V) - clearance;
        double maxV = Math.Max(vertical.Start.V, vertical.End.V) + clearance;
        return vertical.Start.U >= minU &&
               vertical.Start.U <= maxU &&
               horizontal.Start.V >= minV &&
               horizontal.Start.V <= maxV;
    }

    private static bool ParallelSegmentsClash(
        LayoutSegment first,
        LayoutSegment second,
        bool vertical,
        double clearance)
    {
        if (vertical)
        {
            if (Math.Abs(first.Start.U - second.Start.U) > clearance) return false;
            double firstMin = Math.Min(first.Start.V, first.End.V);
            double firstMax = Math.Max(first.Start.V, first.End.V);
            double secondMin = Math.Min(second.Start.V, second.End.V);
            double secondMax = Math.Max(second.Start.V, second.End.V);
            return Math.Min(firstMax, secondMax) - Math.Max(firstMin, secondMin) > -clearance;
        }

        if (Math.Abs(first.Start.V - second.Start.V) > clearance) return false;
        double firstMinimum = Math.Min(first.Start.U, first.End.U);
        double firstMaximum = Math.Max(first.Start.U, first.End.U);
        double secondMinimum = Math.Min(second.Start.U, second.End.U);
        double secondMaximum = Math.Max(second.Start.U, second.End.U);
        return Math.Min(firstMaximum, secondMaximum) -
               Math.Max(firstMinimum, secondMinimum) > -clearance;
    }

    private static bool RouteOrderIsInverted(
        PredictedRoute first,
        PredictedRoute second)
    {
        const double tolerance = 1e-8;
        double headDelta = first.Horizontal.Start.V - second.Horizontal.Start.V;
        double endDelta = first.Vertical.End.V - second.Vertical.End.V;
        return Math.Abs(headDelta) > tolerance &&
               Math.Abs(endDelta) > tolerance &&
               headDelta * endDelta < 0.0;
    }

    private static double SegmentLength(LayoutSegment segment) =>
        Math.Abs(segment.Start.U - segment.End.U) +
        Math.Abs(segment.Start.V - segment.End.V);

    private readonly record struct PredictedRoute(
        LayoutSegment Horizontal,
        LayoutSegment Vertical);

    private readonly record struct RouteScore(int Crossings, double Length);

    private readonly record struct AutoRouteScore(
        int PhysicalClashes,
        int OrderInversions,
        double Length);

    private readonly record struct LaneScore(
        int PhysicalClashes,
        double EndpointMovement,
        double Length);
}
