using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI.Selection;

namespace FamilyMEP.Plugin.DrainConnection;

internal sealed class DrainSourceFilter : ISelectionFilter
{
    public bool AllowElement(Element element) =>
        DrainSelection.GetOpenRoundConnectors(element).Count > 0;
    public bool AllowReference(Reference reference, XYZ point) => false;
}

internal sealed class HorizontalPipeFilter : ISelectionFilter
{
    public bool AllowElement(Element element) => DrainSelection.IsHorizontalPipe(element);
    public bool AllowReference(Reference reference, XYZ point) => false;
}

internal static class DrainSelection
{
    public static bool IsHorizontalPipe(Element? element)
    {
        if (element is not Pipe pipe || pipe.Location is not LocationCurve location)
            return false;
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        return Math.Sqrt(dx * dx + dy * dy) > DrainGeometry.Mm(25);
    }

    public static Connector ChooseSourceConnector(Element element)
    {
        Connector? connector = GetOpenRoundConnectors(element)
            .OrderByDescending(item => item.CoordinateSystem?.BasisZ.Z < 0 ? 1 : 0)
            .ThenByDescending(item => Math.Abs(item.CoordinateSystem?.BasisZ.Z ?? 0))
            .ThenBy(item => item.Origin.Z)
            .FirstOrDefault();
        if (connector is null)
            throw new InvalidOperationException("The source has no open round piping connector.");
        if (Math.Abs(connector.CoordinateSystem?.BasisZ.Z ?? 0) < 0.5)
            throw new InvalidOperationException("The source connector is not sufficiently vertical.");
        return connector;
    }

    public static IReadOnlyList<Connector> GetOpenRoundConnectors(Element element) =>
        GetConnectors(element)
            .Where(item => !item.IsConnected)
            .ToList();

    public static IReadOnlyList<Connector> GetConnectors(Element element)
    {
        ConnectorManager? manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null
        };
        if (manager is null) return [];

        var connectors = new List<Connector>();
        foreach (Connector connector in manager.Connectors)
        {
            if (connector.Domain != Domain.DomainPiping ||
                connector.Shape != ConnectorProfileType.Round)
                continue;
            bool usable = element is MEPCurve
                ? connector.ConnectorType == ConnectorType.End
                : connector.ConnectorType is ConnectorType.End or ConnectorType.Curve or ConnectorType.Physical;
            if (usable)
                connectors.Add(connector);
        }
        return connectors;
    }

    public static Connector ConnectorNear(Element element, XYZ point, bool requireOpen)
    {
        Connector? best = GetConnectors(element)
            .Where(item => !requireOpen || !item.IsConnected)
            .OrderBy(item => item.Origin.DistanceTo(point))
            .FirstOrDefault();
        if (best is null || best.Origin.DistanceTo(point) > DrainGeometry.Mm(3))
            throw new InvalidOperationException($"No connector found at the expected point on {element.Id.Value}.");
        return best;
    }
}
