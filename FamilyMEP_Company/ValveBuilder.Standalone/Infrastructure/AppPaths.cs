namespace FamilyMEP.Plugin.Infrastructure;

internal static class AppPaths
{
    private static readonly Lazy<string> ResolvedRoot = new(ResolveRoot);

    public static string Root => ResolvedRoot.Value;
    public static string StateFile => Path.Combine(Root, "state.json");
    public static string PreviewFolder => Path.Combine(Root, "previews");
    public static string BuiltInPreviewFolder => Path.Combine(PreviewFolder, "built-in");
    public static string BackupFolder => Path.Combine(Root, "backups");
    public static string LogFolder => Path.Combine(Root, "logs");
    public static string SetFolder => Path.Combine(Root, "sets");
    public static string PackageFolder => Path.Combine(Root, "packages");
    public static string ToolLibraryFolder => Path.Combine(Root, "tool-library");
    public static string BuiltInLibraryFolder => Path.Combine(ToolLibraryFolder, "built-in");
    public static string UserLibraryFolder => Path.Combine(ToolLibraryFolder, "user-library");
    public static string ExtractedFamilyFolder => Path.Combine(ToolLibraryFolder, "extracted-cache");
    public static string ProfileFile => Path.Combine(Root, "project-profiles.json");
    public static string CategoryCacheFile => Path.Combine(Root, "family-categories.json");
    public static string ViewStandardsFolder => Path.Combine(Root, "view-standards");
    public static string ViewStandardsSourceFolder => Path.Combine(ViewStandardsFolder, "sources");
    public static string ViewStandardsFile => Path.Combine(ViewStandardsFolder, "library.json");
    public static string GeneratedValveFolder => Path.Combine(Root, "generated-valves");
    public static string CatalogFolder => Path.Combine(Root, "catalogs");
    public static string BallValveMasterFamily => ResolveBallValveMasterFamily();

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PreviewFolder);
        Directory.CreateDirectory(BuiltInPreviewFolder);
        Directory.CreateDirectory(BackupFolder);
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(SetFolder);
        Directory.CreateDirectory(PackageFolder);
        Directory.CreateDirectory(ToolLibraryFolder);
        Directory.CreateDirectory(BuiltInLibraryFolder);
        Directory.CreateDirectory(UserLibraryFolder);
        Directory.CreateDirectory(ExtractedFamilyFolder);
        Directory.CreateDirectory(ViewStandardsFolder);
        Directory.CreateDirectory(ViewStandardsSourceFolder);
        Directory.CreateDirectory(GeneratedValveFolder);
        Directory.CreateDirectory(CatalogFolder);
        TryRemoveLegacyPreviewFolder();
    }

    private static string ResolveRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("FAMILYMEP_HOME");
        if (!IsDriveF(configured))
        {
            // Preserve existing company-machine installations that used the old variable.
            configured = Environment.GetEnvironmentVariable("LN_FAMILY_MANAGER_HOME");
        }
        if (IsDriveF(configured)) return Path.GetFullPath(configured!);

        string assemblyFolder = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? string.Empty;
        DirectoryInfo? cursor = string.IsNullOrWhiteSpace(assemblyFolder) ? null : new DirectoryInfo(assemblyFolder);
        for (int depth = 0; cursor is not null && depth < 6; depth++, cursor = cursor.Parent)
        {
            string developmentData = Path.Combine(cursor.FullName, "familymep-data");
            if (Directory.Exists(developmentData) && IsDriveF(developmentData)) return developmentData;
            string legacyDevelopmentData = Path.Combine(cursor.FullName, "family-manager-data");
            if (Directory.Exists(legacyDevelopmentData) && IsDriveF(legacyDevelopmentData)) return legacyDevelopmentData;
            string portableData = Path.Combine(cursor.FullName, "Data");
            if (Directory.Exists(portableData) && IsDriveF(portableData)) return portableData;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FamilyMEP",
            "Data");
    }

    private static string ResolveBallValveMasterFamily()
    {
        const string fileName = "BallValve_Threaded_Master.rfa";
        string primary = Path.Combine(CatalogFolder, "BallValve", fileName);
        if (File.Exists(primary)) return primary;

        string assemblyFolder = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? string.Empty;
        DirectoryInfo? cursor = string.IsNullOrWhiteSpace(assemblyFolder)
            ? null
            : new DirectoryInfo(assemblyFolder);
        for (int depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            string[] candidates =
            [
                Path.Combine(cursor.FullName, "familymep-data", "catalogs", "BallValve", fileName),
                Path.Combine(cursor.FullName, "FamilyMEP", "Data", "catalogs", "BallValve", fileName),
                Path.Combine(cursor.FullName, "Data", "catalogs", "BallValve", fileName)
            ];
            string? existing = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }
        return primary;
    }

    private static bool IsDriveF(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), @"F:\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryRemoveLegacyPreviewFolder()
    {
        try
        {
            var current = new DirectoryInfo(Root);
            if (!current.Name.Equals("familymep-data", StringComparison.OrdinalIgnoreCase) || current.Parent is null) return;
            string legacy = Path.Combine(current.Parent.FullName, "family-manager-data");
            if (!Directory.Exists(legacy)) return;

            // The migration leaves only preview images that were locked by the running Revit process.
            // Never remove a legacy folder that still contains family, archive, state, or backup data.
            string[] files = Directory.GetFiles(legacy, "*", SearchOption.AllDirectories);
            if (files.Any(path => !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))) return;
            Directory.Delete(legacy, recursive: true);
        }
        catch
        {
            // Revit may still hold an image handle. A later FamilyMEP start will retry safely.
        }
    }
}
