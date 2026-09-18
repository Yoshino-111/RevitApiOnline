using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using RevitTransform = Autodesk.Revit.DB.Transform;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal readonly record struct CadModelSegment(
    int PathId,
    string Layer,
    int NativeColorArgb,
    int LineWeight,
    double X1,
    double Y1,
    double X2,
    double Y2);

internal readonly record struct CadGraphicsInfo(string Layer, int ColorArgb, int LineWeight);

internal sealed class CadImportAnalysis
{
    internal ElementId ImportInstanceId { get; init; } = ElementId.InvalidElementId;
    internal ElementId ViewId { get; init; } = ElementId.InvalidElementId;
    internal string ViewName { get; init; } = string.Empty;
    internal string LevelName { get; init; } = string.Empty;
    internal IReadOnlyList<CadModelSegment> ModelSegments { get; init; } = [];
    internal XYZ BottomLeft { get; init; } = XYZ.Zero;
    internal XYZ BottomRight { get; init; } = XYZ.Zero;
    internal XYZ TopLeft { get; init; } = XYZ.Zero;

    internal double Width => BottomLeft.DistanceTo(BottomRight);
    internal double Height => BottomLeft.DistanceTo(TopLeft);

    internal CadSceneBuildResult BuildScene()
    {
        double widthFeet = Math.Max(Width, 1e-6);
        double heightFeet = Math.Max(Height, 1e-6);
        double widthMillimeters = UnitUtils.ConvertFromInternalUnits(widthFeet, UnitTypeId.Millimeters);
        double heightMillimeters = UnitUtils.ConvertFromInternalUnits(heightFeet, UnitTypeId.Millimeters);
        CadModelSegment[] orderedModelSegments = ModelSegments
            .OrderBy(item => item.PathId)
            .ToArray();
        PdfVectorSegment[] segments = orderedModelSegments
            .Select((item, index) => new PdfVectorSegment(
                index,
                item.PathId,
                item.NativeColorArgb,
                Math.Max(1.0f, item.LineWeight),
                (float)((item.X1 - BottomLeft.X) / widthFeet),
                (float)((TopLeft.Y - item.Y1) / heightFeet),
                (float)((item.X2 - BottomLeft.X) / widthFeet),
                (float)((TopLeft.Y - item.Y2) / heightFeet)))
            .ToArray();
        PdfVectorScene scene = PdfVectorScene.FromSegments(
            widthMillimeters,
            heightMillimeters,
            segments);
        IReadOnlyDictionary<int, string> layerByPath = orderedModelSegments
            .GroupBy(item => item.PathId)
            .ToDictionary(group => group.Key, group => group.First().Layer);
        IReadOnlyDictionary<string, int> layerCounts = orderedModelSegments
            .GroupBy(item => item.Layer, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<int, int> nativeColorBySegment = orderedModelSegments
            .Select((item, index) => new { Item = item, Index = index })
            .ToDictionary(item => item.Index, item => item.Item.NativeColorArgb);
        BitmapSource preview = RenderPreview(scene, orderedModelSegments);
        return new CadSceneBuildResult(scene, preview, layerByPath, layerCounts, nativeColorBySegment);
    }

    private static BitmapSource RenderPreview(
        PdfVectorScene scene,
        IReadOnlyList<CadModelSegment> nativeSegments)
    {
        // This bitmap is only the low-zoom context for the vector viewer. Give it
        // enough resolution for Fit/200%, while high zoom is rendered directly
        // from the native CAD StreamGeometry in the WPF overlay.
        const int maximumWidth = 3200;
        const int maximumHeight = 2200;
        double ratio = Math.Max(0.05, scene.PageWidth / Math.Max(scene.PageHeight, 0.001));
        int pixelWidth = ratio >= 1
            ? maximumWidth
            : Math.Max(500, (int)Math.Round(maximumHeight * ratio));
        int pixelHeight = ratio >= 1
            ? Math.Max(500, (int)Math.Round(maximumWidth / ratio))
            : maximumHeight;
        pixelWidth = Math.Min(pixelWidth, maximumWidth);
        pixelHeight = Math.Min(pixelHeight, maximumHeight);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 39, 48)),
                null,
                new System.Windows.Rect(0, 0, pixelWidth, pixelHeight));
            for (int index = 0; index < scene.Segments.Length; index++)
            {
                PdfVectorSegment segment = scene.Segments[index];
                int nativeColor = index < nativeSegments.Count
                    ? nativeSegments[index].NativeColorArgb
                    : segment.ColorArgb;
                byte red = (byte)((nativeColor >> 16) & 255);
                byte green = (byte)((nativeColor >> 8) & 255);
                byte blue = (byte)(nativeColor & 255);
                // Black/near-black ByLayer entities are unreadable on the CAD-dark
                // preview; AutoCAD displays these as a light contrast color too.
                if (red < 42 && green < 42 && blue < 42)
                    red = green = blue = 205;
                var pen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue)), 1.15);
                pen.Thickness = PortableMath.Clamp(nativeSegments[index].LineWeight / 18.0, 0.70, 3.50);
                pen.Freeze();
                drawing.DrawLine(
                    pen,
                    new System.Windows.Point(segment.X1 * pixelWidth, segment.Y1 * pixelHeight),
                    new System.Windows.Point(segment.X2 * pixelWidth, segment.Y2 * pixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed record CadSceneBuildResult(
    PdfVectorScene Scene,
    BitmapSource Preview,
    IReadOnlyDictionary<int, string> LayerByPath,
    IReadOnlyDictionary<string, int> LayerCounts,
    IReadOnlyDictionary<int, int> NativeColorBySegment);

internal static class CadImportExtractor
{
    internal static CadImportAnalysis Extract(Document document, ImportInstance importInstance, View view)
    {
        var segments = new List<CadModelSegment>();
        int nextPathId = 0;
        var options = new Options
        {
            IncludeNonVisibleObjects = false,
            ComputeReferences = false,
            View = view
        };
        GeometryElement geometry = importInstance.get_Geometry(options)
            ?? throw new InvalidOperationException("Revit returned no geometry for the imported CAD instance.");
        CollectGeometry(document, geometry, RevitTransform.Identity, null, false, segments, ref nextPathId);
        segments = segments
            .Where(item => Math.Sqrt(
                Math.Pow(item.X2 - item.X1, 2) +
                Math.Pow(item.Y2 - item.Y1, 2)) > 1e-7)
            .Take(1_000_000)
            .ToList();
        if (segments.Count == 0)
            throw new InvalidOperationException("No visible line, polyline, or arc geometry was found in the CAD import.");
        double minX = segments.Min(item => Math.Min(item.X1, item.X2));
        double maxX = segments.Max(item => Math.Max(item.X1, item.X2));
        double minY = segments.Min(item => Math.Min(item.Y1, item.Y2));
        double maxY = segments.Max(item => Math.Max(item.Y1, item.Y2));
        if (maxX - minX < 1e-6 || maxY - minY < 1e-6)
            throw new InvalidOperationException("The CAD extents are invalid or effectively one-dimensional.");
        return new CadImportAnalysis
        {
            ImportInstanceId = importInstance.Id,
            ViewId = view.Id,
            ViewName = view.Name,
            LevelName = view.GenLevel?.Name ?? string.Empty,
            ModelSegments = segments,
            BottomLeft = new XYZ(minX, minY, 0),
            BottomRight = new XYZ(maxX, minY, 0),
            TopLeft = new XYZ(minX, maxY, 0)
        };
    }

    private static void CollectGeometry(
        Document document,
        GeometryElement geometry,
        RevitTransform transform,
        CadGraphicsInfo? inheritedGraphics,
        bool groupInstance,
        ICollection<CadModelSegment> output,
        ref int nextPathId)
    {
        foreach (GeometryObject geometryObject in geometry)
        {
            CadGraphicsInfo graphics = ResolveGraphics(document, geometryObject, inheritedGraphics);
            if (geometryObject is GeometryInstance instance)
            {
                int? blockPathId = groupInstance ? nextPathId++ : null;
                CollectGeometryObjectSet(
                    document,
                    instance.GetInstanceGeometry(),
                    transform,
                    graphics,
                    blockPathId,
                    output,
                    ref nextPathId);
                groupInstance = true;
                continue;
            }
            int pathId = nextPathId++;
            AddGeometryObject(geometryObject, transform, graphics, pathId, output);
        }
    }

    private static void CollectGeometryObjectSet(
        Document document,
        GeometryElement geometry,
        RevitTransform transform,
        CadGraphicsInfo inheritedGraphics,
        int? sharedPathId,
        ICollection<CadModelSegment> output,
        ref int nextPathId)
    {
        foreach (GeometryObject geometryObject in geometry)
        {
            CadGraphicsInfo graphics = ResolveGraphics(document, geometryObject, inheritedGraphics);
            if (geometryObject is GeometryInstance nested)
            {
                int nestedPathId = nextPathId++;
                CollectGeometryObjectSet(
                    document,
                    nested.GetInstanceGeometry(),
                    transform,
                    graphics,
                    nestedPathId,
                    output,
                    ref nextPathId);
                continue;
            }
            int pathId = sharedPathId ?? nextPathId++;
            AddGeometryObject(geometryObject, transform, graphics, pathId, output);
        }
    }

    private static void AddGeometryObject(
        GeometryObject geometryObject,
        RevitTransform transform,
        CadGraphicsInfo graphics,
        int pathId,
        ICollection<CadModelSegment> output)
    {
        IList<XYZ>? points = geometryObject switch
        {
            Curve curve => curve.Tessellate(),
            PolyLine polyLine => polyLine.GetCoordinates(),
            _ => null
        };
        if (points is null || points.Count < 2) return;
        for (int index = 0; index < points.Count - 1; index++)
        {
            XYZ first = transform.OfPoint(points[index]);
            XYZ second = transform.OfPoint(points[index + 1]);
            output.Add(new CadModelSegment(
                pathId,
                graphics.Layer,
                graphics.ColorArgb,
                graphics.LineWeight,
                first.X,
                first.Y,
                second.X,
                second.Y));
        }
    }

    private static CadGraphicsInfo ResolveGraphics(
        Document document,
        GeometryObject geometryObject,
        CadGraphicsInfo? fallback)
    {
        if (geometryObject.GraphicsStyleId != ElementId.InvalidElementId &&
            document.GetElement(geometryObject.GraphicsStyleId) is GraphicsStyle style &&
            !string.IsNullOrWhiteSpace(style.GraphicsStyleCategory?.Name))
        {
            Category category = style.GraphicsStyleCategory;
            Autodesk.Revit.DB.Color color = category.LineColor;
            int colorArgb = color.IsValid
                ? unchecked((int)(0xFF000000u |
                    (uint)(color.Red << 16) |
                    (uint)(color.Green << 8) |
                    color.Blue))
                : fallback?.ColorArgb ?? unchecked((int)0xFF000000u);
            int lineWeight = fallback?.LineWeight ?? 1;
            try
            {
                int? categoryWeight = category.GetLineWeight(GraphicsStyleType.Projection);
                if (categoryWeight > 0) lineWeight = categoryWeight.Value;
            }
            catch
            {
                // Some imported subcategories do not expose a projection
                // weight. Keep the inherited/default weight in that case.
            }
            return new CadGraphicsInfo(category.Name, colorArgb, lineWeight);
        }
        return fallback ?? new CadGraphicsInfo("0", unchecked((int)0xFF000000u), 1);
    }
}
