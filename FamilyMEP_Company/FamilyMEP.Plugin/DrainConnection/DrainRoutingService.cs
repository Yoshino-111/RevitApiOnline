using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace FamilyMEP.Plugin.DrainConnection;

internal static class DrainRoutingService
{
    public static IReadOnlyList<DrainRoute> Preview(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCandidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No flow-safe automatic route fits. The branch must rise toward the device and the wye must face the high end of the main. Reduce slope or select another main.");
        return routes;
    }

    public static IReadOnlyList<DrainRoute> PreviewCase02(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase02Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 02 route fits. This case needs a straight branch, one 45 degree elbow near the main, and enough clearance for the diagonal Y leg.");
        return routes;
    }

    public static IReadOnlyList<DrainRoute> PreviewCase03(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase03Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 03 route fits. The device must be above the main in plan and have enough vertical clearance for one standing pipe, one 45 degree elbow, and the diagonal Y leg.");
        return routes;
    }

    public static IReadOnlyList<DrainRoute> PreviewCase04(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        context = Case04MainSizedContext(context);
        (XYZ inside, XYZ endpoint) = ResolveOpenMainEndpoint(
            context,
            settings.TargetPoint);
        settings = settings with
        {
            SlopePercent = EndpointSlopePercent(inside, endpoint),
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase04Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 04 route fits this open endpoint. Check that the device is high enough above the main for the compact 45 degree fittings.");
        return routes;
    }

    public static IReadOnlyList<DrainRoute> PreviewCase05(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        return BuildCase05PipeOnlyRoutes(context, settings);
    }

    public static IReadOnlyList<DrainRoute> PreviewCase06(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        context = Case04MainSizedContext(context);
        (XYZ inside, XYZ endpoint) = ResolveOpenMainEndpoint(
            context,
            settings.TargetPoint);
        settings = settings with
        {
            SlopePercent = Math.Abs(EndpointSlopePercent(inside, endpoint)),
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase06Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 06 endpoint elbow route fits. Pick near an open main end with enough outward length for the 45 degree connection.");
        return routes;
    }

    public static DrainOperationResult Create(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCandidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe device-to-main route fits the selected drain and main.");

        foreach (DrainRoute route in routes)
        {
            string label =
                $"branch {route.PlanAngleDegrees:0.###} deg / " +
                $"slope {settings.SlopePercent:0.###}%";
            using var attempt = new Transaction(
                document,
                "Case 01 create device-to-main branch");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"{label}: {failure}"));
            try
            {
                DrainOperationResult result = CreateBranchOnly(
                    document,
                    context,
                    route);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add($"{label}: Revit rolled back the branch candidate.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    result,
                    failures);
            }
            catch (Exception exception)
            {
                failures.Add($"{label}: {exception.Message}");
                if (attempt.GetStatus() == TransactionStatus.Started)
                    attempt.RollBack();
            }
        }

        string detailText = string.Join(
            Environment.NewLine,
            failures.Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .TakeLast(8));
        throw new InvalidOperationException(
            "Revit could not create the device-to-main branch with two 45 degree elbows." +
            (detailText.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + detailText));
    }

    public static DrainOperationResult CreateCase02(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase02Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe Case 02 route fits the selected drain and main.");

        foreach (DrainRoute route in routes)
        {
            using var attempt = new Transaction(
                document,
                "Case 02 create elbow-offset branch");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 02: {failure}"));
            try
            {
                DrainOperationResult stage1 = CreateSplitSlopeBranch(
                    document,
                    context,
                    route);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add("Case 02: Revit rolled back the branch candidate.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    stage1,
                    failures);
            }
            catch (Exception exception)
            {
                failures.Add($"Case 02: {exception.Message}");
                if (attempt.GetStatus() == TransactionStatus.Started)
                    attempt.RollBack();
            }
        }

        string details = string.Join(
            Environment.NewLine,
            failures.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct()
                .TakeLast(8));
        throw new InvalidOperationException(
            "Revit could not create the Case 02 branch." +
            (details.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + details));
    }

    public static DrainOperationResult CreateCase03(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase03Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 03 route fits the selected drain and main.");

        foreach (DrainRoute route in routes)
        {
            using var attempt = new Transaction(
                document,
                "Case 03 create vertical-plane branch");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 03: {failure}"));
            try
            {
                DrainOperationResult stage1 = CreateStandingDiagonalBranch(
                    document,
                    context,
                    route);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add("Case 03: Revit rolled back the branch candidate.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    stage1,
                    failures);
            }
            catch (Exception exception)
            {
                failures.Add($"Case 03: {exception.Message}");
                if (attempt.GetStatus() == TransactionStatus.Started)
                    attempt.RollBack();
            }
        }

        string details = string.Join(
            Environment.NewLine,
            failures.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct()
                .TakeLast(8));
        throw new InvalidOperationException(
            "Revit could not create the Case 03 vertical-plane connection." +
            (details.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + details));
    }

    public static DrainOperationResult CreateCase04(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        context = Case04MainSizedContext(context);
        (XYZ inside, XYZ endpoint) = ResolveOpenMainEndpoint(
            context,
            settings.TargetPoint);
        settings = settings with
        {
            SlopePercent = EndpointSlopePercent(inside, endpoint),
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase04Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 04 endpoint route fits. The device needs enough vertical clearance above the main for the compact 45 degree fittings.");

        var failures = new List<string>();
        foreach (DrainRoute route in routes)
        {
            using var attempt = new Transaction(
                document,
                "Case 04 connect to open main endpoint");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 04: {failure}"));
            try
            {
                DrainOperationResult result = CreateBranchOnlyCase04(
                    document,
                    context,
                    route);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add("Case 04: Revit rolled back the endpoint connection.");
                    continue;
                }
                return result;
            }
            catch (Exception exception)
            {
                failures.Add($"Case 04: {exception.Message}");
                if (attempt.GetStatus() == TransactionStatus.Started)
                    attempt.RollBack();
            }
        }

        string details = string.Join(
            Environment.NewLine,
            failures.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct()
                .TakeLast(8));
        throw new InvalidOperationException(
            "Revit could not create the Case 04 endpoint connection." +
            (details.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + details));
    }

    public static DrainOperationResult CreateCase05(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        IReadOnlyList<DrainRoute> routes = BuildCase05PipeOnlyRoutes(
            context,
            settings);
        var failures = new List<string>();
        foreach (DrainRoute route in routes)
        {
            using var stage1Transaction = new Transaction(
                document,
                "Case 05 create gravity branch before routed Y");
            stage1Transaction.Start();
            ConfigureFailureHandling(
                stage1Transaction,
                message => failures.Add($"Offset {DrainGeometry.ToMm(route.CompactOffset):0} mm: {message}"));
            try
            {
                DrainOperationResult stage1 = CreateSplitSlopeBranch(
                    document,
                    context,
                    route);
                if (stage1Transaction.Commit() != TransactionStatus.Committed)
                {
                    failures.Add(
                        $"Offset {DrainGeometry.ToMm(route.CompactOffset):0} mm: Revit rolled back the Case 05 branch.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    stage1,
                    failures);
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"Offset {DrainGeometry.ToMm(route.CompactOffset):0} mm: {exception.Message}");
                if (stage1Transaction.GetStatus() == TransactionStatus.Started)
                    stage1Transaction.RollBack();
            }
        }

        throw new InvalidOperationException(
            "Revit could not create the Case 05 rolled branch and routed Y at the selected main point." +
            Environment.NewLine + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                failures.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct()
                    .TakeLast(8)));
    }

    public static DrainOperationResult CreateCase06(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        context = Case04MainSizedContext(context);
        (XYZ inside, XYZ endpoint) = ResolveOpenMainEndpoint(
            context,
            settings.TargetPoint);
        settings = settings with
        {
            SlopePercent = Math.Abs(EndpointSlopePercent(inside, endpoint)),
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase06Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe Case 06 route fits the selected drain and open main end.");

        var failures = new List<string>();
        foreach (DrainRoute route in routes)
        {
            using var attempt = new Transaction(
                document,
                "Case 06 connect endpoint with 45 degree elbow");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 06: {failure}"));
            try
            {
                Pipe target = document.GetElement(context.Target.Id) as Pipe
                    ?? throw new InvalidOperationException(
                        "The selected Case 06 main is no longer available.");
                DrainOperationResult result = CreateCase06EndpointElbow(
                    document,
                    context,
                    route,
                    target,
                    endpoint);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add("Case 06: Revit rolled back the endpoint elbow candidate.");
                    continue;
                }
                return result;
            }
            catch (Exception exception)
            {
                failures.Add($"Case 06: {exception.Message}");
                if (attempt.GetStatus() == TransactionStatus.Started)
                    attempt.RollBack();
            }
        }

        string details = string.Join(
            Environment.NewLine,
            failures.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct()
                .TakeLast(8));
        throw new InvalidOperationException(
            "Revit could not create the Case 06 endpoint 45 degree elbow connection." +
            (details.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + details));
    }

    private static DrainOperationResult CreateCase06EndpointElbow(
        Document document,
        Context context,
        DrainRoute route,
        Pipe target,
        XYZ oldEndpoint)
    {
        var ids = new List<ElementId>();
        Element source = document.GetElement(context.Source.Id)
            ?? throw new InvalidOperationException(
                "The Case 06 drain is no longer available.");

        // Stop the existing main exactly at the calculated Case-01-style
        // junction point. There is no continuation beyond this point and no Y.
        ExtendOpenMainEndpoint(target, oldEndpoint, route.WyePoint);
        document.Regenerate();

        ApproachPipes approach = CreateApproachPipes(
            document,
            context,
            route,
            ids);
        Pipe branch = CreatePipe(
            document,
            context,
            route.DiagonalEnd,
            route.WyePoint);
        ids.Add(branch.Id);
        document.Regenerate();

        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        FamilyInstance? sourceTransition = ConnectCase04Source(
            document,
            sourceConnector,
            approach.Stub,
            route);
        FamilyInstance source45A = CreateCase04Elbow(
            document,
            approach.Stub,
            approach.Diagonal,
            route.StubEnd,
            "upper device-side 45 degree elbow");
        FamilyInstance source45B = CreateCase04Elbow(
            document,
            approach.Diagonal,
            branch,
            route.DiagonalEnd,
            "lower device-side 45 degree elbow");
        FamilyInstance main45 = CreateCase04Elbow(
            document,
            branch,
            target,
            route.WyePoint,
            "main endpoint 45 degree elbow");
        ids.AddRange([source45A.Id, source45B.Id, main45.Id]);
        if (sourceTransition is not null)
            ids.Add(sourceTransition.Id);
        document.Regenerate();

        return new DrainOperationResult(
            true,
            false,
            "Case 06 created: Case-01-style gravity branch connected directly " +
            "to the open main endpoint with a routing-preference 45 degree elbow. " +
            "Main DN, Pipe Type, and slope were retained; no Y was created.",
            route,
            ids);
    }

    private static IReadOnlyList<DrainRoute> BuildCase05PipeOnlyRoutes(
        Context context,
        DrainSettings settings)
    {
        XYZ source = context.SourceConnector.Origin;
        double mainX = context.MainEnd.X - context.MainStart.X;
        double mainY = context.MainEnd.Y - context.MainStart.Y;
        double mainPlanLength = Math.Sqrt(mainX * mainX + mainY * mainY);
        if (mainPlanLength <= 1e-9)
            throw new InvalidOperationException(
                "Case 05 requires a main pipe with a usable plan direction.");

        double ux = mainX / mainPlanLength;
        double uy = mainY / mainPlanLength;
        XYZ mainPoint = ResolveConnectionPointOnMain(
            context.Target,
            settings.TargetPoint
            ?? throw new InvalidOperationException(
                "Pick the exact connection point on the main pipe for Case 05."));
        double sourceAlong =
            (source.X - context.MainStart.X) * ux +
            (source.Y - context.MainStart.Y) * uy;
        double projectedX = context.MainStart.X + ux * sourceAlong;
        double projectedY = context.MainStart.Y + uy * sourceAlong;
        double lateralX = source.X - projectedX;
        double lateralY = source.Y - projectedY;
        double lateralDistance = Math.Sqrt(
            lateralX * lateralX + lateralY * lateralY);

        double sourceSlope = settings.SlopePercent / 100.0;
        double planFactor = 1.0 / Math.Sqrt(1.0 + sourceSlope * sourceSlope);
        double fortyFive = Math.PI / 4.0;
        double rollDegrees = settings.YRollAngleDegrees;
        if (rollDegrees < 0.0 || rollDegrees >= 89.9)
            throw new InvalidOperationException(
                "Case 05 Y Up-Roll must be from 0 degrees up to, but not including, 89.9 degrees.");
        double preferredOffset = Math.Max(
            DrainGeometry.Mm(100),
            context.BranchDiameter);
        double minimumVerticalLength = Math.Max(
            DrainGeometry.Mm(25),
            context.BranchDiameter * 0.25);
        double minimumAPlan = Math.Max(
            DrainGeometry.Mm(25),
            context.BranchDiameter * 0.25);
        double minimumB = Math.Max(
            DrainGeometry.Mm(25),
            context.BranchDiameter * 0.25);
        double selectedAlong =
            (mainPoint.X - context.MainStart.X) * ux +
            (mainPoint.Y - context.MainStart.Y) * uy;
        double parameter = Math.Max(0.0, Math.Min(1.0, selectedAlong / mainPlanLength));
        double cross = mainX * lateralY - mainY * lateralX;

        var routes = new List<DrainRoute>();
        foreach (double mainHand in new[] { 1.0, -1.0 })
        {
            double aPlanX = ux * mainHand;
            double aPlanY = uy * mainHand;
            XYZ routeA = new(
                aPlanX * planFactor,
                aPlanY * planFactor,
                -sourceSlope * planFactor);
            XYZ verticalRoute = -XYZ.BasisZ;
            // Keep the compact double-45 pair in the same vertical plane as
            // pipe A. The former exact-cone solution added a normal component
            // so both connector angles were mathematically 45 degrees, but
            // that rolled the pair sideways and produced the visible hook in
            // TOP view. The coplanar bisector keeps the source, offset and A
            // centerlines on one plan axis; Revit resolves the small slope
            // allowance through the elbow family selected by the pipe type.
            XYZ approachAxis = (verticalRoute + routeA).Normalize();

            foreach (double offsetMultiplier in new[] { 1.0, 1.25, 1.50, 1.75, 2.0 })
            {
                double compactDrop = preferredOffset * offsetMultiplier;
                double approachLength = compactDrop / -approachAxis.Z;
                double diagonalPlanX = source.X + approachAxis.X * approachLength;
                double diagonalPlanY = source.Y + approachAxis.Y * approachLength;

                foreach (double branchHand in new[] { 1.0, -1.0 })
                {
                    XYZ routeB;
                    if (rollDegrees <= 1e-8)
                    {
                        // At zero roll, A and B use the same entered gravity
                        // slope. Their TOP-view direction changes by 45 degrees.
                        double perpendicularX = -aPlanY * branchHand;
                        double perpendicularY = aPlanX * branchHand;
                        double bPlanX =
                            aPlanX * Math.Cos(fortyFive) +
                            perpendicularX * Math.Sin(fortyFive);
                        double bPlanY =
                            aPlanY * Math.Cos(fortyFive) +
                            perpendicularY * Math.Sin(fortyFive);
                        routeB = new XYZ(
                            bPlanX * planFactor,
                            bPlanY * planFactor,
                            -sourceSlope * planFactor).Normalize();
                    }
                    else
                    {
                        // For a user roll, B is no longer slope-controlled.
                        // It follows the 45-degree Y branch connector rolled
                        // upward around the A/main axis. This automatically
                        // lifts the complete upstream elbow/A group.
                        XYZ verticalPerpendicular =
                            XYZ.BasisZ - routeA * XYZ.BasisZ.DotProduct(routeA);
                        if (verticalPerpendicular.GetLength() <= 1e-9)
                            continue;
                        verticalPerpendicular = verticalPerpendicular.Normalize();
                        XYZ lateralPerpendicular =
                            routeA.CrossProduct(verticalPerpendicular).Normalize();
                        double roll = rollDegrees * Math.PI / 180.0;
                        XYZ rolledDown =
                            -verticalPerpendicular * Math.Sin(roll) +
                            lateralPerpendicular *
                            (branchHand * Math.Cos(roll));
                        routeB = (
                            routeA * Math.Cos(fortyFive) +
                            rolledDown * Math.Sin(fortyFive)).Normalize();
                    }

                    double bPlanMagnitude = Math.Sqrt(
                        routeB.X * routeB.X + routeB.Y * routeB.Y);
                    if (bPlanMagnitude <= 1e-9 || routeB.Z >= -1e-9)
                        continue;
                    double bPlanXUnit = routeB.X / bPlanMagnitude;
                    double bPlanYUnit = routeB.Y / bPlanMagnitude;

                    // D + A*a = picked main point - B*b. Solving this plan
                    // intersection fixes A and B lengths at the exact point
                    // selected by the user; neither length is hard-coded.
                    double relativeX = mainPoint.X - diagonalPlanX;
                    double relativeY = mainPoint.Y - diagonalPlanY;
                    double determinant =
                        aPlanX * bPlanYUnit - aPlanY * bPlanXUnit;
                    if (Math.Abs(determinant) <= 1e-9)
                        continue;
                    double pipeAPlanLength =
                        (relativeX * bPlanYUnit - relativeY * bPlanXUnit) /
                        determinant;
                    double pipeBPlanLength =
                        (aPlanX * relativeY - aPlanY * relativeX) /
                        determinant;
                    if (pipeAPlanLength < minimumAPlan ||
                        pipeBPlanLength < minimumB)
                        continue;

                    double pipeALength = pipeAPlanLength / planFactor;
                    double pipeBLength = pipeBPlanLength / bPlanMagnitude;
                    double diagonalEndZ =
                        mainPoint.Z -
                        routeA.Z * pipeALength -
                        routeB.Z * pipeBLength;
                    double verticalEndZ =
                        diagonalEndZ - approachAxis.Z * approachLength;
                    double verticalLength = source.Z - verticalEndZ;
                    if (verticalLength < minimumVerticalLength)
                        continue;

                    XYZ verticalEnd = new(source.X, source.Y, verticalEndZ);
                    XYZ diagonalEnd =
                        verticalEnd + approachAxis * approachLength;
                    XYZ pipeAEnd = diagonalEnd + routeA * pipeALength;
                    XYZ calculatedMainPoint = pipeAEnd + routeB * pipeBLength;
                    if (calculatedMainPoint.DistanceTo(mainPoint) >
                        DrainGeometry.Mm(1))
                        continue;

                    routes.Add(new DrainRoute(
                        source,
                        verticalEnd,
                        diagonalEnd,
                        mainPoint,
                        context.MainStart,
                        context.MainEnd,
                        compactDrop,
                        pipeAPlanLength + pipeBPlanLength,
                        parameter,
                        0.0,
                        45.0,
                        settings.SlopePercent,
                        cross >= 0 ? DrainSide.Left : DrainSide.Right,
                        pipeAEnd,
                        5));
                }
            }
        }
        if (routes.Count == 0)
            throw new InvalidOperationException(
                rollDegrees <= 1e-8
                    ? "The selected point cannot fit Case 05 with A and B using the entered slope. Pick another point farther along the main."
                    : $"The selected point cannot fit Case 05 with Y Up-Roll {rollDegrees:0.###} degrees. Pick another point or reduce the roll angle.");
        // In TOP view A is the segment parallel to the main and B is the
        // diagonal into the picked point. Prefer the longest feasible A (and
        // therefore the shortest B). Sorting by the shortest vertical stub
        // previously selected the opposite visual result: tiny A, very long B.
        return routes
            .OrderByDescending(route => PlanDistance(
                route.DiagonalEnd,
                route.NearMainElbow
                ?? throw new InvalidOperationException("Case 05 route has no A/B point.")))
            .ThenBy(route => PlanDistance(
                route.NearMainElbow
                ?? throw new InvalidOperationException("Case 05 route has no A/B point."),
                route.WyePoint))
            .ThenBy(route => route.CompactOffset)
            .Take(24)
            .ToList();
    }

    public static (int Elbows, int Junctions) RoutingRuleCounts(PipeType? pipeType)
    {
        RoutingPreferenceManager? manager = pipeType?.RoutingPreferenceManager;
        if (manager is null) return (0, 0);
        return (
            manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows),
            manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions));
    }

    public static string DescribeJunctionRules(Document document, PipeType? pipeType)
    {
        RoutingPreferenceManager? manager = pipeType?.RoutingPreferenceManager;
        if (manager is null) return "none";

        var names = new List<string>();
        int count = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions);
        for (int index = 0; index < count; index++)
        {
            RoutingPreferenceRule rule =
                manager.GetRule(RoutingPreferenceRuleGroupType.Junctions, index);
            string name = JunctionPartName(document, rule);
            names.Add($"{index + 1}. {name}");
        }

        return names.Count == 0
            ? "none"
            : $"{manager.PreferredJunctionType}; {string.Join(" | ", names)}";
    }

    private static string JunctionPartName(
        Document document,
        RoutingPreferenceRule rule)
    {
        Element? part = document.GetElement(rule.MEPPartId);
        return part switch
        {
            FamilySymbol symbol => $"{symbol.FamilyName}: {symbol.Name}",
            null => $"missing part {rule.MEPPartId.Value}",
            _ => part.Name
        };
    }

    private static DrainOperationResult CreateBranchOnly(
        Document document,
        Context context,
        DrainRoute route)
    {
        var ids = new List<ElementId>();
        Pipe target = document.GetElement(context.Target.Id) as Pipe
            ?? throw new InvalidOperationException("The target main is no longer available.");
        Element source = document.GetElement(context.Source.Id)
            ?? throw new InvalidOperationException("The source drain is no longer available.");
        XYZ connectionPoint = ResolveConnectionPointOnMain(target, route.WyePoint);
        route = route with { WyePoint = connectionPoint };
        ApproachPipes approach = CreateApproachPipes(document, context, route, ids);

        // Build the entire gravity route in the same direction a user draws it:
        // source device -> two 45-degree elbows -> branch endpoint on the main.
        // The main remains untouched until this complete route is valid.
        Pipe branch = CreatePipe(
            document,
            context,
            route.DiagonalEnd,
            route.WyePoint);
        ids.Add(branch.Id);
        document.Regenerate();

        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        FamilyInstance? sourceTransition = null;
        if (route.CaseNumber == 6)
        {
            sourceTransition = ConnectCase04Source(
                document,
                sourceConnector,
                approach.Stub,
                route);
        }
        else
        {
            ConnectSource(document, sourceConnector, approach.Stub, route);
        }
        FamilyInstance elbow1 = document.Create.NewElbowFitting(
            DrainSelection.ConnectorNear(approach.Stub, route.StubEnd, true),
            DrainSelection.ConnectorNear(approach.Diagonal, route.StubEnd, true));
        ids.Add(elbow1.Id);
        FamilyInstance elbow2 = document.Create.NewElbowFitting(
            DrainSelection.ConnectorNear(approach.Diagonal, route.DiagonalEnd, true),
            DrainSelection.ConnectorNear(branch, route.DiagonalEnd, true));
        ids.Add(elbow2.Id);
        if (sourceTransition is not null)
            ids.Add(sourceTransition.Id);
        document.Regenerate();

        return new DrainOperationResult(
            true,
            false,
            "Stage 1 created: the gravity branch runs from the device to the main " +
            "with two routing-preference 45 degree elbows. " +
            "The main is not broken and no Y fitting is created yet.",
            route,
            ids);
    }

    private static DrainOperationResult CreateSplitSlopeBranch(
        Document document,
        Context context,
        DrainRoute route)
    {
        XYZ nearMainElbow = route.NearMainElbow
            ?? throw new InvalidOperationException(
                "Case 02 did not calculate its near-main elbow point.");
        var ids = new List<ElementId>();
        Pipe target = document.GetElement(context.Target.Id) as Pipe
            ?? throw new InvalidOperationException("The target main is no longer available.");
        Element source = document.GetElement(context.Source.Id)
            ?? throw new InvalidOperationException("The source drain is no longer available.");
        XYZ connectionPoint = ResolveConnectionPointOnMain(target, route.WyePoint);
        route = route with { WyePoint = connectionPoint };

        ApproachPipes approach = CreateApproachPipes(document, context, route, ids);
        Pipe straightBranch = CreatePipe(
            document,
            context,
            route.DiagonalEnd,
            nearMainElbow);
        Pipe yLeg = CreatePipe(
            document,
            context,
            nearMainElbow,
            route.WyePoint);
        ids.AddRange([straightBranch.Id, yLeg.Id]);
        document.Regenerate();

        // Place fittings before locking the source end to the device. Revit may
        // trim pipe endpoints while resolving elbow takeout; anchoring the stub
        // to the fixture first can force a pipe to reverse and invalidate it.
        FamilyInstance nearMain45 = CreateCase05Elbow(
            document,
            straightBranch,
            yLeg,
            nearMainElbow,
            "A/B 45 degree elbow");
        FamilyInstance sourceElbow2 = CreateCase05Elbow(
            document,
            approach.Diagonal,
            straightBranch,
            route.DiagonalEnd,
            "lower device-side 45 degree elbow");
        FamilyInstance sourceElbow1 = CreateCase05Elbow(
            document,
            approach.Stub,
            approach.Diagonal,
            route.StubEnd,
            "upper device-side 45 degree elbow");
        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        ConnectSource(document, sourceConnector, approach.Stub, route);
        ids.AddRange([sourceElbow1.Id, sourceElbow2.Id, nearMain45.Id]);
        document.Regenerate();

        if (route.CaseNumber == 5)
        {
            ValidateCase05PipeAxes(
                approach.Stub,
                approach.Diagonal,
                straightBranch,
                context,
                new DrainSettings(
                    route.SlopePercent,
                    context.BranchPipeType.Id));
        }

        return new DrainOperationResult(
            true,
            false,
            $"Case {route.CaseNumber:00} Stage 1 created: source double-45 pair, " +
            "straight source-side sloped branch, one 45 degree elbow, and a Y-rolled leg ending on the main.",
            route,
            ids);
    }

    private static FamilyInstance CreateCase05Elbow(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        string label)
    {
        try
        {
            FamilyInstance elbow = document.Create.NewElbowFitting(
                DrainSelection.ConnectorNear(first, point, true),
                DrainSelection.ConnectorNear(second, point, true));
            document.Regenerate();
            return elbow;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 05 {label} could not be created: {exception.Message}",
                exception);
        }
    }

    private static void ValidateCase05PipeAxes(
        Pipe vertical,
        Pipe diagonal,
        Pipe pipeA,
        Context context,
        DrainSettings settings)
    {
        if (vertical.Location is not LocationCurve verticalLocation ||
            diagonal.Location is not LocationCurve diagonalLocation ||
            pipeA.Location is not LocationCurve pipeALocation)
            throw new InvalidOperationException(
                "Case 05 could not read the created pipe centerlines.");

        XYZ verticalStart = verticalLocation.Curve.GetEndPoint(0);
        XYZ verticalEnd = verticalLocation.Curve.GetEndPoint(1);
        double verticalPlanShift = Math.Sqrt(
            Math.Pow(verticalEnd.X - verticalStart.X, 2) +
            Math.Pow(verticalEnd.Y - verticalStart.Y, 2));
        if (verticalPlanShift > DrainGeometry.Mm(1))
            throw new InvalidOperationException(
                $"Revit tilted the device pipe by {DrainGeometry.ToMm(verticalPlanShift):0.###} mm in plan; candidate rejected.");

        XYZ diagonalStart = diagonalLocation.Curve.GetEndPoint(0);
        XYZ diagonalEnd = diagonalLocation.Curve.GetEndPoint(1);
        double diagonalX = diagonalEnd.X - diagonalStart.X;
        double diagonalY = diagonalEnd.Y - diagonalStart.Y;
        double diagonalPlanLength = Math.Sqrt(
            diagonalX * diagonalX + diagonalY * diagonalY);

        XYZ aStart = pipeALocation.Curve.GetEndPoint(0);
        XYZ aEnd = pipeALocation.Curve.GetEndPoint(1);
        double aX = aEnd.X - aStart.X;
        double aY = aEnd.Y - aStart.Y;
        double aPlanLength = Math.Sqrt(aX * aX + aY * aY);
        double mainX = context.MainEnd.X - context.MainStart.X;
        double mainY = context.MainEnd.Y - context.MainStart.Y;
        double mainPlanLength = Math.Sqrt(mainX * mainX + mainY * mainY);
        if (aPlanLength <= DrainGeometry.Mm(10) || mainPlanLength <= DrainGeometry.Mm(10))
            throw new InvalidOperationException(
                "Pipe A or the selected main is too short for direction validation.");
        double parallelError = Math.Abs(aX * mainY - aY * mainX) /
            (aPlanLength * mainPlanLength);
        if (parallelError > 1e-5)
            throw new InvalidOperationException(
                "Revit moved pipe A away from the selected main's plan direction; candidate rejected.");
        if (diagonalPlanLength <= DrainGeometry.Mm(1))
            throw new InvalidOperationException(
                "The compact double-45 offset has no usable TOP-view direction.");
        double offsetParallelError = Math.Abs(
            diagonalX * mainY - diagonalY * mainX) /
            (diagonalPlanLength * mainPlanLength);
        if (offsetParallelError > 1e-5)
            throw new InvalidOperationException(
                "Revit rolled the device-side double-45 pair out of pipe A's vertical plane; candidate rejected.");

        double actualSlope = Math.Abs(aEnd.Z - aStart.Z) / aPlanLength * 100.0;
        if (Math.Abs(actualSlope - settings.SlopePercent) > 0.01)
            throw new InvalidOperationException(
                $"Pipe A resolved at {actualSlope:0.###}% instead of {settings.SlopePercent:0.###}%; candidate rejected.");
    }

    private static DrainOperationResult CreateStandingDiagonalBranch(
        Document document,
        Context context,
        DrainRoute route)
    {
        var ids = new List<ElementId>();
        Pipe target = document.GetElement(context.Target.Id) as Pipe
            ?? throw new InvalidOperationException("The target main is no longer available.");
        Element source = document.GetElement(context.Source.Id)
            ?? throw new InvalidOperationException("The source drain is no longer available.");
        XYZ connectionPoint = ResolveConnectionPointOnMain(target, route.WyePoint);
        route = route with { WyePoint = connectionPoint };

        // Case 03 uses a standing pipe under the device, one elbow,
        // then one diagonal 3D leg into the routed Y.
        Pipe standing = CreatePipe(
            document,
            context,
            route.DrainOrigin,
            route.StubEnd);
        Pipe diagonal = CreatePipe(
            document,
            context,
            route.StubEnd,
            route.WyePoint);
        ids.AddRange([standing.Id, diagonal.Id]);
        document.Regenerate();

        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        ConnectSource(document, sourceConnector, standing, route);
        FamilyInstance elbow45 = document.Create.NewElbowFitting(
            DrainSelection.ConnectorNear(standing, route.StubEnd, true),
            DrainSelection.ConnectorNear(diagonal, route.StubEnd, true));
        ids.Add(elbow45.Id);
        document.Regenerate();

        return new DrainOperationResult(
            true,
            false,
            $"Case {route.CaseNumber:00} Stage 1 created: standing pipe, one routing-preference " +
            "elbow, and one diagonal 3D leg ending on the main.",
            route,
            ids);
    }

    private static DrainOperationResult CreateBranchOnlyCase04(
        Document document,
        Context context,
        DrainRoute route)
    {
        XYZ secondElbow = route.NearMainElbow
            ?? throw new InvalidOperationException(
                "Case 04 did not calculate its second 45 degree elbow.");
        XYZ extensionElbow = route.MainExtensionElbow
            ?? throw new InvalidOperationException(
                "Case 04 did not calculate its main-extension elbow.");
        var ids = new List<ElementId>();
        Pipe target = document.GetElement(context.Target.Id) as Pipe
            ?? throw new InvalidOperationException("The target main is no longer available.");
        Element source = document.GetElement(context.Source.Id)
            ?? throw new InvalidOperationException("The source drain is no longer available.");

        ExtendOpenMainEndpoint(target, route.WyePoint, extensionElbow);
        document.Regenerate();
        Connector targetEnd = DrainSelection.ConnectorNear(target, extensionElbow, true);

        ApproachPipes approach = CreateApproachPipes(document, context, route, ids);
        Pipe straightBranch = CreatePipe(
            document,
            context,
            route.DiagonalEnd,
            secondElbow);
        Pipe turnDiagonal = CreatePipe(
            document,
            context,
            secondElbow,
            extensionElbow);
        ids.AddRange([straightBranch.Id, turnDiagonal.Id]);
        document.Regenerate();

        // Anchor the vertical stub to the drain before Revit resolves any
        // elbows. Otherwise the last ConnectTo call can pull the whole compact
        // device-side pair sideways and leave a visibly tilted drop pipe.
        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        FamilyInstance? sourceTransition = ConnectCase04Source(
            document,
            sourceConnector,
            approach.Stub,
            route);
        FamilyInstance source45A = CreateCase04Elbow(
            document, approach.Stub, approach.Diagonal, route.StubEnd,
            "upper device-side 45 degree elbow");
        FamilyInstance source45B = CreateCase04Elbow(
            document, approach.Diagonal, straightBranch, route.DiagonalEnd,
            "lower device-side 45 degree elbow");
        FamilyInstance second45 = CreateCase04Elbow(
            document, straightBranch, turnDiagonal, secondElbow,
            "second plan 45 degree elbow");
        FamilyInstance main45;
        try
        {
            main45 = document.Create.NewElbowFitting(
                DrainSelection.ConnectorNear(turnDiagonal, extensionElbow, true),
                targetEnd);
            document.Regenerate();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 04 main-extension 45 degree elbow could not be created: {exception.Message}",
                exception);
        }
        ValidateVerticalDeviceStub(approach.Stub, route.DrainOrigin);
        ids.AddRange([
            source45A.Id,
            source45B.Id,
            second45.Id,
            main45.Id]);
        if (sourceTransition is not null)
            ids.Add(sourceTransition.Id);
        document.Regenerate();

        return new DrainOperationResult(
            true,
            false,
            "Case 04 created: the open main was extended, then two 45 degree plan elbows " +
            "and a straight run connected it to the device. Main DN, type, and slope were retained.",
            route,
            ids);
    }

    private static FamilyInstance CreateCase04Elbow(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        string label)
    {
        try
        {
            FamilyInstance elbow = document.Create.NewElbowFitting(
                DrainSelection.ConnectorNear(first, point, true),
                DrainSelection.ConnectorNear(second, point, true));
            document.Regenerate();
            return elbow;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 04 {label} could not be created: {exception.Message}",
                exception);
        }
    }

    private static void ValidateVerticalDeviceStub(Pipe stub, XYZ drainOrigin)
    {
        if (stub.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "Case 04 could not read the device drop pipe centerline.");
        XYZ first = location.Curve.GetEndPoint(0);
        XYZ second = location.Curve.GetEndPoint(1);
        XYZ nearDrain = first.DistanceTo(drainOrigin) <= second.DistanceTo(drainOrigin)
            ? first
            : second;
        XYZ lower = ReferenceEquals(nearDrain, first) ? second : first;
        double planShift = Math.Sqrt(
            Math.Pow(lower.X - nearDrain.X, 2) +
            Math.Pow(lower.Y - nearDrain.Y, 2));
        if (planShift > DrainGeometry.Mm(0.5))
            throw new InvalidOperationException(
                $"Case 04 rejected a tilted device drop pipe ({DrainGeometry.ToMm(planShift):0.###} mm plan offset)." );
    }

    private static void ExtendOpenMainEndpoint(
        Pipe target,
        XYZ oldEndpoint,
        XYZ newEndpoint)
    {
        if (target.Location is not LocationCurve location)
            throw new InvalidOperationException("Case 04 cannot read the main centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        bool replaceStart = oldEndpoint.DistanceTo(start) <= oldEndpoint.DistanceTo(end);
        XYZ fixedEnd = replaceStart ? end : start;
        if (fixedEnd.DistanceTo(newEndpoint) <= DrainGeometry.Mm(10))
            throw new InvalidOperationException("Case 04 main extension is too short.");
        location.Curve = replaceStart
            ? Line.CreateBound(newEndpoint, fixedEnd)
            : Line.CreateBound(fixedEnd, newEndpoint);
    }

    private static DrainOperationResult CompleteWithRoutedY(
        Document document,
        Context context,
        DrainOperationResult stage1,
        List<string> failures)
    {
        DrainRoute route = stage1.Route
            ?? throw new InvalidOperationException("Stage 1 did not return its route.");
        string caseLabel = $"Case {route.CaseNumber:00}";
        var stage1Ids = stage1.CreatedIds?.ToList() ?? [];
        Pipe branch = stage1Ids
            .Select(document.GetElement)
            .OfType<Pipe>()
            .FirstOrDefault(pipe => HasEndAt(pipe, route.WyePoint))
            ?? throw new InvalidOperationException(
                "The Stage 1 branch endpoint on the main could not be found.");

        JunctionOrientation[] orientations =
        [
            new(false, "manual 3D rotation")
        ];
        using var stage2Group = new TransactionGroup(
            document,
            $"{caseLabel} Stage 2 routed Y");
        stage2Group.Start();
        try
        {
            IReadOnlyList<PreparedJunctionType> allPreparedTypes;
            using (var prepare = new Transaction(
                       document,
                       "Stage 2 isolate Junction routing families"))
            {
                prepare.Start();
                allPreparedTypes = PrepareJunctionPipeTypes(
                    document,
                    context.TargetPipeType,
                    context.MainDiameter,
                    context.BranchDiameter);
                if (prepare.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(
                        "Revit could not isolate the main Pipe Type Junction routing rules.");
            }

            IReadOnlyList<PreparedJunctionType> angleMatchedTypes = allPreparedTypes
                .Where(item =>
                    Math.Abs(item.FittingAngleDegrees - route.FittingAngleDegrees) <= 2.0)
                .ToList();
            IReadOnlyList<PreparedJunctionType> preparedTypes =
                angleMatchedTypes.Count > 0 ? angleMatchedTypes : allPreparedTypes;
            IReadOnlyList<ElementId> allPreparedTypeIds = allPreparedTypes
                .Select(item => item.TypeId)
                .ToList();
            // Reducing Y families in project routing preferences carry the
            // main size on both run connectors and the device size on their
            // branch connector. Do not force a same-size Y seed first.
            bool[] seedStrategies = [false];

            foreach (PreparedJunctionType preparedType in preparedTypes)
            {
                foreach (bool useMainSizeSeed in seedStrategies)
                {
                    foreach (JunctionOrientation orientation in orientations)
                    {
                        string sizeLabel = useMainSizeSeed
                            ? "main-DN seed + reducer"
                            : "direct branch DN";
                        string attemptLabel =
                            $"Stage 2 {preparedType.Label}, {sizeLabel}, " +
                            $"main DN{DrainGeometry.ToMm(context.MainDiameter):0.#}, " +
                            $"branch DN{DrainGeometry.ToMm(context.BranchDiameter):0.#}, " +
                            orientation.Label;
                        using var stage2 = new Transaction(
                            document,
                            $"{caseLabel} break main and insert isolated routing Y");
                        stage2.Start();
                        ConfigureFailureHandling(
                            stage2,
                            failure => failures.Add($"{attemptLabel}: {failure}"));
                        try
                        {
                            Pipe target = document.GetElement(context.Target.Id) as Pipe
                                ?? throw new InvalidOperationException(
                                    "The selected main is no longer available for Stage 2.");
                            XYZ junctionPoint = ResolveConnectionPointOnMain(
                                target,
                                route.WyePoint);

                            // Force this attempt to use exactly one Junction routing
                            // family. Revit otherwise stops at the first compatible-DN
                            // Tee rule and never advances to the following 45-degree Y.
                            target.ChangeTypeId(preparedType.TypeId);
                            branch = document.GetElement(branch.Id) as Pipe
                                ?? throw new InvalidOperationException(
                                    "The Stage 1 branch is no longer available.");

                            Pipe junctionLeg = branch;
                            JunctionSeed? seed = null;
                            if (useMainSizeSeed)
                            {
                                seed = CreateMainSizeJunctionSeed(
                                    document,
                                    context,
                                    branch,
                                    junctionPoint,
                                    preparedType.TypeId);
                                junctionLeg = seed.Pipe;
                            }
                            document.Regenerate();

                            ElementId splitId = PlumbingUtils.BreakCurve(
                                document,
                                target.Id,
                                junctionPoint);
                            if (splitId == ElementId.InvalidElementId)
                                throw new InvalidOperationException(
                                    "Revit could not break the main at the branch endpoint.");

                            Pipe splitPipe = document.GetElement(splitId) as Pipe
                                ?? throw new InvalidOperationException(
                                    "The second main segment was not found after the break.");
                            document.Regenerate();

                            FamilyInstance? junction = null;
                            bool nativeRoutedJunction = false;
                            if (route.CaseNumber == 5 && seed is null)
                            {
                                junction = TryCreateNativeRoutedJunction(
                                    document,
                                    target,
                                    splitPipe,
                                    junctionLeg,
                                    junctionPoint,
                                    preparedType.PartId,
                                    out string nativeFailure);
                                nativeRoutedJunction = junction is not null;
                                if (!nativeRoutedJunction &&
                                    !string.IsNullOrWhiteSpace(nativeFailure))
                                    failures.Add(
                                        $"{attemptLabel}, native routed Y: {nativeFailure}");
                            }

                            junction ??= PlaceRotateAndConnectJunction(
                                document,
                                preparedType.PartId,
                                junctionPoint,
                                target,
                                splitPipe,
                                junctionLeg,
                                context.MainDiameter,
                                context.BranchDiameter,
                                true);
                            document.Regenerate();

                            target.ChangeTypeId(context.TargetPipeType.Id);
                            splitPipe.ChangeTypeId(context.TargetPipeType.Id);
                            if (seed is not null)
                                seed.Pipe.ChangeTypeId(context.TargetPipeType.Id);
                            document.Regenerate();
                            foreach (ElementId typeId in allPreparedTypeIds)
                            {
                                if (document.GetElement(typeId) is not null)
                                    document.Delete(typeId);
                            }
                            document.Regenerate();

                            if (stage2.Commit() != TransactionStatus.Committed)
                            {
                                failures.Add(
                                    $"{attemptLabel}: Revit rolled back Y insertion.");
                                continue;
                            }
                            if (stage2Group.Assimilate() != TransactionStatus.Committed)
                                throw new InvalidOperationException(
                                    "Revit did not assimilate the Stage 2 routed Y.");

                            stage1Ids.Add(splitId);
                            stage1Ids.Add(junction.Id);
                            if (seed is not null)
                                stage1Ids.AddRange([seed.Pipe.Id, seed.Transition.Id]);
                            return new DrainOperationResult(
                                true,
                                false,
                                $"{caseLabel} created in two stages: the branch was committed first, " +
                                "then the main was broken at its endpoint and Revit inserted the " +
                                $"{preparedType.Label} Y using {sizeLabel}.",
                                route with { WyePoint = junctionPoint },
                                stage1Ids);
                        }
                        catch (Exception exception)
                        {
                            failures.Add($"{attemptLabel}: {exception.Message}");
                            if (stage2.GetStatus() == TransactionStatus.Started)
                                stage2.RollBack();
                        }
                    }
                }
            }

            if (stage2Group.GetStatus() == TransactionStatus.Started)
                stage2Group.RollBack();
        }
        catch (Exception exception)
        {
            failures.Add($"Stage 2 routing isolation: {exception.Message}");
            if (stage2Group.GetStatus() == TransactionStatus.Started)
                stage2Group.RollBack();
        }

        IReadOnlyList<string> warnings = failures
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct()
            .TakeLast(6)
            .ToList();
        return new DrainOperationResult(
            true,
            true,
            "Stage 1 was created and kept, but Revit could not insert the routing Y. " +
            "The main was restored without a break so the model is left clean for Stage 2 diagnosis.",
            route,
            stage1Ids,
            warnings);
    }

    private static bool HasEndAt(Pipe pipe, XYZ point)
    {
        if (pipe.Location is not LocationCurve location)
            return false;
        return location.Curve.GetEndPoint(0).DistanceTo(point) <= DrainGeometry.Mm(1) ||
               location.Curve.GetEndPoint(1).DistanceTo(point) <= DrainGeometry.Mm(1);
    }

    private static JunctionSeed CreateMainSizeJunctionSeed(
        Document document,
        Context context,
        Pipe branch,
        XYZ junctionPoint,
        ElementId preparedPipeTypeId)
    {
        if (branch.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "The Stage 1 branch has no usable centerline for a main-DN seed.");

        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        bool junctionAtStart = start.DistanceTo(junctionPoint) <= DrainGeometry.Mm(1);
        bool junctionAtEnd = end.DistanceTo(junctionPoint) <= DrainGeometry.Mm(1);
        if (!junctionAtStart && !junctionAtEnd)
            throw new InvalidOperationException(
                "The Stage 1 branch does not terminate at the calculated junction point.");

        XYZ deviceSide = junctionAtStart ? end : start;
        XYZ towardDevice = deviceSide - junctionPoint;
        double availableLength = towardDevice.GetLength();
        double seedLength = Math.Max(
            DrainGeometry.Mm(120),
            context.MainDiameter * 2.5);
        if (availableLength < seedLength + DrainGeometry.Mm(100))
            throw new InvalidOperationException(
                "The final branch segment is too short for a main-DN Y seed and reducer.");

        XYZ transitionPoint =
            junctionPoint + towardDevice.Normalize() * seedLength;
        location.Curve = junctionAtStart
            ? Line.CreateBound(transitionPoint, deviceSide)
            : Line.CreateBound(deviceSide, transitionPoint);
        document.Regenerate();

        // Author the seed in the same device-to-main direction as the branch.
        // This gives its main-end connector the orientation expected by the Y.
        Pipe seed = CreatePipe(
            document,
            context,
            preparedPipeTypeId,
            context.MainDiameter,
            transitionPoint,
            junctionPoint);
        document.Regenerate();
        FamilyInstance transition = document.Create.NewTransitionFitting(
            DrainSelection.ConnectorNear(branch, transitionPoint, true),
            DrainSelection.ConnectorNear(seed, transitionPoint, true));
        document.Regenerate();
        return new JunctionSeed(seed, transition);
    }

    private static IReadOnlyList<PreparedJunctionType> PrepareJunctionPipeTypes(
        Document document,
        PipeType targetPipeType,
        double mainDiameter,
        double branchDiameter)
    {
        RoutingPreferenceManager sourceManager = targetPipeType.RoutingPreferenceManager;
        int count = sourceManager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Junctions);
        ElementId preferredPartId = ResolvePreferredJunctionPart(
            sourceManager,
            mainDiameter,
            branchDiameter);
        var candidates = new List<JunctionRuleCandidate>();
        for (int index = 0; index < count; index++)
        {
            RoutingPreferenceRule rule = sourceManager.GetRule(
                RoutingPreferenceRuleGroupType.Junctions,
                index);
            if (rule.MEPPartId == ElementId.InvalidElementId ||
                document.GetElement(rule.MEPPartId) is not FamilySymbol)
                continue;

            candidates.Add(new JunctionRuleCandidate(
                index,
                rule.MEPPartId,
                JunctionPartName(document, rule)));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "The Target Pipe Type has no valid junction family in Routing Preferences.");

        var prepared = new List<PreparedJunctionType>();
        foreach (JunctionRuleCandidate candidate in candidates
                     .OrderByDescending(item => item.PartId == preferredPartId)
                     .ThenBy(item => item.RuleIndex))
        {
            PipeType temporary = targetPipeType.Duplicate(
                $"__FamilyMEP_Drain_{Guid.NewGuid():N}") as PipeType
                ?? throw new InvalidOperationException(
                    "Revit could not create a temporary routing Pipe Type.");
            RoutingPreferenceManager manager = temporary.RoutingPreferenceManager;
            for (int index = count - 1; index >= 0; index--)
            {
                if (index != candidate.RuleIndex)
                    manager.RemoveRule(RoutingPreferenceRuleGroupType.Junctions, index);
            }

            double fittingAngle = ResolveJunctionAngleDegrees(document, candidate.PartId);
            prepared.Add(new PreparedJunctionType(
                temporary.Id,
                candidate.PartId,
                $"{candidate.Label} (connector {fittingAngle:0.###} deg)",
                fittingAngle));
        }

        IReadOnlyList<PreparedJunctionType> case01 = prepared
            .Where(item => item.FittingAngleDegrees >= 35.0 && item.FittingAngleDegrees <= 55.0)
            .ToList();
        if (case01.Count == 0)
            throw new InvalidOperationException(
                "No junction family with a connector angle between 35 and 55 degrees is available for this drain case.");
        return case01;
    }

    private static double ResolveJunctionAngleDegrees(
        Document document,
        ElementId symbolId)
    {
        if (document.GetElement(symbolId) is not FamilySymbol symbol)
            return 45.0;

        FamilyInstance? probe = null;
        try
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                document.Regenerate();
            }

            probe = document.Create.NewFamilyInstance(
                XYZ.Zero,
                symbol,
                StructuralType.NonStructural);
            document.Regenerate();
            IReadOnlyList<Connector> connectors = DrainSelection.GetConnectors(probe);
            if (connectors.Count < 3)
                return 45.0;

            (int first, int second) = MostOppositeConnectorPair(connectors);
            Connector? branch = connectors
                .Where((_, index) => index != first && index != second)
                .OrderByDescending(item => item.Radius)
                .FirstOrDefault();
            if (branch?.CoordinateSystem is null ||
                connectors[first].CoordinateSystem is null ||
                connectors[second].CoordinateSystem is null)
                return 45.0;

            double firstAngle = AcuteDegrees(
                branch.CoordinateSystem.BasisZ,
                connectors[first].CoordinateSystem.BasisZ);
            double secondAngle = AcuteDegrees(
                branch.CoordinateSystem.BasisZ,
                connectors[second].CoordinateSystem.BasisZ);
            double measured = Math.Min(firstAngle, secondAngle);
            // Preserve a measured 90-degree Tee as 90. Returning the former
            // 45-degree fallback here incorrectly classified Tee rules as Y
            // candidates and made Revit stop on the Tee before reaching the Y.
            return measured is > 1.0 and <= 90.001 ? measured : 45.0;
        }
        catch
        {
            return 45.0;
        }
        finally
        {
            if (probe is not null && document.GetElement(probe.Id) is not null)
                document.Delete(probe.Id);
        }
    }

    private static (int First, int Second) MostOppositeConnectorPair(
        IReadOnlyList<Connector> connectors)
    {
        int first = 0;
        int second = 1;
        double smallestDot = double.MaxValue;
        for (int i = 0; i < connectors.Count - 1; i++)
        {
            XYZ? a = connectors[i].CoordinateSystem?.BasisZ;
            if (a is null) continue;
            for (int j = i + 1; j < connectors.Count; j++)
            {
                XYZ? b = connectors[j].CoordinateSystem?.BasisZ;
                if (b is null) continue;
                double dot = a.Normalize().DotProduct(b.Normalize());
                if (dot < smallestDot)
                {
                    smallestDot = dot;
                    first = i;
                    second = j;
                }
            }
        }
        return (first, second);
    }

    private static double AcuteDegrees(XYZ first, XYZ second)
    {
        double dot = Math.Max(
            -1.0,
            Math.Min(1.0, first.Normalize().DotProduct(second.Normalize())));
        double angle = Math.Acos(dot) * 180.0 / Math.PI;
        return Math.Min(angle, 180.0 - angle);
    }

    private static ElementId ResolvePreferredJunctionPart(
        RoutingPreferenceManager manager,
        double mainDiameter,
        double branchDiameter)
    {
        double[][] conditionSets =
        [
            [mainDiameter, mainDiameter, branchDiameter],
            [mainDiameter, branchDiameter],
            [mainDiameter]
        ];
        foreach (double[] diameters in conditionSets)
        {
            try
            {
                using var conditions = new RoutingConditions(
                    RoutingPreferenceErrorLevel.None)
                {
                    PreferredJunctionType = manager.PreferredJunctionType
                };
                foreach (double diameter in diameters)
                    conditions.AppendCondition(new RoutingCondition(diameter));
                ElementId partId = manager.GetMEPPartId(
                    RoutingPreferenceRuleGroupType.Junctions,
                    conditions);
                if (partId != ElementId.InvalidElementId)
                    return partId;
            }
            catch
            {
                // Some project families expose only the primary size criterion.
                // Continue with the next condition shape, then try every valid rule.
            }
        }

        return ElementId.InvalidElementId;
    }

    private static XYZ ResolveConnectionPointOnMain(Pipe target, XYZ proposedPoint)
    {
        if (target.Location is not LocationCurve location)
            throw new InvalidOperationException("The selected main has no usable centerline.");

        IntersectionResult? projection = location.Curve.Project(proposedPoint);
        if (projection is null)
            throw new InvalidOperationException(
                "The calculated branch endpoint could not be projected onto the selected main.");

        XYZ pointOnMain = projection.XYZPoint;
        if (pointOnMain.DistanceTo(proposedPoint) > DrainGeometry.Mm(1))
            throw new InvalidOperationException(
                "The calculated branch endpoint is not on the selected main centerline.");

        double distanceToStart = pointOnMain.DistanceTo(location.Curve.GetEndPoint(0));
        double distanceToEnd = pointOnMain.DistanceTo(location.Curve.GetEndPoint(1));
        if (Math.Min(distanceToStart, distanceToEnd) < DrainGeometry.Mm(10))
            throw new InvalidOperationException(
                "The connection point is too close to an end of the selected main.");

        return pointOnMain;
    }

    private static ApproachPipes CreateApproachPipes(
        Document document,
        Context context,
        DrainRoute route,
        List<ElementId> ids)
    {
        Pipe stub = CreatePipe(document, context, route.DrainOrigin, route.StubEnd);
        Pipe diagonal = CreatePipe(document, context, route.StubEnd, route.DiagonalEnd);
        ids.AddRange([stub.Id, diagonal.Id]);
        document.Regenerate();
        return new ApproachPipes(stub, diagonal);
    }

    private static FamilyInstance? TryCreateNativeRoutedJunction(
        Document document,
        Pipe target,
        Pipe splitPipe,
        Pipe branch,
        XYZ junctionPoint,
        ElementId expectedPartId,
        out string failure)
    {
        failure = string.Empty;
        using var attempt = new SubTransaction(document);
        attempt.Start();
        try
        {
            Connector firstRun = DrainSelection.ConnectorNear(
                target,
                junctionPoint,
                true);
            Connector secondRun = DrainSelection.ConnectorNear(
                splitPipe,
                junctionPoint,
                true);
            Connector branchConnector = DrainSelection.ConnectorNear(
                branch,
                junctionPoint,
                true);

            // The first two connectors are the collinear main run and the
            // third connector is B. Because target/split currently use the
            // isolated temporary Pipe Type, Revit can resolve only the one
            // 45-degree Y rule selected for this attempt. This also lets the
            // fitting family resolve a reducing branch from the three pipe
            // connector sizes instead of writing locked family parameters.
            FamilyInstance junction = document.Create.NewTeeFitting(
                firstRun,
                secondRun,
                branchConnector);
            document.Regenerate();
            if (junction.Symbol.Id != expectedPartId)
                throw new InvalidOperationException(
                    $"Revit selected '{junction.Symbol.FamilyName}: {junction.Symbol.Name}' " +
                    "instead of the isolated 45-degree Y routing rule.");
            JunctionConnectors connected = JunctionConnectorsOf(junction);
            if (!connected.RunA.IsConnected ||
                !connected.RunB.IsConnected ||
                !connected.Branch.IsConnected)
                throw new InvalidOperationException(
                    "The routed Y was placed but one or more of its three connectors remain open.");
            if (attempt.Commit() != TransactionStatus.Committed)
            {
                failure = "Revit rolled back the native three-connector fitting.";
                return null;
            }

            return junction;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            if (attempt.GetStatus() == TransactionStatus.Started)
                attempt.RollBack();
            return null;
        }
    }

    private static FamilyInstance PlaceRotateAndConnectJunction(
        Document document,
        ElementId symbolId,
        XYZ junctionPoint,
        Pipe target,
        Pipe splitPipe,
        Pipe branch,
        double mainDiameter,
        double branchDiameter,
        bool connectBranch)
    {
        if (document.GetElement(symbolId) is not FamilySymbol symbol)
            throw new InvalidOperationException(
                "The selected Junction routing rule does not reference a loadable Y family symbol.");
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }

        FamilyInstance junction = document.Create.NewFamilyInstance(
            junctionPoint,
            symbol,
            StructuralType.NonStructural);
        document.Regenerate();

        ApplyJunctionConnectorSizes(
            document,
            junction,
            mainDiameter,
            branchDiameter);

        JunctionConnectors initial = JunctionConnectorsOf(junction);
        XYZ sourceRun = initial.RunA.CoordinateSystem.BasisZ.Normalize();
        XYZ sourceBranch = initial.Branch.CoordinateSystem.BasisZ.Normalize();
        XYZ mainAxis = PipeDirectionAwayFrom(target, junctionPoint).Normalize();
        XYZ branchAxis = PipeDirectionAwayFrom(branch, junctionPoint).Normalize();

        // Pick the run direction that preserves the authored 45-degree branch
        // hand, then align the family run with the sloped main centerline.
        double authoredDot = sourceBranch.DotProduct(sourceRun);
        if (Math.Abs(branchAxis.DotProduct(-mainAxis) - authoredDot) <
            Math.Abs(branchAxis.DotProduct(mainAxis) - authoredDot))
            mainAxis = -mainAxis;
        RotateVectorToVector(
            document,
            junction.Id,
            junctionPoint,
            sourceRun,
            mainAxis);
        document.Regenerate();

        JunctionConnectors afterRun = JunctionConnectorsOf(junction);
        XYZ currentBranch = afterRun.Branch.CoordinateSystem.BasisZ.Normalize();
        XYZ currentPerpendicular = PerpendicularComponent(currentBranch, mainAxis);
        XYZ desiredPerpendicular = PerpendicularComponent(branchAxis, mainAxis);
        double roll = SignedAngle(
            currentPerpendicular,
            desiredPerpendicular,
            mainAxis);
        if (Math.Abs(roll) > 1e-8)
        {
            ElementTransformUtils.RotateElement(
                document,
                junction.Id,
                Line.CreateUnbound(junctionPoint, mainAxis),
                roll);
            document.Regenerate();
        }

        JunctionConnectors oriented = JunctionConnectorsOf(junction);
        XYZ fittingIntersection = ClosestAxisIntersection(
            oriented.RunA.Origin,
            oriented.RunA.CoordinateSystem.BasisZ,
            oriented.Branch.Origin,
            oriented.Branch.CoordinateSystem.BasisZ);
        ElementTransformUtils.MoveElement(
            document,
            junction.Id,
            junctionPoint - fittingIntersection);
        document.Regenerate();

        JunctionConnectors fitted = JunctionConnectorsOf(junction);
        XYZ targetDirection = PipeDirectionAwayFrom(target, junctionPoint).Normalize();
        Connector targetFitting =
            fitted.RunA.CoordinateSystem.BasisZ.Normalize().DotProduct(targetDirection) >=
            fitted.RunB.CoordinateSystem.BasisZ.Normalize().DotProduct(targetDirection)
                ? fitted.RunA
                : fitted.RunB;
        Connector splitFitting = targetFitting.Id == fitted.RunA.Id
            ? fitted.RunB
            : fitted.RunA;

        MovePipeEnd(target, junctionPoint, targetFitting.Origin);
        MovePipeEnd(splitPipe, junctionPoint, splitFitting.Origin);
        if (connectBranch)
            MovePipeEnd(branch, junctionPoint, fitted.Branch.Origin);
        document.Regenerate();

        ConnectCoincident(document, target, targetFitting);
        ConnectCoincident(document, splitPipe, splitFitting);
        if (connectBranch)
            ConnectCoincident(document, branch, fitted.Branch);
        document.Regenerate();
        return junction;
    }

    private static void ApplyJunctionConnectorSizes(
        Document document,
        FamilyInstance junction,
        double mainDiameter,
        double branchDiameter)
    {
        JunctionConnectors connectors = JunctionConnectorsOf(junction);
        double tolerance = DrainGeometry.Mm(0.5);
        bool alreadySized =
            Math.Abs(connectors.RunA.Radius * 2.0 - mainDiameter) <= tolerance &&
            Math.Abs(connectors.RunB.Radius * 2.0 - mainDiameter) <= tolerance &&
            Math.Abs(connectors.Branch.Radius * 2.0 - branchDiameter) <= tolerance;
        if (alreadySized)
            return;

        try
        {
            connectors.RunA.Radius = mainDiameter * 0.5;
            connectors.RunB.Radius = mainDiameter * 0.5;
            connectors.Branch.Radius = branchDiameter * 0.5;
            document.Regenerate();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The routed Y family could not accept run DN{DrainGeometry.ToMm(mainDiameter):0.#} " +
                $"and branch DN{DrainGeometry.ToMm(branchDiameter):0.#}. {exception.Message}",
                exception);
        }

        JunctionConnectors verified = JunctionConnectorsOf(junction);
        double runA = verified.RunA.Radius * 2.0;
        double runB = verified.RunB.Radius * 2.0;
        double branch = verified.Branch.Radius * 2.0;
        if (Math.Abs(runA - mainDiameter) > tolerance ||
            Math.Abs(runB - mainDiameter) > tolerance ||
            Math.Abs(branch - branchDiameter) > tolerance)
            throw new InvalidOperationException(
                "The routed Y family did not resolve its reducing connector sizes. " +
                $"Required: run DN{DrainGeometry.ToMm(mainDiameter):0.#}/" +
                $"DN{DrainGeometry.ToMm(mainDiameter):0.#}, " +
                $"branch DN{DrainGeometry.ToMm(branchDiameter):0.#}. " +
                $"Resolved: run DN{DrainGeometry.ToMm(runA):0.#}/" +
                $"DN{DrainGeometry.ToMm(runB):0.#}, " +
                $"branch DN{DrainGeometry.ToMm(branch):0.#}.");
    }

    private static JunctionConnectors JunctionConnectorsOf(FamilyInstance junction)
    {
        IReadOnlyList<Connector> connectors = DrainSelection.GetConnectors(junction);
        if (connectors.Count < 3)
            throw new InvalidOperationException(
                "The routed Y family has fewer than three piping connectors.");
        (int first, int second) = MostOppositeConnectorPair(connectors);
        Connector branch = connectors
            .Where((_, index) => index != first && index != second)
            .OrderByDescending(item => item.Radius)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The branch connector of the routed Y family could not be identified.");
        return new JunctionConnectors(connectors[first], connectors[second], branch);
    }

    private static XYZ PipeDirectionAwayFrom(Pipe pipe, XYZ junctionPoint)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.Value} has no usable centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        return start.DistanceTo(junctionPoint) <= end.DistanceTo(junctionPoint)
            ? end - junctionPoint
            : start - junctionPoint;
    }

    private static void RotateVectorToVector(
        Document document,
        ElementId elementId,
        XYZ origin,
        XYZ from,
        XYZ to)
    {
        XYZ a = from.Normalize();
        XYZ b = to.Normalize();
        double dot = Math.Max(-1.0, Math.Min(1.0, a.DotProduct(b)));
        double angle = Math.Acos(dot);
        if (angle <= 1e-8) return;

        XYZ axis = a.CrossProduct(b);
        if (axis.GetLength() <= 1e-8)
        {
            XYZ helper = Math.Abs(a.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            axis = a.CrossProduct(helper);
        }
        ElementTransformUtils.RotateElement(
            document,
            elementId,
            Line.CreateUnbound(origin, axis.Normalize()),
            angle);
    }

    private static XYZ PerpendicularComponent(XYZ vector, XYZ axis)
    {
        XYZ unitAxis = axis.Normalize();
        XYZ perpendicular = vector - unitAxis * vector.DotProduct(unitAxis);
        if (perpendicular.GetLength() <= 1e-8)
            throw new InvalidOperationException(
                "The Y branch axis cannot be rolled around the selected main.");
        return perpendicular.Normalize();
    }

    private static double SignedAngle(XYZ from, XYZ to, XYZ axis)
    {
        XYZ a = from.Normalize();
        XYZ b = to.Normalize();
        XYZ n = axis.Normalize();
        return Math.Atan2(n.DotProduct(a.CrossProduct(b)), a.DotProduct(b));
    }

    private static XYZ ClosestAxisIntersection(
        XYZ firstOrigin,
        XYZ firstDirection,
        XYZ secondOrigin,
        XYZ secondDirection)
    {
        XYZ u = firstDirection.Normalize();
        XYZ v = secondDirection.Normalize();
        XYZ w = firstOrigin - secondOrigin;
        double uv = u.DotProduct(v);
        double denominator = 1.0 - uv * uv;
        if (Math.Abs(denominator) <= 1e-8)
            throw new InvalidOperationException(
                "The routed Y run and branch connector axes are parallel.");
        double uParameter = (uv * v.DotProduct(w) - u.DotProduct(w)) / denominator;
        double vParameter = (v.DotProduct(w) - uv * u.DotProduct(w)) / denominator;
        XYZ firstPoint = firstOrigin + u * uParameter;
        XYZ secondPoint = secondOrigin + v * vParameter;
        if (firstPoint.DistanceTo(secondPoint) > DrainGeometry.Mm(3))
            throw new InvalidOperationException(
                "The routed Y family connector axes do not meet at one junction center.");
        return (firstPoint + secondPoint) * 0.5;
    }

    private static void MovePipeEnd(Pipe pipe, XYZ oldPoint, XYZ newPoint)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.Value} has no usable centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        if (start.DistanceTo(oldPoint) <= DrainGeometry.Mm(3))
            location.Curve = Line.CreateBound(newPoint, end);
        else if (end.DistanceTo(oldPoint) <= DrainGeometry.Mm(3))
            location.Curve = Line.CreateBound(start, newPoint);
        else
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.Value} has no endpoint at the Y junction center.");
    }

    private static void ConnectCoincident(
        Document document,
        Pipe pipe,
        Connector fittingConnector)
    {
        Connector pipeConnector = DrainSelection.ConnectorNear(
            pipe,
            fittingConnector.Origin,
            true);
        pipeConnector.ConnectTo(fittingConnector);
        document.Regenerate();
        if (!pipeConnector.IsConnected || !fittingConnector.IsConnected)
            throw new InvalidOperationException(
                $"Revit did not connect pipe {pipe.Id.Value} to the placed Y family.");
    }

    private static Pipe CreatePipe(Document document, Context context, XYZ start, XYZ end)
        => CreatePipe(
            document,
            context,
            context.BranchPipeType.Id,
            context.BranchDiameter,
            start,
            end);

    private static Pipe CreatePipe(
        Document document,
        Context context,
        ElementId pipeTypeId,
        double diameter,
        XYZ start,
        XYZ end)
    {
        Pipe pipe = Pipe.Create(
            document,
            context.SystemTypeId,
            pipeTypeId,
            context.LevelId,
            start,
            end);
        SetDiameter(pipe, diameter);
        return pipe;
    }

    private static void SetDiameter(Pipe pipe, double diameterValue)
    {
        Parameter? diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (diameter is { IsReadOnly: false })
            diameter.Set(diameterValue);
    }

    private static void ConnectSource(
        Document document,
        Connector source,
        Pipe stub,
        DrainRoute route)
    {
        source.ConnectTo(DrainSelection.ConnectorNear(stub, route.DrainOrigin, true));
        document.Regenerate();
    }

    private static FamilyInstance? ConnectCase04Source(
        Document document,
        Connector source,
        Pipe stub,
        DrainRoute route)
    {
        Connector pipeConnector = DrainSelection.ConnectorNear(
            stub,
            route.DrainOrigin,
            true);
        if (Math.Abs(source.Radius - pipeConnector.Radius) <= DrainGeometry.Mm(0.25))
        {
            source.ConnectTo(pipeConnector);
            document.Regenerate();
            return null;
        }

        FamilyInstance transition = document.Create.NewTransitionFitting(
            source,
            pipeConnector);
        document.Regenerate();
        return transition;
    }

    private static void ConfigureFailureHandling(
        Transaction transaction,
        Action<string> report)
    {
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new DrainFailurePreprocessor(report));
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
    }

    private static (XYZ Inside, XYZ Endpoint) ResolveOpenMainEndpoint(
        Context context,
        XYZ? preferredPoint)
    {
        if (context.Target.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "The selected main has no usable centerline for Case 04.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        XYZ endpointPreference = preferredPoint ?? context.SourceConnector.Origin;
        Connector? openEnd = DrainSelection.GetOpenRoundConnectors(context.Target)
            .Where(connector =>
                connector.Origin.DistanceTo(start) <= DrainGeometry.Mm(3) ||
                connector.Origin.DistanceTo(end) <= DrainGeometry.Mm(3))
            .OrderBy(connector => PlanDistance(
                connector.Origin,
                endpointPreference))
            .FirstOrDefault();
        if (openEnd is null)
            throw new InvalidOperationException(
                "Case 04 requires an open connector at one end of the selected main.");
        bool atStart = openEnd.Origin.DistanceTo(start) <= openEnd.Origin.DistanceTo(end);
        return atStart ? (end, start) : (start, end);
    }

    private static double PlanDistance(XYZ first, XYZ second)
    {
        double dx = first.X - second.X;
        double dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Context Case04MainSizedContext(Context context) =>
        context with
        {
            BranchPipeType = context.TargetPipeType,
            BranchDiameter = context.MainDiameter
        };

    private static double EndpointSlopePercent(XYZ inside, XYZ endpoint)
    {
        double dx = endpoint.X - inside.X;
        double dy = endpoint.Y - inside.Y;
        double planLength = Math.Sqrt(dx * dx + dy * dy);
        if (planLength <= 1e-9)
            throw new InvalidOperationException(
                "Case 04 requires a main pipe with a usable plan direction.");
        return (endpoint.Z - inside.Z) / planLength * 100.0;
    }

    private static Context Resolve(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Element source = document.GetElement(sourceId)
            ?? throw new InvalidOperationException("Select a source drain.");
        Pipe target = document.GetElement(targetId) as Pipe
            ?? throw new InvalidOperationException("Select a horizontal target Pipe.");
        if (!DrainSelection.IsHorizontalPipe(target))
            throw new InvalidOperationException("The target pipe is vertical.");
        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        PipeType branchType = document.GetElement(settings.BranchPipeTypeId) as PipeType
            ?? throw new InvalidOperationException("Select a valid Branch Pipe Type.");
        if (target.Location is not LocationCurve location)
            throw new InvalidOperationException("The target has no LocationCurve.");

        Parameter systemParameter = target.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
            ?? throw new InvalidOperationException("Target Piping System Type is unavailable.");
        ElementId systemTypeId = systemParameter.AsElementId();
        ElementId levelId = target.ReferenceLevel?.Id ?? target.LevelId;
        if (levelId == ElementId.InvalidElementId)
            throw new InvalidOperationException("Target reference level is unavailable.");

        double branchDiameter = sourceConnector.Radius * 2;
        double mainDiameter =
            target.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
        if (branchDiameter <= 0 || mainDiameter <= 0)
            throw new InvalidOperationException("Pipe diameters could not be resolved.");

        return new Context(
            source,
            sourceConnector,
            target,
            document.GetElement(target.GetTypeId()) as PipeType
                ?? throw new InvalidOperationException("Target Pipe Type is unavailable."),
            branchType,
            branchDiameter,
            mainDiameter,
            systemTypeId,
            levelId,
            location.Curve.GetEndPoint(0),
            location.Curve.GetEndPoint(1));
    }

    private sealed record Context(
        Element Source,
        Connector SourceConnector,
        Pipe Target,
        PipeType TargetPipeType,
        PipeType BranchPipeType,
        double BranchDiameter,
        double MainDiameter,
        ElementId SystemTypeId,
        ElementId LevelId,
        XYZ MainStart,
        XYZ MainEnd);

    private sealed record ApproachPipes(Pipe Stub, Pipe Diagonal);

    private sealed record JunctionSeed(Pipe Pipe, FamilyInstance Transition);

    private sealed record JunctionConnectors(
        Connector RunA,
        Connector RunB,
        Connector Branch);

    private sealed record PreparedJunctionType(
        ElementId TypeId,
        ElementId PartId,
        string Label,
        double FittingAngleDegrees);

    private sealed record JunctionRuleCandidate(
        int RuleIndex,
        ElementId PartId,
        string Label);

    private sealed record JunctionOrientation(
        bool SwapRunConnectors,
        string Label);

    private sealed class DrainFailurePreprocessor(Action<string> report)
        : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                report(failure.GetDescriptionText());
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(failure);
                else
                    hasError = true;
            }

            return hasError
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }
}
