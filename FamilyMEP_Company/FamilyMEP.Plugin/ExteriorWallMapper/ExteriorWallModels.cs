using System.Globalization;
using Autodesk.Revit.DB;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.ExteriorWallMapper;

internal static class LinearComponentKinds
{
    internal const string ExteriorWall = "EWA";
    internal const string ExteriorWindow = "EWI";
    internal const string ExteriorDoor = "ED";
    internal const string InteriorWall = "IWA";
    internal const string InteriorWindow = "IWI";
    internal const string InteriorDoor = "ID";

    internal static string DisplayName(string value) => value switch
    {
        ExteriorWindow => "Exterior Window",
        ExteriorDoor => "Exterior Door",
        InteriorWall => "Interior Wall",
        InteriorWindow => "Interior Window",
        InteriorDoor => "Interior Door",
        _ => "Exterior Wall"
    };

    internal static bool IsExterior(string value) => value is ExteriorWall or ExteriorWindow or ExteriorDoor;
    internal static bool IsOpening(string value) => value is ExteriorWindow or InteriorWindow or ExteriorDoor or InteriorDoor;
}

public sealed record ExteriorWallLayerItem(
    int Rank,
    string Name,
    double ThicknessMm,
    bool IsExteriorFace,
    bool IsLast,
    string SourceElementId = "",
    string SourceUniqueId = "",
    string SourceIfcGuid = "",
    string SourceLinkPath = "",
    string SourceGeometryKey = "")
{
    public string ThicknessDisplay => $"{ThicknessMm:0.#} mm";
    public string MaterialClass => WallMaterialClassifier.Classify(Name);
    public string LinearMaterialProposal =>
        global::FamilyMEP.Plugin.ExteriorWallMapper.LinearMaterialProposal.ForMaterial(MaterialClass, Name);
    public string BranchDisplay =>
        $"{(IsLast ? "└─" : "├─")} {ThicknessDisplay,-10} {(IsExteriorFace ? "[OUTSIDE] " : string.Empty)}{Name}";
}

internal static class LinearMaterialProposal
{
    internal static string ForMaterial(string materialClass, string sourceName) => materialClass switch
    {
        "Glass" => $"Glass / glazing material; search DIN EN 572 or glass ({sourceName})",
        "Concrete" => $"Concrete; search DIN EN 12524 concrete ({sourceName})",
        "Insulation" => $"Insulation; search DIN EN 12524 mineral wool/insulation ({sourceName})",
        "Gypsum" => $"Gypsum board/plaster; search DIN 4108-4 gypsum ({sourceName})",
        "Metal" => $"Metal layer/frame; search steel or aluminium ({sourceName})",
        "Masonry" => $"Masonry; search DIN 4108-4 brick/masonry ({sourceName})",
        "Wood" => $"Wood; search DIN EN 12524 timber/wood ({sourceName})",
        "Plaster / Mortar" => $"Plaster/mortar; search DIN 4108-4 plaster ({sourceName})",
        _ => $"Manual material match required ({sourceName})"
    };

    internal static string ForComponent(string linearCategory, string sourceName) => linearCategory switch
    {
        LinearComponentKinds.ExteriorWindow => $"Create/select EWI Exterior Window component: {sourceName}",
        LinearComponentKinds.InteriorWindow => $"Create/select IWI Interior Window component: {sourceName}",
        LinearComponentKinds.ExteriorDoor => $"Create/select ED Exterior Door component: {sourceName}",
        LinearComponentKinds.InteriorDoor => $"Create/select ID Interior Door component: {sourceName}",
        _ => string.Empty
    };
}

internal static class WallMaterialClassifier
{
    internal static string Classify(string? value)
    {
        string text = RemoveDiacritics(value ?? string.Empty).ToUpperInvariant();
        if (ContainsAny(text, "GLAS", "GLASS", "VERGLAS")) return "Glass";
        if (ContainsAny(text, "STAHLBETON", "BETON", "CONCRETE")) return "Concrete";
        if (ContainsAny(text, "DAEMM", "DAMM", "INSULATION", "MINERALWOLLE", "MINERAL WOOL")) return "Insulation";
        if (ContainsAny(text, "GIPS", "GYPSUM", "GKB", "GKF")) return "Gypsum";
        if (ContainsAny(text, "METALL", "METAL", "STAHL", "STEEL", "ALUMIN", "ALU")) return "Metal";
        if (ContainsAny(text, "MAUER", "MASONRY", "ZIEGEL", "BRICK", "KALKSAND")) return "Masonry";
        if (ContainsAny(text, "HOLZ", "WOOD", "TIMBER")) return "Wood";
        if (ContainsAny(text, "PUTZ", "PLASTER", "MORTEL", "MOERTEL", "MORTAR")) return "Plaster / Mortar";
        return "Other";
    }

    private static string RemoveDiacritics(string value)
    {
        string decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        char[] characters = decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        return new string(characters).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

internal sealed class RoomOrientationSummary
{
    internal IReadOnlyList<ExteriorWallRow> Rows { get; init; } = [];

    public string Level { get; init; } = string.Empty;
    public string SpaceGroupLabel { get; init; } = string.Empty;
    public string Orientation { get; init; } = string.Empty;
    public string LinearCategories { get; init; } = string.Empty;
    public int WallGroupCount { get; init; }
    public int ArchitecturalWallTypeCount { get; init; }
    public int MinimumLayerCount { get; init; }
    public int MaximumLayerCount { get; init; }
    public string LayerCountDisplay => MinimumLayerCount == MaximumLayerCount
        ? MaximumLayerCount.ToString()
        : $"{MinimumLayerCount} - {MaximumLayerCount}";
}

internal sealed record LinkedWallReference(
    long RootLinkInstanceId,
    long LinkedElementId,
    string LinkPath,
    string SourceDocumentPath,
    Transform SourceToHost,
    IReadOnlyList<long> NestedLinkInstancePath,
    string ElementName,
    bool CanCreateDirectReference,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ)
{
    internal BoundingBoxXYZ ToBoundingBox() => new()
    {
        Min = new XYZ(MinX, MinY, MinZ),
        Max = new XYZ(MaxX, MaxY, MaxZ)
    };
}

internal sealed class ArchitecturalWallTypeGroup
{
    internal required IReadOnlyList<ExteriorWallRow> Rows { get; init; }
    internal required IReadOnlyList<LinkedWallReference> References { get; init; }

    public string TypeName { get; init; } = string.Empty;
    public double ThicknessMm { get; init; }
    public string ThicknessDisplay => ThicknessMm > 0 ? $"{ThicknessMm:0.#} mm" : "--";
    public int LayerCount { get; init; }
    public int ElementCount => References.Count;
    public int SpaceCount => Rows.Select(row => row.SpaceId).Distinct().Count();
    public string LinearCategories => string.Join(", ", Rows.Select(row => row.LinearCategory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public string Levels => string.Join(", ", Rows.Select(row => row.Level).Distinct().OrderBy(value => value));
    public string Orientations => string.Join(", ", Rows.Select(row => row.Orientation).Distinct().OrderBy(value => value));
    public string LinkChain { get; init; } = string.Empty;
}

internal sealed class WallThicknessGroup
{
    internal required IReadOnlyList<ExteriorWallRow> Rows { get; init; }
    internal required IReadOnlyList<LinkedWallReference> References { get; init; }
    internal required IReadOnlyList<WallThicknessSpaceItem> Spaces { get; init; }

    public double ThicknessMm { get; init; }
    public string ThicknessDisplay => $"{ThicknessMm:0} mm";
    public string LayerTypes { get; init; } = string.Empty;
    public int LayerTypeCount { get; init; }
    public int SpaceCount => Spaces.Count;
    public int StructureCount => Rows.Count;
    public int ElementCount => References.Count;
    public string LinearCategories => string.Join(", ", Rows.Select(row => row.LinearCategory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public double ExteriorAreaM2 => Rows.Sum(row => row.ExteriorAreaM2);
    public string ExteriorAreaDisplay => $"{ExteriorAreaM2:0.##}";
    public string Levels => string.Join(", ", Rows.Select(row => row.Level).Distinct().OrderBy(value => value));
    public string Orientations => string.Join(", ", Rows.Select(row => row.Orientation).Distinct().OrderBy(value => value));
}

internal sealed class WallThicknessSpaceItem
{
    internal required ExteriorWallRow Row { get; init; }

    public string Level => Row.Level;
    public string SpaceNumber => Row.SpaceNumber;
    public string SpaceName => Row.SpaceName;
    public string SpaceDisplay => Row.SpaceGroupLabel;
    public string Orientation => Row.Orientation;
    public string LinearCategory => Row.LinearCategory;
    public string MatchingLayerTypes { get; init; } = string.Empty;
    public string AssemblyThickness => Row.ThicknessDisplay;
    public int AssemblyLayers => Row.LayerCount;
    public string ExteriorArea => Row.ExteriorAreaDisplay;
}

internal sealed class ExteriorWallRow : BindableBase
{
    private string _targetLinear = string.Empty;
    private bool _isSelected;

    internal long SpaceId { get; init; }
    internal string AssemblyKey { get; init; } = string.Empty;
    internal IReadOnlyList<LinkedWallReference> References { get; init; } = [];

    public string BatchId { get; set; } = string.Empty;
    public string LinearCategory { get; init; } = LinearComponentKinds.ExteriorWall;
    public string SpaceNumber { get; init; } = string.Empty;
    public string SpaceName { get; init; } = string.Empty;
    public string SpaceDisplay => $"{SpaceNumber}  {SpaceName}".Trim();
    public string SpaceGroupLabel => string.IsNullOrWhiteSpace(SpaceName)
        ? SpaceNumber
        : $"{SpaceNumber} — {SpaceName}";
    internal long AdjacentSpaceId { get; init; }
    public string AdjacentSpaceNumber { get; init; } = string.Empty;
    public string AdjacentSpaceName { get; init; } = string.Empty;
    public string AdjacentSpaceDisplay => AdjacentSpaceId <= 0
        ? "--"
        : string.IsNullOrWhiteSpace(AdjacentSpaceName)
            ? AdjacentSpaceNumber
            : $"{AdjacentSpaceNumber} - {AdjacentSpaceName}";
    public string Level { get; init; } = string.Empty;
    public string Orientation { get; init; } = string.Empty;
    public double ThicknessMm { get; init; }
    public string ThicknessDisplay => ThicknessMm > 0 ? $"{ThicknessMm:0.#} mm" : "--";
    public int LayerCount { get; init; }
    public string LayerDescription { get; init; } = string.Empty;
    public IReadOnlyList<ExteriorWallLayerItem> Layers { get; init; } = [];
    public string LayerTreeDisplay => Layers.Count == 0
        ? LayerDescription
        : string.Join(Environment.NewLine, Layers.Select(layer => layer.BranchDisplay));
    public IReadOnlyList<ExteriorWallLayerItem> RecommendedLayers { get; init; } = [];
    public int RecommendedLayerCount => RecommendedLayers.Count;
    public string RecommendedLayerDescription { get; init; } = string.Empty;
    public string RecommendedLayerTreeDisplay => RecommendedLayers.Count == 0
        ? RecommendedLayerDescription
        : string.Join(Environment.NewLine, RecommendedLayers.Select(layer => layer.BranchDisplay));
    public string Exposure => LinearComponentKinds.IsExterior(LinearCategory)
        ? "Sun-exposed"
        : "Adjacent Space";
    public string IfcCategory { get; init; } = string.Empty;
    public string IfcGuid { get; init; } = string.Empty;
    public string LinkName { get; init; } = string.Empty;
    public double ExteriorAreaM2 { get; init; }
    public string ExteriorAreaDisplay => $"{ExteriorAreaM2:0.##}";
    public int WallCount { get; init; }
    public string Status { get; init; } = "Review";
    public string StatusColor => Status switch
    {
        "Ready" => "#14966A",
        "Unmapped" => "#8B97A3",
        _ => "#F2A11B"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string TargetLinear
    {
        get => _targetLinear;
        set => Set(ref _targetLinear, value ?? string.Empty);
    }
}

internal sealed class RoomWallInventoryItem
{
    internal required ExteriorWallRow Row { get; init; }
    internal IReadOnlyList<LinkedWallReference> References => Row.References;

    public string Level => Row.Level;
    public string SpaceNumber => Row.SpaceNumber;
    public string SpaceName => Row.SpaceName;
    public string SpaceDisplay => Row.SpaceGroupLabel;
    public string Orientation => Row.Orientation;
    public string BoundaryCondition => LinearComponentKinds.IsExterior(Row.LinearCategory)
        ? "EXTERIOR / OUTSIDE AIR"
        : Row.AdjacentSpaceId > 0
            ? "SHARED BETWEEN ROOMS"
            : "INTERIOR / ADJACENT";
    public string BoundaryCode => Row.LinearCategory;
    public string AdjacentSpace => Row.AdjacentSpaceDisplay;
    public bool IsSharedWall => Row.LinearCategory == LinearComponentKinds.InteriorWall && Row.AdjacentSpaceId > 0;
    public string SharedWallDisplay => IsSharedWall ? "Yes" : "No";
    public bool IsExteriorWall => Row.LinearCategory == LinearComponentKinds.ExteriorWall;
    public string SharedPairKey => IsSharedWall
        ? $"{Math.Min(Row.SpaceId, Row.AdjacentSpaceId)}|{Math.Max(Row.SpaceId, Row.AdjacentSpaceId)}|{Row.AssemblyKey}"
        : string.Empty;
    public string MarkerColor => IsSharedWall ? "#FFFFFF" : IsExteriorWall ? "#14966A" : "#657187";
    public string Thickness => Row.ThicknessDisplay;
    public double ThicknessMm => Row.ThicknessMm;
    public int LayerCount => Row.LayerCount;
    public string Materials => Row.Layers.Count == 0
        ? Row.LayerDescription
        : string.Join(" + ", Row.Layers.Select(layer => layer.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase));
    public string MaterialLayers => Row.LayerTreeDisplay;
    public string MaterialClasses => string.Join(", ",
        (Row.Layers.Count == 0
            ? new[] { WallMaterialClassifier.Classify(Row.LayerDescription) }
            : Row.Layers.Select(layer => layer.MaterialClass))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public string LinearProposal => LinearComponentKinds.IsOpening(Row.LinearCategory)
        ? LinearMaterialProposal.ForComponent(Row.LinearCategory, Materials)
        : Row.Layers.Count == 0
            ? LinearMaterialProposal.ForMaterial(
                WallMaterialClassifier.Classify(Row.LayerDescription),
                Row.LayerDescription)
            : string.Join(" | ", Row.Layers.Select(layer => layer.LinearMaterialProposal)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    public string IfcCategory => Row.IfcCategory;
    public string Area => Row.ExteriorAreaDisplay;
    public int WallCount => Row.WallCount;
    public string Status => Row.Status;
    public string StatusColor => Row.StatusColor;
}

internal sealed class RoomWallRoomSummary
{
    internal long SpaceId { get; init; }
    internal IReadOnlyList<ExteriorWallRow> Rows { get; init; } = [];

    public string SpaceKey => $"{SpaceId}|{Level}|{SpaceNumber}";
    public string Level { get; init; } = string.Empty;
    public string SpaceNumber { get; init; } = string.Empty;
    public string SpaceName { get; init; } = string.Empty;
    public string SpaceDisplay => string.IsNullOrWhiteSpace(SpaceName)
        ? SpaceNumber
        : $"{SpaceNumber} - {SpaceName}";
    public bool HasEwa => Rows.Any(row => row.LinearCategory == LinearComponentKinds.ExteriorWall);
    public string HasEwaDisplay => HasEwa ? "Yes" : "No";
    public int DetectedSideCount => CountSides(Rows);
    public int EwaSideCount => CountSides(Rows.Where(row => row.LinearCategory == LinearComponentKinds.ExteriorWall));
    public int IwaSideCount => CountSides(Rows.Where(row => row.LinearCategory == LinearComponentKinds.InteriorWall));
    public int OpeningCount => Rows.Count(row => LinearComponentKinds.IsOpening(row.LinearCategory));
    public int SharedSideCount => CountSides(Rows.Where(row =>
        row.LinearCategory == LinearComponentKinds.InteriorWall && row.AdjacentSpaceId > 0));
    public int AssemblyCount => Rows.Count;
    public string WallTypes => string.Join(", ", Rows.Select(row => row.LinearCategory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static int CountSides(IEnumerable<ExteriorWallRow> rows) => rows
        .Select(row => $"{row.LinearCategory}|{row.Orientation}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

internal sealed class WallTypeGroupItem
{
    internal IReadOnlyList<ExteriorWallRow> Rows { get; init; } = [];
    internal IReadOnlyList<ExteriorWallRow> AssociatedOpenings { get; init; } = [];

    public string GroupId { get; init; } = string.Empty;
    public string LinearCategory => Rows.FirstOrDefault()?.LinearCategory ?? string.Empty;
    public double ThicknessMm => Rows.FirstOrDefault()?.ThicknessMm ?? 0.0;
    public string Thickness => ThicknessMm > 0 ? $"{ThicknessMm:0.#} mm" : "--";
    public int LayerCount => Rows.FirstOrDefault()?.LayerCount ?? 0;
    public string Materials => Rows.Count == 0
        ? string.Empty
        : Rows[0].Layers.Count == 0
            ? Rows[0].LayerDescription
            : string.Join(" + ", Rows[0].Layers.Select(layer => layer.Name));
    public string MaterialClasses => string.Join(", ", Rows
        .SelectMany(row => row.Layers.Count == 0
            ? new[] { WallMaterialClassifier.Classify(row.LayerDescription) }
            : row.Layers.Select(layer => layer.MaterialClass))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public string LinearMaterialProposal => Rows.Count == 0
        ? string.Empty
        : string.Join(" | ", Rows[0].Layers.Select(layer => layer.LinearMaterialProposal)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    public int RoomCount => Rows.Select(row => row.SpaceId).Distinct().Count();
    public int WallOccurrenceCount => Rows.Count;
    public int WallElementCount => Rows.Sum(row => row.WallCount);
    public double AreaM2 => Rows.Sum(row => row.ExteriorAreaM2);
    public string Area => $"{AreaM2:0.##}";
    public int OpeningCount => AssociatedOpenings.Sum(row => row.WallCount);
    public string OpeningTypes => string.Join(", ", AssociatedOpenings
        .Select(row => row.LinearCategory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
}

internal sealed class WallTypeOccurrenceItem
{
    internal required ExteriorWallRow Wall { get; init; }
    internal IReadOnlyList<ExteriorWallRow> AssociatedOpenings { get; init; } = [];

    public string Level => Wall.Level;
    public string SpaceNumber => Wall.SpaceNumber;
    public string RoomName => Wall.SpaceName;
    public string Orientation => Wall.Orientation;
    public string WallType => Wall.LinearCategory;
    public string Thickness => Wall.ThicknessDisplay;
    public string Area => Wall.ExteriorAreaDisplay;
    public int WallElements => Wall.WallCount;
    public int OpeningCount => AssociatedOpenings.Sum(row => row.WallCount);
    public string OpeningTypes => string.Join(", ", AssociatedOpenings
        .Select(row => row.LinearCategory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public string OpeningsInSameSpaceAndOrientation => AssociatedOpenings.Count == 0
        ? "--"
        : string.Join("; ", AssociatedOpenings
            .GroupBy(row => new { row.LinearCategory, row.LayerDescription })
            .Select(group => $"{group.Key.LinearCategory} ×{group.Sum(row => row.WallCount)}: {group.Key.LayerDescription}"));
}

internal sealed class ReplacementBatch : BindableBase
{
    private string _targetLinear = string.Empty;
    private bool _isSelected;

    internal string AssemblyKey { get; init; } = string.Empty;
    internal IReadOnlyList<ExteriorWallRow> Rows { get; init; } = [];

    public string BatchId { get; init; } = string.Empty;
    public string LinearCategory { get; init; } = LinearComponentKinds.ExteriorWall;
    public string SourceAssembly { get; init; } = string.Empty;
    public string LayerDescription { get; init; } = string.Empty;
    public IReadOnlyList<ExteriorWallLayerItem> Layers { get; init; } = [];
    public string LayerTreeDisplay => Layers.Count == 0
        ? LayerDescription
        : string.Join(Environment.NewLine, Layers.Select(layer => layer.BranchDisplay));
    public IReadOnlyList<ExteriorWallLayerItem> RecommendedLayers { get; init; } = [];
    public int RecommendedLayerCount => RecommendedLayers.Count;
    public string RecommendedLayerDescription { get; init; } = string.Empty;
    public string RecommendedLayerTreeDisplay => RecommendedLayers.Count == 0
        ? RecommendedLayerDescription
        : string.Join(Environment.NewLine, RecommendedLayers.Select(layer => layer.BranchDisplay));
    public int WallCount { get; init; }
    public double ExteriorAreaM2 { get; init; }
    public string ExteriorAreaDisplay => $"{ExteriorAreaM2:0.##}";
    public string Status { get; init; } = "Review";
    public string StatusColor => Status switch
    {
        "Ready" => "#14966A",
        "Unmapped" => "#8B97A3",
        _ => "#F2A11B"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string TargetLinear
    {
        get => _targetLinear;
        set => Set(ref _targetLinear, value ?? string.Empty);
    }
}

internal sealed class LinearRoomFaceItem
{
    internal required IReadOnlyList<ExteriorWallRow> Rows { get; init; }
    internal long SpaceId { get; init; }

    public string BatchId { get; init; } = string.Empty;
    public string LinearCategory { get; init; } = LinearComponentKinds.ExteriorWall;
    public bool IsGlobalEwa { get; init; }
    public string Level { get; init; } = string.Empty;
    public string SpaceNumber { get; init; } = string.Empty;
    public string SpaceName { get; init; } = string.Empty;
    public string SpaceDisplay => string.IsNullOrWhiteSpace(SpaceName)
        ? SpaceNumber
        : $"{SpaceNumber} - {SpaceName}";
    public string Orientation { get; init; } = string.Empty;
    public double ExteriorAreaM2 { get; init; }
    public string ExteriorAreaDisplay => $"{ExteriorAreaM2:0.##}";
    public int StructureCount { get; init; }
    public string ExpectedLinearRow => LinearCategory switch
    {
        LinearComponentKinds.ExteriorWindow => $"Exterior Window / {Orientation} / Exterior",
        LinearComponentKinds.InteriorWindow => $"Interior Window / {Orientation} / Adjacent Room",
        LinearComponentKinds.ExteriorDoor => $"Exterior Door / {Orientation} / Exterior",
        LinearComponentKinds.InteriorDoor => $"Interior Door / {Orientation} / Adjacent Room",
        LinearComponentKinds.InteriorWall => $"Interior Wall / {Orientation} / Adjacent Room",
        _ => $"Exterior Wall / {Orientation} / Exterior"
    };
    public string MatchStatus => IsGlobalEwa
        ? "Global EWA"
        : StructureCount <= 1 ? "Ready" : $"Review: {StructureCount} structures";
    public string StatusColor => IsGlobalEwa || StructureCount <= 1 ? "#14966A" : "#F2A11B";
}

internal sealed class LinearBatchPlan : BindableBase
{
    internal required IReadOnlyList<ReplacementBatch> Batches { get; init; }
    internal required IReadOnlyList<ExteriorWallRow> Rows { get; init; }
    internal required IReadOnlyList<LinearRoomFaceItem> Faces { get; init; }

    public string PlanId { get; init; } = string.Empty;
    public string LinearCategory { get; init; } = LinearComponentKinds.ExteriorWall;
    public bool IsGlobalEwa { get; init; }
    public string LinearCategoryDisplay => $"{LinearCategory} - {LinearComponentKinds.DisplayName(LinearCategory)}";
    public string BatchId => PlanId;
    public string SourceBatchIds => string.Join(", ", Batches.Select(batch => batch.BatchId));
    private ReplacementBatch SampleBatch => Batches[0];
    public string DetectedStructure => string.IsNullOrWhiteSpace(SampleBatch.RecommendedLayerDescription)
        ? SampleBatch.LayerDescription
        : SampleBatch.RecommendedLayerDescription;
    public string DetectedStructureTree => SampleBatch.RecommendedLayers.Count == 0
        ? SampleBatch.LayerTreeDisplay
        : SampleBatch.RecommendedLayerTreeDisplay;
    public double TotalThicknessMm => Rows.FirstOrDefault()?.ThicknessMm ?? 0;
    public string TotalThicknessDisplay => $"{TotalThicknessMm:0.#} mm";
    public string ComponentProposal => IsGlobalEwa
        ? $"1 Exterior Wall component for ALL EWA - {_globalEwaFaceLabel}"
        : LinearComponentKinds.IsOpening(LinearCategory)
        ? $"1 {LinearComponentKinds.DisplayName(LinearCategory)} component - {DetectedStructure}"
        : $"1 {LinearComponentKinds.DisplayName(LinearCategory)} composite - total {TotalThicknessDisplay}; " +
          $"{SampleBatch.RecommendedLayerCount} material layer(s) (recheck in DETAIL)";
    private string _globalEwaFaceLabel => $"{RoomFaceCount} room face(s); structures in DETAIL only";
    public int SpaceCount => Faces.Select(face => face.SpaceId).Distinct().Count();
    public int RoomFaceCount => Faces.Count;
    public int ReviewFaceCount => IsGlobalEwa ? 0 : Faces.Count(face => face.StructureCount > 1);
    public string Levels => string.Join(", ", Faces.Select(face => face.Level)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
    public string Orientations => string.Join(", ", Faces.Select(face => face.Orientation)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    public bool HasMixedTargets => Batches.Select(batch => batch.TargetLinear)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Skip(1)
        .Any();
    public string Status => HasMixedTargets
        ? "Resolve targets"
        : string.IsNullOrWhiteSpace(TargetLinear)
        ? "Assign Bxxx"
        : ReviewFaceCount > 0
            ? "Review hit list"
            : "Ready";
    public string StatusColor => HasMixedTargets
        ? "#B3261E"
        : string.IsNullOrWhiteSpace(TargetLinear)
        ? "#8B97A3"
        : ReviewFaceCount > 0
            ? "#F2A11B"
            : "#14966A";
    public string FindReplaceSummary
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(TargetLinear) ? "<select Bxxx>" : TargetLinear.Trim();
            if (IsGlobalEwa)
                return $"Search Room Components at Project level: Component Type = Exterior Wall AND Adjoining = Exterior. " +
                       $"Replace U-value = {target}. Verify {RoomFaceCount} expected EWA room face(s); no orientation filter is required.";
            string componentType = LinearComponentKinds.DisplayName(LinearCategory);
            string adjoining = LinearComponentKinds.IsExterior(LinearCategory) ? "Exterior" : "Adjacent Room";
            string scopes = string.Join("; ", Faces
                .GroupBy(face => new { face.Level, face.Orientation })
                .OrderBy(group => group.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.Key.Orientation, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key.Level} / {group.Key.Orientation}: {group.Count()} face(s)"));
            return $"Search Room Components: Component Type = {componentType} AND Adjoining = {adjoining}; " +
                   $"filter Orientation per scope ({scopes}). Replace U-value = {target}. " +
                   $"Verify the hit list against {RoomFaceCount} expected room face(s).";
        }
    }

    public string TargetLinear
    {
        get
        {
            string[] targets = Batches.Select(batch => batch.TargetLinear)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return targets.Length == 1 ? targets[0] : string.Empty;
        }
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (Batches.All(batch => string.Equals(batch.TargetLinear, normalized, StringComparison.Ordinal))) return;
            foreach (ReplacementBatch batch in Batches) batch.TargetLinear = normalized;
            NotifyTargetChanged();
        }
    }

    internal void NotifyTargetChanged()
    {
        Raise(nameof(TargetLinear));
        Raise(nameof(Status));
        Raise(nameof(StatusColor));
        Raise(nameof(HasMixedTargets));
        Raise(nameof(FindReplaceSummary));
    }
}

internal sealed record ExteriorWallScanResult(
    IReadOnlyList<ExteriorWallRow> Rows,
    int LoadedLinkCount,
    int TotalLinkCount,
    int SpaceCount,
    IReadOnlyList<string> Warnings);
