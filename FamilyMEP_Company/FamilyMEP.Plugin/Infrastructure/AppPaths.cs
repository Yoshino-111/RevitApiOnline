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
    public static string GeneratedPipeFittingFolder => Path.Combine(Root, "generated-pipe-fittings");
    public static string GeneratedAirTerminalFolder => Path.Combine(Root, "generated-air-terminals");
    public static string GeneratedDuctAccessoryFolder => Path.Combine(Root, "generated-duct-accessories");
    public static string CatalogFolder => Path.Combine(Root, "catalogs");
    public static string TemplateFolder => Path.Combine(Root, "templates");
    public static string GenericModel2020Template =>
        ResolveDataFile(
            Path.Combine("templates", "Metric Generic Model 2020.rft"));
    public static string BallValveMasterFamily => ResolveBallValveMasterFamily();
    public static string GateValveMasterFamily => ResolveValveMasterFamily(
        "GateValve",
        "GateValve_Threaded_Master.rfa");
    public static string GlobeValveMasterFamily => ResolveValveMasterFamily(
        "GlobeValve",
        "GlobeValve_Threaded_Master.rfa");
    public static string AngleValveMasterFamily => ResolveValveMasterFamily(
        "AngleValve",
        "AngleValve_Threaded_Master.rfa");
    public static string SwingCheckValveMasterFamily => ResolveValveMasterFamily(
        "SwingCheckValve",
        "SwingCheckValve_Threaded_Master.rfa");
    public static string RingCheckValveMasterFamily => ResolveValveMasterFamily(
        "RingCheckValve",
        "RingCheckValve_Threaded_Master.rfa");
    public static string ButterflyValveMasterFamily => ResolveValveMasterFamily(
        "ButterflyValve",
        "ButterflyValve_Wafer_Handle_Master.rfa");
    public static string AirTerminal1100MasterFamily => ResolveValveMasterFamily(
        Path.Combine("AirTerminal", "1100"),
        "AirTerminal_Master.rfa");
    public static string AirTerminalPlqMasterFamily => ResolveValveMasterFamily(
        Path.Combine("AirTerminal", "PLQ"),
        "AirTerminal_Master.rfa");
    public static string AirTerminal1900MasterFamily => ResolveValveMasterFamily(
        Path.Combine("AirTerminal", "1900"),
        "AirTerminal_Master.rfa");
    public static string AirTerminal5810MasterFamily => ResolveValveMasterFamily(
        Path.Combine("AirTerminal", "5810_5815"),
        "AirTerminal_Master.rfa");
    public static string AirTerminalS80MasterFamily => ResolveValveMasterFamily(
        Path.Combine("AirTerminal", "S80_S85"),
        "AirTerminal_Master.rfa");
    public static string TroxRfdCatalogPdf =>
        ResolveDataFile(
            Path.Combine(
                "catalogs",
                "AirTerminal",
                "TROX_RFD",
                "TROX_RFD_Product_Data_2026.pdf"));
    public static string TroxKa2CatalogPdf =>
        ResolveDataFile(
            Path.Combine(
                "catalogs",
                "DuctAccessory",
                "TROX_KA2",
                "TROX_KA2_EU_Product_Data_2025.pdf"));
    public static string GeberitMapressBendCatalogPdf =>
        ResolveDataFile(
            Path.Combine(
                "catalogs",
                "PipeFitting",
                "Geberit_PRO_103025",
                "Geberit_PRO_103025.pdf"));

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
        Directory.CreateDirectory(GeneratedPipeFittingFolder);
        Directory.CreateDirectory(GeneratedAirTerminalFolder);
        Directory.CreateDirectory(GeneratedDuctAccessoryFolder);
        Directory.CreateDirectory(CatalogFolder);
        Directory.CreateDirectory(TemplateFolder);
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

        // Assembly.Location is empty when the Revit 2020 Drain plugin is loaded
        // from bytes. The hot loader publishes its source folder explicitly so
        // diagnostics and shared data still resolve beside the development build.
        string? hotReloadSource = AppDomain.CurrentDomain.GetData(
            "FamilyMEP.HotReloadSourceFolder") as string;
        if (!string.IsNullOrWhiteSpace(hotReloadSource))
        {
            DirectoryInfo? hotCursor = new DirectoryInfo(hotReloadSource);
            for (int depth = 0; hotCursor is not null && depth < 6;
                 depth++, hotCursor = hotCursor.Parent)
            {
                string developmentData = Path.Combine(hotCursor.FullName, "familymep-data");
                if (Directory.Exists(developmentData) && IsDriveF(developmentData))
                    return developmentData;
            }
        }

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
        return ResolveValveMasterFamily("BallValve", "BallValve_Threaded_Master.rfa");
    }

    private static string ResolveValveMasterFamily(string catalogName, string fileName)
    {
        string primary = Path.Combine(CatalogFolder, catalogName, fileName);
        if (File.Exists(primary)) return primary;

        string assemblyFolder = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? string.Empty;
        DirectoryInfo? cursor = string.IsNullOrWhiteSpace(assemblyFolder)
            ? null
            : new DirectoryInfo(assemblyFolder);
        for (int depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            string[] candidates =
            [
                Path.Combine(cursor.FullName, "familymep-data", "catalogs", catalogName, fileName),
                Path.Combine(cursor.FullName, "FamilyMEP", "Data", "catalogs", catalogName, fileName),
                Path.Combine(cursor.FullName, "Data", "catalogs", catalogName, fileName)
            ];
            string? existing = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }
        return primary;
    }

    private static string ResolveDataFile(string relativePath)
    {
        string primary = Path.Combine(Root, relativePath);
        if (File.Exists(primary)) return primary;

        string assemblyFolder =
            Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? string.Empty;
        DirectoryInfo? cursor = string.IsNullOrWhiteSpace(assemblyFolder)
            ? null
            : new DirectoryInfo(assemblyFolder);
        for (int depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            string[] candidates =
            [
                Path.Combine(cursor.FullName, "familymep-data", relativePath),
                Path.Combine(cursor.FullName, "FamilyMEP", "Data", relativePath),
                Path.Combine(cursor.FullName, "Data", relativePath)
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
