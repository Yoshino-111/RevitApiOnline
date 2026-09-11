using Autodesk.Revit.DB;

namespace FamilyMEP.Plugin.DrainConnection;

internal enum DrainSide
{
    Left,
    Right
}

internal sealed record DrainSettings(
    double SlopePercent,
    ElementId BranchPipeTypeId,
    double YRollAngleDegrees = 0.0,
    XYZ? TargetPoint = null);

internal sealed record DrainRoute(
    XYZ DrainOrigin,
    XYZ StubEnd,
    XYZ DiagonalEnd,
    XYZ WyePoint,
    XYZ MainStart,
    XYZ MainEnd,
    double CompactOffset,
    double BranchPlanLength,
    double MainParameter,
    double PlanAngleDegrees,
    double FittingAngleDegrees,
    double SlopePercent,
    DrainSide Side,
    XYZ? NearMainElbow = null,
    int CaseNumber = 1,
    XYZ? MainExtensionElbow = null)
{
    public double StubLength => DrainOrigin.Z - StubEnd.Z;
    public double BranchRise => DiagonalEnd.Z - WyePoint.Z;
}

internal sealed record PipeTypeItem(ElementId Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed record DrainOperationResult(
    bool Success,
    bool Partial,
    string Message,
    DrainRoute? Route = null,
    IReadOnlyList<ElementId>? CreatedIds = null,
    IReadOnlyList<string>? Warnings = null);
