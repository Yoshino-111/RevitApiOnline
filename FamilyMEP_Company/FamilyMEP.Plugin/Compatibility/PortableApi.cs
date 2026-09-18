using Autodesk.Revit.DB;

namespace FamilyMEP.Plugin.Compatibility;

internal static class PortableApi
{
    public static FilteredElementCollector LinkedCollector(Document document, ElementId viewId, RevitLinkInstance link)
    {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        // Older APIs cannot filter linked elements by host-view visibility.
        // Callers still clip projected bounds to their active layout frame.
        return new FilteredElementCollector(link.GetLinkDocument());
#else
        return new FilteredElementCollector(document, viewId, link.Id);
#endif
    }
    public static ImageTypeOptions ImageOptions(string path)
    {
#if REVIT2020
        return new ImageTypeOptions(path, false);
#else
        return new ImageTypeOptions(path, false, ImageTypeSource.Import);
#endif
    }
    public static long CompatValue(this ElementId id)
    {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return id.IntegerValue;
#else
        return id.Value;
#endif
    }

    public static ElementId ElementId(long value)
    {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return new ElementId(checked((int)value));
#else
        return new ElementId(value);
#endif
    }
    public static ElementId ElementId(BuiltInCategory value) => new ElementId(value);
    public static ElementId ElementId(BuiltInParameter value) => new ElementId(value);
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static byte[] HashData(byte[] value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(value);
    }
    public static string ToHexString(byte[] value) => BitConverter.ToString(value).Replace("-", "");
    public static void Fill<T>(T[] array, T value) { for (int i=0; i<array.Length; i++) array[i]=value; }
    public static void AddArgument(this System.Diagnostics.ProcessStartInfo info, string value)
    {
        // Windows command-line quoting, including trailing backslashes.
        string quoted = System.Text.RegularExpressions.Regex.Replace(value, @"(\\*)""", "$1$1\\\"");
        quoted = System.Text.RegularExpressions.Regex.Replace(quoted, @"(\\+)$", "$1$1");
        info.Arguments += (info.Arguments.Length == 0 ? "" : " ") + "\"" + quoted + "\"";
    }
}
