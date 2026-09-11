using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class ViewStandardsLibraryService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<ViewStandardsProject> Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (!File.Exists(AppPaths.ViewStandardsFile)) return [];
            return (JsonSerializer.Deserialize<List<ViewStandardsProject>>(
                        File.ReadAllText(AppPaths.ViewStandardsFile), Options) ?? [])
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<ViewStandardsProject> projects)
    {
        AppPaths.EnsureCreated();
        string temporary = AppPaths.ViewStandardsFile + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                Options));
        PortableFramework.MoveOverwrite(temporary, AppPaths.ViewStandardsFile);
    }

    public static ViewStandardsProject CreateSnapshot(string sourcePath)
    {
        AppPaths.EnsureCreated();
        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)
            || !Path.GetExtension(fullSourcePath).Equals(".rvt", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Select a valid Revit project (.rvt).", sourcePath);
        }

        string id = Guid.NewGuid().ToString("N");
        string projectFolder = Path.Combine(AppPaths.ViewStandardsSourceFolder, id);
        Directory.CreateDirectory(projectFolder);
        string snapshotPath = Path.Combine(projectFolder, SanitizeFileName(Path.GetFileName(fullSourcePath)));
        File.Copy(fullSourcePath, snapshotPath, overwrite: true);
        return new ViewStandardsProject
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(fullSourcePath),
            OriginalPath = fullSourcePath,
            SnapshotPath = snapshotPath,
            ImportedUtc = DateTime.UtcNow
        };
    }

    public static void DeleteSnapshot(ViewStandardsProject project)
    {
        if (string.IsNullOrWhiteSpace(project.SnapshotPath)) return;
        string sourceRoot = Path.GetFullPath(AppPaths.ViewStandardsSourceFolder)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string projectFolder = Path.GetDirectoryName(Path.GetFullPath(project.SnapshotPath)) ?? string.Empty;
        string guardedFolder = projectFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!guardedFolder.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a standards source outside the managed library.");
        }

        if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "standards.rvt" : value;
    }
}
