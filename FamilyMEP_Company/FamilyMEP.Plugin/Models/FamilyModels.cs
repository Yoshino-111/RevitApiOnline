using System.Windows;

namespace FamilyMEP.Plugin.Models;

internal sealed class FamilyItem : BindableBase
{
    private bool _selected;
    private bool _favorite;
    private string? _previewImage;
    private string _status = "OK";
    private string _category = string.Empty;
    private string _group = string.Empty;
    private bool _hasExactCategory;

    public required string FamilyName { get; init; }
    public string Name => FamilyName;
    public required string Path { get; init; }
    public required string Category { get => _category; set => Set(ref _category, value); }
    public required string Group { get => _group; set => Set(ref _group, value); }
    public bool HasExactCategory { get => _hasExactCategory; set => Set(ref _hasExactCategory, value); }
    public required long Length { get; init; }
    public required DateTime ModifiedDate { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsToolLibrary { get; set; }
    public string? ArchivePath { get; init; }
    public string? ArchiveEntryPath { get; init; }
    public string? ArchivePreviewEntryPath { get; init; }
    public bool RequiresExtraction => IsBuiltIn && !File.Exists(Path);
    public string FileSize => FormatSize(Length);
    public string Types => Category;
    public string Size => FileSize;
    public string Modified => ModifiedDate.ToString("yyyy-MM-dd HH:mm");
    public string Version => "RFA";
    public string Shared => IsBuiltIn ? "Built-in library" : IsToolLibrary ? "Tool library" : "File library";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string StatusGlyph => Status == "OK" ? "OK" : "!";
    public int Issues => Status == "OK" ? 0 : 1;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }
    public string? PreviewImage { get => _previewImage; set => Set(ref _previewImage, value); }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d:0.#} MB";
        }

        return $"{Math.Max(1, bytes / 1024d):0} KB";
    }
}

internal sealed class CategoryItem
{
    public required string Name { get; init; }
    public required string FilterKey { get; init; }
    public required int Count { get; init; }
    public bool IsGroup { get; init; }
    public bool IsExpanded { get; init; } = true;
    public string ExpandGlyph => IsGroup ? IsExpanded ? "\uE70D" : "\uE76C" : string.Empty;
    public string ToggleGlyph => IsGroup ? "⌄" : string.Empty;
    public Thickness IndentMargin => IsGroup || FilterKey is "*" or "favorites" ? new(0) : new(28, 0, 0, 0);
    public string IconData => FilterKey switch
    {
        "*" => "M2,2 L7,2 L7,7 L2,7 Z M9,2 L14,2 L14,7 L9,7 Z M2,9 L7,9 L7,14 L2,14 Z M9,9 L14,9 L14,14 L9,14 Z",
        "favorites" => "M8,1.5 L10,5.5 L14.5,6.1 L11.2,9.2 L12.1,13.8 L8,11.6 L3.9,13.8 L4.8,9.2 L1.5,6.1 L6,5.5 Z",
        var key when key.Equals("group:MEP Model", StringComparison.OrdinalIgnoreCase) => "M2,5 L11,5 L14,8 L11,11 L2,11 Z M5,5 L5,11 M11,5 L11,11 M8,2 L8,5",
        var key when key.Equals("group:Annotation", StringComparison.OrdinalIgnoreCase) => "M2,3 L9,3 L14,8 L8,14 L2,8 Z M5,6 A1,1 0 1 0 5.1,6",
        var key when key.Equals("group:Support", StringComparison.OrdinalIgnoreCase) => "M2,3 L14,3 M8,3 L8,13 M4,13 L12,13",
        _ => "M2,4 L7,4 L9,6 L14,6 L14,13 L2,13 Z"
    };

    public string CategoryIconData
    {
        get
        {
            string key = FilterKey.ToLowerInvariant();
            if (key == "*") return "M2,2 L7,2 L7,7 L2,7 Z M9,2 L14,2 L14,7 L9,7 Z M2,9 L7,9 L7,14 L2,14 Z M9,9 L14,9 L14,14 L9,14 Z";
            if (key == "favorites") return "M8,1.5 L10,5.5 L14.5,6.1 L11.2,9.2 L12.1,13.8 L8,11.6 L3.9,13.8 L4.8,9.2 L1.5,6.1 L6,5.5 Z";
            if (key.StartsWith("group:")) return IconData;
            if (key.Contains("air terminal")) return "M2,3 L14,3 L14,13 L2,13 Z M5,6 L11,6 M5,9 L11,9";
            if (key.Contains("duct fitting") || key.Contains("pipe fitting")) return "M3,2 L8,2 L8,8 L14,8 L14,13 L6,13 L6,5 L3,5 Z";
            if (key.Contains("duct accessor")) return "M2,5 L14,5 L14,11 L2,11 Z M6,5 L10,11 M10,5 L6,11";
            if (key.Contains("pipe accessor") || key.Contains("plumbing fixture")) return "M2,8 L6,8 M10,8 L14,8 M6,5 L10,8 L6,11 Z";
            if (key.Contains("sprinkler")) return "M8,2 L8,7 M4,7 L12,7 M5,10 L3,13 M8,10 L8,14 M11,10 L13,13";
            if (key.Contains("cable tray")) return "M2,4 L14,4 L12,12 L4,12 Z M5,4 L6,12 M8,4 L8,12 M11,4 L10,12";
            if (key.Contains("conduit")) return "M2,8 A3,3 0 0 1 5,5 L11,5 A3,3 0 0 1 14,8 M2,8 A3,3 0 0 0 5,11 L11,11 A3,3 0 0 0 14,8";
            if (key.Contains("communication") || key.Contains("data device")) return "M3,3 A1.5,1.5 0 1 0 3.1,3 M13,4 A1.5,1.5 0 1 0 13.1,4 M8,13 A1.5,1.5 0 1 0 8.1,13 M4,4 L7,11 M12,5 L9,11";
            if (key.Contains("lighting")) return "M8,2 A4,4 0 0 1 11,8 L10,10 L6,10 L5,8 A4,4 0 0 1 8,2 M6,12 L10,12 M7,14 L9,14";
            if (key.Contains("electrical") || key.Contains("fire alarm")) return "M9,1 L3,9 L7,9 L6,15 L13,6 L9,6 Z";
            if (key.Contains("mechanical equipment")) return "M8,2 A2,2 0 1 0 8,6 A2,2 0 1 0 8,2 M3,8 L13,8 L13,14 L3,14 Z M6,8 L6,14 M10,8 L10,14";
            if (key.Contains("structural") || key.Contains("column") || key.Contains("framing")) return "M3,2 L13,2 L13,5 L9,5 L9,12 L13,12 L13,15 L3,15 L3,12 L7,12 L7,5 L3,5 Z";
            if (key.Contains("door") || key.Contains("window")) return "M3,2 L13,2 L13,14 L3,14 Z M6,5 L11,5 L11,14 L6,14 Z";
            if (key.Contains("tag") || key.Contains("annotation")) return "M2,3 L9,3 L14,8 L8,14 L2,8 Z M5,6 A1,1 0 1 0 5.1,6";
            if (key.Contains("profile")) return "M3,3 L13,3 L13,6 L7,6 L7,10 L13,10 L13,13 L3,13 Z";
            return IconData;
        }
    }
}

internal sealed class ParameterItem
{
    public bool Selected { get; set; }
    public required string Name { get; init; }
    public string NewName { get; set; } = string.Empty;
    public required string Group { get; init; }
    public required string DataType { get; init; }
    public required string InstanceType { get; init; }
    public bool Shared { get; init; }
    public int FoundIn { get; set; }
    public string Formula { get; init; } = string.Empty;
}

internal sealed class FamilyInspectionResult
{
    public List<FamilyTypeInfo> Types { get; init; } = [];
    public List<FamilyParameterInfo> Parameters { get; init; } = [];
}

internal sealed class FamilyTypeInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}

internal sealed class FamilyParameterInfo
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string DataType { get; init; }
    public required string Scope { get; init; }
    public string Value { get; init; } = string.Empty;
    public string Formula { get; init; } = string.Empty;
    public bool Shared { get; init; }
    public string Metadata => $"{Scope}  |  {DataType}  |  {Group}{(Shared ? "  |  Shared" : string.Empty)}";
    public string ValueDisplay => !string.IsNullOrWhiteSpace(Formula)
        ? $"Formula: {Formula}"
        : !string.IsNullOrWhiteSpace(Value)
            ? $"Value: {Value}"
            : "No default value";
}

internal sealed class BatchLogItem
{
    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");
    public required string Level { get; init; }
    public required string Message { get; init; }
    public required string FamilyName { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
}

internal enum BatchOperationKind
{
    RenameParameter,
    AddSharedParameter,
    ReplaceParameter,
    SetValue,
    SetFormula,
    RemoveParameter,
    RenameTypes,
    CreateTypes,
    DeleteTypes,
    RenameFiles,
    ChangeCategory,
    NamingStandard
}

internal sealed class BatchChangeItem : BindableBase
{
    private bool _selected = true;
    public required string FamilyPath { get; init; }
    public required string FamilyName { get; init; }
    public required string Category { get; init; }
    public required string ItemName { get; init; }
    public required string CurrentValue { get; init; }
    public required string NewValue { get; init; }
    public required string Validation { get; init; }
    public required string Action { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool CanRun => Validation is "Ready" or "Warning";
}

internal sealed record BatchOperationRequest(
    BatchOperationKind Kind,
    string MatchRule,
    string Source,
    string Target,
    string ConflictHandling,
    string SharedParameterFile,
    bool IsInstance,
    string ParameterGroup);

internal sealed class BatchFamilyPreviewResult
{
    public List<ParameterItem> Parameters { get; init; } = [];
    public List<BatchChangeItem> Changes { get; init; } = [];
}

internal sealed record BatchFamilyExecutionResult(BatchLogItem Log, string? NewPath = null);

internal sealed record BackupSessionInfo(
    string Folder,
    string DisplayName,
    int FamilyCount,
    long SizeBytes,
    DateTime Created);

internal sealed class ProjectFamilyItem : BindableBase
{
    private bool _selected;
    public required string UniqueId { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

internal sealed record ProjectDocumentItem(string Key, string DisplayName);

internal sealed record FamilyLoadResult(int Loaded, int Skipped);

internal sealed record ResolvedFamilyCategory(string Path, string Category);

internal sealed record ProjectFamilyExportResult(
    int Exported,
    int Skipped,
    List<ResolvedFamilyCategory> Families);

internal sealed record PreviewCreationResult(string PreviewPath, string? RevitCategory);

internal sealed class CategorySelectionItem : BindableBase
{
    private bool _selected;
    public required string Name { get; init; }
    public required int Count { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string DisplayName => $"{Name} ({Count})";
}

internal sealed class FamilyPickerItem : BindableBase
{
    private bool _selected;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? Path { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
