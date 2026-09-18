using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace FamilyMEP.Plugin.SmartDimension;

internal sealed record SmartDimensionResult(
    int Created,
    int Skipped,
    int Grouped,
    bool Cancelled,
    string Summary,
    IReadOnlyList<string> Messages);

/// <summary>
/// Creates real Revit dimensions for the categories checked in Smart Tag.
/// Rectangular ducts use physical duct-edge references (never insulation);
/// round ducts and every supported family category use center references.
/// Every chain is anchored to an outside Grid or Wall reference.
/// </summary>
internal static class SmartDimensionService
{
    private const double Epsilon = 1e-7;
    private static readonly Guid OwnershipSchemaId = new("B3BB2BB1-B31A-48D0-93E7-0FB9C593A129");
    private static readonly BuiltInCategory[] ObstacleCategories =
    [
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_DuctInsulations,
        BuiltInCategory.OST_DuctLinings,
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeInsulations,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_Walls
    ];

    public static SmartDimensionResult Create(
        UIApplication application,
        ISet<string> selectedCategoryKeys,
        double maximumElementGapMillimeters = 2500.0)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        Document document = uidoc.Document;
        View view = document.ActiveView;
        if (view.IsTemplate || view is ViewSheet or ViewSchedule ||
            view.ViewType is not ViewType.FloorPlan and not ViewType.CeilingPlan and not ViewType.EngineeringPlan)
        {
            throw new InvalidOperationException(
                "Smart Dim requires an active Floor Plan, Ceiling Plan, or Engineering Plan.");
        }

        if (selectedCategoryKeys.Count == 0)
            throw new InvalidOperationException("Select one or more categories in Smart Tag Setup first.");
        if (double.IsNaN(maximumElementGapMillimeters) ||
            double.IsInfinity(maximumElementGapMillimeters) ||
            maximumElementGapMillimeters <= 0)
            throw new InvalidOperationException("Smart Dim maximum element gap must be greater than zero.");

        ViewBasis basis = ViewBasis.Create(view);
        DimensionZone sampleZone;
        try
        {
            PickedBox picked = uidoc.Selection.PickBox(
                PickBoxStyle.Directional,
                "Smart Dim: drag one SAMPLE cluster zone. Its width and height will partition all checked model elements. Press Esc to cancel.");
            sampleZone = DimensionZone.FromPickedBox(picked, basis);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new SmartDimensionResult(
                0, 0, 0, true,
                "Smart Dim zone selection was cancelled; Revit was not changed.",
                Array.Empty<string>());
        }

        List<Element> targets = new FilteredElementCollector(document, view.Id)
            .WhereElementIsNotElementType()
            .Where(element => IsSupported(element, selectedCategoryKeys))
            .ToList();

        targets = targets.GroupBy(item => item.Id.CompatValue()).Select(group => group.First()).ToList();
        if (targets.Count == 0)
        {
            return new SmartDimensionResult(
                0, 0, 0, false,
                "No supported element from the checked categories is visible in the active view.",
                ["The picked rectangle is a sample cluster size, not a selection filter. Rectangular ducts use physical edges; round ducts and all other checked categories use centres."]);
        }

        List<DatumReference> datums = CollectDatums(document, view, basis);
        List<Obstacle> obstacles;
        HashSet<string> existing;
        var messages = new List<string>();
        int created = 0;
        int centerDimensions = 0;
        int ductDimensions = 0;
        var dimensionedTargetIds = new HashSet<long>();

        List<Duct> selectedDucts = targets.OfType<Duct>().Where(IsRectangularDuct).ToList();
        List<Element> centerTargets = targets
            .Where(item => item is FamilyInstance || item is MEPCurve)
            .Where(item => item is not Duct duct || !IsRectangularDuct(duct))
            .ToList();

        using var transaction = new Transaction(document, "FamilyMEP - Smart Dimensions");
        transaction.Start();
        int replaced = DeleteOwnedDimensions(
            document, view, targets.Select(item => item.Id.CompatValue()).ToHashSet());
        if (replaced > 0)
            messages.Add($"Replaced {replaced} previous Smart Dim dimension(s) in this selection.");
        obstacles = CollectObstacles(document, view, basis);
        existing = CollectExistingDimensionSignatures(document, view);
        centerDimensions = CreateCenterChains(
            document, view, centerTargets, basis, datums, obstacles, existing, messages,
            dimensionedTargetIds, maximumElementGapMillimeters, sampleZone);
        created += centerDimensions;

        ductDimensions = CreateDuctEdgeChains(
            document, view, selectedDucts, basis, datums, obstacles, existing,
            messages, dimensionedTargetIds, maximumElementGapMillimeters, sampleZone);
        created += ductDimensions;

        // A mixed family chain can be rejected when one family has no usable
        // authored centre plane. Do not let that one reference discard the
        // remaining devices. Give every still-uncovered family/round MEP
        // element its own two-datum horizontal and vertical fallback pass.
        List<Element> uncoveredCenterElements = centerTargets
            .Where(item => !dimensionedTargetIds.Contains(item.Id.CompatValue()))
            .ToList();
        int recoveredCenterDimensions = CreateUncoveredCenterFallbacks(
            document, view, uncoveredCenterElements, basis, datums, obstacles,
            existing, messages, dimensionedTargetIds);
        centerDimensions += recoveredCenterDimensions;
        created += recoveredCenterDimensions;

        if (created == 0)
            transaction.RollBack();
        else
            transaction.Commit();

        if (created > 0)
            uidoc.RefreshActiveView();

        int dimensionedCenters = centerTargets.Count(item => dimensionedTargetIds.Contains(item.Id.CompatValue()));
        int dimensionedDucts = selectedDucts.Count(item => dimensionedTargetIds.Contains(item.Id.CompatValue()));
        int grouped = Math.Max(0, dimensionedDucts - ductDimensions);
        int skipped = centerTargets.Count - dimensionedCenters + selectedDucts.Count - dimensionedDucts;
        int gridDatums = datums.Count(item => item.Kind == "Grid");
        int wallDatums = datums.Count - gridDatums;
        string summary =
            $"Sample cluster zone {sampleZone.Width * 304.8:0.#} x {sampleZone.Height * 304.8:0.#} model mm applied across the active view; " +
            $"Datum references {gridDatums} Grid/{wallDatums} Wall. " +
            $"Maximum consecutive-element gap {maximumElementGapMillimeters:0.#} model mm. " +
            $"Datum-to-center chains {centerDimensions} ({dimensionedCenters}/{centerTargets.Count} elements), " +
            $"rectangular duct edge chains {ductDimensions} " +
            $"({dimensionedDucts}/{selectedDucts.Count} ducts covered).";
        if (messages.Count == 0)
            messages.Add("All available authored references were dimensioned.");
        return new SmartDimensionResult(created, skipped, grouped, false, summary, messages.Take(30).ToList());
    }

    private static int CreateUncoveredCenterFallbacks(
        Document document,
        View view,
        IReadOnlyList<Element> elements,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        HashSet<long> dimensionedTargetIds)
    {
        List<CenterTarget> targets = elements
            .Select(element => BuildCenterTarget(element, view, basis))
            .Where(item => item is not null)
            .Cast<CenterTarget>()
            .ToList();
        if (targets.Count == 0) return 0;

        int created = 0;
        created += CreateIndividualCenterDimensions(
            document, view, targets, horizontal: true, basis, datums,
            obstacles, existing, messages, dimensionedTargetIds, preferredSide: -1);
        created += CreateIndividualCenterDimensions(
            document, view, targets, horizontal: false, basis, datums,
            obstacles, existing, messages, dimensionedTargetIds, preferredSide: 1);
        return created;
    }

    private static int CreateFamilySizeDimensions(
        Document document,
        View view,
        FamilyInstance instance,
        ViewBasis basis,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages)
    {
        Transform transform = instance.GetTransform();
        XYZ axisX = basis.FlattenAndNormalize(transform.BasisX);
        XYZ axisY = basis.FlattenAndNormalize(transform.BasisY);
        if (axisX.GetLength() < Epsilon || axisY.GetLength() < Epsilon)
            return 0;

        int result = 0;
        result += TryCreateFamilyPair(
            document, view, instance,
            FamilyInstanceReferenceType.Left,
            FamilyInstanceReferenceType.Right,
            axisX, axisY, basis, obstacles, existing, messages);
        result += TryCreateFamilyPair(
            document, view, instance,
            FamilyInstanceReferenceType.Front,
            FamilyInstanceReferenceType.Back,
            axisY, axisX, basis, obstacles, existing, messages);
        return result;
    }

    private static int TryCreateFamilyPair(
        Document document,
        View view,
        FamilyInstance instance,
        FamilyInstanceReferenceType firstType,
        FamilyInstanceReferenceType secondType,
        XYZ measureAxis,
        XYZ offsetAxis,
        ViewBasis basis,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages)
    {
        Reference? first = instance.GetReferences(firstType).FirstOrDefault();
        Reference? second = instance.GetReferences(secondType).FirstOrDefault();
        if (first is null || second is null)
        {
            AddMessage(messages,
                $"{instance.Name}: missing authored {firstType}/{secondType} family references; that size direction was skipped.");
            return 0;
        }

        IReadOnlyList<XYZ> points = GetBoundingPoints(instance, view);
        if (points.Count == 0) return 0;
        (double minMeasure, double maxMeasure) = Extents(points, measureAxis);
        (double minOffset, double maxOffset) = Extents(points, offsetAxis);
        Line? line = ChooseClearLine(
            basis, measureAxis, offsetAxis,
            minMeasure, maxMeasure, minOffset, maxOffset,
            view.Scale, obstacles, instance.Id.CompatValue());
        if (line is null)
        {
            AddMessage(messages, $"{instance.Name}: no empty lane was found for the {firstType}/{secondType} size dimension.");
            return 0;
        }
        ReferenceArray references = ToReferenceArray(first, second);
        if (!TryCreateDimension(document, view, line, references, existing, messages, out Dimension? dimension))
            return 0;
        MarkOwned(dimension!, [instance.Id.CompatValue()]);
        AddDimensionObstacle(document, view, dimension!, basis, obstacles);
        return 1;
    }

    private static int CreateCenterChains(
        Document document,
        View view,
        IReadOnlyList<Element> elements,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        HashSet<long> dimensionedTargetIds,
        double maximumElementGapMillimeters,
        DimensionZone sampleZone)
    {
        // All categories checked by the user participate in the same local
        // drafting pass. Splitting Air Terminals, Accessories, Equipment, etc.
        // into separate passes produced several overlapping dimension strings
        // around one physical MEP bank. Shared station de-duplication below
        // now creates the clean mixed-category chains used in the sample.
        List<CenterTarget> targets = elements
            .Select(element => BuildCenterTarget(element, view, basis))
            .Where(item => item is not null)
            .Cast<CenterTarget>()
            .ToList();
        List<List<CenterTarget>> clusters = BuildLocalCenterClusters(
            targets,
            basis,
            maximumElementGapMillimeters,
            sampleZone);
        double maximumElementGap = maximumElementGapMillimeters / 304.8;
        var createdStationChains = new HashSet<string>(StringComparer.Ordinal);
        int created = 0;
        foreach (List<CenterTarget> cluster in clusters)
        {
            // Horizontal stations are separated into physical rows inside the
            // cluster. The upper row is dimensioned above and the lower row
            // below, matching conventional coordinated-MEP drafting.
            created += CreateCenterChainsForAxis(
                document, view, cluster, horizontal: true, basis, datums,
                obstacles, existing, messages, dimensionedTargetIds, maximumElementGap,
                createdStationChains);
            // One local cluster owns exactly one vertical station chain. Do not
            // create another vertical string for every X column.
            created += CreateCenterChainsForAxis(
                document, view, cluster, horizontal: false, basis, datums,
                obstacles, existing, messages, dimensionedTargetIds, maximumElementGap,
                createdStationChains);
        }
        return created;
    }

    private static List<List<CenterTarget>> BuildLocalCenterClusters(
        IReadOnlyList<CenterTarget> targets,
        ViewBasis basis,
        double maximumElementGapMillimeters,
        DimensionZone sampleZone)
    {
        double joinGap = maximumElementGapMillimeters / 304.8;
        double maximumWidth = Math.Max(sampleZone.Width, 1.0 / 304.8);
        double maximumHeight = Math.Max(sampleZone.Height, 1.0 / 304.8);
        var clusters = new List<List<CenterTarget>>();
        foreach (CenterTarget target in targets
                     .OrderByDescending(item => item.Center.DotProduct(basis.Up))
                     .ThenBy(item => item.Center.DotProduct(basis.Right))
                     .ThenBy(item => item.Element.Id.CompatValue()))
        {
            double x = target.Center.DotProduct(basis.Right);
            double y = target.Center.DotProduct(basis.Up);
            List<CenterTarget>? best = null;
            double bestDistance = double.MaxValue;
            foreach (List<CenterTarget> cluster in clusters)
            {
                double minX = Math.Min(x, cluster.Min(item => item.Center.DotProduct(basis.Right)));
                double maxX = Math.Max(x, cluster.Max(item => item.Center.DotProduct(basis.Right)));
                double minY = Math.Min(y, cluster.Min(item => item.Center.DotProduct(basis.Up)));
                double maxY = Math.Max(y, cluster.Max(item => item.Center.DotProduct(basis.Up)));
                if (maxX - minX > maximumWidth || maxY - minY > maximumHeight) continue;
                double nearest = cluster.Min(item =>
                {
                    double dx = Math.Abs(x - item.Center.DotProduct(basis.Right));
                    double dy = Math.Abs(y - item.Center.DotProduct(basis.Up));
                    return Math.Max(dx, dy);
                });
                if (nearest > joinGap || nearest >= bestDistance) continue;
                best = cluster;
                bestDistance = nearest;
            }
            if (best is null)
                clusters.Add([target]);
            else
                best.Add(target);
        }
        return clusters;
    }

    private static int CreateCenterChainsForAxis(
        Document document,
        View view,
        IReadOnlyList<CenterTarget> targets,
        bool horizontal,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        HashSet<long> dimensionedTargetIds,
        double maximumElementGap,
        HashSet<string> createdStationChains)
    {
        XYZ measureAxis = horizontal ? basis.Right : basis.Up;
        XYZ offsetAxis = horizontal ? basis.Up : basis.Right;
        double maxChainGap = maximumElementGap;
        double maxChainSpan = maximumElementGap * 4.0;
        var available = targets
            .Where(item => (horizontal ? item.XReference : item.YReference) is not null)
            .OrderBy(item => item.Center.DotProduct(offsetAxis))
            .ThenBy(item => item.Center.DotProduct(measureAxis))
            .ToList();

        // All categories checked by the user share one station set inside a
        // local physical cluster. Splitting by category or by a small Y offset
        // produced two identical strings for an Accessory + Air Terminal bank.
        // Equal stations are de-duplicated below, so one clean mixed-category
        // chain is both sufficient and easier to read.
        var rows = new List<List<CenterTarget>>();
        if (available.Count > 0) rows.Add(available);

        int created = 0;
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            List<CenterTarget> row = rows[rowIndex];
            double? alignedLaneOffset = null;
            // Put an upper row above its local cluster and a lower row below.
            // A vertical string is created once, on the right side.
            int preferredSide = horizontal
                ? rows.Count == 1
                    ? -1
                    : rowIndex < rows.Count / 2 ? -1 : 1
                : 1;
            var chains = new List<List<CenterTarget>>();
            foreach (CenterTarget target in row.OrderBy(item => item.Center.DotProduct(measureAxis)))
            {
                List<CenterTarget>? chain = chains.LastOrDefault();
                double coordinate = target.Center.DotProduct(measureAxis);
                // The user-defined consecutive-element gap governs both
                // horizontal and vertical chains. A tall bank is therefore
                // split into readable local vertical strings as well.
                if (chain is null ||
                    coordinate - chain.Last().Center.DotProduct(measureAxis) > maxChainGap ||
                    coordinate - chain[0].Center.DotProduct(measureAxis) > maxChainSpan)
                    chains.Add([target]);
                else
                    chain.Add(target);
            }

            foreach (List<CenterTarget> chain in chains)
            {
                List<double> centerCoordinates = chain
                    .Select(item => item.Center.DotProduct(measureAxis)).ToList();
                double minCenter = centerCoordinates.Min();
                double maxCenter = centerCoordinates.Max();
                DatumPair? datumPair = FindChainDatums(
                    datums, measureAxis, minCenter, maxCenter);
                if (datumPair is null)
                {
                    AddMessage(messages,
                        horizontal
                            ? $"Local center cluster ({chain.Count} item): two outside Wall/Grid endpoints were not found; trying each element separately."
                            : $"Local center cluster ({chain.Count} item): two outside Wall/Grid endpoints were not found for its single vertical chain; vertical dimension skipped.");
                    if (!horizontal) continue;
                    created += CreateIndividualCenterDimensions(
                        document, view, chain, horizontal, basis, datums,
                        obstacles, existing, messages, dimensionedTargetIds, preferredSide);
                    continue;
                }
                var coordinateReferences = chain
                    .Select(item =>
                    {
                        Reference? reference = horizontal ? item.XReference : item.YReference;
                        double coordinate = reference is null
                            ? item.Center.DotProduct(measureAxis)
                            : GetCenterReferenceCoordinate(item, reference, basis, measureAxis);
                        return (Coordinate: coordinate, Reference: reference);
                    })
                    .Where(item => item.Reference is not null)
                    .Select(item => (item.Coordinate, Reference: item.Reference!))
                    .Append((datumPair.Lower.Coordinate, datumPair.Lower.Reference))
                    .Append((datumPair.Upper.Coordinate, datumPair.Upper.Reference))
                    .OrderBy(item => item.Coordinate)
                    .ToList();
                // Revit accepts separate references at the same station but
                // displays a zero-length segment between them. Keep one real
                // reference per physical station before creating the chain.
                var uniqueStations = new List<(double Coordinate, Reference Reference)>();
                foreach (var candidate in coordinateReferences)
                {
                    if (uniqueStations.Count > 0 &&
                        Math.Abs(candidate.Coordinate - uniqueStations[^1].Coordinate) < 1.0 / 304.8)
                        continue;
                    uniqueStations.Add(candidate);
                }
                List<Reference> references = DistinctReferences(
                    document, uniqueStations.Select(item => item.Reference));
                if (references.Count < 2) continue;
                string stationSignature =
                    (horizontal ? "H:" : "V:") +
                    string.Join(",", uniqueStations.Select(item =>
                        Math.Round(item.Coordinate * 304.8, 0)));
                if (createdStationChains.Contains(stationSignature))
                {
                    AddMessage(messages,
                        $"Duplicate {(horizontal ? "horizontal" : "vertical")} station chain skipped to prevent overlapping dimensions.");
                    foreach (CenterTarget item in chain)
                        dimensionedTargetIds.Add(item.Element.Id.CompatValue());
                    continue;
                }

                IReadOnlyList<XYZ> points = chain
                    .SelectMany(item => GetBoundingPoints(item.Element, view)).ToList();
                if (points.Count == 0) continue;
                (double minOffset, double maxOffset) = Extents(points, offsetAxis);
                double minMeasure = datumPair.Lower.Coordinate;
                double maxMeasure = datumPair.Upper.Coordinate;
                Line? line = ChooseClearLine(
                    basis, measureAxis, offsetAxis,
                    minMeasure, maxMeasure, minOffset, maxOffset,
                    view.Scale, obstacles, -1, preferredSide, alignedLaneOffset,
                    minCenter, maxCenter);
                if (line is null)
                {
                    AddMessage(messages,
                        horizontal
                            ? $"Center chain ({chain.Count} item): no clear shared lane; trying each element separately."
                            : $"Center cluster ({chain.Count} item): no clear lane for its single vertical chain; vertical dimension skipped.");
                    if (!horizontal) continue;
                    created += CreateIndividualCenterDimensions(
                        document, view, chain, horizontal, basis, datums,
                        obstacles, existing, messages, dimensionedTargetIds, preferredSide);
                    continue;
                }
                alignedLaneOffset ??= line.GetEndPoint(0).DotProduct(offsetAxis);

                if (!TryCreateDimension(
                        document, view, line, ToReferenceArray(references.ToArray()),
                        existing, messages, out Dimension? dimension))
                {
                    if (!horizontal)
                    {
                        AddMessage(messages,
                            $"Center cluster ({chain.Count} item): Revit rejected its single vertical chain; no individual vertical dimensions were created.");
                        continue;
                    }
                    created += CreateIndividualCenterDimensions(
                        document, view, chain, horizontal, basis, datums,
                        obstacles, existing, messages, dimensionedTargetIds, preferredSide);
                    continue;
                }
                MarkOwned(dimension!, chain.Select(item => item.Element.Id.CompatValue()));
                createdStationChains.Add(stationSignature);
                AddDimensionObstacle(document, view, dimension!, basis, obstacles);
                foreach (CenterTarget item in chain) dimensionedTargetIds.Add(item.Element.Id.CompatValue());
                created++;
            }
        }
        return created;
    }

    private static int CreateIndividualCenterDimensions(
        Document document,
        View view,
        IReadOnlyList<CenterTarget> targets,
        bool horizontal,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        HashSet<long> dimensionedTargetIds,
        int preferredSide = 0)
    {
        XYZ measureAxis = horizontal ? basis.Right : basis.Up;
        XYZ offsetAxis = horizontal ? basis.Up : basis.Right;
        int created = 0;
        foreach (CenterTarget target in targets)
        {
            Reference? center = horizontal ? target.XReference : target.YReference;
            if (center is null) continue;
            double coordinate = target.Center.DotProduct(measureAxis);
            DatumPair? datumPair = FindChainDatums(
                datums, measureAxis, coordinate, coordinate);
            if (datumPair is null) continue;
            double minMeasure = datumPair.Lower.Coordinate;
            double maxMeasure = datumPair.Upper.Coordinate;

            IReadOnlyList<XYZ> points = GetBoundingPoints(target.Element, view);
            if (points.Count == 0) continue;
            (double localMinMeasure, double localMaxMeasure) = Extents(points, measureAxis);
            (double minOffset, double maxOffset) = Extents(points, offsetAxis);
            Line? line = ChooseClearLine(
                basis, measureAxis, offsetAxis,
                minMeasure, maxMeasure, minOffset, maxOffset,
                view.Scale, obstacles, target.Element.Id.CompatValue(), preferredSide,
                alignedLaneOffset: null,
                collisionMinMeasure: localMinMeasure,
                collisionMaxMeasure: localMaxMeasure);
            if (line is null) continue;
            if (!TryCreateDimension(
                    document, view, line,
                    ToReferenceArray(datumPair.Lower.Reference, center, datumPair.Upper.Reference),
                    existing, messages, out Dimension? dimension)) continue;
            MarkOwned(dimension!, [target.Element.Id.CompatValue()]);
            AddDimensionObstacle(document, view, dimension!, basis, obstacles);
            dimensionedTargetIds.Add(target.Element.Id.CompatValue());
            created++;
        }
        return created;
    }

    private static CenterTarget? BuildCenterTarget(Element element, View view, ViewBasis basis)
    {
        if (element is MEPCurve mepCurve)
            return BuildMepCurveCenterTarget(mepCurve, view, basis);
        if (element is not FamilyInstance instance) return null;

        Reference? leftRight = instance.GetReferences(
            FamilyInstanceReferenceType.CenterLeftRight).FirstOrDefault();
        Reference? frontBack = instance.GetReferences(
            FamilyInstanceReferenceType.CenterFrontBack).FirstOrDefault();
        Transform transform = instance.GetTransform();
        XYZ localX = basis.FlattenAndNormalize(transform.BasisX);
        XYZ localY = basis.FlattenAndNormalize(transform.BasisY);
        if (localX.GetLength() < Epsilon || localY.GetLength() < Epsilon) return null;

        Reference? xReference;
        Reference? yReference;
        if (Math.Abs(localX.DotProduct(basis.Right)) >= Math.Abs(localX.DotProduct(basis.Up)))
        {
            xReference = Math.Abs(localX.DotProduct(basis.Right)) >= 0.95 ? leftRight : null;
            yReference = Math.Abs(localY.DotProduct(basis.Up)) >= 0.95 ? frontBack : null;
        }
        else
        {
            xReference = Math.Abs(localY.DotProduct(basis.Right)) >= 0.95 ? frontBack : null;
            yReference = Math.Abs(localX.DotProduct(basis.Up)) >= 0.95 ? leftRight : null;
        }
        XYZ center = GetCenter(instance, view);
        // `new Reference(instance)` identifies the element but is not a
        // geometric reference, so NewDimension rejects it for many project
        // families. Prefer a strong/weak authored plane nearest the insertion
        // centre, then use the nearest real planar face as the last-resort
        // measurable station. This keeps an imperfect family from dropping an
        // entire mixed-category chain.
        xReference ??= FindNearestFamilyReference(instance, view, basis, basis.Right, center);
        yReference ??= FindNearestFamilyReference(instance, view, basis, basis.Up, center);
        return new CenterTarget(instance, center, xReference, yReference);
    }

    private static Reference? FindNearestFamilyReference(
        FamilyInstance instance,
        View view,
        ViewBasis basis,
        XYZ measureAxis,
        XYZ center)
    {
        double centerCoordinate = center.DotProduct(measureAxis);
        var candidates = new List<(Reference Reference, double Coordinate, int Priority)>();
        foreach (FamilyInstanceReferenceType type in new[]
                 {
                     FamilyInstanceReferenceType.StrongReference,
                     FamilyInstanceReferenceType.WeakReference,
                     FamilyInstanceReferenceType.Left,
                     FamilyInstanceReferenceType.Right,
                     FamilyInstanceReferenceType.Front,
                     FamilyInstanceReferenceType.Back
                 })
        {
            foreach (Reference reference in instance.GetReferences(type))
            {
                if (!TryGetReferenceStation(instance, reference, basis, measureAxis, out double coordinate))
                    continue;
                string name;
                try { name = instance.GetReferenceName(reference) ?? string.Empty; }
                catch { name = string.Empty; }
                int priority = name.Contains("center", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("centre", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("origin", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : type is FamilyInstanceReferenceType.StrongReference or
                        FamilyInstanceReferenceType.WeakReference ? 1 : 2;
                candidates.Add((reference, coordinate, priority));
            }
        }

        if (candidates.Count > 0)
        {
            return candidates
                .OrderBy(item => item.Priority)
                .ThenBy(item => Math.Abs(item.Coordinate - centerCoordinate))
                .Select(item => item.Reference)
                .First();
        }

        return GetPlanarFaceReferences(instance, view, basis)
            .Where(item => Math.Abs(item.Normal.DotProduct(measureAxis)) >= 0.985)
            .OrderBy(item => Math.Abs(item.CoordinateAlong(measureAxis) - centerCoordinate))
            .Select(item => item.Reference)
            .FirstOrDefault();
    }

    private static bool TryGetReferenceStation(
        FamilyInstance instance,
        Reference reference,
        ViewBasis basis,
        XYZ measureAxis,
        out double coordinate)
    {
        coordinate = 0;
        try
        {
            GeometryObject? geometry = instance.GetGeometryObjectFromReference(reference);
            if (geometry is PlanarFace face)
            {
                XYZ normal = basis.FlattenAndNormalize(face.FaceNormal);
                if (normal.GetLength() < Epsilon ||
                    Math.Abs(normal.DotProduct(measureAxis)) < 0.985) return false;
                coordinate = face.Origin.DotProduct(measureAxis);
                return true;
            }
            if (geometry is Line line)
            {
                XYZ direction = basis.FlattenAndNormalize(line.Direction);
                if (direction.GetLength() < Epsilon ||
                    Math.Abs(direction.DotProduct(measureAxis)) > 0.15) return false;
                coordinate = line.GetEndPoint(0).DotProduct(measureAxis);
                return true;
            }
        }
        catch
        {
            // Invalid or non-geometric family references are ignored here and
            // the physical-face fallback below gets a chance instead.
        }
        return false;
    }

    private static double GetCenterReferenceCoordinate(
        CenterTarget target,
        Reference reference,
        ViewBasis basis,
        XYZ measureAxis)
    {
        if (target.Element is FamilyInstance instance &&
            TryGetReferenceStation(instance, reference, basis, measureAxis, out double coordinate))
            return coordinate;
        return target.Center.DotProduct(measureAxis);
    }

    private static CenterTarget? BuildMepCurveCenterTarget(
        MEPCurve curve,
        View view,
        ViewBasis basis)
    {
        if (curve.Location is not LocationCurve { Curve: Line line }) return null;
        XYZ direction = basis.FlattenAndNormalize(line.Direction);
        if (direction.GetLength() < Epsilon) return null;

        bool horizontalRun = Math.Abs(direction.DotProduct(basis.Right)) >=
                             Math.Abs(direction.DotProduct(basis.Up));
        XYZ snappedAxis = horizontalRun ? basis.Right : basis.Up;
        if (Math.Abs(direction.DotProduct(snappedAxis)) < 0.95) return null;

        Reference centerReference = line.Reference ?? new Reference(curve);
        return new CenterTarget(
            curve,
            GetCenter(curve, view),
            horizontalRun ? null : centerReference,
            horizontalRun ? centerReference : null);
    }

    private static DatumPair? FindChainDatums(
        IReadOnlyList<DatumReference> datums,
        XYZ measureAxis,
        double minCenter,
        double maxCenter)
    {
        List<DatumReference> matching = datums
            .Where(item => Math.Abs(item.Normal.DotProduct(measureAxis)) >= 0.985)
            .ToList();
        DatumReference? lower = PickDatum(
            matching.Where(item => item.Coordinate < minCenter - 1.0 / 304.8),
            minCenter);
        DatumReference? upper = PickDatum(
            matching.Where(item => item.Coordinate > maxCenter + 1.0 / 304.8),
            maxCenter);
        return lower is null || upper is null ? null : new DatumPair(lower, upper);

        static DatumReference? PickDatum(
            IEnumerable<DatumReference> candidates,
            double edge)
        {
            // Architectural walls are the preferred real construction
            // reference. Only when that side has no wall do we use the closest
            // grid. Distance must never suppress an otherwise valid endpoint:
            // the baseline remains local even when its witness reaches farther.
            return candidates
                .OrderBy(item => item.Kind == "Wall" ? 0 : 1)
                .ThenBy(item => Math.Abs(item.Coordinate - edge))
                .FirstOrDefault();
        }
    }

    private static List<Reference> DistinctReferences(Document document, IEnumerable<Reference> references) =>
        references
            .GroupBy(reference => StableReference(document, reference), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    private static int CreateDuctEdgeChains(
        Document document,
        View view,
        IReadOnlyList<Duct> ducts,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        HashSet<long> dimensionedTargetIds,
        double maximumElementGapMillimeters,
        DimensionZone sampleZone)
    {
        double maximumElementGap = maximumElementGapMillimeters / 304.8;
        var targets = new List<DuctEdgeTarget>();
        foreach (Duct duct in ducts)
        {
            DuctEdgeTarget? target = BuildDuctEdgeTarget(duct, view, basis);
            if (target is not null) targets.Add(target);
        }

        int created = 0;
        List<List<DuctEdgeTarget>> groups = BuildLocalDuctClusters(
            targets,
            maximumElementGap,
            sampleZone);

        foreach (List<DuctEdgeTarget> group in groups)
        {
            List<DuctEdgeTarget> members = group.ToList();
            bool horizontalRun = members[0].HorizontalRun;
            XYZ measureAxis = horizontalRun ? basis.Up : basis.Right;
            XYZ offsetAxis = horizontalRun ? basis.Right : basis.Up;
            var ordered = members
                .SelectMany(item => new[]
                {
                    (Coordinate: item.MinMeasure, Reference: item.FirstReference),
                    (Coordinate: item.MaxMeasure, Reference: item.SecondReference)
                })
                .OrderBy(item => item.Coordinate)
                .ToList();

            // Collinear connected duct segments expose different Revit face
            // references at exactly the same paper coordinate. Keep one tick
            // per physical edge so the chain has no zero-length segments.
            var chainReferences = new List<(double Coordinate, Reference Reference)>();
            foreach (var candidate in ordered)
            {
                if (chainReferences.Count > 0 &&
                    Math.Abs(candidate.Coordinate - chainReferences[^1].Coordinate) < 1.0 / 304.8)
                    continue;
                chainReferences.Add(candidate);
            }
            if (chainReferences.Count < 2) continue;

            double minMeasure = chainReferences.First().Coordinate;
            double maxMeasure = chainReferences.Last().Coordinate;
            double localMinMeasure = minMeasure;
            double localMaxMeasure = maxMeasure;
            DatumPair? datumPair = FindChainDatums(
                datums, measureAxis, minMeasure, maxMeasure);
            if (datumPair is null)
            {
                AddMessage(messages,
                    $"Rectangular duct chain ({members.Count} duct) skipped: two outside Wall/Grid endpoints were not found.");
                continue;
            }
            var references = chainReferences
                .Append((datumPair.Lower.Coordinate, datumPair.Lower.Reference))
                .Append((datumPair.Upper.Coordinate, datumPair.Upper.Reference))
                .OrderBy(item => item.Coordinate)
                .Select(item => item.Reference)
                .ToList();
            minMeasure = datumPair.Lower.Coordinate;
            maxMeasure = datumPair.Upper.Coordinate;

            double minOffset = members.Min(item => item.MinOffset);
            double maxOffset = members.Max(item => item.MaxOffset);
            Line? line = ChooseClearLine(
                basis, measureAxis, offsetAxis,
                minMeasure, maxMeasure, minOffset, maxOffset,
                view.Scale, obstacles, -1,
                collisionMinMeasure: localMinMeasure,
                collisionMaxMeasure: localMaxMeasure);
            Dimension? dimension = null;
            bool chainCreated = line is not null && TryCreateDimension(
                document, view, line, ToReferenceArray(DistinctReferences(document, references).ToArray()),
                existing, messages, out dimension);
            if (chainCreated)
            {
                MarkOwned(dimension!, members.Select(item => item.Duct.Id.CompatValue()));
                AddDimensionObstacle(document, view, dimension!, basis, obstacles);
                foreach (DuctEdgeTarget member in members)
                    dimensionedTargetIds.Add(member.Duct.Id.CompatValue());
                created++;
                continue;
            }

            // A family/link/reference peculiarity can make a multi-reference
            // chain invalid. Preserve complete coverage by falling back only
            // for that local group, not for the entire duct system.
            foreach (DuctEdgeTarget member in members)
            {
                if (!TryCreateDuctWidthDimension(
                        document, view, member.Duct, basis, datums, obstacles, existing,
                        messages, out Dimension? fallback)) continue;
                MarkOwned(fallback!, [member.Duct.Id.CompatValue()]);
                AddDimensionObstacle(document, view, fallback!, basis, obstacles);
                dimensionedTargetIds.Add(member.Duct.Id.CompatValue());
                created++;
            }
        }
        return created;
    }

    private static List<List<DuctEdgeTarget>> BuildLocalDuctClusters(
        IReadOnlyList<DuctEdgeTarget> targets,
        double maximumElementGap,
        DimensionZone sampleZone)
    {
        var clusters = new List<List<DuctEdgeTarget>>();
        foreach (DuctEdgeTarget target in targets
                     .OrderBy(item => item.HorizontalRun)
                     .ThenBy(item => (item.MinOffset + item.MaxOffset) * 0.5)
                     .ThenBy(item => (item.MinMeasure + item.MaxMeasure) * 0.5)
                     .ThenBy(item => item.Duct.Id.CompatValue()))
        {
            double measure = (target.MinMeasure + target.MaxMeasure) * 0.5;
            double offset = (target.MinOffset + target.MaxOffset) * 0.5;
            List<DuctEdgeTarget>? best = null;
            double bestDistance = double.MaxValue;
            foreach (List<DuctEdgeTarget> cluster in clusters.Where(item =>
                         item[0].HorizontalRun == target.HorizontalRun))
            {
                double minMeasure = Math.Min(measure, cluster.Min(item => (item.MinMeasure + item.MaxMeasure) * 0.5));
                double maxMeasure = Math.Max(measure, cluster.Max(item => (item.MinMeasure + item.MaxMeasure) * 0.5));
                double minOffset = Math.Min(offset, cluster.Min(item => (item.MinOffset + item.MaxOffset) * 0.5));
                double maxOffset = Math.Max(offset, cluster.Max(item => (item.MinOffset + item.MaxOffset) * 0.5));
                double maximumMeasureSpan = target.HorizontalRun ? sampleZone.Height : sampleZone.Width;
                double maximumOffsetSpan = target.HorizontalRun ? sampleZone.Width : sampleZone.Height;
                if (maxMeasure - minMeasure > maximumMeasureSpan ||
                    maxOffset - minOffset > maximumOffsetSpan) continue;
                double nearest = cluster.Min(item => Math.Max(
                    Math.Abs(measure - (item.MinMeasure + item.MaxMeasure) * 0.5),
                    Math.Abs(offset - (item.MinOffset + item.MaxOffset) * 0.5)));
                if (nearest > maximumElementGap || nearest >= bestDistance) continue;
                best = cluster;
                bestDistance = nearest;
            }
            if (best is null) clusters.Add([target]);
            else best.Add(target);
        }
        return clusters;
    }

    private static DuctEdgeTarget? BuildDuctEdgeTarget(Duct duct, View view, ViewBasis basis)
    {
        if (duct.Location is not LocationCurve { Curve: Line line }) return null;
        Parameter? width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
        Parameter? height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        if (width is null || height is null || width.AsDouble() <= Epsilon || height.AsDouble() <= Epsilon)
            return null;

        XYZ ductAxis = basis.FlattenAndNormalize(line.Direction);
        bool horizontal = Math.Abs(ductAxis.DotProduct(basis.Right)) >=
                          Math.Abs(ductAxis.DotProduct(basis.Up));
        XYZ snappedDuctAxis = horizontal ? basis.Right : basis.Up;
        if (Math.Abs(ductAxis.DotProduct(snappedDuctAxis)) < 0.95) return null;
        XYZ measureAxis = horizontal ? basis.Up : basis.Right;
        List<FaceReference> faces = GetPlanarFaceReferences(duct, view, basis)
            .Where(item => Math.Abs(item.Normal.DotProduct(measureAxis)) >= 0.985)
            .OrderBy(item => item.CoordinateAlong(measureAxis))
            .ToList();
        if (faces.Count < 2) return null;

        FaceReference first = faces.First();
        FaceReference second = faces.Last();
        double firstMeasure = first.CoordinateAlong(measureAxis);
        double secondMeasure = second.CoordinateAlong(measureAxis);
        double firstOffset = line.GetEndPoint(0).DotProduct(snappedDuctAxis);
        double secondOffset = line.GetEndPoint(1).DotProduct(snappedDuctAxis);
        return new DuctEdgeTarget(
            duct, horizontal,
            Math.Min(firstMeasure, secondMeasure), Math.Max(firstMeasure, secondMeasure),
            Math.Min(firstOffset, secondOffset), Math.Max(firstOffset, secondOffset),
            first.Reference, second.Reference);
    }

    private static bool TryCreateDuctWidthDimension(
        Document document,
        View view,
        Duct duct,
        ViewBasis basis,
        IReadOnlyList<DatumReference> datums,
        List<Obstacle> obstacles,
        HashSet<string> existing,
        List<string> messages,
        out Dimension? dimension)
    {
        dimension = null;
        if (duct.Location is not LocationCurve { Curve: Line centerLine })
        {
            AddMessage(messages, $"{duct.Name}: only straight rectangular ducts are dimensioned.");
            return false;
        }

        Parameter? width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
        Parameter? height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        if (width is null || height is null || width.AsDouble() <= Epsilon || height.AsDouble() <= Epsilon)
        {
            AddMessage(messages, $"{duct.Name}: round/oval duct skipped; Smart Dim uses rectangular duct edges only.");
            return false;
        }

        XYZ ductAxis = basis.FlattenAndNormalize(centerLine.Direction);
        if (ductAxis.GetLength() < Epsilon) return false;
        XYZ widthAxis = basis.Rotate90(ductAxis);
        List<FaceReference> faces = GetPlanarFaceReferences(duct, view, basis)
            .Where(item => Math.Abs(item.Normal.DotProduct(widthAxis)) >= 0.985)
            .OrderBy(item => item.CoordinateAlong(widthAxis))
            .ToList();
        if (faces.Count < 2)
        {
            AddMessage(messages, $"{duct.Name}: Revit did not expose both physical duct-edge references.");
            return false;
        }

        FaceReference first = faces.First();
        FaceReference second = faces.Last();
        IReadOnlyList<XYZ> points = GetBoundingPoints(duct, view);
        if (points.Count == 0) return false;
        (double minWidth, double maxWidth) = Extents(points, widthAxis);
        (double minLength, double maxLength) = Extents(points, ductAxis);
        double localMinWidth = minWidth;
        double localMaxWidth = maxWidth;
        DatumPair? datumPair = FindChainDatums(
            datums, widthAxis, minWidth, maxWidth);
        if (datumPair is null)
        {
            AddMessage(messages, $"{duct.Name}: two outside Wall/Grid references were not found for the duct-edge endpoints.");
            return false;
        }
        minWidth = datumPair.Lower.Coordinate;
        maxWidth = datumPair.Upper.Coordinate;
        Line? line = ChooseClearLine(
            basis, widthAxis, ductAxis,
            minWidth, maxWidth, minLength, maxLength,
            view.Scale, obstacles, duct.Id.CompatValue(),
            collisionMinMeasure: localMinWidth,
            collisionMaxMeasure: localMaxWidth);
        if (line is null)
        {
            AddMessage(messages, $"{duct.Name}: no empty lane was found for the physical duct-edge dimension.");
            return false;
        }
        return TryCreateDimension(
            document, view, line,
            ToReferenceArray(
                datumPair.Lower.Reference,
                first.Reference,
                second.Reference,
                datumPair.Upper.Reference),
            existing, messages, out dimension);
    }

    private static bool TryCreateDimension(
        Document document,
        View view,
        Line line,
        ReferenceArray references,
        HashSet<string> existing,
        List<string> messages,
        out Dimension? dimension)
    {
        dimension = null;
        string signature = ReferenceSignature(document, references);
        if (existing.Contains(signature))
            return false;

        using var sub = new SubTransaction(document);
        sub.Start();
        try
        {
            dimension = document.Create.NewDimension(view, line, references);
            sub.Commit();
            existing.Add(signature);
            return true;
        }
        catch (Exception exception)
        {
            if (sub.GetStatus() == TransactionStatus.Started)
                sub.RollBack();
            AddMessage(messages, "Dimension skipped: " + Friendly(exception));
            return false;
        }
    }

    private static Line? ChooseClearLine(
        ViewBasis basis,
        XYZ measureAxis,
        XYZ offsetAxis,
        double minMeasure,
        double maxMeasure,
        double minOffset,
        double maxOffset,
        int viewScale,
        IReadOnlyList<Obstacle> obstacles,
        long excludedElementId,
        int preferredSide = 0,
        double? alignedLaneOffset = null,
        double? collisionMinMeasure = null,
        double? collisionMaxMeasure = null)
    {
        double paper = Math.Max(1, viewScale) / 304.8;
        // Keep every baseline in the local drafting band. Text collisions are
        // repaired after Revit creates the real segments, so they must not push
        // the whole string tens of millimetres away from its MEP cluster.
        // Four paper millimetres is the normal first lane: visibly separated
        // from the device outline without producing a remote dimension string.
        double[] gaps = [4, 5.5, 7, 9, 12, 15, 18];
        var candidates = new List<(Line Line, double Score)>();
        if (alignedLaneOffset.HasValue)
        {
            double side = alignedLaneOffset.Value < minOffset ? -1 : 1;
            AddCandidate(alignedLaneOffset.Value, (int)side, 0, aligned: true);
        }
        foreach (double gap in gaps)
        {
            foreach ((double offset, int side) in new[]
            {
                (minOffset - gap * paper, -1),
                (maxOffset + gap * paper, 1)
            })
            {
                AddCandidate(offset, side, gap, aligned: false);
            }
        }
        // Prefer a genuinely clear lane. If the model completely surrounds the
        // cluster, still use the least-obstructed nearby lane instead of
        // dropping the dimension; the post-create text pass then moves only
        // the affected labels outward.
        return candidates.OrderBy(item => item.Score).Select(item => item.Line).FirstOrDefault();

        void AddCandidate(double offset, int side, double gap, bool aligned)
        {
            XYZ start = basis.OnPlane(measureAxis * minMeasure + offsetAxis * offset);
            XYZ end = basis.OnPlane(measureAxis * maxMeasure + offsetAxis * offset);
            if (start.DistanceTo(end) < Epsilon) return;
            Line line = Line.CreateBound(start, end);
            double checkMin = collisionMinMeasure ?? minMeasure;
            double checkMax = collisionMaxMeasure ?? maxMeasure;
            XYZ checkStart = basis.OnPlane(measureAxis * checkMin + offsetAxis * offset);
            XYZ checkEnd = basis.OnPlane(measureAxis * checkMax + offsetAxis * offset);
            Rect2 rect = Rect2.FromPoints(
                basis.Project(checkStart), basis.Project(checkEnd), 1.2 * paper);
            double collisions = obstacles
                .Where(item => item.ElementId != excludedElementId && item.Rect.Intersects(rect))
                .Sum(item => item.Annotation ? 6.0 : 10.0);
            double sidePenalty = preferredSide == 0
                ? side > 0 ? 0.1 : 0.0
                : side == preferredSide ? 0.0 : 10_000.0;
            double alignmentBonus = aligned ? -2_500.0 : 0.0;
            double score = collisions * 1_000_000.0 + gap * 100.0 + sidePenalty + alignmentBonus;
            candidates.Add((line, score));
        }
    }

    private static List<DatumReference> CollectDatums(Document document, View view, ViewBasis basis)
    {
        var result = new List<DatumReference>();
        foreach (Grid grid in new FilteredElementCollector(document, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            AddGridDatum(grid, null, Transform.Identity, basis, result);
        foreach (Wall wall in new FilteredElementCollector(document, view.Id).OfClass(typeof(Wall)).Cast<Wall>())
            AddWallDatums(wall, null, Transform.Identity, basis, result);

        foreach (RevitLinkInstance link in new FilteredElementCollector(document, view.Id)
                     .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            Document? linked = link.GetLinkDocument();
            if (linked is null) continue;
            Transform transform = link.GetTotalTransform();
            foreach (Grid grid in new FilteredElementCollector(linked).OfClass(typeof(Grid)).Cast<Grid>())
                AddGridDatum(grid, link, transform, basis, result);
            foreach (Wall wall in new FilteredElementCollector(linked).OfClass(typeof(Wall)).Cast<Wall>())
                AddWallDatums(wall, link, transform, basis, result);
        }
        return result;
    }

    private static void AddGridDatum(
        Grid grid,
        RevitLinkInstance? link,
        Transform transform,
        ViewBasis basis,
        List<DatumReference> result)
    {
        if (grid.Curve is not Line line) return;
        XYZ direction = basis.FlattenAndNormalize(transform.OfVector(line.Direction));
        if (direction.GetLength() < Epsilon) return;
        XYZ normal = basis.Rotate90(direction);
        XYZ point = transform.OfPoint(line.GetEndPoint(0));
        // A Grid is a datum element, not ordinary model geometry. Its Curve
        // reference is frequently null/non-dimensional in project views. A
        // real linear dimension must reference the Grid element itself.
        Reference gridReference = new(grid);
        Reference reference = link is null
            ? gridReference
            : gridReference.CreateLinkReference(link);
        result.Add(new DatumReference(reference, normal, point.DotProduct(normal), "Grid"));
    }

    private static void AddWallDatums(
        Wall wall,
        RevitLinkInstance? link,
        Transform transform,
        ViewBasis basis,
        List<DatumReference> result)
    {
        foreach (ShellLayerType side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
        {
            foreach (Reference source in HostObjectUtils.GetSideFaces(wall, side))
            {
                if (wall.GetGeometryObjectFromReference(source) is not PlanarFace face) continue;
                XYZ normal = basis.FlattenAndNormalize(transform.OfVector(face.FaceNormal));
                if (normal.GetLength() < Epsilon) continue;
                XYZ point = transform.OfPoint(face.Origin);
                Reference reference = link is null ? source : source.CreateLinkReference(link);
                result.Add(new DatumReference(reference, normal, point.DotProduct(normal), "Wall"));
            }
        }
    }

    private static List<Obstacle> CollectObstacles(Document document, View view, ViewBasis basis)
    {
        var result = new List<Obstacle>();
        var ids = new HashSet<long>();
        foreach (BuiltInCategory category in ObstacleCategories)
        {
            foreach (Element element in new FilteredElementCollector(document, view.Id)
                         .OfCategory(category).WhereElementIsNotElementType())
            {
                if (!ids.Add(element.Id.CompatValue())) continue;
                AddObstacle(element, view, basis, false, result);
            }
        }
        foreach (Element element in new FilteredElementCollector(document, view.Id)
                     .WhereElementIsNotElementType()
                     .Where(item => item is IndependentTag or TextNote or Dimension))
        {
            if (!ids.Add(element.Id.CompatValue())) continue;
            AddObstacle(element, view, basis, true, result);
        }
        return result;
    }

    private static void AddObstacle(
        Element element,
        View view,
        ViewBasis basis,
        bool annotation,
        List<Obstacle> result)
    {
        IReadOnlyList<XYZ> points = GetBoundingPoints(element, view);
        if (points.Count == 0) return;
        List<Point2> projected = points.Select(basis.Project).ToList();
        result.Add(new Obstacle(element.Id.CompatValue(), Rect2.FromPoints(projected), annotation));
    }

    private static void AddDimensionObstacle(
        Document document,
        View view,
        Dimension dimension,
        ViewBasis basis,
        List<Obstacle> obstacles)
    {
        document.Regenerate();
        // Keep Revit's normal dimension text placement. The tool only chooses
        // a clear baseline; it no longer moves segment text or creates a custom
        // leader style.
        try
        {
            Curve? curve = dimension.Curve;
            if (curve is not null && curve.IsBound)
            {
                double paper = Math.Max(1, view.Scale) / 304.8;
                Point2 start = basis.Project(curve.GetEndPoint(0));
                Point2 end = basis.Project(curve.GetEndPoint(1));
                obstacles.Add(new Obstacle(
                    dimension.Id.CompatValue(),
                    Rect2.FromPoints(start, end, 3.5 * paper),
                    true));
            }
        }
        catch
        {
            // Fall back to the Revit bounding box for unusual dimension types.
            AddObstacle(dimension, view, basis, true, obstacles);
        }
    }

    private static void ApplySmartDimensionLeaderStyle(Document document, Dimension dimension)
    {
        try
        {
            DimensionType? source = document.GetElement(dimension.GetTypeId()) as DimensionType;
            if (source is null) return;
            DimensionType smart;
            if (source.Name.StartsWith("FamilyMEP Smart Dim - ", StringComparison.Ordinal))
            {
                smart = source;
            }
            else
            {
                string smartName = $"FamilyMEP Smart Dim - {source.Name}";
                smart = new FilteredElementCollector(document)
                            .OfClass(typeof(DimensionType))
                            .Cast<DimensionType>()
                            .FirstOrDefault(item => string.Equals(item.Name, smartName, StringComparison.Ordinal))
                        ?? (DimensionType)source.Duplicate(smartName);
            }

            SetInteger(smart, BuiltInParameter.DIM_LEADER_DISPLAY_CONDITION, 1);
            SetInteger(smart, BuiltInParameter.ARC_LEADER_PARAM, 1);
            if (dimension.GetTypeId() != smart.Id)
                dimension.ChangeTypeId(smart.Id);
        }
        catch
        {
            // Some protected office templates lock type parameters. Text can
            // still be moved; Revit will use that type's configured leader.
        }

        static void SetInteger(Element element, BuiltInParameter id, int value)
        {
            Parameter? parameter = element.get_Parameter(id);
            if (parameter is { IsReadOnly: false, StorageType: StorageType.Integer })
                parameter.Set(value);
        }
    }

    private static List<Rect2> AdjustDimensionTextForCollisions(
        Document document,
        View view,
        Dimension dimension,
        ViewBasis basis,
        IReadOnlyList<Obstacle> obstacles)
    {
        var result = new List<Rect2>();
        var occupied = new List<Obstacle>(obstacles);
        XYZ measureAxis;
        try
        {
            if (dimension.Curve is not Line dimensionLine) return result;
            measureAxis = basis.FlattenAndNormalize(dimensionLine.Direction);
        }
        catch
        {
            return result;
        }
        if (measureAxis.GetLength() < Epsilon) return result;
        XYZ offsetAxis = basis.Rotate90(measureAxis);
        (double textHeight, double widthFactor) = GetDimensionTextMetrics(document, view, dimension);

        try
        {
            if (dimension.Segments.Size > 0)
            {
                foreach (DimensionSegment segment in dimension.Segments)
                {
                    if (!segment.IsTextPositionAdjustable()) continue;
                    string value = SafeValueString(() => segment.ValueString);
                    double length = Math.Abs(segment.Value ?? 0.0);
                    XYZ position = segment.TextPosition;
                    (XYZ moved, Rect2 box, bool changed, int side) = FindClearTextPosition(
                        position, segment.Origin, length, value, textHeight, widthFactor,
                        view, basis, measureAxis, offsetAxis, occupied, dimension.Id.CompatValue());
                    if (changed)
                    {
                        segment.TextPosition = moved;
                        TrySetLeaderEnd(
                            point => segment.LeaderEndPosition = point,
                            segment.Origin, length, measureAxis, side);
                    }
                    result.Add(box);
                    // Use a synthetic id so following segments of the same
                    // dimension also avoid the text already placed here.
                    occupied.Add(new Obstacle(long.MinValue, box, true));
                }
            }
            else if (dimension.IsTextPositionAdjustable())
            {
                string value = SafeValueString(() => dimension.ValueString);
                double length = Math.Abs(dimension.Value ?? 0.0);
                XYZ position = dimension.TextPosition;
                (XYZ moved, Rect2 box, bool changed, int side) = FindClearTextPosition(
                    position, dimension.Origin, length, value, textHeight, widthFactor,
                    view, basis, measureAxis, offsetAxis, occupied, dimension.Id.CompatValue());
                if (changed)
                {
                    dimension.TextPosition = moved;
                    TrySetLeaderEnd(
                        point => dimension.LeaderEndPosition = point,
                        dimension.Origin, length, measureAxis, side);
                }
                result.Add(box);
            }
        }
        catch
        {
            // Keep the real dimension even if a particular style does not
            // expose movable text (ordinate/equality/custom types).
        }
        return result;
    }

    private static (XYZ Position, Rect2 Box, bool Changed, int Side) FindClearTextPosition(
        XYZ current,
        XYZ origin,
        double segmentLength,
        string value,
        double textHeight,
        double widthFactor,
        View view,
        ViewBasis basis,
        XYZ measureAxis,
        XYZ offsetAxis,
        IReadOnlyList<Obstacle> obstacles,
        long dimensionId)
    {
        double paper = Math.Max(1, view.Scale) / 304.8;
        double textWidth = Math.Max(3.5 * paper,
            (Math.Max(1, value.Length) * 0.48 + 0.55) * textHeight * widthFactor);
        Rect2 currentBox = OrientedTextBox(
            basis, current, measureAxis, offsetAxis, textWidth, textHeight);
        if (!HasTextCollision(currentBox, obstacles, dimensionId))
            return (current, currentBox, false, 0);

        var candidates = new List<(XYZ Point, Rect2 Box, int Side, double Score)>();
        double halfSpan = Math.Max(segmentLength * 0.5, 1.0 * paper);
        foreach (int side in new[] { -1, 1 })
        foreach (double lift in new[] { 0.0, 1.5, -1.5, 3.0, -3.0 })
        {
            XYZ point = origin + measureAxis * side *
                (halfSpan + textWidth * 0.5 + 1.2 * paper) + offsetAxis * lift * paper;
            Rect2 box = OrientedTextBox(
                basis, point, measureAxis, offsetAxis, textWidth, textHeight);
            double collisions = obstacles
                .Where(item => item.ElementId != dimensionId && item.Rect.Intersects(box))
                .Sum(item => item.Annotation ? 2.0 : 12.0);
            double score = collisions * 1_000_000.0 + current.DistanceTo(point);
            candidates.Add((point, box, side, score));
        }
        var best = candidates.OrderBy(item => item.Score).First();
        return (best.Point, best.Box, true, best.Side);
    }

    private static bool HasTextCollision(
        Rect2 box,
        IReadOnlyList<Obstacle> obstacles,
        long dimensionId) =>
        // Only real model geometry is allowed to pull dimension text away from
        // its default Revit position. Annotation bounding boxes are often much
        // larger than their visible ink and previously caused false moves.
        obstacles.Any(item => !item.Annotation &&
                              item.ElementId != dimensionId &&
                              item.Rect.Intersects(box));

    private static Rect2 OrientedTextBox(
        ViewBasis basis,
        XYZ center,
        XYZ measureAxis,
        XYZ offsetAxis,
        double width,
        double height)
    {
        XYZ along = measureAxis * (width * 0.5);
        XYZ across = offsetAxis * (height * 0.5);
        return Rect2.FromPoints(new[]
        {
            basis.Project(center - along - across),
            basis.Project(center - along + across),
            basis.Project(center + along - across),
            basis.Project(center + along + across)
        });
    }

    private static (double Height, double WidthFactor) GetDimensionTextMetrics(
        Document document,
        View view,
        Dimension dimension)
    {
        double paper = Math.Max(1, view.Scale) / 304.8;
        DimensionType? type = document.GetElement(dimension.GetTypeId()) as DimensionType;
        double paperHeight = type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 2.5 / 304.8;
        double widthFactor = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1.0;
        return (Math.Max(2.2 * paper, paperHeight * Math.Max(1, view.Scale)),
            PortableMath.Clamp(widthFactor, 0.5, 2.0));
    }

    private static string SafeValueString(Func<string?> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void TrySetLeaderEnd(
        Action<XYZ> setter,
        XYZ origin,
        double segmentLength,
        XYZ measureAxis,
        int side)
    {
        if (side == 0) return;
        try
        {
            double inset = Math.Max(0.0, segmentLength * 0.45);
            setter(origin + measureAxis * side * inset);
        }
        catch
        {
            // Moving TextPosition is sufficient for types whose leader end is
            // read-only or generated automatically by Revit.
        }
    }

    private static int DeleteOwnedDimensions(
        Document document,
        View view,
        ISet<long> selectedTargetIds)
    {
        Schema? schema = Schema.Lookup(OwnershipSchemaId);
        if (schema is null) return 0;
        var delete = new List<ElementId>();
        foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                     .OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            Entity entity = dimension.GetEntity(schema);
            if (!entity.IsValid()) continue;
            string sourceIds = entity.Get<string>("SourceIds") ?? string.Empty;
            bool selected = sourceIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => long.TryParse(value, out long id) ? id : long.MinValue)
                .Any(selectedTargetIds.Contains);
            if (selected) delete.Add(dimension.Id);
        }
        if (delete.Count > 0) document.Delete(delete);
        return delete.Count;
    }

    private static void MarkOwned(Dimension dimension, IEnumerable<long> sourceIds)
    {
        Schema schema = GetOwnershipSchema();
        var entity = new Entity(schema);
        entity.Set("SourceIds", string.Join(",", sourceIds.Distinct().OrderBy(value => value)));
        entity.Set("Version", 2);
        dimension.SetEntity(entity);
    }

    private static Schema GetOwnershipSchema()
    {
        Schema? existing = Schema.Lookup(OwnershipSchemaId);
        if (existing is not null) return existing;
        var builder = new SchemaBuilder(OwnershipSchemaId);
        builder.SetSchemaName("FamilyMEP_SmartDimension");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("SourceIds", typeof(string));
        builder.AddSimpleField("Version", typeof(int));
        return builder.Finish();
    }

    private static HashSet<string> CollectExistingDimensionSignatures(Document document, View view)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                     .OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            if (dimension.References is null || dimension.References.Size < 2) continue;
            result.Add(ReferenceSignature(document, dimension.References));
        }
        return result;
    }

    private static string ReferenceSignature(Document document, ReferenceArray references)
    {
        var values = new List<string>();
        for (int index = 0; index < references.Size; index++)
            values.Add(StableReference(document, references.get_Item(index)));
        values.Sort(StringComparer.Ordinal);
        return string.Join("|", values);
    }

    private static string StableReference(Document document, Reference reference)
    {
        try { return reference.ConvertToStableRepresentation(document); }
        catch { return $"{reference.ElementId.CompatValue()}:{reference.LinkedElementId.CompatValue()}:{reference.ElementReferenceType}"; }
    }

    private static bool NeedsDetailedSize(FamilyInstance instance)
    {
        long category = instance.Category?.Id.CompatValue() ?? 0;
        return category is
            (long)BuiltInCategory.OST_MechanicalEquipment or
            (long)BuiltInCategory.OST_PlumbingFixtures or
            (long)BuiltInCategory.OST_ElectricalEquipment;
    }

    private static List<FaceReference> GetPlanarFaceReferences(Element element, View view, ViewBasis basis)
    {
        var result = new List<FaceReference>();
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            View = view
        };
        GeometryElement? geometry = element.get_Geometry(options);
        if (geometry is null) return result;
        CollectPlanarFaces(geometry, Transform.Identity, basis, result);
        return result;
    }

    private static void CollectPlanarFaces(
        GeometryElement geometry,
        Transform transform,
        ViewBasis basis,
        List<FaceReference> result)
    {
        foreach (GeometryObject item in geometry)
        {
            if (item is GeometryInstance instance)
            {
                CollectPlanarFaces(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), basis, result);
                continue;
            }
            if (item is not Solid { Volume: > Epsilon } solid) continue;
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || planar.Reference is null) continue;
                XYZ normal = basis.FlattenAndNormalize(transform.OfVector(planar.FaceNormal));
                if (normal.GetLength() < Epsilon) continue;
                XYZ origin = transform.OfPoint(planar.Origin);
                result.Add(new FaceReference(planar.Reference, origin, normal));
            }
        }
    }

    private static IReadOnlyList<XYZ> GetBoundingPoints(Element element, View view)
    {
        BoundingBoxXYZ? box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (box is null) return [];
        var result = new List<XYZ>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
            result.Add(box.Transform.OfPoint(new XYZ(x, y, z)));
        return result;
    }

    private static XYZ GetCenter(Element element, View view)
    {
        if (element.Location is LocationPoint point) return point.Point;
        if (element.Location is LocationCurve curve)
            return curve.Curve.Evaluate(0.5, true);
        IReadOnlyList<XYZ> points = GetBoundingPoints(element, view);
        return points.Count == 0 ? XYZ.Zero :
            new XYZ(points.Average(item => item.X), points.Average(item => item.Y), points.Average(item => item.Z));
    }

    private static (double Min, double Max) Extents(IReadOnlyList<XYZ> points, XYZ axis)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (XYZ point in points)
        {
            double value = point.DotProduct(axis);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        return (min, max);
    }

    private static ReferenceArray ToReferenceArray(params Reference[] references)
    {
        var result = new ReferenceArray();
        foreach (Reference reference in references) result.Append(reference);
        return result;
    }

    private static bool IsRectangularDuct(Duct duct)
    {
        Parameter? width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
        Parameter? height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        return width is not null && height is not null &&
               width.AsDouble() > Epsilon && height.AsDouble() > Epsilon;
    }

    private static bool IsSupported(Element element, ISet<string> selectedCategoryKeys)
    {
        long category = element.Category?.Id.CompatValue() ?? 0;
        if (!selectedCategoryKeys.Contains(category.ToString())) return false;
        if (element is MEPCurve)
        {
            return category is
                (long)BuiltInCategory.OST_DuctCurves or
                (long)BuiltInCategory.OST_PipeCurves;
        }
        if (element is not FamilyInstance) return false;
        return category is
            (long)BuiltInCategory.OST_DuctFitting or
            (long)BuiltInCategory.OST_DuctAccessory or
            (long)BuiltInCategory.OST_PipeFitting or
            (long)BuiltInCategory.OST_PipeAccessory or
            (long)BuiltInCategory.OST_MechanicalEquipment or
            (long)BuiltInCategory.OST_DuctTerminal or
            (long)BuiltInCategory.OST_Sprinklers or
            (long)BuiltInCategory.OST_PlumbingFixtures or
            (long)BuiltInCategory.OST_ElectricalEquipment;
    }

    private static string Friendly(Exception exception) =>
        exception.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        ?? exception.GetType().Name;

    private static void AddMessage(List<string> messages, string message)
    {
        if (messages.Count < 30 && !messages.Contains(message, StringComparer.Ordinal))
            messages.Add(message);
    }

    private sealed record DatumReference(Reference Reference, XYZ Normal, double Coordinate, string Kind);
    private sealed record DatumPair(DatumReference Lower, DatumReference Upper);
    private sealed record CenterTarget(
        Element Element,
        XYZ Center,
        Reference? XReference,
        Reference? YReference);
    private sealed record DuctEdgeTarget(
        Duct Duct,
        bool HorizontalRun,
        double MinMeasure,
        double MaxMeasure,
        double MinOffset,
        double MaxOffset,
        Reference FirstReference,
        Reference SecondReference);
    private sealed record FaceReference(Reference Reference, XYZ Origin, XYZ Normal)
    {
        public double CoordinateAlong(XYZ axis) => Origin.DotProduct(axis);
    }
    private sealed record Obstacle(long ElementId, Rect2 Rect, bool Annotation);

    private readonly record struct Point2(double X, double Y);

    private readonly record struct DimensionZone(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        public bool Contains(XYZ point, ViewBasis basis)
        {
            Point2 projected = basis.Project(point);
            return projected.X >= MinX - Epsilon && projected.X <= MaxX + Epsilon &&
                   projected.Y >= MinY - Epsilon && projected.Y <= MaxY + Epsilon;
        }

        public static DimensionZone FromPickedBox(PickedBox picked, ViewBasis basis)
        {
            Point2 first = basis.Project(picked.Min);
            Point2 second = basis.Project(picked.Max);
            return new DimensionZone(
                Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
                Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
        }
    }

    private readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Intersects(Rect2 other) =>
            MinX <= other.MaxX && MaxX >= other.MinX &&
            MinY <= other.MaxY && MaxY >= other.MinY;

        public static Rect2 FromPoints(IReadOnlyList<Point2> points) => new(
            points.Min(item => item.X), points.Min(item => item.Y),
            points.Max(item => item.X), points.Max(item => item.Y));

        public static Rect2 FromPoints(Point2 first, Point2 second, double padding) => new(
            Math.Min(first.X, second.X) - padding,
            Math.Min(first.Y, second.Y) - padding,
            Math.Max(first.X, second.X) + padding,
            Math.Max(first.Y, second.Y) + padding);
    }

    private readonly record struct ViewBasis(XYZ Origin, XYZ Right, XYZ Up, XYZ Normal)
    {
        public static ViewBasis Create(View view)
        {
            XYZ right = view.RightDirection.Normalize();
            XYZ up = view.UpDirection.Normalize();
            return new ViewBasis(view.Origin, right, up, right.CrossProduct(up).Normalize());
        }

        public Point2 Project(XYZ point)
        {
            XYZ delta = point - Origin;
            return new Point2(delta.DotProduct(Right), delta.DotProduct(Up));
        }

        public XYZ OnPlane(XYZ worldVector)
        {
            return worldVector + Normal * ((Origin - worldVector).DotProduct(Normal));
        }

        public XYZ FlattenAndNormalize(XYZ vector)
        {
            XYZ flat = Right * vector.DotProduct(Right) + Up * vector.DotProduct(Up);
            return flat.GetLength() < Epsilon ? XYZ.Zero : flat.Normalize();
        }

        public XYZ Rotate90(XYZ vector) =>
            (Right * -vector.DotProduct(Up) + Up * vector.DotProduct(Right)).Normalize();
    }
}
