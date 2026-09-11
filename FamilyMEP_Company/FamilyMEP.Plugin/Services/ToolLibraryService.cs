using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal sealed record ToolLibrarySaveResult(
    int Saved,
    int Skipped,
    List<string> SavedPaths,
    Dictionary<string, string> PathMap);

internal static class ToolLibraryService
{
    private const string ManifestEntryName = "familymep-package.json";
    private const string LegacyManifestEntryName = "ln-family-package.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static List<FamilyItem> ScanBuiltInLibraries(ISet<string> favorites)
    {
        AppPaths.EnsureCreated();
        var result = new List<FamilyItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> archives = Directory.EnumerateFiles(AppPaths.BuiltInLibraryFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedLibraryArchive)
            .OrderByDescending(File.GetLastWriteTimeUtc);
        foreach (string archivePath in archives)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                ZipArchiveEntry? manifestEntry = archive.GetEntry(ManifestEntryName)
                    ?? archive.GetEntry(LegacyManifestEntryName);
                if (manifestEntry is null) continue;
                FamilyPackageManifest? manifest;
                using (Stream stream = manifestEntry.Open())
                    manifest = JsonSerializer.Deserialize<FamilyPackageManifest>(stream, JsonOptions);
                if (manifest is null || manifest.FormatVersion != 1) continue;

                string archiveKey = StableKey(Path.GetFullPath(archivePath));
                DateTime archiveModified = File.GetLastWriteTimeUtc(archivePath);
                int index = 0;
                foreach (FamilyPackageEntry entry in manifest.Families)
                {
                    index++;
                    if (string.IsNullOrWhiteSpace(entry.FamilyName) || !seenNames.Add(entry.FamilyName)) continue;
                    string fileName = Path.GetFileName(entry.FamilyArchivePath);
                    if (!fileName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) continue;
                    string extractedPath = Path.Combine(
                        AppPaths.ExtractedFamilyFolder,
                        archiveKey,
                        SanitizeName(entry.Category),
                        fileName);
                    string? previewPath = ExtractBuiltInPreview(
                        archive,
                        entry.PreviewArchivePath,
                        archiveKey,
                        index,
                        archiveModified);
                    result.Add(new FamilyItem
                    {
                        FamilyName = entry.FamilyName,
                        Path = extractedPath,
                        Category = string.IsNullOrWhiteSpace(entry.Category) ? "Unclassified" : entry.Category,
                        Group = FamilyScannerService.NormalizeLibraryGroup(entry.Group, entry.Category),
                        Length = entry.Length,
                        ModifiedDate = entry.ModifiedUtc.ToLocalTime(),
                        HasExactCategory = entry.CategoryIsExact,
                        Favorite = favorites.Contains(extractedPath),
                        // Embedded previews from older archives may contain the former
                        // 3D mesh rendering for Detail Items and tags. Let those families
                        // generate the new centered Ref. Level preview on demand.
                        PreviewImage = RevitOperations.RequiresTwoDimensionalPreview(entry.Category)
                            ? null
                            : previewPath,
                        IsBuiltIn = true,
                        IsToolLibrary = true,
                        ArchivePath = archivePath,
                        ArchiveEntryPath = entry.FamilyArchivePath,
                        ArchivePreviewEntryPath = entry.PreviewArchivePath
                    });
                }
            }
            catch
            {
                // One damaged library must not prevent other libraries from opening.
            }
        }
        return result;
    }

    public static bool IsSupportedLibraryArchive(string path)
    {
        return path.EndsWith(".familymeplib", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".lnlibrary", StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureMaterialized(FamilyItem family)
    {
        if (!family.IsBuiltIn) return family.Path;
        if (string.IsNullOrWhiteSpace(family.ArchivePath) || string.IsNullOrWhiteSpace(family.ArchiveEntryPath))
            throw new InvalidDataException($"Built-in family '{family.FamilyName}' has no archive source.");
        if (!File.Exists(family.ArchivePath))
            throw new FileNotFoundException("The built-in library archive was not found.", family.ArchivePath);
        if (File.Exists(family.Path))
        {
            var existing = new FileInfo(family.Path);
            if (existing.Length == family.Length && existing.LastWriteTimeUtc >= family.ModifiedDate.ToUniversalTime()) return family.Path;
        }

        string fullTarget = Path.GetFullPath(family.Path);
        string cacheRoot = Path.GetFullPath(AppPaths.ExtractedFamilyFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The extracted family path is outside the Tool Library cache.");

        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
        string temporary = fullTarget + ".tmp";
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(family.ArchivePath);
            ZipArchiveEntry source = archive.GetEntry(family.ArchiveEntryPath)
                ?? throw new InvalidDataException($"Missing family entry: {family.ArchiveEntryPath}");
            using (Stream input = source.Open())
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            PortableFramework.MoveOverwrite(temporary, fullTarget);
            File.SetLastWriteTimeUtc(fullTarget, family.ModifiedDate.ToUniversalTime());
            return fullTarget;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    public static ToolLibrarySaveResult SaveToUserLibrary(
        IEnumerable<FamilyItem> families,
        string previewColor,
        IProgress<(int Current, int Total)>? progress = null)
    {
        AppPaths.EnsureCreated();
        List<FamilyItem> sourceFamilies = families.ToList();
        HashSet<string> knownNames = Directory.EnumerateFiles(AppPaths.UserLibraryFolder, "*.rfa", SearchOption.AllDirectories)
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = new List<ResolvedFamilyCategory>();
        var savedPaths = new List<string>();
        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        int processed = 0;
        foreach (FamilyItem family in sourceFamilies)
        {
            if (!knownNames.Add(family.FamilyName))
            {
                skipped++;
                progress?.Report((++processed, sourceFamilies.Count));
                continue;
            }
            string source = EnsureMaterialized(family);
            if (!File.Exists(source))
            {
                skipped++;
                progress?.Report((++processed, sourceFamilies.Count));
                continue;
            }
            string folder = Path.Combine(AppPaths.UserLibraryFolder, SanitizeName(family.Category));
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, Path.GetFileName(source));
            File.Copy(source, destination, false);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            savedPaths.Add(destination);
            pathMap[family.Path] = destination;
            if (family.HasExactCategory) categories.Add(new ResolvedFamilyCategory(destination, family.Category));

            string? previewSource = family.PreviewImage;
            if (family.HasExactCategory && !string.IsNullOrWhiteSpace(previewSource) && File.Exists(previewSource))
            {
                string previewDestination = RevitOperations.GetPreviewCachePath(
                    destination,
                    previewColor,
                    family.Category);
                File.Copy(previewSource, previewDestination, true);
                File.SetLastWriteTimeUtc(previewDestination, DateTime.UtcNow);
            }
            progress?.Report((++processed, sourceFamilies.Count));
        }
        if (categories.Count > 0)
        {
            FamilyCategoryCacheService.Update(categories);
            FamilyCategoryCacheService.Save();
        }
        return new ToolLibrarySaveResult(savedPaths.Count, skipped, savedPaths, pathMap);
    }

    public static bool IsUserLibraryPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(AppPaths.UserLibraryFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractBuiltInPreview(
        ZipArchive archive,
        string? entryPath,
        string archiveKey,
        int index,
        DateTime archiveModified)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        ZipArchiveEntry? source = archive.GetEntry(entryPath);
        if (source is null) return null;
        string destination = Path.Combine(AppPaths.BuiltInPreviewFolder, $"{archiveKey}_{index:D6}.png");
        if (File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            if (existing.Length == source.Length && existing.LastWriteTimeUtc >= archiveModified) return destination;
        }
        string temporary = destination + ".tmp";
        try
        {
            using (Stream input = source.Open())
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            PortableFramework.MoveOverwrite(temporary, destination);
            File.SetLastWriteTimeUtc(destination, archiveModified);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return null;
        }
    }

    private static string StableKey(string value)
    {
        return PortableFramework.StableHash16(value.ToUpperInvariant());
    }

    private static string SanitizeName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "Unclassified" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "Unclassified" : name;
    }
}
