using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace FamilyMEP.Plugin.DrainConnection;

internal static class DrainRoutingService
{
    private const double TargetYAngleDegrees = 45.0;
    private const double YAngleToleranceDegrees = 1.0;
    private const double PipePlanAngleToleranceDegrees = 0.01;
    private const double MinimumYAngleDegrees =
        TargetYAngleDegrees - YAngleToleranceDegrees;
    private const double MaximumYAngleDegrees =
        TargetYAngleDegrees + YAngleToleranceDegrees;

    public static IReadOnlyList<PipeTypeItem> FindCompatibleLoadedYTypes(
        Document document)
    {
        IReadOnlyList<FamilySymbol> symbols = new FilteredElementCollector(document)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_PipeFitting)
            .Cast<FamilySymbol>()
            .ToList();
        var anglesBySymbolId = new Dictionary<long, double>();
        var anglesRequiringMeasurement = new HashSet<long>();

        var routedJunctionIds = new HashSet<long>();
        foreach (PipeType pipeType in new FilteredElementCollector(document)
                     .OfClass(typeof(PipeType))
                     .Cast<PipeType>())
        {
            RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
            int count = manager.GetNumberOfRules(
                RoutingPreferenceRuleGroupType.Junctions);
            for (int index = 0; index < count; index++)
            {
                ElementId partId = manager.GetRule(
                    RoutingPreferenceRuleGroupType.Junctions,
                    index).MEPPartId;
                if (partId != ElementId.InvalidElementId)
                    routedJunctionIds.Add(partId.CompatValue());
            }
        }

        // Read already placed fittings once. Opening the tool must never place
        // hundreds of temporary instances or open every family document; that
        // made large Revit 2020 projects appear frozen for several minutes.
        var inspectedInstanceSymbolIds = new HashSet<long>();
        foreach (FamilyInstance instance in new FilteredElementCollector(document)
                     .OfClass(typeof(FamilyInstance))
                     .OfCategory(BuiltInCategory.OST_PipeFitting)
                     .Cast<FamilyInstance>())
        {
            long symbolId = instance.Symbol.Id.CompatValue();
            if (!inspectedInstanceSymbolIds.Add(symbolId) ||
                anglesBySymbolId.ContainsKey(symbolId))
                continue;
            double instanceAngle = MeasureJunctionConnectorAngle(
                DrainSelection.GetConnectors(instance));
            if (IsSupportedYAngle(instanceAngle))
                anglesBySymbolId[symbolId] = instanceAngle;
        }

        foreach (FamilySymbol symbol in symbols)
        {
            long symbolId = symbol.Id.CompatValue();
            if (anglesBySymbolId.ContainsKey(symbolId) ||
                IsUnsafeMultiBranchName(symbol))
                continue;

            bool routedJunction = routedJunctionIds.Contains(symbolId);
            bool likelyY = IsLikelyYName(symbol);
            if (!routedJunction && !likelyY)
                continue;

            double parameterAngle = ResolveAngleParameterDegrees(symbol);
            if (IsSupportedYAngle(parameterAngle))
                anglesBySymbolId[symbolId] = parameterAngle;
            else if (routedJunction || likelyY)
                // Some protected/vendor Y families expose neither an instance
                // angle nor an editable type angle. Keep them selectable with
                // a preview-only seed; Create measures the real three-connector
                // geometry before any branch pipe is committed.
            {
                anglesBySymbolId[symbolId] = 45.0;
                anglesRequiringMeasurement.Add(symbolId);
            }
        }

        return symbols
            .Where(symbol => anglesBySymbolId.ContainsKey(symbol.Id.CompatValue()))
            .Select(symbol =>
            {
                long symbolId = symbol.Id.CompatValue();
                bool measureOnCreate = anglesRequiringMeasurement.Contains(symbolId);
                double angle = anglesBySymbolId[symbolId];
                string suffix = measureOnCreate
                    ? "— Y angle measured from connectors on Create"
                    : $"— Y {angle:0.###}°";
                return new PipeTypeItem(
                    symbol.Id,
                    $"{symbol.FamilyName}: {symbol.Name} {suffix}",
                    angle,
                    measureOnCreate);
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsUnsafeMultiBranchName(FamilySymbol symbol)
    {
        string name = $"{symbol.FamilyName} {symbol.Name}";
        return name.Contains("double", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("cross", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("pants", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyYName(FamilySymbol symbol)
    {
        string name = $"{symbol.FamilyName} {symbol.Name}";
        return name.Contains("wye", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("lateral", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("branch", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("junction", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("sanitary tee", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("tee sanitary", StringComparison.OrdinalIgnoreCase);
    }

    private static double MeasureJunctionConnectorAngle(
        IReadOnlyList<Connector> connectors)
    {
        if (connectors.Count != 3)
            return double.NaN;
        (int first, int second) = MostOppositeConnectorPair(connectors);
        XYZ firstDirection = connectors[first].CoordinateSystem.BasisZ.Normalize();
        XYZ secondDirection = connectors[second].CoordinateSystem.BasisZ.Normalize();
        if (Math.Abs(firstDirection.DotProduct(secondDirection)) < 0.99)
            return double.NaN;
        Connector branch = connectors
            .Where((_, index) => index != first && index != second)
            .Single();
        return Math.Min(
            AcuteDegrees(branch.CoordinateSystem.BasisZ, firstDirection),
            AcuteDegrees(branch.CoordinateSystem.BasisZ, secondDirection));
    }

    private static IReadOnlyDictionary<string, double>
        ReadJunctionAnglesFromFamilyDefinition(
            Document projectDocument,
            Family family,
            IReadOnlyList<FamilySymbol> requestedSymbols)
    {
        var result = new Dictionary<string, double>(
            StringComparer.CurrentCultureIgnoreCase);
        if (!family.IsEditable || family.IsInPlace)
            return result;

        Document? familyDocument = null;
        try
        {
            familyDocument = projectDocument.EditFamily(family);
            FamilyManager manager = familyDocument.FamilyManager;
            IReadOnlyList<ConnectorElement> connectors =
                new FilteredElementCollector(familyDocument)
                    .OfClass(typeof(ConnectorElement))
                    .Cast<ConnectorElement>()
                    .Where(connector =>
                        connector.Domain == Domain.DomainPiping &&
                        connector.Shape == ConnectorProfileType.Round)
                    .ToList();
            if (connectors.Count != 3)
                return result;

            using var selectType = new Transaction(
                familyDocument,
                "Inspect drain Y connector geometry");
            selectType.Start();
            try
            {
                IReadOnlyList<FamilyType> familyTypes = manager.Types
                    .Cast<FamilyType>()
                    .ToList();
                foreach (FamilySymbol requested in requestedSymbols)
                {
                    FamilyType? familyType = familyTypes.FirstOrDefault(type =>
                        type.Name.Equals(
                            requested.Name,
                            StringComparison.CurrentCultureIgnoreCase));
                    if (familyType is null)
                        continue;
                    manager.CurrentType = familyType;
                    familyDocument.Regenerate();
                    double angle = MeasureJunctionDirections(
                        connectors.Select(connector =>
                            connector.CoordinateSystem.BasisZ).ToList());
                    if (!double.IsNaN(angle))
                        result[requested.Name] = angle;
                }
            }
            finally
            {
                if (selectType.GetStatus() == TransactionStatus.Started)
                    selectType.RollBack();
            }
        }
        catch
        {
            // Some protected/vendor families cannot be opened. Existing
            // instances and placement probes remain the first two strategies.
        }
        finally
        {
            familyDocument?.Close(false);
        }
        return result;
    }

    private static double ResolveAngleParameterDegrees(FamilySymbol symbol)
    {
        foreach (Parameter parameter in symbol.Parameters)
        {
            if (parameter.StorageType != StorageType.Double ||
                !parameter.HasValue ||
                !parameter.Definition.GetDataType().Equals(SpecTypeId.Angle))
                continue;
            double degrees = parameter.AsDouble() * 180.0 / Math.PI;
            if (degrees is >= 1.0 and <= 90.001)
                return degrees;
        }
        return double.NaN;
    }

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
            context.MainDiameter,
            settings.JunctionAngles);
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
            context.MainDiameter,
            settings.JunctionAngles);
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
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 03 route fits. The device must be above the main in plan and have enough vertical clearance for the standing pipe, routed elbow, and selected-Y diagonal leg.");
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
                "No Case 04 route fits this endpoint or trim point. Check that the device is high enough above the main for the compact 45 degree fittings.");
        return routes;
    }

    public static IReadOnlyList<DrainRoute> PreviewCase06(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        return [BuildCase06ReviewRoute(context, settings)];
    }

    public static IReadOnlyList<DrainRoute> PreviewCase05(
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
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase05Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No Case 05 endpoint elbow route fits. Pick near an open main end with enough outward length for the 45 degree connection.");
        return routes;
    }

    public static DrainOperationResult Create(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        settings = ValidateSelectedYBeforeStage1(
            document, settings, context.TargetPipeType);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCandidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter,
            settings.JunctionAngles);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe device-to-main route fits after trying every compatible " +
                $"Y angle ({string.Join(", ", settings.JunctionAngles ?? [])}) and compact " +
                "takeout from 0.35x to 4x branch DN. Check the drain-connection log for " +
                "the source/main elevations and picked point.");

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
                    failures, settings);
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
        settings = ValidateSelectedYBeforeStage1(
            document, settings, context.TargetPipeType);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase02Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter,
            settings.JunctionAngles);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe Case 02 route fits the selected drain and main.");

        foreach (DrainRoute route in routes)
        {
            double nearMainLength = route.NearMainElbow is null
                ? 0.0
                : PlanDistance(route.NearMainElbow, route.WyePoint);
            string candidateLabel =
                $"compact {DrainGeometry.ToMm(route.CompactOffset):0.#} mm / " +
                $"near-main {DrainGeometry.ToMm(nearMainLength):0.#} mm";
            using var attempt = new Transaction(
                document,
                "Case 02 create elbow-offset branch");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 02 {candidateLabel}: {failure}"));
            try
            {
                DrainOperationResult stage1 = CreateSplitSlopeBranch(
                    document,
                    context,
                    route);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add($"Case 02 {candidateLabel}: Revit rolled back the branch candidate.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    stage1,
                    failures, settings);
            }
            catch (Exception exception)
            {
                failures.Add($"Case 02 {candidateLabel}: {exception.Message}");
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
        settings = ValidateSelectedYBeforeStage1(
            document, settings, context.TargetPipeType);
        var failures = new List<string>();
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase03Candidates(
            context.MainStart,
            context.MainEnd,
            context.SourceConnector.Origin,
            settings,
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
                    failures, settings);
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
                    route,
                    DrainGeometry.Mm(settings.Case04MiddlePipeLengthMm),
                    sourceId,
                    targetId);
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

    public static DrainOperationResult CreateCase06(
        Document document,
        ElementId sourceId,
        ElementId targetId,
        DrainSettings settings)
    {
        Context context = Resolve(document, sourceId, targetId, settings);
        settings = ValidateSelectedYBeforeStage1(
            document,
            settings,
            context.TargetPipeType);
        context = Resolve(document, sourceId, targetId, settings);
        IReadOnlyList<DrainRoute> routes = [BuildCase06ReviewRoute(context, settings)];
        var failures = new List<string>();
        foreach (DrainRoute route in routes)
        {
            using var stage1Transaction = new Transaction(
                document,
                "Case 06 create connected branch to main");
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
                        $"Offset {DrainGeometry.ToMm(route.CompactOffset):0} mm: Revit rolled back the Case 06 connected branch.");
                    continue;
                }
                return CompleteWithRoutedY(
                    document,
                    context,
                    stage1,
                    failures,
                    settings);
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
            "Revit could not create the Case 06 elbows and routed Y on the approved pipe layout." +
            Environment.NewLine + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                failures.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct()
                    .TakeLast(8)));
    }

    private static DrainOperationResult CreateCase06PipeLayout(
        Document document,
        Context context,
        DrainRoute route)
    {
        XYZ aEnd = route.NearMainElbow
            ?? throw new InvalidOperationException(
                "Case 06 did not calculate the end of parallel pipe A.");
        var ids = new List<ElementId>();
        Pipe vertical = CreatePipe(
            document,
            context,
            route.DrainOrigin,
            route.StubEnd);
        Pipe doubleElbowMiddle = CreatePipe(
            document,
            context,
            route.StubEnd,
            route.DiagonalEnd);
        Pipe pipeA = CreatePipe(
            document,
            context,
            route.DiagonalEnd,
            aEnd);
        Pipe pipeB = CreatePipe(
            document,
            context,
            aEnd,
            route.WyePoint);
        ids.AddRange([
            vertical.Id,
            doubleElbowMiddle.Id,
            pipeA.Id,
            pipeB.Id]);
        document.Regenerate();

        return new DrainOperationResult(
            true,
            false,
            "Case 06 pipe layout created to the picked main point. " +
            "No elbows, main break, or Y fitting were created; inspect the route first.",
            route,
            ids);
    }

    private static DrainRoute BuildCase06ReviewRoute(
        Context context,
        DrainSettings settings)
    {
        XYZ source = context.SourceConnector.Origin;
        double mainX = context.MainEnd.X - context.MainStart.X;
        double mainY = context.MainEnd.Y - context.MainStart.Y;
        double mainPlanLength = Math.Sqrt(mainX * mainX + mainY * mainY);
        if (mainPlanLength <= 1e-9)
            throw new InvalidOperationException(
                "Case 06 requires a main pipe with a usable plan direction.");
        double ux = mainX / mainPlanLength;
        double uy = mainY / mainPlanLength;

        XYZ pickedPoint = ResolveConnectionPointOnMain(
            context.Target,
            settings.TargetPoint
            ?? throw new InvalidOperationException(
                "Pick the Case 06 direction point on the main."));
        double sourceAlong =
            (source.X - context.MainStart.X) * ux +
            (source.Y - context.MainStart.Y) * uy;
        double pickedAlong =
            (pickedPoint.X - context.MainStart.X) * ux +
            (pickedPoint.Y - context.MainStart.Y) * uy;
        double hand = Math.Sign(pickedAlong - sourceAlong);
        if (Math.Abs(hand) < 0.5)
        {
            double roomTowardEnd = mainPlanLength - sourceAlong;
            double roomTowardStart = sourceAlong;
            hand = roomTowardEnd >= roomTowardStart ? 1.0 : -1.0;
        }

        double projectedX = context.MainStart.X + ux * sourceAlong;
        double projectedY = context.MainStart.Y + uy * sourceAlong;
        double lateralX = source.X - projectedX;
        double lateralY = source.Y - projectedY;
        double lateralDistance = Math.Sqrt(
            lateralX * lateralX + lateralY * lateralY);
        if (lateralDistance <= DrainGeometry.Mm(25))
            throw new InvalidOperationException(
                "Case 06 needs the device to be offset from the main in plan.");

        double preferredCompactPlan = Math.Max(
            DrainGeometry.Mm(200),
            context.BranchDiameter * 2.0);
        double minimumCompactPlan = Math.Max(
            DrainGeometry.Mm(100),
            context.BranchDiameter);
        // Socket/cast-iron elbows can consume substantially more straight pipe
        // than UPVC elbows. Prefer a longer A segment when the selected main
        // has room, then shrink it only for genuinely short mains.
        double preferredPipeAPlan = Math.Max(
            DrainGeometry.Mm(600),
            context.BranchDiameter * 6.0);
        double minimumPipeAPlan = Math.Max(
            DrainGeometry.Mm(75),
            context.BranchDiameter);
        // For a true 45-degree B in plan, its along-main travel equals its
        // perpendicular travel to the main. This fixes the requested visual
        // direction explicitly and leaves no mirrored candidate to select.
        double endClearance = Math.Max(
            DrainGeometry.Mm(150),
            context.MainDiameter * 1.5);
        double availableAlong = hand > 0.0
            ? mainPlanLength - endClearance - sourceAlong
            : sourceAlong - endClearance;
        double requiredMinimumAlong =
            lateralDistance + minimumCompactPlan + minimumPipeAPlan;
        if (availableAlong < requiredMinimumAlong)
        {
            double oppositeHand = -hand;
            double oppositeAvailable = oppositeHand > 0.0
                ? mainPlanLength - endClearance - sourceAlong
                : sourceAlong - endClearance;
            if (oppositeAvailable >= requiredMinimumAlong)
            {
                hand = oppositeHand;
                availableAlong = oppositeAvailable;
            }
        }
        double compactPlan = Math.Min(
            preferredCompactPlan,
            availableAlong - lateralDistance - minimumPipeAPlan);
        if (compactPlan < minimumCompactPlan)
            throw new InvalidOperationException(
                "Case 06 has insufficient main length for the two device-side 45-degree elbows.");
        double pipeAPlan = Math.Min(
            preferredPipeAPlan,
            availableAlong - lateralDistance - compactPlan);
        if (pipeAPlan < minimumPipeAPlan)
            throw new InvalidOperationException(
                "Case 06 needs more main length in the selected direction for the parallel pipe and the 45-degree pipe into the main.");
        double mainAlong = sourceAlong + hand * (
            compactPlan + pipeAPlan + lateralDistance);

        double mainParameter = mainAlong / mainPlanLength;
        XYZ mainPoint = new(
            context.MainStart.X + ux * mainAlong,
            context.MainStart.Y + uy * mainAlong,
            context.MainStart.Z +
            (context.MainEnd.Z - context.MainStart.Z) * mainParameter);
        double slope = Math.Abs(settings.SlopePercent) / 100.0;
        double pipeBPlan = lateralDistance * Math.Sqrt(2.0);
        double aEndZ = mainPoint.Z + pipeBPlan * slope;
        double diagonalEndZ = aEndZ + pipeAPlan * slope;
        double verticalEndZ = diagonalEndZ + compactPlan;
        double minimumVertical = Math.Max(
            DrainGeometry.Mm(25),
            context.BranchDiameter * 0.25);
        if (source.Z - verticalEndZ < minimumVertical)
            throw new InvalidOperationException(
                "The device needs more vertical clearance for the Case 06 double-45 drop.");

        XYZ verticalEnd = new(source.X, source.Y, verticalEndZ);
        XYZ diagonalEnd = new(
            source.X + ux * hand * compactPlan,
            source.Y + uy * hand * compactPlan,
            diagonalEndZ);
        XYZ aEnd = new(
            diagonalEnd.X + ux * hand * pipeAPlan,
            diagonalEnd.Y + uy * hand * pipeAPlan,
            aEndZ);
        double cross = mainX * lateralY - mainY * lateralX;
        double junctionAngle = settings.JunctionAngles?
            .FirstOrDefault(angle => angle >= 20.0 && angle <= 75.0) ?? 45.0;

        return new DrainRoute(
            source,
            verticalEnd,
            diagonalEnd,
            mainPoint,
            context.MainStart,
            context.MainEnd,
            compactPlan,
            pipeAPlan + pipeBPlan,
            mainParameter,
            45.0,
            junctionAngle,
            Math.Abs(settings.SlopePercent),
            cross >= 0 ? DrainSide.Left : DrainSide.Right,
            aEnd,
            6);
    }

    public static DrainOperationResult CreateCase05(
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
            BranchPipeTypeId = context.TargetPipeType.Id
        };
        IReadOnlyList<DrainRoute> routes = DrainGeometry.BuildCase05Candidates(
            inside,
            endpoint,
            context.SourceConnector.Origin,
            settings,
            context.BranchDiameter,
            context.MainDiameter);
        if (routes.Count == 0)
            throw new InvalidOperationException(
                "No gravity-safe Case 05 route fits the selected drain and open main end.");

        var failures = new List<string>();
        foreach (DrainRoute route in routes)
        {
            using var attempt = new Transaction(
                document,
                "Case 05 connect endpoint with 45 degree elbow");
            attempt.Start();
            ConfigureFailureHandling(
                attempt,
                failure => failures.Add($"Case 05: {failure}"));
            try
            {
                Pipe target = document.GetElement(targetId) as Pipe
                    ?? throw new InvalidOperationException(
                        "The selected Case 05 main is no longer available.");
                DrainOperationResult result = CreateCase05EndpointElbow(
                    document,
                    context,
                    route,
                    target,
                    endpoint,
                    sourceId);
                if (attempt.Commit() != TransactionStatus.Committed)
                {
                    failures.Add("Case 05: Revit rolled back the endpoint elbow candidate.");
                    continue;
                }
                return result;
            }
            catch (Exception exception)
            {
                failures.Add($"Case 05: {exception.Message}");
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
            "Revit could not create the Case 05 endpoint 45 degree elbow connection." +
            (details.Length == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + details));
    }

    private static DrainOperationResult CreateCase05EndpointElbow(
        Document document,
        Context context,
        DrainRoute route,
        Pipe target,
        XYZ oldEndpoint,
        ElementId sourceId)
    {
        ValidateRouteFallsToMain(route, "Case 05 route");
        var ids = new List<ElementId>();
        string stage = "prepare the picked main endpoint";

        try
        {
            // A picked interior point becomes a new open end: retain the longer
            // main side and remove the short surplus tail. If the selected point
            // was already an open end this is a no-op.
            target = PreparePickedMainEndpoint(
                document,
                target,
                route.MainStart,
                oldEndpoint);

            // Stop the retained main exactly at the calculated Case-01-style
            // junction point. There is no continuation beyond this point and no Y.
            stage = "extend the retained main to the elbow point";
            target = ExtendOpenMainEndpoint(
                document,
                target,
                oldEndpoint,
                route.WyePoint);

            stage = "create the device-side pipes";
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
            ElementId stubId = approach.Stub.Id;
            ElementId diagonalId = approach.Diagonal.Id;
            ElementId branchId = branch.Id;
            ElementId targetPipeId = target.Id;
            ids.Add(branch.Id);
            document.Regenerate();

            stage = "connect the device reducer or leave its DN gap";
            Element source = document.GetElement(sourceId)
                ?? throw new InvalidOperationException(
                    "The Case 05 drain is no longer available.");
            Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
            SourceConnection sourceConnection = ConnectCase04Source(
                document,
                context,
                sourceConnector,
                approach.Stub,
                route);
            // A failed reducer probe rolls back a SubTransaction. Revit 2025
            // can invalidate every managed Pipe wrapper that participated in
            // that probe even though the underlying elements still exist.
            // Always reacquire the pipes before creating the elbows.
            approach = new ApproachPipes(
                RequirePipe(document, stubId, "Case 05 device stub"),
                RequirePipe(document, diagonalId, "Case 05 device diagonal"));
            branch = RequirePipe(document, branchId, "Case 05 branch");
            stage = "create the upper device-side 45 degree elbow";
            FamilyInstance source45A = document.Create.NewElbowFitting(
                DrainSelection.ConnectorNear(
                    approach.Stub, route.StubEnd, true),
                DrainSelection.ConnectorNear(
                    approach.Diagonal, route.StubEnd, true));
            approach = new ApproachPipes(
                RequirePipe(document, stubId, "Case 05 device stub"),
                RequirePipe(document, diagonalId, "Case 05 device diagonal"));
            branch = RequirePipe(document, branchId, "Case 05 sloped branch");
            target = RequirePipe(document, targetPipeId, "Case 05 retained main");
            stage = "create the lower device-side 45 degree elbow";
            FamilyInstance source45B = document.Create.NewElbowFitting(
                DrainSelection.ConnectorNear(
                    approach.Diagonal, route.DiagonalEnd, true),
                DrainSelection.ConnectorNear(
                    branch, route.DiagonalEnd, true));
            approach = new ApproachPipes(
                RequirePipe(document, stubId, "Case 05 device stub"),
                RequirePipe(document, diagonalId, "Case 05 device diagonal"));
            branch = RequirePipe(document, branchId, "Case 05 sloped branch");
            target = RequirePipe(document, targetPipeId, "Case 05 retained main");
            stage = "create the main endpoint 45 degree elbow";
            FamilyInstance main45 = CreateCase04Elbow(
                document,
                branch,
                target,
                route.WyePoint,
                "main endpoint 45 degree elbow",
                preservePipeAxes: true);
            ids.AddRange([source45A.Id, source45B.Id, main45.Id]);
            AddSourceConnectionIds(ids, sourceConnection);
            document.Regenerate();
            branch = RequirePipe(document, branchId, "Case 05 sloped branch");
            approach = new ApproachPipes(
                RequirePipe(document, stubId, "Case 05 device stub"),
                RequirePipe(document, diagonalId, "Case 05 device diagonal"));
            ValidateVerticalDeviceStub(approach.Stub, route.DrainOrigin);
            ValidateRequestedSlope(
                branch,
                route.SlopePercent,
                "Case 05 branch after elbow insertion",
                tolerancePercentagePoints: 0.01);
            ValidatePipeFallsTowardMain(
                approach.Stub, route.DrainOrigin, route.StubEnd,
                "Case 05 device drop");
            ValidatePipeFallsTowardMain(
                approach.Diagonal, route.StubEnd, route.DiagonalEnd,
                "Case 05 device-side diagonal");
            ValidatePipeFallsTowardMain(
                branch, route.DiagonalEnd, route.WyePoint,
                "Case 05 branch into main");
            ValidatePipeFallsAwayFromConnection(
                target,
                route.WyePoint,
                "Case 05 retained main after the endpoint elbow");

            string resultMessage = sourceConnection.HasOpenBreak
                ? "Case 05 created: the branch and main endpoint elbow were completed, but the " +
                  "device-to-main DN change is intentionally left open. " +
                  sourceConnection.Warning
                : "Case 05 created: Case-01-style gravity branch connected directly " +
                  "to the open main endpoint with a routing-preference 45 degree elbow. " +
                  "Main DN, Pipe Type, and slope were retained; no Y was created.";

            return new DrainOperationResult(
                true,
                false,
                resultMessage,
                route,
                ids);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 05 failed while trying to {stage}: {exception.Message}",
                exception);
        }
    }

    private static IReadOnlyList<DrainRoute> BuildCase06PipeOnlyRoutes(
        Context context,
        DrainSettings settings,
        double elbowAngleDegrees,
        bool useExactElbowGeometry = true)
    {
        XYZ source = context.SourceConnector.Origin;
        double mainX = context.MainEnd.X - context.MainStart.X;
        double mainY = context.MainEnd.Y - context.MainStart.Y;
        double mainPlanLength = Math.Sqrt(mainX * mainX + mainY * mainY);
        if (mainPlanLength <= 1e-9)
            throw new InvalidOperationException(
                "Case 06 requires a main pipe with a usable plan direction.");

        double ux = mainX / mainPlanLength;
        double uy = mainY / mainPlanLength;
        XYZ pickedMainPoint = ResolveConnectionPointOnMain(
            context.Target,
            settings.TargetPoint
            ?? throw new InvalidOperationException(
                "Pick the exact connection point on the main pipe for Case 06."));
        double sourceAlong =
            (source.X - context.MainStart.X) * ux +
            (source.Y - context.MainStart.Y) * uy;
        double projectedX = context.MainStart.X + ux * sourceAlong;
        double projectedY = context.MainStart.Y + uy * sourceAlong;
        double lateralX = source.X - projectedX;
        double lateralY = source.Y - projectedY;
        double lateralDistance = Math.Sqrt(
            lateralX * lateralX + lateralY * lateralY);

        // During the pipe-layout review, the click selects the along-main
        // direction rather than forcing an impossible nearby break point. A
        // 45-degree B needs approximately the lateral device-to-main distance
        // along the main, plus a visible positive length for parallel pipe A.
        // If the click is nearer than that, move the proposed break farther in
        // the clicked direction so A and B both continue the same way instead
        // of A overshooting and B doubling back.
        double pickedAlong =
            (pickedMainPoint.X - context.MainStart.X) * ux +
            (pickedMainPoint.Y - context.MainStart.Y) * uy;
        XYZ mainPoint = pickedMainPoint;
        if (!useExactElbowGeometry)
        {
            double direction = Math.Sign(pickedAlong - sourceAlong);
            if (Math.Abs(direction) < 0.5)
                direction = 1.0;
            double reviewMinimumAPlan = Math.Max(
                DrainGeometry.Mm(300),
                context.BranchDiameter * 3.0);
            double requiredAlong = lateralDistance + reviewMinimumAPlan;
            double proposedAlong = sourceAlong + direction * Math.Max(
                Math.Abs(pickedAlong - sourceAlong),
                requiredAlong);
            double mainEndClearance = Math.Max(
                DrainGeometry.Mm(100),
                context.MainDiameter);
            if (proposedAlong <= mainEndClearance ||
                proposedAlong >= mainPlanLength - mainEndClearance)
                throw new InvalidOperationException(
                    "Case 06 needs more main length in the clicked direction for parallel pipe A and the 45-degree diagonal B.");
            double proposedParameter = proposedAlong / mainPlanLength;
            mainPoint = new XYZ(
                context.MainStart.X + ux * proposedAlong,
                context.MainStart.Y + uy * proposedAlong,
                context.MainStart.Z +
                (context.MainEnd.Z - context.MainStart.Z) * proposedParameter);
        }

        double sourceSlope = settings.SlopePercent / 100.0;
        double planFactor = 1.0 / Math.Sqrt(1.0 + sourceSlope * sourceSlope);
        double junctionAngleDegrees = settings.JunctionAngles?
            .FirstOrDefault(angle => angle >= 20.0 && angle <= 75.0) ?? 45.0;
        // Pipe A and pipe B meet through an elbow. Their geometry must follow
        // the elbow angle, independently from the selected Y's branch angle.
        // For the zero-roll layout both pipes have the entered gravity slope,
        // so a 45 degree plan turn is slightly more than 45 degrees in 3D.
        // Compensate the plan angle so Revit sees an exact 45 degree connector
        // angle and can resolve strict socket-elbow families.
        if (elbowAngleDegrees <= 5.0 || elbowAngleDegrees >= 85.0)
            throw new InvalidOperationException(
                $"Case 06 elbow connector angle {elbowAngleDegrees:0.###} degrees is not usable.");
        double elbowAngle = elbowAngleDegrees * Math.PI / 180.0;
        double rollDegrees = settings.YRollAngleDegrees;
        if (rollDegrees < 0.0 || rollDegrees >= 89.9)
            throw new InvalidOperationException(
                "Case 06 Y Up-Roll must be from 0 degrees up to, but not including, 89.9 degrees.");
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
        double preferredMainHand = Math.Sign(selectedAlong - sourceAlong);
        if (Math.Abs(preferredMainHand) < 0.5)
            preferredMainHand = 1.0;
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
            // Solve the short pipe between the two source elbows from the real
            // connector angle. A nominal 45-degree cast-iron elbow in this
            // project has 47-degree connector axes, so a planar 45-degree
            // bisector is rejected by Revit. The lateral component below is
            // the smallest roll that lets the same real elbow connect both the
            // vertical drop and sloped pipe A while A remains parallel to main.
            double elbowCosine = Math.Cos(elbowAngle);
            double approachHorizontal = Math.Sin(elbowAngle);
            double routeAPlanMagnitude = Math.Sqrt(
                routeA.X * routeA.X + routeA.Y * routeA.Y);
            if (routeAPlanMagnitude <= 1e-9)
                continue;
            double routeAUnitX = routeA.X / routeAPlanMagnitude;
            double routeAUnitY = routeA.Y / routeAPlanMagnitude;
            double approachAlongA =
                (elbowCosine + elbowCosine * routeA.Z) /
                routeAPlanMagnitude;
            double approachLateralSquared =
                approachHorizontal * approachHorizontal -
                approachAlongA * approachAlongA;
            if (approachLateralSquared < -1e-9)
                continue;
            double approachLateral = Math.Sqrt(Math.Max(
                0.0,
                approachLateralSquared));

            IEnumerable<double> approachHands = useExactElbowGeometry
                ? new[] { 1.0, -1.0 }
                : new[] { 1.0 };
            foreach (double approachHand in approachHands)
            {
                double normalX = -routeAUnitY * approachHand;
                double normalY = routeAUnitX * approachHand;
                XYZ approachAxis = useExactElbowGeometry
                    ? new XYZ(
                        routeAUnitX * approachAlongA + normalX * approachLateral,
                        routeAUnitY * approachAlongA + normalY * approachLateral,
                        -elbowCosine).Normalize()
                    // Geometry-review route: keep the double-elbow middle pipe
                    // in A's vertical plane so TOP view clearly shows the
                    // requested vertical drop -> parallel A -> 45-degree B.
                    : (-XYZ.BasisZ + routeA).Normalize();

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
                        // slope. B leaves the A/B elbow back toward the main,
                        // so its along-main component is opposite route A. The
                        // old +A component made the two rays leaving the elbow
                        // about 135 degrees apart and Revit rejected the fitting.
                        double perpendicularX = -aPlanY * branchHand;
                        double perpendicularY = aPlanX * branchHand;
                        double slopeSquared = sourceSlope * sourceSlope;
                        double compensatedPlanCosine =
                            Math.Cos(elbowAngle) * (1.0 + slopeSquared) +
                            slopeSquared;
                        if (compensatedPlanCosine < -1.0 ||
                            compensatedPlanCosine > 1.0)
                            continue;
                        double compensatedPlanAngle = Math.Acos(
                            Math.Max(-1.0, Math.Min(1.0, compensatedPlanCosine)));
                        double bPlanX =
                            -aPlanX * Math.Cos(compensatedPlanAngle) +
                            perpendicularX * Math.Sin(compensatedPlanAngle);
                        double bPlanY =
                            -aPlanY * Math.Cos(compensatedPlanAngle) +
                            perpendicularY * Math.Sin(compensatedPlanAngle);
                            routeB = new XYZ(
                                bPlanX * planFactor,
                                bPlanY * planFactor,
                                -sourceSlope * planFactor).Normalize();
                        }
                        else
                        {
                        // For a user roll, B is no longer slope-controlled. Its
                        // base direction is back toward the main (-A), then the
                        // selected Y branch is rolled around the main axis.
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
                                -routeA * Math.Cos(elbowAngle) +
                                rolledDown * Math.Sin(elbowAngle)).Normalize();
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
                            elbowAngleDegrees,
                            junctionAngleDegrees,
                            settings.SlopePercent,
                            cross >= 0 ? DrainSide.Left : DrainSide.Right,
                            pipeAEnd,
                            6));
                    }
                }
            }
        }
        if (routes.Count == 0)
        {
            double availableDrop = source.Z - mainPoint.Z;
            double usableRollDrop = Math.Max(
                0.0,
                availableDrop - preferredOffset - minimumVerticalLength);
            double maximumRoll = lateralDistance <= 1e-9
                ? 89.8
                : Math.Atan(usableRollDrop / lateralDistance) * 180.0 / Math.PI;
            throw new InvalidOperationException(
                rollDegrees <= 1e-8
                    ? "The selected point cannot fit Case 06 after trying A in both main directions and B toward every Y side. Check the device elevation and fitting takeout."
                    : $"The selected point cannot fit Case 06 with Y Up-Roll {rollDegrees:0.###} degrees. " +
                      $"The available geometry supports at most about {maximumRoll:0.#} degrees " +
                      $"(available drop {DrainGeometry.ToMm(availableDrop):0} mm, lateral offset {DrainGeometry.ToMm(lateralDistance):0} mm). " +
                      "Reduce Y Up-Roll or pick a point/main with more vertical clearance.");
        }
        // In TOP view A is the segment parallel to the main and B is the
        // diagonal into the picked point. Prefer the longest feasible A (and
        // therefore the shortest B). Sorting by the shortest vertical stub
        // previously selected the opposite visual result: tiny A, very long B.
        return routes
            // Pipe A must leave the device in the along-main direction toward
            // the picked break point. The previous longest-A preference could
            // select the opposite hand, visibly sending A the wrong way before
            // B doubled back to the main.
            .OrderBy(route =>
            {
                XYZ a = (route.NearMainElbow
                    ?? throw new InvalidOperationException("Case 06 route has no A/B point.")) -
                    route.DiagonalEnd;
                double alongMain = a.X * ux + a.Y * uy;
                return alongMain * preferredMainHand >= 0.0 ? 0 : 1;
            })
            // After A has taken the correct along-main hand, choose the 45
            // diagonal B that continues toward the picked point. The mirrored
            // candidate lets A overshoot the break and makes B double back in
            // the opposite direction, which is the reversed layout reported
            // by the user.
            .ThenBy(route =>
            {
                XYZ b = route.WyePoint - (route.NearMainElbow
                    ?? throw new InvalidOperationException("Case 06 route has no A/B point."));
                double alongMain = b.X * ux + b.Y * uy;
                return alongMain * preferredMainHand >= 0.0 ? 0 : 1;
            })
            // The elbow and Y can have different real connector angles even
            // when both families are sold as nominal 45-degree fittings. Try
            // the main-hand candidate whose B axis best matches the measured
            // Y branch axis before considering visual length preferences.
            .ThenBy(route =>
            {
                XYZ b = route.WyePoint - (route.NearMainElbow
                    ?? throw new InvalidOperationException("Case 06 route has no A/B point."));
                XYZ main = route.MainEnd - route.MainStart;
                double cosine = Math.Abs(
                    b.Normalize().DotProduct(main.Normalize()));
                cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
                double angle = Math.Acos(cosine) * 180.0 / Math.PI;
                return Math.Abs(angle - route.FittingAngleDegrees);
            })
            .ThenByDescending(route => PlanDistance(
                route.DiagonalEnd,
                route.NearMainElbow
                ?? throw new InvalidOperationException("Case 06 route has no A/B point.")))
            .ThenBy(route => PlanDistance(
                route.NearMainElbow
                ?? throw new InvalidOperationException("Case 06 route has no A/B point."),
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
            null => $"missing part {rule.MEPPartId.CompatValue()}",
            _ => part.Name
        };
    }

    private static DrainOperationResult CreateBranchOnly(
        Document document,
        Context context,
        DrainRoute route)
    {
        ValidateRouteFallsToMain(route, $"Case {route.CaseNumber:00} route");
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
        SourceConnection? sourceConnection = null;
        if (route.CaseNumber == 5)
        {
            sourceConnection = ConnectCase04Source(
                document,
                context,
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
        if (sourceConnection is not null)
            AddSourceConnectionIds(ids, sourceConnection);
        document.Regenerate();
        ValidateRequestedSlope(branch, route.SlopePercent, "Case 01 branch");
        ValidatePipeFallsTowardMain(
            approach.Stub, route.DrainOrigin, route.StubEnd,
            $"Case {route.CaseNumber:00} device drop");
        ValidatePipeFallsTowardMain(
            approach.Diagonal, route.StubEnd, route.DiagonalEnd,
            $"Case {route.CaseNumber:00} device-side diagonal");
        ValidatePipeFallsTowardMain(
            branch, route.DiagonalEnd, route.WyePoint,
            $"Case {route.CaseNumber:00} branch");

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
        ValidateRouteFallsToMain(route, $"Case {route.CaseNumber:00} route");
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
        FamilyInstance nearMain45 = CreateCase06Elbow(
            document,
            straightBranch,
            yLeg,
            nearMainElbow,
            "A/B 45 degree elbow");
        FamilyInstance sourceElbow2 = CreateCase06Elbow(
            document,
            approach.Diagonal,
            straightBranch,
            route.DiagonalEnd,
            "lower device-side 45 degree elbow");
        FamilyInstance sourceElbow1 = CreateCase06Elbow(
            document,
            approach.Stub,
            approach.Diagonal,
            route.StubEnd,
            "upper device-side 45 degree elbow");
        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        ConnectSource(document, sourceConnector, approach.Stub, route);
        ids.AddRange([sourceElbow1.Id, sourceElbow2.Id, nearMain45.Id]);
        document.Regenerate();

        if (route.CaseNumber == 2)
        {
            ValidateRequestedSlope(
                straightBranch,
                route.SlopePercent,
                "Case 02 straight branch");
            ValidateRequestedSlope(
                yLeg,
                route.SlopePercent,
                "Case 02 Y leg");
        }
        else if (route.CaseNumber == 6)
        {
            ValidateCase06PipeAxes(
                approach.Stub,
                approach.Diagonal,
                straightBranch,
                context,
                new DrainSettings(
                    route.SlopePercent,
                    context.BranchPipeType.Id));
        }

        ValidatePipeFallsTowardMain(
            approach.Stub, route.DrainOrigin, route.StubEnd,
            $"Case {route.CaseNumber:00} device drop");
        ValidatePipeFallsTowardMain(
            approach.Diagonal, route.StubEnd, route.DiagonalEnd,
            $"Case {route.CaseNumber:00} device-side diagonal");
        ValidatePipeFallsTowardMain(
            straightBranch, route.DiagonalEnd, nearMainElbow,
            $"Case {route.CaseNumber:00} pipe A");
        ValidatePipeFallsTowardMain(
            yLeg, nearMainElbow, route.WyePoint,
            $"Case {route.CaseNumber:00} pipe B into main");

        return new DrainOperationResult(
            true,
            false,
            $"Case {route.CaseNumber:00} Stage 1 created: source double-45 pair, " +
            "straight source-side sloped branch, one 45 degree elbow, and a Y-rolled leg ending on the main.",
            route,
            ids);
    }

    private static FamilyInstance CreateCase06Elbow(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        string label)
        => CreateElbowWithRoutingFallback(
            document,
            first,
            second,
            point,
            $"Case 06 {label}",
            preservePipeAxes: true);

    private static FamilyInstance CreateElbowWithRoutingFallback(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        string operationLabel,
        bool preservePipeAxes = false)
    {
        ElementId firstId = first.Id;
        ElementId secondId = second.Id;
        XYZ firstAxisBefore = PipeAxis(first);
        XYZ secondAxisBefore = PipeAxis(second);
        string placementFailure = string.Empty;

        // For geometry-sensitive routes (Cases 02/05/06), let the fitting
        // follow the already approved pipe axes. NewElbowFitting may trim a
        // long-takeout socket fitting into both adjacent short pipes and, in
        // Revit 2020, reverse one pipe before failure processing can roll it
        // back cleanly. Direct connector placement trims each endpoint to the
        // real family takeout without asking Revit to solve a new pipe route.
        if (preservePipeAxes)
        {
            FamilyInstance? direct = TryPlaceCase06ElbowFromRoutingRules(
                document,
                first,
                second,
                point,
                out placementFailure);
            if (direct is not null)
                return direct;

            first = RequirePipe(document, firstId, $"{operationLabel} first pipe");
            second = RequirePipe(document, secondId, $"{operationLabel} second pipe");
        }

        string nativeFailure = string.Empty;
        using (var nativeAttempt = new SubTransaction(document))
        {
            nativeAttempt.Start();
            try
            {
                FamilyInstance elbow = document.Create.NewElbowFitting(
                    DrainSelection.ConnectorNear(first, point, true),
                    DrainSelection.ConnectorNear(second, point, true));
                document.Regenerate();
                if (preservePipeAxes)
                {
                    Pipe firstAfter = RequirePipe(
                        document,
                        firstId,
                        $"{operationLabel} first pipe");
                    Pipe secondAfter = RequirePipe(
                        document,
                        secondId,
                        $"{operationLabel} second pipe");
                    double firstRotation = AcuteDegrees(
                        firstAxisBefore,
                        PipeAxis(firstAfter));
                    double secondRotation = AcuteDegrees(
                        secondAxisBefore,
                        PipeAxis(secondAfter));
                    if (firstRotation > 0.05 || secondRotation > 0.05)
                        throw new InvalidOperationException(
                            $"Revit rotated the connected pipe axes by " +
                            $"{firstRotation:0.###}° and {secondRotation:0.###}°");
                }
                if (nativeAttempt.Commit() == TransactionStatus.Committed)
                    return elbow;
                nativeFailure = "Revit rolled back NewElbowFitting.";
            }
            catch (Exception exception)
            {
                nativeFailure = exception.Message;
                if (nativeAttempt.GetStatus() == TransactionStatus.Started)
                    nativeAttempt.RollBack();
            }
        }

        if (!preservePipeAxes)
        {
            first = RequirePipe(document, firstId, $"{operationLabel} first pipe");
            second = RequirePipe(document, secondId, $"{operationLabel} second pipe");
            FamilyInstance? placed = TryPlaceCase06ElbowFromRoutingRules(
                document,
                first,
                second,
                point,
                out placementFailure);
            if (placed is not null)
                return placed;
        }

        throw new InvalidOperationException(
            $"{operationLabel} could not be created. " +
            $"NewElbowFitting: {nativeFailure}. " +
            $"Direct routing-family placement: {placementFailure}");
    }

    private static XYZ PipeAxis(Pipe pipe)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.CompatValue()} has no readable centerline.");
        return (location.Curve.GetEndPoint(1) -
                location.Curve.GetEndPoint(0)).Normalize();
    }

    private static FamilyInstance? TryPlaceCase06ElbowFromRoutingRules(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        out string failure)
    {
        failure = string.Empty;
        if (document.GetElement(first.GetTypeId()) is not PipeType pipeType)
        {
            failure = "The branch Pipe Type is unavailable.";
            return null;
        }

        RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
        int ruleCount = manager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Elbows);
        IReadOnlyList<ElementId> symbolIds = Enumerable.Range(0, ruleCount)
            .Select(index => manager.GetRule(
                RoutingPreferenceRuleGroupType.Elbows,
                index).MEPPartId)
            .Where(id => id != ElementId.InvalidElementId)
            .Distinct()
            .ToList();
        if (symbolIds.Count == 0)
        {
            failure = "The branch Pipe Type has no elbow routing families.";
            return null;
        }

        XYZ firstAxis = PipeDirectionAwayFrom(first, point).Normalize();
        XYZ secondAxis = PipeDirectionAwayFrom(second, point).Normalize();
        double requiredAngle = AcuteDegrees(firstAxis, secondAxis);
        double diameter = first
            .get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?
            .AsDouble() ?? 0.0;
        var failures = new List<string>();

        foreach (ElementId symbolId in symbolIds)
        {
            if (document.GetElement(symbolId) is not FamilySymbol symbol)
                continue;
            foreach (bool reverse in new[] { false, true })
            {
                using var attempt = new SubTransaction(document);
                attempt.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        document.Regenerate();
                    }

                    FamilyInstance elbow = document.Create.NewFamilyInstance(
                        point,
                        symbol,
                        StructuralType.NonStructural);
                    document.Regenerate();
                    IReadOnlyList<Connector> initial = DrainSelection.GetConnectors(elbow);
                    if (initial.Count != 2)
                        throw new InvalidOperationException(
                            $"family has {initial.Count} round piping connectors");

                    if (diameter > 0.0)
                    {
                        foreach (Connector connector in initial)
                        {
                            if (Math.Abs(connector.Radius * 2.0 - diameter) <=
                                DrainGeometry.Mm(0.5))
                                continue;
                            connector.Radius = diameter * 0.5;
                        }
                        document.Regenerate();
                        initial = DrainSelection.GetConnectors(elbow);
                    }

                    // Some manufacturer elbows open at 47 degrees when placed
                    // unconnected even though the catalog/routing name is 45.
                    // If the family exposes a writable angle parameter, flex
                    // this instance to the already approved pipe-centerline
                    // angle before orientation and connection.
                    TrySetFittingAngleParameters(elbow, requiredAngle);
                    document.Regenerate();
                    initial = DrainSelection.GetConnectors(elbow);

                    Connector sourceFirst = initial[reverse ? 1 : 0];
                    Connector sourceSecond = initial[reverse ? 0 : 1];
                    int sourceFirstId = sourceFirst.Id;
                    int sourceSecondId = sourceSecond.Id;
                    XYZ intersection = ClosestAxisIntersection(
                        sourceFirst.Origin,
                        sourceFirst.CoordinateSystem.BasisZ,
                        sourceSecond.Origin,
                        sourceSecond.CoordinateSystem.BasisZ);
                    XYZ sourceFirstRay = ConnectorRay(sourceFirst, intersection);
                    XYZ sourceSecondRay = ConnectorRay(sourceSecond, intersection);
                    double familyAngle = AcuteDegrees(sourceFirstRay, sourceSecondRay);
                    if (Math.Abs(familyAngle - requiredAngle) > 0.35)
                        throw new InvalidOperationException(
                            $"connector angle {familyAngle:0.###} degrees does not fit " +
                            $"the required {requiredAngle:0.###} degrees");

                    RotateVectorToVector(
                        document,
                        elbow.Id,
                        point,
                        sourceFirstRay,
                        firstAxis);
                    document.Regenerate();

                    IReadOnlyList<Connector> afterFirstRotation =
                        DrainSelection.GetConnectors(elbow);
                    sourceFirst = afterFirstRotation.Single(item => item.Id == sourceFirstId);
                    sourceSecond = afterFirstRotation.Single(item => item.Id == sourceSecondId);
                    intersection = ClosestAxisIntersection(
                        sourceFirst.Origin,
                        sourceFirst.CoordinateSystem.BasisZ,
                        sourceSecond.Origin,
                        sourceSecond.CoordinateSystem.BasisZ);
                    sourceSecondRay = ConnectorRay(sourceSecond, intersection);
                    double roll = SignedAngle(
                        PerpendicularComponent(sourceSecondRay, firstAxis),
                        PerpendicularComponent(secondAxis, firstAxis),
                        firstAxis);
                    if (Math.Abs(roll) > 1e-8)
                    {
                        ElementTransformUtils.RotateElement(
                            document,
                            elbow.Id,
                            Line.CreateUnbound(point, firstAxis),
                            roll);
                        document.Regenerate();
                    }

                    IReadOnlyList<Connector> oriented = DrainSelection.GetConnectors(elbow);
                    sourceFirst = oriented.Single(item => item.Id == sourceFirstId);
                    sourceSecond = oriented.Single(item => item.Id == sourceSecondId);
                    intersection = ClosestAxisIntersection(
                        sourceFirst.Origin,
                        sourceFirst.CoordinateSystem.BasisZ,
                        sourceSecond.Origin,
                        sourceSecond.CoordinateSystem.BasisZ);
                    ElementTransformUtils.MoveElement(
                        document,
                        elbow.Id,
                        point - intersection);
                    document.Regenerate();

                    IReadOnlyList<Connector> fitted = DrainSelection.GetConnectors(elbow);
                    sourceFirst = fitted.Single(item => item.Id == sourceFirstId);
                    sourceSecond = fitted.Single(item => item.Id == sourceSecondId);
                    MovePipeEnd(first, point, sourceFirst.Origin);
                    MovePipeEnd(second, point, sourceSecond.Origin);
                    document.Regenerate();
                    ConnectCoincident(document, first, sourceFirst);
                    ConnectCoincident(document, second, sourceSecond);
                    if (attempt.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit rolled back direct elbow placement");
                    return elbow;
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"{symbol.FamilyName}: {symbol.Name}" +
                        (reverse ? " [reversed]" : string.Empty) +
                        $": {exception.Message}");
                    if (attempt.GetStatus() == TransactionStatus.Started)
                        attempt.RollBack();
                }
            }
        }

        failure = string.Join(" | ", failures.Distinct().TakeLast(6));
        return null;
    }

    private static void TrySetFittingAngleParameters(
        FamilyInstance fitting,
        double angleDegrees)
    {
        double angleRadians = angleDegrees * Math.PI / 180.0;
        foreach (Parameter parameter in fitting.Parameters.Cast<Parameter>())
        {
            if (parameter.IsReadOnly ||
                parameter.StorageType != StorageType.Double)
                continue;
            string name = parameter.Definition?.Name ?? string.Empty;
            bool looksLikeAngle =
                name.Contains("angle", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("winkel", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("góc", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bend", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeAngle)
                continue;
            try
            {
                parameter.Set(angleRadians);
            }
            catch
            {
                // Continue with another writable angle parameter. The caller
                // verifies the real connector axes after regeneration.
            }
        }
    }

    private static void ValidateCase06PipeAxes(
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
                "Case 06 could not read the created pipe centerlines.");

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
        ValidateRouteFallsToMain(route, $"Case {route.CaseNumber:00} route");
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
        ValidatePipeFallsTowardMain(
            standing, route.DrainOrigin, route.StubEnd,
            "Case 03 standing device drop");
        ValidatePipeFallsTowardMain(
            diagonal, route.StubEnd, route.WyePoint,
            "Case 03 diagonal into main");

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
        DrainRoute route,
        double minimumMiddlePipeLength,
        ElementId sourceId,
        ElementId targetId)
    {
        ValidateRouteFallsToMain(route, "Case 04 route");
        XYZ secondElbow = route.NearMainElbow
            ?? throw new InvalidOperationException(
                "Case 04 did not calculate its second 45 degree elbow.");
        XYZ extensionElbow = route.MainExtensionElbow
            ?? throw new InvalidOperationException(
                "Case 04 did not calculate its main-extension elbow.");
        var ids = new List<ElementId>();
        Pipe target = document.GetElement(targetId) as Pipe
            ?? throw new InvalidOperationException("The target main is no longer available.");
        string stage = "prepare the picked main endpoint";

        try
        {
            // The picked point may be inside the selected main. Convert it into an
            // open endpoint by deleting only the shorter surplus side, then extend
            // the retained side for the two-45 layout.
            target = PreparePickedMainEndpoint(
                document,
                target,
                route.MainStart,
                route.WyePoint);
            stage = "extend the retained main to the first plan elbow";
            target = ExtendOpenMainEndpoint(
                document,
                target,
                route.WyePoint,
                extensionElbow);
            ElementId targetPipeId = target.Id;

        stage = "create the device-side and plan pipes";
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
        ElementId stubId = approach.Stub.Id;
        ElementId approachDiagonalId = approach.Diagonal.Id;
        ElementId straightBranchId = straightBranch.Id;
        ElementId turnDiagonalId = turnDiagonal.Id;
        ids.AddRange([straightBranch.Id, turnDiagonal.Id]);
        document.Regenerate();

        // Anchor the vertical stub to the drain before Revit resolves any
        // elbows. Otherwise the last ConnectTo call can pull the whole compact
        // device-side pair sideways and leave a visibly tilted drop pipe.
        stage = "connect the device reducer or leave its DN gap";
        Element source = document.GetElement(sourceId)
            ?? throw new InvalidOperationException("The source drain is no longer available.");
        Connector sourceConnector = DrainSelection.ChooseSourceConnector(source);
        SourceConnection sourceConnection = ConnectCase04Source(
            document,
            context,
            sourceConnector,
            approach.Stub,
            route);
        approach = new ApproachPipes(
            RequirePipe(document, stubId, "Case 04 device stub"),
            RequirePipe(document, approachDiagonalId, "Case 04 device diagonal"));
        straightBranch = RequirePipe(
            document,
            straightBranchId,
            "Case 04 straight branch");
        turnDiagonal = RequirePipe(
            document,
            turnDiagonalId,
            "Case 04 plan diagonal");
        stage = "create the upper device-side 45 degree elbow";
        FamilyInstance source45A = CreateCase04Elbow(
            document, approach.Stub, approach.Diagonal, route.StubEnd,
            "upper device-side 45 degree elbow");
        stage = "create the lower device-side 45 degree elbow";
        FamilyInstance source45B = CreateCase04Elbow(
            document, approach.Diagonal, straightBranch, route.DiagonalEnd,
            "lower device-side 45 degree elbow");
        document.Regenerate();
        ValidateMinimumMiddlePipeLength(
            approach.Diagonal,
            minimumMiddlePipeLength);
        stage = "create the second plan 45 degree elbow";
        FamilyInstance second45 = CreateCase04Elbow(
            document, straightBranch, turnDiagonal, secondElbow,
            "second plan 45 degree elbow");
        stage = "create the main-extension 45 degree elbow";
        FamilyInstance main45 = CreateCase04Elbow(
            document,
            turnDiagonal,
            target,
            extensionElbow,
            "main-extension 45 degree elbow");
        ValidateVerticalDeviceStub(approach.Stub, route.DrainOrigin);
        if (sourceConnection.Adapter is not null)
        {
            ValidateVerticalDeviceStub(sourceConnection.Adapter, route.DrainOrigin);
            ValidateVerticalPipeAlignment(sourceConnection.Adapter, approach.Stub);
        }
        ids.AddRange([
            source45A.Id,
            source45B.Id,
            second45.Id,
            main45.Id]);
        AddSourceConnectionIds(ids, sourceConnection);
        document.Regenerate();

        approach = new ApproachPipes(
            RequirePipe(document, stubId, "Case 04 device stub"),
            RequirePipe(document, approachDiagonalId, "Case 04 device diagonal"));
        straightBranch = RequirePipe(
            document, straightBranchId, "Case 04 straight branch");
        turnDiagonal = RequirePipe(
            document, turnDiagonalId, "Case 04 plan diagonal");
        target = RequirePipe(document, targetPipeId, "Case 04 retained main");
        ValidatePipeFallsTowardMain(
            approach.Stub, route.DrainOrigin, route.StubEnd,
            "Case 04 device drop");
        ValidatePipeFallsTowardMain(
            approach.Diagonal, route.StubEnd, route.DiagonalEnd,
            "Case 04 device-side diagonal");
        ValidatePipeFallsTowardMain(
            straightBranch, route.DiagonalEnd, secondElbow,
            "Case 04 straight branch");
        ValidatePipeFallsTowardMain(
            turnDiagonal, secondElbow, extensionElbow,
            "Case 04 diagonal into main extension");
        ValidatePipeFallsAwayFromConnection(
            target,
            extensionElbow,
            "Case 04 retained main after the endpoint elbow");

        string resultMessage = sourceConnection.HasOpenBreak
            ? "Case 04 created: the open main was extended and the route was completed. " +
              $"The DN change remains as an open break at the device drop" +
              (sourceConnection.BreakPoint is null
                  ? "."
                  : $" ({DrainGeometry.ToMm(sourceConnection.BreakPoint.X):0.#}, " +
                    $"{DrainGeometry.ToMm(sourceConnection.BreakPoint.Y):0.#}, " +
                    $"{DrainGeometry.ToMm(sourceConnection.BreakPoint.Z):0.#} mm).") +
              $" {sourceConnection.Warning}"
            : "Case 04 created: the open main was extended, then two 45 degree plan elbows " +
              "and a straight run connected it to the device. Main DN, type, and slope were retained.";

            return new DrainOperationResult(
                true,
                false,
                resultMessage,
                route,
                ids);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 04 failed while trying to {stage}: {exception.Message}",
                exception);
        }
    }

    private static FamilyInstance CreateCase04Elbow(
        Document document,
        Pipe first,
        Pipe second,
        XYZ point,
        string label,
        bool preservePipeAxes = false)
    {
        try
        {
            return CreateElbowWithRoutingFallback(
                document,
                first,
                second,
                point,
                $"Case 04 {label}",
                preservePipeAxes);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Case 04 {label} could not be created: {exception.Message}",
                exception);
        }
    }

    private static void ValidateRouteFallsToMain(
        DrainRoute route,
        string label)
    {
        double tolerance = DrainGeometry.Mm(0.5);
        var points = new List<XYZ>
        {
            route.DrainOrigin,
            route.StubEnd
        };
        if (points[^1].DistanceTo(route.DiagonalEnd) > tolerance)
            points.Add(route.DiagonalEnd);

        if (route.NearMainElbow is XYZ nearMain &&
            points[^1].DistanceTo(nearMain) > tolerance)
            points.Add(nearMain);
        if (route.MainExtensionElbow is XYZ extension &&
            points[^1].DistanceTo(extension) > tolerance)
            points.Add(extension);
        if (points[^1].DistanceTo(route.WyePoint) > tolerance)
            points.Add(route.WyePoint);

        if (route.DrainOrigin.Z <= route.WyePoint.Z + tolerance)
            throw new InvalidOperationException(
                $"{label}: the device must be higher than the main connection. " +
                $"Device Z={DrainGeometry.ToMm(route.DrainOrigin.Z):0.###} mm, " +
                $"main Z={DrainGeometry.ToMm(route.WyePoint.Z):0.###} mm.");

        for (int index = 0; index < points.Count - 1; index++)
        {
            XYZ upstream = points[index];
            XYZ downstream = points[index + 1];
            if (downstream.Z > upstream.Z + tolerance)
                throw new InvalidOperationException(
                    $"{label}: segment {index + 1} rises " +
                    $"{DrainGeometry.ToMm(downstream.Z - upstream.Z):0.###} mm " +
                    "toward the main. Only level or downward flow is allowed.");
        }
    }

    private static void ValidatePipeFallsTowardMain(
        Pipe pipe,
        XYZ upstreamReference,
        XYZ downstreamReference,
        string label)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"{label}: the pipe centerline is unavailable.");
        XYZ first = location.Curve.GetEndPoint(0);
        XYZ second = location.Curve.GetEndPoint(1);
        double normalCost =
            first.DistanceTo(upstreamReference) +
            second.DistanceTo(downstreamReference);
        double reversedCost =
            second.DistanceTo(upstreamReference) +
            first.DistanceTo(downstreamReference);
        XYZ upstream = normalCost <= reversedCost ? first : second;
        XYZ downstream = normalCost <= reversedCost ? second : first;
        ValidateDownwardElevation(upstream, downstream, label);
    }

    private static void ValidatePipeFallsIntoConnection(
        Pipe pipe,
        XYZ connectionPoint,
        string label)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"{label}: the pipe centerline is unavailable.");
        XYZ first = location.Curve.GetEndPoint(0);
        XYZ second = location.Curve.GetEndPoint(1);
        bool firstIsConnection =
            first.DistanceTo(connectionPoint) <= second.DistanceTo(connectionPoint);
        XYZ downstream = firstIsConnection ? first : second;
        XYZ upstream = firstIsConnection ? second : first;
        ValidateDownwardElevation(upstream, downstream, label);
    }

    private static void ValidatePipeFallsAwayFromConnection(
        Pipe pipe,
        XYZ connectionPoint,
        string label)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"{label}: the pipe centerline is unavailable.");
        XYZ first = location.Curve.GetEndPoint(0);
        XYZ second = location.Curve.GetEndPoint(1);
        bool firstIsConnection =
            first.DistanceTo(connectionPoint) <= second.DistanceTo(connectionPoint);
        XYZ upstream = firstIsConnection ? first : second;
        XYZ downstream = firstIsConnection ? second : first;
        ValidateDownwardElevation(upstream, downstream, label);
    }

    private static void ValidateDownwardElevation(
        XYZ upstream,
        XYZ downstream,
        string label)
    {
        double rise = downstream.Z - upstream.Z;
        if (rise > DrainGeometry.Mm(0.5))
            throw new InvalidOperationException(
                $"{label}: the pipe rises {DrainGeometry.ToMm(rise):0.###} mm " +
                "toward the main. The candidate was rejected because drainage " +
                "must remain level or fall continuously toward the main.");
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

    private static IReadOnlyList<double> ResolveRoutingElbowAngles(
        Document document,
        PipeType pipeType,
        double diameter)
    {
        RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
        int ruleCount = manager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Elbows);
        IReadOnlyList<ElementId> symbolIds = Enumerable.Range(0, ruleCount)
            .Select(index => manager.GetRule(
                RoutingPreferenceRuleGroupType.Elbows,
                index).MEPPartId)
            .Where(id => id != ElementId.InvalidElementId)
            .Distinct()
            .ToList();
        ElementId preferredSymbolId = ElementId.InvalidElementId;
        try
        {
            using var conditions = new RoutingConditions(
                RoutingPreferenceErrorLevel.None);
            conditions.AppendCondition(new RoutingCondition(diameter));
            preferredSymbolId = manager.GetMEPPartId(
                RoutingPreferenceRuleGroupType.Elbows,
                conditions);
        }
        catch
        {
            // Fall back to routing-rule order when this Revit version or pipe
            // family does not resolve an elbow from a single size condition.
        }
        symbolIds = symbolIds
            .OrderByDescending(id => id == preferredSymbolId)
            .ToList();
        var angles = new List<double>();

        // Always inspect a fresh, unconnected symbol instance. An elbow that
        // is already connected in the model can have its instance geometry
        // flexed by Revit (45 degrees here) even though the routing family
        // definition still has fixed 47-degree connector axes.
        if (symbolIds.Count > 0)
        {
            using var inspect = new Transaction(
                document,
                "Measure drain elbow connector angles");
            inspect.Start();
            try
            {
                foreach (ElementId symbolId in symbolIds)
                {
                    if (document.GetElement(symbolId) is not FamilySymbol symbol)
                        continue;
                    try
                    {
                        if (!symbol.IsActive)
                        {
                            symbol.Activate();
                            document.Regenerate();
                        }
                        FamilyInstance sample = document.Create.NewFamilyInstance(
                            XYZ.Zero,
                            symbol,
                            StructuralType.NonStructural);
                        document.Regenerate();
                        IReadOnlyList<Connector> connectors =
                            DrainSelection.GetConnectors(sample);
                        if (connectors.Count != 2)
                            continue;
                        if (diameter > 0)
                        {
                            foreach (Connector connector in connectors)
                            {
                                if (Math.Abs(connector.Radius * 2.0 - diameter) >
                                    DrainGeometry.Mm(0.5))
                                    connector.Radius = diameter * 0.5;
                            }
                            document.Regenerate();
                            connectors = DrainSelection.GetConnectors(sample);
                        }
                        double angle = AcuteDegrees(
                            connectors[0].CoordinateSystem.BasisZ,
                            connectors[1].CoordinateSystem.BasisZ);
                        if (angle is > 5.0 and < 85.0)
                            angles.Add(angle);
                    }
                    catch
                    {
                        // Continue with the next routing rule. The whole
                        // inspection transaction is rolled back below.
                    }
                }
            }
            finally
            {
                if (inspect.GetStatus() == TransactionStatus.Started)
                    inspect.RollBack();
            }
        }

        IReadOnlyList<double> measured = angles
            .Select(angle => Math.Round(angle, 6))
            .Distinct()
            .ToList();
        return measured.Count > 0 ? measured : [45.0];
    }

    private static void ValidateVerticalPipeAlignment(Pipe upper, Pipe lower)
    {
        if (upper.Location is not LocationCurve upperLocation ||
            lower.Location is not LocationCurve lowerLocation)
            throw new InvalidOperationException(
                "Case 04 could not compare the device drop centerlines.");
        XYZ upperPoint = upperLocation.Curve.GetEndPoint(0);
        XYZ lowerPoint = lowerLocation.Curve.GetEndPoint(0);
        double planOffset = Math.Sqrt(
            Math.Pow(upperPoint.X - lowerPoint.X, 2) +
            Math.Pow(upperPoint.Y - lowerPoint.Y, 2));
        if (planOffset > DrainGeometry.Mm(0.5))
            throw new InvalidOperationException(
                $"The selected reducer offsets the Case 04 device drop by " +
                $"{DrainGeometry.ToMm(planOffset):0.###} mm in plan.");
    }

    private static Pipe ExtendOpenMainEndpoint(
        Document document,
        Pipe target,
        XYZ oldEndpoint,
        XYZ newEndpoint)
    {
        ElementId targetId = target.Id;
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
        document.Regenerate();
        Pipe refreshed = document.GetElement(targetId) as Pipe
            ?? throw new InvalidOperationException(
                "The retained main disappeared after its endpoint was moved.");
        if (!HasEndAt(refreshed, newEndpoint))
            throw new InvalidOperationException(
                "The retained main did not finish at the calculated elbow point.");
        return refreshed;
    }

    private static Pipe PreparePickedMainEndpoint(
        Document document,
        Pipe selectedMain,
        XYZ retainedSidePoint,
        XYZ requestedEndpoint)
    {
        ElementId selectedMainId = selectedMain.Id;
        if (selectedMain.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "The selected main has no centerline for endpoint preparation.");

        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        double endpointTolerance = DrainGeometry.Mm(2);
        if (requestedEndpoint.DistanceTo(start) <= endpointTolerance ||
            requestedEndpoint.DistanceTo(end) <= endpointTolerance)
        {
            return selectedMain;
        }

        IntersectionResult projection = location.Curve.Project(requestedEndpoint)
            ?? throw new InvalidOperationException(
                "The picked endpoint could not be projected to the selected main.");
        XYZ breakPoint = projection.XYZPoint;
        double minimumTail = Math.Max(
            DrainGeometry.Mm(20),
            selectedMain.get_Parameter(
                BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.0);
        if (breakPoint.DistanceTo(start) <= minimumTail ||
            breakPoint.DistanceTo(end) <= minimumTail)
        {
            throw new InvalidOperationException(
                "The picked point is too close to a connected main end. " +
                "Pick at least one pipe diameter away from that end.");
        }

        // Keep the selected pipe's ElementId stable. BreakCurve may retain the
        // original id on either side depending on the Revit version; deleting
        // the short side can therefore invalidate Context.Target. Trimming the
        // same LocationCurve removes the surplus geometry without replacing
        // the element that the remaining creation code already references.
        XYZ retainedByGravity = RetainedMainEndpoint(
            start,
            end,
            breakPoint);
        bool retainStart = retainedByGravity.DistanceTo(start) <=
                           retainedByGravity.DistanceTo(end);
        XYZ retainedEndpoint = retainStart ? start : end;
        XYZ discardedEndpoint = retainStart ? end : start;
        DisconnectPipeEndpoint(selectedMain, discardedEndpoint);
        location.Curve = retainStart
            ? Line.CreateBound(retainedEndpoint, breakPoint)
            : Line.CreateBound(breakPoint, retainedEndpoint);
        document.Regenerate();

        Pipe refreshed = document.GetElement(selectedMainId) as Pipe
            ?? throw new InvalidOperationException(
                "The retained main disappeared after the surplus tail was removed.");
        if (!HasEndAt(refreshed, breakPoint))
            throw new InvalidOperationException(
                "The retained main did not end at the requested connection point.");
        return refreshed;
    }

    private static void DisconnectPipeEndpoint(Pipe pipe, XYZ endpoint)
    {
        Connector? connector = DrainSelection.GetConnectors(pipe)
            .OrderBy(item => item.Origin.DistanceTo(endpoint))
            .FirstOrDefault();
        if (connector is null || connector.Origin.DistanceTo(endpoint) > DrainGeometry.Mm(3))
            return;

        IReadOnlyList<Connector> references = connector.AllRefs
            .Cast<Connector>()
            .Where(reference => reference.Owner?.Id != pipe.Id)
            .ToList();
        foreach (Connector reference in references)
        {
            try
            {
                connector.DisconnectFrom(reference);
            }
            catch
            {
                // Setting the shortened centerline below is authoritative. A
                // connector that Revit already detached needs no extra action.
            }
        }
    }

    private static DrainOperationResult CompleteWithRoutedY(
        Document document,
        Context context,
        DrainOperationResult stage1,
        List<string> failures,
        DrainSettings settings)
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
                    context.BranchDiameter,
                    settings.JunctionSymbolId,
                    settings.JunctionAnglesBySymbolId,
                    route.FittingAngleDegrees);
                if (prepare.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(
                        "Revit could not isolate the main Pipe Type Junction routing rules.");
            }

            // Stage 2 is connector-driven. Do not filter a selected family by a
            // nominal/parameter angle; each candidate is oriented from its real
            // connector axes and accepted only when all three pipes connect.
            IReadOnlyList<PreparedJunctionType> preparedTypes = allPreparedTypes;
            IReadOnlyList<ElementId> allPreparedTypeIds = allPreparedTypes
                .Where(item => item.NativeRouting)
                .Select(item => item.TypeId)
                .ToList();
            // Try the selected family as a reducing Y first. When its branch
            // connector is fixed at the run diameter, retry with a short
            // main-DN seed and a reducer back to the device branch diameter.
            bool reducingConnection = Math.Abs(
                context.MainDiameter - context.BranchDiameter) >
                DrainGeometry.Mm(0.5);
            bool[] seedStrategies = reducingConnection
                ? [false, true]
                : [false];

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
                            if (preparedType.NativeRouting)
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
                            double fittingBranchDiameter = seed is null
                                ? context.BranchDiameter
                                : context.MainDiameter;
                            document.Regenerate();

                            ValidateMainConnectionPlanAngle(
                                junctionLeg,
                                target,
                                $"{caseLabel} planned Y connection",
                                route.CaseNumber);
                            double requiredFittingAngle =
                                MeasureMainConnection3dAngle(junctionLeg, target);
                            if (!IsSupportedYAngle(requiredFittingAngle))
                                throw new InvalidOperationException(
                                    $"{caseLabel} requires a {requiredFittingAngle:0.###} degree " +
                                    "3D Y connector after applying the pipe slopes; this is outside " +
                                    $"the allowed {MinimumYAngleDegrees:0.#}-" +
                                    $"{MaximumYAngleDegrees:0.#} degree range.");

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
                            // Always use connector-driven placement here. Native
                            // NewTeeFitting may pull the branch from exact 45-degree
                            // plan geometry toward the family's nominal connector angle.
                            junction ??= PlaceRotateAndConnectJunction(
                                document,
                                preparedType.PartId,
                                junctionPoint,
                                target,
                                splitPipe,
                                junctionLeg,
                                context.MainDiameter,
                                fittingBranchDiameter,
                                requiredFittingAngle,
                                true);
                            document.Regenerate();
                            ValidateConnectedJunction(junction, target, splitPipe, junctionLeg,
                                preparedType.PartId, context.MainDiameter, fittingBranchDiameter);

                            if (preparedType.NativeRouting)
                            {
                                target.ChangeTypeId(context.TargetPipeType.Id);
                                splitPipe.ChangeTypeId(context.TargetPipeType.Id);
                            }
                            if (seed is not null)
                                seed.Pipe.ChangeTypeId(context.TargetPipeType.Id);
                            document.Regenerate();
                            ValidateConnectedJunction(junction, target, splitPipe, junctionLeg,
                                preparedType.PartId, context.MainDiameter, fittingBranchDiameter);
                            double connectedPlanAngle = ValidateMainConnectionPlanAngle(
                                junctionLeg,
                                target,
                                $"{caseLabel} connected Y",
                                route.CaseNumber);
                            ValidatePipeFallsIntoConnection(
                                junctionLeg,
                                junctionPoint,
                                $"{caseLabel} final branch into Y");
                            // Stage 1 already verifies the requested branch slope.
                            // Some manufacturer socket families place their branch
                            // connector a fraction off the theoretical Y axis. Revit
                            // then adjusts the short final pipe by a few hundredths of
                            // a percent when ConnectTo closes the joint. Connectivity
                            // is authoritative here; do not roll back a fully connected
                            // three-connector Y because of that fitting takeout offset.
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
                                $"{preparedType.Label} Y using {sizeLabel} " +
                                $"({(nativeRoutedJunction ? "three pipe connectors" : "manual placement")}); " +
                                $"branch-to-main 45-degree view angle {connectedPlanAngle:0.###} degrees.",
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
        double branchDiameter,
        ElementId? selectedSymbolId,
        IReadOnlyDictionary<long, double>? knownAngles,
        double requestedFittingAngle)
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

        if (selectedSymbolId is not null && selectedSymbolId != ElementId.InvalidElementId)
        {
            if (document.GetElement(selectedSymbolId) is not FamilySymbol selected ||
                selected.Category?.Id.CompatValue() != (long)BuiltInCategory.OST_PipeFitting)
                throw new InvalidOperationException("The selected Y fitting type is no longer available.");
            candidates.Clear();
            candidates.Add(new JunctionRuleCandidate(-1, selected.Id,
                $"{selected.FamilyName}: {selected.Name} [Id {selected.Id.CompatValue()}]"));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "The Target Pipe Type has no valid junction family in Routing Preferences.");

        var prepared = new List<PreparedJunctionType>();
        foreach (JunctionRuleCandidate candidate in candidates
                     .OrderByDescending(item => item.PartId == preferredPartId)
                     .ThenBy(item => item.RuleIndex))
        {
            double fittingAngle = ResolveJunctionAngleDegrees(document, candidate.PartId);
            if (double.IsNaN(fittingAngle) &&
                knownAngles?.TryGetValue(candidate.PartId.CompatValue(), out double knownAngle) == true)
                fittingAngle = knownAngle;
            // The WPF catalog already verified the selected symbol and used its
            // connector angle to build Stage 1. A second probe can fail or choose
            // the wrong connector pair on differently authored families, so the
            // selected symbol keeps the same verified angle throughout Stage 2.
            if (selectedSymbolId is not null &&
                candidate.PartId == selectedSymbolId &&
                IsSupportedYAngle(requestedFittingAngle))
                fittingAngle = requestedFittingAngle;
            using var isolation = new SubTransaction(document);
            isolation.Start();
            try
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

                if (candidate.RuleIndex == -1)
                {
                    var rule = new RoutingPreferenceRule(candidate.PartId, "Selected drain Y");
                    rule.AddCriterion(new PrimarySizeCriterion(0.0, double.MaxValue));
                    manager.AddRule(RoutingPreferenceRuleGroupType.Junctions, rule);
                    manager.PreferredJunctionType = PreferredJunctionType.Tee;
                }
                ElementId temporaryId = temporary.Id;
                if (isolation.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rolled back the temporary Y routing rule.");
                prepared.Add(new PreparedJunctionType(
                    temporaryId, candidate.PartId,
                    $"{candidate.Label} (connector {fittingAngle:0.###} deg)", fittingAngle));
            }
            catch (Exception exception) when (candidate.RuleIndex == -1)
            {
                if (isolation.GetStatus() == TransactionStatus.Started)
                    isolation.RollBack();
                // Some loaded Y families cannot belong to the Junctions group.
                // Their explicit selection must still reach direct placement.
                // The original pipe type is neither edited nor a cleanup target.
                prepared.Add(new PreparedJunctionType(
                    targetPipeType.Id, candidate.PartId,
                    $"{candidate.Label} (connector {fittingAngle:0.###} deg; direct family placement; " +
                    $"routing unavailable: {exception.Message})", fittingAngle, false));
            }
        }

        IReadOnlyList<PreparedJunctionType> case01 = prepared
            .Where(item => IsSupportedYAngle(item.FittingAngleDegrees))
            .ToList();
        if (case01.Count == 0)
            throw new InvalidOperationException(
                "No verified three-connector Y is available. The fitting needs two collinear main connectors and one non-90-degree branch connector. Stage 1 pipes are retained.");
        return case01;
    }

    private static DrainSettings ValidateSelectedYBeforeStage1(
        Document document,
        DrainSettings settings,
        PipeType targetPipeType)
    {
        ElementId? selectedSymbolId = settings.JunctionSymbolId;
        bool explicitSelection = selectedSymbolId is not null &&
            selectedSymbolId != ElementId.InvalidElementId;
        IReadOnlyList<ElementId> candidateIds;
        if (explicitSelection)
        {
            candidateIds = [selectedSymbolId!];
        }
        else
        {
            RoutingPreferenceManager manager = targetPipeType.RoutingPreferenceManager;
            int count = manager.GetNumberOfRules(
                RoutingPreferenceRuleGroupType.Junctions);
            candidateIds = Enumerable.Range(0, count)
                .Select(index => manager.GetRule(
                    RoutingPreferenceRuleGroupType.Junctions,
                    index).MEPPartId)
                .Where(id => id != ElementId.InvalidElementId)
                .Distinct()
                .ToList();
        }

        if (candidateIds.Count == 0)
            throw new InvalidOperationException(
                "The selected main Pipe Type has no Junction fitting rules to inspect.");

        using var validation = new Transaction(
            document,
            "Measure drain Y connectors before creating branch");
        validation.Start();
        try
        {
            var measuredAngles = settings.JunctionAnglesBySymbolId is null
                ? new Dictionary<long, double>()
                : settings.JunctionAnglesBySymbolId.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);
            var validAngles = new List<double>();
            var rejected = new List<string>();
            foreach (ElementId candidateId in candidateIds)
            {
                if (document.GetElement(candidateId) is not FamilySymbol symbol ||
                    symbol.Category?.Id.CompatValue() !=
                    (long)BuiltInCategory.OST_PipeFitting)
                {
                    rejected.Add($"Element {candidateId.CompatValue()} is not a Pipe Fitting type.");
                    continue;
                }

                string fittingName = $"{symbol.FamilyName}: {symbol.Name}";
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        document.Regenerate();
                    }

                    FamilyInstance? existing = ExistingJunctionInstance(document, symbol.Id);
                    FamilyInstance? probe = existing is null
                        ? CreateJunctionProbe(document, symbol)
                        : null;
                    document.Regenerate();
                    IReadOnlyList<Connector> connectors = DrainSelection.GetConnectors(
                        existing ?? probe!);
                    if (connectors.Count != 3)
                        throw new InvalidOperationException(
                            $"has {connectors.Count} round piping connector(s); exactly 3 are required");

                    (int first, int second) = MostOppositeConnectorPair(connectors);
                    XYZ firstDirection = connectors[first].CoordinateSystem.BasisZ.Normalize();
                    XYZ secondDirection = connectors[second].CoordinateSystem.BasisZ.Normalize();
                    if (Math.Abs(firstDirection.DotProduct(secondDirection)) < 0.99)
                        throw new InvalidOperationException(
                            "does not have two parallel main-run connector axes");

                    Connector branch = connectors
                        .Where((_, index) => index != first && index != second)
                        .Single();
                    double branchAngle = Math.Min(
                        AcuteDegrees(branch.CoordinateSystem.BasisZ, firstDirection),
                        AcuteDegrees(branch.CoordinateSystem.BasisZ, secondDirection));
                    if (!IsSupportedYAngle(branchAngle))
                        throw new InvalidOperationException(
                            $"has a {branchAngle:0.###} degree branch; drain Y fittings must be " +
                            $"within {MinimumYAngleDegrees:0.#}-{MaximumYAngleDegrees:0.#} degrees");

                    measuredAngles[candidateId.CompatValue()] = branchAngle;
                    validAngles.Add(branchAngle);
                }
                catch (Exception exception)
                {
                    rejected.Add($"'{fittingName}' {exception.Message}");
                }
            }

            if (validAngles.Count == 0)
                throw new InvalidOperationException(
                    "No usable Y fitting was found from the real connector geometry. " +
                    string.Join(" | ", rejected.Take(6)));

            return settings with
            {
                JunctionAngles = validAngles.Distinct().OrderBy(angle => angle).ToList(),
                JunctionAnglesBySymbolId = measuredAngles
            };
        }
        finally
        {
            if (validation.GetStatus() == TransactionStatus.Started)
                validation.RollBack();
        }
    }

    private static void ValidateMinimumMiddlePipeLength(
        Pipe middlePipe,
        double requiredLength)
    {
        if (requiredLength <= DrainGeometry.Mm(0.1))
            return;
        if (middlePipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "Case 04 could not read the pipe between the two device-side 45 degree elbows.");
        double actualLength = location.Curve.Length;
        if (actualLength + DrainGeometry.Mm(1) < requiredLength)
            throw new InvalidOperationException(
                $"The pipe between the two device-side 45 degree elbows is only " +
                $"{DrainGeometry.ToMm(actualLength):0.#} mm after fitting takeout; " +
                $"at least {DrainGeometry.ToMm(requiredLength):0.#} mm is required. " +
                "Trying a longer Case 04 layout.");
    }

    private static bool IsSupportedYAngle(double angleDegrees) =>
        !double.IsNaN(angleDegrees) &&
        angleDegrees >= MinimumYAngleDegrees - 1e-6 &&
        angleDegrees <= MaximumYAngleDegrees + 1e-6;

    private static double ResolveJunctionAngleDegrees(
        Document document,
        ElementId symbolId)
    {
        if (document.GetElement(symbolId) is not FamilySymbol symbol)
            return double.NaN;

        FamilyInstance? probe = null;
        try
        {
            FamilyInstance? existing = ExistingJunctionInstance(document, symbol.Id);
            probe = existing is null
                ? CreateJunctionProbe(document, symbol)
                : null;
            document.Regenerate();
            IReadOnlyList<Connector> connectors = DrainSelection.GetConnectors(
                existing ?? probe!);
            if (connectors.Count != 3)
                return double.NaN;

            (int first, int second) = MostOppositeConnectorPair(connectors);
            if (Math.Abs(connectors[first].CoordinateSystem.BasisZ.Normalize().DotProduct(
                    connectors[second].CoordinateSystem.BasisZ.Normalize())) < 0.99)
                return double.NaN;
            Connector? branch = connectors
                .Where((_, index) => index != first && index != second)
                .OrderByDescending(item => item.Radius)
                .FirstOrDefault();
            if (branch?.CoordinateSystem is null ||
                connectors[first].CoordinateSystem is null ||
                connectors[second].CoordinateSystem is null)
                return double.NaN;

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
            return measured is > 1.0 and <= 90.001 ? measured : double.NaN;
        }
        catch
        {
            return double.NaN;
        }
        finally
        {
            if (probe is not null && document.GetElement(probe.Id) is not null)
                document.Delete(probe.Id);
        }
    }

    private static FamilyInstance? ExistingJunctionInstance(
        Document document,
        ElementId symbolId)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_PipeFitting)
            .Cast<FamilyInstance>()
            .FirstOrDefault(instance =>
                instance.Symbol.Id == symbolId &&
                DrainSelection.GetConnectors(instance).Count > 0);
    }

    private static FamilyInstance CreateJunctionProbe(
        Document document,
        FamilySymbol symbol)
    {
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }

        Exception? pointPlacementFailure = null;
        try
        {
            return document.Create.NewFamilyInstance(
                XYZ.Zero,
                symbol,
                StructuralType.NonStructural);
        }
        catch (Exception exception)
        {
            pointPlacementFailure = exception;
        }

        Level? level = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(item => Math.Abs(item.Elevation))
            .FirstOrDefault();
        if (level is not null)
        {
            try
            {
                return document.Create.NewFamilyInstance(
                    new XYZ(0, 0, level.Elevation),
                    symbol,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Exception levelPlacementFailure)
            {
                throw new InvalidOperationException(
                    $"Could not inspect '{symbol.FamilyName}: {symbol.Name}'. " +
                    $"Family placement type: {symbol.Family.FamilyPlacementType}. " +
                    $"Point placement: {pointPlacementFailure.Message} " +
                    $"Level placement: {levelPlacementFailure.Message}",
                    levelPlacementFailure);
            }
        }

        throw new InvalidOperationException(
            $"Could not inspect '{symbol.FamilyName}: {symbol.Name}'. " +
            $"Family placement type: {symbol.Family.FamilyPlacementType}. " +
            pointPlacementFailure?.Message,
            pointPlacementFailure);
    }

    private static double MeasureJunctionDirections(
        IReadOnlyList<XYZ> directions)
    {
        if (directions.Count != 3 ||
            directions.Any(direction => direction.GetLength() <= 1e-9))
            return double.NaN;

        int first = 0;
        int second = 1;
        // Connector arrows are not authored consistently across manufacturers:
        // the two run connectors may point in opposite directions or the same
        // direction. Their axes are still the most parallel pair, so compare the
        // absolute dot product instead of requiring a particular arrow direction.
        double greatestParallelism = double.MinValue;
        for (int i = 0; i < directions.Count - 1; i++)
        {
            for (int j = i + 1; j < directions.Count; j++)
            {
                double dot = directions[i].Normalize().DotProduct(
                    directions[j].Normalize());
                double parallelism = Math.Abs(dot);
                if (parallelism > greatestParallelism)
                {
                    greatestParallelism = parallelism;
                    first = i;
                    second = j;
                }
            }
        }
        if (greatestParallelism < 0.99)
            return double.NaN;

        int branchIndex = Enumerable.Range(0, directions.Count)
            .Single(index => index != first && index != second);
        double firstAngle = AcuteDegrees(
            directions[branchIndex],
            directions[first]);
        double secondAngle = AcuteDegrees(
            directions[branchIndex],
            directions[second]);
        double measured = Math.Min(firstAngle, secondAngle);
        return measured is > 1.0 and <= 90.001
            ? measured
            : double.NaN;
    }

    private static (int First, int Second) MostOppositeConnectorPair(
        IReadOnlyList<Connector> connectors)
    {
        int first = 0;
        int second = 1;
        // Treat connector axes as unoriented lines. Family authors may reverse
        // either connector arrow without changing the actual Y geometry.
        double greatestParallelism = double.MinValue;
        for (int i = 0; i < connectors.Count - 1; i++)
        {
            XYZ? a = connectors[i].CoordinateSystem?.BasisZ;
            if (a is null) continue;
            for (int j = i + 1; j < connectors.Count; j++)
            {
                XYZ? b = connectors[j].CoordinateSystem?.BasisZ;
                if (b is null) continue;
                double parallelism = Math.Abs(a.Normalize().DotProduct(b.Normalize()));
                if (parallelism > greatestParallelism)
                {
                    greatestParallelism = parallelism;
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
        double desiredFittingAngleDegrees,
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

        ConfigureFlexibleJunctionAngle(
            document,
            junction,
            desiredFittingAngleDegrees);

        ApplyJunctionConnectorSizes(
            document,
            junction,
            mainDiameter,
            branchDiameter);
        ConfigureFlexibleJunctionAngle(
            document,
            junction,
            desiredFittingAngleDegrees);

        JunctionConnectors initial = JunctionConnectorsOf(junction);
        XYZ initialIntersection = JunctionIntersectionFromConnectorAxes(initial);
        // Use the physical ray from the connector-axis intersection to each
        // connector. BasisZ arrow signs vary between otherwise valid Y families.
        XYZ sourceRun = ConnectorRay(initial.RunA, initialIntersection);
        XYZ sourceBranch = ConnectorRay(initial.Branch, initialIntersection);
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
        XYZ afterRunIntersection = JunctionIntersectionFromConnectorAxes(afterRun);
        XYZ currentBranch = ConnectorRay(afterRun.Branch, afterRunIntersection);
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
        XYZ fittingIntersection = JunctionIntersectionFromConnectorAxes(oriented);
        ElementTransformUtils.MoveElement(
            document,
            junction.Id,
            junctionPoint - fittingIntersection);
        document.Regenerate();

        JunctionConnectors fitted = JunctionConnectorsOf(junction);
        XYZ targetDirection = PipeDirectionAwayFrom(target, junctionPoint).Normalize();
        Connector targetFitting =
            (fitted.RunA.Origin - junctionPoint).Normalize().DotProduct(targetDirection) >=
            (fitted.RunB.Origin - junctionPoint).Normalize().DotProduct(targetDirection)
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

        // A few socket families expose the Y angle as a writable instance
        // parameter and initialize every new instance at 90 degrees. Confirm
        // the connected fitting did not flex back to that default.
        ConfigureFlexibleJunctionAngle(
            document,
            junction,
            desiredFittingAngleDegrees);
        double connectedAngle = MeasureJunctionConnectorAngle(
            DrainSelection.GetConnectors(junction));
        if (double.IsNaN(connectedAngle) ||
            Math.Abs(connectedAngle - desiredFittingAngleDegrees) >
            PipePlanAngleToleranceDegrees)
            throw new InvalidOperationException(
                $"The placed fitting flexed to {connectedAngle:0.###} degrees instead of the selected " +
                $"Y angle {desiredFittingAngleDegrees:0.###} degrees.");
        return junction;
    }

    private static void ConfigureFlexibleJunctionAngle(
        Document document,
        FamilyInstance junction,
        double desiredAngleDegrees)
    {
        if (double.IsNaN(desiredAngleDegrees))
            return;

        double currentAngle = MeasureJunctionConnectorAngle(
            DrainSelection.GetConnectors(junction));
        if (!double.IsNaN(currentAngle) &&
            Math.Abs(currentAngle - desiredAngleDegrees) <=
            PipePlanAngleToleranceDegrees)
            return;

        IReadOnlyList<string> angleParameterNames = junction.Parameters
            .Cast<Parameter>()
            .Where(parameter =>
                parameter.StorageType == StorageType.Double &&
                !parameter.IsReadOnly &&
                parameter.Definition.GetDataType().Equals(SpecTypeId.Angle))
            .Select(parameter => parameter.Definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FlexibleAngleParameterPriority)
            .ToList();

        double desiredRadians = desiredAngleDegrees * Math.PI / 180.0;
        var attempts = new List<string>();
        foreach (string parameterName in angleParameterNames)
        {
            using var probe = new SubTransaction(document);
            probe.Start();
            try
            {
                Parameter? angleParameter = junction
                    .GetParameters(parameterName)
                    .FirstOrDefault(parameter =>
                        parameter.StorageType == StorageType.Double &&
                        !parameter.IsReadOnly);
                if (angleParameter is null || !angleParameter.Set(desiredRadians))
                {
                    probe.RollBack();
                    continue;
                }

                document.Regenerate();
                double measured = MeasureJunctionConnectorAngle(
                    DrainSelection.GetConnectors(junction));
                attempts.Add($"{parameterName} -> {measured:0.###} deg");
                if (!double.IsNaN(measured) &&
                    Math.Abs(measured - desiredAngleDegrees) <=
                    PipePlanAngleToleranceDegrees)
                {
                    if (probe.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            $"Revit rolled back angle parameter '{parameterName}'.");
                    return;
                }

                probe.RollBack();
            }
            catch (Exception exception)
            {
                attempts.Add($"{parameterName}: {exception.Message}");
                if (probe.GetStatus() == TransactionStatus.Started)
                    probe.RollBack();
            }
        }

        throw new InvalidOperationException(
            $"A new '{junction.Symbol.FamilyName}: {junction.Symbol.Name}' instance resolved at " +
            $"{currentAngle:0.###} degrees. Revit could not set its flexible instance angle to " +
            $"{desiredAngleDegrees:0.###} degrees. " +
            (attempts.Count == 0
                ? "No writable Angle instance parameter was found."
                : string.Join(" | ", attempts.Take(6))));
    }

    private static int FlexibleAngleParameterPriority(string parameterName)
    {
        bool main = parameterName.Contains("main", StringComparison.OrdinalIgnoreCase);
        bool rotate = parameterName.Contains("rotate", StringComparison.OrdinalIgnoreCase);
        bool angle = parameterName.Contains("angle", StringComparison.OrdinalIgnoreCase);
        if (main && rotate) return 0;
        if (main && angle) return 1;
        if (angle) return 2;
        if (rotate) return 3;
        return 4;
    }

    private static void ValidateConnectedJunction(
        FamilyInstance junction, Pipe main, Pipe split, Pipe branch,
        ElementId expectedSymbolId, double mainDiameter, double branchDiameter)
    {
        if (junction.Symbol.Id != expectedSymbolId)
            throw new InvalidOperationException("Revit substituted a different fitting type for the selected Y.");
        IReadOnlyList<Connector> connectors = DrainSelection.GetConnectors(junction);
        if (connectors.Count != 3)
            throw new InvalidOperationException("The selected Y must have exactly three round piping connectors.");
        double fittingAngle = MeasureJunctionConnectorAngle(connectors);
        if (!IsSupportedYAngle(fittingAngle))
            throw new InvalidOperationException(
                $"The connected Y resolved at {fittingAngle:0.###} degrees. " +
                $"Drain connections require {TargetYAngleDegrees:0.#} +/- " +
                $"{YAngleToleranceDegrees:0.#} degrees.");
        var used = new HashSet<int>();
        foreach (Pipe pipe in new[] { main, split, branch })
        {
            Connector? fittingEnd = connectors.FirstOrDefault(end =>
                DrainSelection.GetConnectors(pipe).Any(pipeEnd =>
                    pipeEnd.IsConnectedTo(end) &&
                    pipeEnd.Origin.DistanceTo(end.Origin) <= DrainGeometry.Mm(1)));
            double required = pipe.Id == branch.Id ? branchDiameter : mainDiameter;
            if (fittingEnd is null || !used.Add(fittingEnd.Id) ||
                Math.Abs(fittingEnd.Radius * 2 - required) > DrainGeometry.Mm(0.5))
                throw new InvalidOperationException(
                    $"Y connection to pipe {pipe.Id.CompatValue()} is missing or has the wrong diameter (required DN{DrainGeometry.ToMm(required):0.#}).");
        }
        // Connector ownership, diameter, and the measured connector angle are
        // authoritative. Family parameter names vary between manufacturers.
    }

    private static double ValidateMainConnectionPlanAngle(
        Pipe branch,
        Pipe main,
        string label,
        int caseNumber)
    {
        if (caseNumber == 3)
        {
            double verticalPlaneAngle = MeasureMainConnection3dAngle(
                branch,
                main);
            if (Math.Abs(verticalPlaneAngle - TargetYAngleDegrees) >
                PipePlanAngleToleranceDegrees)
                throw new InvalidOperationException(
                    $"{label}: branch-to-main vertical-plane angle is " +
                    $"{verticalPlaneAngle:0.###} degrees. It must remain " +
                    $"{TargetYAngleDegrees:0.##} degrees (+/- " +
                    $"{PipePlanAngleToleranceDegrees:0.##}).");
            return verticalPlaneAngle;
        }

        XYZ branchAxis = PipeAxis(branch);
        XYZ mainAxis = PipeAxis(main);
        double branchPlanLength = Math.Sqrt(
            branchAxis.X * branchAxis.X + branchAxis.Y * branchAxis.Y);
        double mainPlanLength = Math.Sqrt(
            mainAxis.X * mainAxis.X + mainAxis.Y * mainAxis.Y);
        if (branchPlanLength <= 1e-9 || mainPlanLength <= 1e-9)
            throw new InvalidOperationException(
                $"{label}: the branch or main has no usable plan direction.");

        double cosine = Math.Abs(
            (branchAxis.X * mainAxis.X + branchAxis.Y * mainAxis.Y) /
            (branchPlanLength * mainPlanLength));
        cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
        double angle = Math.Acos(cosine) * 180.0 / Math.PI;
        if (Math.Abs(angle - TargetYAngleDegrees) >
            PipePlanAngleToleranceDegrees)
            throw new InvalidOperationException(
                $"{label}: branch-to-main plan angle is {angle:0.###} degrees. " +
                $"It must remain {TargetYAngleDegrees:0.##} degrees (+/- " +
                $"{PipePlanAngleToleranceDegrees:0.##}); the route was rejected " +
                "instead of allowing Revit to skew the pipe.");
        return angle;
    }

    private static double MeasureMainConnection3dAngle(
        Pipe branch,
        Pipe main)
    {
        XYZ branchAxis = PipeAxis(branch);
        XYZ mainAxis = PipeAxis(main);
        double cosine = Math.Abs(branchAxis.DotProduct(mainAxis));
        cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
        return Math.Acos(cosine) * 180.0 / Math.PI;
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
        if (connectors.Count != 3)
            throw new InvalidOperationException(
                $"The routed Y family has {connectors.Count} round piping connectors; exactly three are required.");
        XYZ center = junction.Location is LocationPoint locationPoint
            ? locationPoint.Point
            : junction.GetTransform().Origin;
        (int first, int second) = MostOppositeConnectorPair(connectors);
        Connector branch = connectors
            .Where((_, index) => index != first && index != second)
            .OrderByDescending(item => item.Radius)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The branch connector of the routed Y family could not be identified.");
        return new JunctionConnectors(connectors[first], connectors[second], branch, center);
    }

    private static XYZ RunDirectionFromConnectorOrigins(JunctionConnectors connectors)
    {
        XYZ direction = connectors.RunA.Origin - connectors.RunB.Origin;
        if (direction.GetLength() <= 1e-9)
            return connectors.RunA.CoordinateSystem.BasisZ.Normalize();
        return direction.Normalize();
    }

    private static XYZ JunctionIntersectionFromConnectorAxes(
        JunctionConnectors connectors)
    {
        // Axis intersection does not depend on the sign of BasisZ.
        XYZ runDirection = connectors.RunA.CoordinateSystem.BasisZ.Normalize();
        XYZ branchDirection = connectors.Branch.CoordinateSystem.BasisZ.Normalize();
        return ClosestAxisIntersection(
            connectors.RunA.Origin,
            runDirection,
            connectors.Branch.Origin,
            branchDirection);
    }

    private static XYZ ConnectorRay(Connector connector, XYZ axisIntersection)
    {
        // Pipes connect along BasisZ, not necessarily along the vector from the
        // family insertion point to the connector origin. Socket families can
        // have a small eccentric origin offset. Use the real connector axis and
        // use its location relative to the common axis intersection only to
        // choose the outward sign.
        XYZ axis = connector.CoordinateSystem.BasisZ.Normalize();
        XYZ radial = connector.Origin - axisIntersection;
        if (radial.GetLength() > 1e-8 && axis.DotProduct(radial) < 0.0)
            axis = -axis;
        return axis;
    }

    private static XYZ OutwardConnectorDirection(Connector connector, XYZ center)
    {
        XYZ direction = connector.CoordinateSystem.BasisZ.Normalize();
        XYZ radial = connector.Origin - center;
        if (radial.GetLength() > 1e-9 && direction.DotProduct(radial) < 0)
            direction = -direction;
        return direction;
    }

    private static XYZ BranchDirectionFromConnectorOrigins(
        JunctionConnectors connectors)
    {
        XYZ direction = connectors.Branch.Origin - connectors.Center;
        if (direction.GetLength() <= 1e-9)
            return connectors.Branch.CoordinateSystem.BasisZ.Normalize();
        return direction.Normalize();
    }

    private static XYZ PipeDirectionAwayFrom(Pipe pipe, XYZ junctionPoint)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.CompatValue()} has no usable centerline.");
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
                $"Pipe {pipe.Id.CompatValue()} has no usable centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        XYZ originalAxis = end - start;
        double minimumRemainingLength = DrainGeometry.Mm(2);
        if (start.DistanceTo(oldPoint) <= DrainGeometry.Mm(3))
        {
            XYZ remaining = end - newPoint;
            if (remaining.GetLength() < minimumRemainingLength ||
                remaining.DotProduct(originalAxis) <= 0.0)
                throw new InvalidOperationException(
                    $"Pipe {pipe.Id.CompatValue()} is shorter than this fitting's takeout; " +
                    "moving its start would reverse the pipe.");
            location.Curve = Line.CreateBound(newPoint, end);
        }
        else if (end.DistanceTo(oldPoint) <= DrainGeometry.Mm(3))
        {
            XYZ remaining = newPoint - start;
            if (remaining.GetLength() < minimumRemainingLength ||
                remaining.DotProduct(originalAxis) <= 0.0)
                throw new InvalidOperationException(
                    $"Pipe {pipe.Id.CompatValue()} is shorter than this fitting's takeout; " +
                    "moving its end would reverse the pipe.");
            location.Curve = Line.CreateBound(start, newPoint);
        }
        else
            throw new InvalidOperationException(
                $"Pipe {pipe.Id.CompatValue()} has no endpoint at the Y junction center.");
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
                $"Revit did not connect pipe {pipe.Id.CompatValue()} to the placed Y family.");
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

    private static Pipe RequirePipe(
        Document document,
        ElementId pipeId,
        string label)
    {
        return document.GetElement(pipeId) as Pipe
            ?? throw new InvalidOperationException(
                $"{label} is no longer available after Revit regenerated the model.");
    }

    private static void SetDiameter(Pipe pipe, double diameterValue)
    {
        Parameter? diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (diameter is { IsReadOnly: false })
            diameter.Set(diameterValue);
    }

    private static void ValidateRequestedSlope(
        Pipe pipe,
        double requestedSlopePercent,
        string label,
        double tolerancePercentagePoints = 0.005)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException($"{label} has no readable centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        double planLength = PlanDistance(start, end);
        if (planLength <= DrainGeometry.Mm(10))
            throw new InvalidOperationException($"{label} is too short to verify its slope.");
        double actualSlope = Math.Abs(end.Z - start.Z) / planLength * 100.0;
        if (Math.Abs(actualSlope - Math.Abs(requestedSlopePercent)) >
            tolerancePercentagePoints)
            throw new InvalidOperationException(
                $"{label} resolved at {actualSlope:0.####}% instead of the requested " +
                $"{Math.Abs(requestedSlopePercent):0.####}%; this fitting candidate was rejected.");
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

    private static SourceConnection ConnectCase04Source(
        Document document,
        Context context,
        Connector source,
        Pipe stub,
        DrainRoute route)
    {
        ElementId sourceId = source.Owner.Id;
        ElementId stubId = stub.Id;
        ElementId targetPipeTypeId = context.TargetPipeType.Id;
        Connector pipeConnector = DrainSelection.ConnectorNear(
            stub,
            route.DrainOrigin,
            true);
        double sourceDiameter = source.Radius * 2.0;
        double branchDiameter = pipeConnector.Radius * 2.0;
        if (stub.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "Case 04 could not read the device-side pipe for its source connection.");
        XYZ first = location.Curve.GetEndPoint(0);
        XYZ second = location.Curve.GetEndPoint(1);
        XYZ farEnd = first.DistanceTo(route.DrainOrigin) <=
                     second.DistanceTo(route.DrainOrigin)
            ? second
            : first;
        if (Math.Abs(source.Radius - pipeConnector.Radius) <= DrainGeometry.Mm(0.25))
        {
            source.ConnectTo(pipeConnector);
            // Revit can pull the pipe laterally when the fixture connector has
            // a family-instance offset. Restore the requested vertical drop for
            // the equal-DN path as well as for the reducer path below.
            location.Curve = Line.CreateBound(route.DrainOrigin, farEnd);
            document.Regenerate();
            stub = RequirePipe(document, stubId, "Case 04 device stub");
            ValidateVerticalDeviceStub(stub, route.DrainOrigin);
            return new SourceConnection(null, null);
        }

        double availableLength = route.DrainOrigin.DistanceTo(farEnd);
        double minimumPiece = DrainGeometry.Mm(25);
        double visibleOpenGap = DrainGeometry.Mm(25);
        double desiredAdapterLength = Math.Max(
            DrainGeometry.Mm(100),
            Math.Max(sourceDiameter, branchDiameter) * 1.5);
        double adapterLength = Math.Min(
            desiredAdapterLength,
            availableLength - minimumPiece - visibleOpenGap);
        if (adapterLength < minimumPiece)
            throw new InvalidOperationException(
                "Case 04/06 needs at least 75 mm of straight drop below the device " +
                "to change from the device DN to the main DN and preserve a visible open gap when no reducer is available.");

        XYZ transitionPoint = route.DrainOrigin +
            (farEnd - route.DrainOrigin).Normalize() * adapterLength;
        MovePipeEnd(stub, route.DrainOrigin, transitionPoint);
        document.Regenerate();
        stub = RequirePipe(document, stubId, "Case 04 main-DN device stub");
        Pipe adapter = CreatePipe(
            document,
            context,
            targetPipeTypeId,
            sourceDiameter,
            route.DrainOrigin,
            transitionPoint);
        ElementId adapterId = adapter.Id;
        document.Regenerate();

        adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
        Connector currentSource = DrainSelection.ChooseSourceConnector(
            document.GetElement(sourceId)
                ?? throw new InvalidOperationException(
                    "The source drain disappeared before its adapter was connected."));
        currentSource.ConnectTo(DrainSelection.ConnectorNear(
            adapter,
            route.DrainOrigin,
            true));
        document.Regenerate();
        adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
        // Reassert the requested vertical centerline after connecting to the
        // fixture. Some connector definitions apply a small instance offset.
        // Case 04 must keep the device drop vertical in plan.
        if (adapter.Location is not LocationCurve adapterLocation)
            throw new InvalidOperationException(
                "Case 04 could not read the device-DN adapter centerline.");
        adapterLocation.Curve = Line.CreateBound(
            route.DrainOrigin,
            transitionPoint);
        document.Regenerate();
        adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
        stub = RequirePipe(document, stubId, "Case 04 main-DN device stub");
        ValidateVerticalDeviceStub(adapter, route.DrainOrigin);
        using var reductionAttempt = new SubTransaction(document);
        reductionAttempt.Start();
        try
        {
            FamilyInstance transition = document.Create.NewTransitionFitting(
                DrainSelection.ConnectorNear(adapter, transitionPoint, true),
                DrainSelection.ConnectorNear(stub, transitionPoint, true));
            ElementId transitionId = transition.Id;
            document.Regenerate();
            adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
            stub = RequirePipe(document, stubId, "Case 04 main-DN device stub");
            // Reject eccentric or mis-oriented reducers that pull either side
            // of the device drop away from the same vertical plan point. The
            // catch below removes only that reducer and leaves a clean open DN
            // break instead of accepting the visibly slanted drop.
            ValidateVerticalDeviceStub(adapter, route.DrainOrigin);
            ValidateVerticalDeviceStub(stub, transitionPoint);
            ValidateVerticalPipeAlignment(adapter, stub);
            if (reductionAttempt.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    "Revit rolled back the device-side reducer.");
            adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
            FamilyInstance committedTransition =
                document.GetElement(transitionId) as FamilyInstance
                ?? throw new InvalidOperationException(
                    "The device-side reducer disappeared after it was committed.");
            return new SourceConnection(adapter, committedTransition);
        }
        catch (Exception exception)
        {
            if (reductionAttempt.GetStatus() == TransactionStatus.Started)
                reductionAttempt.RollBack();
            document.Regenerate();

            // Keep the adapter connected to the device and the main-sized stub
            // connected to the completed route. Move the main-sized pipe end
            // downward to leave a visible gap. Coincident open connectors looked
            // connected in plan and hid the missing reducer from the user.
            XYZ dropDirection = (farEnd - route.DrainOrigin).Normalize();
            XYZ openMainEnd = transitionPoint + dropDirection * visibleOpenGap;
            adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
            stub = RequirePipe(document, stubId, "Case 04 main-DN device stub");
            MovePipeEnd(stub, transitionPoint, openMainEnd);
            document.Regenerate();
            stub = RequirePipe(document, stubId, "Case 04 main-DN device stub");
            adapter = RequirePipe(document, adapterId, "Case 04 device-DN adapter");
            ValidateVerticalDeviceStub(stub, openMainEnd);
            return new SourceConnection(
                adapter,
                null,
                true,
                (transitionPoint + openMainEnd) * 0.5,
                $"No reducer could be created between DN{DrainGeometry.ToMm(sourceDiameter):0.#} " +
                $"and DN{DrainGeometry.ToMm(branchDiameter):0.#}. " +
                $"A {DrainGeometry.ToMm(visibleOpenGap):0.#} mm open gap was left at the device drop: " +
                exception.Message);
        }
    }

    private static void AddSourceConnectionIds(
        List<ElementId> ids,
        SourceConnection connection)
    {
        if (connection.Adapter is not null)
            ids.Add(connection.Adapter.Id);
        if (connection.Transition is not null)
            ids.Add(connection.Transition.Id);
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
        IntersectionResult projection = location.Curve.Project(endpointPreference)
            ?? throw new InvalidOperationException(
                "The picked point could not be projected to the selected main.");
        XYZ pickedPoint = projection.XYZPoint;
        double interiorClearance = Math.Max(
            DrainGeometry.Mm(25),
            context.MainDiameter);

        // Respect an intentional interior click. Drainage must continue from
        // the new branch connection toward the lower end of the main. Keeping
        // the longer side here can retain the uphill half and make Cases 04/05
        // rise after their endpoint elbow.
        if (pickedPoint.DistanceTo(start) > interiorClearance &&
            pickedPoint.DistanceTo(end) > interiorClearance)
        {
            XYZ inside = RetainedMainEndpoint(start, end, pickedPoint);
            return (inside, pickedPoint);
        }

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
                "The selected main has no open end near the picked point. " +
                "Pick farther inside the pipe so the shorter surplus tail can be removed.");
        bool atStart = openEnd.Origin.DistanceTo(start) <= openEnd.Origin.DistanceTo(end);
        return atStart ? (end, start) : (start, end);
    }

    private static XYZ RetainedMainEndpoint(
        XYZ start,
        XYZ end,
        XYZ connectionPoint)
    {
        double elevationDifference = end.Z - start.Z;
        if (Math.Abs(elevationDifference) > DrainGeometry.Mm(0.5))
            return elevationDifference > 0.0 ? start : end;

        // A level main has no downhill side, so retain the larger useful side
        // and remove the shorter surplus tail as before.
        return connectionPoint.DistanceTo(start) >= connectionPoint.DistanceTo(end)
            ? start
            : end;
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

    private sealed record SourceConnection(
        Pipe? Adapter,
        FamilyInstance? Transition,
        bool HasOpenBreak = false,
        XYZ? BreakPoint = null,
        string? Warning = null);

    private sealed record JunctionSeed(Pipe Pipe, FamilyInstance Transition);

    private sealed record JunctionConnectors(
        Connector RunA,
        Connector RunB,
        Connector Branch,
        XYZ Center);

    private sealed record PreparedJunctionType(
        ElementId TypeId,
        ElementId PartId,
        string Label,
        double FittingAngleDegrees,
        bool NativeRouting = true);

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
