#if REVIT2021
using Autodesk.Revit.DB;
namespace FamilyMEP.Plugin.Compatibility;
internal static class Revit2021Definition
{
    public static ForgeTypeId GetDataType(this Definition definition) => definition.GetSpecTypeId();
}
#endif
