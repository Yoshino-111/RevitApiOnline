using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FamilyMEP.Entry;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SmartTagCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            return Application.SmartTagPlugin.Execute(commandData, ref message, elements);
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Smart Tag", exception.Message);
            return Result.Failed;
        }
    }
}
