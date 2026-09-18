#if REVIT2020
global using UnitTypeId = FamilyMEP.Plugin.Compatibility.LegacyUnitIds;
global using SpecTypeId = FamilyMEP.Plugin.Compatibility.LegacySpecIds;
using Autodesk.Revit.DB;
namespace FamilyMEP.Plugin.Compatibility;
internal static class LegacyUnitIds
{
    public static DisplayUnitType Millimeters => DisplayUnitType.DUT_MILLIMETERS;
    public static DisplayUnitType Centimeters => DisplayUnitType.DUT_CENTIMETERS;
    public static DisplayUnitType Meters => DisplayUnitType.DUT_METERS;
    public static DisplayUnitType Inches => DisplayUnitType.DUT_DECIMAL_INCHES;
    public static DisplayUnitType Feet => DisplayUnitType.DUT_DECIMAL_FEET;
    public static DisplayUnitType SquareMeters => DisplayUnitType.DUT_SQUARE_METERS;
}
internal static class LegacySpecIds
{
    public static UnitType Length => UnitType.UT_Length;
    public static UnitType Angle => UnitType.UT_Angle;
    public static UnitType PipeSize => UnitType.UT_PipeSize;
}
internal static class LegacyDefinition
{
    public static UnitType GetDataType(this Definition definition) => definition.UnitType;
}
#endif
