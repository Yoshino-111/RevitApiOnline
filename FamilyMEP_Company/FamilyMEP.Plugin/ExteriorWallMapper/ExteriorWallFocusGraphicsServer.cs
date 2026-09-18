using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace FamilyMEP.Plugin.ExteriorWallMapper;

/// <summary>
/// Draws a transient red overlay for source geometry that lives in an RVT/IFC
/// link. No DirectShape or other model element is created in the Space file.
/// </summary>
internal sealed class ExteriorWallFocusGraphicsServer : IDirectContext3DServer
{
    private const int MaximumTrianglesPerBatch = 18000;
    // Move the transient skin roughly 3 mm outside the source faces. Without
    // this small offset the linked model can win the depth test (z-fighting),
    // making a correctly rendered red surface appear invisible.
    private const double SurfaceOffsetFeet = 0.01;
    private readonly Guid _serverId = Guid.NewGuid();
    private readonly object _sync = new();
    private FocusTriangle[] _triangles = [];
    private long _viewId = -1;
    private Outline? _outline;
    private int _renderCallCount;
    private string _lastRenderError = string.Empty;

    internal int TriangleCount
    {
        get { lock (_sync) return _triangles.Length; }
    }

    internal int RenderCallCount => System.Threading.Volatile.Read(ref _renderCallCount);

    internal string LastRenderError
    {
        get { lock (_sync) return _lastRenderError; }
    }

    internal void SetGeometry(View view, IReadOnlyList<FocusTriangle> triangles)
    {
        FocusTriangle[] snapshot = triangles.ToArray();
        lock (_sync)
        {
            _viewId = view.Id.CompatValue();
            _triangles = snapshot;
            _outline = CreateOutline(snapshot);
            _renderCallCount = 0;
            _lastRenderError = string.Empty;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _viewId = -1;
            _triangles = [];
            _outline = null;
        }
    }

    public bool CanExecute(View dBView)
    {
        lock (_sync) return dBView.Id.CompatValue() == _viewId && _triangles.Length > 0;
    }

    public Outline GetBoundingBox(View dBView)
    {
        lock (_sync)
            return _outline ?? new Outline(XYZ.Zero, new XYZ(0.001, 0.001, 0.001));
    }

    public void RenderScene(View dBView, DisplayStyle displayStyle)
    {
        // Submit in the opaque pass, which Revit invokes for every supported
        // display style. Transparent-pass-only geometry disappeared in some
        // wireframe/linked-model focus views.
        if (DrawContext.IsTransparentPass()) return;
        FocusTriangle[] snapshot;
        lock (_sync)
        {
            if (dBView.Id.CompatValue() != _viewId || _triangles.Length == 0) return;
            snapshot = _triangles;
        }

        System.Threading.Interlocked.Increment(ref _renderCallCount);
        try
        {
            // Do not inherit a transform left in the shared graphics pipeline by
            // another DirectContext3D server. Vertices are already in host coordinates.
            DrawContext.SetWorldTransform(Transform.Identity);
            for (int offset = 0; offset < snapshot.Length; offset += MaximumTrianglesPerBatch)
            {
                int triangleCount = Math.Min(MaximumTrianglesPerBatch, snapshot.Length - offset);
                FlushTriangles(snapshot, offset, triangleCount);
            }
        }
        catch (Exception exception)
        {
            lock (_sync) _lastRenderError = exception.GetType().Name + ": " + exception.Message;
        }
    }

    public bool UseInTransparentPass(View dBView) => false;
    public bool UsesHandles() => false;
    // These identifiers are reserved for Autodesk internal servers. Third-party
    // DirectContext3D servers must return an empty value.
    public string GetApplicationId() => string.Empty;
    public string GetSourceId() => string.Empty;
    public string GetDescription() => "Transient linked-wall focus overlay";
    public string GetVendorId() => "FMEP";
    public string GetName() => "Exterior Wall Mapper linked element focus";
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public Guid GetServerId() => _serverId;

    private static void FlushTriangles(FocusTriangle[] triangles, int offset, int triangleCount)
    {
        int vertexCount = triangleCount * 3;
        // Flat Position rendering is deliberate. Normal-lit effects are not
        // displayed consistently by Revit in Wireframe/Hidden Line/Consistent
        // Colors, while the flat effect remains visible across those styles.
        int vertexBufferSize = VertexPosition.GetSizeInFloats() * vertexCount;
        using var vertexBuffer = new VertexBuffer(vertexBufferSize);
        vertexBuffer.Map(vertexBufferSize);
        VertexStreamPosition vertexStream = vertexBuffer.GetVertexStreamPosition();
        for (int index = 0; index < triangleCount; index++)
        {
            FocusTriangle triangle = triangles[offset + index];
            XYZ offsetVector = triangle.Normal * SurfaceOffsetFeet;
            vertexStream.AddVertex(new VertexPosition(triangle.A + offsetVector));
            vertexStream.AddVertex(new VertexPosition(triangle.B + offsetVector));
            vertexStream.AddVertex(new VertexPosition(triangle.C + offsetVector));
        }
        vertexBuffer.Unmap();

        int indexBufferSize = IndexTriangle.GetSizeInShortInts() * triangleCount;
        using var indexBuffer = new IndexBuffer(indexBufferSize);
        indexBuffer.Map(indexBufferSize);
        IndexStreamTriangle indexStream = indexBuffer.GetIndexStreamTriangle();
        for (int index = 0; index < triangleCount; index++)
        {
            int start = index * 3;
            indexStream.AddTriangle(new IndexTriangle(start, start + 1, start + 2));
        }
        indexBuffer.Unmap();

        using var vertexFormat = new VertexFormat(VertexFormatBits.Position);
        using var effect = new EffectInstance(VertexFormatBits.Position);
        var red = new Autodesk.Revit.DB.Color(245, 24, 32);
        effect.SetColor(red);
        effect.SetTransparency(0.0);
        DrawContext.FlushBuffer(
            vertexBuffer,
            vertexCount,
            indexBuffer,
            triangleCount * 3,
            vertexFormat,
            effect,
            PrimitiveType.TriangleList,
            0,
            triangleCount);
    }

    private static Outline? CreateOutline(IReadOnlyList<FocusTriangle> triangles)
    {
        if (triangles.Count == 0) return null;
        XYZ[] points = triangles.SelectMany(item => new[] { item.A, item.B, item.C }).ToArray();
        double epsilon = 1e-6;
        XYZ min = new(
            points.Min(point => point.X) - epsilon,
            points.Min(point => point.Y) - epsilon,
            points.Min(point => point.Z) - epsilon);
        XYZ max = new(
            points.Max(point => point.X) + epsilon,
            points.Max(point => point.Y) + epsilon,
            points.Max(point => point.Z) + epsilon);
        return new Outline(min, max);
    }
}

internal sealed record FocusTriangle(XYZ A, XYZ B, XYZ C, XYZ Normal);
