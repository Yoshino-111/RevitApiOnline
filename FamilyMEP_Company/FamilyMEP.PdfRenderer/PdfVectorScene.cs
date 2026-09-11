using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

internal readonly record struct PdfPoint(double X, double Y);

internal sealed class PdfVectorScene
{
    public int Version { get; init; } = 1;
    public int PageIndex { get; init; }
    public int PageCount { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public int PageRotation { get; init; }
    public List<PdfVectorPath> Paths { get; init; } = [];
    public List<PdfVectorSegment> Segments { get; init; } = [];
}

internal sealed class PdfVectorPath
{
    public int Id { get; init; }
    public string ObjectKey { get; init; } = string.Empty;
    public string StrokeColor { get; init; } = "#000000";
    public byte Alpha { get; init; } = 255;
    public double StrokeWidth { get; init; }
    public List<int> SegmentIds { get; init; } = [];
}

internal sealed class PdfVectorSegment
{
    public int Id { get; init; }
    public int PathId { get; init; }
    public string ObjectKey { get; init; } = string.Empty;
    public string StrokeColor { get; init; } = "#000000";
    public byte Alpha { get; init; } = 255;
    public double StrokeWidth { get; init; }
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
}

internal static class PdfVectorExtractor
{
    private const int BezierSteps = 10;

    internal static PdfVectorScene ExtractFirstPage(string pdfPath)
    {
        PdfiumNative.FPDF_InitLibrary();
        try
        {
            IntPtr document = PdfiumNative.FPDF_LoadDocument(pdfPath, null);
            if (document == IntPtr.Zero)
                throw new InvalidDataException("PDFium could not load the PDF document.");

            try
            {
                int pageCount = PdfiumNative.FPDF_GetPageCount(document);
                if (pageCount <= 0)
                    throw new InvalidDataException("The PDF does not contain any pages.");

                IntPtr page = PdfiumNative.FPDF_LoadPage(document, 0);
                if (page == IntPtr.Zero)
                    throw new InvalidDataException("PDFium could not load page 1.");

                try
                {
                    var scene = new PdfVectorScene
                    {
                        PageCount = pageCount,
                        PageWidth = PdfiumNative.FPDF_GetPageWidthF(page),
                        PageHeight = PdfiumNative.FPDF_GetPageHeightF(page),
                        PageRotation = PdfiumNative.FPDFPage_GetRotation(page)
                    };
                    int count = PdfiumNative.FPDFPage_CountObjects(page);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr pageObject = PdfiumNative.FPDFPage_GetObject(page, i);
                        ExtractObject(scene, pageObject, PdfiumNative.Matrix.Identity, i.ToString());
                    }
                    return scene;
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(document);
            }
        }
        finally
        {
            PdfiumNative.FPDF_DestroyLibrary();
        }
    }

    internal static async Task WriteAsync(PdfVectorScene scene, string outputPath)
    {
        string? folder = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);
        await using FileStream stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, scene, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    internal static void WriteBinary(PdfVectorScene scene, string outputPath)
    {
        string? folder = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);
        using FileStream stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { (byte)'F', (byte)'M', (byte)'E', (byte)'P', (byte)'V', (byte)'E', (byte)'C', 1 });
        writer.Write(scene.PageIndex);
        writer.Write(scene.PageCount);
        writer.Write(scene.PageWidth);
        writer.Write(scene.PageHeight);
        writer.Write(scene.Paths.Count);
        writer.Write(scene.Segments.Count);
        foreach (PdfVectorSegment segment in scene.Segments)
        {
            writer.Write(segment.Id);
            writer.Write(segment.PathId);
            writer.Write(ParseArgb(segment.StrokeColor, segment.Alpha));
            writer.Write((float)segment.StrokeWidth);
            writer.Write((float)segment.X1);
            writer.Write((float)segment.Y1);
            writer.Write((float)segment.X2);
            writer.Write((float)segment.Y2);
        }
    }

    private static int ParseArgb(string color, byte alpha)
    {
        int rgb = color.Length == 7
            ? int.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;
        return unchecked((int)((uint)alpha << 24) | rgb);
    }

    private static void ExtractObject(
        PdfVectorScene scene,
        IntPtr pageObject,
        PdfiumNative.Matrix parentMatrix,
        string objectKey)
    {
        if (pageObject == IntPtr.Zero)
            return;

        PdfiumNative.Matrix localMatrix = PdfiumNative.Matrix.Identity;
        PdfiumNative.FPDFPageObj_GetMatrix(pageObject, out localMatrix);
        PdfiumNative.Matrix worldMatrix = PdfiumNative.Matrix.Compose(parentMatrix, localMatrix);
        int type = PdfiumNative.FPDFPageObj_GetType(pageObject);

        if (type == PdfiumNative.ObjectPath)
        {
            ExtractPath(scene, pageObject, worldMatrix, objectKey);
            return;
        }

        if (type != PdfiumNative.ObjectForm)
            return;

        int childCount = PdfiumNative.FPDFFormObj_CountObjects(pageObject);
        for (int i = 0; i < childCount; i++)
        {
            IntPtr child = PdfiumNative.FPDFFormObj_GetObject(pageObject, (nuint)i);
            ExtractObject(scene, child, worldMatrix, $"{objectKey}.{i}");
        }
    }

    private static void ExtractPath(
        PdfVectorScene scene,
        IntPtr pathObject,
        PdfiumNative.Matrix matrix,
        string objectKey)
    {
        uint red = 0, green = 0, blue = 0, alpha = 255;
        float strokeWidth = 0;
        PdfiumNative.FPDFPageObj_GetStrokeColor(pathObject, out red, out green, out blue, out alpha);
        PdfiumNative.FPDFPageObj_GetStrokeWidth(pathObject, out strokeWidth);
        string color = $"#{Math.Min(red, 255):X2}{Math.Min(green, 255):X2}{Math.Min(blue, 255):X2}";

        var path = new PdfVectorPath
        {
            Id = scene.Paths.Count,
            ObjectKey = objectKey,
            StrokeColor = color,
            Alpha = (byte)Math.Min(alpha, 255),
            StrokeWidth = strokeWidth
        };

        PdfPoint? current = null;
        PdfPoint? subpathStart = null;
        int count = PdfiumNative.FPDFPath_CountSegments(pathObject);
        for (int i = 0; i < count; i++)
        {
            IntPtr segment = PdfiumNative.FPDFPath_GetPathSegment(pathObject, i);
            if (segment == IntPtr.Zero ||
                PdfiumNative.FPDFPathSegment_GetPoint(segment, out float rawX, out float rawY) == 0)
                continue;

            PdfPoint point = matrix.Transform(rawX, rawY);
            int segmentType = PdfiumNative.FPDFPathSegment_GetType(segment);
            if (segmentType == PdfiumNative.SegmentMoveTo || current is null)
            {
                current = point;
                subpathStart = point;
            }
            else if (segmentType == PdfiumNative.SegmentLineTo)
            {
                AddSegment(scene, path, current.Value, point);
                current = point;
            }
            else if (segmentType == PdfiumNative.SegmentBezierTo && i + 2 < count)
            {
                IntPtr control2Segment = PdfiumNative.FPDFPath_GetPathSegment(pathObject, i + 1);
                IntPtr endSegment = PdfiumNative.FPDFPath_GetPathSegment(pathObject, i + 2);
                if (control2Segment != IntPtr.Zero && endSegment != IntPtr.Zero &&
                    PdfiumNative.FPDFPathSegment_GetType(control2Segment) == PdfiumNative.SegmentBezierTo &&
                    PdfiumNative.FPDFPathSegment_GetType(endSegment) == PdfiumNative.SegmentBezierTo &&
                    PdfiumNative.FPDFPathSegment_GetPoint(control2Segment, out float c2x, out float c2y) != 0 &&
                    PdfiumNative.FPDFPathSegment_GetPoint(endSegment, out float ex, out float ey) != 0)
                {
                    PdfPoint start = current.Value;
                    PdfPoint control1 = point;
                    PdfPoint control2 = matrix.Transform(c2x, c2y);
                    PdfPoint end = matrix.Transform(ex, ey);
                    PdfPoint last = start;
                    for (int step = 1; step <= BezierSteps; step++)
                    {
                        double t = step / (double)BezierSteps;
                        PdfPoint next = Cubic(start, control1, control2, end, t);
                        AddSegment(scene, path, last, next);
                        last = next;
                    }
                    current = end;
                    i += 2;
                    segment = endSegment;
                }
            }

            if (PdfiumNative.FPDFPathSegment_GetClose(segment) != 0 &&
                current is not null && subpathStart is not null)
            {
                AddSegment(scene, path, current.Value, subpathStart.Value);
                current = subpathStart;
            }
        }

        if (path.SegmentIds.Count > 0)
            scene.Paths.Add(path);
    }

    private static void AddSegment(
        PdfVectorScene scene,
        PdfVectorPath path,
        PdfPoint start,
        PdfPoint end)
    {
        if (scene.PageWidth <= 0 || scene.PageHeight <= 0)
            return;
        PdfPoint normalizedStart = Normalize(scene, start);
        PdfPoint normalizedEnd = Normalize(scene, end);
        double x1 = normalizedStart.X;
        double y1 = normalizedStart.Y;
        double x2 = normalizedEnd.X;
        double y2 = normalizedEnd.Y;
        if (!double.IsFinite(x1 + y1 + x2 + y2) ||
            Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 0.0000001)
            return;

        var segment = new PdfVectorSegment
        {
            Id = scene.Segments.Count,
            PathId = path.Id,
            ObjectKey = path.ObjectKey,
            StrokeColor = path.StrokeColor,
            Alpha = path.Alpha,
            StrokeWidth = path.StrokeWidth,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2
        };
        scene.Segments.Add(segment);
        path.SegmentIds.Add(segment.Id);
    }

    private static PdfPoint Normalize(PdfVectorScene scene, PdfPoint point) => scene.PageRotation switch
    {
        1 => new PdfPoint(point.Y / scene.PageWidth, point.X / scene.PageHeight),
        2 => new PdfPoint(1 - point.X / scene.PageWidth, point.Y / scene.PageHeight),
        3 => new PdfPoint(1 - point.Y / scene.PageWidth, 1 - point.X / scene.PageHeight),
        _ => new PdfPoint(point.X / scene.PageWidth, 1 - point.Y / scene.PageHeight)
    };

    private static PdfPoint Cubic(
        PdfPoint start,
        PdfPoint control1,
        PdfPoint control2,
        PdfPoint end,
        double t)
    {
        double u = 1 - t;
        double uu = u * u;
        double tt = t * t;
        return new PdfPoint(
            uu * u * start.X + 3 * uu * t * control1.X + 3 * u * tt * control2.X + tt * t * end.X,
            uu * u * start.Y + 3 * uu * t * control1.Y + 3 * u * tt * control2.Y + tt * t * end.Y);
    }
}
