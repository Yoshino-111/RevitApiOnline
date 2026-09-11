using System.Text.Json;

namespace FamilyMEP.Plugin.SmartTag;

// Geometry-only local diagnostic: no document path, parameter values or tag text.
// The same DTO can be replayed by the smoke-test executable without Revit.
internal sealed record SmartTagNearHostReplay(
    string Build,
    DateTime CapturedUtc,
    LayoutTagInput[] Inputs,
    LayoutObstacle[] Obstacles,
    LayoutRect Frame,
    SmartTagLayoutSettings Settings,
    TagLayoutPlacement[] Reservations,
    SmartTagLayoutResult Standard,
    SmartTagLayoutResult Result)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SmartTagNearHostReplay Capture(IReadOnlyList<LayoutTagInput> inputs,
        IReadOnlyList<LayoutObstacle> obstacles, LayoutRect frame, SmartTagLayoutSettings settings,
        IReadOnlyList<TagLayoutPlacement> reservations, SmartTagLayoutResult standard, SmartTagLayoutResult result)
    {
        TagLayoutPlacement Sanitize(TagLayoutPlacement p) => p with { Label = string.Empty };
        return new(typeof(SmartTagNearHostReplay).Assembly.ManifestModule.ModuleVersionId.ToString(), DateTime.UtcNow,
            inputs.Select(p => p with { Label = string.Empty }).ToArray(), obstacles.ToArray(), frame, settings,
            reservations.Select(Sanitize).ToArray(),
            standard with { Placements = standard.Placements.Select(Sanitize).ToArray() },
            result with { Placements = result.Placements.Select(Sanitize).ToArray() });
    }
}
