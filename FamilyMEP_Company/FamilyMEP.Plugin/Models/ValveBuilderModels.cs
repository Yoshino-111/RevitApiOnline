namespace FamilyMEP.Plugin.Models;

internal enum ValveSourceMode
{
    NewGenericModel,
    SelectedProjectFamily,
    FamilyFile
}

internal sealed record ValveSizeDefinition(
    string TypeName,
    double NominalDiameterMm,
    double BodyLengthMm,
    double BodyDiameterMm,
    double PortDiameterMm,
    double BodyHeightMm,
    double HandleLengthMm)
{
    public string SourceTypeName { get; init; } = TypeName;
    public string PartNumber { get; init; } = string.Empty;
    public string NominalSize { get; init; } = string.Empty;
    public double Cv { get; init; }
    public double TorqueNm { get; init; }
    public double WeightKg { get; init; }
}

internal sealed record ValveBuilderRequest(
    ValveSourceMode SourceMode,
    string FamilyPath,
    string TypeName,
    double NominalDiameterMm,
    double BodyLengthMm,
    double BodyHeightMm,
    double HandleAngleDegrees,
    string ConnectionType,
    string MaterialName,
    string DiameterParameterAliases,
    string LengthParameterAliases,
    string HeightParameterAliases,
    string AngleParameterAliases,
    string ConnectionParameterAliases,
    string MaterialParameterAliases,
    bool UseLookupTableGeometry,
    bool OpenGeneratedFamily,
    bool ApplyToSelectedInstances)
{
    public string FamilyName { get; init; } = "FamilyMEP_BallValve";
    public string OutputPath { get; init; } = string.Empty;
    public double BodyDiameterMm { get; init; }
    public double PortDiameterMm { get; init; }
    public double HandleLengthMm { get; init; }
    public IReadOnlyList<ValveSizeDefinition> SizeCatalog { get; init; } = [];
    public string Manufacturer { get; init; } = "FamilyMEP";
    public string ProductSeries { get; init; } = "Parametric Ball Valve";
    public string CatalogRevision { get; init; } = string.Empty;
    public string CatalogUrl { get; init; } = string.Empty;
    public string CatalogDimensionBasis { get; init; } = string.Empty;
    public string GeometryAccuracy { get; init; } = "Catalog envelope + procedural detail";
    public string LodStatus { get; init; } = "LOD 400 parametric; LOD 500 requires manufacturer CAD/BIM";
    public bool PreserveSourceParameters { get; init; }
    public string ExternalCatalogTypeName { get; init; } = string.Empty;
    public bool LoadIntoProject { get; init; } = true;
}

internal sealed class ValveBuilderResult
{
    public string FamilyName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int ConnectorCount { get; set; }
    public int UpdatedInstanceCount { get; set; }
    public List<string> AppliedParameters { get; } = [];
    public List<string> Warnings { get; } = [];

    public string BuildReport()
    {
        var lines = new List<string>
        {
            $"Family: {FamilyName}",
            $"Type: {TypeName}",
            $"Category: {CategoryName}",
            $"MEP connectors: {ConnectorCount}",
            $"Selected instances changed: {UpdatedInstanceCount}",
            $"Generated copy: {OutputPath}",
            string.Empty,
            "Parameters applied:"
        };

        lines.AddRange(AppliedParameters.Count == 0
            ? ["- None"]
            : AppliedParameters.Select(item => $"- {item}"));

        if (Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(item => $"- {item}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
