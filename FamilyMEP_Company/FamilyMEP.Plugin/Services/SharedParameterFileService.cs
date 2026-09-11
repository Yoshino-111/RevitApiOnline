namespace FamilyMEP.Plugin.Services;

internal sealed class SharedParameterDefinitionItem
{
    public required string GroupName { get; init; }
    public required string Name { get; init; }
    public required string Guid { get; init; }
    public required string DataType { get; init; }
    public required string Discipline { get; init; }
    public string Description { get; init; } = string.Empty;
    public string DisplayName => $"{Name}  |  {Discipline} - {DataType}";
}

internal static class SharedParameterFileService
{
    public static List<SharedParameterDefinitionItem> Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Shared parameter file was not found.", path);
        string[] lines = File.ReadAllLines(path);
        var groups = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            if (!line.StartsWith("GROUP\t", StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length >= 3 && int.TryParse(parts[1], out int id)) groups[id] = parts[2].Trim();
        }

        var definitions = new List<SharedParameterDefinitionItem>();
        foreach (string line in lines)
        {
            if (!line.StartsWith("PARAM\t", StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 6 || !int.TryParse(parts[5], out int groupId)) continue;
            string rawDataType = parts[3].Trim();
            definitions.Add(new SharedParameterDefinitionItem
            {
                Guid = parts[1].Trim(),
                Name = parts[2].Trim(),
                DataType = Humanize(rawDataType),
                Discipline = ResolveDiscipline(rawDataType),
                GroupName = groups.TryGetValue(groupId, out string? groupName) ? groupName : $"Group {groupId}",
                Description = parts.Length > 7 ? parts[7].Trim() : string.Empty
            });
        }
        return definitions
            .OrderBy(item => item.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ResolveDiscipline(string dataType)
    {
        string value = dataType.ToUpperInvariant();
        if (value.Contains("HVAC") || value.Contains("AIR_FLOW") || value.Contains("DUCT")) return "HVAC";
        if (value.Contains("PIPING") || value.Contains("PIPE") || value.Contains("FLOW") || value.Contains("PRESSURE")) return "Piping";
        if (value.Contains("ELECTRICAL") || value.Contains("CURRENT") || value.Contains("VOLTAGE") || value.Contains("POWER")) return "Electrical";
        if (value.Contains("STRUCTURAL") || value.Contains("FORCE") || value.Contains("MOMENT") || value.Contains("STRESS")) return "Structural";
        if (value.Contains("ENERGY") || value.Contains("THERMAL") || value.Contains("HEAT")) return "Energy";
        return "Common";
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        return string.Join(" ", value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant()));
    }
}
