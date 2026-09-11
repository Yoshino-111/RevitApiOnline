namespace FamilyMEP.Plugin.SmartTag;

internal static class SmartTagDuctChangeRules
{
    internal const double MinimumSizeChangeLengthFeet = 500.0 / 304.8;

    internal static bool ShouldTag(
        bool sizeChanged,
        bool elevationChanged,
        double ductLengthFeet) =>
        (sizeChanged || elevationChanged) &&
        ductLengthFeet >= MinimumSizeChangeLengthFeet;
}
