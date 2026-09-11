#if NET48
using System.Security.Cryptography;
using System.Text;

namespace System
{
    internal static class StringCompatibilityExtensions
    {
        public static bool Contains(this string source, string value, StringComparison comparisonType) =>
            source?.IndexOf(value, comparisonType) >= 0;
    }
}

namespace FamilyMEP.Plugin.Compatibility
{
    internal static class FrameworkCompat
    {
        public static int Clamp(int value, int minimum, int maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;

        public static double Clamp(double value, double minimum, double maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
    }
}
#endif
