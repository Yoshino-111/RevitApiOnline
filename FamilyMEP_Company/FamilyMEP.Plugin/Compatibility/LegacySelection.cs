#if REVIT2020 || REVIT2021 || REVIT2022
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
namespace FamilyMEP.Plugin.Compatibility;
internal static class LegacySelection
{
    // Legacy selection cannot select a linked sub-element; select its link instance.
    public static void SetReferences(this Selection selection, IList<Reference> references) =>
        selection.SetElementIds(references.Select(reference => reference.ElementId).Distinct().ToList());
}
#endif
