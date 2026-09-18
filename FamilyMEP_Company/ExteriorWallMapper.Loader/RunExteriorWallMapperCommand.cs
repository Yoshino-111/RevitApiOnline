using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ExteriorWallMapper.Loader;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunExteriorWallMapperCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return RevitHotLoader2025.HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.ExteriorWallMapperPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("Exterior Wall Mapper", exception.ToString());
            return Result.Failed;
        }
    }
}
