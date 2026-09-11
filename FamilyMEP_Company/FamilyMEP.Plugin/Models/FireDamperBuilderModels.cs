namespace FamilyMEP.Plugin.Models;

internal sealed record FireDamperDefinition(
    string Key,
    string DisplayName,
    string Description,
    string SourcePath,
    string OutputFamilyName,
    string ProductUrl,
    string CatalogUrl);

internal static class FireDamperCatalog
{
    public static IReadOnlyList<FireDamperDefinition> All { get; } =
    [
        new(
            "TROX_KA2_EU",
            "TROX KA2-EU Fire Damper",
            "Rectangular fire damper for commercial kitchen exhaust and extract air.",
            Infrastructure.AppPaths.TroxKa2CatalogPdf,
            "DuctAccessory_TROX_KA2_EU",
            "https://www.trox.de/en/fire-dampers/ka2-eu",
            "https://cdn.trox.de/a21cd7187374574a/6820aa7817a4/KA2-EU_PD_2025_05_08_DE_en.pdf")
    ];
}

internal sealed class FireDamperInspection
{
    public string CategoryName { get; init; } = "Duct Accessories";
    public int ConnectorCount { get; init; } = 2;
    public int TypeCount { get; init; }
    public string DefaultTypeName { get; init; } = "B500xH400";
}

internal sealed record FireDamperBuilderRequest(
    FireDamperDefinition Definition,
    string OutputPath,
    bool OpenGeneratedFamily)
{
    public bool LoadIntoProject { get; init; } = true;
}

internal sealed class FireDamperBuilderResult
{
    public string FamilyName { get; set; } = string.Empty;
    public string ActiveTypeName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int ConnectorCount { get; set; }
    public int TypeCount { get; set; }
    public List<string> AppliedChanges { get; } = [];
    public List<string> Warnings { get; } = [];

    public string BuildReport()
    {
        var lines = new List<string>
        {
            $"Family: {FamilyName}",
            $"Active Type: {ActiveTypeName}",
            $"Catalog Types: {TypeCount}",
            $"Category: {CategoryName}",
            $"Duct connectors: {ConnectorCount}",
            $"Generated copy: {OutputPath}",
            string.Empty,
            "Applied:"
        };
        lines.AddRange(AppliedChanges.Count == 0
            ? ["- None"]
            : AppliedChanges.Select(item => $"- {item}"));
        if (Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(item => $"- {item}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
