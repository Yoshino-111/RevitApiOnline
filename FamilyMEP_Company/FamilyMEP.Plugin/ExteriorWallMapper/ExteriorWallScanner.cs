using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;

namespace FamilyMEP.Plugin.ExteriorWallMapper;

internal static class ExteriorWallScanner
{
    private const double MaxAssemblyDepthMm = 700.0;
    private const double MinimumOverlapMm = 50.0;
    private const double MaximumLayerGapMm = 50.0;
    private const int MaximumLayersPerAssembly = 12;

    internal static ExteriorWallScanResult Scan(Document hostDocument)
    {
        if (hostDocument is null) throw new ArgumentNullException(nameof(hostDocument));
        if (hostDocument.IsFamilyDocument)
            throw new InvalidOperationException("Exterior Wall Mapper requires a Revit project document.");

        var warnings = new List<string>();
        IReadOnlyList<LinkCandidate> candidates = CollectLinkedWallCandidates(hostDocument, warnings);
        List<RevitLinkInstance> topLinks = new FilteredElementCollector(hostDocument)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(IsTopLevelLink)
            .ToList();
        int loadedLinks = topLinks.Count(item => item.GetLinkDocument() is not null);

        List<Space> spaces = new FilteredElementCollector(hostDocument)
            .OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType()
            .OfType<Space>()
            .Where(space => space.Location is not null && space.Area > 1e-9)
            .ToList();
        IReadOnlyDictionary<long, IReadOnlyList<BoundaryEdge>> boundaryIndex =
            BuildBoundaryIndex(spaces, warnings);

        var segmentRows = new List<SegmentRow>();
        var diagnostics = new ScanDiagnostics(candidates.Count);
        foreach (Space space in spaces)
            ScanSpace(
                hostDocument,
                space,
                candidates,
                segmentRows,
                warnings,
                diagnostics,
                boundaryIndex.GetValueOrDefault(space.LevelId.CompatValue(), []));

        // Keep one logical row per Space face. Highlight later de-duplicates the
        // linked element references, while LINEAR still needs every room surface.
        IReadOnlyList<ExteriorWallRow> rows = AggregateRows(segmentRows);
        if (spaces.Count == 0)
            warnings.Add("No placed and enclosed MEP Spaces were found in the active project.");
        if (topLinks.Count == 0)
            warnings.Add("No top-level Revit/IFC links were found.");
        if (rows.Count == 0 && spaces.Count > 0 && topLinks.Count > 0)
        {
            warnings.Add(
                $"Scan diagnostics: {spaces.Count} placed MEP Spaces, " +
                $"{diagnostics.BoundarySegmentCount} boundary segments, " +
                $"{diagnostics.LinkedBoundarySegmentCount} link-owned segments, " +
                $"{diagnostics.ExteriorBoundarySegmentCount} exterior candidates, " +
                $"{diagnostics.LinkedWallCandidateCount} linked wall elements.");
            warnings.Add("No linked wall boundary adjoining outside air was found. Check Room Bounding, or use local Space Separation Lines close to the linked walls.");
        }

        return new ExteriorWallScanResult(
            rows,
            loadedLinks,
            topLinks.Count,
            spaces.Count,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ScanSpace(
        Document hostDocument,
        Space space,
        IReadOnlyList<LinkCandidate> candidates,
        ICollection<SegmentRow> output,
        ICollection<string> warnings,
        ScanDiagnostics diagnostics,
        IReadOnlyList<BoundaryEdge> levelBoundaries)
    {
        IList<IList<BoundarySegment>>? loops;
        try
        {
            using var options = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };
            loops = space.GetBoundarySegments(options);
        }
        catch (Exception exception)
        {
            warnings.Add($"Space {space.Number} {space.Name}: boundary scan failed ({exception.Message}).");
            return;
        }

        if (loops is null) return;
        BoundingBoxXYZ? spaceBounds = space.get_BoundingBox(null);
        double sampleZ = spaceBounds is null
            ? ((LocationPoint?)space.Location)?.Point.Z ?? 0.0
            : spaceBounds.Min.Z + Math.Min(UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters),
                Math.Max((spaceBounds.Max.Z - spaceBounds.Min.Z) * 0.5, 0.1));
        double height = space.UnboundedHeight > 1e-6
            ? space.UnboundedHeight
            : spaceBounds is null
                ? UnitUtils.ConvertToInternalUnits(3000.0, UnitTypeId.Millimeters)
                : Math.Max(spaceBounds.Max.Z - spaceBounds.Min.Z, 0.1);

        var exteriorFaces = new List<ExteriorBoundaryCandidate>();
        var interiorFaces = new List<ExteriorBoundaryCandidate>();
        foreach (BoundarySegment segment in loops.SelectMany(loop => loop))
        {
            try
            {
                diagnostics.BoundarySegmentCount++;
                RevitLinkInstance? rootLink = hostDocument.GetElement(segment.ElementId) as RevitLinkInstance;
                if (rootLink is not null) diagnostics.LinkedBoundarySegmentCount++;
                Curve curve = segment.GetCurve();
                if (curve.Length < UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters))
                    continue;
                if (!TryExteriorNormal(hostDocument, space, curve, sampleZ, out XYZ exteriorNormal))
                    continue;
                Space? adjacentSpace = FindSpaceAcrossBoundary(
                    hostDocument,
                    space,
                    curve,
                    sampleZ,
                    exteriorNormal);
                BoundaryEdge? adjacentBoundary = null;
                bool hasAdjacentSpace = adjacentSpace is not null || TryFindBoundaryClusterAcross(
                    space.Id.CompatValue(),
                    curve,
                    exteriorNormal,
                    levelBoundaries,
                    out adjacentBoundary);
                var face = new ExteriorBoundaryCandidate(
                    segment,
                    curve,
                    rootLink,
                    exteriorNormal,
                    Orientation(exteriorNormal),
                    adjacentSpace?.Id.CompatValue() ?? adjacentBoundary?.SpaceId ?? 0,
                    adjacentSpace?.Number ?? adjacentBoundary?.SpaceNumber ?? string.Empty,
                    adjacentSpace?.Name ?? adjacentBoundary?.SpaceName ?? string.Empty);
                if (hasAdjacentSpace)
                    interiorFaces.Add(face);
                else
                {
                    diagnostics.ExteriorBoundarySegmentCount++;
                    exteriorFaces.Add(face);
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Space {space.Number} {space.Name}: one boundary segment was skipped ({exception.Message}).");
            }
        }

        // A small corner return can be technically exposed but should not define
        // the facade assignment. Sum all collinear segments by compass direction
        // and retain only the direction with the largest total exposed length.
        string dominantOrientation = exteriorFaces.Count == 0
            ? string.Empty
            : exteriorFaces
            .GroupBy(face => face.Orientation, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(face => face.Curve.Length))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .Key;

        foreach (ExteriorBoundaryCandidate face in exteriorFaces.Where(face =>
            string.Equals(face.Orientation, dominantOrientation, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                BoundarySegment segment = face.Segment;
                Curve curve = face.Curve;
                RevitLinkInstance? rootLink = face.RootLink;
                XYZ exteriorNormal = face.ExteriorNormal;

                ElementId directLinkedId = rootLink is null
                    ? ElementId.InvalidElementId
                    : segment.LinkElementId;
                AssemblyCandidateSelection selection = FindAssemblyCandidates(
                    rootLink?.Id ?? ElementId.InvalidElementId,
                    directLinkedId,
                    curve,
                    sampleZ,
                    exteriorNormal,
                    candidates);
                IReadOnlyList<LinkCandidate> layers = selection.Layers;

                string levelName = hostDocument.GetElement(space.LevelId)?.Name ?? string.Empty;
                string orientation = face.Orientation;
                double areaM2 = UnitUtils.ConvertFromInternalUnits(
                    curve.Length * height,
                    UnitTypeId.SquareMeters);

                if (layers.Count == 0)
                {
                    if (rootLink is null) continue;
                    BoundingBoxXYZ fallbackBounds = CurveBounds(curve, sampleZ, height);
                    output.Add(new SegmentRow(
                        space.Id.CompatValue(),
                        space.Number ?? string.Empty,
                        space.Name ?? string.Empty,
                        0,
                        string.Empty,
                        string.Empty,
                        levelName,
                        LinearComponentKinds.ExteriorWall,
                        orientation,
                        0,
                        0,
                        "Linked boundary element could not be resolved",
                        "Unknown",
                        string.Empty,
                        rootLink.Name,
                        areaM2,
                        "Unmapped",
                        $"UNMAPPED|{rootLink.Id.CompatValue()}|{directLinkedId.CompatValue()}",
                        [],
                        [],
                        string.Empty,
                        [ToReference(rootLink.Id, directLinkedId, rootLink.Name, string.Empty, Transform.Identity, [], "Unresolved linked boundary", false, fallbackBounds)]));
                    continue;
                }

                AssemblyInfo assembly = BuildAssembly(layers, exteriorNormal);
                // Reaching this point means there is no adjacent Space on the
                // outward side. Treat the complete wall stack as sun-exposed by
                // default even when IFC/Revit did not set IsExternal correctly.
                string status = assembly.LayerCount > 0 ? "Ready" : "Review";
                output.Add(new SegmentRow(
                    space.Id.CompatValue(),
                    space.Number ?? string.Empty,
                    space.Name ?? string.Empty,
                    0,
                    string.Empty,
                    string.Empty,
                    levelName,
                    LinearComponentKinds.ExteriorWall,
                    orientation,
                    assembly.TotalThicknessMm,
                    assembly.LayerCount,
                    assembly.Description,
                    string.Join(" + ", layers.Select(item => item.IfcCategory).Distinct(StringComparer.OrdinalIgnoreCase)),
                    string.Join("; ", layers.Select(item => item.IfcGuid).Where(value => value.Length > 0).Distinct()),
                    string.Join(" + ", layers.Select(item => item.LinkPath).Distinct(StringComparer.OrdinalIgnoreCase)),
                    areaM2,
                    status,
                    assembly.Key,
                    assembly.Layers,
                    assembly.RecommendedLayers,
                    assembly.RecommendedDescription,
                    selection.HighlightTargets
                        .Concat(layers)
                        .DistinctBy(item => item.UniqueKey)
                        .Select(item => ToReference(item, curve, sampleZ, height))
                        .ToArray()));
            }
            catch (Exception exception)
            {
                warnings.Add($"Space {space.Number} {space.Name}: dominant exterior face was skipped ({exception.Message}).");
            }
        }

        AddOpeningRows(
            hostDocument,
            space,
            exteriorFaces,
            candidates,
            output,
            sampleZ,
            height,
            warnings,
            true);
        AddInteriorWallRows(
            hostDocument,
            space,
            interiorFaces,
            candidates,
            output,
            sampleZ,
            height,
            warnings);
        AddOpeningRows(
            hostDocument,
            space,
            interiorFaces,
            candidates,
            output,
            sampleZ,
            height,
            warnings,
            false);
    }

    private static void AddOpeningRows(
        Document hostDocument,
        Space space,
        IReadOnlyList<ExteriorBoundaryCandidate> exteriorFaces,
        IReadOnlyList<LinkCandidate> candidates,
        ICollection<SegmentRow> output,
        double sampleZ,
        double spaceHeight,
        ICollection<string> warnings,
        bool isExterior)
    {
        if (exteriorFaces.Count == 0) return;
        try
        {
            WindowCandidateMatch[] windows = exteriorFaces
                .SelectMany(face => FindOpeningCandidates(face, candidates, sampleZ, spaceHeight))
                .GroupBy(match => match.Candidate.UniqueKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(match => match.WidthInternal).First())
                .ToArray();
            string levelName = hostDocument.GetElement(space.LevelId)?.Name ?? string.Empty;
            foreach (WindowCandidateMatch match in windows)
            {
                LinkCandidate candidate = match.Candidate;
                string linearCategory = candidate.Kind switch
                {
                    LinearComponentKinds.ExteriorDoor when isExterior => LinearComponentKinds.ExteriorDoor,
                    LinearComponentKinds.ExteriorDoor => LinearComponentKinds.InteriorDoor,
                    _ when isExterior => LinearComponentKinds.ExteriorWindow,
                    _ => LinearComponentKinds.InteriorWindow
                };
                double thicknessMm = UnitUtils.ConvertFromInternalUnits(
                    match.DepthInternal,
                    UnitTypeId.Millimeters);
                double areaM2 = UnitUtils.ConvertFromInternalUnits(
                    match.WidthInternal * match.HeightInternal,
                    UnitTypeId.SquareMeters);
                var layer = new ExteriorWallLayerItem(
                    1,
                    candidate.TypeName,
                    thicknessMm,
                    true,
                    true,
                    candidate.ElementId.CompatValue().ToString(CultureInfo.InvariantCulture),
                    candidate.ElementUniqueId,
                    candidate.IfcGuid,
                    candidate.LinkPath,
                    candidate.GeometryKey);
                output.Add(new SegmentRow(
                    space.Id.CompatValue(),
                    space.Number ?? string.Empty,
                    space.Name ?? string.Empty,
                    isExterior ? 0 : match.Face.AdjacentSpaceId,
                    isExterior ? string.Empty : match.Face.AdjacentSpaceNumber,
                    isExterior ? string.Empty : match.Face.AdjacentSpaceName,
                    levelName,
                    linearCategory,
                    match.Face.Orientation,
                    thicknessMm,
                    1,
                    candidate.TypeName,
                    candidate.IfcCategory,
                    candidate.IfcGuid,
                    candidate.LinkPath,
                    areaM2,
                    "Ready",
                    $"{linearCategory}|{Normalize(candidate.TypeName)}",
                    [layer],
                    [layer],
                    candidate.TypeName,
                    [ToReference(candidate, match.Face.Curve, sampleZ, spaceHeight)]));
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Space {space.Number} {space.Name}: {(isExterior ? "exterior" : "interior")} opening scan failed ({exception.Message}).");
        }
    }

    private static IReadOnlyList<WindowCandidateMatch> FindOpeningCandidates(
        ExteriorBoundaryCandidate face,
        IReadOnlyList<LinkCandidate> candidates,
        double sampleZ,
        double spaceHeight)
    {
        XYZ tangent = face.Curve.ComputeDerivatives(0.5, true).BasisX;
        tangent = new XYZ(tangent.X, tangent.Y, 0.0).Normalize();
        XYZ[] curvePoints = face.Curve.Tessellate().ToArray();
        double segmentMin = curvePoints.Min(point => point.DotProduct(tangent));
        double segmentMax = curvePoints.Max(point => point.DotProduct(tangent));
        double boundary = face.Curve.Evaluate(0.5, true).DotProduct(face.ExteriorNormal);
        double maximumNormalDistance = UnitUtils.ConvertToInternalUnits(700.0, UnitTypeId.Millimeters);
        double minimumWidth = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);
        double verticalTolerance = UnitUtils.ConvertToInternalUnits(1200.0, UnitTypeId.Millimeters);
        double approximateBottom = sampleZ - Math.Min(
            UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters),
            spaceHeight * 0.5);
        double approximateTop = approximateBottom + spaceHeight;
        var output = new List<WindowCandidateMatch>();
        foreach (LinkCandidate candidate in candidates.Where(item =>
                     item.Kind is LinearComponentKinds.ExteriorWindow or LinearComponentKinds.ExteriorDoor))
        {
            if (face.RootLink is not null && candidate.RootLinkInstanceId != face.RootLink.Id) continue;
            if (candidate.MaxZ < approximateBottom - verticalTolerance ||
                candidate.MinZ > approximateTop + verticalTolerance) continue;
            (double tangentMin, double tangentMax) = Projection(candidate.Corners, tangent);
            double overlap = Math.Min(segmentMax, tangentMax) - Math.Max(segmentMin, tangentMin);
            if (overlap < minimumWidth) continue;
            (double normalMin, double normalMax) = Projection(candidate.Corners, face.ExteriorNormal);
            double relativeMin = normalMin - boundary;
            double relativeMax = normalMax - boundary;
            if (relativeMax < -maximumNormalDistance || relativeMin > maximumNormalDistance) continue;
            double width = Math.Max(tangentMax - tangentMin, overlap);
            double depth = Math.Max(
                EffectiveCandidateThicknessInternal(candidate, face.ExteriorNormal),
                UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters));
            double height = Math.Max(candidate.MaxZ - candidate.MinZ, UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters));
            output.Add(new WindowCandidateMatch(candidate, face, width, height, depth));
        }
        return output;
    }

    private static void AddInteriorWallRows(
        Document hostDocument,
        Space space,
        IReadOnlyList<ExteriorBoundaryCandidate> interiorFaces,
        IReadOnlyList<LinkCandidate> candidates,
        ICollection<SegmentRow> output,
        double sampleZ,
        double height,
        ICollection<string> warnings)
    {
        foreach (ExteriorBoundaryCandidate face in interiorFaces)
        {
            try
            {
                ElementId directLinkedId = face.RootLink is null
                    ? ElementId.InvalidElementId
                    : face.Segment.LinkElementId;
                AssemblyCandidateSelection selection = FindAssemblyCandidates(
                    face.RootLink?.Id ?? ElementId.InvalidElementId,
                    directLinkedId,
                    face.Curve,
                    sampleZ,
                    face.ExteriorNormal,
                    candidates,
                    false);
                if (selection.Layers.Count == 0) continue;
                AssemblyInfo assembly = BuildInteriorAssembly(selection.Layers, face.ExteriorNormal);
                double areaM2 = UnitUtils.ConvertFromInternalUnits(
                    face.Curve.Length * height,
                    UnitTypeId.SquareMeters);
                string levelName = hostDocument.GetElement(space.LevelId)?.Name ?? string.Empty;
                output.Add(new SegmentRow(
                    space.Id.CompatValue(),
                    space.Number ?? string.Empty,
                    space.Name ?? string.Empty,
                    face.AdjacentSpaceId,
                    face.AdjacentSpaceNumber,
                    face.AdjacentSpaceName,
                    levelName,
                    LinearComponentKinds.InteriorWall,
                    face.Orientation,
                    assembly.TotalThicknessMm,
                    assembly.LayerCount,
                    assembly.Description,
                    string.Join(" + ", selection.Layers.Select(item => item.IfcCategory).Distinct(StringComparer.OrdinalIgnoreCase)),
                    string.Join("; ", selection.Layers.Select(item => item.IfcGuid).Where(value => value.Length > 0).Distinct()),
                    string.Join(" + ", selection.Layers.Select(item => item.LinkPath).Distinct(StringComparer.OrdinalIgnoreCase)),
                    areaM2,
                    assembly.LayerCount > 0 ? "Ready" : "Review",
                    assembly.Key,
                    assembly.Layers,
                    assembly.RecommendedLayers,
                    assembly.RecommendedDescription,
                    selection.HighlightTargets
                        .Concat(selection.Layers)
                        .DistinctBy(item => item.UniqueKey)
                        .Select(item => ToReference(item, face.Curve, sampleZ, height))
                        .ToArray()));
            }
            catch (Exception exception)
            {
                warnings.Add($"Space {space.Number} {space.Name}: one interior wall face was skipped ({exception.Message}).");
            }
        }
    }

    private static AssemblyInfo BuildInteriorAssembly(
        IReadOnlyList<LinkCandidate> layers,
        XYZ outwardNormal)
    {
        AssemblyInfo source = BuildAssembly(layers, outwardNormal);
        ExteriorWallLayerItem[] allLayers = source.Layers
            .Select((layer, index) => new ExteriorWallLayerItem(
                index + 1,
                layer.Name,
                layer.ThicknessMm,
                false,
                index == source.Layers.Count - 1,
                layer.SourceElementId,
                layer.SourceUniqueId,
                layer.SourceIfcGuid,
                layer.SourceLinkPath,
                layer.SourceGeometryKey))
            .ToArray();
        ExteriorWallLayerItem[] recommended = source.RecommendedLayers
            .Select((layer, index) => new ExteriorWallLayerItem(
                index + 1,
                layer.Name,
                layer.ThicknessMm,
                false,
                index == source.RecommendedLayers.Count - 1,
                layer.SourceElementId,
                layer.SourceUniqueId,
                layer.SourceIfcGuid,
                layer.SourceLinkPath,
                layer.SourceGeometryKey))
            .ToArray();
        return source with
        {
            Description = source.Description.Replace("[OUTSIDE] ", string.Empty, StringComparison.Ordinal),
            Layers = allLayers,
            RecommendedLayers = recommended,
            RecommendedDescription = source.RecommendedDescription.Replace("[OUTSIDE] ", string.Empty, StringComparison.Ordinal)
        };
    }

    private static bool TryExteriorNormal(
        Document document,
        Space space,
        Curve curve,
        double sampleZ,
        out XYZ exteriorNormal)
    {
        exteriorNormal = XYZ.Zero;
        XYZ midpoint = curve.Evaluate(0.5, true);
        midpoint = new XYZ(midpoint.X, midpoint.Y, sampleZ);
        Transform derivatives = curve.ComputeDerivatives(0.5, true);
        XYZ tangent = new XYZ(derivatives.BasisX.X, derivatives.BasisX.Y, 0.0);
        if (tangent.GetLength() < 1e-9) return false;
        tangent = tangent.Normalize();
        XYZ normal = new XYZ(-tangent.Y, tangent.X, 0.0);

        int insideSign = 0;
        foreach (double distanceMm in new[] { 50.0, 100.0, 200.0, 350.0 })
        {
            double distance = UnitUtils.ConvertToInternalUnits(distanceMm, UnitTypeId.Millimeters);
            bool plusIsCurrent = IsSameSpace(document.GetSpaceAtPoint(midpoint + normal * distance), space);
            bool minusIsCurrent = IsSameSpace(document.GetSpaceAtPoint(midpoint - normal * distance), space);
            if (plusIsCurrent == minusIsCurrent) continue;
            insideSign = plusIsCurrent ? 1 : -1;
            break;
        }

        if (insideSign == 0 && space.Location is LocationPoint location)
        {
            XYZ towardSpace = location.Point - midpoint;
            insideSign = towardSpace.DotProduct(normal) >= 0 ? 1 : -1;
        }
        if (insideSign == 0) return false;
        exteriorNormal = insideSign > 0 ? -normal : normal;
        return true;
    }

    private static Space? FindSpaceAcrossBoundary(
        Document document,
        Space current,
        Curve curve,
        double sampleZ,
        XYZ exteriorNormal)
    {
        double maximumDistance = UnitUtils.ConvertToInternalUnits(
            MaxAssemblyDepthMm,
            UnitTypeId.Millimeters);
        double start = UnitUtils.ConvertToInternalUnits(25.0, UnitTypeId.Millimeters);
        double step = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

        // Probe several positions along the complete Space face. Fine increments
        // cross the empty wall zone and detect the neighbouring Space immediately
        // behind it, without treating a remote Space across an exterior void as an
        // internal neighbour.
        foreach (double parameter in new[] { 0.12, 0.30, 0.50, 0.70, 0.88 })
        {
            XYZ origin = curve.Evaluate(parameter, true);
            origin = new XYZ(origin.X, origin.Y, sampleZ);
            for (double distance = start; distance <= maximumDistance; distance += step)
            {
                Space? adjacent = document.GetSpaceAtPoint(origin + exteriorNormal * distance);
                if (adjacent is not null && !IsSameSpace(adjacent, current))
                    return adjacent;
            }
        }
        return null;
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<BoundaryEdge>> BuildBoundaryIndex(
        IReadOnlyList<Space> spaces,
        ICollection<string> warnings)
    {
        var byLevel = new Dictionary<long, List<BoundaryEdge>>();
        using var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
        };
        foreach (Space space in spaces)
        {
            IList<IList<BoundarySegment>>? loops;
            try { loops = space.GetBoundarySegments(options); }
            catch (Exception exception)
            {
                warnings.Add($"Space {space.Number} {space.Name}: boundary index failed ({exception.Message}).");
                continue;
            }
            if (loops is null) continue;
            if (!byLevel.TryGetValue(space.LevelId.CompatValue(), out List<BoundaryEdge>? edges))
            {
                edges = [];
                byLevel.Add(space.LevelId.CompatValue(), edges);
            }
            foreach (BoundarySegment segment in loops.SelectMany(loop => loop))
            {
                XYZ[] points;
                try { points = segment.GetCurve().Tessellate().ToArray(); }
                catch { continue; }
                for (int index = 1; index < points.Length; index++)
                {
                    XYZ a = points[index - 1];
                    XYZ b = points[index];
                    if (Distance2D(a, b) < UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters))
                        continue;
                    edges.Add(new BoundaryEdge(
                        space.Id.CompatValue(),
                        space.Number ?? string.Empty,
                        space.Name ?? string.Empty,
                        new XYZ(a.X, a.Y, 0.0),
                        new XYZ(b.X, b.Y, 0.0)));
                }
            }
        }
        return byLevel.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<BoundaryEdge>)item.Value);
    }

    private static bool TryFindBoundaryClusterAcross(
        long currentSpaceId,
        Curve curve,
        XYZ exteriorNormal,
        IReadOnlyList<BoundaryEdge> boundaries,
        out BoundaryEdge? adjacentBoundary)
    {
        adjacentBoundary = null;
        if (boundaries.Count == 0) return false;
        XYZ[] facePoints = curve.Tessellate()
            .Select(point => new XYZ(point.X, point.Y, 0.0))
            .ToArray();
        if (facePoints.Length < 2) return false;
        XYZ faceStart = facePoints[0];
        XYZ faceEnd = facePoints[^1];
        XYZ tangent = faceEnd - faceStart;
        tangent = new XYZ(tangent.X, tangent.Y, 0.0);
        if (tangent.GetLength() < 1e-9) return false;
        tangent = tangent.Normalize();
        XYZ normal = new XYZ(exteriorNormal.X, exteriorNormal.Y, 0.0).Normalize();
        double faceMin = facePoints.Min(point => point.DotProduct(tangent));
        double faceMax = facePoints.Max(point => point.DotProduct(tangent));
        double facePlane = facePoints.Average(point => point.DotProduct(normal));
        double adjacencyDepth = UnitUtils.ConvertToInternalUnits(MaxAssemblyDepthMm, UnitTypeId.Millimeters);
        double backTolerance = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);
        double minimumOverlap = UnitUtils.ConvertToInternalUnits(MinimumOverlapMm, UnitTypeId.Millimeters);

        foreach (BoundaryEdge edge in boundaries)
        {
            if (edge.SpaceId == currentSpaceId) continue;
            XYZ segment = edge.B - edge.A;
            double length = Distance2D(edge.A, edge.B);
            if (length < 1e-9) continue;
            XYZ edgeDirection = segment.Normalize();
            if (Math.Abs(edgeDirection.DotProduct(tangent)) < 0.95) continue;
            double edgeMidNormal = ((edge.A + edge.B) * 0.5).DotProduct(normal) - facePlane;
            if (edgeMidNormal < -backTolerance || edgeMidNormal > adjacencyDepth) continue;
            double edgeMin = Math.Min(edge.A.DotProduct(tangent), edge.B.DotProduct(tangent));
            double edgeMax = Math.Max(edge.A.DotProduct(tangent), edge.B.DotProduct(tangent));
            double overlap = Math.Min(faceMax, edgeMax) - Math.Max(faceMin, edgeMin);
            if (overlap < minimumOverlap) continue;
            adjacentBoundary = edge;
            return true;
        }
        return false;
    }

    private static double Distance2D(XYZ first, XYZ second)
    {
        double dx = first.X - second.X;
        double dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsSameSpace(Space? first, Space second) =>
        first is not null && first.Id == second.Id;

    private static AssemblyCandidateSelection FindAssemblyCandidates(
        ElementId rootLinkId,
        ElementId directLinkedId,
        Curve curve,
        double sampleZ,
        XYZ exteriorNormal,
        IReadOnlyList<LinkCandidate> inventory,
        bool includeNeighborLayers = true)
    {
        XYZ midpoint = curve.Evaluate(0.5, true);
        midpoint = new XYZ(midpoint.X, midpoint.Y, sampleZ);
        XYZ tangent = curve.ComputeDerivatives(0.5, true).BasisX;
        tangent = new XYZ(tangent.X, tangent.Y, 0.0).Normalize();
        IReadOnlyList<XYZ> curvePoints = curve.Tessellate().ToArray();
        double segmentMin = curvePoints.Min(point => point.DotProduct(tangent));
        double segmentMax = curvePoints.Max(point => point.DotProduct(tangent));
        double segmentMid = midpoint.DotProduct(tangent);
        double maxDepth = UnitUtils.ConvertToInternalUnits(MaxAssemblyDepthMm, UnitTypeId.Millimeters);
        double minOverlap = UnitUtils.ConvertToInternalUnits(MinimumOverlapMm, UnitTypeId.Millimeters);
        double maxTangentGap = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);
        var matches = new List<CandidateMatch>();

        foreach (LinkCandidate candidate in inventory)
        {
            if (candidate.Kind != "WALL") continue;
            if (rootLinkId != ElementId.InvalidElementId && candidate.RootLinkInstanceId != rootLinkId) continue;
            if (sampleZ < candidate.MinZ - 0.1 || sampleZ > candidate.MaxZ + 0.1) continue;
            (double tangentMin, double tangentMax) = Projection(candidate.Corners, tangent);
            double overlap = Math.Min(segmentMax, tangentMax) - Math.Max(segmentMin, tangentMin);
            bool direct = candidate.Depth == 0 && candidate.ElementId == directLinkedId;
            double requiredOverlap = direct
                ? UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters)
                : minOverlap;
            if (overlap < requiredOverlap) continue;
            double tangentExtent = tangentMax - tangentMin;
            double tangentGap = segmentMid < tangentMin
                ? tangentMin - segmentMid
                : segmentMid > tangentMax
                    ? segmentMid - tangentMax
                    : 0.0;
            if (tangentGap > maxTangentGap / 3.0) continue;

            (double rawNormalMin, double rawNormalMax) = Projection(candidate.Corners, exteriorNormal);
            double projectedThickness = EffectiveCandidateThicknessInternal(candidate, exteriorNormal);
            if (projectedThickness < 1e-9 || projectedThickness > maxDepth) continue;
            double normalCenter = (rawNormalMin + rawNormalMax) / 2.0;
            double normalMin = normalCenter - projectedThickness / 2.0;
            double normalMax = normalCenter + projectedThickness / 2.0;
            bool wallProportioned = tangentExtent >= Math.Max(requiredOverlap, projectedThickness * 1.25);
            if (candidate.LayerParts.Count == 0 && !wallProportioned) continue;
            double boundary = midpoint.DotProduct(exteriorNormal);
            double relativeMin = normalMin - boundary;
            double relativeMax = normalMax - boundary;
            if (relativeMax < -UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters) ||
                relativeMin > maxDepth) continue;
            double distance = relativeMin > 0 ? relativeMin : Math.Abs(relativeMax < 0 ? relativeMax : 0);
            matches.Add(new CandidateMatch(
                candidate,
                distance,
                overlap,
                relativeMin,
                relativeMax,
                projectedThickness));
        }

        if (matches.Count == 0) return new AssemblyCandidateSelection([], []);
        List<CandidateMatch> directMatches = matches
            .Where(item => item.Candidate.Depth == 0 && item.Candidate.ElementId == directLinkedId)
            .OrderBy(item => item.Distance)
            .ThenByDescending(item => item.Overlap)
            .ToList();

        CandidateMatch anchor = directMatches.FirstOrDefault()
            ?? matches
                .OrderBy(item => item.Candidate.MarkedExternal == true ? 0 : 1)
                .ThenBy(item => item.Distance)
                .ThenByDescending(item => item.Overlap)
                .First();
        ElementId anchorRootLinkId = anchor.Candidate.RootLinkInstanceId;
        double layerBand = maxDepth;
        CandidateMatch[] localMatches = matches
            .Where(item =>
                item.Candidate.RootLinkInstanceId == anchorRootLinkId &&
                item.Distance <= layerBand)
            .OrderBy(item => item.Candidate.MarkedExternal == true ? 0 : item.Candidate.MarkedExternal == false ? 2 : 1)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Thickness)
            .ThenByDescending(item => item.Overlap)
            .DistinctBy(item => item.Candidate.UniqueKey)
            .ToArray();

        var selected = new List<CandidateMatch>();
        CandidateMatch[] remaining = localMatches
            .OrderBy(item => item.RelativeMin)
            .ThenBy(item => item.RelativeMax)
            .ToArray();
        CandidateMatch seed = directMatches
            .FirstOrDefault(item => item.Candidate.RootLinkInstanceId == anchorRootLinkId)
            ?? remaining
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.LayerParts.Count > 0 ? 0 : 1)
            .ThenByDescending(item => item.Overlap)
            .First();
        selected.Add(seed);
        double maximumGap = UnitUtils.ConvertToInternalUnits(MaximumLayerGapMm, UnitTypeId.Millimeters);

        while (includeNeighborLayers && selected.Count < MaximumLayersPerAssembly)
        {
            double stackMin = selected.Min(item => item.RelativeMin);
            double stackMax = selected.Max(item => item.RelativeMax);
            CandidateMatch? next = remaining
                .Where(match => !selected.Contains(match))
                .Where(match => !IsDuplicateVolume(selected, match))
                .Select(match => new
                {
                    Match = match,
                    Gap = match.RelativeMin > stackMax
                        ? match.RelativeMin - stackMax
                        : stackMin > match.RelativeMax
                            ? stackMin - match.RelativeMax
                            : 0.0
                })
                .Where(item => item.Gap <= maximumGap)
                .Where(item =>
                {
                    double proposedMin = Math.Min(stackMin, item.Match.RelativeMin);
                    double proposedMax = Math.Max(stackMax, item.Match.RelativeMax);
                    return proposedMax - proposedMin <= maxDepth &&
                        selected.Sum(selectedItem => selectedItem.Thickness) + item.Match.Thickness <= maxDepth;
                })
                .OrderBy(item => item.Gap)
                .ThenBy(item => item.Match.RelativeMin)
                .ThenByDescending(item => item.Match.Overlap)
                .Select(item => item.Match)
                .FirstOrDefault();
            if (next is null) break;
            selected.Add(next);
        }
        LinkCandidate[] layers = selected
            .OrderBy(item => item.RelativeMin)
            .Select(item => item.Candidate)
            .ToArray();

        // Keep every architectural Wall/Part that clashes with or touches the
        // confirmed exterior Space face. Overlapping representations are retained
        // here for traceability/highlight, but BuildAssembly still de-duplicates
        // their physical volume so the material layer count remains correct.
        double selectedMin = selected.Min(item => item.RelativeMin);
        double selectedMax = selected.Max(item => item.RelativeMax);
        double highlightTolerance = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);
        LinkCandidate[] highlightTargets = localMatches
            .Where(item =>
            {
                double intersection = Math.Min(selectedMax, item.RelativeMax) -
                    Math.Max(selectedMin, item.RelativeMin);
                double gap = item.RelativeMin > selectedMax
                    ? item.RelativeMin - selectedMax
                    : selectedMin > item.RelativeMax
                        ? selectedMin - item.RelativeMax
                        : 0.0;
                return intersection > 1e-6 || gap <= highlightTolerance;
            })
            .OrderBy(item => item.RelativeMin)
            .ThenByDescending(item => item.Overlap)
            .Select(item => item.Candidate)
            .DistinctBy(item => item.UniqueKey)
            .ToArray();

        return new AssemblyCandidateSelection(layers, highlightTargets);
    }

    private static bool IsDuplicateVolume(IReadOnlyList<CandidateMatch> selected, CandidateMatch match)
    {
        double thickness = Math.Max(match.Thickness, 1e-9);
        return selected.Any(existing =>
        {
            double intersection = Math.Min(existing.RelativeMax, match.RelativeMax) -
                Math.Max(existing.RelativeMin, match.RelativeMin);
            return intersection > Math.Min(existing.Thickness, thickness) * 0.55;
        });
    }

    private static AssemblyInfo BuildAssembly(IReadOnlyList<LinkCandidate> layers, XYZ exteriorNormal)
    {
        var parts = new List<LayerPart>();
        foreach (LinkCandidate candidate in layers)
        {
            if (candidate.LayerParts.Count > 0)
            {
                // Revit compound layers are reported exterior-to-interior. The
                // candidate stack itself is ordered Space-to-outside, so reverse
                // compound layers to keep one consistent physical direction.
                parts.AddRange(candidate.LayerParts.Reverse().Select(part => AttachSource(part, candidate)));
                continue;
            }
            double thicknessMm = UnitUtils.ConvertFromInternalUnits(
                EffectiveCandidateThicknessInternal(candidate, exteriorNormal),
                UnitTypeId.Millimeters);
            parts.Add(AttachSource(new LayerPart(candidate.TypeName, thicknessMm), candidate));
        }

        parts = parts.Where(item => item.ThicknessMm > 0.2).ToList();
        if (parts.Count > 0)
            parts[^1] = parts[^1] with { IsExteriorFace = true };
        parts = parts
            .OrderByDescending(item => item.ThicknessMm)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        double total = PhysicalStackThicknessMm(layers, exteriorNormal);
        ExteriorWallLayerItem[] layerItems = parts
            .Select((item, index) => new ExteriorWallLayerItem(
                index + 1,
                item.Name,
                item.ThicknessMm,
                item.IsExteriorFace,
                index == parts.Count - 1,
                item.SourceElementId,
                item.SourceUniqueId,
                item.SourceIfcGuid,
                item.SourceLinkPath,
                item.SourceGeometryKey))
            .ToArray();
        string description = string.Join(" + ", parts.Select(item =>
            $"{(item.IsExteriorFace ? "[OUTSIDE] " : string.Empty)}{item.Name} {item.ThicknessMm:0.#} mm"));
        string key = string.Join("|", parts.Select(item =>
            $"{Normalize(item.Name)}:{Math.Round(item.ThicknessMm, 0).ToString(CultureInfo.InvariantCulture)}"));
        if (key.Length == 0)
            key = string.Join("|", layers.Select(item => $"{Normalize(item.TypeName)}:{item.ElementId.CompatValue()}"));
        IReadOnlyList<LayerPart> recommendedParts = RecommendLayers(parts);
        ExteriorWallLayerItem[] recommendedItems = recommendedParts
            .Select((item, index) => new ExteriorWallLayerItem(
                index + 1,
                item.Name,
                item.ThicknessMm,
                item.IsExteriorFace,
                index == recommendedParts.Count - 1,
                item.SourceElementId,
                item.SourceUniqueId,
                item.SourceIfcGuid,
                item.SourceLinkPath,
                item.SourceGeometryKey))
            .ToArray();
        string recommendedDescription = string.Join(" + ", recommendedParts.Select(item =>
            $"{(item.IsExteriorFace ? "[OUTSIDE] " : string.Empty)}{item.Name} {item.ThicknessMm:0.#} mm"));
        return new AssemblyInfo(
            total,
            parts.Count,
            description,
            key,
            layerItems,
            recommendedItems,
            recommendedDescription);
    }

    private static LayerPart AttachSource(LayerPart part, LinkCandidate candidate) => part with
    {
        SourceElementId = candidate.ElementId.CompatValue().ToString(CultureInfo.InvariantCulture),
        SourceUniqueId = candidate.ElementUniqueId,
        SourceIfcGuid = candidate.IfcGuid,
        SourceLinkPath = candidate.LinkPath,
        SourceGeometryKey = candidate.GeometryKey
    };

    private static double PhysicalStackThicknessMm(
        IReadOnlyList<LinkCandidate> layers,
        XYZ exteriorNormal)
    {
        if (layers.Count == 0) return 0.0;
        var intervals = layers
            .Select(layer =>
            {
                (double min, double max) = Projection(layer.Corners, exteriorNormal);
                double center = (min + max) / 2.0;
                double thickness = EffectiveCandidateThicknessInternal(layer, exteriorNormal);
                return (Min: center - thickness / 2.0, Max: center + thickness / 2.0);
            })
            .Where(interval => interval.Max - interval.Min > 1e-9)
            .OrderBy(interval => interval.Min)
            .ToArray();
        if (intervals.Length == 0) return 0.0;

        double joinTolerance = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
        double totalInternal = 0.0;
        double currentMin = intervals[0].Min;
        double currentMax = intervals[0].Max;
        for (int index = 1; index < intervals.Length; index++)
        {
            (double nextMin, double nextMax) = intervals[index];
            if (nextMin <= currentMax + joinTolerance)
            {
                currentMax = Math.Max(currentMax, nextMax);
                continue;
            }
            totalInternal += Math.Max(currentMax - currentMin, 0.0);
            currentMin = nextMin;
            currentMax = nextMax;
        }
        totalInternal += Math.Max(currentMax - currentMin, 0.0);
        return UnitUtils.ConvertFromInternalUnits(totalInternal, UnitTypeId.Millimeters);
    }

    private static double EffectiveCandidateThicknessInternal(
        LinkCandidate candidate,
        XYZ exteriorNormal)
    {
        if (IsPlausibleWallThickness(candidate.DeclaredThicknessInternal))
            return candidate.DeclaredThicknessInternal!.Value;

        (double projectedMin, double projectedMax) = Projection(candidate.Corners, exteriorNormal);
        double projected = projectedMax - projectedMin;
        double orientedMinimum = OrientedMinimumHorizontalExtent(candidate.Corners);
        if (IsPlausibleWallThickness(orientedMinimum))
            return Math.Min(projected, orientedMinimum);
        return projected;
    }

    private static double OrientedMinimumHorizontalExtent(IReadOnlyList<XYZ> points)
    {
        if (points.Count < 2) return 0.0;
        double centerX = points.Average(point => point.X);
        double centerY = points.Average(point => point.Y);
        double xx = 0.0;
        double yy = 0.0;
        double xy = 0.0;
        foreach (XYZ point in points)
        {
            double dx = point.X - centerX;
            double dy = point.Y - centerY;
            xx += dx * dx;
            yy += dy * dy;
            xy += dx * dy;
        }

        double angle = 0.5 * Math.Atan2(2.0 * xy, xx - yy);
        var firstAxis = new XYZ(Math.Cos(angle), Math.Sin(angle), 0.0);
        var secondAxis = new XYZ(-Math.Sin(angle), Math.Cos(angle), 0.0);
        (double firstMin, double firstMax) = Projection(points, firstAxis);
        (double secondMin, double secondMax) = Projection(points, secondAxis);
        return Math.Min(firstMax - firstMin, secondMax - secondMin);
    }

    private static bool IsPlausibleWallThickness(double? value)
    {
        if (value is null || value <= 0.0) return false;
        double millimeters = UnitUtils.ConvertFromInternalUnits(value.Value, UnitTypeId.Millimeters);
        return millimeters >= 2.0 && millimeters <= 2000.0;
    }

    private static IReadOnlyList<LayerPart> RecommendLayers(IReadOnlyList<LayerPart> parts)
    {
        if (parts.Count <= 3) return parts;
        LayerPart core = parts.OrderByDescending(item => item.ThicknessMm).First();
        LayerPart? exterior = parts.FirstOrDefault(item => item.IsExteriorFace);
        if (exterior is null || SameLayer(core, exterior))
            exterior = parts.OrderBy(item => item.ThicknessMm).FirstOrDefault(item => !SameLayer(item, core));
        LayerPart? intermediate = parts
            .Where(item => !SameLayer(item, core) && (exterior is null || !SameLayer(item, exterior)))
            .OrderByDescending(item => item.ThicknessMm)
            .FirstOrDefault();

        return new[] { core, intermediate, exterior }
            .Where(item => item is not null)
            .Cast<LayerPart>()
            .DistinctBy(item => $"{Normalize(item.Name)}|{Math.Round(item.ThicknessMm, 1)}")
            .OrderByDescending(item => item.ThicknessMm)
            .ToArray();
    }

    private static bool SameLayer(LayerPart first, LayerPart second) =>
        string.Equals(Normalize(first.Name), Normalize(second.Name), StringComparison.Ordinal) &&
        Math.Abs(first.ThicknessMm - second.ThicknessMm) < 0.1;

    private static IReadOnlyList<LinkCandidate> CollectLinkedWallCandidates(
        Document hostDocument,
        ICollection<string> warnings)
    {
        var output = new List<LinkCandidate>();
        var inventoryReports = new List<string>();
        List<RevitLinkInstance> roots = new FilteredElementCollector(hostDocument)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(IsTopLevelLink)
            .ToList();
        foreach (RevitLinkInstance root in roots)
        {
            Document? linkedDocument = root.GetLinkDocument();
            if (linkedDocument is null) continue;
            try
            {
                CollectLinkDocument(
                    linkedDocument,
                    root.GetTotalTransform(),
                    root.Id,
                    root.Name,
                    0,
                    [],
                    output,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    inventoryReports);
            }
            catch (Exception exception)
            {
                warnings.Add($"Link {root.Name}: {exception.Message}");
            }
        }
        if (output.Count == 0)
        {
            foreach (string report in inventoryReports)
                warnings.Add(report);
        }
        return output;
    }

    private static void CollectLinkDocument(
        Document document,
        Transform toHost,
        ElementId rootLinkInstanceId,
        string linkPath,
        int depth,
        IReadOnlyList<ElementId> nestedLinkInstancePath,
        ICollection<LinkCandidate> output,
        ISet<string> ancestry,
        ICollection<string> inventoryReports)
    {
        string documentKey = string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName;
        string ancestryKey = $"{documentKey}|{depth}|{linkPath}";
        if (!ancestry.Add(ancestryKey)) return;

        Element[] modelElements = new FilteredElementCollector(document)
            .WhereElementIsNotElementType()
            .Where(element => element.Category?.CategoryType == CategoryType.Model)
            .ToArray();
        int directShapeCount = 0;
        int importInstanceCount = 0;
        int wallLikeCount = 0;
        foreach (Element element in modelElements)
        {
            if (element is DirectShape) directShapeCount++;
            if (element is ImportInstance) importInstanceCount++;
            BoundingBoxXYZ? bounds = element.get_BoundingBox(null);
            if (bounds is null) continue;
            if (element is ImportInstance importInstance)
            {
                int beforeImport = output.Count;
                CollectImportGeometryCandidates(
                    document,
                    importInstance,
                    toHost,
                    rootLinkInstanceId,
                    documentKey,
                    linkPath,
                    depth,
                    nestedLinkInstancePath,
                    output);
                wallLikeCount += output.Count - beforeImport;
                continue;
            }
            string candidateKind;
            if (IsDoorLike(document, element))
                candidateKind = LinearComponentKinds.ExteriorDoor;
            else if (IsWindowLike(document, element))
                candidateKind = LinearComponentKinds.ExteriorWindow;
            else if (IsWallLike(document, element, bounds))
                candidateKind = "WALL";
            else
                continue;
            wallLikeCount++;
            IReadOnlyList<XYZ> corners = ElementGeometryPoints(element, bounds, toHost);
            if (corners.Count == 0) continue;
            IReadOnlyList<LayerPart> layerParts = ReadLayerParts(document, element);
            string componentMaterialName = ReadComponentMaterialName(document, element);
            double? declaredThickness = ReadDeclaredThicknessInternal(document, element);
            output.Add(new LinkCandidate(
                rootLinkInstanceId,
                element.Id,
                element.UniqueId ?? string.Empty,
                element.Id.CompatValue().ToString(CultureInfo.InvariantCulture),
                documentKey,
                toHost,
                nestedLinkInstancePath.Select(id => id.CompatValue()).ToArray(),
                linkPath,
                depth,
                candidateKind,
                element.Name ?? string.Empty,
                componentMaterialName,
                IfcCategory(element),
                ReadFirstParameter(element, "IfcGUID", "IFC GUID", "IfcGuid", "GlobalId"),
                ReadExternalFlag(document, element),
                declaredThickness,
                layerParts,
                corners,
                corners.Min(point => point.Z),
                corners.Max(point => point.Z)));
        }

        if (depth >= 4) return;
        RevitLinkInstance[] nestedLinks = new FilteredElementCollector(document)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToArray();
        int resolvedNestedCount = 0;
        var nestedResolutionNotes = new List<string>();
        foreach (RevitLinkInstance nested in nestedLinks)
        {
            LinkDocumentResolution resolution = ResolveLinkDocument(nested);
            nestedResolutionNotes.Add($"{nested.Name}: {resolution.Note}");
            if (resolution.Document is null) continue;
            resolvedNestedCount++;
            try
            {
                string childPath = $"{linkPath} > {nested.Name}";
                CollectLinkDocument(
                    resolution.Document,
                    toHost.Multiply(nested.GetTransform()),
                    rootLinkInstanceId,
                    childPath,
                    depth + 1,
                    nestedLinkInstancePath.Concat([nested.Id]).ToArray(),
                    output,
                    ancestry,
                    inventoryReports);
            }
            finally
            {
                if (resolution.OpenedByScanner)
                {
                    try { resolution.Document.Close(false); }
                    catch { /* The candidate inventory is already captured; Revit owns final cleanup. */ }
                }
            }
        }
        inventoryReports.Add(
            $"Link inventory [{linkPath}]: {modelElements.Length} model elements, " +
            $"{directShapeCount} DirectShapes, {importInstanceCount} ImportInstances, " +
            $"{wallLikeCount} wall-like elements, {resolvedNestedCount}/{nestedLinks.Length} nested links resolved." +
            (nestedResolutionNotes.Count == 0 ? string.Empty : $" {string.Join(" | ", nestedResolutionNotes)}"));
        ancestry.Remove(ancestryKey);
    }

    private static IReadOnlyList<XYZ> ElementGeometryPoints(
        Element element,
        BoundingBoxXYZ fallbackBounds,
        Transform toHost)
    {
        var points = new List<XYZ>();
        try
        {
            using var options = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false,
                ComputeReferences = false
            };
            GeometryElement? geometry = element.get_Geometry(options);
            if (geometry is not null) CollectGeometryPoints(geometry, points);
        }
        catch
        {
            // Some linked/imported elements do not expose geometry outside their
            // owning view. Their transformed bounding box remains a safe fallback.
        }

        IEnumerable<XYZ> source = points.Count >= 2
            ? points
            : BoundingCorners(fallbackBounds);
        return source
            .Select(toHost.OfPoint)
            .ToArray();
    }

    private static void CollectGeometryPoints(GeometryElement geometry, ICollection<XYZ> output)
    {
        const int maximumPointCount = 2048;
        foreach (GeometryObject geometryObject in geometry)
        {
            if (output.Count >= maximumPointCount) return;
            switch (geometryObject)
            {
                case GeometryInstance instance:
                    CollectGeometryPoints(instance.GetInstanceGeometry(), output);
                    break;
                case Solid solid when solid.Volume > 1e-9:
                    foreach (Edge edge in solid.Edges)
                    {
                        foreach (XYZ point in edge.Tessellate())
                        {
                            output.Add(point);
                            if (output.Count >= maximumPointCount) return;
                        }
                    }
                    break;
                case Mesh mesh:
                    foreach (XYZ point in mesh.Vertices)
                    {
                        output.Add(point);
                        if (output.Count >= maximumPointCount) return;
                    }
                    break;
                case Curve curve:
                    foreach (XYZ point in curve.Tessellate())
                    {
                        output.Add(point);
                        if (output.Count >= maximumPointCount) return;
                    }
                    break;
            }
        }
    }

    private static LinkDocumentResolution ResolveLinkDocument(RevitLinkInstance instance)
    {
        Document? direct = instance.GetLinkDocument();
        if (direct is not null)
            return new LinkDocumentResolution(direct, false, "resolved by Revit API");

        RevitLinkType? linkType = instance.Document.GetElement(instance.GetTypeId()) as RevitLinkType;
        ExternalFileReference? externalReference = null;
        try { externalReference = linkType?.GetExternalFileReference(); }
        catch { /* Some external resource providers do not expose a file reference. */ }
        if (externalReference is null)
            return new LinkDocumentResolution(null, false, "GetLinkDocument=null; no external path");

        string status = externalReference.GetLinkedFileStatus().ToString();
        ModelPath absolutePath = externalReference.GetAbsolutePath();
        string visiblePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(absolutePath);
        string targetTitle = Path.GetFileNameWithoutExtension(visiblePath);
        foreach (Document candidate in instance.Document.Application.Documents)
        {
            if (candidate.Equals(instance.Document)) continue;
            if (SamePath(candidate.PathName, visiblePath) ||
                string.Equals(candidate.Title, targetTitle, StringComparison.OrdinalIgnoreCase))
                return new LinkDocumentResolution(candidate, false, $"{status}; resolved from open documents");
        }

        if (externalReference.GetLinkedFileStatus() != LinkedFileStatus.Loaded)
            return new LinkDocumentResolution(null, false, $"{status}; {visiblePath}");

        try
        {
            var openOptions = new OpenOptions { Audit = false };
            Document opened = instance.Document.Application.OpenDocumentFile(absolutePath, openOptions);
            return new LinkDocumentResolution(opened, true, $"{status}; opened temporarily from {visiblePath}");
        }
        catch (Exception exception)
        {
            return new LinkDocumentResolution(
                null,
                false,
                $"{status}; GetLinkDocument=null; temporary open failed ({exception.Message})");
        }
    }

    private static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        string left = first.Replace('/', '\\').TrimEnd('\\');
        string right = second.Replace('/', '\\').TrimEnd('\\');
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTopLevelLink(RevitLinkInstance instance)
    {
        RevitLinkType? type = instance.Document.GetElement(instance.GetTypeId()) as RevitLinkType;
        return type is null || !type.IsNestedLink;
    }

    private static bool IsWallLike(Document document, Element element, BoundingBoxXYZ bounds)
    {
        if (element.Category is null) return false;
        long categoryId = element.Category.Id.CompatValue();
        if (categoryId == (long)BuiltInCategory.OST_Walls ||
            categoryId == (long)BuiltInCategory.OST_CurtainWallPanels)
            return true;
        if (categoryId == (long)BuiltInCategory.OST_Parts)
            return LooksLikeVerticalWallPart(bounds);

        string typeName = document.GetElement(element.GetTypeId())?.Name ?? string.Empty;
        string identity = string.Join(" ",
            element.Category.Name,
            element.Name ?? string.Empty,
            typeName,
            ReadFirstParameter(
                element,
                "IfcExportAs",
                "IfcType",
                "IFC Entity",
                "Entity",
                "IfcName",
                "IfcObjectType",
                "ObjectType",
                "PredefinedType"));
        if (ContainsExcludedIdentity(identity)) return false;
        if (ContainsWallIdentity(identity)) return true;

        // IFC importers frequently map IfcBuildingElementPart layers to Generic Models
        // without keeping an IfcWall category. Include only vertical, wall-proportioned
        // DirectShapes so nearby furniture and horizontal slabs do not pollute a batch.
        if (element is not DirectShape)
            return false;
        return LooksLikeVerticalWallPart(bounds);
    }

    private static bool IsWindowLike(Document document, Element element)
    {
        if (element.Category is null) return false;
        if (element.Category.Id.CompatValue() == (long)BuiltInCategory.OST_Windows) return true;
        string typeName = document.GetElement(element.GetTypeId())?.Name ?? string.Empty;
        string identity = string.Join(" ",
            element.Category.Name,
            element.Name ?? string.Empty,
            typeName,
            ReadFirstParameter(
                element,
                "IfcExportAs",
                "IfcType",
                "IFC Entity",
                "Entity",
                "IfcName",
                "IfcObjectType",
                "ObjectType",
                "PredefinedType"));
        return identity.Contains("IfcWindow", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Window", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Fenster", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDoorLike(Document document, Element element)
    {
        if (element.Category is null) return false;
        if (element.Category.Id.CompatValue() == (long)BuiltInCategory.OST_Doors) return true;
        string typeName = document.GetElement(element.GetTypeId())?.Name ?? string.Empty;
        string identity = string.Join(" ",
            element.Category.Name,
            element.Name ?? string.Empty,
            typeName,
            ReadFirstParameter(
                element,
                "IfcExportAs",
                "IfcType",
                "IFC Entity",
                "Entity",
                "IfcName",
                "IfcObjectType",
                "ObjectType",
                "PredefinedType"));
        return identity.Contains("IfcDoor", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Door", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Tuer", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Tür", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWallIdentity(string identity) =>
        identity.Contains("IfcWall", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("IfcCurtainWall", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("IfcBuildingElementPart", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("IfcCovering", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("Wall", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("Wand", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("Außenwand", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("Aussenwand", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExcludedIdentity(string identity)
    {
        string[] excluded =
        [
            "IfcDoor", "IfcWindow", "IfcColumn", "IfcBeam", "IfcMember", "IfcFurniture",
            "IfcFlow", "IfcPipe", "IfcDuct", "IfcStair", "IfcRailing", "IfcSlab", "IfcRoof",
            "Door", "Window", "Column", "Furniture", "Equipment", "Pipe", "Duct", "Stair",
            "Railing", "Floor", "Roof", "Tür", "Fenster", "Stütze", "Decke", "Dach"
        ];
        return excluded.Any(value => identity.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static void CollectImportGeometryCandidates(
        Document document,
        ImportInstance importInstance,
        Transform toHost,
        ElementId rootLinkInstanceId,
        string documentKey,
        string linkPath,
        int depth,
        IReadOnlyList<ElementId> nestedLinkInstancePath,
        ICollection<LinkCandidate> output)
    {
        using var options = new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = false,
            ComputeReferences = false
        };
        GeometryElement? geometry = importInstance.get_Geometry(options);
        if (geometry is null) return;

        int shapeIndex = 0;
        foreach (ImportedShape shape in EnumerateImportedShapes(document, geometry))
        {
            if (!shape.Identity.Contains("Wall", StringComparison.OrdinalIgnoreCase) &&
                !shape.Identity.Contains("Wand", StringComparison.OrdinalIgnoreCase) &&
                !LooksLikeVerticalWallPart(shape.Bounds))
                continue;
            if (ContainsExcludedIdentity(shape.Identity)) continue;

            IReadOnlyList<XYZ> corners = BoundingCorners(shape.Bounds)
                .Select(point => toHost.OfPoint(point))
                .ToArray();
            if (corners.Count == 0) continue;
            string key = $"IMPORT:{importInstance.Id.CompatValue()}:{shapeIndex++}";
            output.Add(new LinkCandidate(
                rootLinkInstanceId,
                importInstance.Id,
                importInstance.UniqueId ?? string.Empty,
                key,
                documentKey,
                toHost,
                nestedLinkInstancePath.Select(id => id.CompatValue()).ToArray(),
                linkPath,
                depth,
                "WALL",
                shape.Identity,
                shape.Identity,
                string.IsNullOrWhiteSpace(shape.Identity) ? "Imported geometry" : shape.Identity,
                string.Empty,
                null,
                null,
                [],
                corners,
                corners.Min(point => point.Z),
                corners.Max(point => point.Z)));
        }
    }

    private static IEnumerable<ImportedShape> EnumerateImportedShapes(
        Document document,
        GeometryElement geometry)
    {
        foreach (GeometryObject geometryObject in geometry)
        {
            if (geometryObject is GeometryInstance instance)
            {
                foreach (ImportedShape nested in EnumerateImportedShapes(document, instance.GetInstanceGeometry()))
                    yield return nested;
                continue;
            }

            BoundingBoxXYZ? bounds = geometryObject switch
            {
                Solid solid when solid.Volume > 1e-9 => solid.GetBoundingBox(),
                Mesh mesh when mesh.Vertices.Count > 2 => BoundsFromPoints(mesh.Vertices),
                _ => null
            };
            if (bounds is null) continue;
            GraphicsStyle? style = document.GetElement(geometryObject.GraphicsStyleId) as GraphicsStyle;
            string identity = style?.GraphicsStyleCategory?.Name ?? style?.Name ?? "Imported geometry";
            yield return new ImportedShape(bounds, identity);
        }
    }

    private static BoundingBoxXYZ BoundsFromPoints(IEnumerable<XYZ> points)
    {
        XYZ[] values = points.ToArray();
        return new BoundingBoxXYZ
        {
            Min = new XYZ(values.Min(point => point.X), values.Min(point => point.Y), values.Min(point => point.Z)),
            Max = new XYZ(values.Max(point => point.X), values.Max(point => point.Y), values.Max(point => point.Z))
        };
    }

    private static bool LooksLikeVerticalWallPart(BoundingBoxXYZ bounds)
    {
        IReadOnlyList<XYZ> corners = BoundingCorners(bounds);
        double dx = corners.Max(point => point.X) - corners.Min(point => point.X);
        double dy = corners.Max(point => point.Y) - corners.Min(point => point.Y);
        double dz = corners.Max(point => point.Z) - corners.Min(point => point.Z);
        double length = Math.Max(dx, dy);
        double thickness = Math.Min(dx, dy);
        double minimumHeight = UnitUtils.ConvertToInternalUnits(600.0, UnitTypeId.Millimeters);
        double minimumLength = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);
        double maximumThickness = UnitUtils.ConvertToInternalUnits(1500.0, UnitTypeId.Millimeters);
        return dz >= minimumHeight && length >= minimumLength && thickness <= maximumThickness &&
            length >= Math.Max(thickness * 1.25, minimumLength);
    }

    private static IReadOnlyList<LayerPart> ReadLayerParts(Document document, Element element)
    {
        if (element is not Wall wall) return [];
        WallType? wallType = document.GetElement(wall.GetTypeId()) as WallType;
        CompoundStructure? structure = wallType?.GetCompoundStructure();
        if (structure is null) return [];
        var parts = new List<LayerPart>();
        foreach (CompoundStructureLayer layer in structure.GetLayers())
        {
            string? name = document.GetElement(layer.MaterialId)?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = layer.Function.ToString();
            double thicknessMm = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Millimeters);
            if (thicknessMm > 0.2) parts.Add(new LayerPart(name, thicknessMm));
        }
        return parts;
    }

    private static string ReadComponentMaterialName(Document document, Element element)
    {
        Element? type = document.GetElement(element.GetTypeId());
        string component = FirstNonEmpty(
            ReadFirstParameter(element, "IfcName", "IFC Name", "IfcObjectType", "ObjectType"),
            ReadFirstParameter(element, "IfcDescription", "IFC Description", "Description"),
            type is null ? string.Empty : ReadFirstParameter(type, "IfcName", "IFC Name", "IfcObjectType", "ObjectType"),
            type?.Name,
            element.Name,
            "Wall");
        string material = FirstNonEmpty(
            ReadFirstParameter(element, "IfcMaterial", "IFC Material", "Material Name", "Material"),
            type is null ? string.Empty : ReadFirstParameter(type, "IfcMaterial", "IFC Material", "Material Name", "Material"));
        if (string.IsNullOrWhiteSpace(material) ||
            component.Contains(material, StringComparison.CurrentCultureIgnoreCase))
            return component;
        return $"{component} / {material}";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static double? ReadDeclaredThicknessInternal(Document document, Element element)
    {
        if (element is Wall wall && document.GetElement(wall.GetTypeId()) is WallType wallType)
        {
            double nativeWidth = wallType.Width;
            if (IsPlausibleWallThickness(nativeWidth)) return nativeWidth;
        }

        Element? type = document.GetElement(element.GetTypeId());
        string[] names =
        [
            "Qto_WallBaseQuantities.Width",
            "Qto_WallBaseQuantities.NominalWidth",
            "OverallWidth", "Overall Width", "Wall Thickness",
            "Thickness", "Dicke"
        ];
        foreach (Element source in new[] { element, type }.Where(item => item is not null).Cast<Element>())
        {
            foreach (string name in names)
            {
                Parameter? parameter = source.LookupParameter(name);
                if (parameter is null || !parameter.HasValue) continue;
                if (parameter.StorageType == StorageType.Double)
                {
                    double value = parameter.AsDouble();
                    if (IsPlausibleWallThickness(value)) return value;
                }
                string display = parameter.AsString() ?? parameter.AsValueString() ?? string.Empty;
                double? parsed = ParseMillimeterThickness(display);
                if (IsPlausibleWallThickness(parsed)) return parsed;
            }
        }

        foreach (string identity in new[]
                 {
                     ReadFirstParameter(element, "IfcName", "IFC Name"),
                     type?.Name ?? string.Empty,
                     element.Name ?? string.Empty
                 })
        {
            double? parsed = ParseTrailingRepeatedThickness(identity);
            if (IsPlausibleWallThickness(parsed)) return parsed;
        }
        return null;
    }

    private static double? ParseMillimeterThickness(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            value.Replace(',', '.'), @"(?<!\d)(\d+(?:\.\d+)?)\s*(?:mm)?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double millimeters))
            return null;
        return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
    }

    private static double? ParseTrailingRepeatedThickness(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            value.Replace(',', '.'), @"(?:/|\s)(\d+(?:\.\d+)?)\s+\1\s*$");
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double millimeters))
            return null;
        return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
    }

    private static bool? ReadExternalFlag(Document document, Element element)
    {
        if (element is Wall wall && document.GetElement(wall.GetTypeId()) is WallType wallType)
            return wallType.Function == WallFunction.Exterior
                ? true
                : wallType.Function == WallFunction.Interior
                    ? false
                    : null;
        foreach (string parameterName in new[] { "IsExternal", "Is External", "Pset_WallCommon.IsExternal" })
        {
            Parameter? parameter = element.LookupParameter(parameterName);
            if (parameter is null || !parameter.HasValue) continue;
            if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger() != 0;
            string value = parameter.AsString() ?? parameter.AsValueString() ?? string.Empty;
            if (bool.TryParse(value, out bool parsed)) return parsed;
            if (string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
            if (string.Equals(value, "No", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
        }
        return null;
    }

    private static string IfcCategory(Element element)
    {
        string entity = ReadFirstParameter(element, "IfcExportAs", "IfcType", "IFC Entity", "Entity");
        return string.IsNullOrWhiteSpace(entity) ? element.Category?.Name ?? "Unknown" : entity;
    }

    private static string ReadFirstParameter(Element element, params string[] names)
    {
        foreach (string name in names)
        {
            Parameter? parameter = element.LookupParameter(name);
            if (parameter is null || !parameter.HasValue) continue;
            string value = parameter.StorageType == StorageType.String
                ? parameter.AsString() ?? string.Empty
                : parameter.AsValueString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static IReadOnlyList<SegmentRow> DeduplicateSharedWallReferences(
        IReadOnlyList<SegmentRow> segments)
    {
        if (segments.Count == 0) return [];

        // One long architectural wall can border several Spaces. Assign that wall
        // to the Space with the greatest detected contact area so the result list
        // and Revit selection contain the linked element exactly once.
        Dictionary<string, long> ownerByReference = segments
            .SelectMany(segment => segment.References.Select(reference => new
            {
                Segment = segment,
                Key = $"{reference.RootLinkInstanceId}|{reference.LinkPath}|{reference.LinkedElementId}"
            }))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(item => item.Segment.SpaceId)
                    .OrderByDescending(spaceGroup => spaceGroup.Sum(item => item.Segment.ExteriorAreaM2))
                    .ThenBy(spaceGroup => spaceGroup.Key)
                    .First()
                    .Key,
                StringComparer.OrdinalIgnoreCase);

        var output = new List<SegmentRow>();
        foreach (SegmentRow segment in segments)
        {
            LinkedWallReference[] ownedReferences = segment.References
                .Where(reference =>
                {
                    string key = $"{reference.RootLinkInstanceId}|{reference.LinkPath}|{reference.LinkedElementId}";
                    return ownerByReference.GetValueOrDefault(key, segment.SpaceId) == segment.SpaceId;
                })
                .DistinctBy(reference => $"{reference.RootLinkInstanceId}|{reference.LinkPath}|{reference.LinkedElementId}")
                .ToArray();
            if (ownedReferences.Length == 0) continue;
            output.Add(segment with { References = ownedReferences });
        }
        return output;
    }

    private static IReadOnlyList<ExteriorWallRow> AggregateRows(IReadOnlyList<SegmentRow> segments)
    {
        return segments
            .GroupBy(item => new
            {
                item.SpaceId,
                item.SpaceNumber,
                item.SpaceName,
                item.AdjacentSpaceId,
                item.AdjacentSpaceNumber,
                item.AdjacentSpaceName,
                item.Level,
                item.LinearCategory,
                item.Orientation,
                item.AssemblyKey
            })
            .Select(group =>
            {
                SegmentRow first = group.First();
                LinkedWallReference[] references = group
                    .SelectMany(item => item.References)
                    .GroupBy(
                        item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(MergeReferenceBounds)
                    .ToArray();
                string status = group.All(item => item.Status == "Ready")
                    ? "Ready"
                    : group.Any(item => item.Status == "Unmapped")
                        ? "Unmapped"
                        : "Review";
                return new ExteriorWallRow
                {
                    SpaceId = first.SpaceId,
                    SpaceNumber = first.SpaceNumber,
                    SpaceName = first.SpaceName,
                    AdjacentSpaceId = first.AdjacentSpaceId,
                    AdjacentSpaceNumber = first.AdjacentSpaceNumber,
                    AdjacentSpaceName = first.AdjacentSpaceName,
                    Level = first.Level,
                    LinearCategory = first.LinearCategory,
                    Orientation = first.Orientation,
                    ThicknessMm = group.Max(item => item.ThicknessMm),
                    LayerCount = group.Max(item => item.LayerCount),
                    LayerDescription = first.LayerDescription,
                    IfcCategory = string.Join(" + ", group.Select(item => item.IfcCategory).Distinct()),
                    IfcGuid = string.Join("; ", group.Select(item => item.IfcGuid).Where(value => value.Length > 0).Distinct()),
                    LinkName = string.Join(" + ", group.Select(item => item.LinkName).Distinct()),
                    ExteriorAreaM2 = group.Sum(item => item.ExteriorAreaM2),
                    WallCount = group.Count(),
                    Status = status,
                    AssemblyKey = first.AssemblyKey,
                    Layers = first.Layers,
                    RecommendedLayers = first.RecommendedLayers,
                    RecommendedLayerDescription = first.RecommendedLayerDescription,
                    References = references
                };
            })
            .OrderBy(item => item.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Orientation, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LinkedWallReference ToReference(LinkCandidate candidate)
    {
        BoundingBoxXYZ bounds = FromCorners(candidate.Corners);
        return ToReference(
            candidate.RootLinkInstanceId,
            candidate.ElementId,
            candidate.LinkPath,
            candidate.DocumentKey,
            candidate.ToHost,
            candidate.NestedLinkInstancePath,
            candidate.TypeName,
            candidate.Depth == 0,
            bounds);
    }

    private static LinkedWallReference ToReference(
        LinkCandidate candidate,
        Curve roomBoundary,
        double sampleZ,
        double roomHeight)
    {
        BoundingBoxXYZ elementBounds = FromCorners(candidate.Corners);
        BoundingBoxXYZ focusBounds = OccurrenceBounds(
            elementBounds,
            roomBoundary,
            sampleZ,
            roomHeight);
        return ToReference(
            candidate.RootLinkInstanceId,
            candidate.ElementId,
            candidate.LinkPath,
            candidate.DocumentKey,
            candidate.ToHost,
            candidate.NestedLinkInstancePath,
            candidate.TypeName,
            candidate.Depth == 0,
            focusBounds);
    }

    private static BoundingBoxXYZ OccurrenceBounds(
        BoundingBoxXYZ elementBounds,
        Curve roomBoundary,
        double sampleZ,
        double roomHeight)
    {
        XYZ[] boundaryPoints = roomBoundary.Tessellate().ToArray();
        double horizontalPadding = UnitUtils.ConvertToInternalUnits(750.0, UnitTypeId.Millimeters);
        double verticalPadding = UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);
        double approximateBottom = sampleZ - Math.Min(
            UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters),
            roomHeight * 0.5);
        double approximateTop = approximateBottom + roomHeight;

        double minX = Math.Max(elementBounds.Min.X, boundaryPoints.Min(point => point.X) - horizontalPadding);
        double minY = Math.Max(elementBounds.Min.Y, boundaryPoints.Min(point => point.Y) - horizontalPadding);
        double minZ = Math.Max(elementBounds.Min.Z, approximateBottom - verticalPadding);
        double maxX = Math.Min(elementBounds.Max.X, boundaryPoints.Max(point => point.X) + horizontalPadding);
        double maxY = Math.Min(elementBounds.Max.Y, boundaryPoints.Max(point => point.Y) + horizontalPadding);
        double maxZ = Math.Min(elementBounds.Max.Z, approximateTop + verticalPadding);

        double minimumExtent = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);
        if (maxX - minX < minimumExtent || maxY - minY < minimumExtent || maxZ - minZ < minimumExtent)
            return elementBounds;
        return new BoundingBoxXYZ
        {
            Min = new XYZ(minX, minY, minZ),
            Max = new XYZ(maxX, maxY, maxZ)
        };
    }

    private static LinkedWallReference MergeReferenceBounds(
        IEnumerable<LinkedWallReference> values)
    {
        LinkedWallReference[] references = values.ToArray();
        LinkedWallReference first = references[0];
        return first with
        {
            MinX = references.Min(item => item.MinX),
            MinY = references.Min(item => item.MinY),
            MinZ = references.Min(item => item.MinZ),
            MaxX = references.Max(item => item.MaxX),
            MaxY = references.Max(item => item.MaxY),
            MaxZ = references.Max(item => item.MaxZ)
        };
    }

    private static LinkedWallReference ToReference(
        ElementId rootLinkId,
        ElementId linkedElementId,
        string linkPath,
        string sourceDocumentPath,
        Transform sourceToHost,
        IReadOnlyList<long> nestedLinkInstancePath,
        string elementName,
        bool direct,
        BoundingBoxXYZ bounds) =>
        new(
            rootLinkId.CompatValue(),
            linkedElementId.CompatValue(),
            linkPath,
            sourceDocumentPath,
            sourceToHost,
            nestedLinkInstancePath,
            elementName,
            direct,
            bounds.Min.X,
            bounds.Min.Y,
            bounds.Min.Z,
            bounds.Max.X,
            bounds.Max.Y,
            bounds.Max.Z);

    private static BoundingBoxXYZ CurveBounds(Curve curve, double sampleZ, double height)
    {
        IReadOnlyList<XYZ> points = curve.Tessellate().ToArray();
        double padding = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(points.Min(item => item.X) - padding, points.Min(item => item.Y) - padding, sampleZ - padding),
            Max = new XYZ(points.Max(item => item.X) + padding, points.Max(item => item.Y) + padding, sampleZ + height + padding)
        };
    }

    private static IReadOnlyList<XYZ> BoundingCorners(BoundingBoxXYZ bounds)
    {
        XYZ min = bounds.Min;
        XYZ max = bounds.Max;
        var local = new[]
        {
            new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z),
            new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
            new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z),
            new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z)
        };
        return local.Select(bounds.Transform.OfPoint).ToArray();
    }

    private static BoundingBoxXYZ FromCorners(IReadOnlyList<XYZ> corners) => new()
    {
        Min = new XYZ(corners.Min(item => item.X), corners.Min(item => item.Y), corners.Min(item => item.Z)),
        Max = new XYZ(corners.Max(item => item.X), corners.Max(item => item.Y), corners.Max(item => item.Z))
    };

    private static (double Min, double Max) Projection(IReadOnlyList<XYZ> points, XYZ direction) =>
        (points.Min(point => point.DotProduct(direction)), points.Max(point => point.DotProduct(direction)));

    private static string Orientation(XYZ normal)
    {
        double angle = Math.Atan2(normal.X, normal.Y) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;
        string[] labels = ["North", "North-East", "East", "South-East", "South", "South-West", "West", "North-West"];
        int index = (int)Math.Round(angle / 45.0, MidpointRounding.AwayFromZero) % 8;
        return labels[index];
    }

    private static string Normalize(string value) =>
        string.Join(" ", (value ?? string.Empty).Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record LayerPart(
        string Name,
        double ThicknessMm,
        bool IsExteriorFace = false,
        string SourceElementId = "",
        string SourceUniqueId = "",
        string SourceIfcGuid = "",
        string SourceLinkPath = "",
        string SourceGeometryKey = "");

    private sealed record AssemblyInfo(
        double TotalThicknessMm,
        int LayerCount,
        string Description,
        string Key,
        IReadOnlyList<ExteriorWallLayerItem> Layers,
        IReadOnlyList<ExteriorWallLayerItem> RecommendedLayers,
        string RecommendedDescription);

    private sealed record AssemblyCandidateSelection(
        IReadOnlyList<LinkCandidate> Layers,
        IReadOnlyList<LinkCandidate> HighlightTargets);

    private sealed record LinkCandidate(
        ElementId RootLinkInstanceId,
        ElementId ElementId,
        string ElementUniqueId,
        string GeometryKey,
        string DocumentKey,
        Transform ToHost,
        IReadOnlyList<long> NestedLinkInstancePath,
        string LinkPath,
        int Depth,
        string Kind,
        string ElementName,
        string TypeName,
        string IfcCategory,
        string IfcGuid,
        bool? MarkedExternal,
        double? DeclaredThicknessInternal,
        IReadOnlyList<LayerPart> LayerParts,
        IReadOnlyList<XYZ> Corners,
        double MinZ,
        double MaxZ)
    {
        internal string UniqueKey => $"{RootLinkInstanceId.CompatValue()}|{DocumentKey}|{ElementId.CompatValue()}|{GeometryKey}";
    }

    private sealed record ImportedShape(BoundingBoxXYZ Bounds, string Identity);

    private sealed record BoundaryEdge(
        long SpaceId,
        string SpaceNumber,
        string SpaceName,
        XYZ A,
        XYZ B);

    private sealed record ExteriorBoundaryCandidate(
        BoundarySegment Segment,
        Curve Curve,
        RevitLinkInstance? RootLink,
        XYZ ExteriorNormal,
        string Orientation,
        long AdjacentSpaceId,
        string AdjacentSpaceNumber,
        string AdjacentSpaceName);

    private sealed record CandidateMatch(
        LinkCandidate Candidate,
        double Distance,
        double Overlap,
        double RelativeMin,
        double RelativeMax,
        double Thickness);

    private sealed record WindowCandidateMatch(
        LinkCandidate Candidate,
        ExteriorBoundaryCandidate Face,
        double WidthInternal,
        double HeightInternal,
        double DepthInternal);

    private sealed record LinkDocumentResolution(Document? Document, bool OpenedByScanner, string Note);

    private sealed record SegmentRow(
        long SpaceId,
        string SpaceNumber,
        string SpaceName,
        long AdjacentSpaceId,
        string AdjacentSpaceNumber,
        string AdjacentSpaceName,
        string Level,
        string LinearCategory,
        string Orientation,
        double ThicknessMm,
        int LayerCount,
        string LayerDescription,
        string IfcCategory,
        string IfcGuid,
        string LinkName,
        double ExteriorAreaM2,
        string Status,
        string AssemblyKey,
        IReadOnlyList<ExteriorWallLayerItem> Layers,
        IReadOnlyList<ExteriorWallLayerItem> RecommendedLayers,
        string RecommendedLayerDescription,
        IReadOnlyList<LinkedWallReference> References);

    private sealed class ScanDiagnostics(int linkedWallCandidateCount)
    {
        internal int LinkedWallCandidateCount { get; } = linkedWallCandidateCount;
        internal int BoundarySegmentCount { get; set; }
        internal int LinkedBoundarySegmentCount { get; set; }
        internal int ExteriorBoundarySegmentCount { get; set; }
    }
}
