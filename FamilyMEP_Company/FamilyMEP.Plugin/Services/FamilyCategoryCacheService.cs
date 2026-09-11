using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal sealed class FamilyCategoryCacheEntry
{
    public long Length { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Category { get; set; } = string.Empty;
}

internal static class FamilyCategoryCacheService
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static Dictionary<string, FamilyCategoryCacheEntry>? _entries;

    public static bool TryGet(FileInfo file, out string category)
    {
        lock (SyncRoot)
        {
            EnsureLoaded();
            string key = Key(file.FullName);
            if (_entries!.TryGetValue(key, out FamilyCategoryCacheEntry? entry)
                && entry.Length == file.Length
                && entry.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks
                && !string.IsNullOrWhiteSpace(entry.Category))
            {
                category = entry.Category;
                return true;
            }
            category = string.Empty;
            return false;
        }
    }

    public static void Update(IEnumerable<ResolvedFamilyCategory> categories)
    {
        lock (SyncRoot)
        {
            EnsureLoaded();
            foreach (ResolvedFamilyCategory resolved in categories)
            {
                if (!File.Exists(resolved.Path) || string.IsNullOrWhiteSpace(resolved.Category)) continue;
                var file = new FileInfo(resolved.Path);
                _entries![Key(file.FullName)] = new FamilyCategoryCacheEntry
                {
                    Length = file.Length,
                    ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks,
                    Category = resolved.Category.Trim()
                };
            }
        }
    }

    public static void Save()
    {
        lock (SyncRoot)
        {
            EnsureLoaded();
            AppPaths.EnsureCreated();
            string temporary = AppPaths.CategoryCacheFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, Options));
            PortableFramework.MoveOverwrite(temporary, AppPaths.CategoryCacheFile);
        }
    }

    private static void EnsureLoaded()
    {
        if (_entries is not null) return;
        AppPaths.EnsureCreated();
        try
        {
            _entries = File.Exists(AppPaths.CategoryCacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, FamilyCategoryCacheEntry>>(
                    File.ReadAllText(AppPaths.CategoryCacheFile), Options)
                : null;
        }
        catch
        {
            _entries = null;
        }
        _entries ??= new Dictionary<string, FamilyCategoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
