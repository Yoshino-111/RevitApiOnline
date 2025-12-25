using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitApiOnline.SpaceMapping
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SpaceMappingAppBinding : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;

            try
            {
                // Giống ScheduleExportImportBinding: gọi form static
                SpaceMappingAppShow.ShowForm(uiApp);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Space Mapping", ex.ToString());
                return Result.Failed;
            }
        }
    }
}
