using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FamilyMEP.DrainConnection.Entry;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class DrainConnectionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            return Application.Plugin.Execute(commandData, ref message, elements);
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("Drain Connection", exception.Message);
            return Result.Failed;
        }
    }
}
