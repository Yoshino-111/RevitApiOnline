using FamilyMEP.Plugin.Infrastructure;

namespace FamilyMEP.Plugin.Models;

internal sealed record AirTerminalDefinition(
    string Key,
    string DisplayName,
    string Description,
    string SourcePath,
    string OutputFamilyName,
    string AirflowRole,
    string ProductUrl,
    string CatalogUrl)
{
    public bool IsProcedural { get; init; }
}

internal static class AirTerminalCatalog
{
    public static IReadOnlyList<AirTerminalDefinition> All { get; } =
    [
        new(
            "1100",
            "1100 Perforated Diffuser",
            "Perforated-face supply diffuser with square or rectangular neck.",
            AppPaths.AirTerminal1100MasterFamily,
            "AirTerminal_PerforatedDiffuser",
            "Supply air",
            "https://www.krueger-hvac.com/Catalog%20Home/Diffusers/Diffusers%20-%20Perforated-Face/1100",
            "https://www.krueger-hvac.com/file/4069/11001190_Series_MiniCatalog.pdf"),
        new(
            "PLQ",
            "PLQ Plaque Diffuser",
            "Removable square plaque diffuser with manufacturer-authored geometry.",
            AppPaths.AirTerminalPlqMasterFamily,
            "AirTerminal_PlaqueDiffuser",
            "Supply air",
            "https://www.krueger-hvac.com/Catalog%20Home/Diffusers/Diffusers%20-%20Architectural/PLQ",
            "https://www.krueger-hvac.com/file/4047/PLQ_5PLQ_MiniCatalog.pdf"),
        new(
            "1900",
            "1900 Linear Slot Diffuser",
            "Linear slot diffuser with official slot-count, width and length types.",
            AppPaths.AirTerminal1900MasterFamily,
            "AirTerminal_LinearSlotDiffuser",
            "Supply air",
            "https://www.krueger-hvac.com/Catalog%20Home/Diffusers/Diffusers%20-%20Linear%20Slot/1900",
            "https://www.krueger-hvac.com/file/4083/1900_MiniCatalog.pdf"),
        new(
            "5810_5815",
            "5810 / 5815 Multi-Deflection Grille",
            "Supply/return grille with removable reversible multi-deflection core.",
            AppPaths.AirTerminal5810MasterFamily,
            "AirTerminal_MultiDeflectionGrille",
            "Supply / return air",
            "https://www.krueger-hvac.com/Catalog%20Home/Grilles/Grilles%20-%20Return/5810",
            "https://www.krueger-hvac.com/file/3371/5810_DimensionalData.pdf"),
        new(
            "S80_S85",
            "S80 / S85 Return Grille",
            "Fixed-blade return grille with official face and neck size types.",
            AppPaths.AirTerminalS80MasterFamily,
            "AirTerminal_ReturnGrille",
            "Return air",
            "https://www.krueger-hvac.com/Catalog%20Home/Grilles/Grilles%20-%20Return/S80",
            "https://www.krueger-hvac.com/file/4100/S80_MiniCatalog.pdf"),
        new(
            "TROX_RFD",
            "TROX RFD Swirl Diffuser",
            "Catalog-built circular or square ceiling swirl diffuser with fixed radial blades.",
            AppPaths.TroxRfdCatalogPdf,
            "AirTerminal_TROX_RFD",
            "Supply air",
            "https://www.trox.de/en/ceiling-diffusers/rfd-dc72f8222b4fce4b",
            "https://www.trox.de/en/downloads/795bbacb0342a1bc/RFD_PD_2026_03_26_DE_en.pdf?type=product_info")
        {
            IsProcedural = true
        }
    ];
}

internal sealed class AirTerminalInspection
{
    public string CategoryName { get; init; } = string.Empty;
    public int ConnectorCount { get; init; }
    public IReadOnlyList<string> TypeNames { get; init; } = [];
}

internal sealed record AirTerminalBuilderRequest(
    AirTerminalDefinition Definition,
    string PreviewTypeName,
    string OutputPath,
    bool OpenGeneratedFamily)
{
    public bool LoadIntoProject { get; init; } = true;
}

internal sealed class AirTerminalBuilderResult
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
            $"Official Types preserved: {TypeCount}",
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
