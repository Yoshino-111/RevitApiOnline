using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Schedule
{
    public static class ScheduleExportImportAppShow
    {
        public static void ShowForm(UIApplication uiApp)
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Schedule CSV", "No active document.");
                return;
            }

            // Handlers + ExternalEvents
            var exportHandler = new ScheduleExportHandler();
            var importHandler = new ScheduleImportHandler();
            var keyGenerateHandler = new ScheduleKeyGenerateHandler();
            var keyClearHandler = new ScheduleKeyClearHandler();

            ExternalEvent exportEvent = ExternalEvent.Create(exportHandler);
            ExternalEvent importEvent = ExternalEvent.Create(importHandler);
            ExternalEvent keyGenerateEvent = ExternalEvent.Create(keyGenerateHandler);
            ExternalEvent keyClearEvent = ExternalEvent.Create(keyClearHandler);

            // ViewModel
            var dataContext = new ScheduleExportImportDataContext(
                uiDoc,
                exportEvent,
                importEvent,
                keyGenerateEvent,
                keyClearEvent);

            // Gán DataContext cho handlers
            exportHandler.DataContext = dataContext;
            importHandler.DataContext = dataContext;
            keyGenerateHandler.DataContext = dataContext;
            keyClearHandler.DataContext = dataContext;

            // Window
            var window = new ScheduleExportImportWpf
            {
                DataContext = dataContext
            };

            var helper = new WindowInteropHelper(window)
            {
                Owner = uiApp.MainWindowHandle
            };

            // Modeless để ExternalEvent chạy
            window.Show();
        }
    }
}
