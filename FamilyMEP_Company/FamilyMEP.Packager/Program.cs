using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: FamilyMEP.Packager <project-root> <output.familymeplib>");
    return 2;
}

string projectRoot = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
string dataRoot = Path.Combine(projectRoot, "familymep-data");
ManagerState state = ReadJson<ManagerState>(Path.Combine(dataRoot, "state.json")) ?? new ManagerState();
Dictionary<string, CategoryCacheEntry> categoryCache =
    ReadJson<Dictionary<string, CategoryCacheEntry>>(Path.Combine(dataRoot, "family-categories.json"))
    ?? new(StringComparer.OrdinalIgnoreCase);

var byName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
foreach (string root in state.Roots.Where(Directory.Exists))
{
    IEnumerable<string> files;
    try { files = Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories); }
    catch { continue; }
    foreach (string path in files)
    {
        try
        {
            var file = new FileInfo(path);
            string name = Path.GetFileNameWithoutExtension(file.Name);
            if (IsBackupName(name) || byName.ContainsKey(name)) continue;
            byName[name] = file;
        }
        catch { }
    }
}

List<PackageSource> sources = byName.Values
    .Select(file => CreateSource(file, categoryCache, dataRoot, state.PreviewColor))
    .OrderBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
    .ThenBy(item => item.FamilyName, StringComparer.CurrentCultureIgnoreCase)
    .ToList();

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
string temporary = outputPath + ".tmp";
if (File.Exists(temporary)) File.Delete(temporary);
var manifest = new PackageManifest
{
    PackageName = "FamilyMEP Core Library",
    PreviewColor = state.PreviewColor,
    Families = []
};

try
{
    using FileStream file = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
    using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
    for (int index = 0; index < sources.Count; index++)
    {
        PackageSource source = sources[index];
        string familyEntryPath = $"families/{Sanitize(source.Category)}/{index + 1:D6}_{source.File.Name}";
        AddFile(archive, source.File.FullName, familyEntryPath, CompressionLevel.Fastest);
        string? previewEntryPath = null;
        if (source.PreviewPath is not null)
        {
            previewEntryPath = $"previews/{index + 1:D6}.png";
            AddFile(archive, source.PreviewPath, previewEntryPath, CompressionLevel.NoCompression);
        }
        manifest.Families.Add(new PackageEntry
        {
            FamilyName = source.FamilyName,
            Category = source.Category,
            Group = source.Group,
            CategoryIsExact = source.CategoryIsExact,
            FamilyArchivePath = familyEntryPath,
            PreviewArchivePath = previewEntryPath,
            ModifiedUtc = source.File.LastWriteTimeUtc,
            Length = source.File.Length
        });
        if ((index + 1) % 50 == 0 || index + 1 == sources.Count)
            Console.WriteLine($"PACK {index + 1:N0}/{sources.Count:N0}");
    }
    ZipArchiveEntry manifestEntry = archive.CreateEntry("familymep-package.json", CompressionLevel.Fastest);
    using Stream manifestStream = manifestEntry.Open();
    JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
    archive.Dispose();
    file.Dispose();
    File.Move(temporary, outputPath, true);
}
catch
{
    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
    throw;
}

var output = new FileInfo(outputPath);
Console.WriteLine($"DONE families={manifest.Families.Count:N0} previews={manifest.Families.Count(item => item.PreviewArchivePath is not null):N0} sizeGB={output.Length / 1024d / 1024d / 1024d:N3}");
return 0;

static T? ReadJson<T>(string path)
{
    try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
    catch { return default; }
}

static PackageSource CreateSource(
    FileInfo file,
    IReadOnlyDictionary<string, CategoryCacheEntry> cache,
    string dataRoot,
    string previewColor)
{
    bool exact = cache.TryGetValue(file.FullName.ToUpperInvariant(), out CategoryCacheEntry? cached)
        && cached.Length == file.Length
        && cached.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks
        && !string.IsNullOrWhiteSpace(cached.Category);
    string category = exact ? cached!.Category.Trim() : FallbackCategory(file);
    string group = ClassifyGroup(category);
    string previewPath = PreviewPath(dataRoot, file.FullName, previewColor);
    if (!File.Exists(previewPath) || File.GetLastWriteTimeUtc(previewPath) < file.LastWriteTimeUtc) previewPath = string.Empty;
    return new PackageSource(
        Path.GetFileNameWithoutExtension(file.Name), file, category, group, exact, previewPath.Length == 0 ? null : previewPath);
}

static string ClassifyGroup(string category)
{
    string upper = category.ToUpperInvariant();
    string[] annotation = ["TAG", "ANNOTATION", "DETAIL ITEM", "DETAIL COMPONENT", "SYMBOL", "VIEW TITLE", "SECTION HEAD", "ELEVATION MARK", "CALLOUT HEAD", "KEYNOTE"];
    if (annotation.Any(upper.Contains)) return "Annotation";
    string[] support = ["PROFILE", "TITLE BLOCK", "SUPPORT", "HANGER", "MASS", "CURTAIN PANEL", "PATTERN BASED", "ADAPTIVE"];
    return support.Any(upper.Contains) ? "Support" : "MEP Model";
}

static string FallbackCategory(FileInfo file)
{
    string searchable = $"{file.Name} {file.DirectoryName}".ToUpperInvariant();
    (string Code, string Label)[] known =
    [
        ("AN", "Unclassified Annotation"), ("PR", "Profiles"), ("SUP", "Support"),
        ("DA", "Duct Accessories"), ("DF", "Duct Fittings"), ("AT", "Air Terminals"),
        ("FD", "Fire Dampers"), ("ME", "Mechanical Equipment"), ("PF", "Pipe Fittings"),
        ("PA", "Pipe Accessories"), ("PV", "Valves"), ("PL", "Plumbing Fixtures"),
        ("SP", "Sprinklers"), ("EL", "Electrical"), ("DI", "Devices and Instruments"),
        ("CF", "Cable/Conduit Fittings")
    ];
    string[] tokens = Regex.Split(Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant(), "[^A-Z0-9]+");
    foreach ((string code, string label) in known)
    {
        if (tokens.Contains(code, StringComparer.OrdinalIgnoreCase)
            || searchable.Contains($"\\{code} -", StringComparison.OrdinalIgnoreCase)
            || searchable.Contains($"\\{code}_", StringComparison.OrdinalIgnoreCase)) return label;
    }
    return "Unclassified";
}

static string PreviewPath(string dataRoot, string familyPath, string previewColor)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(familyPath).ToUpperInvariant()));
    string key = Convert.ToHexString(hash)[..16];
    string color = NormalizeColor(previewColor).TrimStart('#');
    string name = string.Concat(Path.GetFileNameWithoutExtension(familyPath)
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    return Path.Combine(dataRoot, "previews", $"{name}_{key}_mesh_v2_{color}.png");
}

static string NormalizeColor(string? value)
{
    string color = string.IsNullOrWhiteSpace(value) ? "#4B5563" : value.Trim().ToUpperInvariant();
    if (!color.StartsWith('#')) color = "#" + color;
    return color.Length == 7 && color.Skip(1).All(Uri.IsHexDigit) ? color : "#4B5563";
}

static bool IsBackupName(string stem) =>
    Regex.IsMatch(stem, @"(?:\.|_|-)\d{3,}$", RegexOptions.CultureInvariant)
    || stem.EndsWith("_BACKUP", StringComparison.OrdinalIgnoreCase)
    || stem.EndsWith(".BACKUP", StringComparison.OrdinalIgnoreCase);

static string Sanitize(string value)
{
    string result = string.IsNullOrWhiteSpace(value) ? "Unclassified" : value.Trim();
    foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
    return result;
}

static void AddFile(ZipArchive archive, string sourcePath, string archivePath, CompressionLevel compression)
{
    ZipArchiveEntry entry = archive.CreateEntry(archivePath.Replace('\\', '/'), compression);
    entry.LastWriteTime = File.GetLastWriteTime(sourcePath);
    using Stream input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024);
    using Stream output = entry.Open();
    input.CopyTo(output, 1024 * 1024);
}

sealed class ManagerState
{
    public List<string> Roots { get; set; } = [];
    public string PreviewColor { get; set; } = "#4B5563";
}

sealed class CategoryCacheEntry
{
    public long Length { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Category { get; set; } = string.Empty;
}

sealed class PackageManifest
{
    public int FormatVersion { get; set; } = 1;
    public string PackageName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string PreviewColor { get; set; } = "#4B5563";
    public List<PackageEntry> Families { get; set; } = [];
}

sealed class PackageEntry
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

sealed record PackageSource(
    string FamilyName,
    FileInfo File,
    string Category,
    string Group,
    bool CategoryIsExact,
    string? PreviewPath);
