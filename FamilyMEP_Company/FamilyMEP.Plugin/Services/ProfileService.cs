using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Infrastructure;

namespace FamilyMEP.Plugin.Services;

internal sealed class FamilyLibraryProfile
{
    public string Name { get; set; } = string.Empty;
    public List<string> FamilyPaths { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

internal static class ProfileService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<FamilyLibraryProfile> Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (!File.Exists(AppPaths.ProfileFile)) return [];
            return (JsonSerializer.Deserialize<List<FamilyLibraryProfile>>(
                        File.ReadAllText(AppPaths.ProfileFile), Options) ?? [])
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
                .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<FamilyLibraryProfile> profiles)
    {
        AppPaths.EnsureCreated();
        string temporary = AppPaths.ProfileFile + ".tmp";
        List<FamilyLibraryProfile> data = profiles
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Options));
        PortableFramework.MoveOverwrite(temporary, AppPaths.ProfileFile);
    }
}
