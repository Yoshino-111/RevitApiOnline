using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace FamilyMEP.Plugin.SmartTag;

internal sealed record SmartTagRecord(
    long TagKey,
    long ExistingTagId,
    long ExistingTagTypeId,
    long ElementId,
    bool WillCreate,
    string CategoryName,
    XYZ AnchorWorld,
    XYZ HeadWorld,
    LayoutTagInput Layout);

internal sealed record SmartTagTypeInfo(long Id, string Name);

internal sealed record SmartTagCategoryInfo(
    string Key,
    string Name,
    int ElementCount,
    int ExistingTagCount,
    IReadOnlyList<SmartTagTypeInfo> TagTypes,
    long DefaultTagTypeId);

internal sealed record SmartTagViewSnapshot(
    long ViewId,
    string ViewName,
    int ViewScale,
    XYZ RightDirection,
    XYZ UpDirection,
    LayoutRect Frame,
    int PixelWidth,
    int PixelHeight,
    byte[] ImageBytes,
    IReadOnlyList<SmartTagRecord> Tags,
    IReadOnlyList<SmartTagCategoryInfo> Categories,
    IReadOnlyList<LayoutObstacle> Obstacles);

internal sealed record SmartTagApplyResult(
    int Created,
    int Arranged,
    int Skipped,
    IReadOnlyList<string> Warnings,
    int ActualClashes,
    int TextOverlaps,
    int LeaderCrossings,
    int ModelOverlaps)
{
    public int Applied => Created + Arranged;
    public IReadOnlyList<TagLayoutPlacement> FinalPlacements { get; init; } = [];
}

internal sealed record SmartTagActualPreviewResult(
    byte[] ImageBytes,
    SmartTagApplyResult LayoutResult);

internal sealed record SmartTagManualAlignResult(
    int Aligned,
    int Skipped,
    IReadOnlyList<string> Warnings,
    byte[] ImageBytes);

internal static class SmartTagRevitService
{
    private static readonly BuiltInCategory[] SupportedMepCategories =
    [
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_Sprinklers
    ];

    private static readonly BuiltInCategory[] SupportedTagCategories =
    [
        BuiltInCategory.OST_DuctTags,
        BuiltInCategory.OST_DuctAccessoryTags,
        BuiltInCategory.OST_PipeTags,
        BuiltInCategory.OST_PipeFittingTags,
        BuiltInCategory.OST_PipeAccessoryTags,
        BuiltInCategory.OST_MechanicalEquipmentTags,
        BuiltInCategory.OST_DuctTerminalTags,
        BuiltInCategory.OST_SprinklerTags
    ];

    private static readonly BuiltInCategory[] MepObstacleCategories =
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
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_PlumbingFixtures
    ];

    private sealed record AppliedTagWorkItem(
        IndependentTag Tag,
        Reference Reference,
        SmartTagRecord Record,
        TagLayoutPlacement Placement,
        int LayoutGroupId = -1);

    private sealed record LeaderLaneRoute(
        IndependentTag Tag,
        Reference Reference,
        LayoutPoint Head,
        LayoutPoint End,
        double HostWidth,
        bool IsFixed);

    public static SmartTagViewSnapshot CaptureActiveView(
        UIApplication application,
        IReadOnlySet<long>? elementFilter = null,
        bool fastGuidedCapture = false)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        Document document = uidoc.Document;
        View view = document.ActiveView;
        if (view.IsTemplate || view is ViewSheet or ViewSchedule)
        {
            throw new InvalidOperationException(
                "Smart Tag requires an active plan, reflected ceiling plan, section, elevation, or drafting view.");
        }

        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == view.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        IList<XYZ> zoomCorners = uiView.GetZoomCorners();
        if (zoomCorners.Count < 2)
        {
            throw new InvalidOperationException("Revit did not return the active viewport bounds.");
        }

        XYZ right = view.RightDirection.Normalize();
        XYZ up = view.UpDirection.Normalize();
        var frame = new LayoutRect(
            zoomCorners.Min(point => point.DotProduct(right)),
            zoomCorners.Min(point => point.DotProduct(up)),
            zoomCorners.Max(point => point.DotProduct(right)),
            zoomCorners.Max(point => point.DotProduct(up)));
        var window = uiView.GetWindowRectangle();
        int pixelWidth = Math.Max(1, window.Right - window.Left);
        int pixelHeight = Math.Max(1, window.Bottom - window.Top);
        byte[] image = CaptureViewport(
            application.MainWindowHandle,
            window.Left,
            window.Top,
            pixelWidth,
            pixelHeight);

        List<SmartTagRecord> tags = CollectTagCandidates(
            document,
            view,
            right,
            up,
            frame,
            elementFilter);
        List<SmartTagCategoryInfo> categories = BuildCategoryInfos(document, tags);
        Dictionary<long, LayoutRect> knownMepBounds = tags
            .GroupBy(item => item.ElementId)
            .ToDictionary(group => group.Key, group => group.First().Layout.ElementBounds);
        List<LayoutObstacle> obstacles = CollectObstacles(
            document, view, right, up, frame,
            includeArchitecture: !fastGuidedCapture,
            knownMepBounds: knownMepBounds);
        return new SmartTagViewSnapshot(
            view.Id.Value,
            view.Name,
            Math.Max(1, view.Scale),
            right,
            up,
            frame,
            pixelWidth,
            pixelHeight,
            image,
            tags,
            categories,
            obstacles);
    }

    public static SmartTagViewSnapshot? PickElementsAndCapture(
        UIApplication application,
        IReadOnlySet<string> selectedCategoryKeys,
        bool fastGuidedCapture = false)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        if (selectedCategoryKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "Select at least one category before scanning elements.");
        }
        try
        {
            IList<Element> picked = uidoc.Selection.PickElementsByRectangle(
                new SupportedMepSelectionFilter(selectedCategoryKeys),
                "Smart Tag: drag a rectangle. Only checked categories are selectable. Press Esc to cancel.");
            HashSet<long> elementIds = picked
                .Where(element => Classify(element) is SupportedCategory category &&
                                  selectedCategoryKeys.Contains(category.Key))
                .Select(element => element.Id.Value)
                .ToHashSet();
            if (elementIds.Count == 0)
            {
                return null;
            }
            return CaptureActiveView(application, elementIds, fastGuidedCapture);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }

    public static LayoutRect? PickGuidedTagZone(UIApplication application)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        View view = uidoc.Document.ActiveView;
        if (view.IsTemplate || view is ViewSheet or ViewSchedule)
        {
            throw new InvalidOperationException(
                "Guided Zones requires an active model view, not a sheet or schedule.");
        }

        try
        {
            PickedBox picked = uidoc.Selection.PickBox(
                PickBoxStyle.Enclosing,
                "Smart Tag: drag the EMPTY area where tag text must stay. Press Esc to cancel.");
            XYZ right = view.RightDirection.Normalize();
            XYZ up = view.UpDirection.Normalize();
            double firstU = picked.Min.DotProduct(right);
            double firstV = picked.Min.DotProduct(up);
            double secondU = picked.Max.DotProduct(right);
            double secondV = picked.Max.DotProduct(up);
            var zone = new LayoutRect(
                Math.Min(firstU, secondU),
                Math.Min(firstV, secondV),
                Math.Max(firstU, secondU),
                Math.Max(firstV, secondV));
            return zone.Width > 1e-6 && zone.Height > 1e-6 ? zone : null;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }

    public static LayoutRect? PickDuctDensitySampleZone(UIApplication application)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        View view = uidoc.Document.ActiveView;
        if (view.IsTemplate || view is ViewSheet or ViewSchedule)
        {
            throw new InvalidOperationException(
                "Duct density requires an active model view, not a sheet or schedule.");
        }

        try
        {
            PickedBox picked = uidoc.Selection.PickBox(
                PickBoxStyle.Directional,
                $"Smart Tag Duct: drag one SAMPLE area. Its size repeats across the Active View with maximum {SmartTagDuctDensity.MaximumTagsPerZone} Duct tags per area. Press Esc to cancel.");
            XYZ right = view.RightDirection.Normalize();
            XYZ up = view.UpDirection.Normalize();
            double firstU = picked.Min.DotProduct(right);
            double firstV = picked.Min.DotProduct(up);
            double secondU = picked.Max.DotProduct(right);
            double secondV = picked.Max.DotProduct(up);
            var zone = new LayoutRect(
                Math.Min(firstU, secondU),
                Math.Min(firstV, secondV),
                Math.Max(firstU, secondU),
                Math.Max(firstV, secondV));
            return zone.Width > 1e-6 && zone.Height > 1e-6 ? zone : null;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }

    public static SmartTagManualAlignResult? PickAndAlignExistingTags(
        UIApplication application,
        bool alignLeftEdge,
        bool arrangeStack = false,
        double rowGapPaperMillimeters = 1.0)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        Document document = uidoc.Document;
        View view = document.ActiveView;
        XYZ right = view.RightDirection.Normalize();
        XYZ up = view.UpDirection.Normalize();
        Reference referencePick;
        IList<Reference> picked = [];
        LayoutRect? scanZone = null;
        try
        {
            referencePick = uidoc.Selection.PickObject(
                ObjectType.Element,
                arrangeStack
                    ? "Smart Tag step 1/2: click the TOP REFERENCE tag. It stays fixed while target tags are stacked below it."
                    : alignLeftEdge
                    ? "Smart Tag step 1/2: click the REFERENCE tag TEXT (press Tab if needed) for LEFT-edge alignment."
                    : "Smart Tag step 1/2: click the REFERENCE tag TEXT (press Tab if needed) for RIGHT-edge alignment.");
            if (arrangeStack)
            {
                picked = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new ExistingTagSelectionFilter(),
                    "Smart Tag step 2/2: select TARGET tags, then click Finish to stack and untangle their leaders.");
            }
            else
            {
                PickedBox targetPick = uidoc.Selection.PickBox(
                    PickBoxStyle.Crossing,
                    alignLeftEdge
                        ? "Smart Tag step 2/2: drag a rectangle across target TEXT or LEADER lines to align LEFT edges."
                        : "Smart Tag step 2/2: drag a rectangle across target TEXT or LEADER lines to align RIGHT edges.");
                scanZone = ProjectPickedBox(targetPick, right, up);
            }
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }

        IndependentTag referenceTag = document.GetElement(referencePick) as IndependentTag
            ?? throw new InvalidOperationException(
                "The picked reference is not a tag. Run Align again, hover the tag text/leader, press Tab until the tag highlights, then click.");
        var warnings = new List<string>();
        List<IndependentTag> selectedTags;
        if (arrangeStack)
        {
            selectedTags = picked
                .Select(document.GetElement)
                .OfType<IndependentTag>()
                // Revit allows tags owned by a primary view to remain selectable in
                // a dependent view. OwnerViewId therefore must not be compared with
                // the active view here. An orphaned tag still has valid visible text
                // bounds and can also be used for manual edge alignment.
                .Where(tag => tag.Id != referenceTag.Id)
                .GroupBy(tag => tag.Id.Value)
                .Select(group => group.First())
                .ToList();
        }
        else
        {
            (selectedTags, _) = CollectTagsTouchedByRectangle(
                document,
                view,
                scanZone!.Value,
                right,
                up,
                warnings,
                referenceTag.Id.Value);
        }
        if (selectedTags.Count == 0)
        {
            throw new InvalidOperationException(
                arrangeStack
                    ? "No target tags were selected. Select tag text/leader objects in step 2, then click Finish."
                    : "The scan rectangle did not touch any target tag text or leader line.");
        }

        var measuredBounds = new Dictionary<long, LayoutRect>();
        var measurableTagIds = new HashSet<long>();
        List<IndependentTag> measurementTags = selectedTags
            .Prepend(referenceTag)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToList();

        // A tag bounding box includes its leader. Hide leaders only inside a
        // measurement transaction and roll that transaction back. The real
        // leader type, endpoint, elbow and host attachment are therefore never
        // edited by the measuring pass.
        using (var measurement = new Transaction(
                   document,
                   "FamilyMEP - Measure Selected Tag Text Edges"))
        {
            measurement.Start();
            foreach (IndependentTag tag in measurementTags)
            {
                try
                {
                    // Pinned state and leader visibility are restored by the
                    // rollback. This lets a pinned reference tag define the
                    // exact real text edge without changing the project.
                    if (tag.Pinned) tag.Pinned = false;
                    if (tag.HasLeader)
                    {
                        tag.HasLeader = false;
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {tag.Id.Value} text measurement: {FriendlyTagError(exception)}");
                }
                // Even when Revit refuses to toggle an orphaned/multi-leader
                // tag, its visible bounding box is still usable. Include it so
                // Finish cannot silently produce an empty target set.
                measurableTagIds.Add(tag.Id.Value);
            }
            document.Regenerate();
            foreach (IndependentTag tag in measurementTags)
            {
                if (!measurableTagIds.Contains(tag.Id.Value)) continue;
                try
                {
                    BoundingBoxXYZ? box = tag.get_BoundingBox(view);
                    if (box is null)
                    {
                        warnings.Add($"Tag {tag.Id.Value} has no visible text bounds in this view.");
                        continue;
                    }
                    measuredBounds[tag.Id.Value] = ProjectBox(box, right, up);
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {tag.Id.Value} text bounds: {FriendlyTagError(exception)}");
                }
            }
            measurement.RollBack();
        }

        Dictionary<long, LayoutRect> safeManualBounds;
        if (arrangeStack)
        {
            // Keep the fixed sample exactly as drawn, but measure every target
            // in its final Horizontal orientation before STACK places rows.
            safeManualBounds = MeasureTagTextBounds(
                document,
                view,
                [referenceTag],
                right,
                up,
                warnings,
                "FamilyMEP - Measure Stack Reference");
            Dictionary<long, LayoutRect> horizontalTargetBounds = MeasureTagTextBounds(
                document,
                view,
                selectedTags,
                right,
                up,
                warnings,
                "FamilyMEP - Measure Horizontal Stack Targets",
                forceHorizontal: true);
            foreach ((long key, LayoutRect value) in horizontalTargetBounds)
                safeManualBounds[key] = value;
        }
        else
        {
            // LEFT/RIGHT require the same exact leaderless body measurement,
            // but retain each tag's current orientation and vertical row.
            safeManualBounds = MeasureTagTextBounds(
                document,
                view,
                measurementTags,
                right,
                up,
                warnings,
                "FamilyMEP - Measure Manual Tag Bodies");
        }
        NormalizeAutoBodyBounds(
            measurementTags,
            safeManualBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: true);
        foreach ((long key, LayoutRect value) in safeManualBounds)
            measuredBounds[key] = value;

        if (!measuredBounds.TryGetValue(referenceTag.Id.Value, out LayoutRect referenceBounds))
        {
            throw new InvalidOperationException(
                "The reference tag text bounds could not be measured in the active view.");
        }
        Dictionary<long, LayoutRect> targetBounds = measuredBounds
            .Where(item => item.Key != referenceTag.Id.Value)
            .ToDictionary(item => item.Key, item => item.Value);
        LayoutPoint referenceInsertion = Project(referenceTag.TagHeadPosition, right, up);
        LayoutPoint referenceAnchor = TryGetLeaderOrHostAnchor(
            referenceTag,
            document,
            view,
            right,
            up) ?? new LayoutPoint(
                (referenceBounds.MinU + referenceBounds.MaxU) * 0.5,
                (referenceBounds.MinV + referenceBounds.MaxV) * 0.5);
        double referenceEdge = alignLeftEdge
            ? SmartTagStackRouting.GetVisibleLeftEdge(
                referenceBounds,
                referenceInsertion,
                referenceAnchor,
                referenceTag.HasLeader)
            : SmartTagStackRouting.GetVisibleRightEdge(
                referenceBounds,
                referenceInsertion,
                referenceAnchor,
                referenceTag.HasLeader);
        if (arrangeStack)
        {
            return ArrangeSelectedTagStack(
                application,
                uidoc,
                document,
                view,
                referenceTag,
                selectedTags,
                referenceBounds,
                targetBounds,
                referenceEdge,
                alignLeftEdge,
                right,
                up,
                warnings,
                rowGapPaperMillimeters);
        }

        var shifts = new Dictionary<long, double>();
        foreach (IndependentTag tag in selectedTags)
        {
            if (!targetBounds.TryGetValue(tag.Id.Value, out LayoutRect bounds)) continue;
            LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
            LayoutPoint anchor = TryGetLeaderOrHostAnchor(
                tag,
                document,
                view,
                right,
                up) ?? new LayoutPoint(
                    (bounds.MinU + bounds.MaxU) * 0.5,
                    (bounds.MinV + bounds.MaxV) * 0.5);
            double targetEdge = alignLeftEdge
                ? SmartTagStackRouting.GetVisibleLeftEdge(
                    bounds, insertion, anchor, tag.HasLeader)
                : SmartTagStackRouting.GetVisibleRightEdge(
                    bounds, insertion, anchor, tag.HasLeader);
            shifts[tag.Id.Value] = referenceEdge - targetEdge;
        }
        int aligned = 0;
        var alignedIds = new List<ElementId>();
        using (var transaction = new Transaction(
                   document,
                   alignLeftEdge
                       ? "FamilyMEP - Align Selected Tag Left Edges"
                       : "FamilyMEP - Align Selected Tag Right Edges"))
        {
            transaction.Start();
            foreach (IndependentTag tag in selectedTags)
            {
                if (!shifts.TryGetValue(tag.Id.Value, out double shiftU)) continue;
                if (tag.Pinned)
                {
                    warnings.Add($"Target tag {tag.Id.Value} is pinned and was skipped.");
                    continue;
                }
                try
                {
                    // LEFT/RIGHT are edge-only commands. Preserve the exact
                    // original tag row and change only the view-right axis.
                    tag.TagHeadPosition += right * shiftU;
                    aligned++;
                    alignedIds.Add(tag.Id);
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} alignment: {FriendlyTagError(exception)}");
                }
            }
            document.Regenerate();
            RouteSeparatedOrthogonalLeaders(
                document,
                view,
                selectedTags
                    .Where(tag => alignedIds.Contains(tag.Id))
                    .OrderByDescending(tag => tag.TagHeadPosition.DotProduct(up))
                    .ToArray(),
                right,
                up,
                warnings);
            document.Regenerate();
            NormalizeManualEdgeFromRealBounds(
                document,
                view,
                referenceTag,
                selectedTags.Where(tag => alignedIds.Contains(tag.Id)).ToArray(),
                alignLeftEdge,
                right,
                up,
                warnings);
            document.Regenerate();
            RouteLeadersPreferStraight(
                document,
                view,
                selectedTags.Where(tag => alignedIds.Contains(tag.Id)).ToArray(),
                right,
                up,
                warnings,
                fixedTag: referenceTag);
            document.Regenerate();
            transaction.Commit();
        }

        if (alignedIds.Count > 0)
        {
            // Visible confirmation that Finish completed and that only tags
            // passed the selection filter. Grids/model elements can never be
            // present in this final Revit selection.
            uidoc.Selection.SetElementIds(alignedIds);
        }
        uidoc.RefreshActiveView();
        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == view.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        WaitForActualTagPreviewPaint(
            uidoc,
            application.MainWindowHandle,
            Math.Max(1, aligned));
        var window = uiView.GetWindowRectangle();
        byte[] image = CaptureViewport(
            application.MainWindowHandle,
            window.Left,
            window.Top,
            Math.Max(1, window.Right - window.Left),
            Math.Max(1, window.Bottom - window.Top));
        return new SmartTagManualAlignResult(
            aligned,
            selectedTags.Count - aligned,
            warnings,
            image);
    }

    private static SmartTagManualAlignResult ArrangeSelectedTagStack(
        UIApplication application,
        UIDocument uidoc,
        Document document,
        View view,
        IndependentTag referenceTag,
        IReadOnlyList<IndependentTag> selectedTags,
        LayoutRect referenceBounds,
        IReadOnlyDictionary<long, LayoutRect> targetBounds,
        double referenceEdge,
        bool alignLeftEdge,
        XYZ right,
        XYZ up,
        List<string> warnings,
        double rowGapPaperMillimeters)
    {
        var unorderedStackItems = selectedTags
            .Where(tag => targetBounds.ContainsKey(tag.Id.Value))
            .Select(tag =>
            {
                LayoutRect bounds = targetBounds[tag.Id.Value];
                LayoutPoint anchor = TryGetLeaderOrHostAnchor(
                                         tag,
                                         document,
                                         view,
                                         right,
                                         up) ??
                                     new LayoutPoint(
                                         (bounds.MinU + bounds.MaxU) * 0.5,
                                         (bounds.MinV + bounds.MaxV) * 0.5);
                LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
                double visibleLeftEdge = SmartTagStackRouting.GetVisibleLeftEdge(
                    bounds,
                    insertion,
                    anchor,
                    tag.HasLeader);
                LayoutRect routingBounds = alignLeftEdge
                    ? ShiftRect(bounds, visibleLeftEdge - bounds.MinU, 0.0)
                    : bounds;
                return (
                    Tag: tag,
                    Bounds: bounds,
                    RoutingBounds: routingBounds,
                    Anchor: anchor,
                    VisibleLeftEdge: visibleLeftEdge);
            })
            .ToList();
        if (unorderedStackItems.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected target tag text bounds could not be measured in the active view.");
        }

        double rowGap = Math.Max(0.0, rowGapPaperMillimeters) *
                        Math.Max(1, view.Scale) / 304.8;
        SmartTagStackRoutePlan routePlan = SmartTagStackRouting.FindBestOrder(
            unorderedStackItems
                .Select(item => new SmartTagStackRouteInput(
                    item.Tag.Id.Value,
                    item.RoutingBounds,
                    Project(item.Tag.TagHeadPosition, right, up),
                    item.Anchor))
                .ToArray(),
            referenceBounds,
            referenceEdge,
            alignLeftEdge,
            rowGap);
        Dictionary<long, (IndependentTag Tag, LayoutRect Bounds,
            LayoutRect RoutingBounds, LayoutPoint Anchor, double VisibleLeftEdge)> itemsById =
            unorderedStackItems.ToDictionary(item => item.Tag.Id.Value);
        List<(IndependentTag Tag, LayoutRect Bounds,
            LayoutRect RoutingBounds, LayoutPoint Anchor, double VisibleLeftEdge)> stackItems =
            routePlan.OrderedKeys.Select(key => itemsById[key]).ToList();
        if (routePlan.CrossingCount > 0)
        {
            warnings.Add(
                $"Stack route solver found {routePlan.CrossingCount} unavoidable predicted " +
                "leader crossing(s) for the selected tag/host geometry.");
        }
        double nextTop = referenceBounds.MinV - rowGap;
        int arranged = 0;
        var arrangedIds = new List<ElementId>();

        using (var transaction = new Transaction(
                   document,
                   "FamilyMEP - Stack Selected Tags And Untangle Leaders"))
        {
            transaction.Start();
            foreach (var item in stackItems)
            {
                if (item.Tag.Pinned) continue;
                try { item.Tag.TagOrientation = TagOrientation.Horizontal; }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {item.Tag.Id.Value} horizontal orientation: " +
                        FriendlyTagError(exception));
                }
            }
            document.Regenerate();
            foreach (var item in stackItems)
            {
                IndependentTag tag = item.Tag;
                LayoutRect bounds = item.Bounds;
                if (tag.Pinned)
                {
                    warnings.Add($"Target tag {tag.Id.Value} is pinned and was skipped.");
                    continue;
                }

                try
                {
                    double targetEdge = alignLeftEdge
                        ? item.VisibleLeftEdge
                        : bounds.MaxU;
                    double currentCenterV = (bounds.MinV + bounds.MaxV) * 0.5;
                    double desiredCenterV = nextTop - bounds.Height * 0.5;
                    tag.TagHeadPosition +=
                        right * (referenceEdge - targetEdge) +
                        up * (desiredCenterV - currentCenterV);
                    nextTop = desiredCenterV - bounds.Height * 0.5 - rowGap;
                    arranged++;
                    arrangedIds.Add(tag.Id);
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} stack placement: {FriendlyTagError(exception)}");
                }
            }

            document.Regenerate();

            // Build the standard one-elbow routes first. Leaders sharing the
            // same vertical axis are distributed slightly across the host
            // width below, while every elbow still shares Head.V + End.U.
            var routes = new List<(
                IndependentTag Tag,
                Reference Reference,
                LayoutPoint Head,
                LayoutPoint End,
                double HostWidth)>();
            foreach (var item in stackItems)
            {
                IndependentTag tag = item.Tag;
                if (!arrangedIds.Contains(tag.Id) || !tag.HasLeader) continue;
                IReadOnlyList<Reference> references;
                try
                {
                    references = tag.GetTaggedReferences().ToList();
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} leader references: {FriendlyTagError(exception)}");
                    continue;
                }

                foreach (Reference reference in references)
                {
                    try
                    {
                        LayoutPoint? end = TryGetLeaderRouteEnd(
                            tag,
                            reference,
                            document,
                            view,
                            right,
                            up);
                        if (end is null)
                        {
                            warnings.Add($"Tag {tag.Id.Value} leader end could not be resolved.");
                            continue;
                        }
                        LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                        double hostWidth = TryGetReferenceHostWidth(
                            reference,
                            document,
                            view,
                            right,
                            up);
                        routes.Add((tag, reference, head, end.Value, hostWidth));
                    }
                    catch (Exception exception)
                    {
                        warnings.Add($"Tag {tag.Id.Value} orthogonal leader: {FriendlyTagError(exception)}");
                    }
                }
            }

            double laneStep = 0.7 * Math.Max(1, view.Scale) / 304.8;
            double sameAxisTolerance = Math.Max(1e-6, laneStep * 0.35);
            var axisGroups = new List<List<(
                IndependentTag Tag,
                Reference Reference,
                LayoutPoint Head,
                LayoutPoint End,
                double HostWidth)>>();
            foreach (var route in routes.OrderBy(item => item.End.U))
            {
                List<(IndependentTag Tag, Reference Reference, LayoutPoint Head,
                    LayoutPoint End, double HostWidth)>? group = axisGroups
                    .LastOrDefault(candidate =>
                        Math.Abs(candidate.Average(item => item.End.U) - route.End.U) <=
                        sameAxisTolerance);
                if (group is null)
                {
                    group = [];
                    axisGroups.Add(group);
                }
                group.Add(route);
            }

            foreach (List<(IndependentTag Tag, Reference Reference, LayoutPoint Head,
                         LayoutPoint End, double HostWidth)> group in axisGroups)
            {
                double centerU = group.Average(item => item.End.U);
                double desiredSpan = laneStep * Math.Max(0, group.Count - 1);
                List<double> knownWidths = group
                    .Where(item => item.HostWidth > 1e-6)
                    .Select(item => item.HostWidth)
                    .ToList();
                double availableSpan = knownWidths.Count == 0
                    ? desiredSpan
                    : knownWidths.Min() * 0.65;
                double span = Math.Min(desiredSpan, availableSpan);
                double separation = group.Count <= 1 ? 0.0 : span / (group.Count - 1);

                for (int routeIndex = 0; routeIndex < group.Count; routeIndex++)
                {
                    var route = group[routeIndex];
                    double offset = group.Count <= 1
                        ? 0.0
                        : -span * 0.5 + separation * routeIndex;
                    double endU = centerU + offset;
                    double appliedEndU = route.End.U;
                    if (group.Count > 1)
                    {
                        try
                        {
                            if (route.Tag.LeaderEndCondition != LeaderEndCondition.Free)
                                route.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                            XYZ shiftedEnd = route.Tag.TagHeadPosition +
                                             right * (endU - route.Head.U) +
                                             up * (route.End.V - route.Head.V);
                            route.Tag.SetLeaderEnd(route.Reference, shiftedEnd);
                            appliedEndU = endU;
                        }
                        catch (Exception exception)
                        {
                            warnings.Add(
                                $"Tag {route.Tag.Id.Value} leader endpoint separation: " +
                                FriendlyTagError(exception));
                        }
                    }
                    try
                    {
                        XYZ elbow = route.Tag.TagHeadPosition +
                                    right * (appliedEndU - route.Head.U);
                        route.Tag.SetLeaderElbow(route.Reference, elbow);
                    }
                    catch (Exception exception)
                    {
                        warnings.Add(
                            $"Tag {route.Tag.Id.Value} separated orthogonal leader: " +
                            FriendlyTagError(exception));
                    }
                }
            }

            NormalizeManualColumnFromRealBounds(
                document,
                view,
                referenceTag,
                stackItems
                    .Where(item => arrangedIds.Contains(item.Tag.Id))
                    .Select(item => item.Tag)
                    .ToArray(),
                alignLeftEdge,
                placeAllBelow: true,
                rowGap,
                right,
                up,
                warnings,
                placeAllAbove: false,
                normalizeAutoBodyBounds: true);
            document.Regenerate();
            RouteLeadersPreferStraight(
                document,
                view,
                stackItems
                    .Where(item => arrangedIds.Contains(item.Tag.Id))
                    .Select(item => item.Tag)
                    .ToArray(),
                right,
                up,
                warnings,
                fixedTag: referenceTag);
            document.Regenerate();
            transaction.Commit();
        }

        if (arrangedIds.Count > 0)
            uidoc.Selection.SetElementIds(arrangedIds);
        uidoc.RefreshActiveView();
        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == view.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        WaitForActualTagPreviewPaint(
            uidoc,
            application.MainWindowHandle,
            Math.Max(1, arranged));
        var window = uiView.GetWindowRectangle();
        byte[] image = CaptureViewport(
            application.MainWindowHandle,
            window.Left,
            window.Top,
            Math.Max(1, window.Right - window.Left),
            Math.Max(1, window.Bottom - window.Top));
        return new SmartTagManualAlignResult(
            arranged,
            selectedTags.Count - arranged,
            warnings,
            image);
    }

    private static LayoutPoint? TryGetPrimaryLeaderEnd(
        IndependentTag tag,
        XYZ right,
        XYZ up)
    {
        if (!tag.HasLeader) return null;
        try
        {
            foreach (Reference reference in tag.GetTaggedReferences())
            {
                try
                {
                    return Project(tag.GetLeaderEnd(reference), right, up);
                }
                catch
                {
                    // Try another tagged reference on a multi-reference tag.
                }
            }
        }
        catch
        {
            // A leader-less/orphaned tag falls back to its current text row.
        }
        return null;
    }

    private static LayoutPoint? TryGetLeaderOrHostAnchor(
        IndependentTag tag,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        LayoutPoint? freeLeaderEnd = TryGetPrimaryLeaderEnd(tag, right, up);
        if (freeLeaderEnd is not null) return freeLeaderEnd;
        try
        {
            foreach (Reference reference in tag.GetTaggedReferences())
            {
                LayoutPoint? host = TryGetReferenceHostAnchor(
                    reference,
                    document,
                    view,
                    right,
                    up);
                if (host is not null) return host;
            }
        }
        catch
        {
            // Orphaned tags retain their current text order as final fallback.
        }
        return null;
    }

    private static LayoutPoint? TryGetAutoColumnTargetAnchor(
        IndependentTag tag,
        Document document,
        View view,
        XYZ right,
        XYZ up,
        XYZ proximityPoint,
        out bool isDuct)
    {
        isDuct = false;
        try
        {
            foreach (Reference reference in tag.GetTaggedReferences())
            {
                Element? host = document.GetElement(reference.ElementId);
                Transform? linkTransform = null;
                if (host is RevitLinkInstance link &&
                    reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    host = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                    linkTransform = link.GetTransform();
                }

                if (host?.Category?.Id.Value != (long)BuiltInCategory.OST_DuctCurves)
                    continue;

                isDuct = true;
                // A readable Free endpoint is already the user's accepted
                // attachment point. Preserve it exactly, just as AUTO does for
                // DA/AT; only an unreadable Attached endpoint needs a geometric
                // fallback on the duct curve.
                LayoutPoint? existingEnd = TryGetPrimaryLeaderEnd(tag, right, up);
                if (existingEnd is not null) return existingEnd;
                try
                {
                    if (host.Location is LocationCurve locationCurve &&
                        locationCurve.Curve.IsBound)
                    {
                        XYZ localPoint = linkTransform is null
                            ? proximityPoint
                            : linkTransform.Inverse.OfPoint(proximityPoint);
                        IntersectionResult? projection = locationCurve.Curve.Project(localPoint);
                        if (projection is not null)
                        {
                            XYZ nearestPoint = linkTransform is null
                                ? projection.XYZPoint
                                : linkTransform.OfPoint(projection.XYZPoint);
                            return Project(nearestPoint, right, up);
                        }
                    }
                }
                catch
                {
                    // Fall back to the current leader/host anchor below.
                }

                return TryGetLeaderOrHostAnchor(tag, document, view, right, up);
            }
        }
        catch
        {
            // Unusual/orphaned tags keep the generic AUTO behavior.
        }

        return TryGetLeaderOrHostAnchor(tag, document, view, right, up);
    }

    private static void NormalizeAutoTagOrientationAndLeader(
        IndependentTag tag,
        LayoutPoint anchor,
        XYZ right,
        XYZ up,
        ICollection<string> warnings)
    {
        try
        {
            tag.TagOrientation = TagOrientation.Horizontal;
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Tag {tag.Id.Value} horizontal orientation: " +
                FriendlyTagError(exception));
        }
        if (!tag.HasLeader) return;
        try
        {
            foreach (Reference reference in tag.GetTaggedReferences())
            {
                try
                {
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    XYZ end = MoveInViewPlane(tag.TagHeadPosition, anchor, right, up);
                    tag.SetLeaderEnd(reference, end);
                    LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                    XYZ elbow = tag.TagHeadPosition + right * (anchor.U - head.U);
                    tag.SetLeaderElbow(reference, elbow);
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {tag.Id.Value} AUTO orthogonal leader: " +
                        FriendlyTagError(exception));
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Tag {tag.Id.Value} AUTO leader references: " +
                FriendlyTagError(exception));
        }
    }

    private static LayoutPoint? TryGetReferenceHostAnchor(
        Reference reference,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        try
        {
            Element? host = document.GetElement(reference.ElementId);
            if (host is RevitLinkInstance link &&
                reference.LinkedElementId != ElementId.InvalidElementId)
            {
                Element? linkedHost = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                BoundingBoxXYZ? linkedBox = linkedHost?.get_BoundingBox(null);
                if (linkedBox is not null)
                {
                    XYZ localCenter = (linkedBox.Min + linkedBox.Max) * 0.5;
                    return Project(link.GetTransform().OfPoint(localCenter), right, up);
                }
            }

            BoundingBoxXYZ? box = host?.get_BoundingBox(view) ?? host?.get_BoundingBox(null);
            if (box is not null)
            {
                LayoutRect projected = ProjectBox(box, right, up);
                return new LayoutPoint(
                    (projected.MinU + projected.MaxU) * 0.5,
                    (projected.MinV + projected.MaxV) * 0.5);
            }
        }
        catch
        {
            // Some references do not expose their host from this document.
        }
        try
        {
            XYZ point = reference.GlobalPoint;
            if (point is not null &&
                double.IsFinite(point.X) &&
                double.IsFinite(point.Y) &&
                double.IsFinite(point.Z))
            {
                return Project(point, right, up);
            }
        }
        catch
        {
            // No usable global point.
        }
        return null;
    }

    private static LayoutPoint? TryGetLeaderRouteEnd(
        IndependentTag tag,
        Reference reference,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        try
        {
            return Project(tag.GetLeaderEnd(reference), right, up);
        }
        catch
        {
            return TryGetReferenceHostAnchor(reference, document, view, right, up);
        }
    }

    private static LayoutPoint? TryGetSelectionLeaderRouteEnd(
        IndependentTag tag,
        Reference reference,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        try
        {
            return Project(tag.GetLeaderEnd(reference), right, up);
        }
        catch
        {
            // GetLeaderEnd is commonly unavailable for Attached leaders. For a
            // long Duct, the host bounding-box centre is not the displayed
            // attachment and can make an unrelated crossing box select the tag.
            // Reconstruct the final leg from the elbow to the closest point on
            // the actual duct curve instead.
            try
            {
                XYZ approachPoint;
                try { approachPoint = tag.GetLeaderElbow(reference); }
                catch { approachPoint = tag.TagHeadPosition; }

                Element? host = document.GetElement(reference.ElementId);
                Transform? linkTransform = null;
                if (host is RevitLinkInstance link &&
                    reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    host = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                    linkTransform = link.GetTransform();
                }

                if (host?.Category?.Id.Value == (long)BuiltInCategory.OST_DuctCurves &&
                    host.Location is LocationCurve locationCurve &&
                    locationCurve.Curve.IsBound)
                {
                    XYZ localApproach = linkTransform is null
                        ? approachPoint
                        : linkTransform.Inverse.OfPoint(approachPoint);
                    IntersectionResult? projection = locationCurve.Curve.Project(localApproach);
                    if (projection is not null)
                    {
                        XYZ nearestPoint = linkTransform is null
                            ? projection.XYZPoint
                            : linkTransform.OfPoint(projection.XYZPoint);
                        return Project(nearestPoint, right, up);
                    }
                }
            }
            catch
            {
                // Compact hosts and unusual linked references use the existing
                // host-anchor fallback below.
            }

            return TryGetReferenceHostAnchor(reference, document, view, right, up);
        }
    }

    private static double TryGetReferenceHostWidth(
        Reference reference,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        try
        {
            Element? host = document.GetElement(reference.ElementId);
            if (host is RevitLinkInstance link &&
                reference.LinkedElementId != ElementId.InvalidElementId)
            {
                Element? linkedHost = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                BoundingBoxXYZ? linkedBox = linkedHost?.get_BoundingBox(null);
                return linkedBox is null
                    ? 0.0
                    : ProjectBox(linkedBox, link.GetTransform(), right, up).Width;
            }
            BoundingBoxXYZ? box = host?.get_BoundingBox(view) ?? host?.get_BoundingBox(null);
            return box is null ? 0.0 : ProjectBox(box, right, up).Width;
        }
        catch
        {
            return 0.0;
        }
    }

    public static SmartTagManualAlignResult? PickAndArrangeTagsAroundReference(
        UIApplication application,
        double rowGapPaperMillimeters = 1.0,
        bool placeAboveReference = false)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        Document document = uidoc.Document;
        View view = document.ActiveView;
        XYZ right = view.RightDirection.Normalize();
        XYZ up = view.UpDirection.Normalize();
        Reference referencePick;
        LayoutRect sourceZone;
        try
        {
            referencePick = uidoc.Selection.PickObject(
                ObjectType.Element,
                new ExistingTagSelectionFilter(),
                "AUTO bước 1/2: click TAG MẪU cố định.");
            PickedBox sourcePick = uidoc.Selection.PickBox(
                PickBoxStyle.Crossing,
                "AUTO bước 2/2: quét khung chạm TEXT hoặc LEADER của các tag cần xếp.");
            sourceZone = ProjectPickedBox(sourcePick, right, up);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }

        IndependentTag referenceTag = document.GetElement(referencePick) as IndependentTag
            ?? throw new InvalidOperationException("The picked reference is not an Independent Tag.");
        var warnings = new List<string>();
        (List<IndependentTag> selectedTags, Dictionary<long, LayoutRect> targetBounds) =
            CollectTagsTouchedByRectangle(
                document,
                view,
                sourceZone,
                right,
                up,
                warnings,
                referenceTag.Id.Value);
        if (selectedTags.Count == 0)
        {
            throw new InvalidOperationException(
                "The scan rectangle did not touch any target tag text or leader line.");
        }

        // AUTO has one visual rule for every category. Measure the body after a
        // temporary Horizontal rotation so vertical Duct tags use the same real
        // width/height and row gap as DA/AT before anything is committed.
        targetBounds = MeasureTagTextBounds(
            document,
            view,
            selectedTags,
            right,
            up,
            warnings,
            "FamilyMEP - Measure Horizontal Auto Column Targets",
            forceHorizontal: true);
        NormalizeAutoBodyBounds(
            selectedTags,
            targetBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: true);

        Dictionary<long, LayoutRect> referenceMeasurement = MeasureTagTextBounds(
            document,
            view,
            [referenceTag],
            right,
            up,
            warnings,
            "FamilyMEP - Measure Auto Column Reference",
            forceHorizontal: true);
        NormalizeAutoBodyBounds(
            [referenceTag],
            referenceMeasurement,
            view,
            right,
            up,
            warnings,
            reportAdjustments: true);
        if (!referenceMeasurement.TryGetValue(referenceTag.Id.Value, out LayoutRect referenceBounds))
            throw new InvalidOperationException("The reference tag text bounds could not be measured.");

        // A Duct can be a fixed AUTO sample too. Resolve that sample against
        // the nearest point on its curve to the visible tag head; using the
        // curve midpoint here is what leaves a very long sample leader and
        // gives the route solver a misleading fixed route.
        LayoutPoint referenceAnchor = TryGetAutoColumnTargetAnchor(
                                          referenceTag,
                                          document,
                                          view,
                                          right,
                                          up,
                                          referenceTag.TagHeadPosition,
                                          out _) ??
                                      new LayoutPoint(
                                          (referenceBounds.MinU + referenceBounds.MaxU) * 0.5,
                                          (referenceBounds.MinV + referenceBounds.MaxV) * 0.5);
        LayoutPoint referenceInsertion = Project(referenceTag.TagHeadPosition, right, up);
        double referenceLeftEdge = SmartTagStackRouting.GetVisibleLeftEdge(
            referenceBounds,
            referenceInsertion,
            referenceAnchor,
            referenceTag.HasLeader);
        LayoutRect referenceRoutingBounds = ShiftRect(
            referenceBounds,
            referenceLeftEdge - referenceBounds.MinU,
            0.0);
        XYZ referenceAnchorWorld = MoveInViewPlane(
            referenceTag.TagHeadPosition,
            referenceAnchor,
            right,
            up);
        var targets = selectedTags
            .Where(tag => targetBounds.ContainsKey(tag.Id.Value))
            .Select(tag =>
            {
                LayoutRect bounds = targetBounds[tag.Id.Value];
                LayoutPoint anchor = TryGetAutoColumnTargetAnchor(
                                         tag,
                                         document,
                                         view,
                                         right,
                                         up,
                                         referenceAnchorWorld,
                                         out _) ??
                                     new LayoutPoint(
                                         (bounds.MinU + bounds.MaxU) * 0.5,
                                         (bounds.MinV + bounds.MaxV) * 0.5);
                LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
                double leftEdge = SmartTagStackRouting.GetVisibleLeftEdge(
                    bounds,
                    insertion,
                    anchor,
                    tag.HasLeader);
                LayoutRect routingBounds = ShiftRect(
                    bounds,
                    leftEdge - bounds.MinU,
                    0.0);
                return (
                    Tag: tag,
                    Bounds: bounds,
                    RoutingBounds: routingBounds,
                    Anchor: anchor,
                    LeftEdge: leftEdge);
            })
            .ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("The scanned target tags have no measurable text bounds.");

        // AUTO has one invariant on both sides of the model: the visible left
        // text edge follows the picked sample. Host/leader direction must never
        // change which text edge is aligned.
        const bool alignLeftEdge = true;
        double referenceEdge = referenceLeftEdge;
        double rowGap = Math.Max(0.0, rowGapPaperMillimeters) *
                        Math.Max(1, view.Scale) / 304.8;

        SmartTagAutoColumnRoutePlan autoPlan = SmartTagStackRouting.FindBestAroundReference(
            targets.Select(item => new SmartTagStackRouteInput(
                    item.Tag.Id.Value,
                    item.RoutingBounds,
                    SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                        item.Bounds,
                        item.Anchor),
                    item.Anchor))
                .ToArray(),
            new SmartTagStackRouteInput(
                referenceTag.Id.Value,
                referenceRoutingBounds,
                SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                    referenceBounds,
                    referenceAnchor),
                referenceAnchor),
            referenceRoutingBounds,
            referenceEdge,
            alignLeftEdge,
            rowGap,
            belowOnly: !placeAboveReference,
            aboveOnly: placeAboveReference);
        var targetsById = targets.ToDictionary(item => item.Tag.Id.Value);
        var originalHeads = targets.ToDictionary(
            item => item.Tag.Id.Value,
            item => Project(item.Tag.TagHeadPosition, right, up));
        SmartTagAutoColumnRoutePlan finalPlan = autoPlan;
        int arranged = 0;
        var arrangedIds = new List<ElementId>();
        IndependentTag[] committedOrderedTags = [];
        using var autoTransactionGroup = new TransactionGroup(
            document,
            "FamilyMEP - Auto Arrange Tags Around Reference");
        autoTransactionGroup.Start();
        using (var transaction = new Transaction(
                   document,
                   "FamilyMEP - Auto Arrange Tags Around Reference"))
        {
            transaction.Start();
            foreach (var item in targets)
            {
                try { item.Tag.TagOrientation = TagOrientation.Horizontal; }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {item.Tag.Id.Value} horizontal orientation: " +
                        FriendlyTagError(exception));
                }
            }
            // The picked sample is a fixed visual reference. Its orientation,
            // head, endpoint and elbow must remain exactly as the user placed
            // them; AUTO only measures it and moves the scanned target tags.
            document.Regenerate();
            PlacePlan(autoPlan.AboveNearestFirst, placeAbove: true);
            PlacePlan(autoPlan.BelowNearestFirst, placeAbove: false);
            document.Regenerate();
            RouteSeparatedOrthogonalLeaders(
                document,
                view,
                targets
                    .Where(item => arrangedIds.Contains(item.Tag.Id))
                    .OrderByDescending(item => item.Tag.TagHeadPosition.DotProduct(up))
                    .Select(item => item.Tag)
                    .ToArray(),
                right,
                up,
                warnings,
                fixedTag: referenceTag,
                minimumLaneClearance: Math.Max(rowGap * 0.35, 1e-8));
            document.Regenerate();

            var arrangedTargets = targets
                .Where(item => arrangedIds.Contains(item.Tag.Id))
                .ToList();
            IReadOnlyList<long> initialOrderedKeys = placeAboveReference
                ? autoPlan.AboveNearestFirst
                : autoPlan.BelowNearestFirst;
            IReadOnlyList<IndependentTag> finalOrderedTags = initialOrderedKeys
                .Where(targetsById.ContainsKey)
                .Select(key => targetsById[key].Tag)
                .ToArray();

            // Revit may move a free endpoint when a tag changes row, while the
            // lane spread itself can expose a new route inversion. Complex mixed
            // DA/AT/Duct groups need a few actual-geometry acceptance cycles;
            // two passes can stop while the last pair of vertical legs overlaps.
            for (int acceptance = 0; acceptance < 4; acceptance++)
            {
                if (acceptance > 0)
                {
                    RouteSeparatedOrthogonalLeaders(
                        document,
                        view,
                        finalOrderedTags,
                        right,
                        up,
                        warnings,
                        fixedTag: referenceTag,
                        minimumLaneClearance: Math.Max(rowGap * 0.35, 1e-8));
                    document.Regenerate();
                }

                Dictionary<long, LayoutRect> finalTextBounds =
                    MeasureTagTextBoundsInOpenTransaction(
                        document,
                        view,
                        arrangedTargets.Select(item => item.Tag).Prepend(referenceTag).ToArray(),
                        right,
                        up,
                        warnings);
                NormalizeAutoBodyBounds(
                    arrangedTargets.Select(item => item.Tag).Prepend(referenceTag).ToArray(),
                    finalTextBounds,
                    view,
                    right,
                    up,
                    warnings,
                    reportAdjustments: false);
                LayoutRect finalReferenceBounds = finalTextBounds.TryGetValue(
                    referenceTag.Id.Value,
                    out LayoutRect measuredReferenceBounds)
                        ? measuredReferenceBounds
                        : referenceBounds;
                LayoutPoint finalReferenceInsertion = Project(
                    referenceTag.TagHeadPosition,
                    right,
                    up);
                LayoutPoint finalReferenceAnchor = TryGetLeaderOrHostAnchor(
                    referenceTag,
                    document,
                    view,
                    right,
                    up) ?? referenceAnchor;
                double finalReferenceEdge = SmartTagStackRouting.GetVisibleLeftEdge(
                    finalReferenceBounds,
                    finalReferenceInsertion,
                    finalReferenceAnchor,
                    referenceTag.HasLeader);
                LayoutRect finalReferenceRoutingBounds = ShiftRect(
                    finalReferenceBounds,
                    finalReferenceEdge - finalReferenceBounds.MinU,
                    0.0);
                var actualTargets = arrangedTargets
                    .Select(item =>
                    {
                        LayoutPoint currentHead = Project(item.Tag.TagHeadPosition, right, up);
                        LayoutPoint originalHead = originalHeads[item.Tag.Id.Value];
                        LayoutRect currentBounds = finalTextBounds.TryGetValue(
                            item.Tag.Id.Value,
                            out LayoutRect measuredBounds)
                                ? measuredBounds
                                : ShiftRect(
                                    item.Bounds,
                                    currentHead.U - originalHead.U,
                                    currentHead.V - originalHead.V);
                        LayoutPoint currentEnd = TryGetLeaderOrHostAnchor(
                                                     item.Tag,
                                                     document,
                                                     view,
                                                     right,
                                                     up) ?? item.Anchor;
                        double leftEdge = SmartTagStackRouting.GetVisibleLeftEdge(
                            currentBounds,
                            currentHead,
                            currentEnd,
                            item.Tag.HasLeader);
                        LayoutRect routingBounds = ShiftRect(
                            currentBounds,
                            leftEdge - currentBounds.MinU,
                            0.0);
                        return (
                            item.Tag,
                            Bounds: currentBounds,
                            RoutingBounds: routingBounds,
                            Anchor: currentEnd,
                            LeftEdge: leftEdge);
                    })
                    .ToList();

                finalPlan = SmartTagStackRouting.FindBestAroundReference(
                    actualTargets.Select(item => new SmartTagStackRouteInput(
                            item.Tag.Id.Value,
                            item.RoutingBounds,
                            SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                                item.Bounds,
                                item.Anchor),
                            item.Anchor))
                        .ToArray(),
                    new SmartTagStackRouteInput(
                        referenceTag.Id.Value,
                        finalReferenceRoutingBounds,
                        SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                            finalReferenceBounds,
                            referenceAnchor),
                        referenceAnchor),
                    finalReferenceRoutingBounds,
                    finalReferenceEdge,
                    alignLeftEdge,
                    rowGap,
                    belowOnly: !placeAboveReference,
                    aboveOnly: placeAboveReference);

                var actualById = actualTargets.ToDictionary(item => item.Tag.Id.Value);
                double boundary = placeAboveReference
                    ? finalReferenceBounds.MaxV + rowGap
                    : finalReferenceBounds.MinV - rowGap;
                IReadOnlyList<long> acceptedKeys = placeAboveReference
                    ? finalPlan.AboveNearestFirst
                    : finalPlan.BelowNearestFirst;
                foreach (long key in acceptedKeys)
                {
                    var item = actualById[key];
                    double currentCenterV = (item.Bounds.MinV + item.Bounds.MaxV) * 0.5;
                    double desiredCenterV = placeAboveReference
                        ? boundary + item.Bounds.Height * 0.5
                        : boundary - item.Bounds.Height * 0.5;
                    item.Tag.TagHeadPosition +=
                        right * (finalReferenceEdge - item.LeftEdge) +
                        up * (desiredCenterV - currentCenterV);
                    boundary = placeAboveReference
                        ? desiredCenterV + item.Bounds.Height * 0.5 + rowGap
                        : desiredCenterV - item.Bounds.Height * 0.5 - rowGap;
                }
                document.Regenerate();

                finalOrderedTags = acceptedKeys
                    .Where(actualById.ContainsKey)
                    .Select(key => actualById[key].Tag)
                    .ToArray();
                RouteOrthogonalLeadersKeepingEnds(
                    document,
                    view,
                    finalOrderedTags,
                    right,
                    up,
                    warnings);
                document.Regenerate();
            }

            // Final visible-edge lock: leader attachment can alter a Family's
            // text offset after the last regeneration. Re-measure once, force
            // every real left edge and visible gap to the sample, then rebuild
            // the same accepted orthogonal routes without moving endpoints.
            NormalizeManualColumnFromRealBounds(
                document,
                view,
                referenceTag,
                finalOrderedTags,
                alignLeftEdge,
                placeAllBelow: !placeAboveReference,
                rowGap,
                right,
                up,
                warnings,
                placeAllAbove: placeAboveReference,
                normalizeAutoBodyBounds: true);
            document.Regenerate();
            // Row normalization can change the last horizontal shoulders. Run
            // lane optimization once more against those final real rows; doing
            // it only before normalization is what reintroduced crossings.
            RouteSeparatedOrthogonalLeaders(
                document,
                view,
                finalOrderedTags
                    .OrderByDescending(tag => tag.TagHeadPosition.DotProduct(up))
                    .ToArray(),
                right,
                up,
                warnings,
                fixedTag: referenceTag,
                minimumLaneClearance: Math.Max(rowGap * 0.35, 1e-8));
            document.Regenerate();
            // Duct tag families can shift both their visible attachment and
            // the leader's 3-D plane after regeneration. Setting only Elbow
            // leaves a slightly diagonal shoulder/tail. Reassert orientation,
            // End and Elbow together, then verify their projected axes after
            // every regeneration before the AUTO transaction is committed.
            StabilizeAutoOrthogonalLeaders(
                document,
                view,
                finalOrderedTags,
                right,
                up,
                warnings);
            RouteLeadersPreferStraight(
                document,
                view,
                finalOrderedTags,
                right,
                up,
                warnings,
                fixedTag: referenceTag);
            document.Regenerate();
            committedOrderedTags = finalOrderedTags.ToArray();
            transaction.Commit();

            void PlacePlan(IReadOnlyList<long> orderedKeys, bool placeAbove)
            {
                double boundary = placeAbove
                    ? referenceBounds.MaxV + rowGap
                    : referenceBounds.MinV - rowGap;
                foreach (long key in orderedKeys)
                {
                    var item = targetsById[key];
                    if (item.Tag.Pinned)
                    {
                        warnings.Add($"Target tag {key} is pinned and was skipped.");
                        continue;
                    }
                    try
                    {
                        double currentCenterV = (item.Bounds.MinV + item.Bounds.MaxV) * 0.5;
                        double desiredCenterV = placeAbove
                            ? boundary + item.Bounds.Height * 0.5
                            : boundary - item.Bounds.Height * 0.5;
                        item.Tag.TagHeadPosition +=
                            right * (referenceEdge - item.LeftEdge) +
                            up * (desiredCenterV - currentCenterV);
                        NormalizeAutoTagOrientationAndLeader(
                            item.Tag,
                            item.Anchor,
                            right,
                            up,
                            warnings);
                        boundary = placeAbove
                            ? desiredCenterV + item.Bounds.Height * 0.5 + rowGap
                            : desiredCenterV - item.Bounds.Height * 0.5 - rowGap;
                        arranged++;
                        arrangedIds.Add(item.Tag.Id);
                    }
                    catch (Exception exception)
                    {
                        warnings.Add($"Tag {key} auto-column placement: {FriendlyTagError(exception)}");
                    }
                }
            }
        }

        // A Revit commit can perform one more family regeneration after the
        // in-transaction verification. Re-check persisted geometry and repair
        // it in the same assimilated Undo item, otherwise some Duct families
        // leave the first shoulder or final tail slightly diagonal on screen.
        double persistedTolerance = Math.Max(
            1e-7,
            0.01 * Math.Max(1, view.Scale) / 304.8);
        Dictionary<long, LayoutRect> persistedBodyBounds = MeasureTagTextBounds(
            document,
            view,
            committedOrderedTags,
            right,
            up,
            warnings,
            "FamilyMEP - Verify Persisted Auto Tag Bodies",
            forceHorizontal: true);
        NormalizeAutoBodyBounds(
            committedOrderedTags,
            persistedBodyBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: false);
        HashSet<long> persistedFailures = GetNonOrthogonalAutoLeaderTagIds(
            committedOrderedTags,
            right,
            up,
            persistedTolerance,
            persistedBodyBounds);
        for (int repair = 0; persistedFailures.Count > 0 && repair < 3; repair++)
        {
            using var correction = new Transaction(
                document,
                "FamilyMEP - Finalize Auto Orthogonal Leaders");
            correction.Start();
            StabilizeAutoOrthogonalLeaders(
                document,
                view,
                committedOrderedTags,
                right,
                up,
                warnings);
            RouteLeadersPreferStraight(
                document,
                view,
                committedOrderedTags,
                right,
                up,
                warnings,
                fixedTag: referenceTag);
            correction.Commit();
            persistedBodyBounds = MeasureTagTextBounds(
                document,
                view,
                committedOrderedTags,
                right,
                up,
                warnings,
                "FamilyMEP - Recheck Persisted Auto Tag Bodies",
                forceHorizontal: true);
            NormalizeAutoBodyBounds(
                committedOrderedTags,
                persistedBodyBounds,
                view,
                right,
                up,
                warnings,
                reportAdjustments: false);
            persistedFailures = GetNonOrthogonalAutoLeaderTagIds(
                committedOrderedTags,
                right,
                up,
                persistedTolerance,
                persistedBodyBounds);
        }
        if (persistedFailures.Count > 0)
        {
            warnings.Add(
                $"AUTO persisted orthogonal verification: {persistedFailures.Count} " +
                "tag(s) are constrained by their project family.");
        }
        autoTransactionGroup.Assimilate();

        int predictedCrossings = finalPlan.CrossingCount;
        if (predictedCrossings > 0)
        {
            warnings.Add(
                $"Auto Column retained {predictedCrossings} unavoidable predicted leader crossing(s)." );
        }
        if (arrangedIds.Count > 0)
            uidoc.Selection.SetElementIds(arrangedIds);
        uidoc.RefreshActiveView();
        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == view.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        WaitForActualTagPreviewPaint(uidoc, application.MainWindowHandle, Math.Max(1, arranged));
        var window = uiView.GetWindowRectangle();
        byte[] image = CaptureViewport(
            application.MainWindowHandle,
            window.Left,
            window.Top,
            Math.Max(1, window.Right - window.Left),
            Math.Max(1, window.Bottom - window.Top));
        return new SmartTagManualAlignResult(
            arranged,
            selectedTags.Count - arranged,
            warnings,
            image);
    }

    public static SmartTagManualAlignResult? PickAndArrangeTagsInZone(
        UIApplication application,
        double rowGapPaperMillimeters = 1.0)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project first.");
        Document document = uidoc.Document;
        View view = document.ActiveView;
        XYZ right = view.RightDirection.Normalize();
        XYZ up = view.UpDirection.Normalize();
        LayoutRect sourceZone;
        try
        {
            PickedBox sourcePick = uidoc.Selection.PickBox(
                PickBoxStyle.Crossing,
                "ZONE bước 1/2: quét khung chạm TEXT hoặc LEADER của các tag cần dời.");
            sourceZone = ProjectPickedBox(sourcePick, right, up);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }

        List<IndependentTag> visibleTags = new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>()
            .Where(tag => !tag.IsHidden(view))
            .ToList();
        if (visibleTags.Count == 0)
            throw new InvalidOperationException("No visible Independent Tags were found in the active view.");

        var warnings = new List<string>();
        var fullBounds = new Dictionary<long, LayoutRect>();
        var leaderSegments = new Dictionary<long, List<LayoutSegment>>();
        foreach (IndependentTag tag in visibleTags)
        {
            try
            {
                BoundingBoxXYZ? fullBox = tag.get_BoundingBox(view);
                if (fullBox is not null)
                    fullBounds[tag.Id.Value] = ProjectBox(fullBox, right, up);
            }
            catch
            {
                // Exact text/leader tests below can still identify the tag.
            }

            var segments = new List<LayoutSegment>();
            if (tag.HasLeader)
            {
                try
                {
                    LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                    foreach (Reference reference in tag.GetTaggedReferences())
                    {
                        try
                        {
                            LayoutPoint end = Project(tag.GetLeaderEnd(reference), right, up);
                            try
                            {
                                LayoutPoint elbow = Project(tag.GetLeaderElbow(reference), right, up);
                                segments.Add(new LayoutSegment(head, elbow));
                                segments.Add(new LayoutSegment(elbow, end));
                            }
                            catch
                            {
                                segments.Add(new LayoutSegment(head, end));
                            }
                        }
                        catch
                        {
                            LayoutPoint? attachedEnd = TryGetReferenceHostAnchor(
                                reference,
                                document,
                                view,
                                right,
                                up);
                            if (attachedEnd is not null)
                                segments.Add(new LayoutSegment(head, attachedEnd.Value));
                        }
                    }
                }
                catch
                {
                    // Keep the full-bounds fallback for unusual tag families.
                }
            }
            leaderSegments[tag.Id.Value] = segments;
        }

        // The cheap visible-bounds pass normally reduces hundreds of view tags
        // to only the few touched by the source rectangle. Real text
        // measurement is then limited to these candidates so Zone Tags remains
        // responsive on dense coordination views.
        List<IndependentTag> candidateTags = visibleTags
            .Where(tag =>
            {
                long id = tag.Id.Value;
                bool pathHit = leaderSegments.TryGetValue(id, out List<LayoutSegment>? paths) &&
                               paths.Any(path => SegmentIntersectsRect(path, sourceZone));
                bool boxHit = fullBounds.TryGetValue(id, out LayoutRect full) &&
                              RectanglesTouch(full, sourceZone);
                return pathHit || boxHit;
            })
            .ToList();
        if (candidateTags.Count == 0)
        {
            throw new InvalidOperationException(
                "The first rectangle did not touch any visible tag text or leader line.");
        }

        var textBounds = new Dictionary<long, LayoutRect>();
        using (var measurement = new Transaction(
                   document,
                   "FamilyMEP - Measure Tags For Zone Collection"))
        {
            measurement.Start();
            foreach (IndependentTag tag in candidateTags)
            {
                try
                {
                    if (tag.Pinned) tag.Pinned = false;
                    if (tag.HasLeader) tag.HasLeader = false;
                }
                catch
                {
                    // Its full bounds remain available as a conservative fallback.
                }
            }
            document.Regenerate();
            foreach (IndependentTag tag in candidateTags)
            {
                try
                {
                    BoundingBoxXYZ? box = tag.get_BoundingBox(view);
                    if (box is not null)
                        textBounds[tag.Id.Value] = ProjectBox(box, right, up);
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} text measurement: {FriendlyTagError(exception)}");
                }
            }
            measurement.RollBack();
        }

        List<IndependentTag> selectedTags = candidateTags
            .Where(tag =>
            {
                long id = tag.Id.Value;
                bool textHit = textBounds.TryGetValue(id, out LayoutRect text) &&
                               RectanglesTouch(text, sourceZone);
                bool leaderHit = leaderSegments.TryGetValue(id, out List<LayoutSegment>? paths) &&
                                 paths.Any(path => SegmentIntersectsRect(path, sourceZone));
                bool fallbackHit = !leaderHit &&
                                   (paths is null || paths.Count == 0) &&
                                   fullBounds.TryGetValue(id, out LayoutRect full) &&
                                   RectanglesTouch(full, sourceZone);
                return textHit || leaderHit || fallbackHit;
            })
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToList();
        if (selectedTags.Count == 0)
        {
            throw new InvalidOperationException(
                "The first rectangle did not touch any tag text or leader line. Drag across at least one visible leader/text.");
        }

        LayoutRect targetZone;
        try
        {
            PickedBox targetPick = uidoc.Selection.PickBox(
                PickBoxStyle.Enclosing,
                $"ZONE bước 2/2: quét gần VỊ TRÍ ĐÍCH cho {selectedTags.Count} tag. Kích thước khung không giới hạn cụm tag.");
            targetZone = ProjectPickedBox(targetPick, right, up);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }

        return ArrangeTagsInsidePickedZone(
            application,
            uidoc,
            document,
            view,
            selectedTags,
            textBounds,
            targetZone,
            right,
            up,
            warnings,
            rowGapPaperMillimeters);
    }

    private static SmartTagManualAlignResult ArrangeTagsInsidePickedZone(
        UIApplication application,
        UIDocument uidoc,
        Document document,
        View view,
        IReadOnlyList<IndependentTag> selectedTags,
        IReadOnlyDictionary<long, LayoutRect> measuredBounds,
        LayoutRect targetZone,
        XYZ right,
        XYZ up,
        List<string> warnings,
        double rowGapPaperMillimeters)
    {
        var items = selectedTags
            .Where(tag => measuredBounds.ContainsKey(tag.Id.Value))
            .Select(tag =>
            {
                LayoutRect bounds = measuredBounds[tag.Id.Value];
                LayoutPoint anchor = TryGetLeaderOrHostAnchor(
                                         tag,
                                         document,
                                         view,
                                         right,
                                         up) ??
                                     new LayoutPoint(
                                         (bounds.MinU + bounds.MaxU) * 0.5,
                                         (bounds.MinV + bounds.MaxV) * 0.5);
                return (Tag: tag, Bounds: bounds, Anchor: anchor);
            })
            .OrderByDescending(item => item.Anchor.V)
            .ThenBy(item => item.Anchor.U)
            .ToList();
        if (items.Count == 0)
            throw new InvalidOperationException("The collected tags have no measurable text bounds in this view.");

        double paperFoot = Math.Max(1, view.Scale) / 304.8;
        double rowGap = Math.Max(0.0, rowGapPaperMillimeters) * paperFoot;
        // The second rectangle is only a location gesture. Its centre becomes
        // the alignment rail/stack centre; its width and height never compress,
        // wrap, reject, or otherwise constrain the real tag-family rectangles.
        double averageHostU = items.Average(item => item.Anchor.U);
        double zoneCenterU = (targetZone.MinU + targetZone.MaxU) * 0.5;
        double zoneCenterV = (targetZone.MinV + targetZone.MaxV) * 0.5;
        bool zoneIsRightOfHosts = zoneCenterU >= averageHostU;
        var targetCenters = new Dictionary<long, LayoutPoint>();
        double totalHeight = items.Sum(item => item.Bounds.Height) +
                             rowGap * Math.Max(0, items.Count - 1);
        double top = zoneCenterV + totalHeight * 0.5;
        foreach ((IndependentTag tag, LayoutRect bounds, _) in items)
        {
            double centerU = zoneIsRightOfHosts
                ? zoneCenterU + bounds.Width * 0.5
                : zoneCenterU - bounds.Width * 0.5;
            double centerV = top - bounds.Height * 0.5;
            targetCenters[tag.Id.Value] = new LayoutPoint(centerU, centerV);
            top -= bounds.Height + rowGap;
        }

        int arranged = 0;
        var arrangedIds = new List<ElementId>();
        using (var transaction = new Transaction(
                   document,
                   "FamilyMEP - Arrange Collected Tags At Picked Location"))
        {
            transaction.Start();
            foreach ((IndependentTag tag, LayoutRect bounds, _) in items)
            {
                if (tag.Pinned)
                {
                    warnings.Add($"Target tag {tag.Id.Value} is pinned and was skipped.");
                    continue;
                }
                if (!targetCenters.TryGetValue(tag.Id.Value, out LayoutPoint target))
                    continue;
                try
                {
                    double centerU = (bounds.MinU + bounds.MaxU) * 0.5;
                    double centerV = (bounds.MinV + bounds.MaxV) * 0.5;
                    tag.TagHeadPosition +=
                        right * (target.U - centerU) +
                        up * (target.V - centerV);
                    arranged++;
                    arrangedIds.Add(tag.Id);
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} zone placement: {FriendlyTagError(exception)}");
                }
            }
            document.Regenerate();

            // Match Revit's standard orthogonal presentation: horizontal from
            // each text row, then a 90-degree vertical leg at the host/end U.
            foreach ((IndependentTag tag, _, _) in items)
            {
                if (!arrangedIds.Contains(tag.Id) || !tag.HasLeader) continue;
                try
                {
                    foreach (Reference reference in tag.GetTaggedReferences())
                    {
                        try
                        {
                            LayoutPoint? end = TryGetLeaderRouteEnd(
                                tag,
                                reference,
                                document,
                                view,
                                right,
                                up);
                            if (end is null)
                            {
                                warnings.Add($"Tag {tag.Id.Value} leader end could not be resolved.");
                                continue;
                            }
                            LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                            XYZ elbow = tag.TagHeadPosition + right * (end.Value.U - head.U);
                            tag.SetLeaderElbow(reference, elbow);
                        }
                        catch (Exception exception)
                        {
                            warnings.Add($"Tag {tag.Id.Value} orthogonal leader: {FriendlyTagError(exception)}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tag {tag.Id.Value} leader references: {FriendlyTagError(exception)}");
                }
            }
            document.Regenerate();
            transaction.Commit();
        }

        if (arrangedIds.Count > 0)
            uidoc.Selection.SetElementIds(arrangedIds);
        uidoc.RefreshActiveView();
        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == view.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        WaitForActualTagPreviewPaint(uidoc, application.MainWindowHandle, Math.Max(1, arranged));
        var window = uiView.GetWindowRectangle();
        byte[] image = CaptureViewport(
            application.MainWindowHandle,
            window.Left,
            window.Top,
            Math.Max(1, window.Right - window.Left),
            Math.Max(1, window.Bottom - window.Top));
        return new SmartTagManualAlignResult(
            arranged,
            selectedTags.Count - arranged,
            warnings,
            image);
    }

    private static (
        List<IndependentTag> Tags,
        Dictionary<long, LayoutRect> TextBounds) CollectTagsTouchedByRectangle(
        Document document,
        View view,
        LayoutRect sourceZone,
        XYZ right,
        XYZ up,
        List<string> warnings,
        long excludedTagId = 0)
    {
        List<IndependentTag> visibleTags = new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>()
            .Where(tag => tag.Id.Value != excludedTagId && !tag.IsHidden(view))
            .ToList();
        var fullBounds = new Dictionary<long, LayoutRect>();
        var leaderSegments = new Dictionary<long, List<LayoutSegment>>();
        foreach (IndependentTag tag in visibleTags)
        {
            try
            {
                BoundingBoxXYZ? fullBox = tag.get_BoundingBox(view);
                if (fullBox is not null)
                    fullBounds[tag.Id.Value] = ProjectBox(fullBox, right, up);
            }
            catch
            {
                // Exact paths may still identify the tag.
            }

            var segments = new List<LayoutSegment>();
            if (tag.HasLeader)
            {
                try
                {
                    LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                    foreach (Reference reference in tag.GetTaggedReferences())
                    {
                        LayoutPoint? end = TryGetSelectionLeaderRouteEnd(
                            tag,
                            reference,
                            document,
                            view,
                            right,
                            up);
                        if (end is null) continue;
                        try
                        {
                            LayoutPoint elbow = Project(tag.GetLeaderElbow(reference), right, up);
                            segments.Add(new LayoutSegment(head, elbow));
                            segments.Add(new LayoutSegment(elbow, end.Value));
                        }
                        catch
                        {
                            segments.Add(new LayoutSegment(head, end.Value));
                        }
                    }
                }
                catch
                {
                    // Full bounds remain the fallback for unusual tag types.
                }
            }
            leaderSegments[tag.Id.Value] = segments;
        }

        List<IndependentTag> candidates = visibleTags
            .Where(tag =>
            {
                long id = tag.Id.Value;
                bool pathHit = leaderSegments.TryGetValue(id, out List<LayoutSegment>? paths) &&
                               paths.Any(path => SegmentIntersectsRect(path, sourceZone));
                bool boxHit = fullBounds.TryGetValue(id, out LayoutRect full) &&
                              RectanglesTouch(full, sourceZone);
                return pathHit || boxHit;
            })
            .ToList();
        Dictionary<long, LayoutRect> textBounds = MeasureTagTextBounds(
            document,
            view,
            candidates,
            right,
            up,
            warnings,
            "FamilyMEP - Measure Crossing-Selected Tags");
        List<IndependentTag> selected = candidates
            .Where(tag =>
            {
                long id = tag.Id.Value;
                bool textHit = textBounds.TryGetValue(id, out LayoutRect text) &&
                               RectanglesTouch(text, sourceZone);
                bool leaderHit = leaderSegments.TryGetValue(id, out List<LayoutSegment>? paths) &&
                                 paths.Any(path => SegmentIntersectsRect(path, sourceZone));
                bool fallbackHit = !leaderHit &&
                                   (paths is null || paths.Count == 0) &&
                                   fullBounds.TryGetValue(id, out LayoutRect full) &&
                                   RectanglesTouch(full, sourceZone);
                return textHit || leaderHit || fallbackHit;
            })
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToList();
        return (selected, textBounds);
    }

    private static Dictionary<long, LayoutRect> MeasureTagTextBounds(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        List<string> warnings,
        string transactionName,
        bool forceHorizontal = false)
    {
        var result = new Dictionary<long, LayoutRect>();
        if (tags.Count == 0) return result;
        using var measurement = new Transaction(document, transactionName);
        measurement.Start();
        var measurementTags = new Dictionary<long, IndependentTag>();
        foreach (IndependentTag tag in tags)
        {
            if (TryCreateLeaderlessMeasurementTag(
                    document, view, tag, forceHorizontal, out IndependentTag? proxy))
            {
                measurementTags[tag.Id.Value] = proxy!;
                continue;
            }
            measurementTags[tag.Id.Value] = tag;
            if (forceHorizontal)
            {
                try { tag.TagOrientation = TagOrientation.Horizontal; }
                catch
                {
                    // The original orientation remains available as fallback.
                }
            }
            try
            {
                if (tag.Pinned) tag.Pinned = false;
                if (tag.HasLeader) tag.HasLeader = false;
            }
            catch
            {
                // Rollback still preserves the original tag state.
            }
        }
        document.Regenerate();
        foreach (IndependentTag tag in tags)
        {
            try
            {
                IndependentTag measuredTag = measurementTags[tag.Id.Value];
                BoundingBoxXYZ? box = measuredTag.get_BoundingBox(view);
                if (box is not null)
                {
                    LayoutRect measuredBounds = ProjectBox(box, right, up);
                    result[tag.Id.Value] = measuredTag.Id == tag.Id
                        ? measuredBounds
                        : AnchorLeaderlessBodyToVisibleTag(
                            tag,
                            measuredBounds,
                            document,
                            view,
                            right,
                            up);
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Tag {tag.Id.Value} text measurement: {FriendlyTagError(exception)}");
            }
        }
        measurement.RollBack();
        return result;
    }

    private static Dictionary<long, LayoutRect> MeasureTagTextBoundsInOpenTransaction(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        List<string> warnings)
    {
        var result = new Dictionary<long, LayoutRect>();
        if (tags.Count == 0) return result;
        using var measurement = new SubTransaction(document);
        measurement.Start();
        var measurementTags = new Dictionary<long, IndependentTag>();
        foreach (IndependentTag tag in tags)
        {
            if (TryCreateLeaderlessMeasurementTag(
                    document, view, tag, forceHorizontal: false, out IndependentTag? proxy))
            {
                measurementTags[tag.Id.Value] = proxy!;
                continue;
            }
            measurementTags[tag.Id.Value] = tag;
            try
            {
                if (tag.Pinned) tag.Pinned = false;
                if (tag.HasLeader) tag.HasLeader = false;
            }
            catch
            {
                // The subtransaction rollback restores the live tag state.
            }
        }
        document.Regenerate();
        foreach (IndependentTag tag in tags)
        {
            try
            {
                IndependentTag measuredTag = measurementTags[tag.Id.Value];
                BoundingBoxXYZ? box = measuredTag.get_BoundingBox(view);
                if (box is not null)
                {
                    LayoutRect measuredBounds = ProjectBox(box, right, up);
                    result[tag.Id.Value] = measuredTag.Id == tag.Id
                        ? measuredBounds
                        : AnchorLeaderlessBodyToVisibleTag(
                            tag,
                            measuredBounds,
                            document,
                            view,
                            right,
                            up);
                }
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {tag.Id.Value} final text measurement: " +
                    FriendlyTagError(exception));
            }
        }
        measurement.RollBack();
        return result;
    }

    private static LayoutRect AnchorLeaderlessBodyToVisibleTag(
        IndependentTag source,
        LayoutRect leaderlessBody,
        Document document,
        View view,
        XYZ right,
        XYZ up)
    {
        if (!source.HasLeader) return leaderlessBody;
        try
        {
            BoundingBoxXYZ? fullBox = source.get_BoundingBox(view);
            LayoutPoint? leaderEnd = TryGetLeaderOrHostAnchor(
                source,
                document,
                view,
                right,
                up);
            if (fullBox is null || leaderEnd is null) return leaderlessBody;

            LayoutRect fullBounds = ProjectBox(fullBox, right, up);
            LayoutPoint insertion = Project(source.TagHeadPosition, right, up);
            return SmartTagStackRouting.AnchorLeaderlessBodyToLiveBounds(
                leaderlessBody,
                fullBounds,
                insertion,
                leaderEnd.Value);
        }
        catch
        {
            return leaderlessBody;
        }
    }

    private static bool TryCreateLeaderlessMeasurementTag(
        Document document,
        View view,
        IndependentTag source,
        bool forceHorizontal,
        out IndependentTag? proxy)
    {
        proxy = null;
        try
        {
            Reference? reference = source.GetTaggedReferences().FirstOrDefault();
            ElementId typeId = source.GetTypeId();
            if (reference is null || typeId == ElementId.InvalidElementId) return false;
            TagOrientation orientation = forceHorizontal
                ? TagOrientation.Horizontal
                : source.TagOrientation;
            proxy = IndependentTag.Create(
                document,
                typeId,
                view.Id,
                reference,
                false,
                orientation,
                source.TagHeadPosition);
            proxy.TagHeadPosition = source.TagHeadPosition;
            if (forceHorizontal)
            {
                try { proxy.TagOrientation = TagOrientation.Horizontal; } catch { }
            }
            try { proxy.HasLeader = false; } catch { }
            return true;
        }
        catch
        {
            proxy = null;
            return false;
        }
    }

    private static void NormalizeManualEdgeFromRealBounds(
        Document document,
        View view,
        IndependentTag referenceTag,
        IReadOnlyList<IndependentTag> targetTags,
        bool alignLeftEdge,
        XYZ right,
        XYZ up,
        List<string> warnings)
    {
        List<IndependentTag> distinctTargets = targetTags
            .Where(tag => tag.Id != referenceTag.Id && !tag.Pinned)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToList();
        if (distinctTargets.Count == 0) return;
        Dictionary<long, LayoutRect> bounds = MeasureTagTextBoundsInOpenTransaction(
            document,
            view,
            distinctTargets.Prepend(referenceTag).ToArray(),
            right,
            up,
            warnings);
        NormalizeAutoBodyBounds(
            distinctTargets.Prepend(referenceTag).ToArray(),
            bounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: false);
        if (!bounds.TryGetValue(referenceTag.Id.Value, out LayoutRect referenceBounds))
        {
            warnings.Add("Final reference text bounds could not be measured; initial edge alignment was kept.");
            return;
        }

        LayoutPoint referenceInsertion = Project(referenceTag.TagHeadPosition, right, up);
        LayoutPoint referenceAnchor = TryGetLeaderOrHostAnchor(
            referenceTag,
            document,
            view,
            right,
            up) ?? new LayoutPoint(
                (referenceBounds.MinU + referenceBounds.MaxU) * 0.5,
                (referenceBounds.MinV + referenceBounds.MaxV) * 0.5);
        double referenceEdge = alignLeftEdge
            ? SmartTagStackRouting.GetVisibleLeftEdge(
                referenceBounds,
                referenceInsertion,
                referenceAnchor,
                referenceTag.HasLeader)
            : SmartTagStackRouting.GetVisibleRightEdge(
                referenceBounds,
                referenceInsertion,
                referenceAnchor,
                referenceTag.HasLeader);
        foreach (IndependentTag tag in distinctTargets)
        {
            if (!bounds.TryGetValue(tag.Id.Value, out LayoutRect tagBounds)) continue;
            try
            {
                LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
                LayoutPoint anchor = TryGetLeaderOrHostAnchor(
                    tag,
                    document,
                    view,
                    right,
                    up) ?? new LayoutPoint(
                        (tagBounds.MinU + tagBounds.MaxU) * 0.5,
                        (tagBounds.MinV + tagBounds.MaxV) * 0.5);
                double tagEdge = alignLeftEdge
                    ? SmartTagStackRouting.GetVisibleLeftEdge(
                        tagBounds, insertion, anchor, tag.HasLeader)
                    : SmartTagStackRouting.GetVisibleRightEdge(
                        tagBounds, insertion, anchor, tag.HasLeader);
                tag.TagHeadPosition += right * (referenceEdge - tagEdge);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {tag.Id.Value} final edge normalization: " +
                    FriendlyTagError(exception));
            }
        }
    }

    private static void NormalizeManualColumnFromRealBounds(
        Document document,
        View view,
        IndependentTag referenceTag,
        IReadOnlyList<IndependentTag> targetTags,
        bool alignLeftEdge,
        bool placeAllBelow,
        double visibleGap,
        XYZ right,
        XYZ up,
        List<string> warnings,
        bool placeAllAbove = false,
        bool normalizeAutoBodyBounds = false)
    {
        List<IndependentTag> distinctTargets = targetTags
            .Where(tag => tag.Id != referenceTag.Id && !tag.Pinned)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToList();
        if (distinctTargets.Count == 0) return;

        List<IndependentTag> measurementTags = distinctTargets
            .Prepend(referenceTag)
            .ToList();
        Dictionary<long, LayoutRect> bounds = MeasureTagTextBoundsInOpenTransaction(
            document,
            view,
            measurementTags,
            right,
            up,
            warnings);
        if (normalizeAutoBodyBounds)
            NormalizeAutoBodyBounds(
                measurementTags,
                bounds,
                view,
                right,
                up,
                warnings,
                reportAdjustments: true);
        if (!bounds.TryGetValue(referenceTag.Id.Value, out LayoutRect referenceBounds))
        {
            warnings.Add("Final reference text bounds could not be measured; initial alignment was kept.");
            return;
        }

        LayoutPoint referenceInsertion = Project(referenceTag.TagHeadPosition, right, up);
        LayoutPoint referenceAnchor = TryGetLeaderOrHostAnchor(
            referenceTag,
            document,
            view,
            right,
            up) ?? new LayoutPoint(
                (referenceBounds.MinU + referenceBounds.MaxU) * 0.5,
                (referenceBounds.MinV + referenceBounds.MaxV) * 0.5);
        double referenceEdge = normalizeAutoBodyBounds && alignLeftEdge
            ? SmartTagStackRouting.GetVisibleLeftEdge(
                referenceBounds,
                referenceInsertion,
                referenceAnchor,
                referenceTag.HasLeader)
            : alignLeftEdge
                ? referenceBounds.MinU
                : referenceBounds.MaxU;
        if (placeAllBelow)
        {
            double boundary = referenceBounds.MinV - visibleGap;
            foreach (IndependentTag tag in distinctTargets)
            {
                if (!bounds.TryGetValue(tag.Id.Value, out LayoutRect tagBounds)) continue;
                double desiredCenterV = boundary - tagBounds.Height * 0.5;
                if (Move(tag, tagBounds, desiredCenterV))
                    boundary = desiredCenterV - tagBounds.Height * 0.5 - visibleGap;
            }
            return;
        }
        if (placeAllAbove)
        {
            double boundary = referenceBounds.MaxV + visibleGap;
            foreach (IndependentTag tag in distinctTargets)
            {
                if (!bounds.TryGetValue(tag.Id.Value, out LayoutRect tagBounds)) continue;
                double desiredCenterV = boundary + tagBounds.Height * 0.5;
                if (Move(tag, tagBounds, desiredCenterV))
                    boundary = desiredCenterV + tagBounds.Height * 0.5 + visibleGap;
            }
            return;
        }

        var rows = distinctTargets
            .Where(tag => bounds.ContainsKey(tag.Id.Value))
            .Select(tag => (Tag: tag, Bounds: bounds[tag.Id.Value]))
            .Append((Tag: referenceTag, Bounds: referenceBounds))
            .OrderByDescending(item => (item.Bounds.MinV + item.Bounds.MaxV) * 0.5)
            .ToList();
        int referenceIndex = rows.FindIndex(item => item.Tag.Id == referenceTag.Id);
        double upperBoundary = referenceBounds.MaxV + visibleGap;
        for (int index = referenceIndex - 1; index >= 0; index--)
        {
            (IndependentTag tag, LayoutRect tagBounds) = rows[index];
            double desiredCenterV = upperBoundary + tagBounds.Height * 0.5;
            Move(tag, tagBounds, desiredCenterV);
            upperBoundary = desiredCenterV + tagBounds.Height * 0.5 + visibleGap;
        }
        double lowerBoundary = referenceBounds.MinV - visibleGap;
        for (int index = referenceIndex + 1; index < rows.Count; index++)
        {
            (IndependentTag tag, LayoutRect tagBounds) = rows[index];
            double desiredCenterV = lowerBoundary - tagBounds.Height * 0.5;
            Move(tag, tagBounds, desiredCenterV);
            lowerBoundary = desiredCenterV - tagBounds.Height * 0.5 - visibleGap;
        }

        bool Move(IndependentTag tag, LayoutRect tagBounds, double desiredCenterV)
        {
            try
            {
                double currentCenterV = (tagBounds.MinV + tagBounds.MaxV) * 0.5;
                double tagEdge;
                if (normalizeAutoBodyBounds && alignLeftEdge)
                {
                    LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
                    LayoutPoint anchor = TryGetLeaderOrHostAnchor(
                        tag,
                        document,
                        view,
                        right,
                        up) ?? new LayoutPoint(
                            (tagBounds.MinU + tagBounds.MaxU) * 0.5,
                            (tagBounds.MinV + tagBounds.MaxV) * 0.5);
                    tagEdge = SmartTagStackRouting.GetVisibleLeftEdge(
                        tagBounds,
                        insertion,
                        anchor,
                        tag.HasLeader);
                }
                else
                {
                    tagEdge = alignLeftEdge ? tagBounds.MinU : tagBounds.MaxU;
                }
                tag.TagHeadPosition +=
                    right * (referenceEdge - tagEdge) +
                    up * (desiredCenterV - currentCenterV);
                return true;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {tag.Id.Value} final column normalization: " +
                    FriendlyTagError(exception));
                return false;
            }
        }
    }

    private static void NormalizeAutoBodyBounds(
        IReadOnlyList<IndependentTag> tags,
        Dictionary<long, LayoutRect> bounds,
        View view,
        XYZ right,
        XYZ up,
        List<string> warnings,
        bool reportAdjustments)
    {
        int adjusted = 0;
        double tenMillimetres = 10.0 * Math.Max(1, view.Scale) / 304.8;
        double twentyFiveMillimetres = 25.0 * Math.Max(1, view.Scale) / 304.8;
        foreach (IndependentTag tag in tags)
        {
            LayoutPoint head = Project(tag.TagHeadPosition, right, up);
            string label;
            try { label = tag.TagText; }
            catch { label = string.Empty; }
            (double estimatedWidth, double estimatedHeight) =
                EstimateTagSize(label ?? string.Empty, view.Scale);
            double safeWidth = Math.Max(estimatedWidth, 1e-4);
            double safeHeight = Math.Max(estimatedHeight, 1e-4);

            if (!bounds.TryGetValue(tag.Id.Value, out LayoutRect measured))
            {
                bounds[tag.Id.Value] = new LayoutRect(
                    head.U - safeWidth * 0.5,
                    head.V - safeHeight * 0.5,
                    head.U + safeWidth * 0.5,
                    head.V + safeHeight * 0.5);
                adjusted++;
                continue;
            }

            LayoutRect rawMeasured = measured;
            measured = SmartTagStackRouting.TrimAsymmetricLeaderTail(
                measured,
                head,
                safeHeight);

            // Trust the real leader-off family box unless it is unmistakably a
            // stale leader-sized outlier. The former 2x estimate threshold cut
            // valid multi-line Duct tags down to one line and packed later rows
            // on top of each other. Also never accept a measured body shorter
            // than the displayed text estimate.
            double maximumPlausibleHeight = Math.Max(
                safeHeight * 4.0,
                twentyFiveMillimetres);
            double normalizedMinU = measured.MinU;
            double normalizedMaxU = measured.MaxU;
            double maximumPlausibleWidth = Math.Max(
                safeWidth * 2.25,
                safeWidth + tenMillimetres);
            if (measured.Width > maximumPlausibleWidth)
            {
                double leftSpan = Math.Max(0.0, head.U - measured.MinU);
                double rightSpan = Math.Max(0.0, measured.MaxU - head.U);
                if (leftSpan > rightSpan * 1.75)
                {
                    // A stale leader extends toward the host on the left. The
                    // right extreme still belongs to the visible tag body.
                    normalizedMaxU = measured.MaxU;
                    normalizedMinU = normalizedMaxU - safeWidth;
                }
                else if (rightSpan > leftSpan * 1.75)
                {
                    normalizedMinU = measured.MinU;
                    normalizedMaxU = normalizedMinU + safeWidth;
                }
                else
                {
                    normalizedMinU = head.U - safeWidth * 0.5;
                    normalizedMaxU = head.U + safeWidth * 0.5;
                }
            }

            double centerV;
            double normalizedHeight;
            if (measured.Height > maximumPlausibleHeight)
            {
                centerV = head.V;
                normalizedHeight = safeHeight;
            }
            else
            {
                centerV = (measured.MinV + measured.MaxV) * 0.5;
                normalizedHeight = Math.Max(measured.Height, safeHeight);
            }
            LayoutRect normalized = new(
                normalizedMinU,
                centerV - normalizedHeight * 0.5,
                normalizedMaxU,
                centerV + normalizedHeight * 0.5);
            bool boundsChanged =
                Math.Abs(normalized.MinU - rawMeasured.MinU) > 1e-8 ||
                Math.Abs(normalized.MinV - rawMeasured.MinV) > 1e-8 ||
                Math.Abs(normalized.MaxU - rawMeasured.MaxU) > 1e-8 ||
                Math.Abs(normalized.MaxV - rawMeasured.MaxV) > 1e-8;
            if (!boundsChanged) continue;
            bounds[tag.Id.Value] = normalized;
            adjusted++;
        }
        if (reportAdjustments && adjusted > 0)
        {
            warnings.Add(
                $"AUTO normalized {adjusted} abnormal/missing tag body bound(s) " +
                "before applying the uniform row gap.");
        }
    }

    private static void RouteSeparatedOrthogonalLeaders(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> orderedTags,
        XYZ right,
        XYZ up,
        List<string> warnings,
        IndependentTag? fixedTag = null,
        double minimumLaneClearance = 0.0)
    {
        var routes = new List<LeaderLaneRoute>();
        IndependentTag[] measurableTags = orderedTags
            .Append(fixedTag)
            .Where(tag => tag is not null && tag.HasLeader && !tag.Pinned)
            .Select(tag => tag!)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToArray();
        Dictionary<long, LayoutRect> bodyBounds = MeasureTagTextBoundsInOpenTransaction(
            document,
            view,
            measurableTags,
            right,
            up,
            warnings);
        NormalizeAutoBodyBounds(
            measurableTags,
            bodyBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: false);

        void CollectRoutes(IndependentTag tag, bool isFixed)
        {
            if (!tag.HasLeader) return;
            try
            {
                foreach (Reference reference in tag.GetTaggedReferences())
                {
                    LayoutPoint? end = TryGetLeaderRouteEnd(
                        tag,
                        reference,
                        document,
                        view,
                        right,
                        up);
                    if (end is null) continue;
                    LayoutPoint insertion = Project(tag.TagHeadPosition, right, up);
                    LayoutPoint attachment = bodyBounds.TryGetValue(
                        tag.Id.Value,
                        out LayoutRect bounds)
                            ? SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                                bounds,
                                end.Value)
                            : insertion;
                    routes.Add(new LeaderLaneRoute(
                        tag,
                        reference,
                        attachment,
                        end.Value,
                        TryGetReferenceHostWidth(reference, document, view, right, up),
                        isFixed));
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Tag {tag.Id.Value} leader references: {FriendlyTagError(exception)}");
            }
        }

        foreach (IndependentTag tag in orderedTags)
            CollectRoutes(tag, isFixed: false);
        if (fixedTag is not null && orderedTags.All(tag => tag.Id != fixedTag.Id))
            CollectRoutes(fixedTag, isFixed: true);

        double baseLaneStep = 0.7 * Math.Max(1, view.Scale) / 304.8;
        double laneStep = Math.Max(baseLaneStep, minimumLaneClearance * 1.25);
        double tolerance = Math.Max(
            Math.Max(1e-6, baseLaneStep * 0.35),
            minimumLaneClearance);
        var groups = new List<List<LeaderLaneRoute>>();
        foreach (var route in routes.OrderBy(item => item.End.U))
        {
            var group = groups.LastOrDefault(candidate =>
                Math.Abs(candidate.Average(item => item.End.U) - route.End.U) <= tolerance);
            if (group is null)
            {
                group = [];
                groups.Add(group);
            }
            group.Add(route);
        }

        foreach (var group in groups)
        {
            List<LeaderLaneRoute> fixedRoutes = group.Where(item => item.IsFixed).ToList();
            if (fixedRoutes.Count > 0)
            {
                // The sample tag must not move, including its accepted leader
                // endpoint. Reserve that U lane and move only target endpoints
                // to the nearest free lane. This resolves the otherwise
                // unavoidable collinear tails around a shared DA/AT/Duct host.
                double laneOrigin = fixedRoutes.Average(item => item.End.U);
                List<LeaderLaneRoute> movableRoutes = group
                    .Where(item => !item.IsFixed)
                    .ToList();
                if (movableRoutes.Count == 0)
                {
                    // A lane group containing only the fixed sample is a valid
                    // reservation, not an assignment problem. Leave it exactly
                    // untouched and continue with the target groups.
                    continue;
                }
                List<double> fixedLaneWidths = group
                    .Where(item => item.HostWidth > 1e-6)
                    .Select(item => item.HostWidth)
                    .ToList();
                int maximumLevel = Math.Max(1, (movableRoutes.Count + 1) / 2);
                double maximumOffset = fixedLaneWidths.Count == 0
                    ? laneStep * maximumLevel
                    : fixedLaneWidths.Min() * 0.45;
                double fixedLaneSeparation = Math.Min(
                    laneStep,
                    maximumOffset / maximumLevel);
                fixedLaneSeparation = Math.Max(fixedLaneSeparation, 1e-8);
                double requiredClearance = Math.Min(
                    fixedLaneSeparation * 0.85,
                    Math.Max(minimumLaneClearance, 1e-8));
                var occupied = fixedRoutes.Select(item => item.End.U).ToList();
                var fixedCandidateLanes = new List<double>();
                foreach (double candidate in Enumerable
                             .Range(1, movableRoutes.Count + fixedRoutes.Count + 4)
                             .SelectMany(level => new[]
                             {
                                 laneOrigin - level * fixedLaneSeparation,
                                 laneOrigin + level * fixedLaneSeparation
                             })
                             .OrderBy(value => Math.Abs(value - laneOrigin))
                             .ThenBy(value => value))
                {
                    if (occupied.Any(existing =>
                            Math.Abs(existing - candidate) < requiredClearance) ||
                        fixedCandidateLanes.Any(existing =>
                            Math.Abs(existing - candidate) < requiredClearance))
                        continue;
                    fixedCandidateLanes.Add(candidate);
                    if (fixedCandidateLanes.Count == movableRoutes.Count) break;
                }
                while (fixedCandidateLanes.Count < movableRoutes.Count)
                {
                    int level = fixedCandidateLanes.Count + fixedRoutes.Count + 1;
                    fixedCandidateLanes.Add(laneOrigin - level * fixedLaneSeparation);
                }
                IReadOnlyList<double> fixedAssignment = ChooseLaneAssignment(
                    movableRoutes,
                    fixedCandidateLanes,
                    routes.Where(route => !movableRoutes.Contains(route)).ToArray(),
                    requiredClearance);
                for (int index = 0; index < movableRoutes.Count; index++)
                {
                    LeaderLaneRoute route = movableRoutes[index];
                    double appliedU = ApplyRoute(route, fixedAssignment[index], moveEnd: true);
                    RememberAppliedEnd(route, appliedU);
                }
                // The picked sample is a reservation only. AUTO must never
                // rewrite its elbow or endpoint while arranging target tags.
                continue;
            }

            double centerU = group.Average(item => item.End.U);
            double desiredSpan = laneStep * Math.Max(0, group.Count - 1);
            List<double> widths = group
                .Where(item => item.HostWidth > 1e-6)
                .Select(item => item.HostWidth)
                .ToList();
            double span = Math.Min(
                desiredSpan,
                widths.Count == 0 ? desiredSpan : widths.Min() * 0.65);
            double separation = group.Count <= 1 ? 0.0 : span / (group.Count - 1);
            double[] candidateLanes = group.Count <= 1
                ? [group[0].End.U]
                : Enumerable.Range(0, group.Count)
                    .Select(index => centerU - span * 0.5 + separation * index)
                    .ToArray();
            IReadOnlyList<double> assignment = ChooseLaneAssignment(
                group,
                candidateLanes,
                routes.Where(route => !group.Contains(route)).ToArray(),
                tolerance);
            for (int index = 0; index < group.Count; index++)
            {
                LeaderLaneRoute route = group[index];
                double appliedU = ApplyRoute(
                    route,
                    assignment[index],
                    moveEnd: group.Count > 1);
                RememberAppliedEnd(route, appliedU);
            }
        }

        static IReadOnlyList<double> ChooseLaneAssignment(
            IReadOnlyList<LeaderLaneRoute> movable,
            IReadOnlyList<double> candidateLanes,
            IReadOnlyList<LeaderLaneRoute> fixedRoutes,
            double clearance)
        {
            SmartTagStackRouteInput Convert(LeaderLaneRoute route) =>
                new(
                    route.Tag.Id.Value,
                    new LayoutRect(route.Head.U, route.Head.V, route.Head.U, route.Head.V),
                    route.Head,
                    route.End);
            return SmartTagStackRouting.FindBestLaneAssignment(
                movable.Select(Convert).ToArray(),
                candidateLanes,
                fixedRoutes.Select(Convert).ToArray(),
                Math.Max(clearance, 1e-8));
        }

        void RememberAppliedEnd(LeaderLaneRoute route, double appliedU)
        {
            int routeIndex = routes.IndexOf(route);
            if (routeIndex < 0) return;
            routes[routeIndex] = route with
            {
                End = new LayoutPoint(appliedU, route.End.V)
            };
        }

        double ApplyRoute(LeaderLaneRoute route, double targetU, bool moveEnd)
        {
            double appliedU = route.End.U;
            if (moveEnd)
            {
                try
                {
                    if (route.Tag.LeaderEndCondition != LeaderEndCondition.Free)
                        route.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                    XYZ shiftedEnd = MoveInViewPlane(
                        route.Tag.TagHeadPosition,
                        new LayoutPoint(targetU, route.End.V),
                        right,
                        up);
                    route.Tag.SetLeaderEnd(route.Reference, shiftedEnd);
                    appliedU = targetU;
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Tag {route.Tag.Id.Value} leader endpoint separation: " +
                        FriendlyTagError(exception));
                }
            }
            try
            {
                XYZ elbow = MoveInViewPlane(
                    route.Tag.TagHeadPosition,
                    new LayoutPoint(appliedU, route.Head.V),
                    right,
                    up);
                route.Tag.SetLeaderElbow(route.Reference, elbow);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {route.Tag.Id.Value} orthogonal leader: " +
                    FriendlyTagError(exception));
            }
            return appliedU;
        }
    }

    private static void RouteOrthogonalLeadersKeepingEnds(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        List<string> warnings)
    {
        IndependentTag[] measurableTags = tags
            .Where(tag => tag.HasLeader && !tag.Pinned)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToArray();
        Dictionary<long, LayoutRect> bodyBounds = MeasureTagTextBoundsInOpenTransaction(
            document,
            view,
            measurableTags,
            right,
            up,
            warnings);
        NormalizeAutoBodyBounds(
            measurableTags,
            bodyBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: false);
        foreach (IndependentTag tag in tags)
        {
            if (!tag.HasLeader) continue;
            try
            {
                foreach (Reference reference in tag.GetTaggedReferences())
                {
                    LayoutPoint? end = TryGetLeaderRouteEnd(
                        tag,
                        reference,
                        document,
                        view,
                        right,
                        up);
                    if (end is null) continue;
                    LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                    double shoulderV = bodyBounds.TryGetValue(
                        tag.Id.Value,
                        out LayoutRect bounds)
                            ? (bounds.MinV + bounds.MaxV) * 0.5
                            : head.V;
                    XYZ elbow = MoveInViewPlane(
                        tag.TagHeadPosition,
                        new LayoutPoint(end.Value.U, shoulderV),
                        right,
                        up);
                    tag.SetLeaderElbow(reference, elbow);
                }
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {tag.Id.Value} final orthogonal leader: " +
                    FriendlyTagError(exception));
            }
        }
    }

    private static void RouteLeadersPreferStraight(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        List<string> warnings,
        IndependentTag? fixedTag = null)
    {
        IndependentTag[] distinctTags = tags
            .Where(tag => tag.HasLeader && !tag.Pinned)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToArray();
        if (distinctTags.Length == 0) return;

        IndependentTag[] measuredTags = distinctTags
            .Append(fixedTag)
            .Where(tag => tag is not null && tag.HasLeader)
            .Select(tag => tag!)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToArray();

        Dictionary<long, LayoutRect> bodyBounds = MeasureTagTextBoundsInOpenTransaction(
            document,
            view,
            measuredTags,
            right,
            up,
            warnings);
        NormalizeAutoBodyBounds(
            measuredTags,
            bodyBounds,
            view,
            right,
            up,
            warnings,
            reportAdjustments: false);

        var routes = new List<(IndependentTag Tag, Reference Reference,
            LayoutPoint Attachment, LayoutPoint End, bool IsFixed)>();
        foreach (IndependentTag tag in measuredTags)
        {
            try
            {
                LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                foreach (Reference reference in tag.GetTaggedReferences())
                {
                    LayoutPoint? end = TryGetLeaderRouteEnd(
                        tag,
                        reference,
                        document,
                        view,
                        right,
                        up);
                    if (end is not null)
                    {
                        LayoutPoint attachment = bodyBounds.TryGetValue(
                            tag.Id.Value,
                            out LayoutRect bounds)
                                ? SmartTagStackRouting.GetVisibleLeaderAttachmentPoint(
                                    bounds,
                                    end.Value)
                                : head;
                        routes.Add((
                            tag,
                            reference,
                            attachment,
                            end.Value,
                            fixedTag is not null && tag.Id == fixedTag.Id));
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {tag.Id.Value} straight/orthogonal leader analysis: " +
                    FriendlyTagError(exception));
            }
        }
        if (routes.Count == 0) return;

        double axisTolerance = Math.Max(
            1e-7,
            0.20 * Math.Max(1, view.Scale) / 304.8);
        double bodyClearance = Math.Max(
            1e-8,
            0.10 * Math.Max(1, view.Scale) / 304.8);
        var acceptedPaths = routes
            .Select(route => BuildOrthogonalPath(route.Attachment, route.End))
            .ToList();

        for (int index = 0; index < routes.Count; index++)
        {
            var route = routes[index];
            if (route.IsFixed) continue;
            var exactHorizontalEnd = new LayoutPoint(
                route.End.U,
                route.Attachment.V);
            var straight = new LayoutSegment(route.Attachment, exactHorizontalEnd);
            bool sameHorizontalRow =
                Math.Abs(route.Attachment.V - route.End.V) <= axisTolerance;
            bool crossesOtherBody = sameHorizontalRow && bodyBounds.Any(item =>
                item.Key != route.Tag.Id.Value &&
                SegmentIntersectsRect(straight, item.Value.Expand(bodyClearance)));
            bool crossesOtherLeader = sameHorizontalRow && acceptedPaths
                .Where((_, otherIndex) => otherIndex != index)
                .SelectMany(path => path)
                .Any(segment => SegmentsIntersect(straight, segment));
            bool useStraight = SmartTagStackRouting.ShouldUseStraightHorizontalLeader(
                route.Attachment,
                route.End,
                axisTolerance,
                crossesOtherBody,
                crossesOtherLeader);

            acceptedPaths[index] = useStraight
                ? [straight]
                : BuildOrthogonalPath(route.Attachment, route.End);
            try
            {
                if (route.Tag.LeaderEndCondition != LeaderEndCondition.Free)
                    route.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                LayoutPoint appliedEnd = useStraight
                    ? exactHorizontalEnd
                    : route.End;
                XYZ endWorld = MoveInViewPlane(
                    route.Tag.TagHeadPosition,
                    appliedEnd,
                    right,
                    up);
                route.Tag.SetLeaderEnd(route.Reference, endWorld);

                LayoutPoint elbow = new(
                    route.End.U,
                    route.Attachment.V);
                XYZ elbowWorld = MoveInViewPlane(
                    route.Tag.TagHeadPosition,
                    elbow,
                    right,
                    up);
                route.Tag.SetLeaderElbow(route.Reference, elbowWorld);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Tag {route.Tag.Id.Value} straight/orthogonal leader: " +
                    FriendlyTagError(exception));
            }
        }

        static List<LayoutSegment> BuildOrthogonalPath(
            LayoutPoint head,
            LayoutPoint end)
        {
            var elbow = new LayoutPoint(end.U, head.V);
            var path = new List<LayoutSegment>
            {
                new(head, elbow)
            };
            if (Math.Abs(end.V - head.V) > 1e-8)
                path.Add(new LayoutSegment(elbow, end));
            return path;
        }
    }

    private static void StabilizeAutoOrthogonalLeaders(
        Document document,
        View view,
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        List<string> warnings)
    {
        IndependentTag[] distinctTags = tags
            .Where(tag => tag.HasLeader && !tag.Pinned)
            .GroupBy(tag => tag.Id.Value)
            .Select(group => group.First())
            .ToArray();
        if (distinctTags.Length == 0) return;

        double tolerance = Math.Max(
            1e-7,
            0.01 * Math.Max(1, view.Scale) / 304.8);
        var failedTags = new HashSet<long>();
        bool stable = false;
        for (int pass = 0; pass < 6; pass++)
        {
            failedTags.Clear();
            foreach (IndependentTag tag in distinctTags)
            {
                try { tag.TagOrientation = TagOrientation.Horizontal; }
                catch { failedTags.Add(tag.Id.Value); }
            }
            document.Regenerate();

            Dictionary<long, LayoutRect> bodyBounds =
                MeasureTagTextBoundsInOpenTransaction(
                    document,
                    view,
                    distinctTags,
                    right,
                    up,
                    warnings);
            NormalizeAutoBodyBounds(
                distinctTags,
                bodyBounds,
                view,
                right,
                up,
                warnings,
                reportAdjustments: false);

            foreach (IndependentTag tag in distinctTags)
            {
                try
                {
                    LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                    double shoulderV = bodyBounds.TryGetValue(
                        tag.Id.Value,
                        out LayoutRect bounds)
                            ? (bounds.MinV + bounds.MaxV) * 0.5
                            : head.V;
                    foreach (Reference reference in tag.GetTaggedReferences())
                    {
                        LayoutPoint? projectedEnd = TryGetLeaderRouteEnd(
                            tag, reference, document, view, right, up);
                        if (projectedEnd is null)
                        {
                            failedTags.Add(tag.Id.Value);
                            continue;
                        }
                        try { tag.LeaderEndCondition = LeaderEndCondition.Free; }
                        catch { }
                        XYZ planeOrigin = tag.TagHeadPosition;
                        XYZ exactEnd = MoveInViewPlane(
                            planeOrigin, projectedEnd.Value, right, up);
                        XYZ exactElbow = MoveInViewPlane(
                            planeOrigin,
                            new LayoutPoint(projectedEnd.Value.U, shoulderV),
                            right,
                            up);
                        tag.SetLeaderEnd(reference, exactEnd);
                        tag.SetLeaderElbow(reference, exactElbow);
                    }
                }
                catch
                {
                    failedTags.Add(tag.Id.Value);
                }
            }
            document.Regenerate();

            failedTags.UnionWith(GetNonOrthogonalAutoLeaderTagIds(
                distinctTags,
                right,
                up,
                tolerance,
                bodyBounds));
            stable = failedTags.Count == 0;
            if (stable) break;
        }

        if (!stable || failedTags.Count > 0)
        {
            warnings.Add(
                $"AUTO orthogonal verification: {failedTags.Count} tag(s) " +
                "could not be fully verified after regeneration.");
        }
    }

    private static HashSet<long> GetNonOrthogonalAutoLeaderTagIds(
        IReadOnlyList<IndependentTag> tags,
        XYZ right,
        XYZ up,
        double tolerance,
        IReadOnlyDictionary<long, LayoutRect>? bodyBounds = null)
    {
        var failed = new HashSet<long>();
        foreach (IndependentTag tag in tags)
        {
            if (!tag.HasLeader || tag.Pinned) continue;
            try
            {
                LayoutPoint head = Project(tag.TagHeadPosition, right, up);
                double shoulderV = bodyBounds is not null && bodyBounds.TryGetValue(
                    tag.Id.Value,
                    out LayoutRect bounds)
                        ? (bounds.MinV + bounds.MaxV) * 0.5
                        : head.V;
                foreach (Reference reference in tag.GetTaggedReferences())
                {
                    LayoutPoint end = Project(tag.GetLeaderEnd(reference), right, up);
                    LayoutPoint elbow;
                    try
                    {
                        elbow = Project(tag.GetLeaderElbow(reference), right, up);
                    }
                    catch
                    {
                        // No elbow is valid only when Revit kept one exact
                        // horizontal segment from the text head to the host.
                        if (Math.Abs(end.V - head.V) <= tolerance ||
                            Math.Abs(end.V - shoulderV) <= tolerance)
                            continue;
                        failed.Add(tag.Id.Value);
                        continue;
                    }
                    bool collapsedStraight =
                        Math.Abs(end.V - head.V) <= tolerance &&
                        Math.Abs(elbow.U - head.U) <= tolerance &&
                        Math.Abs(elbow.V - head.V) <= tolerance;
                    bool orthogonalAtHead =
                        Math.Abs(elbow.V - head.V) <= tolerance &&
                        Math.Abs(elbow.U - end.U) <= tolerance;
                    bool orthogonalAtMeasuredBody =
                        Math.Abs(elbow.V - shoulderV) <= tolerance &&
                        Math.Abs(elbow.U - end.U) <= tolerance;
                    if (collapsedStraight ||
                        orthogonalAtHead ||
                        orthogonalAtMeasuredBody)
                        continue;
                    failed.Add(tag.Id.Value);
                }
            }
            catch
            {
                failed.Add(tag.Id.Value);
            }
        }
        return failed;
    }

    private static LayoutRect ProjectPickedBox(PickedBox picked, XYZ right, XYZ up)
    {
        double firstU = picked.Min.DotProduct(right);
        double firstV = picked.Min.DotProduct(up);
        double secondU = picked.Max.DotProduct(right);
        double secondV = picked.Max.DotProduct(up);
        var result = new LayoutRect(
            Math.Min(firstU, secondU),
            Math.Min(firstV, secondV),
            Math.Max(firstU, secondU),
            Math.Max(firstV, secondV));
        if (result.Width <= 1e-6 || result.Height <= 1e-6)
            throw new InvalidOperationException("The picked rectangle has no usable width or height.");
        return result;
    }

    private static bool RectanglesTouch(LayoutRect first, LayoutRect second) =>
        first.MinU <= second.MaxU + 1e-8 && first.MaxU >= second.MinU - 1e-8 &&
        first.MinV <= second.MaxV + 1e-8 && first.MaxV >= second.MinV - 1e-8;

    public static SmartTagApplyResult Apply(
        UIApplication application,
        SmartTagViewSnapshot snapshot,
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyDictionary<string, long> tagTypeIds,
        SmartTagLayoutSettings settings,
        bool includeClashes = false,
        bool previewOnly = false,
        bool replayAnalyzed = false)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("No active Revit project.");
        Document document = uidoc.Document;
        if (document.ActiveView.Id.Value != snapshot.ViewId)
        {
            throw new InvalidOperationException(
                "The active view changed after preview. Return to the previewed view or refresh the preview.");
        }

        Dictionary<long, SmartTagRecord> records = snapshot.Tags.ToDictionary(item => item.TagKey);
        if (replayAnalyzed &&
            (previewOnly || settings.LayoutStyle is not (
                SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones)))
        {
            throw new InvalidOperationException(
                "Replay requires an analyzed Near Host or Guided plan.");
        }
        int created = 0;
        int arranged = 0;
        int skipped = 0;
        var warnings = new List<string>();
        var appliedTags = new List<AppliedTagWorkItem>();
        var applyClock = System.Diagnostics.Stopwatch.StartNew();
        using var transaction = new Transaction(document, "FamilyMEP - Smart Tag Layout");
        transaction.Start();
        foreach (TagLayoutPlacement placement in placements)
        {
            if ((!includeClashes && placement.HasClash) ||
                !records.TryGetValue(placement.TagKey, out SmartTagRecord? record))
            {
                skipped++;
                continue;
            }

            try
            {
                IndependentTag tag;
                Reference? reference;
                if (record.WillCreate)
                {
                    Element? element = document.GetElement(new ElementId(record.ElementId));
                    if (element is null)
                    {
                        skipped++;
                        warnings.Add($"Element {record.ElementId} no longer exists.");
                        continue;
                    }

                    reference = new Reference(element);
                    if (!tagTypeIds.TryGetValue(record.Layout.Group, out long tagTypeId) || tagTypeId <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Select a loaded Tag Family type for {record.CategoryName}.");
                    }
                    XYZ initialHead = MoveInViewPlane(
                        record.AnchorWorld,
                        placement.Head,
                        snapshot.RightDirection,
                        snapshot.UpDirection);
                    tag = IndependentTag.Create(
                        document,
                        new ElementId(tagTypeId),
                        document.ActiveView.Id,
                        reference,
                        false,
                        TagOrientation.Horizontal,
                        initialHead);
                    reference = tag.GetTaggedReferences().FirstOrDefault() ?? reference;
                }
                else
                {
                    if (document.GetElement(new ElementId(record.ExistingTagId)) is not IndependentTag existingTag)
                    {
                        skipped++;
                        continue;
                    }
                    if (existingTag.Pinned)
                    {
                        skipped++;
                        warnings.Add($"Tag {record.ExistingTagId} is pinned.");
                        continue;
                    }
                    tag = existingTag;
                    reference = tag.GetTaggedReferences().FirstOrDefault();
                    if (reference is null)
                    {
                        skipped++;
                        warnings.Add($"Tag {record.ExistingTagId} has no local tagged reference.");
                        continue;
                    }
                }

                tag.TagHeadPosition = MoveInViewPlane(
                    record.HeadWorld,
                    placement.Head,
                    snapshot.RightDirection,
                    snapshot.UpDirection);
                try { tag.HasLeader = false; } catch { }
                appliedTags.Add(new AppliedTagWorkItem(tag, reference, record, placement));
                if (record.WillCreate) created++;
                else arranged++;
            }
            catch (Exception exception)
            {
                skipped++;
                string target = record.WillCreate
                    ? $"Element {record.ElementId}"
                    : $"Tag {record.ExistingTagId}";
                warnings.Add($"{target}: {FriendlyTagError(exception)}");
            }
        }

        // Never let the temporary real-family preview silently shrink the
        // selected set. Previously a failed Air Terminal creation disappeared
        // from FinalPlacements, then Write replayed only the smaller list and
        // reported success with missing tags.
        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost &&
            (skipped != 0 || appliedTags.Count != placements.Count))
        {
            int missing = Math.Max(skipped, placements.Count - appliedTags.Count);
            warnings.Add(
                $"Near Host unsafe: {missing} selected tag(s) could not be created or measured; " +
                "the analyzed set is incomplete. Write blocked. " +
                "Check the selected project Tag Family and element references.");
        }

        // TagHeadPosition is a family insertion point, and different project tag
        // families do not necessarily place their visible left edge at the same
        // offset. Measure the real leader-free tag boxes in one regeneration and
        // correct only U so the written Revit tags match the preview rail.
        SmartTagViewSnapshot baselineSnapshot = snapshot with
        {
            Obstacles = snapshot.Obstacles
                .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Mep)
                .ToList()
        };
        if (!replayAnalyzed)
        {
        document.Regenerate();
        appliedTags = PackActualTagRows(
            document,
            settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost ? snapshot : baselineSnapshot,
            appliedTags,
            settings,
            warnings);
        if (!previewOnly && warnings.Any(w => w.StartsWith("Near Host unsafe:", StringComparison.Ordinal)))
            throw new InvalidOperationException("Near Host: unresolved clashes remain. Write cancelled; inspect the preview or scan a smaller group.");
        document.Regenerate();
        for (int index = 0; index < appliedTags.Count; index++)
        {
            AppliedTagWorkItem item = appliedTags[index];
            try
            {
                BoundingBoxXYZ? box = item.Tag.get_BoundingBox(document.ActiveView);
                if (box is null) continue;
                LayoutRect actualBounds = ProjectBox(
                    box,
                    snapshot.RightDirection,
                    snapshot.UpDirection);
                double shiftU = item.Placement.TagBounds.MinU - actualBounds.MinU;
                // Guided rows align the visible project-family text boxes, not
                // merely TagHeadPosition. Families can have different vertical
                // insertion offsets, so correct V as well after measuring the
                // real leader-free bounds. Other accepted layouts retain their
                // established vertical insertion-point behavior.
                double shiftV = settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones
                    ? item.Placement.TagBounds.MinV - actualBounds.MinV
                    : 0.0;
                if (Math.Abs(shiftU) > 1e-8 || Math.Abs(shiftV) > 1e-8)
                {
                    item.Tag.TagHeadPosition +=
                        snapshot.RightDirection * shiftU +
                        snapshot.UpDirection * shiftV;
                    var correctedHead = new LayoutPoint(
                        item.Placement.Head.U + shiftU,
                        item.Placement.Head.V + shiftV);
                    bool correctedElbow = Math.Abs(
                        correctedHead.V - item.Placement.End.V) > 1e-7;
                    TagLayoutPlacement corrected = item.Placement with
                    {
                        Head = correctedHead,
                        UsesElbow = correctedElbow,
                        Elbow = correctedElbow
                            ? new LayoutPoint(item.Placement.End.U, correctedHead.V)
                            : item.Placement.End
                    };
                    appliedTags[index] = item with { Placement = corrected };
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Tag {item.Tag.Id.Value} alignment: {FriendlyTagError(exception)}");
            }
        }

        document.Regenerate();
        if (settings.LayoutStyle is SmartTagLayoutStyle.CompactGroups or SmartTagLayoutStyle.GuidedZones or SmartTagLayoutStyle.StandardNearHost)
        {
            // The adaptive planner already fixed every local group in host
            // top-to-bottom order. Re-running the crossing optimizer used to
            // swap identities between those accepted rows and made a dense
            // drawing look random even though its columns were aligned.
            warnings.Add(settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones
                ? "Row-order analysis: Guided Zones retains the picked zone and deterministic host order."
                : "Row-order analysis: adaptive groups retain deterministic top-to-bottom host order.");
        }
        else
        {
            appliedTags = OptimizeActualRowOrder(
                appliedTags,
                baselineSnapshot,
                settings,
                warnings);
        }
        if (settings.LayoutStyle is not SmartTagLayoutStyle.GuidedZones and not SmartTagLayoutStyle.StandardNearHost)
        {
            appliedTags = TranslateActualGroupsAwayFromMep(
                appliedTags,
                baselineSnapshot,
                settings,
                warnings);
            appliedTags = OptimizeActualLeaderLanes(
                appliedTags,
                baselineSnapshot,
                settings,
                warnings);
        }
        if (settings.LayoutStyle is not SmartTagLayoutStyle.CompactGroups and not SmartTagLayoutStyle.GuidedZones and not SmartTagLayoutStyle.StandardNearHost)
        {
            appliedTags = TranslateActualClustersAroundArchitecture(
                appliedTags,
                snapshot,
                settings,
                warnings);
        }
        if (settings.LayoutStyle != SmartTagLayoutStyle.StandardNearHost)
        {
            // Accepted Standard keeps its existing final snap. Standard V2
            // owns a separate bounded local-rail alignment pass and
            // must not be moved again after that pass.
            appliedTags = SnapActualDuctFollowersToVisibleCompanions(
                document,
                appliedTags,
                snapshot,
                settings,
                warnings);
        }
        }
        else if (skipped != 0 || appliedTags.Count != placements.Count)
            throw new InvalidOperationException("Analyzed tag set changed. Analyze again.");
        foreach (AppliedTagWorkItem item in appliedTags)
        {
            try
            {
                ConfigureLeader(item, snapshot);
            }
            catch (Exception exception)
            {
                warnings.Add($"Tag {item.Tag.Id.Value} leader: {FriendlyTagError(exception)}");
            }
        }
        // Enabling the real project-family leader can change the family-side
        // attachment point on the first regeneration. Reapply the same exact
        // horizontal/orthogonal geometry once after that regeneration so the
        // committed line cannot retain Revit's temporary diagonal route.
        document.Regenerate();
        foreach (AppliedTagWorkItem item in appliedTags)
        {
            try
            {
                ConfigureLeader(item, snapshot);
            }
            catch (Exception exception)
            {
                warnings.Add($"Tag {item.Tag.Id.Value} leader normalization: {FriendlyTagError(exception)}");
            }
        }
        if (replayAnalyzed)
        {
            document.Regenerate();
            foreach (var item in appliedTags)
            {
                var head = Project(item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection);
                var end = Project(item.Tag.GetLeaderEnd(item.Reference), snapshot.RightDirection, snapshot.UpDirection);
                var expectedEnd = item.Placement.UsesElbow ? item.Placement.End
                    : item.Placement.End with { V = item.Placement.Head.V };
                bool Same(LayoutPoint a, LayoutPoint b) => Math.Abs(a.U - b.U) < 1e-6 && Math.Abs(a.V - b.V) < 1e-6;
                if (!Same(head, item.Placement.Head) || !Same(end, expectedEnd) ||
                    (item.Placement.UsesElbow && !Same(
                        Project(item.Tag.GetLeaderElbow(item.Reference), snapshot.RightDirection, snapshot.UpDirection),
                        new LayoutPoint(expectedEnd.U, item.Placement.Head.V))))
                    throw new InvalidOperationException("Revit did not retain the analyzed tag geometry. Write rolled back; Analyze again.");
            }
        }
        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
            warnings.Add($"Timing: create/measure/layout/leaders {applyClock.Elapsed.TotalSeconds:F2} seconds before commit.");
        IReadOnlyList<TagLayoutPlacement> finalPlacements = appliedTags
            .Select(item => item.Placement)
            .ToList();
        int? validatedNearHostClashes = null;
        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
        {
            var fixedTags = MeasureNearHostReservations(document, snapshot, appliedTags);
            validatedNearHostClashes = SmartTagStandardNearHostLayout.CountHardClashes(finalPlacements,
                fixedTags, snapshot.Obstacles, snapshot.Frame, settings.Clearance);
            bool planningUnsafe = warnings.Any(w =>
                w.StartsWith("Near Host unsafe:", StringComparison.Ordinal));
            // UI and commit gate use the same visible text edges, clearance,
            // fixed reservations and pair counting as the planner validator.
            warnings.RemoveAll(w => w.StartsWith("Near Host unsafe:", StringComparison.Ordinal) &&
                w.Contains("after local repair", StringComparison.Ordinal));
            if (validatedNearHostClashes > 0 && planningUnsafe)
            {
                warnings.Add($"Near Host unsafe: {validatedNearHostClashes} validated text/model or annotation clash(es). Write blocked.");
                if (!previewOnly)
                    throw new InvalidOperationException("Near Host: final geometry is not clash-free. Write rolled back.");
            }
            else if (validatedNearHostClashes > 0)
            {
                // Standard itself is intentionally allowed to write dense
                // full-view layouts. V2 must therefore be judged against that
                // exact real-family baseline, not against an unattainable
                // absolute zero. The preview pass below blocks only when V2
                // introduces more conflicts; replay then verifies that Revit
                // retained the analyzed geometry before committing it.
                warnings.Add($"Standard V2 validated with {validatedNearHostClashes} residual existing clash(es); " +
                    "the analyzed layout did not worsen its Standard baseline, so Write remains available.");
            }
        }
        transaction.Commit();
        uidoc.RefreshActiveView();
        (int textOverlaps, int leaderCrossings) = CountActualIntersections(
            finalPlacements,
            settings.Clearance);
        int modelOverlaps = CountActualModelOverlaps(
            finalPlacements,
            records,
            settings.LayoutStyle is SmartTagLayoutStyle.CompactGroups or SmartTagLayoutStyle.GuidedZones
                ? snapshot.Obstacles
                    .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Mep)
                    .ToList()
                : snapshot.Obstacles,
            settings.Clearance);
        return new SmartTagApplyResult(
            created,
            arranged,
            skipped,
            warnings,
            validatedNearHostClashes ?? textOverlaps + leaderCrossings + modelOverlaps,
            textOverlaps,
            leaderCrossings,
            modelOverlaps) { FinalPlacements = finalPlacements.ToArray() };
    }

    private static int CountActualModelOverlaps(
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyDictionary<long, SmartTagRecord> records,
        IReadOnlyList<LayoutObstacle> obstacles,
        double clearance)
    {
        int overlaps = 0;
        foreach (TagLayoutPlacement placement in placements)
        {
            if (!records.TryGetValue(placement.TagKey, out SmartTagRecord? record)) continue;
            bool overlapsModel = obstacles.Any(obstacle => PlacementIntersectsModel(
                placement,
                record,
                obstacle,
                clearance));
            if (overlapsModel) overlaps++;
        }
        return overlaps;
    }

    private static (int TextOverlaps, int LeaderCrossings) CountActualIntersections(
        IReadOnlyList<TagLayoutPlacement> placements,
        double clearance)
    {
        int textOverlaps = 0;
        int leaderCrossings = 0;
        for (int firstIndex = 0; firstIndex < placements.Count; firstIndex++)
        {
            TagLayoutPlacement first = placements[firstIndex];
            List<LayoutSegment> firstLeaders =
            [
                new LayoutSegment(first.Head, first.Elbow)
            ];
            if (first.UsesElbow)
            {
                firstLeaders.Add(new LayoutSegment(first.End, first.Elbow));
            }
            for (int secondIndex = firstIndex + 1; secondIndex < placements.Count; secondIndex++)
            {
                TagLayoutPlacement second = placements[secondIndex];
                if (first.TagBounds.Intersects(second.TagBounds.Expand(clearance)))
                {
                    textOverlaps++;
                }
                List<LayoutSegment> secondLeaders =
                [
                    new LayoutSegment(second.Head, second.Elbow)
                ];
                if (second.UsesElbow)
                {
                    secondLeaders.Add(new LayoutSegment(second.End, second.Elbow));
                }
                foreach (LayoutSegment firstLeader in firstLeaders)
                foreach (LayoutSegment secondLeader in secondLeaders)
                {
                    if (SegmentsIntersect(firstLeader, secondLeader)) leaderCrossings++;
                }
                foreach (LayoutSegment firstLeader in firstLeaders)
                {
                    if (SegmentIntersectsRect(
                            firstLeader,
                            second.TagBounds.Expand(clearance)))
                    {
                        leaderCrossings++;
                    }
                }
                foreach (LayoutSegment secondLeader in secondLeaders)
                {
                    if (SegmentIntersectsRect(
                            secondLeader,
                            first.TagBounds.Expand(clearance)))
                    {
                        leaderCrossings++;
                    }
                }
            }
        }
        return (textOverlaps, leaderCrossings);
    }

    private static List<AppliedTagWorkItem> PackActualTagRows(
        Document document,
        SmartTagViewSnapshot snapshot,
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagLayoutSettings settings,
        List<string> warnings,
        Dictionary<long, (LayoutRect Bounds, LayoutPoint Head)>? measurements = null)
    {
        // Keep the actual Revit pass on the same dedicated one-side policy as
        // the WPF preview. Auto is deliberately not normalized here.
        settings = settings.AutoSide
            ? settings
            : settings.PlaceLeft
                ? SmartTagLeftLayout.PrepareSettings(settings)
                : SmartTagRightLayout.PrepareSettings(settings);
        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
        {
            measurements = new();
            // Reuse Standard's ACTUAL-family row packing and routing, not just
            // its approximate DTO layout. Near Host previously bypassed these.
            var standard = settings with { LayoutStyle = SmartTagLayoutStyle.Standard };
            var standardPacked = PackActualTagRows(document, snapshot, source, standard, warnings, measurements);
            document.Regenerate();
            standardPacked = OptimizeActualRowOrder(standardPacked, snapshot, standard, warnings);
            standardPacked = TranslateActualGroupsAwayFromMep(standardPacked, snapshot, standard, warnings);
            standardPacked = OptimizeActualLeaderLanes(standardPacked, snapshot, standard, warnings);
            // Keep the accepted real-family Standard positions as the true V2
            // baseline. The numeric V2 planner below may make only a bounded
            // local move toward DA/AT; pre-snapping here used to drag otherwise
            // good Duct tags across the view before the safety comparison.
            source = standardPacked;
        }
        var measured = new List<(AppliedTagWorkItem Item, LayoutRect Bounds)>();
        foreach (AppliedTagWorkItem item in source)
        {
            try
            {
                LayoutPoint head = Project(item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection);
                if (measurements is not null && measurements.TryGetValue(item.Record.TagKey, out var original))
                {
                    double du = head.U - original.Head.U;
                    double dv = head.V - original.Head.V;
                    measured.Add((item, new LayoutRect(original.Bounds.MinU + du, original.Bounds.MinV + dv,
                        original.Bounds.MaxU + du, original.Bounds.MaxV + dv)));
                    continue;
                }
                BoundingBoxXYZ? box = item.Tag.get_BoundingBox(document.ActiveView);
                if (box is null)
                {
                    measured.Add((item, item.Placement.TagBounds));
                    continue;
                }
                var bounds = ProjectBox(box, snapshot.RightDirection, snapshot.UpDirection);
                measured.Add((item, bounds));
                if (measurements is not null) measurements[item.Record.TagKey] = (bounds, head);
            }
            catch
            {
                measured.Add((item, item.Placement.TagBounds));
            }
        }

        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
        {
            var inputs = measured.Select(entry => entry.Item.Record.Layout with
            {
                TagWidth = entry.Bounds.Width,
                TagHeight = entry.Bounds.Height,
                TextOffsetU = (entry.Bounds.MinU + entry.Bounds.MaxU) * 0.5 -
                    Project(entry.Item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection).U,
                TextOffsetV = (entry.Bounds.MinV + entry.Bounds.MaxV) * 0.5 -
                    Project(entry.Item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection).V,
                // The controller assigns this after the immutable view
                // snapshot is built. Carry it on the analyzed placement so the
                // actual-family V2 solve cannot forget which DA/AT rail owns
                // this Duct tag.
                PreferredFollowerAnchorTagKey =
                    entry.Item.Placement.PreferredFollowerAnchorTagKey
            }).ToList();
            var fixedReservations = MeasureNearHostReservations(document, snapshot, source);
            var actualStandard = new SmartTagLayoutResult(measured.Select(entry => entry.Item.Placement with
                {
                    TagBounds = entry.Bounds,
                    Head = Project(entry.Item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection)
                }).ToList(),
                source.Count(item => item.Placement.UsesElbow), 0, snapshot.Frame);
            int standardBaselineClashes = SmartTagStandardNearHostLayout.CountHardClashes(
                actualStandard.Placements, fixedReservations,
                snapshot.Obstacles, snapshot.Frame, settings.Clearance);
            var layout = SmartTagStandardV2Layout.ComputeClustered(
                inputs, snapshot.Obstacles, snapshot.Frame, settings, settings.AutoSide,
                fixedReservations, standardBaseline: actualStandard);
            try
            {
                string folder = Infrastructure.AppPaths.LogFolder;
                Directory.CreateDirectory(folder);
                var replay = SmartTagNearHostReplay.Capture(inputs, snapshot.Obstacles, snapshot.Frame,
                    settings, fixedReservations, actualStandard, layout);
                File.WriteAllText(Path.Combine(folder, "smarttag-nearhost-latest.json"),
                    System.Text.Json.JsonSerializer.Serialize(replay, SmartTagNearHostReplay.JsonOptions));
            }
            catch (Exception diagnosticError)
            {
                warnings.Add("Near Host diagnostic could not be saved: " + diagnosticError.Message);
            }
            var byKey = layout.Placements.ToDictionary(placement => placement.TagKey);
            if (layout.Diagnostic is not null) warnings.Add(layout.Diagnostic);
            var result = new List<AppliedTagWorkItem>(measured.Count);
            foreach (var entry in measured)
            {
                var placement = byKey[entry.Item.Record.Layout.TagKey];
                // Align actual text bounds, not the family insertion point.
                XYZ original = entry.Item.Tag.TagHeadPosition;
                double du = placement.TagBounds.MinU - entry.Bounds.MinU;
                double dv = placement.TagBounds.MinV - entry.Bounds.MinV;
                entry.Item.Tag.TagHeadPosition = original + snapshot.RightDirection * du + snapshot.UpDirection * dv;
                var actualHead = Project(entry.Item.Tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection);
                placement = placement with
                {
                    // V2 may rebuild the row locally as well as snap the text
                    // edge to DA/AT. Keep its analyzed orthogonal route and
                    // host endpoint; ConfigureLeader reapplies both exactly.
                    Head = actualHead
                };
                result.Add(entry.Item with { Placement = placement });
            }
            int remaining = SmartTagStandardNearHostLayout.CountHardClashes(
                result.Select(item => item.Placement).ToList(), fixedReservations,
                snapshot.Obstacles, snapshot.Frame, settings.Clearance);
            if (remaining > standardBaselineClashes)
                warnings.Add(
                    $"Near Host unsafe: {remaining} unresolved text/model or leader clash(es) remain " +
                    $"after local repair, which is worse than the Standard baseline {standardBaselineClashes}. Write blocked.");
            else if (remaining > 0)
                warnings.Add(
                    $"Standard V2 improved the real-family layout from {standardBaselineClashes} to {remaining} " +
                    "validated clash(es). Residual conflicts are inherited from the dense Standard layout; Write remains available.");
            else
                warnings.Add(
                    $"Near Host accepted against Standard: baseline {standardBaselineClashes}, final 0. " +
                    "All selected real-family tags and leaders are clear.");
            return result;
        }

        if (settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones)
        {
            return PackActualTagsInGuidedZone(
                document,
                snapshot,
                measured,
                settings,
                warnings);
        }

        Dictionary<long, int> hostGroups = BuildActualHostGroups(
            measured,
            snapshot,
            settings);
        // Adaptive grouping is the expensive obstacle-aware step. Stamp its
        // accepted result on every work item once so all following repair
        // passes reuse exactly the same groups instead of solving the entire
        // view again (and potentially obtaining a different partition).
        measured = measured
            .Select(entry =>
            {
                int group = hostGroups.TryGetValue(
                    entry.Item.Record.TagKey,
                    out int knownGroup)
                        ? knownGroup
                        : -1;
                return (entry.Item with { LayoutGroupId = group }, entry.Bounds);
            })
            .ToList();
        measured = AlignActualGroupsToCenterRail(
            measured,
            hostGroups,
            snapshot,
            settings,
            out int alignedGroupTags,
            out Dictionary<long, int> alignedHostGroups);
        if (settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups)
        {
            hostGroups = alignedHostGroups;
            measured = measured
                .Select(entry =>
                {
                    int group = hostGroups.TryGetValue(
                        entry.Item.Record.TagKey,
                        out int knownGroup)
                            ? knownGroup
                            : ActualCachedGroup(entry.Item);
                    return (entry.Item with { LayoutGroupId = group }, entry.Bounds);
                })
                .ToList();
        }
        Dictionary<int, double> groupPivotRows = BuildActualGroupPivotRows(
            measured,
            hostGroups);
        string alignmentDescription = settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups
            ? "space-adaptive local rails sized from clear model pockets"
            : settings.AutoSide
                ? "nearby clear left/right rails per local host group"
                : settings.PlaceLeft
                    ? SmartTagLeftLayout.AlignmentDescription
                    : SmartTagRightLayout.AlignmentDescription;
        warnings.Add($"Column alignment: {alignedGroupTags} tag(s) locked to {alignmentDescription}.");
        var packed = new List<AppliedTagWorkItem>(source.Count);
        var reservedBoxes = new List<LayoutRect>();
        var reservedLeaders = new List<LayoutSegment>();
        int unresolved = 0;
        // Standard keeps its accepted row exactly. Adaptive Groups may inspect
        // nearby measured-family rows because the real project Tag Family can
        // be taller than its preview estimate. The bounded search preserves
        // rail order while eliminating text/text and text/MEP overlaps.
        int maximumRowAttempts = settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups
            ? 17
            : 1;
        double lowerLimit = snapshot.Frame.MinV + settings.TopMargin;
        double upperLimit = snapshot.Frame.MaxV - settings.TopMargin;
        Dictionary<long, (bool RightSide, long Rail)> railKeys = BuildActualRailKeys(
            measured,
            settings);
        Dictionary<long, double> lockedRows = BuildActualLockedRows(
            measured,
            railKeys,
            hostGroups,
            lowerLimit,
            upperLimit,
            settings,
            out Dictionary<long, double> lockedRowSteps);
        var lastPlacedByRail = new Dictionary<(bool RightSide, long Rail), (double Row, double Height)>();
        // Solve every rail in immutable host order. Clash avoidance may move a
        // lower row farther down, but it may never jump above the previous host
        // on the same rail. This keeps the final Revit result readable and
        // deterministic instead of letting per-tag collision scores reorder it.
        foreach ((AppliedTagWorkItem original, LayoutRect actualBounds) in measured
                     .OrderByDescending(item => lockedRows.TryGetValue(
                         item.Item.Record.TagKey,
                         out double row)
                             ? row
                             : item.Item.Record.Layout.Anchor.V)
                     .ThenBy(item => railKeys.TryGetValue(
                         item.Item.Record.TagKey,
                         out (bool RightSide, long Rail) key)
                             ? key.RightSide
                             : false)
                     .ThenBy(item => railKeys.TryGetValue(
                         item.Item.Record.TagKey,
                         out (bool RightSide, long Rail) key)
                             ? key.Rail
                             : 0L)
                     .ThenBy(item => item.Item.Record.ElementId))
        {
            LayoutPoint anchor = original.Record.Layout.Anchor;
            LayoutPoint currentHead = Project(
                original.Tag.TagHeadPosition,
                snapshot.RightDirection,
                snapshot.UpDirection);
            int localGroup = hostGroups.TryGetValue(original.Record.TagKey, out int block)
                ? block
                : 0;
            double height = Math.Max(actualBounds.Height, 0.005);
            double rowStep = lockedRowSteps.TryGetValue(
                original.Record.TagKey,
                out double sharedRowStep)
                ? sharedRowStep
                : Math.Max(
                    height + settings.RowSpacing,
                    settings.Clearance * 2.0 + 0.0025);
            double maximumFromHost = Math.Max(
                height * 8.0 + settings.RowSpacing * 7.0,
                Math.Max(
                    settings.Clearance * 12.0 + height,
                    settings.ColumnWidth * 0.35));
            bool rightSide = currentHead.U > anchor.U;
            (bool RightSide, long Rail) railKey = railKeys.TryGetValue(
                original.Record.TagKey,
                out (bool RightSide, long Rail) knownRail)
                    ? knownRail
                    : (rightSide, 0L);
            double lockedRow = lockedRows.TryGetValue(original.Record.TagKey, out double scheduledRow)
                ? scheduledRow
                : anchor.V;
            bool hasPreviousRow = lastPlacedByRail.TryGetValue(
                railKey,
                out (double Row, double Height) previousRow);
            double orderCeiling = hasPreviousRow
                ? previousRow.Row - (previousRow.Height + height) * 0.5 - settings.RowSpacing
                : upperLimit - height * 0.5;
            double pivotV = groupPivotRows.TryGetValue(localGroup, out double pivotRow)
                ? pivotRow
                : anchor.V;
            double bestOffset = 0.0;
            double bestShiftU = 0.0;
            int bestScore = int.MaxValue;
            int bestCritical = int.MaxValue;
            double bestTravel = double.MaxValue;
            double bestQuality = double.MaxValue;
            LayoutRect bestBounds = actualBounds;
            LayoutPoint bestEnd = anchor;
            LayoutPoint bestElbow = new(anchor.U, currentHead.V);
            LayoutSegment bestHorizontal = new(currentHead, bestElbow);
            LayoutSegment? bestTail = null;

            void ConsiderRail(double shiftU, int railLevel)
            {
                LayoutRect shiftedBounds = ShiftRect(actualBounds, shiftU, 0.0);
                if (shiftedBounds.MinU < snapshot.Frame.MinU + settings.Clearance ||
                    shiftedBounds.MaxU > snapshot.Frame.MaxU - settings.Clearance)
                {
                    return;
                }
                for (int attempt = 0; attempt < maximumRowAttempts; attempt++)
                {
                    int directionalAttempts = Math.Max(1, (maximumRowAttempts - 1) / 2);
                    double groupDelta = anchor.V - pivotV;
                    double preferredDirection = hasPreviousRow
                        ? -1.0
                        : groupDelta > 1e-7
                        ? 1.0
                        : groupDelta < -1e-7
                            ? -1.0
                            : original.Record.ElementId % 2 == 0 ? -1.0 : 1.0;
                    int distance;
                    double direction;
                    if (attempt == 0)
                    {
                        distance = 0;
                        direction = 0.0;
                    }
                    else if (attempt <= directionalAttempts)
                    {
                        distance = attempt;
                        direction = preferredDirection;
                    }
                    else
                    {
                        distance = attempt - directionalAttempts;
                        direction = -preferredDirection;
                    }
                    // Always test the host's own row first. If it is clear, Revit
                    // keeps a native straight Attached leader with no elbow.
                    double candidateV = attempt == 0
                        ? lockedRow
                        : lockedRow + direction * distance * rowStep;
                    double offsetV = candidateV - currentHead.V;
                    if (Math.Abs(candidateV - anchor.V) > maximumFromHost + 1e-8)
                    {
                        continue;
                    }
                    if (candidateV > orderCeiling + 1e-8)
                    {
                        continue;
                    }

                    LayoutRect candidateBounds = ShiftRect(shiftedBounds, 0.0, offsetV);
                    if (candidateBounds.MinV < lowerLimit || candidateBounds.MaxV > upperLimit)
                    {
                        continue;
                    }

                    bool moved = Math.Abs(candidateV - anchor.V) > 1e-7;
                    LayoutPoint head = new(currentHead.U + shiftU, candidateV);
                    double[] laneFactors = moved
                        ? [0.0, 0.125, 0.25, 0.375, 0.50, 0.625, 0.75, 0.875, 1.0]
                        : [0.0];
                    foreach (double laneFactor in laneFactors)
                    {
                        LayoutPoint end = CreateActualHostEnd(
                            original.Record.Layout,
                            head.U,
                            candidateV,
                            moved,
                            settings,
                            laneFactor);
                        LayoutPoint elbow = new(end.U, candidateV);
                        var horizontal = new LayoutSegment(head, elbow);
                        LayoutSegment? tail = moved ? new LayoutSegment(end, elbow) : null;
                        int critical = ActualCriticalConflictCount(
                            original.Record,
                            candidateBounds,
                            horizontal,
                            tail,
                            reservedBoxes,
                            reservedLeaders,
                            snapshot.Obstacles,
                            settings);
                        int score = ActualCollisionScore(
                            original.Record,
                            candidateBounds,
                            horizontal,
                            tail,
                            reservedBoxes,
                            reservedLeaders,
                            snapshot.Obstacles,
                            settings);
                        double travel = Math.Abs(candidateV - anchor.V) /
                                        Math.Max(rowStep, 1e-8) + laneFactor * 0.1;
                        // Critical visual conflicts are compared first. The MEP
                        // obstacle score and route length only choose between
                        // candidates having the same critical-conflict count.
                        double quality = score + travel * 200.0 +
                                         (moved ? 25.0 : 0.0) + railLevel * 120.0;
                        if (critical < bestCritical ||
                            critical == bestCritical &&
                            (quality < bestQuality - 1e-8 ||
                             Math.Abs(quality - bestQuality) <= 1e-8 && travel < bestTravel))
                        {
                            bestCritical = critical;
                            bestQuality = quality;
                            bestScore = score;
                            bestTravel = travel;
                            bestOffset = offsetV;
                            bestShiftU = shiftU;
                            bestBounds = candidateBounds;
                            bestEnd = end;
                            bestElbow = elbow;
                            bestHorizontal = horizontal;
                            bestTail = tail;
                        }
                        if (bestCritical == 0 && bestScore == 0)
                        {
                            return;
                        }
                    }
                }
            }

            ConsiderRail(0.0, 0);

            if (bestScore == int.MaxValue)
            {
                // Never escape to a distant annotation group merely to satisfy
                // ordering. Keep the tag on its host row and report the local
                // conflict so the user can adjust spacing or side assignment.
                double fallbackRow = Math.Min(lockedRow, orderCeiling);
                bestOffset = fallbackRow - currentHead.V;
                bestBounds = ShiftRect(actualBounds, 0.0, bestOffset);
                bool fallbackMoved = Math.Abs(fallbackRow - anchor.V) > 1e-7;
                bestEnd = CreateActualHostEnd(
                    original.Record.Layout,
                    currentHead.U,
                    fallbackRow,
                    fallbackMoved,
                    settings,
                    0.0);
                bestElbow = new LayoutPoint(bestEnd.U, fallbackRow);
                bestHorizontal = new LayoutSegment(
                    new LayoutPoint(currentHead.U, fallbackRow),
                    bestElbow);
                bestTail = fallbackMoved
                    ? new LayoutSegment(bestEnd, bestElbow)
                    : null;
                bestCritical = ActualCriticalConflictCount(
                    original.Record,
                    bestBounds,
                    bestHorizontal,
                    bestTail,
                    reservedBoxes,
                    reservedLeaders,
                    snapshot.Obstacles,
                    settings);
            }
            if (Math.Abs(bestOffset) > 1e-8 || Math.Abs(bestShiftU) > 1e-8)
            {
                original.Tag.TagHeadPosition +=
                    snapshot.RightDirection * bestShiftU +
                    snapshot.UpDirection * bestOffset;
            }
            LayoutPoint finalHead = new(
                currentHead.U + bestShiftU,
                currentHead.V + bestOffset);
            bool usesElbow = Math.Abs(finalHead.V - anchor.V) > 1e-7;
            TagLayoutPlacement adjusted = original.Placement with
            {
                Head = finalHead,
                End = bestEnd,
                Elbow = bestElbow,
                TagBounds = bestBounds,
                UsesElbow = usesElbow,
                UsesFreeEnd = usesElbow,
                HasClash = bestCritical > 0
            };
            packed.Add(original with { Placement = adjusted });
            reservedBoxes.Add(bestBounds);
            reservedLeaders.Add(bestHorizontal);
            if (bestTail is LayoutSegment tailSegment)
            {
                reservedLeaders.Add(tailSegment);
            }
            lastPlacedByRail[railKey] = (finalHead.V, height);
            if (bestCritical > 0) unresolved++;
        }

        if (unresolved > 0)
        {
            warnings.Add($"{unresolved} actual Tag Family layout clash(es) remain after real-size packing.");
        }
        return packed;
    }

    private static List<AppliedTagWorkItem> PackActualTagsInGuidedZone(
        Document document,
        SmartTagViewSnapshot snapshot,
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> measured,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        List<LayoutTagInput> actualInputs = measured
            .Select(entry => entry.Item.Record.Layout with
            {
                CurrentHead = Project(
                    entry.Item.Tag.TagHeadPosition,
                    snapshot.RightDirection,
                    snapshot.UpDirection),
                TagWidth = Math.Max(entry.Bounds.Width, 0.01),
                TagHeight = Math.Max(entry.Bounds.Height, 0.01)
            })
            .ToList();
        List<LayoutObstacle> mepObstacles = snapshot.Obstacles
            .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Mep)
            .ToList();
        SmartTagGuidedZoneEvaluation evaluation = SmartTagGuidedZoneLayout.Compute(
            actualInputs,
            mepObstacles,
            snapshot.Frame,
            settings);
        Dictionary<long, TagLayoutPlacement> placements = evaluation.Layout.Placements
            .ToDictionary(item => item.TagKey);
        var packed = new List<AppliedTagWorkItem>(measured.Count);
        foreach ((AppliedTagWorkItem item, _) in measured)
        {
            if (!placements.TryGetValue(item.Record.TagKey, out TagLayoutPlacement? placement))
            {
                continue;
            }
            LayoutPoint current = Project(
                item.Tag.TagHeadPosition,
                snapshot.RightDirection,
                snapshot.UpDirection);
            item.Tag.TagHeadPosition +=
                snapshot.RightDirection * (placement.Head.U - current.U) +
                snapshot.UpDirection * (placement.Head.V - current.V);
            packed.Add(item with { Placement = placement });
        }
        warnings.Add(evaluation.Message + " Actual project Tag Family sizes were used.");
        return packed;
    }

    private static List<AppliedTagWorkItem> TranslateActualGroupsAwayFromMep(
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        var result = source.ToList();
        bool usesLocalGroups = settings.AutoSide ||
                               settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups;
        if (!usesLocalGroups || !settings.AvoidElements || result.Count == 0 ||
            snapshot.Obstacles.All(obstacle => obstacle.Kind != LayoutObstacleKind.Mep))
        {
            return result;
        }

        List<List<int>> groups = Enumerable.Range(0, result.Count)
            .GroupBy(index =>
            {
                AppliedTagWorkItem item = result[index];
                int group = ActualCachedGroup(item);
                bool rightSide = item.Placement.Head.U >= item.Record.Layout.Anchor.U;
                return (group, rightSide);
            })
            .Select(group => group.ToList())
            .OrderByDescending(group => group.Max(index => result[index].Placement.Head.V))
            .ToList();

        int movedGroups = 0;
        int movedTags = 0;
        void ShiftTag(int index, double shiftU)
        {
            AppliedTagWorkItem item = result[index];
            item.Tag.TagHeadPosition += snapshot.RightDirection * shiftU;
            TagLayoutPlacement shifted = item.Placement with
            {
                Head = new LayoutPoint(
                    item.Placement.Head.U + shiftU,
                    item.Placement.Head.V),
                TagBounds = ShiftRect(item.Placement.TagBounds, shiftU, 0.0)
            };
            result[index] = item with { Placement = shifted };
        }

        foreach (List<int> group in groups)
        {
            if (group.All(index =>
                    result[index].Record.Layout.PreferLocalClustering))
            {
                // Duct is optional. Keep its accepted companion rail instead of
                // replacing a local overlap with a very long horizontal leader.
                continue;
            }
            bool rightSide = result[group[0]].Placement.Head.U >=
                             result[group[0]].Record.Layout.Anchor.U;
            bool currentlyCoversMep = group.Any(index => snapshot.Obstacles.Any(obstacle =>
                obstacle.Kind == LayoutObstacleKind.Mep &&
                result[index].Placement.TagBounds.Intersects(
                    obstacle.Bounds.Expand(settings.Clearance),
                    0.0)));
            // Preserve every accepted rail that is already clear. This pass is
            // intentionally a repair for text-on-MEP only, not another general
            // re-layout that could disturb the user's established columns.
            if (!currentlyCoversMep) continue;
            HashSet<int> memberIndices = group.ToHashSet();
            List<LayoutRect> otherBounds = Enumerable.Range(0, result.Count)
                .Where(index => !memberIndices.Contains(index))
                .Select(index => result[index].Placement.TagBounds)
                .ToList();
            double shiftU = SmartTagMepClearance.FindNearestHorizontalShift(
                group.Select(index => result[index].Placement.TagBounds).ToList(),
                group.Select(index => result[index].Record.Layout.Anchor.U).ToList(),
                rightSide,
                otherBounds,
                snapshot.Obstacles,
                snapshot.Frame,
                settings.Clearance,
                settings.OffsetFromElements,
                settings.ColumnWidth);
            if (Math.Abs(shiftU) <= 1e-8) continue;

            foreach (int index in group)
            {
                ShiftTag(index, shiftU);
                movedTags++;
            }
            movedGroups++;
        }

        // A dense local group can have clear space for one row but not enough
        // space for every row to translate together. Repair only the remaining
        // text-on-MEP rows individually. Their V coordinate and host endpoint
        // stay fixed; the following leader-lane pass reroutes the longer or
        // shorter horizontal tail without detaching it from the host.
        int individuallyMoved = 0;
        for (int index = 0; index < result.Count; index++)
        {
            AppliedTagWorkItem item = result[index];
            if (item.Record.Layout.PreferLocalClustering) continue;
            bool stillCoversMep = snapshot.Obstacles.Any(obstacle =>
                obstacle.Kind == LayoutObstacleKind.Mep &&
                item.Placement.TagBounds.Intersects(
                    obstacle.Bounds.Expand(settings.Clearance),
                    0.0));
            if (!stillCoversMep) continue;

            List<LayoutRect> otherBounds = Enumerable.Range(0, result.Count)
                .Where(otherIndex => otherIndex != index)
                .Select(otherIndex => result[otherIndex].Placement.TagBounds)
                .ToList();
            bool rightSide = item.Placement.Head.U >= item.Record.Layout.Anchor.U;
            double shiftU = SmartTagMepClearance.FindNearestHorizontalShift(
                [item.Placement.TagBounds],
                [item.Record.Layout.Anchor.U],
                rightSide,
                otherBounds,
                snapshot.Obstacles,
                snapshot.Frame,
                settings.Clearance,
                settings.OffsetFromElements,
                settings.ColumnWidth);
            if (Math.Abs(shiftU) <= 1e-8) continue;

            ShiftTag(index, shiftU);
            individuallyMoved++;
            movedTags++;
        }

        int remainingTextOverlaps = result.Count(item => snapshot.Obstacles.Any(obstacle =>
            obstacle.Kind == LayoutObstacleKind.Mep &&
            item.Placement.TagBounds.Intersects(
                obstacle.Bounds.Expand(settings.Clearance),
                0.0)));
        warnings.Add(
            $"MEP text clearance: moved {movedGroups} local group(s) / {movedTags} tag(s); " +
            $"{individuallyMoved} stubborn row(s) repaired separately; " +
            $"{remainingTextOverlaps} tag text overlap(s) remain where no nearby clear rail exists.");
        return result;
    }

    private static List<AppliedTagWorkItem> SnapActualDuctFollowersToVisibleCompanions(
        Document document,
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        var result = source.ToList();
        List<int> ductIndices = Enumerable.Range(0, result.Count)
            .Where(index => result[index].Record.Layout.PreferLocalClustering)
            .ToList();
        if (ductIndices.Count == 0) return result;

        var companions = new Dictionary<long, (SmartTagRecord Record, LayoutRect Bounds)>();
        HashSet<long> movingTagKeys = result
            .Select(item => item.Record.TagKey)
            .ToHashSet();

        // Tags created or arranged in this transaction already carry measured
        // project-family bounds after PackActualTagRows.
        foreach (AppliedTagWorkItem item in result.Where(item =>
                     item.Record.Layout.CanAnchorDuctFollowers))
        {
            companions[item.Record.TagKey] = (item.Record, item.Placement.TagBounds);
        }

        // Existing DA/AT tags are deliberately fixed by Standard. Measure their
        // text with leaders temporarily hidden so the bounding box cannot include
        // a long leader and masquerade as a remote annotation rail.
        List<SmartTagRecord> fixedCompanionRecords = snapshot.Tags
            .Where(item => item.Layout.CanAnchorDuctFollowers &&
                           !item.WillCreate &&
                           item.ExistingTagId > 0 &&
                           !movingTagKeys.Contains(item.TagKey))
            .ToList();
        if (fixedCompanionRecords.Count > 0)
        {
            using var measurement = new SubTransaction(document);
            measurement.Start();
            try
            {
                foreach (SmartTagRecord record in fixedCompanionRecords)
                {
                    if (document.GetElement(new ElementId(record.ExistingTagId)) is IndependentTag tag)
                    {
                        try { tag.HasLeader = false; } catch { }
                    }
                }
                document.Regenerate();
                foreach (SmartTagRecord record in fixedCompanionRecords)
                {
                    if (document.GetElement(new ElementId(record.ExistingTagId)) is not IndependentTag tag)
                        continue;
                    BoundingBoxXYZ? box = tag.get_BoundingBox(document.ActiveView);
                    if (box is null) continue;
                    companions[record.TagKey] = (
                        record,
                        ProjectBox(box, snapshot.RightDirection, snapshot.UpDirection));
                }
            }
            finally
            {
                measurement.RollBack();
            }
        }

        if (companions.Count == 0) return result;
        double followRadius = SmartTagLayoutEngine.NearbyFollowRadius(settings);
        double visibleRailRadius = followRadius;
        var assignments = new List<(int Index, long CompanionKey)>();
        foreach (int index in ductIndices)
        {
            LayoutRect host = result[index].Record.Layout.ElementBounds;
            long preferredKey = result[index].Placement.PreferredFollowerAnchorTagKey;
            var candidates = companions
                .Select(pair => new
                {
                    Key = pair.Key,
                    Preferred = pair.Key == preferredKey,
                    HostDistance = Math.Sqrt(RectDistanceSquared(
                        host,
                        pair.Value.Record.Layout.ElementBounds)),
                    TextDistance = Math.Sqrt(RectDistanceSquared(host, pair.Value.Bounds))
                });
            var nearest = candidates
                .Where(item => item.TextDistance <= visibleRailRadius)
                // The selected Duct density pass already chose the closest
                // DA/AT host. Keep that ownership stable while measuring real
                // families; only fall back when its visible rail is genuinely
                // outside the local search radius.
                .OrderByDescending(item => item.Preferred)
                .ThenBy(item => item.TextDistance * 0.65 + item.HostDistance * 0.35)
                .ThenBy(item => item.TextDistance)
                .ThenBy(item => item.Key)
                .FirstOrDefault();
            if (nearest is not null) assignments.Add((index, nearest.Key));
        }

        if (assignments.Count == 0)
        {
            warnings.Add("Duct follow: no visible nearby Air Terminal / Duct Accessory text rail was found; existing Duct positions were retained.");
            return result;
        }

        // Persist the actual visible companion chosen above. It may differ
        // from the controller's host-nearest candidate after Standard measures
        // and moves the real DA/AT family text. The following V2 solve must use
        // this visible ownership, otherwise it can split the Duct back onto an
        // unrelated local rail.
        foreach ((int Index, long CompanionKey) assignment in assignments)
        {
            AppliedTagWorkItem item = result[assignment.Index];
            result[assignment.Index] = item with
            {
                Placement = item.Placement with
                {
                    PreferredFollowerAnchorTagKey = assignment.CompanionKey
                }
            };
        }

        var reservedDuctBounds = new List<LayoutRect>();
        int snapped = 0;
        // Several nearby DA/AT tags may have tiny rail differences. Treat them
        // as one visible column, but split distant vertical areas so a Duct at
        // the top of the plan is never dragged below a bottom-area stack.
        double railMergeDistance = Math.Max(
            settings.Clearance * 2.0,
            Math.Max(settings.OffsetFromElements * 0.75, 0.02));
        double verticalMergeDistance = Math.Max(
            followRadius * 0.85,
            settings.RowSpacing * 12.0);
        var snapGroups = new List<List<(int Index, long CompanionKey)>>();
        foreach ((int Index, long CompanionKey) assignment in assignments
                     .OrderByDescending(item => result[item.Index].Record.Layout.Anchor.V)
                     .ThenBy(item => companions[item.CompanionKey].Bounds.MinU))
        {
            double rail = companions[assignment.CompanionKey].Bounds.MinU;
            double row = result[assignment.Index].Record.Layout.Anchor.V;
            List<(int Index, long CompanionKey)>? target = snapGroups.FirstOrDefault(group =>
                group.Any(item =>
                    Math.Abs(companions[item.CompanionKey].Bounds.MinU - rail) <= railMergeDistance &&
                    Math.Abs(result[item.Index].Record.Layout.Anchor.V - row) <= verticalMergeDistance));
            if (target is null)
            {
                target = [];
                snapGroups.Add(target);
            }
            target.Add(assignment);
        }

        foreach (List<(int Index, long CompanionKey)> group in snapGroups
                     .OrderByDescending(group => group.Max(item =>
                         companions[item.CompanionKey].Bounds.MaxV)))
        {
            double[] groupRails = group
                .Select(item => companions[item.CompanionKey].Bounds.MinU)
                .ToArray();
            // Pick one existing rail rather than an average between rails, so
            // the resulting Duct column is visibly identical to a real DA/AT
            // left edge.
            double targetRail = groupRails
                .Distinct()
                .OrderBy(rail => groupRails.Sum(other => Math.Abs(other - rail)))
                .ThenBy(rail => rail)
                .First();
            List<int> members = group
                .Select(item => item.Index)
                .OrderByDescending(index => result[index].Record.Layout.Anchor.V)
                .ThenBy(index => result[index].Record.ElementId)
                .ToList();
            double lowerCompanionEdge = companions
                .Where(pair => Math.Abs(pair.Value.Bounds.MinU - targetRail) <= railMergeDistance)
                .Where(pair => members.Any(index =>
                    Math.Sqrt(RectDistanceSquared(
                        result[index].Record.Layout.ElementBounds,
                        pair.Value.Record.Layout.ElementBounds)) <= followRadius))
                .Select(pair => pair.Value.Bounds.MinV)
                .DefaultIfEmpty(group.Min(item => companions[item.CompanionKey].Bounds.MinV))
                .Min();
            double nextTop = lowerCompanionEdge - settings.RowSpacing;

            foreach (int index in members)
            {
                AppliedTagWorkItem item = result[index];
                LayoutRect bounds = item.Placement.TagBounds;
                double desiredCenterV = nextTop - bounds.Height * 0.5;
                LayoutRect proposed = ShiftRect(
                    bounds,
                    targetRail - bounds.MinU,
                    desiredCenterV - (bounds.MinV + bounds.MaxV) * 0.5);
                while (reservedDuctBounds.Any(other =>
                           proposed.Intersects(other.Expand(settings.RowSpacing * 0.5))) &&
                       proposed.MinV >= snapshot.Frame.MinV + settings.TopMargin)
                {
                    double step = Math.Max(bounds.Height + settings.RowSpacing, 0.01);
                    desiredCenterV -= step;
                    proposed = ShiftRect(
                        bounds,
                        targetRail - bounds.MinU,
                        desiredCenterV - (bounds.MinV + bounds.MaxV) * 0.5);
                }

                double shiftU = targetRail - bounds.MinU;
                double shiftV = proposed.MinV >= snapshot.Frame.MinV + settings.TopMargin
                    ? desiredCenterV - (bounds.MinV + bounds.MaxV) * 0.5
                    : 0.0;
                proposed = ShiftRect(bounds, shiftU, shiftV);
                item.Tag.TagHeadPosition +=
                    snapshot.RightDirection * shiftU +
                    snapshot.UpDirection * shiftV;
                LayoutPoint head = new(
                    item.Placement.Head.U + shiftU,
                    item.Placement.Head.V + shiftV);
                bool movedVertically = Math.Abs(head.V - item.Record.Layout.Anchor.V) > 1e-7;
                LayoutPoint end = CreateActualHostEnd(
                    item.Record.Layout,
                    head.U,
                    head.V,
                    movedVertically,
                    settings,
                    laneFactor: members.Count <= 1
                        ? 0.0
                        : (double)members.IndexOf(index) / (members.Count - 1));
                TagLayoutPlacement placement = item.Placement with
                {
                    Head = head,
                    End = end,
                    Elbow = movedVertically ? new LayoutPoint(end.U, head.V) : end,
                    TagBounds = proposed,
                    UsesElbow = movedVertically,
                    UsesFreeEnd = true
                };
                result[index] = item with { Placement = placement };
                reservedDuctBounds.Add(proposed);
                nextTop = proposed.MinV - settings.RowSpacing;
                snapped++;
            }
        }

        warnings.Add($"Duct follow: {snapped} tag(s) snapped below the nearest visible DA/AT text rail after real-family measurement.");
        return result;
    }

    private static int ActualCachedGroup(AppliedTagWorkItem item)
    {
        if (item.LayoutGroupId >= 0)
        {
            return item.LayoutGroupId;
        }

        // Defensive fallback for an item that could not be measured during the
        // first regeneration. Keep it isolated; group 0 would incorrectly join
        // every such tag into one remote annotation cluster.
        long key = item.Record.TagKey != 0
            ? item.Record.TagKey
            : item.Record.ElementId;
        return unchecked((int)(key ^ key >> 32));
    }

    private static Dictionary<long, int> BuildActualLocalBlocks(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        SmartTagLayoutSettings settings)
    {
        var result = new Dictionary<long, int>();
        double railResolution = Math.Max(settings.Clearance, 0.0025);
        foreach (IGrouping<(bool RightSide, long Rail), (AppliedTagWorkItem Item, LayoutRect Bounds)> railGroup
                 in source.GroupBy(item =>
                 {
                     bool rightSide = item.Item.Placement.Head.U > item.Item.Record.Layout.Anchor.U;
                     double railCoordinate = rightSide
                         ? item.Bounds.MinU
                         : item.Bounds.MaxU;
                     long rail = (long)Math.Round(railCoordinate / railResolution);
                     return (rightSide, rail);
                 }))
        {
            List<(AppliedTagWorkItem Item, LayoutRect Bounds)> ordered = railGroup
                .OrderByDescending(item => item.Item.Record.Layout.Anchor.V)
                .ToList();
            if (ordered.Count == 0) continue;
            double maximumHeight = ordered.Max(item => Math.Max(item.Bounds.Height, 0.005));
            double gapLimit = Math.Max(
                maximumHeight * 2.5 + settings.RowSpacing * 2.0,
                settings.ColumnWidth * 0.18);
            double spanLimit = Math.Max(
                maximumHeight * 6.0 + settings.RowSpacing * 5.0,
                settings.ColumnWidth * 0.45);
            int block = 0;
            double blockTop = ordered[0].Item.Record.Layout.Anchor.V;
            double previousV = blockTop;
            foreach ((AppliedTagWorkItem item, LayoutRect _) in ordered)
            {
                double anchorV = item.Record.Layout.Anchor.V;
                if (previousV - anchorV > gapLimit || blockTop - anchorV > spanLimit)
                {
                    block++;
                    blockTop = anchorV;
                }
                result[item.Record.TagKey] = block;
                previousV = anchorV;
            }
        }
        return result;
    }

    private static Dictionary<long, (bool RightSide, long Rail)> BuildActualRailKeys(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        SmartTagLayoutSettings settings)
    {
        double resolution = Math.Max(settings.Clearance * 0.25, 0.0005);
        return source.ToDictionary(
            item => item.Item.Record.TagKey,
            item =>
            {
                LayoutPoint anchor = item.Item.Record.Layout.Anchor;
                bool rightSide = (item.Bounds.MinU + item.Bounds.MaxU) * 0.5 >= anchor.U;
                // Both visible columns are keyed by the text's left edge.
                // Tag families have different widths; using MaxU on the left
                // side splits a visually aligned column into several rails.
                double rail = item.Bounds.MinU;
                return (rightSide, (long)Math.Round(rail / resolution));
            });
    }

    private static Dictionary<long, double> BuildActualLockedRows(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        IReadOnlyDictionary<long, (bool RightSide, long Rail)> railKeys,
        IReadOnlyDictionary<long, int> hostGroups,
        double lowerLimit,
        double upperLimit,
        SmartTagLayoutSettings settings,
        out Dictionary<long, double> rowSteps)
    {
        var result = new Dictionary<long, double>();
        rowSteps = new Dictionary<long, double>();
        // RowSpacing is a visible edge-to-edge gap, not a centre pitch. Using
        // the tallest selected family as one global pitch made one-line tags
        // look widely separated while two-line tags looked cramped.
        foreach (IGrouping<((bool RightSide, long Rail) Rail, int HostGroup),
                     (AppliedTagWorkItem Item, LayoutRect Bounds)> rail
                 in source.GroupBy(item => (
                     railKeys[item.Item.Record.TagKey],
                     hostGroups.TryGetValue(item.Item.Record.TagKey, out int group)
                         ? group
                         : 0)))
        {
            List<(AppliedTagWorkItem Item, LayoutRect Bounds)> ordered = rail
                .OrderByDescending(item => item.Item.Record.Layout.PreferLocalClustering
                    ? item.Item.Placement.Head.V
                    : item.Item.Record.Layout.Anchor.V)
                .ThenBy(item => item.Item.Record.ElementId)
                .ToList();
            if (ordered.Count == 0) continue;
            int count = ordered.Count;
            var offsets = new double[count];
            for (int offset = 1; offset < count; offset++)
            {
                double previousHalf = Math.Max(ordered[offset - 1].Bounds.Height, 0.005) * 0.5;
                double currentHalf = Math.Max(ordered[offset].Bounds.Height, 0.005) * 0.5;
                offsets[offset] = offsets[offset - 1] +
                                  previousHalf + currentHalf + settings.RowSpacing;
            }
            // A solitary element keeps its native straight host row. Only a
            // nearby group of two or more tags receives an exact visible gap.
            bool ductFollowerRows = ordered.All(item =>
                item.Item.Record.Layout.PreferLocalClustering);
            double topRow = count == 1
                ? ductFollowerRows
                    ? ordered[0].Item.Placement.Head.V
                    : ordered[0].Item.Record.Layout.Anchor.V
                : Enumerable.Range(0, count)
                    .Average(offset =>
                        (ductFollowerRows
                            ? ordered[offset].Item.Placement.Head.V
                            : ordered[offset].Item.Record.Layout.Anchor.V) +
                        offsets[offset]);

            double minimumShift = double.NegativeInfinity;
            double maximumShift = double.PositiveInfinity;
            for (int offset = 0; offset < count; offset++)
            {
                double row = topRow - offsets[offset];
                double halfHeight = Math.Max(ordered[offset].Bounds.Height, 0.005) * 0.5;
                minimumShift = Math.Max(minimumShift, lowerLimit + halfHeight - row);
                maximumShift = Math.Min(maximumShift, upperLimit - halfHeight - row);
            }
            double collectiveShift = minimumShift <= maximumShift
                ? Math.Clamp(0.0, minimumShift, maximumShift)
                : (minimumShift + maximumShift) * 0.5;
            for (int offset = 0; offset < count; offset++)
            {
                long tagKey = ordered[offset].Item.Record.TagKey;
                result[tagKey] = topRow - offsets[offset] + collectiveShift;
                double ownHeight = Math.Max(ordered[offset].Bounds.Height, 0.005);
                rowSteps[tagKey] = ownHeight + settings.RowSpacing;
            }
        }
        return result;
    }

    private static List<(AppliedTagWorkItem Item, LayoutRect Bounds)> AlignActualGroupsToCenterRail(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        IReadOnlyDictionary<long, int> hostGroups,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        out int alignedGroupTags,
        out Dictionary<long, int> alignedHostGroups)
    {
        alignedGroupTags = 0;
        alignedHostGroups = source.ToDictionary(
            entry => entry.Item.Record.TagKey,
            entry => hostGroups.TryGetValue(entry.Item.Record.TagKey, out int group)
                ? group
                : ActualCachedGroup(entry.Item));
        if (source.Count == 0)
        {
            return [];
        }

        // Preserve the side already selected by the Auto preview. Rebalancing
        // by item count here used to move right-side hosts to the left during
        // the real project-family pass, so preview and written Revit differed.
        var sideByTag = new Dictionary<long, bool>(source.Count);
        foreach ((AppliedTagWorkItem item, LayoutRect _) in source)
        {
            sideByTag[item.Record.TagKey] = settings.AutoSide
                ? item.Placement.Head.U >= item.Record.Layout.Anchor.U
                : !settings.PlaceLeft;
        }

        // Auto receives one nearby rail per local host group and side. Compact
        // keeps the same local grouping even when the user forces Left/Right.
        // Standard fixed Left/Right remains the accepted single global rail.
        bool useLocalGroups = settings.AutoSide ||
                              settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups;
        var targetRails = new Dictionary<(int Group, bool RightSide), double>();
        IEnumerable<IGrouping<(int Group, bool RightSide),
            (AppliedTagWorkItem Item, LayoutRect Bounds)>> railGroups = source.GroupBy(member =>
        {
            int group = useLocalGroups && hostGroups.TryGetValue(
                member.Item.Record.TagKey,
                out int groupId)
                    ? groupId
                    : 0;
            return (group, sideByTag[member.Item.Record.TagKey]);
        });
        foreach (IGrouping<(int Group, bool RightSide),
                     (AppliedTagWorkItem Item, LayoutRect Bounds)> railGroup in railGroups)
        {
            List<(AppliedTagWorkItem Item, LayoutRect Bounds)> members = railGroup.ToList();
            bool rightSide = railGroup.Key.RightSide;

            double maximumTagWidth = members.Max(item =>
                Math.Max(item.Bounds.Width, 0.005));
            // Use the local robust host edge so a remote branch cannot pull a
            // complete annotation column across a large complex view.
            double robustHostEdge = rightSide
                ? Quantile(
                    members.Select(item => item.Item.Record.Layout.ElementBounds.MaxU),
                    0.80)
                : Quantile(
                    members.Select(item => item.Item.Record.Layout.ElementBounds.MinU),
                    0.20);
            // The requested visual rule is a common left edge for every text
            // block on each side. On the left, reserve the widest real project
            // family so its inner edge still clears the robust host boundary.
            // Do not retain the old median rail: Fixed Tag Distance is an exact
            // offset from the analyzed group edge, so an old/far tag cannot pull
            // the whole new column outward.
            // Adaptive preview has already selected one canonical rail for the
            // complete nearby-host component. Preserve that rail when the real
            // project Tag Family is measured; recomputing from each adaptive
            // subgroup's host edge recreates the visual staircase that the
            // preview deliberately removed. Standard retains its exact offset
            // rule below.
            bool followsNearbyCategory = settings.AutoSide && members.Any(item =>
                item.Item.Record.Layout.PreferLocalClustering);
            double targetRail = settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups ||
                                followsNearbyCategory
                ? Median(members.Select(item => item.Item.Placement.TagBounds.MinU))
                : rightSide
                    ? robustHostEdge + settings.OffsetFromElements
                    : robustHostEdge - settings.OffsetFromElements - maximumTagWidth;
            targetRail = rightSide
                ? Math.Min(
                    targetRail,
                    snapshot.Frame.MaxU - settings.Clearance - maximumTagWidth)
                : Math.Max(
                    targetRail,
                    snapshot.Frame.MinU + settings.Clearance);
            // The preview already chose a nearby visible DA/AT rail for Duct.
            // Measuring the real family may change its width, but must not run
            // another outward rail search and send the follower across the view.
            if (useLocalGroups && !followsNearbyCategory)
            {
                targetRail = FindNearestClearActualRail(
                    members,
                    snapshot,
                    settings,
                    targetRail,
                    rightSide,
                    maximumTagWidth);
            }
            targetRails[railGroup.Key] = targetRail;
        }

        if (settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups)
        {
            Dictionary<(int Group, bool RightSide), int> pocketGroups = HarmonizeActualPocketRails(
                source,
                hostGroups,
                sideByTag,
                targetRails,
                snapshot,
                settings);
            alignedHostGroups = source.ToDictionary(
                entry => entry.Item.Record.TagKey,
                entry =>
                {
                    int group = hostGroups.TryGetValue(
                        entry.Item.Record.TagKey,
                        out int known)
                            ? known
                            : ActualCachedGroup(entry.Item);
                    return pocketGroups[(group, sideByTag[entry.Item.Record.TagKey])];
                });
        }

        var result = new List<(AppliedTagWorkItem Item, LayoutRect Bounds)>(source.Count);
        foreach ((AppliedTagWorkItem item, LayoutRect bounds) in source)
        {
            bool rightSide = sideByTag[item.Record.TagKey];
            int group = useLocalGroups && hostGroups.TryGetValue(
                item.Record.TagKey,
                out int groupId)
                    ? groupId
                    : 0;
            double currentRail = bounds.MinU;
            double shiftU = targetRails[(group, rightSide)] - currentRail;
            if (Math.Abs(shiftU) > 1e-8)
            {
                item.Tag.TagHeadPosition += snapshot.RightDirection * shiftU;
                alignedGroupTags++;
            }
            result.Add((item, ShiftRect(bounds, shiftU, 0.0)));
        }
        return result;
    }

    private static Dictionary<(int Group, bool RightSide), int> HarmonizeActualPocketRails(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        IReadOnlyDictionary<long, int> hostGroups,
        IReadOnlyDictionary<long, bool> sideByTag,
        Dictionary<(int Group, bool RightSide), double> targetRails,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings)
    {
        // Separate adaptive groups may still occupy the same visible blank
        // pocket. Without this pass each group selects a slightly different X
        // rail, producing a staircase of tag edges. Join only nearby groups on
        // the same side, and only when one shared rail stays clear of real MEP.
        var groups = source
            .GroupBy(entry =>
            {
                int group = hostGroups.TryGetValue(
                    entry.Item.Record.TagKey,
                    out int known)
                        ? known
                        : ActualCachedGroup(entry.Item);
                return (Group: group, RightSide: sideByTag[entry.Item.Record.TagKey]);
            })
            .Select(group =>
            {
                List<(AppliedTagWorkItem Item, LayoutRect Bounds)> members = group.ToList();
                return (
                    Key: group.Key,
                    Members: members,
                    MinV: members.Min(entry => entry.Bounds.MinV),
                    MaxV: members.Max(entry => entry.Bounds.MaxV),
                    MaximumHeight: members.Max(entry => Math.Max(entry.Bounds.Height, 0.005)),
                    Rail: targetRails[group.Key]);
            })
            .OrderByDescending(group => group.MaxV)
            .ThenBy(group => group.Key.RightSide)
            .ThenBy(group => group.Key.Group)
            .ToList();
        var pocketGroups = new Dictionary<(int Group, bool RightSide), int>();
        if (groups.Count < 2)
        {
            for (int index = 0; index < groups.Count; index++)
            {
                pocketGroups[groups[index].Key] = index;
            }
            return pocketGroups;
        }

        int[] parents = Enumerable.Range(0, groups.Count).ToArray();
        double[] componentMinV = groups.Select(group => group.MinV).ToArray();
        double[] componentMaxV = groups.Select(group => group.MaxV).ToArray();
        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        void Union(int first, int second, double maximumSpan)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot) return;
            double mergedMinV = Math.Min(componentMinV[firstRoot], componentMinV[secondRoot]);
            double mergedMaxV = Math.Max(componentMaxV[firstRoot], componentMaxV[secondRoot]);
            if (mergedMaxV - mergedMinV > maximumSpan) return;
            parents[secondRoot] = firstRoot;
            componentMinV[firstRoot] = mergedMinV;
            componentMaxV[firstRoot] = mergedMaxV;
        }

        static double VerticalGap(double firstMin, double firstMax, double secondMin, double secondMax)
        {
            if (firstMax < secondMin) return secondMin - firstMax;
            if (secondMax < firstMin) return firstMin - secondMax;
            return 0.0;
        }

        for (int first = 0; first < groups.Count; first++)
        {
            for (int second = first + 1; second < groups.Count; second++)
            {
                if (groups[first].Key.RightSide != groups[second].Key.RightSide) continue;
                double maximumHeight = Math.Max(
                    groups[first].MaximumHeight,
                    groups[second].MaximumHeight);
                double pocketGap = Math.Max(
                    maximumHeight * 2.5 + settings.RowSpacing * 2.0,
                    settings.OffsetFromElements * 2.0);
                double railTolerance = Math.Max(
                    settings.ColumnWidth * 0.75,
                    Math.Max(
                        settings.OffsetFromElements * 12.0,
                        maximumHeight * 10.0));
                if (VerticalGap(
                        groups[first].MinV,
                        groups[first].MaxV,
                        groups[second].MinV,
                        groups[second].MaxV) > pocketGap ||
                    Math.Abs(groups[first].Rail - groups[second].Rail) > railTolerance)
                {
                    continue;
                }
                double maximumSpan = Math.Max(
                    settings.ColumnWidth * 0.75,
                    maximumHeight * 16.0 + settings.RowSpacing * 15.0);
                Union(first, second, maximumSpan);
            }
        }

        int pocketId = 0;
        foreach (List<int> pocket in Enumerable.Range(0, groups.Count)
                     .GroupBy(Find)
                     .Select(component => component.ToList())
                     .OrderByDescending(component => component.Max(index => groups[index].MaxV))
                     .ThenBy(component => groups[component[0]].Key.RightSide))
        {
            if (pocket.Count < 2)
            {
                pocketGroups[groups[pocket[0]].Key] = pocketId++;
                continue;
            }
            List<double> candidateRails = pocket
                .Select(index => groups[index].Rail)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            candidateRails.Add(Median(candidateRails));
            int baselineConflicts = pocket.Sum(index => CountActualPocketRailTextConflicts(
                groups[index].Members,
                groups[index].Rail,
                snapshot,
                settings));
            double bestRail = candidateRails[0];
            int bestConflicts = int.MaxValue;
            double bestTravel = double.MaxValue;
            foreach (double candidateRail in candidateRails.Distinct())
            {
                int conflicts = pocket.Sum(index => CountActualPocketRailTextConflicts(
                    groups[index].Members,
                    candidateRail,
                    snapshot,
                    settings));
                double travel = pocket.Sum(index =>
                    Math.Abs(candidateRail - groups[index].Rail) * groups[index].Members.Count);
                if (conflicts < bestConflicts ||
                    conflicts == bestConflicts && travel < bestTravel - 1e-10)
                {
                    bestRail = candidateRail;
                    bestConflicts = conflicts;
                    bestTravel = travel;
                }
            }
            if (bestConflicts > baselineConflicts)
            {
                // No common clear rail exists: retain the original independent
                // pockets rather than forcing their row grids to interact.
                foreach (int index in pocket)
                {
                    pocketGroups[groups[index].Key] = pocketId++;
                }
                continue;
            }
            foreach (int index in pocket)
            {
                pocketGroups[groups[index].Key] = pocketId;
                targetRails[groups[index].Key] = bestRail;
            }
            pocketId++;
        }
        return pocketGroups;
    }

    private static int CountActualPocketRailTextConflicts(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> members,
        double candidateRail,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings)
    {
        int conflicts = 0;
        foreach ((AppliedTagWorkItem item, LayoutRect bounds) in members)
        {
            LayoutRect candidateBounds = ShiftRect(
                bounds,
                candidateRail - bounds.MinU,
                0.0);
            if (candidateBounds.MinU < snapshot.Frame.MinU + settings.Clearance ||
                candidateBounds.MaxU > snapshot.Frame.MaxU - settings.Clearance)
            {
                conflicts += 10_000;
                continue;
            }
            foreach (LayoutObstacle obstacle in snapshot.Obstacles)
            {
                if (obstacle.Kind != LayoutObstacleKind.Mep ||
                    settings.LayoutStyle != SmartTagLayoutStyle.CompactGroups &&
                    (obstacle.ElementKey == item.Record.ElementId ||
                     ObstacleContainsHostAnchor(item.Record, obstacle, settings.Clearance)))
                {
                    continue;
                }
                if (candidateBounds.Intersects(
                        obstacle.Bounds.Expand(settings.Clearance),
                        0.0))
                {
                    conflicts++;
                }
            }
        }
        return conflicts;
    }

    private static double FindNearestClearActualRail(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> members,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        double baseRail,
        bool rightSide,
        double maximumTagWidth)
    {
        double outward = rightSide ? 1.0 : -1.0;
        double step = Math.Max(
            settings.Clearance,
            Math.Max(maximumTagWidth * 0.20, settings.OffsetFromElements * 0.50));
        double searchDistance = Math.Max(
            step * 6.0,
            Math.Min(settings.ColumnWidth * 0.30, maximumTagWidth * 4.0));
        int attempts = Math.Clamp((int)Math.Ceiling(searchDistance / step) + 1, 2, 18);
        double bestRail = baseRail;
        int bestTextConflicts = int.MaxValue;
        int bestLeaderConflicts = int.MaxValue;
        double bestTravel = double.MaxValue;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            double candidateRail = baseRail + outward * attempt * step;
            if (candidateRail < snapshot.Frame.MinU + settings.Clearance ||
                candidateRail + maximumTagWidth > snapshot.Frame.MaxU - settings.Clearance)
            {
                continue;
            }

            int textConflicts = 0;
            int leaderConflicts = 0;
            foreach ((AppliedTagWorkItem item, LayoutRect bounds) in members)
            {
                double shiftU = candidateRail - bounds.MinU;
                LayoutRect candidateBounds = ShiftRect(bounds, shiftU, 0.0);
                LayoutPoint head = Project(
                    item.Tag.TagHeadPosition,
                    snapshot.RightDirection,
                    snapshot.UpDirection);
                head = new LayoutPoint(head.U + shiftU, head.V);
                var leader = new LayoutSegment(head, item.Record.Layout.Anchor);
                foreach (LayoutObstacle obstacle in snapshot.Obstacles)
                {
                    if (obstacle.Kind != LayoutObstacleKind.Mep ||
                        settings.LayoutStyle != SmartTagLayoutStyle.CompactGroups &&
                        obstacle.ElementKey == item.Record.ElementId)
                    {
                        continue;
                    }
                    LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                    if (candidateBounds.Intersects(expanded, 0.0)) textConflicts++;
                    else if (settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups &&
                             obstacle.ElementKey != item.Record.ElementId &&
                             !ObstacleContainsHostAnchor(
                                 item.Record,
                                 obstacle,
                                 settings.Clearance) &&
                             SegmentIntersectsRect(leader, expanded))
                    {
                        leaderConflicts++;
                    }
                    else if (settings.LayoutStyle != SmartTagLayoutStyle.CompactGroups &&
                             obstacle.ElementKey != item.Record.ElementId &&
                             SegmentIntersectsRect(leader, expanded))
                    {
                        leaderConflicts++;
                    }
                }
            }

            double travel = Math.Abs(candidateRail - baseRail);
            if (textConflicts < bestTextConflicts ||
                textConflicts == bestTextConflicts &&
                (leaderConflicts < bestLeaderConflicts ||
                 leaderConflicts == bestLeaderConflicts && travel < bestTravel - 1e-10))
            {
                bestRail = candidateRail;
                bestTextConflicts = textConflicts;
                bestLeaderConflicts = leaderConflicts;
                bestTravel = travel;
            }
            if (bestTextConflicts == 0 && bestLeaderConflicts == 0) break;
        }
        return bestRail;
    }

    private static Dictionary<int, double> BuildActualGroupPivotRows(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        IReadOnlyDictionary<long, int> hostGroups)
    {
        return source
            .GroupBy(item => hostGroups.TryGetValue(item.Item.Record.TagKey, out int group)
                ? group
                : 0)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    double centerU = Median(group.Select(item => item.Item.Record.Layout.Anchor.U));
                    double centerV = Median(group.Select(item => item.Item.Record.Layout.Anchor.V));
                    return group
                        .OrderBy(item =>
                        {
                            LayoutPoint anchor = item.Item.Record.Layout.Anchor;
                            return Math.Abs(anchor.V - centerV) +
                                   Math.Abs(anchor.U - centerU) * 0.35;
                        })
                        .ThenBy(item => item.Item.Record.ElementId)
                        .First()
                        .Item.Record.Layout.Anchor.V;
                });
    }

    private static Dictionary<long, int> BuildActualHostGroups(
        IReadOnlyList<(AppliedTagWorkItem Item, LayoutRect Bounds)> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings)
    {
        bool hasEstablishedCategories = source.Any(item =>
            !item.Item.Record.Layout.PreferLocalClustering);
        bool hasDuctChanges = source.Any(item =>
            item.Item.Record.Layout.PreferLocalClustering);
        if (hasEstablishedCategories && hasDuctChanges)
        {
            // Preserve the accepted Air Terminal / Duct Accessory grouping.
            // Duct is an optional follower and must never merge two established
            // components or change their real-family row packing.
            Dictionary<long, int> established = BuildActualHostGroups(
                source.Where(item => !item.Item.Record.Layout.PreferLocalClustering).ToList(),
                snapshot,
                settings);
            Dictionary<long, int> duct = BuildActualHostGroups(
                source.Where(item => item.Item.Record.Layout.PreferLocalClustering).ToList(),
                snapshot,
                settings);
            int ductOffset = established.Count == 0
                ? 0
                : established.Values.Max() + 1;
            var separated = new Dictionary<long, int>(established);
            foreach ((long tagKey, int groupId) in duct)
                separated[tagKey] = groupId + ductOffset;
            return separated;
        }

        if (settings.LayoutStyle == SmartTagLayoutStyle.CompactGroups)
        {
            // Re-run the same obstacle-aware adaptive planner with the measured
            // project Tag Family size. Architecture is handled later by moving
            // the accepted group as a unit; it must not redefine MEP grouping.
            return SmartTagCompactGroupLayout.BuildAdaptiveGroupMap(
                source.Select(item => item.Item.Record.Layout with
                    {
                        TagWidth = Math.Max(item.Bounds.Width, 0.005),
                        TagHeight = Math.Max(item.Bounds.Height, 0.005)
                    })
                    .ToList(),
                snapshot.Obstacles
                    .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Mep)
                    .ToList(),
                snapshot.Frame,
                settings);
        }

        var result = new Dictionary<long, int>();
        if (source.Count == 0) return result;

        double medianWidth = Median(source.Select(item => Math.Max(item.Bounds.Width, 0.005)));
        double medianHeight = Median(source.Select(item => Math.Max(item.Bounds.Height, 0.005)));
        double horizontalGapLimit = Math.Max(
            medianWidth * 2.5,
            settings.ColumnWidth * 0.20);
        double verticalGapLimit = Math.Max(
            medianHeight * 4.0 + settings.RowSpacing * 3.0,
            settings.ColumnWidth * 0.22);
        double maximumHorizontalSpan = Math.Max(
            medianWidth * 7.0,
            settings.ColumnWidth * 0.60);
        double maximumVerticalSpan = Math.Max(
            medianHeight * 12.0 + settings.RowSpacing * 11.0,
            settings.ColumnWidth * 0.75);
        List<(AppliedTagWorkItem Item, LayoutRect Bounds)> ordered = source
            .OrderByDescending(item => item.Item.Record.Layout.Anchor.V)
            .ThenBy(item => item.Item.Record.Layout.Anchor.U)
            .ToList();

        int[] parents = Enumerable.Range(0, ordered.Count).ToArray();
        double[] minimumU = ordered
            .Select(item => item.Item.Record.Layout.Anchor.U)
            .ToArray();
        double[] maximumU = minimumU.ToArray();
        double[] minimumV = ordered
            .Select(item => item.Item.Record.Layout.Anchor.V)
            .ToArray();
        double[] maximumV = minimumV.ToArray();
        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot) return;
            double mergedMinimumU = Math.Min(minimumU[firstRoot], minimumU[secondRoot]);
            double mergedMaximumU = Math.Max(maximumU[firstRoot], maximumU[secondRoot]);
            double mergedMinimumV = Math.Min(minimumV[firstRoot], minimumV[secondRoot]);
            double mergedMaximumV = Math.Max(maximumV[firstRoot], maximumV[secondRoot]);
            if (mergedMaximumU - mergedMinimumU > maximumHorizontalSpan ||
                mergedMaximumV - mergedMinimumV > maximumVerticalSpan)
            {
                return;
            }
            parents[secondRoot] = firstRoot;
            minimumU[firstRoot] = mergedMinimumU;
            maximumU[firstRoot] = mergedMaximumU;
            minimumV[firstRoot] = mergedMinimumV;
            maximumV[firstRoot] = mergedMaximumV;
        }

        static double AxisGap(double firstMin, double firstMax, double secondMin, double secondMax)
        {
            if (firstMax < secondMin) return secondMin - firstMax;
            if (secondMax < firstMin) return firstMin - secondMax;
            return 0.0;
        }

        // Build bounded transitive groups from the real host bounding boxes. If
        // A is near B and B is near C, all three may share one annotation group
        // even when C is outside the old seed-to-candidate span. The component
        // span cap prevents a long connected system from becoming one giant rail
        // covering the full view.
        for (int first = 0; first < ordered.Count; first++)
        {
            LayoutRect firstHost = ordered[first].Item.Record.Layout.ElementBounds;
            for (int second = first + 1; second < ordered.Count; second++)
            {
                LayoutRect secondHost = ordered[second].Item.Record.Layout.ElementBounds;
                double horizontalGap = AxisGap(
                    firstHost.MinU,
                    firstHost.MaxU,
                    secondHost.MinU,
                    secondHost.MaxU);
                double verticalGap = AxisGap(
                    firstHost.MinV,
                    firstHost.MaxV,
                    secondHost.MinV,
                    secondHost.MaxV);
                if (horizontalGap <= horizontalGapLimit &&
                    verticalGap <= verticalGapLimit)
                {
                    Union(first, second);
                }
            }
        }

        var groupIds = new Dictionary<int, int>();
        int nextGroupId = 0;
        for (int index = 0; index < ordered.Count; index++)
        {
            int root = Find(index);
            if (!groupIds.TryGetValue(root, out int groupId))
            {
                groupId = nextGroupId++;
                groupIds[root] = groupId;
            }
            result[ordered[index].Item.Record.TagKey] = groupId;
        }
        return result;
    }

    private static bool HorizontalRangesOverlap(
        LayoutRect first,
        LayoutRect second,
        double clearance) =>
        first.MinU < second.MaxU + clearance &&
        first.MaxU > second.MinU - clearance;

    private static int ActualCollisionScore(
        SmartTagRecord record,
        LayoutRect tagBounds,
        LayoutSegment horizontal,
        LayoutSegment? tail,
        IReadOnlyList<LayoutRect> reservedBoxes,
        IReadOnlyList<LayoutSegment> reservedLeaders,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        int score = 0;
        foreach (LayoutRect box in reservedBoxes)
        {
            LayoutRect expanded = box.Expand(settings.Clearance);
            // Text overlap is never an acceptable trade for avoiding several
            // leader crossings. Keep this weight lexicographically dominant.
            if (settings.AvoidTagText && tagBounds.Intersects(expanded)) score += 10_000_000;
            if (settings.AvoidLeaders && SegmentIntersectsRect(horizontal, expanded)) score += 300;
            if (settings.AvoidLeaders && tail is LayoutSegment tailSegment &&
                SegmentIntersectsRect(tailSegment, expanded)) score += 300;
        }
        if (settings.AvoidLeaders)
        {
            foreach (LayoutSegment leader in reservedLeaders)
            {
                if (SegmentIntersectsRect(leader, tagBounds.Expand(settings.Clearance))) score += 300;
                if (SegmentsIntersect(horizontal, leader)) score += 500;
                if (tail is LayoutSegment tailSegment && SegmentsIntersect(tailSegment, leader)) score += 500;
            }
        }
        if (settings.AvoidElements)
        {
            foreach (LayoutObstacle obstacle in obstacles)
            {
                LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                bool textOverlap = tagBounds.Intersects(expanded);
                bool horizontalOverlap = SegmentIntersectsRect(horizontal, expanded);
                bool tailOverlap = tail is LayoutSegment architecturalTail &&
                                   SegmentIntersectsRect(architecturalTail, expanded);
                if (obstacle.Kind == LayoutObstacleKind.Architecture)
                {
                    // Keep tag text away from architecture whenever a valid row
                    // exists. Crossing a wall with a leader remains a soft cost:
                    // the tagged MEP host can physically be inside that wall.
                    if (textOverlap) score += 500_000;
                    bool ownsArchitecturalStart = ObstacleContainsHostAnchor(
                        record,
                        obstacle,
                        settings.Clearance);
                    if (!ownsArchitecturalStart && horizontalOverlap) score += 20_000;
                    if (!ownsArchitecturalStart && tailOverlap) score += 20_000;
                    continue;
                }
                if (textOverlap) score += 100_000;
                if (obstacle.ElementKey == record.ElementId ||
                    ObstacleContainsHostAnchor(record, obstacle, settings.Clearance))
                {
                    continue;
                }
                if (horizontalOverlap) score += 100_000;
                if (tailOverlap) score += 100_000;
            }
        }
        return score;
    }

    private static List<AppliedTagWorkItem> TranslateActualClustersAroundArchitecture(
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        var result = source.ToList();
        if (!settings.AvoidElements || result.Count == 0 ||
            snapshot.Obstacles.All(obstacle => obstacle.Kind != LayoutObstacleKind.Architecture))
        {
            return result;
        }

        Dictionary<long, LayoutTagInput> inputs = result.ToDictionary(
            item => item.Record.TagKey,
            item => item.Record.Layout);
        List<List<int>> clusters = Enumerable.Range(0, result.Count)
            .GroupBy(index =>
            {
                AppliedTagWorkItem item = result[index];
                int group = ActualCachedGroup(item);
                bool rightSide = item.Placement.Head.U >= item.Record.Layout.Anchor.U;
                return (group, rightSide);
            })
            .Select(group => group
                .OrderByDescending(index => result[index].Placement.Head.V)
                .ThenBy(index => result[index].Record.ElementId)
                .ToList())
            .OrderByDescending(group => group.Max(index => result[index].Placement.Head.V))
            .ToList();

        int movedClusters = 0;
        int movedTags = 0;
        foreach (List<int> clusterIndices in clusters)
        {
            if (clusterIndices.All(index =>
                    result[index].Record.Layout.PreferLocalClustering))
            {
                // Architecture clearance must not independently detach the
                // optional Duct stack from its nearby companion column.
                continue;
            }
            HashSet<int> clusterSet = clusterIndices.ToHashSet();
            List<TagLayoutPlacement> cluster = clusterIndices
                .Select(index => result[index].Placement)
                .ToList();
            List<TagLayoutPlacement> fixedPlacements = Enumerable.Range(0, result.Count)
                .Where(index => !clusterSet.Contains(index))
                .Select(index => result[index].Placement)
                .ToList();
            LayoutPoint shift = SmartTagArchitectureClearance.FindClusterShift(
                cluster,
                inputs,
                fixedPlacements,
                snapshot.Obstacles,
                snapshot.Frame,
                settings);
            if (Math.Abs(shift.U) <= 1e-8 && Math.Abs(shift.V) <= 1e-8)
            {
                continue;
            }

            foreach (int index in clusterIndices)
            {
                AppliedTagWorkItem original = result[index];
                TagLayoutPlacement translated = SmartTagArchitectureClearance.Translate(
                    original.Placement,
                    original.Record.Layout,
                    shift);
                original.Tag.TagHeadPosition +=
                    snapshot.RightDirection * shift.U +
                    snapshot.UpDirection * shift.V;
                result[index] = original with { Placement = translated };
                movedTags++;
            }
            movedClusters++;
        }

        warnings.Add(
            $"Architecture clearance: translated {movedClusters} complete cluster(s), " +
            $"{movedTags} tag(s); baseline row order and internal spacing preserved.");
        return result;
    }

    private static List<AppliedTagWorkItem> OptimizeActualRowOrder(
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        var result = source.ToList();
        if (result.Count < 2)
        {
            warnings.Add("Row-order analysis: no row requires reordering; X rails unchanged.");
            return result;
        }

        // Packing has already established the two accepted X rails and all row
        // slots. Analyze is allowed only to reorder tag identities inside those
        // existing slots. An insertion can move one conflicting tag several rows
        // up/down while shifting the intervening tags by one row, which escapes
        // the local minimum that adjacent pair swaps cannot solve.
        int reorderPasses = 0;
        int shiftedTags = 0;
        int totalLeaderReduction = 0;
        bool denseView = result.Count > 300;
        Dictionary<long, int> hostGroups = result.ToDictionary(
            item => item.Record.TagKey,
            ActualCachedGroup);
        // Variable real-family heights can expose a few additional inversions
        // after the uniform visible-gap pass. Ten bounded repairs are still
        // predictable on a dense view but are enough to clear the residual
        // crossings that a four-pass cap left behind.
        int maximumPasses = denseView ? 10 : Math.Min(result.Count, 32);
        for (int pass = 0; pass < maximumPasses; pass++)
        {
            (List<AppliedTagWorkItem> Items, List<int> Changed,
                int LeaderReduction, int TextReduction, int ModelReduction,
                double Travel)? best = null;
            foreach (List<int> block in BuildActualRowOrderBlocks(
                         result,
                         settings,
                         hostGroups))
            {
                if (block.Count < 2) continue;
                var conflictDegrees = block.ToDictionary(
                    index => index,
                    index => CountLeaderConflictDegree(
                        index,
                        result,
                        settings.Clearance));
                if (conflictDegrees.Values.All(degree => degree == 0)) continue;
                HashSet<int> movementCandidates = conflictDegrees
                    .Where(pair => pair.Value > 0)
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => result[pair.Key].Record.ElementId)
                    .Take(denseView ? 8 : 24)
                    .Select(pair => pair.Key)
                    .ToHashSet();

                List<int> orderedIndices = block
                    .OrderByDescending(index => result[index].Placement.Head.V)
                    .ThenBy(index => result[index].Record.ElementId)
                    .ToList();
                List<double> rowSlots = orderedIndices
                    .Select(index => result[index].Placement.Head.V)
                    .ToList();
                int maximumJump = denseView
                    ? Math.Min(4, orderedIndices.Count - 1)
                    : orderedIndices.Count <= 24
                    ? orderedIndices.Count - 1
                    : 12;
                for (int from = 0; from < orderedIndices.Count; from++)
                {
                    int movingIndex = orderedIndices[from];
                    if (!movementCandidates.Contains(movingIndex)) continue;
                    int minimumTo = Math.Max(0, from - maximumJump);
                    int maximumTo = Math.Min(
                        orderedIndices.Count - 1,
                        from + maximumJump);
                    for (int to = minimumTo; to <= maximumTo; to++)
                    {
                        if (to == from) continue;
                        var permutation = orderedIndices.ToList();
                        int movedIndex = permutation[from];
                        permutation.RemoveAt(from);
                        permutation.Insert(to, movedIndex);

                        var candidate = result.ToList();
                        var changed = new List<int>();
                        for (int slot = 0; slot < permutation.Count; slot++)
                        {
                            int itemIndex = permutation[slot];
                            double rowV = rowSlots[slot];
                            if (Math.Abs(
                                    result[itemIndex].Placement.Head.V - rowV) <= 1e-8)
                            {
                                continue;
                            }
                            candidate[itemIndex] = MoveAppliedTagToRow(
                                result[itemIndex],
                                rowV,
                                settings);
                            changed.Add(itemIndex);
                        }
                        if (changed.Count < 2) continue;

                        ActualAffectedQuality before = MeasureAffectedQuality(
                            result,
                            changed,
                            snapshot,
                            settings);
                        ActualAffectedQuality after = MeasureAffectedQuality(
                            candidate,
                            changed,
                            snapshot,
                            settings);
                        int leaderReduction =
                            before.LeaderCrossings - after.LeaderCrossings;
                        int textReduction = before.TextOverlaps - after.TextOverlaps;
                        int modelReduction = before.ModelOverlaps - after.ModelOverlaps;
                        // A row reorder must be a Pareto improvement: leader
                        // crossings decrease without creating a new text/model
                        // collision. This protects the accepted alignment.
                        if (leaderReduction <= 0 ||
                            textReduction < 0 ||
                            modelReduction < 0)
                        {
                            continue;
                        }

                        double travel = candidate.Sum(item => Math.Abs(
                            item.Placement.Head.V - item.Record.Layout.Anchor.V));
                        bool isBetter = best is null ||
                            leaderReduction > best.Value.LeaderReduction ||
                            leaderReduction == best.Value.LeaderReduction &&
                            textReduction > best.Value.TextReduction ||
                            leaderReduction == best.Value.LeaderReduction &&
                            textReduction == best.Value.TextReduction &&
                            modelReduction > best.Value.ModelReduction ||
                            leaderReduction == best.Value.LeaderReduction &&
                            textReduction == best.Value.TextReduction &&
                            modelReduction == best.Value.ModelReduction &&
                            travel < best.Value.Travel - 1e-10;
                        if (isBetter)
                        {
                            best = (
                                candidate,
                                changed,
                                leaderReduction,
                                textReduction,
                                modelReduction,
                                travel);
                        }
                    }
                }
            }

            if (best is null) break;
            foreach (int changedIndex in best.Value.Changed)
            {
                result[changedIndex].Tag.TagHeadPosition += snapshot.UpDirection *
                    (best.Value.Items[changedIndex].Placement.Head.V -
                     result[changedIndex].Placement.Head.V);
            }
            result = best.Value.Items;
            reorderPasses++;
            shiftedTags += best.Value.Changed.Count;
            totalLeaderReduction += best.Value.LeaderReduction;
        }

        warnings.Add(
            $"Row-order analysis: {reorderPasses} group reorder(s), {shiftedTags} tag row shift(s), " +
            $"{totalLeaderReduction} leader conflict(s) removed; X rails and row spacing unchanged.");
        return result;
    }

    private static List<List<int>> BuildActualRowOrderBlocks(
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagLayoutSettings settings,
        IReadOnlyDictionary<long, int> hostGroups)
    {
        var result = new List<List<int>>();
        double railResolution = Math.Max(settings.Clearance * 0.25, 0.0005);
        foreach (IGrouping<(bool RightSide, long Rail, int HostGroup), int> rail in
                 Enumerable.Range(0, source.Count).GroupBy(index =>
                 {
                     AppliedTagWorkItem item = source[index];
                     bool rightSide = item.Placement.Head.U >=
                                      item.Record.Layout.Anchor.U;
                      long rail = (long)Math.Round(
                          item.Placement.TagBounds.MinU / railResolution);
                      int hostGroup = hostGroups.TryGetValue(
                          item.Record.TagKey,
                          out int group) ? group : 0;
                      return (rightSide, rail, hostGroup);
                  }))
        {
            List<int> ordered = rail
                .OrderByDescending(index => source[index].Placement.Head.V)
                .ThenBy(index => source[index].Record.ElementId)
                .ToList();
            if (ordered.Count == 0) continue;
            result.Add(ordered);
        }
        return result;
    }

    private static AppliedTagWorkItem MoveAppliedTagToRow(
        AppliedTagWorkItem item,
        double rowV,
        SmartTagLayoutSettings settings)
    {
        TagLayoutPlacement old = item.Placement;
        double shiftV = rowV - old.Head.V;
        LayoutPoint head = new(old.Head.U, rowV);
        bool moved = Math.Abs(rowV - item.Record.Layout.Anchor.V) > 1e-7;
        LayoutPoint end = CreateActualHostEnd(
            item.Record.Layout,
            head.U,
            head.V,
            moved,
            settings,
            0.0,
            0.0);
        LayoutPoint elbow = moved
            ? new LayoutPoint(end.U, rowV)
            : end;
        TagLayoutPlacement placement = old with
        {
            Head = head,
            TagBounds = ShiftRect(old.TagBounds, 0.0, shiftV),
            End = end,
            Elbow = elbow,
            UsesElbow = moved,
            UsesFreeEnd = moved
        };
        return item with { Placement = placement };
    }

    private readonly record struct ActualAffectedQuality(
        int TextOverlaps,
        int LeaderCrossings,
        int ModelOverlaps);

    private static ActualAffectedQuality MeasureAffectedQuality(
        IReadOnlyList<AppliedTagWorkItem> source,
        IReadOnlyCollection<int> changedIndices,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings)
    {
        int textOverlaps = 0;
        int leaderCrossings = 0;
        var changed = changedIndices.ToHashSet();
        foreach (int changedIndex in changed)
        {
            for (int otherIndex = 0; otherIndex < source.Count; otherIndex++)
            {
                if (otherIndex == changedIndex) continue;
                if (changed.Contains(otherIndex) && otherIndex < changedIndex)
                {
                    continue;
                }
                (int pairText, int pairLeader) = CountPlacementPairConflicts(
                    source[changedIndex].Placement,
                    source[otherIndex].Placement,
                    settings.Clearance);
                textOverlaps += pairText;
                leaderCrossings += pairLeader;
            }
        }

        int modelOverlaps = 0;
        foreach (int changedIndex in changed)
        {
            AppliedTagWorkItem item = source[changedIndex];
            if (snapshot.Obstacles.Any(obstacle => PlacementIntersectsModel(
                    item.Placement,
                    item.Record,
                    obstacle,
                    settings.Clearance)))
            {
                modelOverlaps++;
            }
        }
        return new ActualAffectedQuality(
            textOverlaps,
            leaderCrossings,
            modelOverlaps);
    }

    private static (int TextOverlaps, int LeaderCrossings) CountPlacementPairConflicts(
        TagLayoutPlacement first,
        TagLayoutPlacement second,
        double clearance)
    {
        int textOverlaps = first.TagBounds.Intersects(
            second.TagBounds.Expand(clearance)) ? 1 : 0;
        int leaderCrossings = 0;
        List<LayoutSegment> firstLeaders =
        [
            new LayoutSegment(first.Head, first.Elbow)
        ];
        if (first.UsesElbow)
        {
            firstLeaders.Add(new LayoutSegment(first.End, first.Elbow));
        }
        List<LayoutSegment> secondLeaders =
        [
            new LayoutSegment(second.Head, second.Elbow)
        ];
        if (second.UsesElbow)
        {
            secondLeaders.Add(new LayoutSegment(second.End, second.Elbow));
        }
        foreach (LayoutSegment firstLeader in firstLeaders)
        foreach (LayoutSegment secondLeader in secondLeaders)
        {
            if (SegmentsIntersect(firstLeader, secondLeader)) leaderCrossings++;
        }
        foreach (LayoutSegment firstLeader in firstLeaders)
        {
            if (SegmentIntersectsRect(
                    firstLeader,
                    second.TagBounds.Expand(clearance)))
            {
                leaderCrossings++;
            }
        }
        foreach (LayoutSegment secondLeader in secondLeaders)
        {
            if (SegmentIntersectsRect(
                    secondLeader,
                    first.TagBounds.Expand(clearance)))
            {
                leaderCrossings++;
            }
        }
        return (textOverlaps, leaderCrossings);
    }

    private static List<AppliedTagWorkItem> OptimizeActualLeaderLanes(
        IReadOnlyList<AppliedTagWorkItem> source,
        SmartTagViewSnapshot snapshot,
        SmartTagLayoutSettings settings,
        ICollection<string> warnings)
    {
        var result = source.ToList();
        int improvements = 0;
        // The first packing pass is intentionally greedy. Revisit every routed
        // leader against the complete result so an early lane cannot force many
        // later leaders to cross it. Text rows and the two global X rails stay
        // immutable during this refinement.
        bool denseView = result.Count > 300;
        int maximumPasses = denseView ? 1 : 3;
        int maximumRoutes = denseView ? 80 : result.Count;
        for (int pass = 0; pass < maximumPasses; pass++)
        {
            bool changed = false;
            // Rebuild the conflict graph every pass. Routes cutting the most
            // neighbors are the constrained backbone and are solved first;
            // clear straight leaders remain immutable below.
            List<int> processingOrder = Enumerable.Range(0, result.Count)
                .OrderByDescending(index => CountLeaderConflictDegree(
                    index,
                    result,
                    settings.Clearance))
                .ThenBy(index => result[index].Placement.UsesElbow ? 1 : 0)
                .ThenByDescending(index => Math.Abs(
                    result[index].Placement.Head.V -
                    result[index].Record.Layout.Anchor.V))
                .ThenByDescending(index => result[index].Placement.Head.V)
                .ThenBy(index => result[index].Record.ElementId)
                .Take(maximumRoutes)
                .ToList();
            foreach (int index in processingOrder)
            {
                AppliedTagWorkItem current = result[index];
                var otherBoxes = new List<LayoutRect>(result.Count - 1);
                var otherLeaders = new List<LayoutSegment>((result.Count - 1) * 2);
                for (int otherIndex = 0; otherIndex < result.Count; otherIndex++)
                {
                    if (otherIndex == index) continue;
                    TagLayoutPlacement other = result[otherIndex].Placement;
                    otherBoxes.Add(other.TagBounds);
                    otherLeaders.Add(new LayoutSegment(other.Head, other.Elbow));
                    if (other.UsesElbow)
                    {
                        otherLeaders.Add(new LayoutSegment(other.End, other.Elbow));
                    }
                }

                LayoutSegment currentHorizontal = new(
                    current.Placement.Head,
                    current.Placement.Elbow);
                LayoutSegment? currentTail = current.Placement.UsesElbow
                    ? new LayoutSegment(current.Placement.End, current.Placement.Elbow)
                    : null;
                int bestCritical = ActualCriticalConflictCount(
                    current.Record,
                    current.Placement.TagBounds,
                    currentHorizontal,
                    currentTail,
                    otherBoxes,
                    otherLeaders,
                    snapshot.Obstacles,
                    settings);
                int bestScore = ActualCollisionScore(
                    current.Record,
                    current.Placement.TagBounds,
                    currentHorizontal,
                    currentTail,
                    otherBoxes,
                    otherLeaders,
                    snapshot.Obstacles,
                    settings);
                double bestEndpointTravel = DistanceSquared(
                    current.Placement.End,
                    current.Record.Layout.Anchor);
                LayoutPoint bestEnd = current.Placement.End;
                LayoutPoint bestElbow = current.Placement.Elbow;
                bool bestUsesElbow = current.Placement.UsesElbow;

                // A genuinely clear route is an anchor of the layout. In
                // particular, never turn a clear straight leader into an elbow.
                if (bestCritical == 0 && bestScore == 0)
                {
                    continue;
                }

                LayoutPoint head = current.Placement.Head;
                bool moved = Math.Abs(
                    head.V - current.Record.Layout.Anchor.V) > 1e-7;
                int minimumHorizontalLane = moved ? 0 : -4;
                int maximumHorizontalLane = moved ? 8 : 4;
                int verticalLaneCount = moved ? 2 : 0;
                for (int horizontalLane = minimumHorizontalLane;
                     horizontalLane <= maximumHorizontalLane;
                     horizontalLane++)
                {
                    double horizontalFactor = moved
                        ? horizontalLane / 8.0
                        : horizontalLane / 4.0;
                    for (int verticalLane = -verticalLaneCount;
                         verticalLane <= verticalLaneCount;
                         verticalLane++)
                    {
                        double verticalFactor = verticalLaneCount == 0
                            ? 0.0
                            : verticalLane / (double)verticalLaneCount;
                        LayoutPoint end = CreateActualHostEnd(
                            current.Record.Layout,
                            head.U,
                            head.V,
                            moved,
                            settings,
                            horizontalFactor,
                            verticalFactor);
                        LayoutPoint elbow = moved
                            ? new LayoutPoint(end.U, head.V)
                            : end;
                        var horizontal = new LayoutSegment(head, elbow);
                        LayoutSegment? tail = moved
                            ? new LayoutSegment(end, elbow)
                            : null;
                        int critical = ActualCriticalConflictCount(
                            current.Record,
                            current.Placement.TagBounds,
                            horizontal,
                            tail,
                            otherBoxes,
                            otherLeaders,
                            snapshot.Obstacles,
                            settings);
                        int score = ActualCollisionScore(
                            current.Record,
                            current.Placement.TagBounds,
                            horizontal,
                            tail,
                            otherBoxes,
                            otherLeaders,
                            snapshot.Obstacles,
                            settings);
                        double endpointTravel = DistanceSquared(
                            end,
                            current.Record.Layout.Anchor);
                        if (critical < bestCritical ||
                            critical == bestCritical && score < bestScore ||
                            critical == bestCritical && score == bestScore &&
                            endpointTravel < bestEndpointTravel - 1e-10)
                        {
                            bestCritical = critical;
                            bestScore = score;
                            bestEndpointTravel = endpointTravel;
                            bestEnd = end;
                            bestElbow = elbow;
                            bestUsesElbow = moved;
                        }
                    }
                }

                if (DistanceSquared(bestEnd, current.Placement.End) > 1e-12 ||
                    DistanceSquared(bestElbow, current.Placement.Elbow) > 1e-12)
                {
                    TagLayoutPlacement refined = current.Placement with
                    {
                        End = bestEnd,
                        Elbow = bestElbow,
                        UsesElbow = bestUsesElbow,
                        UsesFreeEnd = bestUsesElbow,
                        HasClash = bestCritical > 0
                    };
                    result[index] = current with { Placement = refined };
                    improvements++;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        warnings.Add($"Leader-lane refinement: {improvements} route improvement(s) applied on the fixed rails.");
        return result;
    }

    private static int CountLeaderConflictDegree(
        int index,
        IReadOnlyList<AppliedTagWorkItem> source,
        double clearance)
    {
        TagLayoutPlacement placement = source[index].Placement;
        var ownSegments = new List<LayoutSegment>
        {
            new(placement.Head, placement.Elbow)
        };
        if (placement.UsesElbow)
        {
            ownSegments.Add(new LayoutSegment(placement.End, placement.Elbow));
        }

        int conflicts = 0;
        for (int otherIndex = 0; otherIndex < source.Count; otherIndex++)
        {
            if (otherIndex == index) continue;
            TagLayoutPlacement other = source[otherIndex].Placement;
            var otherSegments = new List<LayoutSegment>
            {
                new(other.Head, other.Elbow)
            };
            if (other.UsesElbow)
            {
                otherSegments.Add(new LayoutSegment(other.End, other.Elbow));
            }

            bool conflict = ownSegments.Any(segment =>
                SegmentIntersectsRect(segment, other.TagBounds.Expand(clearance))) ||
                otherSegments.Any(segment =>
                    SegmentIntersectsRect(segment, placement.TagBounds.Expand(clearance))) ||
                ownSegments.Any(first =>
                    otherSegments.Any(second => SegmentsIntersect(first, second)));
            if (conflict) conflicts++;
        }
        return conflicts;
    }

    private static double DistanceSquared(LayoutPoint first, LayoutPoint second)
    {
        double du = first.U - second.U;
        double dv = first.V - second.V;
        return du * du + dv * dv;
    }

    private static int ActualCriticalConflictCount(
        SmartTagRecord record,
        LayoutRect tagBounds,
        LayoutSegment horizontal,
        LayoutSegment? tail,
        IReadOnlyList<LayoutRect> reservedBoxes,
        IReadOnlyList<LayoutSegment> reservedLeaders,
        IReadOnlyList<LayoutObstacle> obstacles,
        SmartTagLayoutSettings settings)
    {
        int conflicts = 0;
        foreach (LayoutRect box in reservedBoxes)
        {
            LayoutRect expanded = box.Expand(settings.Clearance);
            if (settings.AvoidTagText && tagBounds.Intersects(expanded)) conflicts++;
            if (settings.AvoidLeaders && SegmentIntersectsRect(horizontal, expanded)) conflicts++;
            if (settings.AvoidLeaders && tail is LayoutSegment tailSegment &&
                SegmentIntersectsRect(tailSegment, expanded)) conflicts++;
        }
        if (settings.AvoidLeaders)
        {
            LayoutRect expandedTag = tagBounds.Expand(settings.Clearance);
            foreach (LayoutSegment leader in reservedLeaders)
            {
                if (SegmentIntersectsRect(leader, expandedTag)) conflicts++;
                if (SegmentsIntersect(horizontal, leader)) conflicts++;
                if (tail is LayoutSegment tailSegment &&
                    SegmentsIntersect(tailSegment, leader)) conflicts++;
            }
        }
        if (settings.AvoidElements)
        {
            foreach (LayoutObstacle obstacle in obstacles)
            {
                LayoutRect expanded = obstacle.Bounds.Expand(settings.Clearance);
                if (tagBounds.Intersects(expanded)) conflicts++;
                if (obstacle.Kind == LayoutObstacleKind.Architecture)
                {
                    // A leader may leave an architectural object containing its
                    // own MEP host, but it may not cut unrelated walls, stairs,
                    // doors, or linked-model geometry on the way to the text.
                    if (ObstacleContainsHostAnchor(record, obstacle, settings.Clearance))
                    {
                        continue;
                    }
                    if (SegmentIntersectsRect(horizontal, expanded)) conflicts++;
                    if (tail is LayoutSegment architectureTail &&
                        SegmentIntersectsRect(architectureTail, expanded))
                    {
                        conflicts++;
                    }
                    continue;
                }
                if (obstacle.ElementKey == record.ElementId ||
                    ObstacleContainsHostAnchor(record, obstacle, settings.Clearance))
                {
                    continue;
                }
                if (SegmentIntersectsRect(horizontal, expanded)) conflicts++;
                if (tail is LayoutSegment tailSegment &&
                    SegmentIntersectsRect(tailSegment, expanded)) conflicts++;
            }
        }
        return conflicts;
    }

    private static bool PlacementIntersectsModel(
        TagLayoutPlacement placement,
        SmartTagRecord record,
        LayoutObstacle obstacle,
        double clearance)
    {
        LayoutRect expanded = obstacle.Bounds.Expand(clearance);
        // Tag text may not cover even its own host. Only the leader is allowed
        // to touch the tagged host (and a connected element sharing the anchor).
        if (placement.TagBounds.Intersects(expanded)) return true;
        if (obstacle.Kind == LayoutObstacleKind.Architecture)
        {
            if (ObstacleContainsHostAnchor(record, obstacle, clearance)) return false;
            if (SegmentIntersectsRect(
                    new LayoutSegment(placement.Head, placement.Elbow),
                    expanded))
            {
                return true;
            }
            return placement.UsesElbow && SegmentIntersectsRect(
                new LayoutSegment(placement.End, placement.Elbow),
                expanded);
        }
        if (obstacle.ElementKey == record.ElementId ||
            ObstacleContainsHostAnchor(record, obstacle, clearance))
        {
            return false;
        }
        if (SegmentIntersectsRect(
                new LayoutSegment(placement.Head, placement.Elbow),
                expanded))
        {
            return true;
        }
        return placement.UsesElbow && SegmentIntersectsRect(
            new LayoutSegment(placement.End, placement.Elbow),
            expanded);
    }

    private static bool ObstacleContainsHostAnchor(
        SmartTagRecord record,
        LayoutObstacle obstacle,
        double clearance)
    {
        LayoutRect bounds = obstacle.Bounds.Expand(Math.Max(0.0, clearance * 0.25));
        LayoutPoint anchor = record.Layout.Anchor;
        return anchor.U >= bounds.MinU && anchor.U <= bounds.MaxU &&
               anchor.V >= bounds.MinV && anchor.V <= bounds.MaxV;
    }

    private static LayoutPoint CreateActualHostEnd(
        LayoutTagInput input,
        double headU,
        double headV,
        bool moved,
        SmartTagLayoutSettings settings,
        double laneFactor,
        double verticalLaneFactor = 0.0)
    {
        double hostWidth = Math.Max(0.0, input.ElementBounds.Width);
        if (!moved)
        {
            double straightRange = Math.Min(
                hostWidth * 0.40,
                Math.Max(settings.Clearance * 1.5, 0.005));
            double straightU = Math.Clamp(
                input.Anchor.U + Math.Clamp(laneFactor, -1.0, 1.0) * straightRange,
                input.ElementBounds.MinU,
                input.ElementBounds.MaxU);
            return new LayoutPoint(straightU, input.Anchor.V);
        }
        double direction = Math.Sign(headV - input.Anchor.V);
        double available = direction > 0
            ? input.ElementBounds.MaxV - input.Anchor.V
            : input.Anchor.V - input.ElementBounds.MinV;
        double shift = Math.Min(
            Math.Max(0.0, available) * 0.5,
            Math.Max(settings.Clearance * 0.75, 0.005));
        bool tagIsRight = headU >= input.Anchor.U;
        double inward = hostWidth * 0.8 * Math.Clamp(laneFactor, 0.0, 1.0);
        // Free End begins at the host edge nearest the tag, then tries a few
        // tiny inward lanes. This shortens the route and prevents a leader from
        // unnecessarily traversing the host or nearby elements.
        double shiftedU = tagIsRight
            ? input.ElementBounds.MaxU - inward
            : input.ElementBounds.MinU + inward;
        double baseV = input.Anchor.V + direction * shift;
        double hostHeight = Math.Max(0.0, input.ElementBounds.Height);
        double verticalRange = Math.Min(
            hostHeight * 0.35,
            Math.Max(settings.Clearance * 1.5, 0.005));
        double shiftedV = baseV;
        if (hostHeight > 1e-8)
        {
            shiftedV = Math.Clamp(
                baseV + Math.Clamp(verticalLaneFactor, -1.0, 1.0) * verticalRange,
                input.ElementBounds.MinV,
                input.ElementBounds.MaxV);
        }
        return new LayoutPoint(shiftedU, shiftedV);
    }

    private static LayoutRect ShiftRect(LayoutRect rect, double du, double dv) => new(
        rect.MinU + du,
        rect.MinV + dv,
        rect.MaxU + du,
        rect.MaxV + dv);

    private static double RectDistanceSquared(LayoutRect first, LayoutRect second)
    {
        double du = first.MaxU < second.MinU
            ? second.MinU - first.MaxU
            : second.MaxU < first.MinU
                ? first.MinU - second.MaxU
                : 0.0;
        double dv = first.MaxV < second.MinV
            ? second.MinV - first.MaxV
            : second.MaxV < first.MinV
                ? first.MinV - second.MaxV
                : 0.0;
        return du * du + dv * dv;
    }

    private static bool SegmentIntersectsRect(LayoutSegment segment, LayoutRect rect)
    {
        double minU = Math.Min(segment.Start.U, segment.End.U);
        double maxU = Math.Max(segment.Start.U, segment.End.U);
        double minV = Math.Min(segment.Start.V, segment.End.V);
        double maxV = Math.Max(segment.Start.V, segment.End.V);
        if (maxU < rect.MinU || minU > rect.MaxU || maxV < rect.MinV || minV > rect.MaxV)
        {
            return false;
        }
        if (segment.IsHorizontal)
        {
            return segment.Start.V >= rect.MinV && segment.Start.V <= rect.MaxV;
        }
        if (segment.IsVertical)
        {
            return segment.Start.U >= rect.MinU && segment.Start.U <= rect.MaxU;
        }
        return SegmentsIntersect(segment, new LayoutSegment(
                   new LayoutPoint(rect.MinU, rect.MinV), new LayoutPoint(rect.MaxU, rect.MinV))) ||
               SegmentsIntersect(segment, new LayoutSegment(
                   new LayoutPoint(rect.MaxU, rect.MinV), new LayoutPoint(rect.MaxU, rect.MaxV))) ||
               SegmentsIntersect(segment, new LayoutSegment(
                   new LayoutPoint(rect.MaxU, rect.MaxV), new LayoutPoint(rect.MinU, rect.MaxV))) ||
               SegmentsIntersect(segment, new LayoutSegment(
                   new LayoutPoint(rect.MinU, rect.MaxV), new LayoutPoint(rect.MinU, rect.MinV)));
    }

    private static bool SegmentsIntersect(LayoutSegment first, LayoutSegment second)
    {
        static double Cross(LayoutPoint a, LayoutPoint b, LayoutPoint c) =>
            (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        static bool OnSegment(LayoutPoint a, LayoutPoint b, LayoutPoint p) =>
            p.U >= Math.Min(a.U, b.U) - 1e-8 && p.U <= Math.Max(a.U, b.U) + 1e-8 &&
            p.V >= Math.Min(a.V, b.V) - 1e-8 && p.V <= Math.Max(a.V, b.V) + 1e-8;

        double c1 = Cross(first.Start, first.End, second.Start);
        double c2 = Cross(first.Start, first.End, second.End);
        double c3 = Cross(second.Start, second.End, first.Start);
        double c4 = Cross(second.Start, second.End, first.End);
        if (((c1 > 1e-8 && c2 < -1e-8) || (c1 < -1e-8 && c2 > 1e-8)) &&
            ((c3 > 1e-8 && c4 < -1e-8) || (c3 < -1e-8 && c4 > 1e-8)))
        {
            return true;
        }
        if (Math.Abs(c1) <= 1e-8 && OnSegment(first.Start, first.End, second.Start)) return true;
        if (Math.Abs(c2) <= 1e-8 && OnSegment(first.Start, first.End, second.End)) return true;
        if (Math.Abs(c3) <= 1e-8 && OnSegment(second.Start, second.End, first.Start)) return true;
        return Math.Abs(c4) <= 1e-8 && OnSegment(second.Start, second.End, first.End);
    }

    private static IReadOnlyList<TagLayoutPlacement> MeasureNearHostReservations(
        Document document, SmartTagViewSnapshot snapshot, IReadOnlyList<AppliedTagWorkItem> moving)
    {
        var movingIds = moving.Select(item => item.Tag.Id.Value).ToHashSet();
        HashSet<long> fixedFollowerAnchorIds = snapshot.Tags
            .Where(item => item.Layout.CanAnchorDuctFollowers &&
                           !item.WillCreate &&
                           item.ExistingTagId > 0)
            .Select(item => item.ExistingTagId)
            .ToHashSet();
        var fixedTags = new FilteredElementCollector(document, document.ActiveView.Id)
            .OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(tag => !movingIds.Contains(tag.Id.Value)).ToList();
        // Avoid a Revit regeneration/rollback when there are no fixed tags to measure.
        if (fixedTags.Count == 0) return [];
        var result = new List<TagLayoutPlacement>();
        using var measurement = new SubTransaction(document);
        measurement.Start();
        try
        {
            var routes = new List<(IndependentTag Tag, LayoutPoint Head, LayoutPoint End, LayoutPoint Elbow, bool HasElbow)>();
            foreach (var tag in fixedTags)
            {
                var head = Project(tag.TagHeadPosition, snapshot.RightDirection, snapshot.UpDirection);
                bool recorded = false;
                if (tag.HasLeader)
                {
                    foreach (var reference in tag.GetTaggedReferences())
                    {
                        try
                        {
                            var end = Project(tag.GetLeaderEnd(reference), snapshot.RightDirection, snapshot.UpDirection);
                            bool hasElbow = tag.HasLeaderElbow(reference);
                            var elbow = hasElbow ? Project(tag.GetLeaderElbow(reference), snapshot.RightDirection, snapshot.UpDirection) : end;
                            routes.Add((tag, head, end, elbow, hasElbow));
                            recorded = true;
                        }
                        catch { /* Attached endpoints may not be available; reserve text below. */ }
                    }
                }
                if (!recorded) routes.Add((tag, head, head, head, false));
                try { tag.HasLeader = false; } catch { }
            }
            document.Regenerate();
            var textBounds = new Dictionary<long, LayoutRect>();
            foreach (var route in routes)
            {
                if (!textBounds.TryGetValue(route.Tag.Id.Value, out LayoutRect bounds))
                {
                    var box = route.Tag.get_BoundingBox(document.ActiveView);
                    if (box is null) continue;
                    bounds = ProjectBox(box, snapshot.RightDirection, snapshot.UpDirection);
                    textBounds[route.Tag.Id.Value] = bounds;
                }
                result.Add(new TagLayoutPlacement(route.Tag.Id.Value, route.Head, route.End, route.Elbow,
                    bounds, route.HasElbow, true, false, "", "")
                {
                    CanAnchorDuctFollowers = fixedFollowerAnchorIds.Contains(route.Tag.Id.Value)
                });
            }
        }
        finally { measurement.RollBack(); }
        return result;
    }

    private static void ConfigureLeader(
        AppliedTagWorkItem item,
        SmartTagViewSnapshot snapshot)
    {
        IndependentTag tag = item.Tag;
        TagLayoutPlacement placement = item.Placement;
        try { tag.TagOrientation = TagOrientation.Horizontal; } catch { }
        tag.HasLeader = true;
        // Re-apply the analyzed head on the exact active-view plane immediately
        // before writing leader geometry. Real project families can move their
        // insertion point slightly after regeneration; leaving that drift here
        // makes a mathematically horizontal route look diagonal in Revit.
        tag.TagHeadPosition = MoveInViewPlane(
            tag.TagHeadPosition,
            placement.Head,
            snapshot.RightDirection,
            snapshot.UpDirection);
        // Use TagHeadPosition as the common 3D plane origin. Host locations and
        // old tag heads can have different normal/elevation components even
        // though their U/V projections look equal, which lets Revit render a
        // nominally horizontal leader as a diagonal line.
        // Do not trust tiny floating-point drift left by row packing or by an
        // Attached endpoint selected internally by Revit.  Normalize in view
        // coordinates first: a straight route has one constant V; an elbow
        // route has a horizontal head segment and one vertical host segment.
        LayoutPoint normalizedEnd = placement.UsesElbow
            ? placement.End
            : new LayoutPoint(placement.End.U, placement.Head.V);
        LayoutPoint normalizedElbow = placement.UsesElbow
            ? new LayoutPoint(normalizedEnd.U, placement.Head.V)
            : placement.Head;

        XYZ leaderPlaneOrigin = tag.TagHeadPosition;
        XYZ end = MoveInViewPlane(
            leaderPlaneOrigin,
            normalizedEnd,
            snapshot.RightDirection,
            snapshot.UpDirection);
        XYZ elbow = MoveInViewPlane(
            leaderPlaneOrigin,
            normalizedElbow,
            snapshot.RightDirection,
            snapshot.UpDirection);

        // A clear tag must be exactly one horizontal segment. Revit may choose a
        // different Attached endpoint and add a short vertical/diagonal leg, so
        // write an exact Free endpoint at the analyzed host anchor and collapse
        // the elbow at TagHeadPosition. The endpoint remains on the correct host.
        if (!placement.UsesElbow)
        {
            try { tag.LeaderEndCondition = LeaderEndCondition.Free; } catch { }
            try { tag.SetLeaderEnd(item.Reference, end); } catch { }
            try { tag.SetLeaderElbow(item.Reference, tag.TagHeadPosition); } catch { }
            return;
        }

        // An Attached leader lets Revit choose another point on the host. That
        // point can differ in both U and V from the analyzed endpoint and turns
        // the final leg diagonal. Keep the analyzed point on the same host, but
        // write it as Free End so the 90-degree geometry is deterministic.
        try { tag.LeaderEndCondition = LeaderEndCondition.Free; } catch { }
        try { tag.SetLeaderEnd(item.Reference, end); } catch { }
        try { tag.SetLeaderElbow(item.Reference, elbow); } catch { }
    }

    public static SmartTagActualPreviewResult PreviewActualTags(
        UIApplication application,
        SmartTagViewSnapshot snapshot,
        IReadOnlyList<TagLayoutPlacement> placements,
        IReadOnlyDictionary<string, long> tagTypeIds,
        SmartTagLayoutSettings settings)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("No active Revit project.");
        Document document = uidoc.Document;
        UIView uiView = uidoc.GetOpenUIViews()
            .FirstOrDefault(item => item.ViewId == document.ActiveView.Id)
            ?? throw new InvalidOperationException("The active Revit view is not open on screen.");
        using var group = new TransactionGroup(document, "FamilyMEP - Temporary Smart Tag Preview");
        group.Start();
        try
        {
            SmartTagApplyResult result = Apply(
                application,
                snapshot,
                placements,
                tagTypeIds,
                settings,
                includeClashes: true,
                previewOnly: true);
            // Apply already commits its inner transaction, which regenerates the
            // document. Calling Document.Regenerate here is illegal because the
            // TransactionGroup itself does not make Document.IsModifiable true.
            WaitForActualTagPreviewPaint(
                uidoc,
                application.MainWindowHandle,
                result.Applied);
            var window = uiView.GetWindowRectangle();
            int width = Math.Max(1, window.Right - window.Left);
            int height = Math.Max(1, window.Bottom - window.Top);
            byte[] image = CaptureViewport(
                application.MainWindowHandle,
                window.Left,
                window.Top,
                width,
                height);
            return new SmartTagActualPreviewResult(image, result);
        }
        finally
        {
            group.RollBack();
            uidoc.RefreshActiveView();
        }
    }

    private static void WaitForActualTagPreviewPaint(
        UIDocument uidoc,
        IntPtr revitWindow,
        int appliedTagCount)
    {
        // A fixed 120 ms pause was sufficient for small views, but on a dense
        // plan Revit may still be painting the pre-transaction frame when the
        // viewport is copied. Refresh and synchronously redraw several times;
        // the bounded adaptive wait keeps a 900-tag view responsive while
        // ensuring that temporary project-family tags are present in the image.
        int passCount = Math.Clamp((appliedTagCount + 79) / 80, 2, 6);
        int totalWaitMilliseconds = Math.Clamp(
            180 + appliedTagCount * 3,
            320,
            1400);
        int waitPerPass = Math.Max(80, totalWaitMilliseconds / passCount);
        for (int pass = 0; pass < passCount; pass++)
        {
            uidoc.RefreshActiveView();
            if (revitWindow != IntPtr.Zero)
            {
                NativeMethods.RedrawWindow(
                    revitWindow,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.Invalidate |
                    NativeMethods.UpdateNow |
                    NativeMethods.AllChildren);
            }
            try { NativeMethods.DwmFlush(); } catch { }
            Thread.Sleep(waitPerPass);
        }
    }

    private static List<SmartTagRecord> CollectTagCandidates(
        Document document,
        View view,
        XYZ right,
        XYZ up,
        LayoutRect frame,
        IReadOnlySet<long>? elementFilter)
    {
        var ductEligibility = new Dictionary<long, bool>();
        List<SmartTagRecord> result = CollectExistingTags(
            document,
            view,
            right,
            up,
            frame,
            elementFilter,
            ductEligibility);
        var alreadyTagged = result.Select(item => item.ElementId).ToHashSet();
        var sizeByGroup = result
            .GroupBy(item => item.Layout.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (
                    Width: Median(group.Select(item => item.Layout.TagWidth)),
                    Height: Median(group.Select(item => item.Layout.TagHeight))),
                StringComparer.OrdinalIgnoreCase);

        IEnumerable<Element> elements = elementFilter is { Count: > 0 }
            ? elementFilter
                .Select(id => document.GetElement(new ElementId(id)))
                .Where(element => element is not null)
                .Cast<Element>()
            : new FilteredElementCollector(document, view.Id)
                .WherePasses(new ElementMulticategoryFilter(SupportedMepCategories))
                .WhereElementIsNotElementType();
        foreach (Element element in elements)
        {
            if (elementFilter is not null && !elementFilter.Contains(element.Id.Value))
            {
                continue;
            }
            SupportedCategory? category = Classify(element);
            if (category is null || alreadyTagged.Contains(element.Id.Value))
            {
                continue;
            }
            if (element.Category?.Id.Value == (long)BuiltInCategory.OST_DuctCurves &&
                !IsEligibleDuctChange(element, ductEligibility))
            {
                continue;
            }
            BoundingBoxXYZ? elementBox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (elementBox is null)
            {
                continue;
            }

            XYZ anchor = GetElementAnchor(element, elementBox);
            string label = CreatePreviewLabel(element);
            (double Width, double Height) size = sizeByGroup.TryGetValue(category.Value.Key, out var known)
                ? known
                : EstimateTagSize(label, view.Scale);
            long tagKey = CreateProvisionalTagKey(element.Id.Value);
            LayoutRect elementBounds = ProjectBox(elementBox, right, up);
            if (!elementBounds.Intersects(frame, 0.0))
            {
                continue;
            }
            var input = new LayoutTagInput(
                tagKey,
                element.Id.Value,
                label,
                category.Value.Key,
                Project(anchor, right, up),
                Project(anchor, right, up),
                size.Width,
                size.Height,
                elementBounds)
            {
                PreferLocalClustering = element.Category?.Id.Value ==
                                        (long)BuiltInCategory.OST_DuctCurves,
                CanAnchorDuctFollowers = IsDuctFollowerAnchorCategory(element)
            };
            result.Add(new SmartTagRecord(
                tagKey,
                0,
                0,
                element.Id.Value,
                true,
                category.Value.Name,
                anchor,
                anchor,
                input));
        }
        return result;
    }

    private static List<SmartTagRecord> CollectExistingTags(
        Document document,
        View view,
        XYZ right,
        XYZ up,
        LayoutRect frame,
        IReadOnlySet<long>? elementFilter,
        IDictionary<long, bool> ductEligibility)
    {
        var result = new List<SmartTagRecord>();
        IEnumerable<IndependentTag> tags = new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>();
        foreach (IndependentTag tag in tags)
        {
            if (tag.IsOrphaned)
            {
                continue;
            }
            Reference? reference = tag.GetTaggedReferences().FirstOrDefault();
            if (reference is null)
            {
                continue;
            }
            Element? element = document.GetElement(reference.ElementId);
            SupportedCategory? category = Classify(element);
            if (element is null || category is null)
            {
                continue;
            }
            if (element.Category?.Id.Value == (long)BuiltInCategory.OST_DuctCurves &&
                !IsEligibleDuctChange(element, ductEligibility))
            {
                continue;
            }
            if (elementFilter is not null && !elementFilter.Contains(element.Id.Value))
            {
                continue;
            }

            BoundingBoxXYZ? elementBox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (elementBox is null)
            {
                continue;
            }
            LayoutRect elementBounds = ProjectBox(elementBox, right, up);
            if (!elementBounds.Intersects(frame, 0.0))
            {
                continue;
            }
            XYZ head = tag.TagHeadPosition;
            XYZ anchor = GetElementAnchor(element, elementBox);
            string label;
            try { label = tag.TagText; }
            catch { label = element.Name; }
            if (string.IsNullOrWhiteSpace(label))
            {
                label = element.Name;
            }

            (double tagWidth, double tagHeight) = EstimateTagSize(label, view.Scale);

            var input = new LayoutTagInput(
                tag.Id.Value,
                element.Id.Value,
                label,
                category.Value.Key,
                Project(anchor, right, up),
                Project(head, right, up),
                tagWidth,
                tagHeight,
                elementBounds)
            {
                PreferLocalClustering = element.Category?.Id.Value ==
                                        (long)BuiltInCategory.OST_DuctCurves,
                CanAnchorDuctFollowers = IsDuctFollowerAnchorCategory(element)
            };
            result.Add(new SmartTagRecord(
                tag.Id.Value,
                tag.Id.Value,
                tag.GetTypeId().Value,
                element.Id.Value,
                false,
                category.Value.Name,
                anchor,
                head,
                input));
        }
        return result;
    }

    private static long CreateProvisionalTagKey(long elementId) => -Math.Abs(elementId);

    private static bool IsDuctFollowerAnchorCategory(Element element) =>
        element.Category?.Id.Value is
            (long)BuiltInCategory.OST_DuctTerminal or
            (long)BuiltInCategory.OST_DuctAccessory;

    private static (double Width, double Height) EstimateTagSize(string label, int viewScale)
    {
        string[] lines = label.Replace("\r", string.Empty).Split('\n');
        int longest = Math.Max(8, lines.Max(line => line.Length));
        double widthMm = Math.Clamp(longest * 1.05 + 4.0, 15.0, 58.0);
        double heightMm = Math.Clamp(lines.Length * 2.5 + 1.0, 3.5, 13.0);
        double scale = Math.Max(1, viewScale) / 304.8;
        return (widthMm * scale, heightMm * scale);
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] ordered = source.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0.1;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }

    private static double Quantile(IEnumerable<double> source, double fraction)
    {
        double[] ordered = source.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0.1;
        double index = Math.Clamp(fraction, 0.0, 1.0) * (ordered.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        double weight = index - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * weight;
    }

    private readonly record struct DuctSizeProfile(int Shape, double First, double Second);

    private static bool IsEligibleDuctChange(
        Element duct,
        IDictionary<long, bool> cache)
    {
        if (cache.TryGetValue(duct.Id.Value, out bool known)) return known;
        bool eligible;
        try
        {
            DuctSizeProfile? currentSize = GetDuctSizeProfile(duct);
            double currentElevation = GetCurveMidpointElevation(duct);
            List<Element> neighbours = GetConnectedDuctsAcrossFittings(duct);
            const double oneMillimetre = 1.0 / 304.8;
            bool sizeChanged = currentSize is not null && neighbours.Any(neighbour =>
            {
                DuctSizeProfile? other = GetDuctSizeProfile(neighbour);
                return other is not null && !SameDuctSize(
                    currentSize.Value,
                    other.Value,
                    oneMillimetre);
            });
            bool elevationChanged = double.IsFinite(currentElevation) && neighbours.Any(neighbour =>
            {
                double otherElevation = GetCurveMidpointElevation(neighbour);
                return double.IsFinite(otherElevation) &&
                       Math.Abs(currentElevation - otherElevation) > oneMillimetre;
            });
            double length = duct.Location is LocationCurve location && location.Curve.IsBound
                ? location.Curve.Length
                : duct.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0.0;
            eligible = SmartTagDuctChangeRules.ShouldTag(
                sizeChanged,
                elevationChanged,
                length);
        }
        catch
        {
            // Connectivity unavailable: remain conservative instead of
            // silently falling back to tagging every ordinary duct.
            eligible = false;
        }
        cache[duct.Id.Value] = eligible;
        return eligible;
    }

    private static DuctSizeProfile? GetDuctSizeProfile(Element duct)
    {
        double diameter = duct.get_Parameter(
            BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0.0;
        if (diameter > 1e-8)
            return new DuctSizeProfile(0, diameter, diameter);
        double width = duct.get_Parameter(
            BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0.0;
        double height = duct.get_Parameter(
            BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0.0;
        if (width <= 1e-8 || height <= 1e-8) return null;
        // A rectangular duct rotated by an elbow is still the same size.
        return new DuctSizeProfile(
            1,
            Math.Min(width, height),
            Math.Max(width, height));
    }

    private static bool SameDuctSize(
        DuctSizeProfile first,
        DuctSizeProfile second,
        double tolerance) =>
        first.Shape == second.Shape &&
        Math.Abs(first.First - second.First) <= tolerance &&
        Math.Abs(first.Second - second.Second) <= tolerance;

    private static double GetCurveMidpointElevation(Element element)
    {
        try
        {
            if (element.Location is LocationCurve location && location.Curve.IsBound)
                return location.Curve.Evaluate(0.5, true).Z;
        }
        catch
        {
            // NaN below disables only the elevation comparison.
        }
        return double.NaN;
    }

    private static List<Element> GetConnectedDuctsAcrossFittings(Element duct)
    {
        var result = new Dictionary<long, Element>();
        ConnectorManager? manager = TryGetConnectorManager(duct);
        if (manager is null) return [];
        foreach (Connector connector in manager.Connectors)
        foreach (Connector connected in connector.AllRefs)
        {
            Element? owner = connected.Owner;
            if (owner is null || owner.Id == duct.Id) continue;
            if (owner.Category?.Id.Value == (long)BuiltInCategory.OST_DuctCurves)
            {
                result[owner.Id.Value] = owner;
                continue;
            }
            if (owner.Category?.Id.Value != (long)BuiltInCategory.OST_DuctFitting)
                continue;
            ConnectorManager? fittingManager = TryGetConnectorManager(owner);
            if (fittingManager is null) continue;
            foreach (Connector fittingConnector in fittingManager.Connectors)
            foreach (Connector fittingReference in fittingConnector.AllRefs)
            {
                Element? adjacent = fittingReference.Owner;
                if (adjacent is null || adjacent.Id == duct.Id) continue;
                if (adjacent.Category?.Id.Value == (long)BuiltInCategory.OST_DuctCurves)
                    result[adjacent.Id.Value] = adjacent;
            }
        }
        return result.Values.ToList();
    }

    private static ConnectorManager? TryGetConnectorManager(Element element)
    {
        try
        {
            return element switch
            {
                MEPCurve curve => curve.ConnectorManager,
                FamilyInstance family => family.MEPModel?.ConnectorManager,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string CreatePreviewLabel(Element element)
    {
        var parts = new List<string>();
        string? mark = GetParameterText(element, "Mark");
        string? size = GetParameterText(element, "Size");
        string? abbreviation = GetParameterText(element, "System Abbreviation");
        if (!string.IsNullOrWhiteSpace(abbreviation)) parts.Add(abbreviation);
        parts.Add(string.IsNullOrWhiteSpace(mark) ? element.Name : mark);
        if (!string.IsNullOrWhiteSpace(size)) parts.Add(size);
        return string.Join("\n", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? GetParameterText(Element element, string name)
    {
        Parameter? parameter = element.LookupParameter(name);
        if (parameter is null || !parameter.HasValue) return null;
        string? value = parameter.AsValueString();
        if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.String)
        {
            value = parameter.AsString();
        }
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FriendlyTagError(Exception exception)
    {
        string message = exception.Message;
        if (message.Contains("tag", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("type", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("family", StringComparison.OrdinalIgnoreCase)))
        {
            return "No compatible Tag Family is loaded for this category.";
        }
        return message;
    }

    private static List<LayoutObstacle> CollectObstacles(
        Document document,
        View view,
        XYZ right,
        XYZ up,
        LayoutRect frame,
        bool includeArchitecture = true,
        IReadOnlyDictionary<long, LayoutRect>? knownMepBounds = null)
    {
        var result = new List<LayoutObstacle>();
        var mepFilter = new ElementMulticategoryFilter(MepObstacleCategories);
        IEnumerable<Element> elements = new FilteredElementCollector(document, view.Id)
            .WherePasses(mepFilter)
            .WhereElementIsNotElementType()
            .ToElements();
        foreach (Element element in elements)
        {
            if (!IsMepObstacle(element))
            {
                continue;
            }
            if (knownMepBounds is not null &&
                knownMepBounds.TryGetValue(element.Id.Value, out LayoutRect knownBounds))
            {
                if (knownBounds.Intersects(frame, 0.0))
                {
                    result.Add(new LayoutObstacle(element.Id.Value, knownBounds));
                }
                continue;
            }
            BoundingBoxXYZ? box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (box is null)
            {
                continue;
            }
            LayoutRect bounds = ProjectBox(box, right, up);
            if (bounds.Intersects(frame, 0.0))
            {
                result.Add(new LayoutObstacle(element.Id.Value, bounds));
            }
        }

        // MEP geometry in a Revit link is often displayed halftone/light blue.
        // It is still real model geometry and must reserve text space exactly
        // like host-document MEP elements. The architectural collector below
        // intentionally does not include these categories.
        var linkedMepFilter = mepFilter;
        foreach (RevitLinkInstance link in new FilteredElementCollector(document, view.Id)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            Document? linkDocument = link.GetLinkDocument();
            if (linkDocument is null) continue;
            IEnumerable<Element> linkedElements;
            try
            {
                linkedElements = new FilteredElementCollector(document, view.Id, link.Id)
                    .WherePasses(linkedMepFilter)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch
            {
                linkedElements = new FilteredElementCollector(linkDocument)
                    .WherePasses(linkedMepFilter)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }

            Transform linkTransform = link.GetTotalTransform();
            foreach (Element linkedElement in linkedElements)
            {
                BoundingBoxXYZ? box = linkedElement.get_BoundingBox(null);
                if (box is null) continue;
                LayoutRect bounds = ProjectBox(box, linkTransform, right, up);
                if (!bounds.Intersects(frame, 0.0)) continue;
                result.Add(new LayoutObstacle(
                    CreateLinkedObstacleKey(link.Id.Value, linkedElement.Id.Value),
                    bounds));
            }
        }
        if (includeArchitecture)
        {
            result.AddRange(SmartTagArchitectureObstacleCollector.Collect(
                document,
                view,
                right,
                up,
                frame));
        }
        return result;
    }

    private static long CreateLinkedObstacleKey(long linkId, long elementId)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            hash = (hash ^ linkId) * 1099511628211L;
            hash = (hash ^ elementId) * 1099511628211L;
            if (hash == long.MinValue) return long.MinValue + 1;
            return -Math.Abs(hash == 0 ? 1 : hash);
        }
    }

    private static List<SmartTagCategoryInfo> BuildCategoryInfos(
        Document document,
        IReadOnlyList<SmartTagRecord> records)
    {
        List<ElementType> projectTagTypes = new FilteredElementCollector(document)
            .WherePasses(new ElementMulticategoryFilter(SupportedTagCategories))
            .WhereElementIsElementType()
            .Cast<ElementType>()
            .ToList();
        var result = new List<SmartTagCategoryInfo>();
        foreach (IGrouping<string, SmartTagRecord> group in records
                     .GroupBy(item => item.Layout.Group, StringComparer.OrdinalIgnoreCase))
        {
            long modelCategoryId = long.TryParse(group.Key, out long parsed) ? parsed : 0;
            long tagCategoryId = GetTagCategoryId(modelCategoryId);
            List<SmartTagTypeInfo> types = projectTagTypes
                .Where(type => type.Category?.Id.Value == tagCategoryId)
                .Select(type => new SmartTagTypeInfo(
                    type.Id.Value,
                    $"{type.FamilyName} : {type.Name}"))
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            long existingDefault = group
                .Where(item => item.ExistingTagTypeId > 0)
                .GroupBy(item => item.ExistingTagTypeId)
                .OrderByDescending(item => item.Count())
                .Select(item => item.Key)
                .FirstOrDefault();
            long defaultType = types.Any(type => type.Id == existingDefault)
                ? existingDefault
                : types.FirstOrDefault()?.Id ?? 0;
            result.Add(new SmartTagCategoryInfo(
                group.Key,
                group.First().CategoryName,
                group.Select(item => item.ElementId).Distinct().Count(),
                group.Count(item => !item.WillCreate),
                types,
                defaultType));
        }
        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static long GetTagCategoryId(long modelCategoryId) => modelCategoryId switch
    {
        (long)BuiltInCategory.OST_DuctCurves => (long)BuiltInCategory.OST_DuctTags,
        (long)BuiltInCategory.OST_DuctAccessory => (long)BuiltInCategory.OST_DuctAccessoryTags,
        (long)BuiltInCategory.OST_PipeCurves => (long)BuiltInCategory.OST_PipeTags,
        (long)BuiltInCategory.OST_PipeFitting => (long)BuiltInCategory.OST_PipeFittingTags,
        (long)BuiltInCategory.OST_PipeAccessory => (long)BuiltInCategory.OST_PipeAccessoryTags,
        (long)BuiltInCategory.OST_MechanicalEquipment => (long)BuiltInCategory.OST_MechanicalEquipmentTags,
        (long)BuiltInCategory.OST_DuctTerminal => (long)BuiltInCategory.OST_DuctTerminalTags,
        (long)BuiltInCategory.OST_Sprinklers => (long)BuiltInCategory.OST_SprinklerTags,
        _ => 0
    };

    private static SupportedCategory? Classify(Element? element)
    {
        long category = element?.Category?.Id.Value ?? long.MaxValue;
        bool supported = category switch
        {
            (long)BuiltInCategory.OST_DuctCurves or
            (long)BuiltInCategory.OST_DuctAccessory or
            (long)BuiltInCategory.OST_PipeCurves or
            (long)BuiltInCategory.OST_PipeFitting or
            (long)BuiltInCategory.OST_PipeAccessory or
            (long)BuiltInCategory.OST_MechanicalEquipment or
            (long)BuiltInCategory.OST_DuctTerminal or
            (long)BuiltInCategory.OST_Sprinklers => true,
            _ => false
        };
        if (!supported || element?.Category is null) return null;
        return new SupportedCategory(category.ToString(), element.Category.Name);
    }

    private static bool IsMepObstacle(Element? element)
    {
        long category = element?.Category?.Id.Value ?? long.MaxValue;
        return category switch
        {
            (long)BuiltInCategory.OST_DuctCurves or
            (long)BuiltInCategory.OST_DuctFitting or
            (long)BuiltInCategory.OST_DuctAccessory or
            (long)BuiltInCategory.OST_FlexDuctCurves or
            (long)BuiltInCategory.OST_DuctInsulations or
            (long)BuiltInCategory.OST_DuctLinings or
            (long)BuiltInCategory.OST_PipeCurves or
            (long)BuiltInCategory.OST_PipeFitting or
            (long)BuiltInCategory.OST_PipeAccessory or
            (long)BuiltInCategory.OST_FlexPipeCurves or
            (long)BuiltInCategory.OST_PipeInsulations or
            (long)BuiltInCategory.OST_MechanicalEquipment or
            (long)BuiltInCategory.OST_DuctTerminal or
            (long)BuiltInCategory.OST_Sprinklers or
            (long)BuiltInCategory.OST_CableTray or
            (long)BuiltInCategory.OST_CableTrayFitting or
            (long)BuiltInCategory.OST_Conduit or
            (long)BuiltInCategory.OST_ConduitFitting or
            (long)BuiltInCategory.OST_ElectricalEquipment or
            (long)BuiltInCategory.OST_ElectricalFixtures or
            (long)BuiltInCategory.OST_PlumbingFixtures => true,
            _ => false
        };
    }

    private readonly record struct SupportedCategory(string Key, string Name);

    private sealed class SupportedMepSelectionFilter : ISelectionFilter
    {
        private readonly IReadOnlySet<string>? _selectedCategoryKeys;

        public SupportedMepSelectionFilter(IReadOnlySet<string>? selectedCategoryKeys = null)
        {
            _selectedCategoryKeys = selectedCategoryKeys;
        }

        public bool AllowElement(Element element)
        {
            SupportedCategory? category = Classify(element);
            return category is not null &&
                   (_selectedCategoryKeys is null ||
                    _selectedCategoryKeys.Contains(category.Value.Key));
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private sealed class ExistingTagSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) =>
            element is IndependentTag;

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private static byte[] CaptureViewport(
        IntPtr revitWindow,
        int left,
        int top,
        int width,
        int height)
    {
        if (revitWindow == IntPtr.Zero)
            throw new InvalidOperationException("The Revit main window is unavailable.");
        if (NativeMethods.IsIconic(revitWindow))
            throw new InvalidOperationException(
                "Revit is minimized. Restore it manually before refreshing Smart Tag; the tool will not resize Revit.");

        // BitBlt reads the desktop pixels inside the exact UIView rectangle. To
        // guarantee that no browser, IDE, notification, or other application is
        // copied into that rectangle, put the existing Revit window temporarily
        // above all other windows. SWP_NOMOVE | SWP_NOSIZE means this never
        // restores, maximizes, moves, or resizes Revit.
        bool wasTopMost = (NativeMethods.GetWindowLong(
            revitWindow,
            NativeMethods.ExtendedStyleIndex) & NativeMethods.TopMostStyle) != 0;
        NativeMethods.SetWindowPos(
            revitWindow,
            NativeMethods.TopMostWindow,
            0,
            0,
            0,
            0,
            NativeMethods.NoMove | NativeMethods.NoSize | NativeMethods.ShowWindow);
        NativeMethods.SetForegroundWindow(revitWindow);
        NativeMethods.RedrawWindow(
            revitWindow,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.Invalidate | NativeMethods.UpdateNow | NativeMethods.AllChildren);
        try { NativeMethods.DwmFlush(); } catch { }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            if (!wasTopMost)
            {
                NativeMethods.SetWindowPos(
                    revitWindow,
                    NativeMethods.NotTopMostWindow,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.NoMove | NativeMethods.NoSize | NativeMethods.NoActivate);
            }
            throw new InvalidOperationException("Windows could not open the desktop drawing surface.");
        }
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Windows could not allocate the Active View preview bitmap.");
            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    left,
                    top,
                    NativeMethods.SourceCopy))
                throw new InvalidOperationException("Windows could not capture the Active View pixels.");

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
                NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            if (!wasTopMost)
            {
                NativeMethods.SetWindowPos(
                    revitWindow,
                    NativeMethods.NotTopMostWindow,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.NoMove | NativeMethods.NoSize | NativeMethods.NoActivate);
            }
        }
    }

    private static LayoutPoint Project(XYZ point, XYZ right, XYZ up) =>
        new(point.DotProduct(right), point.DotProduct(up));

    private static LayoutRect ProjectBox(BoundingBoxXYZ box, XYZ right, XYZ up)
        => ProjectBox(box, Transform.Identity, right, up);

    private static LayoutRect ProjectBox(
        BoundingBoxXYZ box,
        Transform outerTransform,
        XYZ right,
        XYZ up)
    {
        Transform transform = box.Transform;
        var points = new List<XYZ>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
        {
            points.Add(outerTransform.OfPoint(transform.OfPoint(new XYZ(x, y, z))));
        }
        return new LayoutRect(
            points.Min(point => point.DotProduct(right)),
            points.Min(point => point.DotProduct(up)),
            points.Max(point => point.DotProduct(right)),
            points.Max(point => point.DotProduct(up)));
    }

    private static XYZ BoxCenter(BoundingBoxXYZ box) =>
        box.Transform.OfPoint((box.Min + box.Max) * 0.5);

    private static XYZ GetElementAnchor(Element element, BoundingBoxXYZ box)
    {
        try
        {
            if (element.Location is LocationPoint point)
            {
                return point.Point;
            }
            if (element.Location is LocationCurve locationCurve && locationCurve.Curve.IsBound)
            {
                return locationCurve.Curve.Evaluate(0.5, true);
            }
        }
        catch
        {
            // Some fabrication and nested elements expose an unusable Location.
        }
        return BoxCenter(box);
    }

    private static XYZ MoveInViewPlane(XYZ source, LayoutPoint destination, XYZ right, XYZ up)
    {
        double currentU = source.DotProduct(right);
        double currentV = source.DotProduct(up);
        return source + right * (destination.U - currentU) + up * (destination.V - currentV);
    }

    private static class NativeMethods
    {
        internal const int SourceCopy = 0x00CC0020;
        internal const int ExtendedStyleIndex = -20;
        internal const int TopMostStyle = 0x00000008;
        internal const uint NoSize = 0x0001;
        internal const uint NoMove = 0x0002;
        internal const uint NoActivate = 0x0010;
        internal const uint ShowWindow = 0x0040;
        internal const uint Invalidate = 0x0001;
        internal const uint UpdateNow = 0x0100;
        internal const uint AllChildren = 0x0080;
        internal static readonly IntPtr TopMostWindow = new(-1);
        internal static readonly IntPtr NotTopMostWindow = new(-2);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RedrawWindow(
            IntPtr window,
            IntPtr updateRectangle,
            IntPtr updateRegion,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr drawingObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr drawingObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(
            IntPtr destination,
            int xDestination,
            int yDestination,
            int width,
            int height,
            IntPtr source,
            int xSource,
            int ySource,
            int operation);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();
    }
}
