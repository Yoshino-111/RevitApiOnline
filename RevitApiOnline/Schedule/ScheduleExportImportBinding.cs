using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Schedule
{
    [Transaction(TransactionMode.Manual)]
    public class ScheduleExportImportBinding : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;

            try
            {
                ScheduleExportImportAppShow.ShowForm(uiApp);
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Luc Nguyen – Schedule CSV – Error", ex.ToString());
                return Result.Failed;
            }
        }
    }
}
