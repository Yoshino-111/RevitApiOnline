using Autodesk.Revit.DB;

namespace FamilyMEP.Plugin.SmartTag;

/// <summary>
/// Collects visible architectural geometry without changing any of the three
/// accepted Auto/Left/Right layout policies.
/// </summary>
internal static class SmartTagArchitectureObstacleCollector
{
    private static readonly BuiltInCategory[] ArchitecturalCategories =
    [
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions,
        BuiltInCategory.OST_Stairs,
        BuiltInCategory.OST_Railings,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_PlumbingFixtures
    ];

    public static IReadOnlyList<LayoutObstacle> Collect(
        Document document,
        View view,
        XYZ right,
        XYZ up,
        LayoutRect frame)
    {
        var result = new List<LayoutObstacle>();
        var seen = new HashSet<(long Key, long MinU, long MinV, long MaxU, long MaxV)>();
        var categoryFilter = new ElementMulticategoryFilter(ArchitecturalCategories);

        IEnumerable<Element> hostElements = new FilteredElementCollector(document, view.Id)
            .WherePasses(categoryFilter)
            .WhereElementIsNotElementType();
        foreach (Element element in hostElements)
        {
            AddObstacle(
                element,
                element.get_BoundingBox(view) ?? element.get_BoundingBox(null),
                Transform.Identity,
                element.Id.CompatValue());
        }

        IEnumerable<RevitLinkInstance> visibleLinks = new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();
        foreach (RevitLinkInstance link in visibleLinks)
        {
            Document? linkDocument = link.GetLinkDocument();
            if (linkDocument is null) continue;

            Transform linkTransform = link.GetTotalTransform();
            IEnumerable<Element> linkedElements;
            try
            {
                // Revit 2025 filters linked elements by their visibility in the
                // host view, avoiding walls/furniture from unrelated levels.
                linkedElements = PortableApi.LinkedCollector(document, view.Id, link)
                    .WherePasses(categoryFilter)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch
            {
                // Fallback for view/link combinations that do not support the
                // host-view collector. The frame test below still limits scope.
                linkedElements = new FilteredElementCollector(linkDocument)
                    .WherePasses(categoryFilter)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }

            foreach (Element element in linkedElements)
            {
                BoundingBoxXYZ? box = element.get_BoundingBox(null);
                if (box is null) continue;
                AddObstacle(
                    element,
                    box,
                    linkTransform,
                    CreateLinkedKey(link.Id.CompatValue(), element.Id.CompatValue()));
            }
        }

        return result;

        void AddObstacle(
            Element element,
            BoundingBoxXYZ? box,
            Transform outerTransform,
            long key)
        {
            if (box is null || !IsArchitecturalCategory(element)) return;
            LayoutRect bounds = ProjectBox(box, outerTransform, right, up);
            double minimumThickness = Math.Max(
                Math.Min(frame.Width, frame.Height) * 0.00005,
                0.001);
            if (bounds.Width < minimumThickness)
            {
                double center = (bounds.MinU + bounds.MaxU) * 0.5;
                bounds = bounds with
                {
                    MinU = center - minimumThickness * 0.5,
                    MaxU = center + minimumThickness * 0.5
                };
            }
            if (bounds.Height < minimumThickness)
            {
                double center = (bounds.MinV + bounds.MaxV) * 0.5;
                bounds = bounds with
                {
                    MinV = center - minimumThickness * 0.5,
                    MaxV = center + minimumThickness * 0.5
                };
            }
            if (!bounds.Intersects(frame, 0.0)) return;

            double resolution = Math.Max(minimumThickness, 0.001);
            var identity = (
                key,
                (long)Math.Round(bounds.MinU / resolution),
                (long)Math.Round(bounds.MinV / resolution),
                (long)Math.Round(bounds.MaxU / resolution),
                (long)Math.Round(bounds.MaxV / resolution));
            if (seen.Add(identity))
            {
                result.Add(new LayoutObstacle(
                    key,
                    bounds,
                    LayoutObstacleKind.Architecture));
            }
        }
    }

    private static bool IsArchitecturalCategory(Element element)
    {
        long categoryId = element.Category?.Id.CompatValue() ?? long.MaxValue;
        return ArchitecturalCategories.Any(category => (long)category == categoryId);
    }

    private static LayoutRect ProjectBox(
        BoundingBoxXYZ box,
        Transform outerTransform,
        XYZ right,
        XYZ up)
    {
        var points = new List<XYZ>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
        {
            XYZ localPoint = box.Transform.OfPoint(new XYZ(x, y, z));
            points.Add(outerTransform.OfPoint(localPoint));
        }
        return new LayoutRect(
            points.Min(point => point.DotProduct(right)),
            points.Min(point => point.DotProduct(up)),
            points.Max(point => point.DotProduct(right)),
            points.Max(point => point.DotProduct(up)));
    }

    private static long CreateLinkedKey(long linkId, long elementId)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            hash = (hash ^ linkId) * 1099511628211L;
            hash = (hash ^ elementId) * 1099511628211L;
            // Host element ids are positive. A negative key can therefore never
            // be mistaken for the MEP element owning the tag.
            if (hash == long.MinValue) return long.MinValue + 1;
            return -Math.Abs(hash == 0 ? 1 : hash);
        }
    }
}
