using System.IO;
using System.Text.RegularExpressions;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class FamilyScannerService
{
    private static readonly (string Code, string Group, string Label)[] Categories =
    [
        ("AN", "Annotation", "Annotation"),
        ("PR", "Support", "Profiles"),
        ("SUP", "Support", "Support"),
        ("DA", "MEP Model", "DA - Duct Accessories"),
        ("DF", "MEP Model", "DF - Duct Fittings"),
        ("AT", "MEP Model", "AT - Air Terminals"),
        ("FD", "MEP Model", "FD - Fire Dampers"),
        ("ME", "MEP Model", "ME - Mechanical Equipment"),
        ("PF", "MEP Model", "PF - Pipe Fittings"),
        ("PA", "MEP Model", "PA - Pipe Accessories"),
        ("PV", "MEP Model", "PV - Valves"),
        ("PL", "MEP Model", "PL - Plumbing Fixtures"),
        ("SP", "MEP Model", "SP - Sprinklers"),
        ("EL", "MEP Model", "EL - Electrical"),
        ("DI", "MEP Model", "DI - Devices and Instruments"),
        ("CF", "MEP Model", "CF - Cable/Conduit Fittings")
    ];

    public static List<FamilyItem> Scan(IEnumerable<string> roots, ISet<string> favorites)
    {
        var result = new List<FamilyItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string path in files)
            {
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    string stem = Path.GetFileNameWithoutExtension(fullPath);
                    if (IsBackupFamilyName(stem))
                    {
                        continue;
                    }
                    if (!seen.Add(fullPath))
                    {
                        continue;
                    }

                    FileInfo info = new(fullPath);
                    bool exactCategory = FamilyCategoryCacheService.TryGet(info, out string revitCategory);
                    (string group, string category) = exactCategory
                        ? ClassifyRevitCategory(revitCategory)
                        : Categorize(info);
                    result.Add(new FamilyItem
                    {
                        FamilyName = Path.GetFileNameWithoutExtension(fullPath),
                        Path = fullPath,
                        Category = category,
                        Group = group,
                        Length = info.Length,
                        ModifiedDate = info.LastWriteTime,
                        HasExactCategory = exactCategory,
                        Favorite = favorites.Contains(fullPath)
                    });
                }
                catch
                {
                    // Skip inaccessible or transient files and continue scanning.
                }
            }
        }

        return result.OrderBy(item => item.FamilyName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsBackupFamilyName(string stem) =>
        Regex.IsMatch(stem, @"(?:\.|_|-)\d{3,}$", RegexOptions.CultureInvariant)
        || stem.EndsWith("_BACKUP", StringComparison.OrdinalIgnoreCase)
        || stem.EndsWith(".BACKUP", StringComparison.OrdinalIgnoreCase);

    private static (string Group, string Category) Categorize(FileInfo file)
    {
        string searchable = $"{file.Name} {file.DirectoryName}".ToUpperInvariant();
        string[] fileTokens = Regex.Split(
            Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant(),
            "[^A-Z0-9]+");
        foreach ((string code, string group, string label) in Categories)
        {
            if (fileTokens.Contains(code, StringComparer.OrdinalIgnoreCase)
                || file.Name.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase)
                || searchable.Contains($"\\{code} -", StringComparison.OrdinalIgnoreCase)
                || searchable.Contains($"\\{code}_", StringComparison.OrdinalIgnoreCase))
            {
                return (group, label);
            }
        }

        if (IsAnnotationName(searchable)) return ("Annotation", "Unclassified Annotation");
        if (IsSupportName(searchable)) return ("Support", "Unclassified Support");
        if (searchable.Contains("DUCT")) return ("MEP Model", "Duct - Other");
        if (searchable.Contains("PIPE") || searchable.Contains("PLUMB")) return ("MEP Model", "Pipe - Other");
        if (searchable.Contains("ELECT")) return ("MEP Model", "Electrical - Other");
        if (searchable.Contains("SUPPORT") || searchable.Contains("HANGER")) return ("Support", "Support - Other");
        return ("MEP Model", "Unclassified");
    }

    public static (string Group, string Category) ClassifyRevitCategory(string category)
    {
        string name = string.IsNullOrWhiteSpace(category) ? "Unclassified" : category.Trim();
        string upper = name.ToUpperInvariant();
        string group = IsAnnotationName(upper)
            ? "Annotation"
            : IsSupportName(upper)
                ? "Support"
                : "MEP Model";
        return (group, name);
    }

    public static string NormalizeLibraryGroup(string? storedGroup, string category)
    {
        if (storedGroup is "MEP Model" or "Annotation" or "Support") return storedGroup;
        return ClassifyRevitCategory(category).Group;
    }

    private static bool IsAnnotationName(string value)
    {
        string[] terms =
        [
            "TAG", "ANNOTATION", "DETAIL ITEM", "DETAIL COMPONENT", "SYMBOL",
            "VIEW TITLE", "SECTION HEAD", "ELEVATION MARK", "CALLOUT HEAD", "KEYNOTE"
        ];
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportName(string value)
    {
        string[] terms =
        [
            "PROFILE", "TITLE BLOCK", "SUPPORT", "HANGER", "MASS",
            "CURTAIN PANEL", "PATTERN BASED", "ADAPTIVE"
        ];
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
