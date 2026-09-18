namespace FamilyMEP.Plugin.Compatibility;
internal static class PortableMath
{
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
