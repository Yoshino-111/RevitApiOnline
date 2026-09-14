namespace FamilyMEP.Plugin.SprinklerModeler;

internal sealed class LayerMappingItem
{
    public int Marker { get; init; }
    public string ColorHex { get; init; } = string.Empty;
    public string CadLayer { get; set; } = string.Empty;
    public int Detected { get; set; }
    public string RevitCategory { get; set; } = string.Empty;
    public string FamilyType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
}

internal sealed class ReviewIssueItem
{
    public string Severity { get; init; } = string.Empty;
    public string Issue { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Action { get; init; } = string.Empty;
}

internal sealed record ConnectionRule(
    string Key,
    string Name,
    string Orientation,
    string VerticalDirection,
    IReadOnlyList<string> Segments,
    string Description,
    string Confidence);

internal static class SprinklerModelerData
{
    internal static IReadOnlyDictionary<string, ConnectionRule> ConnectionRules { get; } =
        new Dictionary<string, ConnectionRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["direct_drop"] = new(
                "direct_drop",
                "Direct Drop",
                "Pendent",
                "Down",
                ["Drop"],
                "Vertical drop from branch to pendent head.",
                "97%"),
            ["armover"] = new(
                "armover",
                "Armover",
                "Pendent",
                "Down",
                ["Drop", "Horizontal", "Drop"],
                "Drop, elbow, and horizontal armover to the head.",
                "93%"),
            ["flexible_drop"] = new(
                "flexible_drop",
                "Flexible Drop",
                "Pendent",
                "Down",
                ["Flexible"],
                "Listed flexible connection from branch to ceiling head.",
                "89%"),
            ["direct_rise"] = new(
                "direct_rise",
                "Direct Rise",
                "Upright",
                "Up",
                ["Rise"],
                "Vertical sprig from branch to upward-facing head.",
                "96%"),
            ["armover_rise"] = new(
                "armover_rise",
                "Armover Rise",
                "Upright",
                "Up",
                ["Rise", "Horizontal", "Rise"],
                "Horizontal offset followed by a vertical sprig.",
                "90%")
        };

    internal static List<LayerMappingItem> CreateLayerMappings() =>
    [
        NewLayer(1, "#F36B5B", "FP-PIPE-MAIN", 48, "Pipe", "Wet Pipe - Main", "Mapped", "96%"),
        NewLayer(2, "#7657E8", "FP-SPRINKLER", 94, "Sprinkler", "Pendent / Upright", "Review", "88%"),
        NewLayer(3, "#0AA6A6", "FP-PIPE-BRANCH", 176, "Pipe", "Wet Pipe - Branch", "Mapped", "94%"),
        NewLayer(4, "#F2AE2E", "FP-FITTING", 63, "Pipe Fitting", "Routing Preference", "Mapped", "91%"),
        NewLayer(5, "#36A76D", "FP-VALVE", 12, "Pipe Accessory", "Gate Valve", "Review", "81%"),
        NewLayer(6, "#8C99A6", "FP-TEXT", 154, "Ignore", "-", "Ignored", "100%")
    ];

    internal static List<ReviewIssueItem> CreateReviewIssues() =>
    [
        NewIssue("Blocked", "Water supply data missing", "Sizing profile", 1, "Add flow test"),
        NewIssue("Review", "Head orientation inferred", "Level 02", 8, "Confirm rules"),
        NewIssue("Review", "Armover exceeds preferred length", "Grid B-7", 3, "Inspect route"),
        NewIssue("Ready", "Connections validated", "Level 02", 83, "Create")
    ];

    internal static string? DefaultRuleForOrientation(string orientation) => orientation switch
    {
        "Upright" => "direct_rise",
        "Pendent" => "direct_drop",
        _ => null
    };

    private static LayerMappingItem NewLayer(
        int marker,
        string colorHex,
        string layer,
        int detected,
        string category,
        string familyType,
        string status,
        string confidence) => new()
        {
            Marker = marker,
            ColorHex = colorHex,
            CadLayer = layer,
            Detected = detected,
            RevitCategory = category,
            FamilyType = familyType,
            Status = status,
            Confidence = confidence
        };

    private static ReviewIssueItem NewIssue(
        string severity,
        string issue,
        string location,
        int count,
        string action) => new()
        {
            Severity = severity,
            Issue = issue,
            Location = location,
            Count = count,
            Action = action
        };
}
