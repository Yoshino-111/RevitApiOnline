using System.IO.Compression;
using System.Text;
using System.Xml;
using Autodesk.Revit.DB;

namespace FamilyMEP.Plugin.ExteriorWallMapper;

internal static class ExteriorWallExcelExporter
{
    private const double ShortWallLengthMm = 600.0;
    private const double WallTypeToleranceMm = 50.0;

    internal static void Export(
        string path,
        IReadOnlyList<ExteriorWallRow> rows,
        IReadOnlyList<ReplacementBatch> batches,
        IReadOnlyList<LinearBatchPlan> linearPlans,
        IReadOnlyList<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An Excel output path is required.", nameof(path));
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        using FileStream stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteText(archive, "[Content_Types].xml", ContentTypes());
        WriteText(archive, "_rels/.rels", PackageRelationships());
        WriteText(archive, "xl/workbook.xml", Workbook());
        WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
        WriteText(archive, "xl/styles.xml", Styles());
        WriteSheet(archive, "xl/worksheets/sheet1.xml", ExteriorWallTable(rows), 22);
        WriteSheet(archive, "xl/worksheets/sheet2.xml", BatchTable(batches), 13);
        WriteSheet(archive, "xl/worksheets/sheet3.xml", LinearPlanTable(linearPlans), 14);
        WriteSheet(archive, "xl/worksheets/sheet4.xml", LinearFaceTable(linearPlans), 11);
        WriteSheet(archive, "xl/worksheets/sheet5.xml", WarningTable(warnings), 3);
        WriteSheet(archive, "xl/worksheets/sheet6.xml", RoomWallInventoryTable(rows), 23);
        WriteSheet(archive, "xl/worksheets/sheet7.xml", GroupedWallTypeTable(rows), 20);
        WriteSheet(archive, "xl/worksheets/sheet8.xml", SunExposedRoomTable(rows), 14);
        WriteSheet(archive, "xl/worksheets/sheet9.xml", LinearRoomReplaceTable(rows), 13);
        WriteSheet(archive, "xl/worksheets/sheet10.xml", LinearReplaceGroupTable(rows), 17);
        WriteOutlineSheet(archive, "xl/worksheets/sheet11.xml", WallAssemblyOutlineTable(rows), 29);
        WriteSheet(archive, "xl/worksheets/sheet12.xml", ToleranceWallGroupTable(rows), 21);
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> ToleranceWallGroupTable(
        IReadOnlyList<ExteriorWallRow> rows)
    {
        ExteriorWallRow[] wallRows = rows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .ToArray();
        var materialFamilies = wallRows
            .GroupBy(row => new
            {
                row.LinearCategory,
                LayerCount = EffectiveLayerCount(row),
                Materials = WallMaterialNameSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key.LayerCount)
            .ToArray();

        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Tolerance Group ID", "LINEAR Wall Type", "Merge Candidate", "Tolerance Rule",
                "Suggested Type Name", "Suggested Nominal Thickness (mm)",
                "Minimum Thickness (mm)", "Maximum Thickness (mm)", "Thickness Range (mm)",
                "Original Thickness Values (mm)", "Exact Type Count", "Layer Count",
                "Layer / Material Names", "Material Class", "Room Count", "Room Numbers",
                "Rooms Using This Tolerance Type", "Levels", "Orientations",
                "Element IDs Only (Copy/Paste)", "QA Status")
        };

        int toleranceGroupIndex = 0;
        foreach (var family in materialFamilies)
        {
            ExteriorWallRow[] sorted = family
                .OrderBy(row => row.ThicknessMm)
                .ThenBy(row => row.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var clusters = new List<List<ExteriorWallRow>>();
            foreach (ExteriorWallRow wall in sorted)
            {
                List<ExteriorWallRow>? cluster = clusters.LastOrDefault();
                if (cluster is null || wall.ThicknessMm - cluster[0].ThicknessMm > WallTypeToleranceMm)
                {
                    cluster = [];
                    clusters.Add(cluster);
                }
                cluster.Add(wall);
            }

            foreach (List<ExteriorWallRow> cluster in clusters)
            {
                toleranceGroupIndex++;
                ExteriorWallRow representative = cluster[0];
                double minimum = cluster.Min(row => row.ThicknessMm);
                double maximum = cluster.Max(row => row.ThicknessMm);
                double range = maximum - minimum;
                double suggested = Math.Round(
                    ((minimum + maximum) / 2.0) / 5.0,
                    MidpointRounding.AwayFromZero) * 5.0;
                double[] originalThicknesses = cluster
                    .Select(row => Math.Round(row.ThicknessMm, 1))
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
                var rooms = cluster
                    .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                    .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                string materialNames = representative.Layers.Count == 0
                    ? representative.LayerDescription
                    : string.Join(" + ", representative.Layers.OrderBy(layer => layer.Rank)
                        .Select(layer => layer.Name));
                string materialClasses = string.Join(", ", cluster
                    .SelectMany(row => row.Layers.Count == 0
                        ? new[] { WallMaterialClassifier.Classify(row.LayerDescription) }
                        : row.Layers.Select(layer => layer.MaterialClass))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                string roomNumbers = string.Join("; ", rooms.Select(room => room.Key.SpaceNumber)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase));
                string roomList = string.Join("; ", rooms.Select(room =>
                    $"[{room.Key.Level}] {room.Key.SpaceNumber} - {CleanRoomName(room.Key.SpaceNumber, room.Key.SpaceName)}"));
                string elementIds = JoinDistinct(cluster.SelectMany(row => row.Layers)
                    .Select(layer => layer.SourceElementId));
                if (elementIds.Length == 0)
                    elementIds = JoinDistinct(cluster.SelectMany(row => row.References)
                        .Select(reference => reference.LinkedElementId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)));
                bool mergeCandidate = originalThicknesses.Length > 1;
                string groupId = $"TG-{toleranceGroupIndex:000}";
                string suggestedName = $"{groupId} | {representative.LinearCategory} | " +
                    $"{suggested:0.#} mm | {family.Key.LayerCount} layer(s)";

                table.Add(new Cell[]
                {
                    T(groupId), T(representative.LinearCategory), T(mergeCandidate ? "Yes" : "No"),
                    T($"Same LINEAR type + same ordered material names; max-min <= {WallTypeToleranceMm:0} mm"),
                    T(suggestedName), N(suggested), N(minimum), N(maximum), N(range),
                    T(string.Join("; ", originalThicknesses.Select(value => value.ToString("0.#")))),
                    N(originalThicknesses.Length), N(family.Key.LayerCount), T(materialNames), T(materialClasses),
                    N(rooms.Length), T(roomNumbers), T(roomList),
                    T(string.Join(", ", rooms.Select(room => room.Key.Level)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))),
                    T(string.Join(", ", cluster.Select(row => row.Orientation)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                    T(elementIds),
                    T(mergeCandidate
                        ? $"Candidate: {originalThicknesses.Length} exact thicknesses can be reviewed as one type"
                        : "Exact thickness only; no tolerance merge required")
                });
            }
        }
        return table;
    }

    private static int EffectiveLayerCount(ExteriorWallRow row) =>
        row.Layers.Count > 0 ? row.Layers.Count : Math.Max(row.LayerCount, 1);

    private static string WallMaterialNameSignature(ExteriorWallRow row) => row.Layers.Count == 0
        ? NormalizeMaterial(row.LayerDescription)
        : string.Join("|", row.Layers.OrderBy(layer => layer.Rank)
            .Select(layer => NormalizeMaterial(layer.Name)));

    private static IReadOnlyList<OutlineRow> WallAssemblyOutlineTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        var groups = rows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .GroupBy(row => new
            {
                row.LinearCategory,
                Thickness = Math.Round(row.ThicknessMm, 1),
                Materials = WallMaterialSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Thickness)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var table = new List<OutlineRow>
        {
            new(TextRow(
                "Assembly Group", "Assembly Name", "Row Type", "LINEAR Wall Type",
                "Physical Thickness (mm)", "Layer Thickness Sum (mm)", "Overlap Removed (mm)",
                "Layer Count", "Layer Order", "Layer Thickness (mm)", "Layer / Material Name",
                "Material Class", "Room Count", "Room Numbers", "Room Number [Wall Element IDs]", "Room Names",
                "Element IDs Only (Copy/Paste)", "Minimum Element Length (mm)",
                $"Short Element IDs (< {ShortWallLengthMm:0} mm)",
                "LINEAR Room Selection Names", "Levels", "Orientations", "Target LINEAR",
                "Source Element IDs", "Source Unique IDs", "IFC GUIDs", "Link Paths", "Geometry Keys",
                "Trace Status"))
        };

        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ExteriorWallRow[] walls = groups[groupIndex]
                .OrderBy(row => row.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Orientation, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ExteriorWallRow representative = walls[0];
            ExteriorWallLayerItem[] layers = representative.Layers.Count > 0
                ? representative.Layers.OrderBy(layer => layer.Rank).ToArray()
                :
                [
                    new ExteriorWallLayerItem(
                        1,
                        representative.LayerDescription,
                        representative.ThicknessMm,
                        representative.LinearCategory == LinearComponentKinds.ExteriorWall,
                        true)
                ];
            var rooms = walls
                .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string groupId = $"WG-{groupIndex + 1:000}";
            string assemblyName = $"{groupId} | {representative.LinearCategory} | " +
                $"{representative.ThicknessMm:0.#} mm | {layers.Length} layer(s)";
            double layerSum = layers.Sum(layer => layer.ThicknessMm);
            double overlapRemoved = Math.Max(layerSum - representative.ThicknessMm, 0.0);
            string roomNumbers = string.Join(" | ", rooms
                .GroupBy(room => room.Key.Level, StringComparer.OrdinalIgnoreCase)
                .OrderBy(levelGroup => levelGroup.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(levelGroup =>
                    $"[{levelGroup.Key}] " + string.Join("; ", levelGroup
                        .Select(room => room.Key.SpaceNumber)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))));
            string roomNames = string.Join("; ", rooms.Select(room => CleanRoomName(room.Key.SpaceNumber, room.Key.SpaceName))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
            string linearRooms = string.Join(Environment.NewLine, rooms.Select(room =>
                LinearRoomSelectionName(room.Key.Level, room.Key.SpaceNumber, room.Key.SpaceName)));
            string levels = string.Join(", ", rooms.Select(room => room.Key.Level)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
            string orientations = string.Join(", ", walls.Select(row => row.Orientation)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            ExteriorWallLayerItem[] allLayerSources = walls.SelectMany(row => row.Layers).ToArray();
            Dictionary<string, double> elementLengths = ElementPlanLengths(walls);
            string roomElementMap = string.Join("; ", rooms.Select(room =>
            {
                string ids = JoinDistinct(room.SelectMany(row => row.Layers)
                    .Select(layer => layer.SourceElementId));
                if (ids.Length == 0)
                    ids = JoinDistinct(room.SelectMany(row => row.References)
                        .Select(reference => reference.LinkedElementId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)));
                return ids.Length == 0 ? string.Empty : $"{room.Key.SpaceNumber}[{ids}]";
            }).Where(value => value.Length > 0));
            string elementIdsOnly = JoinDistinct(allLayerSources.Select(layer => layer.SourceElementId));
            if (elementIdsOnly.Length == 0)
                elementIdsOnly = JoinDistinct(walls.SelectMany(row => row.References)
                    .Select(reference => reference.LinkedElementId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            string shortElementIds = ShortElementIds(elementIdsOnly, elementLengths);
            double? minimumElementLength = MinimumElementLength(elementIdsOnly, elementLengths);

            table.Add(new OutlineRow(
                new Cell[]
                {
                    T(groupId), T(assemblyName), T("ASSEMBLY"), T(representative.LinearCategory),
                    N(representative.ThicknessMm), N(layerSum), N(overlapRemoved), N(layers.Length),
                    T(string.Empty), T(string.Empty),
                    T(string.Join(" + ", layers.Select(layer => layer.Name))),
                    T(string.Join(", ", layers.Select(layer => layer.MaterialClass)
                        .Distinct(StringComparer.OrdinalIgnoreCase))),
                    N(rooms.Length), T(roomNumbers), T(roomElementMap), T(roomNames),
                    T(elementIdsOnly), minimumElementLength.HasValue ? N(minimumElementLength.Value) : T(string.Empty),
                    T(shortElementIds), T(linearRooms), T(levels), T(orientations),
                    T(representative.TargetLinear),
                    T(elementIdsOnly),
                    T(JoinDistinct(allLayerSources.Select(layer => layer.SourceUniqueId))),
                    T(JoinDistinct(allLayerSources.Select(layer => layer.SourceIfcGuid))),
                    T(JoinDistinct(allLayerSources.Select(layer => layer.SourceLinkPath))),
                    T(JoinDistinct(allLayerSources.Select(layer => layer.SourceGeometryKey))),
                    T(allLayerSources.Length > 0 && allLayerSources.All(layer =>
                        !string.IsNullOrWhiteSpace(layer.SourceElementId)) ? "Ready" : "Review: missing source ID")
                },
                Collapsed: true,
                IsSummary: true,
                IsWarning: shortElementIds.Length > 0));

            for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                ExteriorWallLayerItem layer = layers[layerIndex];
                ExteriorWallLayerItem[] sourceLayerInstances = walls
                    .Select(row => row.Layers.OrderBy(item => item.Rank).ElementAtOrDefault(layerIndex))
                    .Where(item => item is not null)
                    .Cast<ExteriorWallLayerItem>()
                    .ToArray();
                string layerRoomElementMap = string.Join("; ", rooms.Select(room =>
                {
                    string ids = JoinDistinct(room
                        .Select(row => row.Layers.OrderBy(item => item.Rank).ElementAtOrDefault(layerIndex))
                        .Where(item => item is not null)
                        .Cast<ExteriorWallLayerItem>()
                        .Select(item => item.SourceElementId));
                    return ids.Length == 0 ? string.Empty : $"{room.Key.SpaceNumber}[{ids}]";
                }).Where(value => value.Length > 0));
                string layerElementIdsOnly = JoinDistinct(sourceLayerInstances.Select(item => item.SourceElementId));
                string shortLayerElementIds = ShortElementIds(layerElementIdsOnly, elementLengths);
                double? minimumLayerLength = MinimumElementLength(layerElementIdsOnly, elementLengths);
                table.Add(new OutlineRow(
                    new Cell[]
                    {
                        T(groupId), T(assemblyName), T("LAYER"), T(representative.LinearCategory),
                        T(string.Empty), T(string.Empty), T(string.Empty), T(string.Empty),
                        N(layerIndex + 1), N(layer.ThicknessMm),
                        T($"{(layer.IsExteriorFace ? "[OUTSIDE] " : string.Empty)}{layer.Name}"),
                        T(layer.MaterialClass), T(string.Empty), T(string.Empty), T(layerRoomElementMap), T(string.Empty),
                        T(layerElementIdsOnly), minimumLayerLength.HasValue ? N(minimumLayerLength.Value) : T(string.Empty),
                        T(shortLayerElementIds), T(string.Empty), T(string.Empty), T(string.Empty), T(string.Empty),
                        T(layerElementIdsOnly),
                        T(JoinDistinct(sourceLayerInstances.Select(item => item.SourceUniqueId))),
                        T(JoinDistinct(sourceLayerInstances.Select(item => item.SourceIfcGuid))),
                        T(JoinDistinct(sourceLayerInstances.Select(item => item.SourceLinkPath))),
                        T(JoinDistinct(sourceLayerInstances.Select(item => item.SourceGeometryKey))),
                        T(sourceLayerInstances.Length > 0 && sourceLayerInstances.All(item =>
                            !string.IsNullOrWhiteSpace(item.SourceElementId)) ? "Ready" : "Review: missing source ID")
                    },
                    OutlineLevel: 1,
                    Hidden: true,
                    IsWarning: shortLayerElementIds.Length > 0));
            }
        }
        return table;
    }

    private static string JoinDistinct(IEnumerable<string> values) => string.Join("; ", values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));

    private static Dictionary<string, double> ElementPlanLengths(IEnumerable<ExteriorWallRow> walls) => walls
        .SelectMany(row => row.References)
        .Where(reference => reference.LinkedElementId > 0)
        .GroupBy(reference => reference.LinkedElementId.ToString(
            System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.Min(reference => UnitUtils.ConvertFromInternalUnits(
                Math.Max(Math.Abs(reference.MaxX - reference.MinX), Math.Abs(reference.MaxY - reference.MinY)),
                UnitTypeId.Millimeters)),
            StringComparer.OrdinalIgnoreCase);

    private static string ShortElementIds(
        string elementIds,
        IReadOnlyDictionary<string, double> elementLengths) => JoinDistinct(
            SplitElementIds(elementIds)
                .Where(id => elementLengths.TryGetValue(id, out double length) && length < ShortWallLengthMm));

    private static double? MinimumElementLength(
        string elementIds,
        IReadOnlyDictionary<string, double> elementLengths)
    {
        double[] lengths = SplitElementIds(elementIds)
            .Where(elementLengths.ContainsKey)
            .Select(id => elementLengths[id])
            .ToArray();
        return lengths.Length == 0 ? null : lengths.Min();
    }

    private static IEnumerable<string> SplitElementIds(string elementIds) => elementIds
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => value.Length > 0);

    private static IReadOnlyList<IReadOnlyList<Cell>> LinearReplaceGroupTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        ExteriorWallRow[] openings = rows
            .Where(row => LinearComponentKinds.IsOpening(row.LinearCategory))
            .ToArray();
        var groups = rows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .GroupBy(row => new
            {
                row.LinearCategory,
                Thickness = Math.Round(row.ThicknessMm, 1),
                Materials = WallMaterialSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Thickness)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Group ID", "LINEAR Wall Type", "Wall Thickness (mm)", "Layer Count",
                "Component / Material Name", "Material Class", "Proposed LINEAR Material",
                "Room Count", "Room Numbers", "LINEAR Storeys", "LINEAR Room Selection Names",
                "Levels", "Orientations", "Rooms With Openings", "Rooms Without Openings",
                "Associated Opening Types", "Target LINEAR")
        };
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ExteriorWallRow[] walls = groups[groupIndex].ToArray();
            ExteriorWallRow representative = walls[0];
            var rooms = walls
                .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .Select(room => new
                {
                    room.Key,
                    Walls = room.ToArray(),
                    LinearName = LinearRoomSelectionName(room.Key.Level, room.Key.SpaceNumber, room.Key.SpaceName),
                    LinearStorey = LinearStoreyDisplay(room.Key.Level, room.Key.SpaceNumber)
                })
                .ToArray();
            var roomOpeningMap = rooms.Select(room => new
                {
                    Room = room,
                    Openings = openings.Where(opening => room.Walls.Any(wall => OpeningMatchesWall(wall, opening)))
                        .Distinct()
                        .ToArray()
                })
                .ToArray();
            string materials = representative.Layers.Count == 0
                ? representative.LayerDescription
                : string.Join(" + ", representative.Layers.Select(layer => layer.Name));
            string classes = string.Join(", ", representative.Layers.Count == 0
                ? new[] { WallMaterialClassifier.Classify(representative.LayerDescription) }
                : representative.Layers.Select(layer => layer.MaterialClass)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            string proposal = representative.Layers.Count == 0
                ? LinearMaterialProposal.ForMaterial(
                    WallMaterialClassifier.Classify(representative.LayerDescription), representative.LayerDescription)
                : string.Join(" | ", representative.Layers.Select(layer => layer.LinearMaterialProposal)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            string openingTypes = string.Join(", ", roomOpeningMap
                .SelectMany(item => item.Openings)
                .Select(row => row.LinearCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            table.Add(new Cell[]
            {
                T($"WG-{groupIndex + 1:000}"), T(groups[groupIndex].Key.LinearCategory),
                N(groups[groupIndex].Key.Thickness), N(representative.LayerCount), T(materials), T(classes),
                T(proposal), N(rooms.Length),
                T(string.Join("; ", rooms.Select(room => room.Key.SpaceNumber)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase))),
                T(string.Join(", ", rooms.Select(room => room.LinearStorey)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase))),
                T(string.Join(Environment.NewLine, rooms.Select(room => room.LinearName))),
                T(string.Join(", ", rooms.Select(room => room.Key.Level)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))),
                T(string.Join(", ", walls.Select(row => row.Orientation)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                T(string.Join("; ", roomOpeningMap.Where(item => item.Openings.Length > 0)
                    .Select(item => item.Room.LinearName))),
                T(string.Join("; ", roomOpeningMap.Where(item => item.Openings.Length == 0)
                    .Select(item => item.Room.LinearName))),
                T(openingTypes.Length == 0 ? "--" : openingTypes), T(string.Empty)
            });
        }
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> LinearRoomReplaceTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        ExteriorWallRow[] openings = rows
            .Where(row => LinearComponentKinds.IsOpening(row.LinearCategory))
            .ToArray();
        var groups = rows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .GroupBy(row => new
            {
                row.LinearCategory,
                Thickness = Math.Round(row.ThicknessMm, 1),
                Materials = WallMaterialSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Thickness)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Group ID", "LINEAR Wall Type", "Wall Thickness (mm)",
                "Component / Material Name", "LINEAR Storey", "LINEAR Room Selection Name",
                "Room Number", "Room Name", "Level", "Orientations", "Has Associated Opening",
                "Associated Opening Types", "Associated Opening Names")
        };
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ExteriorWallRow[] groupWalls = groups[groupIndex].ToArray();
            string groupId = $"WG-{groupIndex + 1:000}";
            string materials = groupWalls[0].Layers.Count == 0
                ? groupWalls[0].LayerDescription
                : string.Join(" + ", groupWalls[0].Layers.Select(layer => layer.Name));
            foreach (var room in groupWalls
                         .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                         .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase))
            {
                ExteriorWallRow[] roomWalls = room.ToArray();
                ExteriorWallRow[] associated = openings
                    .Where(opening => roomWalls.Any(wall => OpeningMatchesWall(wall, opening)))
                    .Distinct()
                    .ToArray();
                string openingTypes = associated.Length == 0
                    ? "--"
                    : string.Join(", ", associated.Select(row => row.LinearCategory)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                string openingNames = associated.Length == 0
                    ? "--"
                    : string.Join("; ", associated
                        .GroupBy(row => new { row.LinearCategory, row.LayerDescription })
                        .Select(openingGroup =>
                            $"{openingGroup.Key.LinearCategory} x{openingGroup.Sum(row => row.WallCount)}: {openingGroup.Key.LayerDescription}"));
                table.Add(new Cell[]
                {
                    T(groupId), T(groups[groupIndex].Key.LinearCategory), N(groups[groupIndex].Key.Thickness),
                    T(materials), T(LinearStoreyDisplay(room.Key.Level, room.Key.SpaceNumber)),
                    T(LinearRoomSelectionName(room.Key.Level, room.Key.SpaceNumber, room.Key.SpaceName)),
                    T(room.Key.SpaceNumber), T(CleanRoomName(room.Key.SpaceNumber, room.Key.SpaceName)),
                    T(room.Key.Level),
                    T(string.Join(", ", roomWalls.Select(row => row.Orientation)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                    T(associated.Length > 0 ? "Yes" : "No"), T(openingTypes), T(openingNames)
                });
            }
        }
        return table;
    }

    private static string LinearRoomSelectionName(string level, string roomNumber, string roomName)
    {
        string number = (roomNumber ?? string.Empty).Trim();
        string name = CleanRoomName(number, roomName);
        if (number.Length == 0) return name;
        string linearNumber = number.Contains('/')
            ? number
            : $"{LinearStoreyCode(level, number)}/{number}";
        return name.Length == 0 ? linearNumber : $"{linearNumber} {name}";
    }

    private static string LinearStoreyDisplay(string level, string roomNumber)
    {
        string storey = LinearStoreyCode(level, roomNumber);
        string levelName = (level ?? string.Empty).Trim();
        return levelName.Length == 0 ? storey : $"{storey} {levelName}";
    }

    private static string LinearStoreyCode(string level, string roomNumber)
    {
        string number = (roomNumber ?? string.Empty).Trim();
        int slash = number.IndexOf('/');
        if (slash > 0) return number[..slash];
        if (TryReadPhysicalFloor(number, out int physicalFloor) ||
            TryReadLevelFloor(level, out physicalFloor))
        {
            int linearStorey = physicalFloor + 2;
            return linearStorey >= 0
                ? linearStorey.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
                : linearStorey.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        string fallback = (level ?? string.Empty).Trim();
        int separator = fallback.IndexOf('-');
        return separator > 0 ? fallback[..separator] : fallback;
    }

    private static bool TryReadPhysicalFloor(string roomNumber, out int floor)
    {
        floor = 0;
        string value = (roomNumber ?? string.Empty).Trim();
        int slash = value.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < value.Length) value = value[(slash + 1)..];
        int dot = value.IndexOf('.');
        if (dot <= 0) return false;
        return int.TryParse(value[..dot], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out floor);
    }

    private static bool TryReadLevelFloor(string level, out int floor)
    {
        floor = 0;
        string value = (level ?? string.Empty).Trim();
        int separator = value.IndexOf('-');
        string token = separator > 0 ? value[..separator] : value;
        if (token.StartsWith('U') && int.TryParse(token[1..], out int basement))
        {
            floor = -basement;
            return true;
        }
        return int.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out floor);
    }

    private static string CleanRoomName(string roomNumber, string roomName)
    {
        string number = (roomNumber ?? string.Empty).Trim();
        string name = (roomName ?? string.Empty).Trim();
        if (number.Length == 0 || name.Length == 0) return name;
        if (name.EndsWith(number, StringComparison.CurrentCultureIgnoreCase))
            name = name[..^number.Length].Trim().TrimEnd('-', '–', '—').Trim();
        else if (name.StartsWith(number, StringComparison.CurrentCultureIgnoreCase))
            name = name[number.Length..].Trim().TrimStart('-', '–', '—').Trim();
        return name;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> SunExposedRoomTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Level", "Space Number", "Room Name", "Has Sun-Exposed EWA",
                "EWA Orientations", "EWA Wall Groups", "EWA Thicknesses (mm)",
                "EWA Component / Materials", "EWA Area (m2)", "EWI Count", "ED Count",
                "Interior Opening Count", "All LINEAR Types in Room", "Status")
        };
        table.AddRange(rows
            .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
            .Select(group => new
            {
                group.Key,
                Rows = group.ToArray(),
                Ewa = group.Where(row => row.LinearCategory == LinearComponentKinds.ExteriorWall).ToArray()
            })
            .Where(room => room.Ewa.Length > 0)
            .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
            .Select(room => new Cell[]
            {
                T(room.Key.Level), T(room.Key.SpaceNumber), T(room.Key.SpaceName), T("Yes"),
                T(string.Join(", ", room.Ewa.Select(row => row.Orientation)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                N(room.Ewa.Select(row => $"{Math.Round(row.ThicknessMm, 1):0.0}|{WallMaterialSignature(row)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count()),
                T(string.Join(", ", room.Ewa.Select(row => Math.Round(row.ThicknessMm, 1))
                    .Distinct().OrderBy(value => value).Select(value => value.ToString("0.#")))),
                T(string.Join(" | ", room.Ewa.Select(row => row.Layers.Count == 0
                        ? row.LayerDescription
                        : string.Join(" + ", row.Layers.Select(layer => layer.Name)))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase))),
                N(room.Ewa.Sum(row => row.ExteriorAreaM2)),
                N(room.Rows.Where(row => row.LinearCategory == LinearComponentKinds.ExteriorWindow)
                    .Sum(row => row.WallCount)),
                N(room.Rows.Where(row => row.LinearCategory == LinearComponentKinds.ExteriorDoor)
                    .Sum(row => row.WallCount)),
                N(room.Rows.Where(row => row.LinearCategory is LinearComponentKinds.InteriorWindow or LinearComponentKinds.InteriorDoor)
                    .Sum(row => row.WallCount)),
                T(string.Join(", ", room.Rows.Select(row => row.LinearCategory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                T(room.Rows.All(row => row.Status == "Ready") ? "Ready" : "Review")
            }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> GroupedWallTypeTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        ExteriorWallRow[] openings = rows
            .Where(row => LinearComponentKinds.IsOpening(row.LinearCategory))
            .ToArray();
        var groups = rows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .GroupBy(row => new
            {
                row.LinearCategory,
                Thickness = Math.Round(row.ThicknessMm, 1),
                Materials = WallMaterialSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Thickness)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Group ID", "LINEAR Wall Type", "Wall Thickness (mm)", "Layer Count",
                "Component / Material Name", "Material Class", "Proposed LINEAR Material",
                "Room Count", "Room Numbers", "Rooms Using This Wall Type", "Levels", "Orientations",
                "Total Wall Area (m2)", "Wall Occurrence Count", "Wall Element Count",
                "Has Associated Opening", "Associated Opening Count", "Associated Opening Types",
                "Associated Opening Names", "Rooms With Associated Openings")
        };
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            string groupId = $"WG-{groupIndex + 1:000}";
            ExteriorWallRow[] walls = group
                .OrderBy(row => row.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Orientation, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ExteriorWallRow representative = walls[0];
            ExteriorWallRow[] associated = openings
                .Where(opening => walls.Any(wall => OpeningMatchesWall(wall, opening)))
                .Distinct()
                .ToArray();
            string materials = representative.Layers.Count == 0
                ? representative.LayerDescription
                : string.Join(" + ", representative.Layers.Select(layer => layer.Name));
            string classes = string.Join(", ", representative.Layers.Count == 0
                ? new[] { WallMaterialClassifier.Classify(representative.LayerDescription) }
                : representative.Layers.Select(layer => layer.MaterialClass)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            string proposal = representative.Layers.Count == 0
                ? LinearMaterialProposal.ForMaterial(
                    WallMaterialClassifier.Classify(representative.LayerDescription), representative.LayerDescription)
                : string.Join(" | ", representative.Layers.Select(layer => layer.LinearMaterialProposal)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            var rooms = walls
                .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string roomList = string.Join("; ", rooms.Select(room =>
                $"[{room.Key.Level}] {room.Key.SpaceNumber} - {room.Key.SpaceName} ({string.Join(", ", room.Select(row => row.Orientation).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))})"));
            string openingTypes = associated.Length == 0
                ? "--"
                : string.Join(", ", associated.Select(row => row.LinearCategory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            string openingNames = associated.Length == 0
                ? "--"
                : string.Join("; ", associated
                    .GroupBy(row => new { row.LinearCategory, row.LayerDescription })
                    .OrderBy(openingGroup => openingGroup.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(openingGroup => openingGroup.Key.LayerDescription, StringComparer.CurrentCultureIgnoreCase)
                    .Select(openingGroup =>
                        $"{openingGroup.Key.LinearCategory} x{openingGroup.Sum(row => row.WallCount)}: {openingGroup.Key.LayerDescription}"));
            string openingRooms = associated.Length == 0
                ? "--"
                : string.Join("; ", associated
                    .GroupBy(row => new { row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName })
                    .OrderBy(room => room.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(room => room.Key.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                    .Select(room => $"[{room.Key.Level}] {room.Key.SpaceNumber} - {room.Key.SpaceName}"));
            table.Add(new Cell[]
            {
                T(groupId), T(representative.LinearCategory), N(representative.ThicknessMm),
                N(representative.LayerCount), T(materials), T(classes), T(proposal), N(rooms.Length),
                T(string.Join("; ", rooms.Select(room => room.Key.SpaceNumber)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase))),
                T(roomList),
                T(string.Join(", ", walls.Select(row => row.Level).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))),
                T(string.Join(", ", walls.Select(row => row.Orientation).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                N(walls.Sum(row => row.ExteriorAreaM2)), N(walls.Length), N(walls.Sum(row => row.WallCount)),
                T(associated.Length > 0 ? "Yes" : "No"), N(associated.Sum(row => row.WallCount)),
                T(openingTypes), T(openingNames), T(openingRooms)
            });
        }
        return table;
    }

    private static string WallMaterialSignature(ExteriorWallRow row) => row.Layers.Count == 0
        ? $"{NormalizeMaterial(row.LayerDescription)}|{Math.Round(row.ThicknessMm, 1):0.0}"
        : string.Join("|", row.Layers.OrderBy(layer => layer.Rank)
            .Select(layer => $"{NormalizeMaterial(layer.Name)}:{Math.Round(layer.ThicknessMm, 1):0.0}"));

    private static string NormalizeMaterial(string value) =>
        string.Join(" ", (value ?? string.Empty).Trim().ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool OpeningMatchesWall(ExteriorWallRow wall, ExteriorWallRow opening)
    {
        if (wall.SpaceId != opening.SpaceId ||
            !string.Equals(wall.Orientation, opening.Orientation, StringComparison.OrdinalIgnoreCase))
            return false;
        if (wall.References.Count == 0 || opening.References.Count == 0) return true;
        double tolerance = UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);
        return wall.References.Any(wallReference => opening.References.Any(openingReference =>
            wallReference.RootLinkInstanceId == openingReference.RootLinkInstanceId &&
            wallReference.MinX <= openingReference.MaxX + tolerance &&
            wallReference.MaxX >= openingReference.MinX - tolerance &&
            wallReference.MinY <= openingReference.MaxY + tolerance &&
            wallReference.MaxY >= openingReference.MinY - tolerance &&
            wallReference.MinZ <= openingReference.MaxZ + tolerance &&
            wallReference.MaxZ >= openingReference.MinZ - tolerance));
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> RoomWallInventoryTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        static int CountSides(IEnumerable<ExteriorWallRow> source) => source
            .Select(row => $"{row.LinearCategory}|{row.Orientation}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        ExteriorWallRow[] wallRows = rows.ToArray();
        var roomStatistics = wallRows
            .GroupBy(row => row.SpaceId)
            .ToDictionary(group => group.Key, group => new
            {
                HasEwa = group.Any(row => row.LinearCategory == LinearComponentKinds.ExteriorWall),
                TotalSides = CountSides(group),
                EwaSides = CountSides(group.Where(row => row.LinearCategory == LinearComponentKinds.ExteriorWall)),
                IwaSides = CountSides(group.Where(row => row.LinearCategory == LinearComponentKinds.InteriorWall))
            });
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Level", "Space Number", "Space Name", "Room Has EWA",
                "Room Detected Sides", "Room EWA Sides", "Room IWA Sides", "Wall Type",
                "Boundary Condition",
                "Adjacent Space Number", "Adjacent Space Name", "Orientation",
                "Wall Thickness (mm)", "Layer Count", "Component / Material Name", "Material Class",
                "Proposed LINEAR Material / Component", "Layer Breakdown",
                "IFC Category", "Area (m2)", "Element Count", "Shared Wall", "Status")
        };
        table.AddRange(wallRows
            .OrderBy(row => row.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Orientation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ThicknessMm)
            .Select(row => new Cell[]
            {
                T(row.Level), T(row.SpaceNumber), T(row.SpaceName),
                T(roomStatistics[row.SpaceId].HasEwa ? "Yes" : "No"),
                N(roomStatistics[row.SpaceId].TotalSides),
                N(roomStatistics[row.SpaceId].EwaSides),
                N(roomStatistics[row.SpaceId].IwaSides),
                T(row.LinearCategory),
                T(LinearComponentKinds.IsExterior(row.LinearCategory)
                    ? "EXTERIOR / OUTSIDE AIR"
                    : row.AdjacentSpaceId > 0
                        ? "SHARED BETWEEN ROOMS"
                        : "INTERIOR / ADJACENT"),
                T(row.AdjacentSpaceNumber), T(row.AdjacentSpaceName), T(row.Orientation),
                N(row.ThicknessMm), N(row.LayerCount),
                T(row.Layers.Count == 0
                    ? row.LayerDescription
                    : string.Join(" + ", row.Layers.Select(layer => layer.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase))),
                T(string.Join(", ",
                    (row.Layers.Count == 0
                        ? new[] { WallMaterialClassifier.Classify(row.LayerDescription) }
                        : row.Layers.Select(layer => layer.MaterialClass))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                T(LinearComponentKinds.IsOpening(row.LinearCategory)
                    ? LinearMaterialProposal.ForComponent(
                        row.LinearCategory,
                        row.Layers.Count == 0
                            ? row.LayerDescription
                            : string.Join(" + ", row.Layers.Select(layer => layer.Name)))
                    : string.Join(" | ", row.Layers.Select(layer => layer.LinearMaterialProposal)
                        .Distinct(StringComparer.OrdinalIgnoreCase))),
                T(row.LayerTreeDisplay), T(row.IfcCategory), N(row.ExteriorAreaM2), N(row.WallCount),
                T(row.LinearCategory == LinearComponentKinds.InteriorWall && row.AdjacentSpaceId > 0 ? "Yes" : "No"),
                T(row.Status)
            }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> ExteriorWallTable(IReadOnlyList<ExteriorWallRow> rows)
    {
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Batch ID", "LINEAR Type", "Space Number", "Space Name", "Level", "Orientation",
                "Exposure", "Boundary Condition", "Adjacent Space Number", "Adjacent Space Name",
                "Thickness (mm)", "Layer Count", "Layer Description", "IFC Category",
                "Recommended Layer Count", "Recommended Layers", "IFC GUID", "Link",
                "Exterior Area (m2)", "Wall Count", "Target LINEAR", "Status")
        };
        table.AddRange(rows.Select(row => new Cell[]
        {
            T(row.BatchId), T(row.LinearCategory), T(row.SpaceNumber), T(row.SpaceName), T(row.Level), T(row.Orientation),
            T(row.Exposure),
            T(LinearComponentKinds.IsExterior(row.LinearCategory)
                ? "Outside Air"
                : row.LinearCategory == LinearComponentKinds.InteriorWall && row.AdjacentSpaceId > 0
                    ? "Shared Between Rooms"
                    : "Interior / Adjacent"),
            T(row.AdjacentSpaceNumber), T(row.AdjacentSpaceName),
            N(row.ThicknessMm), N(row.LayerCount), T(row.LayerDescription), T(row.IfcCategory),
            N(row.RecommendedLayerCount), T(row.RecommendedLayerDescription),
            T(row.IfcGuid), T(row.LinkName), N(row.ExteriorAreaM2), N(row.WallCount),
            T(row.TargetLinear), T(row.Status)
        }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> BatchTable(IReadOnlyList<ReplacementBatch> batches)
    {
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Batch ID", "LINEAR Type", "Source Assembly", "Layer Description", "Wall Count",
                "Recommended Layer Count", "Recommended Layers", "Exterior Area (m2)",
                "Rooms", "Levels", "Target LINEAR", "Status", "Assembly Key")
        };
        table.AddRange(batches.Select(batch => new Cell[]
        {
            T(batch.BatchId), T(batch.LinearCategory), T(batch.SourceAssembly), T(batch.LayerDescription), N(batch.WallCount),
            N(batch.RecommendedLayerCount), T(batch.RecommendedLayerDescription),
            N(batch.ExteriorAreaM2), N(batch.Rows.Select(row => row.SpaceId).Distinct().Count()),
            T(string.Join(", ", batch.Rows.Select(row => row.Level).Distinct())),
            T(batch.TargetLinear), T(batch.Status), T(batch.AssemblyKey)
        }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> WarningTable(IReadOnlyList<string> warnings)
    {
        var table = new List<IReadOnlyList<Cell>> { TextRow("No.", "Severity", "Message") };
        if (warnings.Count == 0)
            table.Add([N(1), T("Info"), T("No scan warnings.")]);
        else
            table.AddRange(warnings.Select((warning, index) => new Cell[] { N(index + 1), T("Review"), T(warning) }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> LinearPlanTable(IReadOnlyList<LinearBatchPlan> plans)
    {
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Plan ID", "LINEAR Type", "Total Thickness (mm)", "Proposed One LINEAR Component", "Space Count",
                "Room Face Count", "Review Face Count", "Levels", "Orientations",
                "Target LINEAR U-value", "Status", "Search Filters", "Replace Field", "Instructions")
        };
        table.AddRange(plans.Select(plan => new Cell[]
        {
            T(plan.BatchId), T(plan.LinearCategory), N(plan.TotalThicknessMm), T(plan.ComponentProposal), N(plan.SpaceCount),
            N(plan.RoomFaceCount), N(plan.ReviewFaceCount), T(plan.Levels), T(plan.Orientations),
            T(plan.TargetLinear), T(plan.Status),
            T($"Component Type = {LinearComponentKinds.DisplayName(plan.LinearCategory)} AND Adjoining = " +
              $"{(LinearComponentKinds.IsExterior(plan.LinearCategory) ? "Exterior" : "Adjacent Room")} AND Orientation = <scope>"),
            T("U-value"), T(plan.FindReplaceSummary)
        }));
        return table;
    }

    private static IReadOnlyList<IReadOnlyList<Cell>> LinearFaceTable(IReadOnlyList<LinearBatchPlan> plans)
    {
        var table = new List<IReadOnlyList<Cell>>
        {
            TextRow(
                "Plan ID", "LINEAR Type", "Level", "Space Number", "Space Name", "Orientation",
                "Exterior Area (m2)", "Detected Structures", "Expected LINEAR Row",
                "Target LINEAR U-value", "Match Status")
        };
        table.AddRange(plans.SelectMany(plan => plan.Faces.Select(face => new Cell[]
        {
            T(plan.BatchId), T(plan.LinearCategory), T(face.Level), T(face.SpaceNumber), T(face.SpaceName), T(face.Orientation),
            N(face.ExteriorAreaM2), N(face.StructureCount), T(face.ExpectedLinearRow),
            T(plan.TargetLinear), T(face.MatchStatus)
        })));
        return table;
    }

    private static void WriteSheet(
        ZipArchive archive,
        string entryName,
        IReadOnlyList<IReadOnlyList<Cell>> table,
        int columnCount)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using XmlWriter writer = CreateWriter(stream);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        string lastColumn = ColumnName(columnCount);
        writer.WriteStartElement("dimension");
        writer.WriteAttributeString("ref", $"A1:{lastColumn}{Math.Max(table.Count, 1)}");
        writer.WriteEndElement();
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("cols");
        for (int column = 1; column <= columnCount; column++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", column.ToString());
            writer.WriteAttributeString("max", column.ToString());
            writer.WriteAttributeString("width", ColumnWidth(column, columnCount).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        for (int rowIndex = 0; rowIndex < table.Count; rowIndex++)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", (rowIndex + 1).ToString());
            IReadOnlyList<Cell> row = table[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Count; columnIndex++)
                WriteCell(writer, row[columnIndex], rowIndex + 1, columnIndex + 1, rowIndex == 0);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        if (table.Count > 0)
        {
            writer.WriteStartElement("autoFilter");
            writer.WriteAttributeString("ref", $"A1:{lastColumn}{table.Count}");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteOutlineSheet(
        ZipArchive archive,
        string entryName,
        IReadOnlyList<OutlineRow> table,
        int columnCount)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using XmlWriter writer = CreateWriter(stream);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetPr");
        writer.WriteStartElement("outlinePr");
        writer.WriteAttributeString("summaryBelow", "0");
        writer.WriteAttributeString("showOutlineSymbols", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        string lastColumn = ColumnName(columnCount);
        writer.WriteStartElement("dimension");
        writer.WriteAttributeString("ref", $"A1:{lastColumn}{Math.Max(table.Count, 1)}");
        writer.WriteEndElement();
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("showOutlineSymbols", "1");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheetFormatPr");
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteAttributeString("outlineLevelRow", "1");
        writer.WriteEndElement();
        writer.WriteStartElement("cols");
        for (int column = 1; column <= columnCount; column++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", column.ToString());
            writer.WriteAttributeString("max", column.ToString());
            writer.WriteAttributeString("width", ColumnWidth(column, columnCount)
                .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        for (int rowIndex = 0; rowIndex < table.Count; rowIndex++)
        {
            OutlineRow row = table[rowIndex];
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", (rowIndex + 1).ToString());
            if (row.OutlineLevel > 0)
                writer.WriteAttributeString("outlineLevel", row.OutlineLevel.ToString());
            if (row.Hidden) writer.WriteAttributeString("hidden", "1");
            if (row.Collapsed) writer.WriteAttributeString("collapsed", "1");
            for (int columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
                WriteCell(
                    writer,
                    row.Cells[columnIndex],
                    rowIndex + 1,
                    columnIndex + 1,
                    rowIndex == 0,
                    row.IsWarning ? 4 : row.IsSummary ? 3 : null);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        if (table.Count > 0)
        {
            writer.WriteStartElement("autoFilter");
            writer.WriteAttributeString("ref", $"A1:{lastColumn}{table.Count}");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteCell(
        XmlWriter writer,
        Cell cell,
        int row,
        int column,
        bool header,
        int? styleOverride = null)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", $"{ColumnName(column)}{row}");
        writer.WriteAttributeString("s", (styleOverride ?? (header ? 1 : cell.Numeric ? 2 : 0)).ToString());
        if (cell.Numeric)
        {
            writer.WriteStartElement("v");
            writer.WriteString(cell.Value);
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is");
            writer.WriteStartElement("t");
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(cell.Value);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static double ColumnWidth(int column, int totalColumns)
    {
        if (totalColumns == 23)
            return column switch
            {
                2 => 18,
                3 => 30,
                4 => 16,
                8 => 14,
                9 => 25,
                10 => 20,
                11 => 28,
                15 => 44,
                16 => 18,
                17 => 60,
                18 => 55,
                19 => 22,
                _ => 15
            };
        if (totalColumns == 20)
            return column switch
            {
                1 => 13,
                5 => 44,
                6 => 18,
                7 => 58,
                8 => 14,
                9 => 45,
                10 => 90,
                11 => 18,
                12 => 22,
                16 => 22,
                18 => 22,
                19 => 58,
                20 => 70,
                _ => 15
            };
        if (totalColumns == 13)
            return column switch
            {
                1 => 13,
                4 => 48,
                5 => 18,
                6 => 42,
                7 => 20,
                8 => 32,
                10 => 22,
                12 => 22,
                13 => 58,
                _ => 16
            };
        if (totalColumns == 17)
            return column switch
            {
                1 => 13,
                5 => 48,
                6 => 18,
                7 => 58,
                8 => 14,
                9 => 44,
                10 => 24,
                11 => 80,
                12 => 18,
                13 => 22,
                14 => 65,
                15 => 65,
                16 => 24,
                17 => 18,
                _ => 15
            };
        if (totalColumns == 29)
            return column switch
            {
                1 => 14,
                2 => 38,
                3 => 13,
                4 => 16,
                5 or 6 or 7 => 20,
                8 or 9 => 13,
                10 => 20,
                11 => 56,
                12 => 20,
                13 => 14,
                14 => 52,
                15 => 70,
                16 => 52,
                17 => 58,
                18 => 22,
                19 => 52,
                20 => 70,
                21 => 20,
                22 => 24,
                23 => 18,
                24 => 34,
                25 => 52,
                26 => 38,
                27 => 48,
                28 => 38,
                29 => 26,
                _ => 15
            };
        if (totalColumns == 21)
            return column switch
            {
                2 => 18,
                3 => 28,
                4 => 16,
                9 => 24,
                10 => 20,
                11 => 28,
                15 => 42,
                16 => 52,
                17 => 22,
                _ => 15
            };
        if (totalColumns == 15)
            return column switch { 3 => 22, 8 => 42, 9 => 20, 10 => 30, 11 => 32, 14 => 18, _ => 15 };
        if (totalColumns == 10)
            return column switch { 2 => 28, 3 => 46, 7 => 24, 10 => 42, _ => 17 };
        return column == 3 ? 90 : 14;
    }

    private static Cell[] TextRow(params string[] values) => values.Select(T).ToArray();
    private static Cell T(string? value) => new(value ?? string.Empty, false);
    private static Cell N(double value) => new(value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), true);

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static XmlWriter CreateWriter(Stream stream) => XmlWriter.Create(stream, new XmlWriterSettings
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        CloseOutput = false
    });

    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string ContentTypes() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet3.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet4.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet5.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet6.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet7.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet8.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet9.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet10.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet11.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet12.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private static string PackageRelationships() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private static string Workbook() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="Sun Exposed Rooms" sheetId="8" r:id="rId8"/>
            <sheet name="Room Wall Inventory" sheetId="6" r:id="rId6"/>
            <sheet name="Grouped Wall Types" sheetId="7" r:id="rId7"/>
            <sheet name="LINEAR Room Replace" sheetId="9" r:id="rId9"/>
            <sheet name="LINEAR Replace Groups" sheetId="10" r:id="rId10"/>
            <sheet name="Wall Assembly Layers" sheetId="11" r:id="rId11"/>
            <sheet name="50mm Tolerance Groups" sheetId="12" r:id="rId12"/>
            <sheet name="Exterior Walls" sheetId="1" r:id="rId1"/>
            <sheet name="Replacement Batches" sheetId="2" r:id="rId2"/>
            <sheet name="LINEAR Batch Plan" sheetId="3" r:id="rId3"/>
            <sheet name="LINEAR Room Faces" sheetId="4" r:id="rId4"/>
            <sheet name="Warnings" sheetId="5" r:id="rId5"/>
          </sheets>
        </workbook>
        """;

    private static string WorkbookRelationships() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet3.xml"/>
          <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet4.xml"/>
          <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet5.xml"/>
          <Relationship Id="rId6" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet6.xml"/>
          <Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet7.xml"/>
          <Relationship Id="rId8" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet8.xml"/>
          <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet9.xml"/>
          <Relationship Id="rId10" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet10.xml"/>
          <Relationship Id="rId11" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet11.xml"/>
          <Relationship Id="rId12" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet12.xml"/>
          <Relationship Id="rId13" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string Styles() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="3">
            <font><sz val="10"/><name val="Segoe UI"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="10"/><name val="Segoe UI"/></font>
            <font><b/><color rgb="FF17345C"/><sz val="10"/><name val="Segoe UI"/></font>
          </fonts>
          <fills count="5">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1455C0"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFDCE9FB"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFE699"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="2" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="0" fontId="0" fillId="4" borderId="0" xfId="0" applyFill="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private sealed record Cell(string Value, bool Numeric);
    private sealed record OutlineRow(
        IReadOnlyList<Cell> Cells,
        int OutlineLevel = 0,
        bool Hidden = false,
        bool Collapsed = false,
        bool IsSummary = false,
        bool IsWarning = false);
}
