#if REVIT2020 || REVIT2021
using Autodesk.Revit.DB;
namespace FamilyMEP.Plugin.Compatibility;
internal static class LegacyTags
{
    // Before multi-reference tags (2022), each tag has a single tagged element.
    public static IList<Reference> GetTaggedReferences(this IndependentTag tag)
    {
        var result = new List<Reference>();
        if (tag.IsOrphaned) return result;
        LinkElementId id = tag.TaggedElementId;
        if (id.LinkInstanceId != ElementId.InvalidElementId)
        {
            var link = tag.Document.GetElement(id.LinkInstanceId) as RevitLinkInstance;
            Element target = link?.GetLinkDocument()?.GetElement(id.LinkedElementId);
            if (target != null) result.Add(new Reference(target).CreateLinkReference(link));
        }
        else
        {
            Element target = tag.Document.GetElement(id.HostElementId);
            if (target != null) result.Add(new Reference(target));
        }
        return result;
    }
    public static XYZ GetLeaderElbow(this IndependentTag tag, Reference reference) => tag.LeaderElbow;
    public static bool HasLeaderElbow(this IndependentTag tag, Reference reference) => tag.HasElbow;
    public static XYZ GetLeaderEnd(this IndependentTag tag, Reference reference) => tag.LeaderEnd;
    public static void SetLeaderElbow(this IndependentTag tag, Reference reference, XYZ point) => tag.LeaderElbow = point;
    public static void SetLeaderEnd(this IndependentTag tag, Reference reference, XYZ point) => tag.LeaderEnd = point;
}
#endif
