using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FamilyMEP.Entry;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class DrainConnectionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            Application.ShuffleDrainConnectionIcon();
#if REVIT2020
            return LegacyDrainHotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements);
#else
            return Application.DrainConnectionPlugin.Execute(commandData, ref message, elements);
#endif
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP — Drain Connection", exception.Message);
            return Result.Failed;
        }
    }
}
