using Autodesk.Revit.DB;

namespace FamilyMEP.Plugin.DrainConnection;

internal static class DrainGeometry
{
    // Used only when no verified family/routing angle was supplied. Real model
    // angles are passed by the controller and tried individually.
    private static readonly double[] AutoFittingAngles = [45.0];
    private static readonly double[] OffsetMultipliers =
        [0.35, 0.50, 0.75, 1.0, 1.25, 1.50, 2.0, 2.50, 3.0, 4.0];
    private static readonly double[] Case02NearMainMultipliers = [3.0, 4.0, 5.0];
    private const double Epsilon = 1e-9;

    public static IReadOnlyList<DrainRoute> BuildCandidates(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter,
        IReadOnlyList<double>? fittingAngles = null,
        double? minimumOffsetOverride = null,
        double? minimumStubOverride = null,
        double? endClearanceOverride = null,
        IReadOnlyList<double>? offsetMultipliers = null)
    {
        // Cast-iron 45-degree elbows commonly need substantially more takeout
        // than a generic short-radius fitting. Case 06 is pipe-only, so reserve
        // real construction length before asking Revit to place any elbow.
        double minimumOffset = minimumOffsetOverride ??
            Math.Max(Mm(20), branchDiameter * 0.25);
        double minimumStub = minimumStubOverride ??
            Math.Max(Mm(20), branchDiameter * 0.20);
        double endClearance = endClearanceOverride ??
            Math.Max(Mm(25), mainDiameter * 0.35);
        var routes = new List<DrainRoute>();
        DrainSide[] sides = [DrainSide.Left, DrainSide.Right];

        foreach (DrainSide side in sides)
        {
            foreach (double fittingAngle in fittingAngles ?? AutoFittingAngles)
            {
                // Pipe geometry is fixed at exactly 45 degrees in plan. The
                // measured Y connector angle is fitting metadata only.
                const double planAngle = 45.0;
                foreach (double multiplier in offsetMultipliers ?? OffsetMultipliers)
                {
                    try
                    {
                        DrainRoute route = Build(
                            mainStart,
                            mainEnd,
                            drainOrigin,
                            side,
                            settings.SlopePercent,
                            planAngle,
                            fittingAngle,
                            Math.Max(minimumOffset, branchDiameter * multiplier),
                            minimumStub,
                            endClearance);
                        if (IsDeviceToMainFlowValid(route))
                            routes.Add(route);
                    }
                    catch (InvalidOperationException)
                    {
                        // The next compact-offset candidate may fit.
                    }
                }
            }
        }

        // Both plan sides are always eligible. Prefer the side facing the
        // main's high end; for a level main endpoint 0 is the deterministic
        // tie-break direction. If that side cannot host the selected family,
        // creation automatically continues with the opposite side.
        return routes
            .OrderByDescending(AutoSideScore)
            .ThenByDescending(route =>
                Math.Round(
                    Math.Min(route.MainParameter, 1.0 - route.MainParameter),
                    6))
            .ToList();
    }

    public static IReadOnlyList<DrainRoute> BuildCase02Candidates(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter,
        IReadOnlyList<double>? fittingAngles = null)
    {
        // Two elbows consume both ends of this short device-side diagonal.
        // Long socket/cast-iron fittings need about twice the clearance of the
        // former 1.35-D segment. Try the spacious route first and retain
        // smaller candidates for short layouts and compact UPVC fittings.
        double[] sourceCompactCandidates =
        [
            Math.Max(Mm(250), branchDiameter * 2.5),
            Math.Max(Mm(200), branchDiameter * 2.0),
            Math.Max(Mm(150), branchDiameter * 1.5),
            Math.Max(Mm(100), branchDiameter)
        ];
        double minimumStub = Math.Max(Mm(20), branchDiameter * 0.20);
        double endClearance = Math.Max(Mm(100), mainDiameter * 1.5);
        var routes = new List<DrainRoute>();

        foreach (DrainSide side in new[] { DrainSide.Left, DrainSide.Right })
        {
            foreach (double fittingAngle in fittingAngles ?? AutoFittingAngles)
            {
                const double planAngle = 45.0;
                foreach (double sourceCompact in sourceCompactCandidates.Distinct())
                {
                    foreach (double multiplier in Case02NearMainMultipliers)
                    {
                        try
                        {
                            double nearMainOffset = Math.Max(
                                Mm(250),
                                Math.Max(mainDiameter, branchDiameter) * multiplier);
                            DrainRoute route = BuildCase02(
                                mainStart,
                                mainEnd,
                                drainOrigin,
                                side,
                                settings.SlopePercent,
                                planAngle,
                                fittingAngle,
                                sourceCompact,
                                nearMainOffset,
                                minimumStub,
                                endClearance);
                            if (IsDeviceToMainFlowValid(route))
                                routes.Add(route);
                        }
                        catch (InvalidOperationException)
                        {
                            // Continue with the next side/offset candidate.
                        }
                    }
                }
            }
        }

        IEnumerable<DrainRoute> flowSafeRoutes = routes;
        if (MainHasPlanUphillDirection(mainStart, mainEnd))
            flowSafeRoutes = flowSafeRoutes.Where(route => AutoSideScore(route) > Epsilon);
        return flowSafeRoutes
            .OrderByDescending(AutoSideScore)
            .ThenByDescending(route => route.CompactOffset)
            .ThenByDescending(route =>
                Math.Round(Math.Min(route.MainParameter, 1.0 - route.MainParameter), 6))
            .ToList();
    }

    public static IReadOnlyList<DrainRoute> BuildCase03Candidates(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter)
    {
        double baseOffset = Math.Max(
            Mm(250),
            Math.Max(mainDiameter, branchDiameter) * 3.0);
        double minimumStub = Math.Max(Mm(75), branchDiameter * 0.75);
        double endClearance = Math.Max(Mm(100), mainDiameter * 1.5);
        var routes = new List<DrainRoute>();
        foreach (DrainSide side in new[] { DrainSide.Left, DrainSide.Right })
        {
            foreach (double fittingAngle in settings.JunctionAngles ?? AutoFittingAngles)
            {
                foreach (double multiplier in new[] { 1.0, 1.35, 1.7 })
                {
                    try
                    {
                        routes.Add(BuildCase03(
                            mainStart,
                            mainEnd,
                            drainOrigin,
                            side,
                            fittingAngle,
                            baseOffset * multiplier,
                            minimumStub,
                            endClearance,
                            branchDiameter,
                            mainDiameter));
                    }
                    catch (InvalidOperationException)
                    {
                        // Continue with the next Y angle, offset and direction.
                    }
                }
            }
        }

        IEnumerable<DrainRoute> flowSafeRoutes = routes;
        if (MainHasPlanUphillDirection(mainStart, mainEnd))
            flowSafeRoutes = flowSafeRoutes.Where(route => AutoSideScore(route) > Epsilon);
        return flowSafeRoutes
            .OrderByDescending(AutoSideScore)
            .ThenBy(route => route.CompactOffset)
            .ToList();
    }

    public static IReadOnlyList<DrainRoute> BuildCase04Candidates(
        XYZ mainInside,
        XYZ mainEndpoint,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter)
    {
        // Reserve the user-entered clear pipe plus estimated socket takeout for
        // both 45-degree elbows. BuildCase04 uses the vertical component of the
        // diagonal, so convert the desired center-to-center length through sin45.
        // Creation verifies the real post-trim pipe and retries larger candidates.
        double largestDiameter = Math.Max(branchDiameter, mainDiameter);
        double requestedMiddleLength = Mm(Math.Max(
            0.0,
            settings.Case04MiddlePipeLengthMm));
        double estimatedPairTakeout = Math.Max(Mm(150), branchDiameter * 2.5);
        double baseSourceCompact =
            (requestedMiddleLength + estimatedPairTakeout) * Math.Sin(Math.PI / 4.0);
        double baseDiagonalOffset = Math.Max(Mm(150), largestDiameter * 2.0);
        // Keep a clearly vertical drop below the floor drain before the compact
        // double-45 transition begins. A tiny stub made the diagonal appear to
        // start directly at (and tilt) the device connector.
        double minimumStub = Math.Max(Mm(20), branchDiameter * 0.20);
        var routes = new List<DrainRoute>();
        double slopePercent = Math.Abs(settings.SlopePercent);
        // UPVC socket elbows used by many Revit 2020 templates have much
        // shorter real takeout than the conservative cast-iron estimate.
        // Include shorter pre-trim candidates; creation still measures the
        // finished clear middle pipe and rejects anything below the requested
        // length, so the user's 200 mm requirement remains authoritative.
        foreach (double compactMultiplier in new[]
                 { 0.50, 0.65, 0.80, 1.0, 1.25, 1.50, 2.0, 2.50, 3.0 })
        {
            double sourceCompact = baseSourceCompact * compactMultiplier;
            foreach (double diagonalMultiplier in new[]
                     { 0.50, 0.75, 1.0, 1.25, 1.50, 2.0, 2.50, 3.0 })
            {
                double diagonalOffset = baseDiagonalOffset * diagonalMultiplier;
                foreach (double side in new[] { 1.0, -1.0 })
                {
                    try
                    {
                        routes.Add(BuildCase04(
                            mainInside,
                            mainEndpoint,
                            drainOrigin,
                            slopePercent,
                            sourceCompact,
                            diagonalOffset,
                            minimumStub,
                            side));
                    }
                    catch (InvalidOperationException)
                    {
                        // Try the mirrored Case 02 layout and compact lengths.
                    }
                }
            }
        }
        return routes
            .OrderBy(route => route.CompactOffset)
            .ThenBy(route => route.BranchPlanLength)
            .ThenByDescending(route => route.StubLength)
            .ToList();
    }

    public static IReadOnlyList<DrainRoute> BuildCase05Candidates(
        XYZ mainInside,
        XYZ mainEndpoint,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter)
    {
        // Case 05 is deliberately the same gravity route as Case 01. The only
        // difference is the final operation on the main: Case 01 inserts a Y;
        // Case 05 trims the surplus main tail and places a 45-degree elbow at
        // the automatically calculated Case-01 junction point.
        //
        // Try a realistic cast-iron elbow takeout first. The generic Case 01
        // order starts at 0.35 DN; for DN70/DN100 those very short candidates
        // make Revit create and roll back several failed fitting transactions
        // before it reaches a usable offset. Keep every fallback, but put the
        // commonly successful 1.5-2.0 DN offsets at the front.
        double[] fastCase05Offsets =
            [1.50, 2.00, 1.25, 2.50, 1.00, 3.00, 0.75, 4.00, 0.50, 0.35];
        static double MainElbowAngleError(DrainRoute route)
        {
            XYZ branchAway = (route.DiagonalEnd - route.WyePoint).Normalize();
            XYZ mainAway = (route.MainStart - route.WyePoint).Normalize();
            double cosine = Math.Abs(branchAway.DotProduct(mainAway));
            cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
            double angle = Math.Acos(cosine) * 180.0 / Math.PI;
            return Math.Abs(angle - 45.0);
        }

        return BuildCandidates(
                mainInside,
                mainEndpoint,
                drainOrigin,
                settings,
                branchDiameter,
                mainDiameter,
                fittingAngles: [45.0],
                offsetMultipliers: fastCase05Offsets)
            .Select(route => route with { CaseNumber = 5 })
            // BuildCandidates also includes the uncompensated plan-angle
            // fallback used by some Y families. Case 05 ends with an elbow,
            // so always try the route whose real 3D main angle is 45 degrees
            // before that fallback can trigger another Revit rollback.
            .OrderBy(MainElbowAngleError)
            .ToList();
    }

    public static IReadOnlyList<DrainRoute> BuildCase06Candidates(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSettings settings,
        double branchDiameter,
        double mainDiameter)
    {
        double minimumOffset = Mm(35);
        double minimumStub = Math.Max(Mm(20), branchDiameter * 0.20);
        double endClearance = Math.Max(Mm(50), mainDiameter * 0.75);
        var routes = new List<DrainRoute>();
        var failures = new List<string>();
        double rollMagnitude = Math.Abs(settings.YRollAngleDegrees);
        if (rollMagnitude <= 0.1 || rollMagnitude >= 89.9)
            throw new InvalidOperationException(
                "Case 06 Y Vertical Roll must be greater than 0.1° and less than 89.9°." );

        // The source-side straight pipe uses Branch Slope. After the extra
        // 45-degree elbow, the final leg is governed only by the fixed 45-degree
        // Y connector rolled around the main axis by the requested amount.
        foreach (double approachHand in new[] { 1.0, -1.0 })
        {
            foreach (double lateralHand in new[] { 1.0, -1.0 })
            {
                foreach (double sourceParallelHand in new[] { 1.0, -1.0 })
                {
                    foreach (double multiplier in new[] { 1.00, 1.25, 1.50, 1.75, 2.00 })
                    {
                        try
                        {
                            routes.AddRange(BuildCase06SplitSlopeRoutes(
                                mainStart,
                                mainEnd,
                                drainOrigin,
                                settings.SlopePercent,
                                rollMagnitude,
                                approachHand,
                                lateralHand,
                                sourceParallelHand,
                                minimumOffset * multiplier,
                                minimumStub,
                                endClearance));
                        }
                        catch (InvalidOperationException exception)
                        {
                            string approach = approachHand > 0
                                ? "device elbow roll A"
                                : "device elbow roll B";
                            string rollHand = lateralHand > 0 ? "roll side A" : "roll side B";
                            string sourceRun = sourceParallelHand > 0 ? "A along main" : "A opposite main";
                            failures.Add($"{approach}, {rollHand}, {sourceRun}: {exception.Message}");
                        }
                    }
                }
            }
        }
        if (routes.Count == 0)
            throw new InvalidOperationException(
                $"Case 06 cannot connect with Y vertical roll {rollMagnitude:0.###}° and " +
                $"source-side slope {settings.SlopePercent:0.###}%. " +
                string.Join(" ", failures.Distinct().TakeLast(4)));
        double preferredCompact = Math.Max(Mm(150), branchDiameter * 1.50);
        return routes
            .Where(IsDeviceToMainFlowValid)
            .OrderBy(route => Math.Abs(route.CompactOffset - preferredCompact))
            .ThenBy(route => route.BranchPlanLength)
            .ThenByDescending(route => route.StubLength)
            .Take(16)
            .ToList();
    }

    private static IReadOnlyList<DrainRoute> BuildCase06SplitSlopeRoutes(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        double sourceSlopePercent,
        double rollAngleDegrees,
        double approachHand,
        double lateralHand,
        double sourceParallelHand,
        double compactOffset,
        double minimumStubLength,
        double minimumEndClearance)
    {
        XYZ mainVector = mainEnd - mainStart;
        double mainLength = mainVector.GetLength();
        double mainPlanLength = Math.Sqrt(
            mainVector.X * mainVector.X + mainVector.Y * mainVector.Y);
        if (mainLength <= Epsilon || mainPlanLength <= Epsilon)
            throw new InvalidOperationException("Case 06 requires a non-vertical main.");

        double mainX = mainEnd.X - mainStart.X;
        double mainY = mainEnd.Y - mainStart.Y;
        double mainPlanX = mainX / mainPlanLength;
        double mainPlanY = mainY / mainPlanLength;
        double sourceAlong =
            (drainOrigin.X - mainStart.X) * mainPlanX +
            (drainOrigin.Y - mainStart.Y) * mainPlanY;
        XYZ sourceFoot = new(
            mainStart.X + mainPlanX * sourceAlong,
            mainStart.Y + mainPlanY * sourceAlong,
            drainOrigin.Z);
        double offsetX = drainOrigin.X - sourceFoot.X;
        double offsetY = drainOrigin.Y - sourceFoot.Y;
        double sourceDistanceToMain = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (sourceDistanceToMain <= Mm(100))
            throw new InvalidOperationException(
                "Case 06 needs the device to be offset from the main in plan.");

        double sourceSlope = sourceSlopePercent / 100.0;
        double sourcePlanFactor = 1.0 / Math.Sqrt(1.0 + sourceSlope * sourceSlope);
        XYZ sourceAxis = new(
            mainPlanX * sourceParallelHand * sourcePlanFactor,
            mainPlanY * sourceParallelHand * sourcePlanFactor,
            sourceSlope * sourcePlanFactor);

        // A runs from the elbow toward the device. routeA is the actual gravity
        // direction from device toward main. Build B exactly 45 degrees from A
        // in 3D; using the sloped main axis here produced 44.x-degree elbows.
        XYZ routeA = -sourceAxis;
        XYZ verticalPerpendicular =
            XYZ.BasisZ - routeA * XYZ.BasisZ.DotProduct(routeA);
        if (verticalPerpendicular.GetLength() <= Epsilon)
            throw new InvalidOperationException("Pipe A cannot define a vertical roll plane.");
        verticalPerpendicular = verticalPerpendicular.Normalize();
        XYZ lateralPerpendicular = routeA.CrossProduct(verticalPerpendicular).Normalize();
        double roll = rollAngleDegrees * Math.PI / 180.0;
        double fortyFive = Math.PI / 4.0;
        XYZ rolledDown =
            -verticalPerpendicular * Math.Sin(roll) +
            lateralPerpendicular * (lateralHand * Math.Cos(roll));
        XYZ routeB = (
            routeA * Math.Cos(fortyFive) +
            rolledDown * Math.Sin(fortyFive)).Normalize();
        if (routeB.Z >= -Epsilon)
            throw new InvalidOperationException("Pipe B does not fall from A toward the main.");
        XYZ yLegAxis = -routeB;
        double yLegPlan = Math.Sqrt(
            yLegAxis.X * yLegAxis.X + yLegAxis.Y * yLegAxis.Y);
        if (yLegPlan <= Epsilon)
            throw new InvalidOperationException("Pipe B has no usable plan direction.");

        // Find an out-of-plane intermediate direction that is exactly 45
        // degrees from both the vertical stub and sloped pipe A. This is the
        // geometric condition for two fixed 45-degree elbows back-to-back.
        XYZ verticalRoute = -XYZ.BasisZ;
        double endDirectionDot = verticalRoute.DotProduct(routeA);
        double elbowCosine = Math.Cos(fortyFive);
        if (endDirectionDot <= -1.0 + Epsilon)
            throw new InvalidOperationException("The device stub and pipe A directions are incompatible.");
        XYZ approachBase =
            (verticalRoute + routeA) * (elbowCosine / (1.0 + endDirectionDot));
        XYZ approachNormal = verticalRoute.CrossProduct(routeA);
        if (approachNormal.GetLength() <= Epsilon)
            throw new InvalidOperationException("The device elbow roll axis is undefined.");
        approachNormal = approachNormal.Normalize();
        double normalSquared = 1.0 - approachBase.DotProduct(approachBase);
        if (normalSquared < -1e-8)
            throw new InvalidOperationException(
                "Two fixed 45 degree elbows cannot bridge the vertical stub and sloped pipe A.");
        XYZ approachAxis = (
            approachBase +
            approachNormal * (approachHand * Math.Sqrt(Math.Max(0.0, normalSquared)))).Normalize();
        if (approachAxis.Z >= -Epsilon)
            throw new InvalidOperationException("The device approach does not fall away from the drain.");
        double approachLength = compactOffset / -approachAxis.Z;
        XYZ approachPlanEnd = drainOrigin + approachAxis * approachLength;

        double minimumParameter = minimumEndClearance / mainLength;
        if (minimumParameter >= 0.49)
            throw new InvalidOperationException("The selected main is too short for Case 06.");

        var routes = new List<DrainRoute>();
        int planIntersections = 0;
        int usableLengths = 0;
        double bestVerticalClearance = double.NegativeInfinity;
        for (int index = 0; index <= 120; index++)
        {
            double mainParameter = minimumParameter +
                (1.0 - 2.0 * minimumParameter) * index / 120.0;
            XYZ wyePoint = mainStart + mainVector * mainParameter;
            double relativeX = approachPlanEnd.X - wyePoint.X;
            double relativeY = approachPlanEnd.Y - wyePoint.Y;
            double denominator =
                yLegAxis.X * sourceAxis.Y -
                yLegAxis.Y * sourceAxis.X;
            if (Math.Abs(denominator) <= Epsilon)
                continue;
            double yLegLength =
                (relativeX * sourceAxis.Y - relativeY * sourceAxis.X) /
                denominator;
            double sourceSideLength =
                (yLegAxis.X * relativeY - yLegAxis.Y * relativeX) /
                denominator;
            if (yLegLength > 0 && sourceSideLength > 0)
                planIntersections++;
            if (yLegLength <= Math.Max(Mm(50), compactOffset * 0.50) ||
                sourceSideLength <= Math.Max(Mm(50), compactOffset * 0.50))
                continue;
            usableLengths++;

            XYZ nearMainElbow = wyePoint + yLegAxis * yLegLength;
            XYZ diagonalEnd = nearMainElbow + sourceAxis * sourceSideLength;
            XYZ calculatedStubEnd = diagonalEnd - approachAxis * approachLength;
            double stubEndZ = calculatedStubEnd.Z;
            bestVerticalClearance = Math.Max(
                bestVerticalClearance,
                drainOrigin.Z - stubEndZ);
            if (drainOrigin.Z - stubEndZ < minimumStubLength)
                continue;
            XYZ stubEnd = new(drainOrigin.X, drainOrigin.Y, stubEndZ);
            XYZ mainPlanAxis = new(mainPlanX, mainPlanY, 0);
            XYZ yLegPlanAxis = new(
                yLegAxis.X / yLegPlan,
                yLegAxis.Y / yLegPlan,
                0);
            double planDot = Math.Max(
                -1.0,
                Math.Min(1.0, Math.Abs(mainPlanAxis.DotProduct(yLegPlanAxis))));
            double planAngle = Math.Acos(planDot) * 180.0 / Math.PI;
            double cross =
                mainPlanAxis.X * yLegPlanAxis.Y -
                mainPlanAxis.Y * yLegPlanAxis.X;
            DrainSide side = cross >= 0 ? DrainSide.Left : DrainSide.Right;
            routes.Add(new DrainRoute(
                drainOrigin,
                stubEnd,
                diagonalEnd,
                wyePoint,
                mainStart,
                mainEnd,
                compactOffset,
                yLegLength * yLegPlan +
                    sourceSideLength * sourcePlanFactor,
                mainParameter,
                planAngle,
                45.0,
                sourceSlopePercent,
                side,
                nearMainElbow,
                6));
        }

        if (routes.Count == 0)
        {
            string detail = planIntersections == 0
                ? "A and B do not intersect in the forward direction within this main segment."
                : usableLengths == 0
                    ? "A or B is shorter than the compact elbow/fitting takeout."
                    : double.IsNegativeInfinity(bestVerticalClearance)
                        ? "No candidate reached the vertical-clearance check."
                        : $"Best available vertical clearance is {ToMm(bestVerticalClearance):0.#} mm; " +
                          $"the route requires at least {ToMm(minimumStubLength):0.#} mm below the device.";
            throw new InvalidOperationException(
                "No point on the selected main fits the parallel-A / Y-rolled-B route. " + detail);
        }
        return routes;
    }

    private static double AutoSideScore(DrainRoute route)
    {
        double dx = route.MainEnd.X - route.MainStart.X;
        double dy = route.MainEnd.Y - route.MainStart.Y;
        double dz = route.MainEnd.Z - route.MainStart.Z;
        if (Math.Abs(dz) <= Mm(0.5))
        {
            dx = -dx;
            dy = -dy;
        }
        else if (dz < 0)
        {
            dx = -dx;
            dy = -dy;
        }

        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= Epsilon) return 0;
        double toDeviceX = route.DrainOrigin.X - route.WyePoint.X;
        double toDeviceY = route.DrainOrigin.Y - route.WyePoint.Y;
        return (toDeviceX * dx + toDeviceY * dy) / length;
    }

    public static bool MainHasPlanUphillDirection(XYZ mainStart, XYZ mainEnd) =>
        Math.Abs(mainEnd.Z - mainStart.Z) > Mm(0.5);

    public static double CompensatedPlanAngle(
        XYZ mainStart,
        XYZ mainEnd,
        double branchSlopePercent,
        double fittingAngleDegrees,
        DrainSide side)
    {
        double dx = mainEnd.X - mainStart.X;
        double dy = mainEnd.Y - mainStart.Y;
        double mainPlanLength = Math.Sqrt(dx * dx + dy * dy);
        if (mainPlanLength <= Epsilon)
            throw new InvalidOperationException("Case 01 requires a non-vertical main.");

        // The acute run leg used by a Y changes with the selected plan side.
        // Keep the sign of the main slope in that direction. Using Abs here
        // produced 2.0185% after fitting insertion on a 2% sloped main.
        double runDirectionSign = side == DrainSide.Left ? 1.0 : -1.0;
        double mainUphillSlope =
            (mainEnd.Z - mainStart.Z) / mainPlanLength * runDirectionSign;
        double branchUphillSlope = branchSlopePercent / 100.0;
        double fittingCosine = Math.Cos(fittingAngleDegrees * Math.PI / 180.0);

        // Dot product of the two uphill 3D unit vectors:
        // cos(theta) = (cos(alpha) + branchSlope * mainSlope)
        //              / sqrt((1 + branchSlope^2) * (1 + mainSlope^2))
        double planCosine =
            fittingCosine *
            Math.Sqrt(
                (1.0 + branchUphillSlope * branchUphillSlope) *
                (1.0 + mainUphillSlope * mainUphillSlope))
            - branchUphillSlope * mainUphillSlope;

        if (planCosine < -1.0 - Epsilon || planCosine > 1.0 + Epsilon)
            throw new InvalidOperationException(
                $"A {fittingAngleDegrees:0.#} degree Y cannot satisfy the selected main and branch slopes.");

        planCosine = Math.Max(-1.0, Math.Min(1.0, planCosine));
        return Math.Acos(planCosine) * 180.0 / Math.PI;
    }

    public static bool IsDeviceToMainFlowValid(DrainRoute route)
    {
        // Gravity route is authored from the device to the main. Every route
        // must therefore lose elevation toward the main.
        if (route.DrainOrigin.Z <= route.StubEnd.Z + Epsilon ||
            route.StubEnd.Z <= route.DiagonalEnd.Z + Epsilon ||
            route.DiagonalEnd.Z <= route.WyePoint.Z + Epsilon)
            return false;

        // Left/right is a plan-routing decision, not a gravity validation.
        // Both plan sides remain eligible. Gravity is controlled only by the
        // monotonically rising elevations from main to device above.
        return true;
    }

    private static DrainRoute Build(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSide side,
        double slopePercent,
        double planAngleDegrees,
        double fittingAngleDegrees,
        double compactOffset,
        double minimumStubLength,
        double minimumEndClearance)
    {
        double dx = mainEnd.X - mainStart.X;
        double dy = mainEnd.Y - mainStart.Y;
        double mainPlanLength = Math.Sqrt(dx * dx + dy * dy);
        if (mainPlanLength <= Epsilon)
            throw new InvalidOperationException("Case 01 requires a non-vertical main.");

        double ux = dx / mainPlanLength;
        double uy = dy / mainPlanLength;
        double nx = -uy;
        double ny = ux;
        double tangent = Math.Tan(planAngleDegrees * Math.PI / 180.0);
        double diagonalX = drainOrigin.X;
        double diagonalY = drainOrigin.Y;
        double wyeX = 0;
        double wyeY = 0;

        for (int iteration = 0; iteration < 4; iteration++)
        {
            double foot = (diagonalX - mainStart.X) * ux + (diagonalY - mainStart.Y) * uy;
            double footX = mainStart.X + ux * foot;
            double footY = mainStart.Y + uy * foot;
            double perpendicular = Math.Abs((diagonalX - footX) * nx + (diagonalY - footY) * ny);
            if (perpendicular <= Epsilon)
                throw new InvalidOperationException("Drain is too close to the main centerline.");

            double alongOffset = perpendicular / Math.Abs(tangent);
            double factor = side == DrainSide.Left ? -1.0 : 1.0;
            wyeX = footX + ux * factor * alongOffset;
            wyeY = footY + uy * factor * alongOffset;

            double vx = wyeX - drainOrigin.X;
            double vy = wyeY - drainOrigin.Y;
            double distance = Math.Sqrt(vx * vx + vy * vy);
            if (distance <= compactOffset + Epsilon)
                throw new InvalidOperationException("Insufficient room for the double-45 pair.");
            diagonalX = drainOrigin.X + vx / distance * compactOffset;
            diagonalY = drainOrigin.Y + vy / distance * compactOffset;
        }

        double mainParameter =
            ((wyeX - mainStart.X) * ux + (wyeY - mainStart.Y) * uy) / mainPlanLength;
        if (mainParameter <= 0 || mainParameter >= 1)
            throw new InvalidOperationException("Auto junction falls outside the selected main.");
        if (Math.Min(mainParameter, 1 - mainParameter) * mainPlanLength < minimumEndClearance)
            throw new InvalidOperationException("Auto junction is too close to a main-pipe end.");

        double wyeZ = mainStart.Z + (mainEnd.Z - mainStart.Z) * mainParameter;
        double branchDx = diagonalX - wyeX;
        double branchDy = diagonalY - wyeY;
        double branchPlanLength = Math.Sqrt(branchDx * branchDx + branchDy * branchDy);
        double diagonalZ = wyeZ + branchPlanLength * slopePercent / 100.0;
        double stubEndZ = diagonalZ + compactOffset;
        if (drainOrigin.Z - stubEndZ < minimumStubLength)
            throw new InvalidOperationException("Not enough vertical drop below the drain.");

        return new DrainRoute(
            drainOrigin,
            new XYZ(drainOrigin.X, drainOrigin.Y, stubEndZ),
            new XYZ(diagonalX, diagonalY, diagonalZ),
            new XYZ(wyeX, wyeY, wyeZ),
            mainStart,
            mainEnd,
            compactOffset,
            branchPlanLength,
            mainParameter,
            planAngleDegrees,
            fittingAngleDegrees,
            slopePercent,
            side);
    }

    private static DrainRoute BuildCase02(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSide side,
        double slopePercent,
        double planAngleDegrees,
        double fittingAngleDegrees,
        double sourceCompact,
        double nearMainOffset,
        double minimumStubLength,
        double minimumEndClearance)
    {
        double dx = mainEnd.X - mainStart.X;
        double dy = mainEnd.Y - mainStart.Y;
        double mainPlanLength = Math.Sqrt(dx * dx + dy * dy);
        if (mainPlanLength <= Epsilon)
            throw new InvalidOperationException("Case 02 requires a non-vertical main.");

        double ux = dx / mainPlanLength;
        double uy = dy / mainPlanLength;
        double nx = -uy;
        double ny = ux;
        double footDistance =
            (drainOrigin.X - mainStart.X) * ux +
            (drainOrigin.Y - mainStart.Y) * uy;
        double footX = mainStart.X + ux * footDistance;
        double footY = mainStart.Y + uy * footDistance;
        double signedPerpendicular =
            (drainOrigin.X - footX) * nx +
            (drainOrigin.Y - footY) * ny;
        double perpendicular = Math.Abs(signedPerpendicular);
        if (perpendicular <= nearMainOffset + sourceCompact + Mm(100))
            throw new InvalidOperationException(
                "Case 02 has insufficient straight length between the device and main.");

        double normalSign = signedPerpendicular >= 0 ? 1.0 : -1.0;
        double elbowX = footX + nx * normalSign * nearMainOffset;
        double elbowY = footY + ny * normalSign * nearMainOffset;
        double tangent = Math.Tan(planAngleDegrees * Math.PI / 180.0);
        if (Math.Abs(tangent) <= Epsilon)
            throw new InvalidOperationException("Case 02 Y angle is invalid.");
        double alongOffset = nearMainOffset / Math.Abs(tangent);
        double sideFactor = side == DrainSide.Left ? -1.0 : 1.0;
        double wyeX = footX + ux * sideFactor * alongOffset;
        double wyeY = footY + uy * sideFactor * alongOffset;
        double mainParameter =
            ((wyeX - mainStart.X) * ux + (wyeY - mainStart.Y) * uy) /
            mainPlanLength;
        if (mainParameter <= 0 || mainParameter >= 1)
            throw new InvalidOperationException("Case 02 junction falls outside the selected main.");
        if (Math.Min(mainParameter, 1.0 - mainParameter) * mainPlanLength < minimumEndClearance)
            throw new InvalidOperationException("Case 02 junction is too close to a main end.");

        XYZ towardElbow = new(elbowX - drainOrigin.X, elbowY - drainOrigin.Y, 0);
        double deviceToElbow = towardElbow.GetLength();
        if (deviceToElbow <= sourceCompact + Mm(50))
            throw new InvalidOperationException("Case 02 straight branch is too short.");
        XYZ planDirection = towardElbow.Normalize();
        double diagonalEndX = drainOrigin.X + planDirection.X * sourceCompact;
        double diagonalEndY = drainOrigin.Y + planDirection.Y * sourceCompact;

        double wyeZ = mainStart.Z + (mainEnd.Z - mainStart.Z) * mainParameter;
        double nearDiagonalLength = Math.Sqrt(
            (elbowX - wyeX) * (elbowX - wyeX) +
            (elbowY - wyeY) * (elbowY - wyeY));
        double longBranchLength = Math.Sqrt(
            (diagonalEndX - elbowX) * (diagonalEndX - elbowX) +
            (diagonalEndY - elbowY) * (diagonalEndY - elbowY));
        double elbowZ = wyeZ + nearDiagonalLength * slopePercent / 100.0;
        double diagonalEndZ = elbowZ + longBranchLength * slopePercent / 100.0;
        double stubEndZ = diagonalEndZ + sourceCompact;
        if (drainOrigin.Z - stubEndZ < minimumStubLength)
            throw new InvalidOperationException(
                "Case 02 does not have enough vertical drop below the drain.");

        return new DrainRoute(
            drainOrigin,
            new XYZ(drainOrigin.X, drainOrigin.Y, stubEndZ),
            new XYZ(diagonalEndX, diagonalEndY, diagonalEndZ),
            new XYZ(wyeX, wyeY, wyeZ),
            mainStart,
            mainEnd,
            sourceCompact,
            longBranchLength + nearDiagonalLength,
            mainParameter,
            planAngleDegrees,
            fittingAngleDegrees,
            slopePercent,
            side,
            new XYZ(elbowX, elbowY, elbowZ),
            2);
    }

    private static DrainRoute BuildCase03(
        XYZ mainStart,
        XYZ mainEnd,
        XYZ drainOrigin,
        DrainSide side,
        double fittingAngleDegrees,
        double alongMainOffset,
        double minimumStubLength,
        double minimumEndClearance,
        double branchDiameter,
        double mainDiameter)
    {
        double dx = mainEnd.X - mainStart.X;
        double dy = mainEnd.Y - mainStart.Y;
        double mainPlanLength = Math.Sqrt(dx * dx + dy * dy);
        if (mainPlanLength <= Epsilon)
            throw new InvalidOperationException("Case 03 requires a non-vertical main.");
        double ux = dx / mainPlanLength;
        double uy = dy / mainPlanLength;
        double nx = -uy;
        double ny = ux;
        double footDistance =
            (drainOrigin.X - mainStart.X) * ux +
            (drainOrigin.Y - mainStart.Y) * uy;
        double footX = mainStart.X + ux * footDistance;
        double footY = mainStart.Y + uy * footDistance;
        double perpendicular = Math.Abs(
            (drainOrigin.X - footX) * nx +
            (drainOrigin.Y - footY) * ny);
        double alignmentTolerance = Math.Max(
            Mm(100),
            Math.Max(branchDiameter, mainDiameter));
        if (perpendicular > alignmentTolerance)
            throw new InvalidOperationException(
                "Case 03 requires the device connector to be directly above the main in plan.");

        double factor = side == DrainSide.Left ? -1.0 : 1.0;
        double wyeDistance = footDistance + factor * alongMainOffset;
        double mainParameter = wyeDistance / mainPlanLength;
        if (mainParameter <= 0 || mainParameter >= 1)
            throw new InvalidOperationException("Case 03 junction falls outside the main.");
        if (Math.Min(mainParameter, 1.0 - mainParameter) * mainPlanLength < minimumEndClearance)
            throw new InvalidOperationException("Case 03 junction is too close to a main end.");

        double wyeX = mainStart.X + ux * wyeDistance;
        double wyeY = mainStart.Y + uy * wyeDistance;
        double wyeZ = mainStart.Z + (mainEnd.Z - mainStart.Z) * mainParameter;

        // The branch runs from the Y back toward the device projection. Its
        // vertical-plane angle follows the real selected Y connector angle, so
        // different project families do not get forced into a nominal 45° shape.
        double mainSlope = (mainEnd.Z - mainStart.Z) / mainPlanLength;
        double directionAlongMain = -factor;
        double localMainAngle = Math.Atan(mainSlope * directionAlongMain);
        double branchAngle = localMainAngle +
            45.0 * Math.PI / 180.0;
        if (branchAngle <= 5.0 * Math.PI / 180.0 ||
            branchAngle >= 85.0 * Math.PI / 180.0)
            throw new InvalidOperationException(
                $"Case 03 cannot form a {fittingAngleDegrees:0.###} degree vertical-plane Y branch on this main slope.");
        double branchRise = alongMainOffset * Math.Tan(branchAngle);
        double elbowZ = wyeZ + branchRise;
        if (drainOrigin.Z - elbowZ < minimumStubLength)
            throw new InvalidOperationException(
                "Case 03 does not have enough vertical clearance for the standing pipe and routed elbow.");

        XYZ elbowPoint = new(footX, footY, elbowZ);
        return new DrainRoute(
            drainOrigin,
            elbowPoint,
            elbowPoint,
            new XYZ(wyeX, wyeY, wyeZ),
            mainStart,
            mainEnd,
            alongMainOffset,
            alongMainOffset,
            mainParameter,
            0.0,
            fittingAngleDegrees,
            Math.Tan(branchAngle) * 100.0,
            side,
            null,
            3);
    }

    private static DrainRoute BuildCase04(
        XYZ mainInside,
        XYZ mainEndpoint,
        XYZ drainOrigin,
        double slopePercent,
        double sourceCompact,
        double diagonalOffset,
        double minimumStubLength,
        double side)
    {
        double dx = mainEndpoint.X - mainInside.X;
        double dy = mainEndpoint.Y - mainInside.Y;
        double planLength = Math.Sqrt(dx * dx + dy * dy);
        if (planLength <= Epsilon)
            throw new InvalidOperationException("Case 04 requires a non-vertical main endpoint.");
        double ux = dx / planLength;
        double uy = dy / planLength;
        double mainSlope = (mainEndpoint.Z - mainInside.Z) / planLength;
        double branchSlope = Math.Abs(slopePercent) / 100.0;
        double fittingCosine = Math.Cos(Math.PI / 4.0);

        static double ExactPlanAngle(
            double firstSlope,
            double secondSlope,
            double targetCosine)
        {
            double planCosine = targetCosine * Math.Sqrt(
                (1.0 + firstSlope * firstSlope) *
                (1.0 + secondSlope * secondSlope)) -
                firstSlope * secondSlope;
            if (planCosine is < -1.0 or > 1.0)
                throw new InvalidOperationException(
                    "The selected slopes cannot form a fixed 45 degree elbow.");
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, planCosine)));
        }

        static (double X, double Y) Rotate(
            double x,
            double y,
            double angle)
        {
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            return (x * cosine - y * sine, x * sine + y * cosine);
        }

        double firstTurn = ExactPlanAngle(mainSlope, branchSlope, fittingCosine);
        double secondTurn = ExactPlanAngle(branchSlope, branchSlope, fittingCosine);
        (double vx, double vy) = Rotate(ux, uy, side * firstTurn);
        (double wx, double wy) = Rotate(ux, uy, side * (firstTurn + secondTurn));

        // Match Case 02 at the device: first drop vertically, then use a 45-degree
        // diagonal in the vertical plane of the following straight branch. Do
        // not roll this diagonal sideways to compensate for the small pipe slope;
        // that roll made the Case 04 drop look skewed in plan.
        double inverseRootTwo = 1.0 / Math.Sqrt(2.0);
        XYZ approachAxis = new(
            -wx * inverseRootTwo,
            -wy * inverseRootTwo,
            -inverseRootTwo);
        double approachLength = sourceCompact / -approachAxis.Z;
        XYZ sourcePairPlan = drainOrigin + approachAxis * approachLength;

        double qx = sourcePairPlan.X - mainEndpoint.X;
        double qy = sourcePairPlan.Y - mainEndpoint.Y;
        double diagonalPlan = diagonalOffset / Math.Abs(Math.Sin(firstTurn));
        double straightPlanNumerator =
            (-uy) * (qx - vx * diagonalPlan) +
            ux * (qy - vy * diagonalPlan);
        double straightPlanDenominator = (-uy) * wx + ux * wy;
        if (Math.Abs(straightPlanDenominator) <= Epsilon)
            throw new InvalidOperationException(
                "The Case 04 final run is parallel to the main in plan.");
        double straightPlan = straightPlanNumerator / straightPlanDenominator;
        double extensionPlan =
            ux * (qx - vx * diagonalPlan - wx * straightPlan) +
            uy * (qy - vy * diagonalPlan - wy * straightPlan);
        if (extensionPlan <= Mm(20) || straightPlan <= Mm(20))
            throw new InvalidOperationException(
                "The selected endpoint does not leave enough length for the two 45 degree turns.");

        XYZ extensionElbow = new(
            mainEndpoint.X + ux * extensionPlan,
            mainEndpoint.Y + uy * extensionPlan,
            mainEndpoint.Z + mainSlope * extensionPlan);
        XYZ secondElbow = new(
            extensionElbow.X + vx * diagonalPlan,
            extensionElbow.Y + vy * diagonalPlan,
            extensionElbow.Z + branchSlope * diagonalPlan);
        XYZ sourcePairEnd = new(
            sourcePairPlan.X,
            sourcePairPlan.Y,
            secondElbow.Z + branchSlope * straightPlan);
        XYZ stubEnd = sourcePairEnd - approachAxis * approachLength;
        if (drainOrigin.Z - stubEnd.Z < minimumStubLength)
            throw new InvalidOperationException(
                "The device is not high enough for the compact two-45 connection.");

        DrainSide drainSide = side > 0 ? DrainSide.Left : DrainSide.Right;
        return new DrainRoute(
            drainOrigin,
            new XYZ(drainOrigin.X, drainOrigin.Y, stubEnd.Z),
            sourcePairEnd,
            mainEndpoint,
            mainInside,
            mainEndpoint,
            sourceCompact,
            extensionPlan + diagonalPlan + straightPlan,
            1.0,
            firstTurn * 180.0 / Math.PI,
            45.0,
            Math.Abs(slopePercent),
            drainSide,
            secondElbow,
            4,
            extensionElbow);
    }

    public static double Mm(double value) =>
        UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    public static double ToMm(double value) =>
        UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters);
}
