using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Schedule
{
    internal class ScheduleKeyGenerateHandler : IExternalEventHandler
    {
        public ScheduleExportImportDataContext DataContext { get; set; }

        public void Execute(UIApplication app)
        {
            var vm = DataContext;
            if (vm == null) return;

            try
            {
                vm.StatusText = "Generating keys...";
                vm.Progress = 0;

                UIDocument uiDoc = app.ActiveUIDocument;
                if (uiDoc == null)
                {
                    TaskDialog.Show("Schedule CSV", "No active document.");
                    vm.StatusText = "Generate failed: no active document.";
                    return;
                }

                Document doc = uiDoc.Document;

                var scheduleVm = vm.SelectedSchedule;
                if (scheduleVm == null)
                {
                    TaskDialog.Show("Schedule CSV", "Please select a schedule.");
                    vm.StatusText = "Generate failed: no schedule selected.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(vm.SelectedKeyParameter))
                {
                    TaskDialog.Show("Schedule CSV", "Please select a parameter for keys.");
                    vm.StatusText = "Generate failed: no key parameter selected.";
                    return;
                }

                var vs = doc.GetElement(scheduleVm.Id) as ViewSchedule;
                if (vs == null)
                {
                    TaskDialog.Show("Schedule CSV", "Selected schedule cannot be found.");
                    vm.StatusText = "Generate failed: schedule not found.";
                    return;
                }

                GenerateKeysForSchedule(doc, vs, vm.SelectedKeyParameter, vm);

                vm.StatusText = "Generate keys: completed.";
                vm.Progress = 100;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule CSV – Generate keys error", ex.ToString());
                if (DataContext != null)
                {
                    DataContext.StatusText = "Generate failed: " + ex.Message;
                    DataContext.Progress = 0;
                }
            }
        }

        public string GetName() => "Schedule Key Generate Handler";

        private static void GenerateKeysForSchedule(
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

            var rnd = new Random();
            var usedInts = new HashSet<int>();
            var usedStrings = new HashSet<string>();

            int NextInt()
            {
                int value;
                do
                {
                    value = rnd.Next(1000000, int.MaxValue); // >=7 digits
                } while (!usedInts.Add(value));
                return value;
            }

            string NextString()
            {
                int val = NextInt();
                string s = val.ToString(CultureInfo.InvariantCulture);
                usedStrings.Add(s);
                return s;
            }

            using (var tx = new Transaction(doc, "Generate random keys for schedule"))
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
                            p.Set(NextInt());
                            break;
                        case StorageType.String:
                            p.Set(NextString());
                            break;
                        default:
                            // Double / ElementId không dùng làm key
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
