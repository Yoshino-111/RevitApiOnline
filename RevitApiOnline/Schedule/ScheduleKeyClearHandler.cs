using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Schedule
{
    internal class ScheduleKeyClearHandler : IExternalEventHandler
    {
        public ScheduleExportImportDataContext DataContext { get; set; }

        public void Execute(UIApplication app)
        {
            var vm = DataContext;
            if (vm == null) return;

            try
            {
                vm.StatusText = "Clearing keys...";
                vm.Progress = 0;

                UIDocument uiDoc = app.ActiveUIDocument;
                if (uiDoc == null)
                {
                    TaskDialog.Show("Schedule CSV", "No active document.");
                    vm.StatusText = "Clear failed: no active document.";
                    return;
                }

                Document doc = uiDoc.Document;

                var scheduleVm = vm.SelectedSchedule;
                if (scheduleVm == null)
                {
                    TaskDialog.Show("Schedule CSV", "Please select a schedule.");
                    vm.StatusText = "Clear failed: no schedule selected.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(vm.SelectedKeyParameter))
                {
                    TaskDialog.Show("Schedule CSV", "Please select a parameter for keys.");
                    vm.StatusText = "Clear failed: no key parameter selected.";
                    return;
                }

                var vs = doc.GetElement(scheduleVm.Id) as ViewSchedule;
                if (vs == null)
                {
                    TaskDialog.Show("Schedule CSV", "Selected schedule cannot be found.");
                    vm.StatusText = "Clear failed: schedule not found.";
                    return;
                }

                ClearKeysForSchedule(doc, vs, vm.SelectedKeyParameter, vm);

                vm.StatusText = "Clear keys: completed.";
                vm.Progress = 100;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule CSV – Clear keys error", ex.ToString());
                if (DataContext != null)
                {
                    DataContext.StatusText = "Clear failed: " + ex.Message;
                    DataContext.Progress = 0;
                }
            }
        }

        public string GetName() => "Schedule Key Clear Handler";

        private static void ClearKeysForSchedule(
            Document doc,
            ViewSchedule vs,
            string paramName,
            ScheduleExportImportDataContext vm)
        {
            var collector = new FilteredElementCollector(doc, vs.Id)
                .WhereElementIsNotElementType()
                .ToElements();

            int total = collector.Count;
            if (total == 0) return;

            using (var tx = new Transaction(doc, "Clear keys for schedule"))
            {
                tx.Start();

                int done = 0;

                foreach (var element in collector)
                {
                    Element owner = element;
                    Parameter p = owner.LookupParameter(paramName);

                    if (p == null)
                    {
                        Element typeElem = doc.GetElement(element.GetTypeId());
                        if (typeElem != null)
                        {
                            p = typeElem.LookupParameter(paramName);
                            owner = typeElem;
                        }
                    }

                    if (p == null || p.IsReadOnly)
                    {
                        done++;
                        continue;
                    }

                    switch (p.StorageType)
                    {
                        case StorageType.Integer:
                            p.Set(0);
                            break;
                        case StorageType.String:
                            p.Set(string.Empty);
                            break;
                        default:
                            break;
                    }

                    done++;
                    if (vm != null && total > 0)
                        vm.Progress = (int)(done * 100.0 / total);
                }

                tx.Commit();
            }
        }
    }
}
