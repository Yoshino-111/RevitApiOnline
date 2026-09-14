using System.IO.Compression;
using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal sealed class FamilyPackageManifest
{
    public int FormatVersion { get; set; } = 1;
    public string PackageName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string PreviewColor { get; set; } = "#4B5563";
    public List<FamilyPackageEntry> Families { get; set; } = [];
}

internal sealed class FamilyPackageEntry
{
    public string FamilyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public bool CategoryIsExact { get; set; }
    public string FamilyArchivePath { get; set; } = string.Empty;
    public string? PreviewArchivePath { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public long Length { get; set; }
}

internal sealed record FamilyPackageImportResult(
    string LibraryRoot,
    string PreviewColor,
    int FamilyCount,
    int PreviewCount,
    int SkippedCount);

internal static class FamilyPackageService
{
    private const string ManifestEntryName = "familymep-package.json";
    private const string LegacyManifestEntryName = "ln-family-package.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Export(
        string packagePath,
        string packageName,
        IReadOnlyCollection<FamilyItem> families,
        string previewColor,
        IProgress<(int Current, int Total)>? progress = null,
        CompressionLevel familyCompression = CompressionLevel.Fastest)
    {
        EnsureDriveF(packagePath, "Package output");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        string temporaryPath = packagePath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

        var manifest = new FamilyPackageManifest
        {
            PackageName = SanitizeName(packageName),
            PreviewColor = previewColor,
            Families = []
        };
        var usedArchivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (FileStream file = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                int index = 0;
                foreach (FamilyItem family in families.OrderBy(item => item.Category).ThenBy(item => item.FamilyName))
                {
                    if (!File.Exists(family.Path)) continue;
                    index++;
                    string category = SanitizeName(family.Category);
                    string archivePath = UniqueArchivePath(
                        $"families/{category}/{Path.GetFileName(family.Path)}",
                        usedArchivePaths,
                        index);
                    AddFile(archive, family.Path, archivePath, familyCompression);

                    string? previewArchivePath = null;
                    string? previewPath = !string.IsNullOrWhiteSpace(family.PreviewImage) && File.Exists(family.PreviewImage)
                        ? family.PreviewImage
                        : RevitOperations.FindCachedPreview(family.Path, previewColor, family.Category);
                    if (previewPath is not null)
                    {
                        previewArchivePath = $"previews/{index:D6}.png";
                        AddFile(archive, previewPath, previewArchivePath, CompressionLevel.NoCompression);
                    }

                    manifest.Families.Add(new FamilyPackageEntry
                    {
                        FamilyName = family.FamilyName,
                        Category = family.Category,
                        Group = family.Group,
                        CategoryIsExact = family.HasExactCategory,
                        FamilyArchivePath = archivePath,
                        PreviewArchivePath = previewArchivePath,
                        ModifiedUtc = File.GetLastWriteTimeUtc(family.Path),
                        Length = new FileInfo(family.Path).Length
                    });
                    progress?.Report((index, families.Count));
                }

                ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                using Stream stream = manifestEntry.Open();
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
            }
            PortableFramework.MoveOverwrite(temporaryPath, packagePath);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static FamilyPackageImportResult Import(
        string packagePath,
        string destinationParent,
        IEnumerable<string> existingFamilyNames,
        IProgress<(int Current, int Total)>? progress = null)
    {
        EnsureDriveF(destinationParent, "Package destination");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Family package was not found.", packagePath);
        AppPaths.EnsureCreated();

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? archive.GetEntry(LegacyManifestEntryName)
            ?? throw new InvalidDataException("This ZIP is not a FamilyMEP package.");
        FamilyPackageManifest manifest;
        using (Stream stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<FamilyPackageManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("The package manifest is invalid.");
        }
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported package version: {manifest.FormatVersion}.");

        string libraryRoot = UniqueDirectory(destinationParent, SanitizeName(manifest.PackageName));
        Directory.CreateDirectory(libraryRoot);
        int importedFamilies = 0;
        int importedPreviews = 0;
        int skippedFamilies = 0;
        var importedExactCategories = new List<ResolvedFamilyCategory>();
        HashSet<string> knownNames = existingFamilyNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            int processedEntries = 0;
            foreach (FamilyPackageEntry family in manifest.Families)
            {
                processedEntries++;
                string familyName = string.IsNullOrWhiteSpace(family.FamilyName)
                    ? Path.GetFileNameWithoutExtension(family.FamilyArchivePath)
                    : family.FamilyName;
                if (!knownNames.Add(familyName))
                {
                    skippedFamilies++;
                    progress?.Report((processedEntries, manifest.Families.Count));
                    continue;
                }
                ZipArchiveEntry source = archive.GetEntry(family.FamilyArchivePath)
                    ?? throw new InvalidDataException($"Missing family entry: {family.FamilyArchivePath}");
                string categoryFolder = Path.Combine(libraryRoot, SanitizeName(family.Category));
                Directory.CreateDirectory(categoryFolder);
                string destinationPath = UniqueFilePath(categoryFolder, Path.GetFileName(family.FamilyArchivePath));
                ExtractEntry(source, destinationPath);
                File.SetLastWriteTimeUtc(destinationPath, family.ModifiedUtc);
                importedFamilies++;
                if (family.CategoryIsExact && !string.IsNullOrWhiteSpace(family.Category))
                {
                    importedExactCategories.Add(new ResolvedFamilyCategory(destinationPath, family.Category));
                }

                if (!string.IsNullOrWhiteSpace(family.PreviewArchivePath)
                    && archive.GetEntry(family.PreviewArchivePath) is { } previewEntry)
                {
                    string previewDestination = RevitOperations.GetPreviewCachePath(
                        destinationPath,
                        manifest.PreviewColor,
                        family.Category);
                    ExtractEntry(previewEntry, previewDestination);
                    File.SetLastWriteTimeUtc(previewDestination, DateTime.UtcNow);
                    importedPreviews++;
                }
                progress?.Report((processedEntries, manifest.Families.Count));
            }
            if (importedFamilies == 0)
            {
                Directory.Delete(libraryRoot, true);
                libraryRoot = string.Empty;
            }
            if (importedExactCategories.Count > 0)
            {
                FamilyCategoryCacheService.Update(importedExactCategories);
                FamilyCategoryCacheService.Save();
            }
            return new FamilyPackageImportResult(
                libraryRoot,
                manifest.PreviewColor,
                importedFamilies,
                importedPreviews,
                skippedFamilies);
        }
        catch
        {
            try
            {
                string resolvedRoot = Path.GetFullPath(libraryRoot);
                string resolvedParent = Path.GetFullPath(destinationParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (resolvedRoot.StartsWith(resolvedParent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolvedRoot, true);
            }
            catch { }
            throw;
        }
    }

    private static void AddFile(ZipArchive archive, string sourcePath, string archivePath, CompressionLevel compression)
    {
        ZipArchiveEntry entry = archive.CreateEntry(archivePath.Replace('\\', '/'), compression);
        entry.LastWriteTime = File.GetLastWriteTime(sourcePath);
        using Stream input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream output = entry.Open();
        input.CopyTo(output, 1024 * 1024);
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using Stream input = entry.Open();
        using Stream output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
        input.CopyTo(output, 1024 * 1024);
    }

    private static string UniqueArchivePath(string proposed, HashSet<string> used, int index)
    {
        string normalized = proposed.Replace('\\', '/');
        if (used.Add(normalized)) return normalized;
        string directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "families";
        string stem = Path.GetFileNameWithoutExtension(normalized);
        string extension = Path.GetExtension(normalized);
        string unique = $"{directory}/{stem}_{index:D4}{extension}";
        used.Add(unique);
        return unique;
    }

    private static string UniqueDirectory(string parent, string name)
    {
        string candidate = Path.Combine(parent, name);
        for (int index = 2; Directory.Exists(candidate); index++) candidate = Path.Combine(parent, $"{name}_{index}");
        return candidate;
    }

    private static string UniqueFilePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; File.Exists(candidate); index++) candidate = Path.Combine(folder, $"{stem}_{index}{extension}");
        return candidate;
    }

    private static string SanitizeName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "FamilyMEP_Package" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Trim(' ', '.');
    }

    private static void EnsureDriveF(string path, string description)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        if (!root.Equals(@"F:\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} must be on drive F:.");
        }
    }
}
