using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal enum PdfVectorSelectionScope
{
    Line,
    PdfObject,
    SimilarStyle
}

internal enum PdfVectorClass
{
    None,
    MainPipe,
    BranchPipe,
    Sprinkler
}

internal enum PdfFittingKind
{
    Elbow,
    Tee,
    Cross
}

internal readonly record struct PdfFittingCandidate(
    PdfFittingKind Kind,
    double X,
    double Y,
    double Confidence);

internal readonly struct PdfVectorSegment
{
    internal PdfVectorSegment(
        int id,
        int pathId,
        int colorArgb,
        float strokeWidth,
        float x1,
        float y1,
        float x2,
        float y2)
    {
        Id = id;
        PathId = pathId;
        ColorArgb = colorArgb;
        StrokeWidth = strokeWidth;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    internal int Id { get; }
    internal int PathId { get; }
    internal int ColorArgb { get; }
    internal float StrokeWidth { get; }
    internal float X1 { get; }
    internal float Y1 { get; }
    internal float X2 { get; }
    internal float Y2 { get; }
    internal double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

internal readonly record struct PdfVectorPathMetrics(
    int SegmentCount,
    double Width,
    double Height,
    double CenterX,
    double CenterY,
    bool IsClosed,
    double RadialVariation)
{
    internal double Diagonal => Math.Sqrt(Width * Width + Height * Height);
    internal double AspectRatio => Height <= 0.0000001 ? double.MaxValue : Width / Height;
}

internal sealed class PdfVectorScene
{
    private const int GridColumns = 160;
    private const int GridRows = 120;
    private readonly Dictionary<int, List<int>> _grid = [];
    private readonly int[] _pathStarts;
    private readonly int[] _pathCounts;
    private readonly PdfVectorPathMetrics[] _pathMetrics;
    private readonly bool[] _pathMetricsReady;
    private readonly Dictionary<int, int[]> _shapeSimilarityCache = [];

    private PdfVectorScene(
        int pageCount,
        double pageWidth,
        double pageHeight,
        int pathCount,
        PdfVectorSegment[] segments)
    {
        PageCount = pageCount;
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        Segments = segments;
        _pathStarts = Enumerable.Repeat(-1, Math.Max(pathCount, 0)).ToArray();
        _pathCounts = new int[Math.Max(pathCount, 0)];
        _pathMetrics = new PdfVectorPathMetrics[Math.Max(pathCount, 0)];
        _pathMetricsReady = new bool[Math.Max(pathCount, 0)];
        BuildIndexes();
    }

    internal int PageCount { get; }
    internal double PageWidth { get; }
    internal double PageHeight { get; }
    internal PdfVectorSegment[] Segments { get; }

    internal static PdfVectorScene FromSegments(
        double pageWidth,
        double pageHeight,
        IReadOnlyList<PdfVectorSegment> segments)
    {
        PdfVectorSegment[] ordered = segments
            .OrderBy(item => item.PathId)
            .ThenBy(item => item.Id)
            .Select((item, index) => new PdfVectorSegment(
                index,
                item.PathId,
                item.ColorArgb,
                item.StrokeWidth,
                item.X1,
                item.Y1,
                item.X2,
                item.Y2))
            .ToArray();
        int pathCount = ordered.Length == 0 ? 0 : ordered.Max(item => item.PathId) + 1;
        return new PdfVectorScene(1, pageWidth, pageHeight, pathCount, ordered);
    }

    internal static async Task<PdfVectorScene> ExtractAsync(string pdfPath)
    {
        string renderer = ResolveRendererPath();
        string cachePath = ResolveCachePath(pdfPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        FileInfo sourceInfo = new(pdfPath);
        if (!File.Exists(cachePath) || File.GetLastWriteTimeUtc(cachePath) < sourceInfo.LastWriteTimeUtc)
        {
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            var startInfo = new ProcessStartInfo
            {
                FileName = renderer,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("extract");
            startInfo.ArgumentList.Add(pdfPath);
            startInfo.ArgumentList.Add(temporaryPath);
            try
            {
                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The FamilyMEP PDF vector extractor could not be started.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                string standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                string standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0 || !File.Exists(temporaryPath))
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(standardError)
                            ? $"PDF vector extractor exited with code {process.ExitCode}. {standardOutput}"
                            : standardError.Trim());
                File.Move(temporaryPath, cachePath, true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }

        return await Task.Run(() => Load(cachePath));
    }

    internal IReadOnlyList<int> HitTest(
        double x,
        double y,
        double toleranceX,
        double toleranceY,
        int maximum = 20)
    {
        int minColumn = ClampColumn((int)Math.Floor((x - toleranceX) * GridColumns));
        int maxColumn = ClampColumn((int)Math.Floor((x + toleranceX) * GridColumns));
        int minRow = ClampRow((int)Math.Floor((y - toleranceY) * GridRows));
        int maxRow = ClampRow((int)Math.Floor((y + toleranceY) * GridRows));
        var candidates = new HashSet<int>();
        for (int row = minRow; row <= maxRow; row++)
        for (int column = minColumn; column <= maxColumn; column++)
        {
            if (_grid.TryGetValue(row * GridColumns + column, out List<int>? cell))
                candidates.UnionWith(cell);
        }

        return candidates
            .Select(id => (Id: id, Distance: DistanceToSegment(Segments[id], x, y, toleranceX, toleranceY)))
            .Where(item => item.Distance <= 1.0)
            .OrderBy(item => item.Distance)
            .ThenByDescending(item => Segments[item.Id].Length)
            .Take(maximum)
            .Select(item => item.Id)
            .ToArray();
    }

    internal IReadOnlyList<int> ResolveSelection(int segmentId, PdfVectorSelectionScope scope, int maximum = 12000)
    {
        if (segmentId < 0 || segmentId >= Segments.Length)
            return [];
        PdfVectorSegment selected = Segments[segmentId];
        if (scope == PdfVectorSelectionScope.Line)
            return [segmentId];
        if (scope == PdfVectorSelectionScope.PdfObject)
        {
            int pathId = selected.PathId;
            if (pathId < 0 || pathId >= _pathStarts.Length || _pathStarts[pathId] < 0)
                return [segmentId];
            int count = Math.Min(_pathCounts[pathId], maximum);
            return Enumerable.Range(_pathStarts[pathId], count).ToArray();
        }

        float widthTolerance = Math.Max(0.02f, Math.Abs(selected.StrokeWidth) * 0.08f);
        return Segments
            .Where(segment =>
                segment.ColorArgb == selected.ColorArgb &&
                Math.Abs(segment.StrokeWidth - selected.StrokeWidth) <= widthTolerance)
            .OrderByDescending(segment => segment.Length)
            .Take(maximum)
            .Select(segment => segment.Id)
            .ToArray();
    }

    internal PdfVectorPathMetrics GetPathMetrics(int segmentId)
    {
        if (segmentId < 0 || segmentId >= Segments.Length)
            return default;
        int pathId = Segments[segmentId].PathId;
        if (pathId < 0 || pathId >= _pathStarts.Length || _pathStarts[pathId] < 0)
            return new PdfVectorPathMetrics(1, 0, 0, 0, 0, false, 1);
        if (_pathMetricsReady[pathId])
            return _pathMetrics[pathId];
        int start = _pathStarts[pathId];
        int count = _pathCounts[pathId];
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        PdfVectorSegment first = Segments[start];
        PdfVectorSegment last = Segments[start + count - 1];
        for (int index = start; index < start + count; index++)
        {
            PdfVectorSegment segment = Segments[index];
            minX = Math.Min(minX, Math.Min(segment.X1, segment.X2));
            minY = Math.Min(minY, Math.Min(segment.Y1, segment.Y2));
            maxX = Math.Max(maxX, Math.Max(segment.X1, segment.X2));
            maxY = Math.Max(maxY, Math.Max(segment.Y1, segment.Y2));
        }
        double centerX = (minX + maxX) * 0.5;
        double centerY = (minY + maxY) * 0.5;
        double radiusSum = 0;
        double radiusSquaredSum = 0;
        for (int index = start; index < start + count; index++)
        {
            PdfVectorSegment segment = Segments[index];
            double dx = segment.X2 - centerX;
            double dy = segment.Y2 - centerY;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            radiusSum += radius;
            radiusSquaredSum += radius * radius;
        }
        double meanRadius = radiusSum / Math.Max(1, count);
        double variance = Math.Max(0, radiusSquaredSum / Math.Max(1, count) - meanRadius * meanRadius);
        double radialVariation = Math.Sqrt(variance) / Math.Max(0.000000001, meanRadius);
        double closeGap = Math.Sqrt(
            (first.X1 - last.X2) * (first.X1 - last.X2) +
            (first.Y1 - last.Y2) * (first.Y1 - last.Y2));
        double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        var metrics = new PdfVectorPathMetrics(
            count,
            maxX - minX,
            maxY - minY,
            centerX,
            centerY,
            closeGap <= Math.Max(0.00000002, diagonal * 0.08),
            radialVariation);
        _pathMetrics[pathId] = metrics;
        _pathMetricsReady[pathId] = true;
        return metrics;
    }

    internal IReadOnlyList<int> ResolveShapeSimilar(int segmentId, int maximum = 12000)
    {
        if (segmentId < 0 || segmentId >= Segments.Length)
            return [];
        if (_shapeSimilarityCache.TryGetValue(segmentId, out int[]? cached))
            return cached;

        PdfVectorSegment seedSegment = Segments[segmentId];
        PdfVectorPathMetrics seed = GetPathMetrics(segmentId);
        if (!seed.IsClosed || seed.Width <= 0 || seed.Height <= 0)
            return ResolveSelection(segmentId, PdfVectorSelectionScope.PdfObject, maximum);

        var shapeMatches = new List<int>();
        var connectedMatches = new List<int>();
        double seedDiagonal = seed.Diagonal;
        for (int pathId = 0; pathId < _pathStarts.Length; pathId++)
        {
            int start = _pathStarts[pathId];
            if (start < 0) continue;
            PdfVectorSegment candidateSegment = Segments[start];
            if (candidateSegment.ColorArgb != seedSegment.ColorArgb) continue;
            float strokeTolerance = Math.Max(0.04f, Math.Abs(seedSegment.StrokeWidth) * 0.30f);
            if (Math.Abs(candidateSegment.StrokeWidth - seedSegment.StrokeWidth) > strokeTolerance) continue;
            PdfVectorPathMetrics candidate = GetPathMetrics(start);
            if (!candidate.IsClosed) continue;
            if (Math.Abs(candidate.SegmentCount - seed.SegmentCount) > Math.Max(1, seed.SegmentCount / 3)) continue;
            if (candidate.Diagonal < seedDiagonal * 0.58 || candidate.Diagonal > seedDiagonal * 1.65) continue;
            if (candidate.AspectRatio < seed.AspectRatio * 0.58 || candidate.AspectRatio > seed.AspectRatio * 1.72) continue;
            if (Math.Abs(candidate.RadialVariation - seed.RadialVariation) > 0.24) continue;

            IReadOnlyList<int> pathSegments = GetSegmentsForPath(pathId);
            shapeMatches.AddRange(pathSegments);
            if (HasLongConnector(pathId, candidate, seedSegment.ColorArgb))
                connectedMatches.AddRange(pathSegments);
            if (connectedMatches.Count >= maximum) break;
        }

        List<int> result = connectedMatches.Count >= Math.Max(3, seed.SegmentCount * 3)
            ? connectedMatches
            : shapeMatches;
        int[] resolved = result.Take(maximum).ToArray();
        _shapeSimilarityCache[segmentId] = resolved;
        return resolved;
    }

    private bool HasLongConnector(int pathId, PdfVectorPathMetrics metrics, int colorArgb)
    {
        double toleranceX = Math.Max(0.00022, metrics.Width * 1.8);
        double toleranceY = Math.Max(0.00022, metrics.Height * 1.8);
        int minColumn = ClampColumn((int)Math.Floor((metrics.CenterX - toleranceX) * GridColumns));
        int maxColumn = ClampColumn((int)Math.Floor((metrics.CenterX + toleranceX) * GridColumns));
        int minRow = ClampRow((int)Math.Floor((metrics.CenterY - toleranceY) * GridRows));
        int maxRow = ClampRow((int)Math.Floor((metrics.CenterY + toleranceY) * GridRows));
        var visited = new HashSet<int>();
        for (int row = minRow; row <= maxRow; row++)
        for (int column = minColumn; column <= maxColumn; column++)
        {
            if (!_grid.TryGetValue(row * GridColumns + column, out List<int>? cell)) continue;
            foreach (int id in cell)
            {
                if (!visited.Add(id)) continue;
                PdfVectorSegment segment = Segments[id];
                if (segment.PathId == pathId || segment.ColorArgb != colorArgb ||
                    segment.Length < metrics.Diagonal * 1.7)
                    continue;
                if (DistanceToSegment(
                        segment,
                        metrics.CenterX,
                        metrics.CenterY,
                        toleranceX,
                        toleranceY) <= 1.0)
                    return true;
            }
        }
        return false;
    }

    internal IReadOnlyList<int> GetSegmentsForPath(int pathId, int maximum = 2000)
    {
        if (pathId < 0 || pathId >= _pathStarts.Length || _pathStarts[pathId] < 0)
            return [];
        return Enumerable.Range(_pathStarts[pathId], Math.Min(_pathCounts[pathId], maximum)).ToArray();
    }

    internal IReadOnlyList<PdfFittingCandidate> DetectFittings(
        IReadOnlyList<int> mainSegmentIds,
        IReadOnlyList<int> branchSegmentIds,
        int maximum = 3000)
    {
        const double snapPoints = 3.0;
        var nodes = new Dictionary<(long X, long Y), FittingNode>();
        AddTopologySegments(mainSegmentIds, 1, nodes, snapPoints);
        AddTopologySegments(branchSegmentIds, 2, nodes, snapPoints);
        var selectedClasses = new Dictionary<int, int>();
        foreach (int id in mainSegmentIds)
            if (id >= 0 && id < Segments.Length) selectedClasses[id] = 1;
        foreach (int id in branchSegmentIds)
            if (id >= 0 && id < Segments.Length)
                selectedClasses[id] = selectedClasses.GetValueOrDefault(id) | 2;
        AddInteriorTopology(nodes, selectedClasses, snapPoints);

        var result = new List<PdfFittingCandidate>();
        foreach (FittingNode node in nodes.Values)
        {
            int directionCount = node.DirectionBuckets.Count;
            if (directionCount < 2 || directionCount > 6 || node.MaxLengthPoints < 4.0)
                continue;

            PdfFittingKind? kind = null;
            double confidence = 0;
            if (directionCount >= 4 && node.ClassMask == 3)
            {
                kind = PdfFittingKind.Cross;
                confidence = 0.92;
            }
            else if (directionCount == 3 && node.ClassMask == 3)
            {
                kind = PdfFittingKind.Tee;
                confidence = 0.94;
            }
            else if (directionCount == 2)
            {
                int[] directions = node.DirectionBuckets.ToArray();
                int difference = Math.Abs(directions[0] - directions[1]);
                difference = Math.Min(difference, 24 - difference);
                // 12 buckets is a straight continuation; 3..9 is an actual turn.
                if (difference is >= 3 and <= 9)
                {
                    kind = PdfFittingKind.Elbow;
                    confidence = difference is >= 5 and <= 7 ? 0.95 : 0.86;
                }
            }

            if (kind.HasValue)
            {
                result.Add(new PdfFittingCandidate(
                    kind.Value,
                    node.XSum / node.EndpointCount,
                    node.YSum / node.EndpointCount,
                    confidence));
                if (result.Count >= maximum) break;
            }
        }
        return result;
    }

    private void AddTopologySegments(
        IReadOnlyList<int> segmentIds,
        int classMask,
        Dictionary<(long X, long Y), FittingNode> nodes,
        double snapPoints)
    {
        foreach (int id in segmentIds)
        {
            if (id < 0 || id >= Segments.Length) continue;
            PdfVectorSegment segment = Segments[id];
            double dxPoints = (segment.X2 - segment.X1) * PageWidth;
            double dyPoints = (segment.Y2 - segment.Y1) * PageHeight;
            double lengthPoints = Math.Sqrt(dxPoints * dxPoints + dyPoints * dyPoints);
            if (lengthPoints < 0.75) continue;
            AddTopologyEndpoint(
                segment.X1, segment.Y1, dxPoints, dyPoints,
                lengthPoints, classMask, nodes, snapPoints);
            AddTopologyEndpoint(
                segment.X2, segment.Y2, -dxPoints, -dyPoints,
                lengthPoints, classMask, nodes, snapPoints);
        }
    }

    private void AddTopologyEndpoint(
        double x,
        double y,
        double dxPoints,
        double dyPoints,
        double lengthPoints,
        int classMask,
        Dictionary<(long X, long Y), FittingNode> nodes,
        double snapPoints)
    {
        var key = (
            (long)Math.Round(x * PageWidth / snapPoints),
            (long)Math.Round(y * PageHeight / snapPoints));
        FittingNode? node = null;
        double closest = double.MaxValue;
        for (long offsetY = -1; offsetY <= 1; offsetY++)
        for (long offsetX = -1; offsetX <= 1; offsetX++)
        {
            if (!nodes.TryGetValue((key.Item1 + offsetX, key.Item2 + offsetY), out FittingNode? candidate) ||
                candidate.EndpointCount == 0)
                continue;
            double candidateX = candidate.XSum / candidate.EndpointCount;
            double candidateY = candidate.YSum / candidate.EndpointCount;
            double dx = (candidateX - x) * PageWidth;
            double dy = (candidateY - y) * PageHeight;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= snapPoints && distance < closest)
            {
                closest = distance;
                node = candidate;
            }
        }
        if (node is null)
            nodes[key] = node = new FittingNode();
        node.XSum += x;
        node.YSum += y;
        node.EndpointCount++;
        node.ClassMask |= classMask;
        node.MaxLengthPoints = Math.Max(node.MaxLengthPoints, lengthPoints);
        AddDirection(node, dxPoints, dyPoints);
    }

    private void AddInteriorTopology(
        Dictionary<(long X, long Y), FittingNode> nodes,
        IReadOnlyDictionary<int, int> selectedClasses,
        double snapPoints)
    {
        double toleranceX = snapPoints / Math.Max(1.0, PageWidth);
        double toleranceY = snapPoints / Math.Max(1.0, PageHeight);
        foreach (FittingNode node in nodes.Values)
        {
            double x = node.XSum / node.EndpointCount;
            double y = node.YSum / node.EndpointCount;
            int column = ClampColumn((int)Math.Floor(x * GridColumns));
            int row = ClampRow((int)Math.Floor(y * GridRows));
            var visited = new HashSet<int>();
            for (int adjacentRow = Math.Max(0, row - 1); adjacentRow <= Math.Min(GridRows - 1, row + 1); adjacentRow++)
            for (int adjacentColumn = Math.Max(0, column - 1); adjacentColumn <= Math.Min(GridColumns - 1, column + 1); adjacentColumn++)
            {
                if (!_grid.TryGetValue(adjacentRow * GridColumns + adjacentColumn, out List<int>? cell))
                    continue;
                foreach (int id in cell)
                {
                    if (!visited.Add(id) || !selectedClasses.TryGetValue(id, out int classMask))
                        continue;
                    PdfVectorSegment segment = Segments[id];
                    double dx = segment.X2 - segment.X1;
                    double dy = segment.Y2 - segment.Y1;
                    double lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared < 0.0000000001) continue;
                    double parameter = ((x - segment.X1) * dx + (y - segment.Y1) * dy) / lengthSquared;
                    if (parameter <= 0.04 || parameter >= 0.96 ||
                        DistanceToSegment(segment, x, y, toleranceX, toleranceY) > 1.0)
                        continue;

                    double dxPoints = dx * PageWidth;
                    double dyPoints = dy * PageHeight;
                    double lengthPoints = Math.Sqrt(dxPoints * dxPoints + dyPoints * dyPoints);
                    node.ClassMask |= classMask;
                    node.MaxLengthPoints = Math.Max(node.MaxLengthPoints, lengthPoints);
                    AddDirection(node, dxPoints, dyPoints);
                    AddDirection(node, -dxPoints, -dyPoints);
                }
            }
        }
    }

    private static void AddDirection(FittingNode node, double dxPoints, double dyPoints)
    {
        double angle = Math.Atan2(dyPoints, dxPoints);
        if (angle < 0) angle += Math.PI * 2;
        int bucket = (int)Math.Round(angle / (Math.PI * 2) * 24) % 24;
        node.DirectionBuckets.Add(bucket);
    }

    private sealed class FittingNode
    {
        internal double XSum;
        internal double YSum;
        internal int EndpointCount;
        internal int ClassMask;
        internal double MaxLengthPoints;
        internal HashSet<int> DirectionBuckets { get; } = [];
    }

    internal string Describe(int segmentId, PdfVectorSelectionScope scope)
    {
        PdfVectorSegment segment = Segments[segmentId];
        int count = ResolveSelection(segmentId, scope).Count;
        string color = $"#{segment.ColorArgb & 0xFFFFFF:X6}";
        return $"{ScopeLabel(scope)} • {count:N0} vector line(s) • color {color} • width {segment.StrokeWidth:0.###}";
    }

    internal static string ScopeLabel(PdfVectorSelectionScope scope) => scope switch
    {
        PdfVectorSelectionScope.Line => "Exact line",
        PdfVectorSelectionScope.PdfObject => "PDF object",
        _ => "Similar style"
    };

    private void BuildIndexes()
    {
        foreach (PdfVectorSegment segment in Segments)
        {
            if (segment.PathId >= 0 && segment.PathId < _pathStarts.Length)
            {
                if (_pathStarts[segment.PathId] < 0)
                    _pathStarts[segment.PathId] = segment.Id;
                _pathCounts[segment.PathId]++;
            }

            int minColumn = ClampColumn((int)Math.Floor(Math.Min(segment.X1, segment.X2) * GridColumns));
            int maxColumn = ClampColumn((int)Math.Floor(Math.Max(segment.X1, segment.X2) * GridColumns));
            int minRow = ClampRow((int)Math.Floor(Math.Min(segment.Y1, segment.Y2) * GridRows));
            int maxRow = ClampRow((int)Math.Floor(Math.Max(segment.Y1, segment.Y2) * GridRows));
            for (int row = minRow; row <= maxRow; row++)
            for (int column = minColumn; column <= maxColumn; column++)
            {
                int key = row * GridColumns + column;
                if (!_grid.TryGetValue(key, out List<int>? cell))
                    _grid[key] = cell = [];
                cell.Add(segment.Id);
            }
        }
    }

    internal static PdfVectorScene Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        byte[] magic = reader.ReadBytes(8);
        byte[] expected = [(byte)'F', (byte)'M', (byte)'E', (byte)'P', (byte)'V', (byte)'E', (byte)'C', 1];
        if (!magic.SequenceEqual(expected))
            throw new InvalidDataException("The cached PDF vector scene has an unsupported format.");
        _ = reader.ReadInt32();
        int pageCount = reader.ReadInt32();
        double pageWidth = reader.ReadDouble();
        double pageHeight = reader.ReadDouble();
        int pathCount = reader.ReadInt32();
        int segmentCount = reader.ReadInt32();
        if (segmentCount < 0 || segmentCount > 5_000_000)
            throw new InvalidDataException("The PDF vector scene has an invalid segment count.");
        var segments = new PdfVectorSegment[segmentCount];
        for (int i = 0; i < segmentCount; i++)
        {
            segments[i] = new PdfVectorSegment(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
        }
        return new PdfVectorScene(pageCount, pageWidth, pageHeight, pathCount, segments);
    }

    private static double DistanceToSegment(
        PdfVectorSegment segment,
        double x,
        double y,
        double toleranceX,
        double toleranceY)
    {
        double x1 = (segment.X1 - x) / toleranceX;
        double y1 = (segment.Y1 - y) / toleranceY;
        double x2 = (segment.X2 - x) / toleranceX;
        double y2 = (segment.Y2 - y) / toleranceY;
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 0.00000001)
            return Math.Sqrt(x1 * x1 + y1 * y1);
        double t = Math.Max(0, Math.Min(1, -(x1 * dx + y1 * dy) / lengthSquared));
        double px = x1 + t * dx;
        double py = y1 + t * dy;
        return Math.Sqrt(px * px + py * py);
    }

    private static string ResolveRendererPath()
    {
        string root = Path.GetDirectoryName(typeof(PdfVectorScene).Assembly.Location)
            ?? throw new InvalidOperationException("The FamilyMEP plugin folder is unavailable.");
        string path = Path.Combine(root, "Tools", "PdfRenderer", "FamilyMEP.PdfRenderer.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException("FamilyMEP PDF vector extractor is missing.", path);
        return path;
    }

    private static string ResolveCachePath(string pdfPath)
    {
        FileInfo source = new(pdfPath);
        string fingerprint = $"{source.FullName.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|vector-v2";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..20];
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FamilyMEP",
            "Spinkler",
            "PdfCache");
        return Path.Combine(folder, $"{key}.page1.fmepvec");
    }

    private static int ClampColumn(int column) => Math.Max(0, Math.Min(GridColumns - 1, column));
    private static int ClampRow(int row) => Math.Max(0, Math.Min(GridRows - 1, row));
}

internal sealed class PdfVectorPickSnapshot
{
    public string ClassName { get; set; } = string.Empty;
    public int SegmentId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public bool ExactCadLayer { get; set; }
    public bool SmartFlattened { get; set; }
    public bool ColorAreaScan { get; set; }
    public double ScanLeft { get; set; }
    public double ScanTop { get; set; }
    public double ScanRight { get; set; } = 1;
    public double ScanBottom { get; set; } = 1;
    public double ScanHue { get; set; }
    public double ScanSaturation { get; set; }
    public double ScanValue { get; set; }
    public bool LineStrokeScan { get; set; }
    public double ScanStartX { get; set; }
    public double ScanStartY { get; set; }
    public double ScanEndX { get; set; }
    public double ScanEndY { get; set; }
    public double ScanToleranceMillimeters { get; set; }
    public bool GeometryAnalysis { get; set; }
    public List<int> ExplicitSegmentIds { get; set; } = [];
}

internal sealed class PdfVectorExclusionSnapshot
{
    public string ClassName { get; set; } = string.Empty;
    public List<int> PathIds { get; set; } = [];
    public List<int> SegmentIds { get; set; } = [];
}
