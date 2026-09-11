namespace FamilyMEP.Plugin.Models;

internal enum ViewStandardKind
{
    ViewTemplate,
    ViewFilter
}

internal sealed class ViewStandardItem : BindableBase
{
    private bool _selected;

    public long ElementIdValue { get; set; }
    public ViewStandardKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Categories { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#CBD5E1";
    public int FilterCount { get; set; }
    public int ControlledParameterCount { get; set; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string KindLabel => Kind == ViewStandardKind.ViewTemplate ? "View Template" : "View Filter";
    public string KindGlyph => Kind == ViewStandardKind.ViewTemplate ? "\uE8A5" : "\uE71C";
    public string DetailLine => Kind == ViewStandardKind.ViewTemplate
        ? $"{FilterCount:N0} filter(s)  •  {ControlledParameterCount:N0} controlled setting(s)"
        : string.IsNullOrWhiteSpace(Categories) ? "No category information" : Categories;
}

internal sealed class ViewStandardsProject : BindableBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public string RevitVersion { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
    public List<ViewStandardItem> Items { get; set; } = [];
    public int TemplateCount => Items.Count(item => item.Kind == ViewStandardKind.ViewTemplate);
    public int FilterCount => Items.Count(item => item.Kind == ViewStandardKind.ViewFilter);
    public int TotalCount => Items.Count;
    public string VersionLabel => string.IsNullOrWhiteSpace(RevitVersion) ? "Revit project" : $"Revit {RevitVersion}";
    public string ImportedLabel => ImportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CountLabel => $"{TemplateCount:N0} templates  •  {FilterCount:N0} filters";
}

internal sealed class ViewStandardsReadResult
{
    public string RevitVersion { get; init; } = string.Empty;
    public List<ViewStandardItem> Items { get; init; } = [];
}

internal sealed class ViewStandardsApplyResult
{
    public int Loaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Messages { get; } = [];
}

internal sealed class ViewStandardDetailEntry
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}
