namespace FamilyMEP.Plugin.Models;

internal sealed record PipeFittingDefinition(
    string Key,
    string DisplayName,
    string Description,
    string OutputFamilyName,
    string ProductUrl,
    string CatalogUrl,
    string SourcePath);

internal static class PipeFittingCatalog
{
    public static IReadOnlyList<PipeFittingDefinition> All { get; } =
    [
        new(
            "GEBERIT_MAPRESS_SS_BEND_90",
            "Mapress Stainless Steel Bend 90°",
            "Official DN12-DN100 catalog. Lookup-driven d/L/Z, two parametric nested press ends and a native 90° elbow core.",
            "PipeFitting_Mapress_Bend90",
            "https://catalog.geberit.us/en-US/product/PRO_103025",
            "https://catalog.geberit.us/api/pdf/product?brand=geberit&locale=en-US&productId=PRO_103025",
            Infrastructure.AppPaths.GeberitMapressBendCatalogPdf)
    ];
}

internal sealed class PipeFittingInspection
{
    public string CategoryName { get; init; } = "Pipe Fittings";
    public string PartTypeName { get; init; } = "Elbow";
    public int ConnectorCount { get; init; } = 2;
    public int TypeCount { get; init; } = 1;
    public string DefaultTypeName { get; init; } = "DN20";
    public double NominalDiameterMm { get; init; } = 20;
    public double OutsideDiameterMm { get; init; } = 22;
    public double LengthMm { get; init; } = 47;
    public double ZMm { get; init; } = 26;
}

internal sealed record PipeFittingBuilderRequest(
    PipeFittingDefinition Definition,
    string OutputPath,
    bool OpenGeneratedFamily)
{
    public bool LoadIntoProject { get; init; } = true;
}

internal sealed class PipeFittingBuilderResult
{
    public string FamilyName { get; set; } = string.Empty;
    public string ActiveTypeName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int ConnectorCount { get; set; }
    public int TypeCount { get; set; }
    public List<string> AcceptanceChecks { get; } = [];
    public List<string> AppliedChanges { get; } = [];
    public List<string> Warnings { get; } = [];

    public string BuildReport()
    {
        var lines = new List<string>
        {
            $"Family: {FamilyName}",
            $"Prototype Type: {ActiveTypeName}",
            $"Category: {CategoryName}",
            $"Pipe connectors: {ConnectorCount}",
            $"Generated copy: {OutputPath}",
            string.Empty,
            "Automatic acceptance:"
        };
        lines.AddRange(AcceptanceChecks.Count == 0
            ? ["- No acceptance checks were recorded"]
            : AcceptanceChecks.Select(item => $"- PASS: {item}"));
        lines.Add(string.Empty);
        lines.Add("Applied:");
        lines.AddRange(AppliedChanges.Count == 0
            ? ["- None"]
            : AppliedChanges.Select(item => $"- {item}"));
        if (Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(item => $"- {item}"));
        }
        lines.Add(string.Empty);
        lines.Add(
            "Full lookup-table Types remain gated until this DN20 prototype is opened and flex-tested without Revit errors.");
        return string.Join(Environment.NewLine, lines);
    }
}
