using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Infrastructure;

namespace FamilyMEP.Plugin.Services;

internal sealed class ManagerState
{
    public List<string> Roots { get; set; } = [];
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string PreviewColor { get; set; } = "#4B5563";
    public string WorkspaceBackground { get; set; } = "None";
    public double WorkspaceBackgroundOpacity { get; set; } = 0.12;
    public string ActiveProfileName { get; set; } = string.Empty;
}

internal static class StateService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ManagerState Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.StateFile))
            {
                return JsonSerializer.Deserialize<ManagerState>(File.ReadAllText(AppPaths.StateFile), Options)
                    ?? new ManagerState();
            }
        }
        catch
        {
            // A damaged state file must not prevent the Revit tool from opening.
        }

        return new ManagerState();
    }

    public static void Save(ManagerState state)
    {
        AppPaths.EnsureCreated();
        string temporary = AppPaths.StateFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        PortableFramework.MoveOverwrite(temporary, AppPaths.StateFile);
    }
}
