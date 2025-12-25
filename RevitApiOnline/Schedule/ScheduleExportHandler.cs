using System;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace RevitApiOnline.Schedule
{
    internal class ScheduleExportHandler : IExternalEventHandler
    {
        // Tên cột / parameter dùng làm khoá
        private const string ElementIdColumnName = "ElementId";

        public ScheduleExportImportDataContext DataContext { get; set; }

        public void Execute(UIApplication app)
        {
            var vm = DataContext;
            if (vm == null) return;

            try
            {
                vm.StatusText = "Exporting CSV...";
                vm.Progress = 0;

                UIDocument uiDoc = app.ActiveUIDocument;
                if (uiDoc == null)
                {
                    TaskDialog.Show("Schedule CSV", "No active document.");
                    vm.StatusText = "Export failed: no active document.";
                    return;
                }

                Document doc = uiDoc.Document;

                var scheduleVm = vm.SelectedSchedule;
                if (scheduleVm == null)
                {
                    TaskDialog.Show("Schedule CSV", "Please select a schedule.");
                    vm.StatusText = "Export failed: no schedule selected.";
                    return;
                }

                var vs = doc.GetElement(scheduleVm.Id) as ViewSchedule;
                if (vs == null)
                {
                    TaskDialog.Show("Schedule CSV", "Selected schedule cannot be found.");
                    vm.StatusText = "Export failed: schedule not found.";
                    return;
                }

                // Hộp thoại chọn file
                var dlg = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    FileName = MakeSafeFileName(vs.Name) + ".csv",
                    Title = "Export schedule to CSV"
                };

                if (dlg.ShowDialog() != true)
                {
                    vm.StatusText = "Export cancelled.";
                    return;
                }

                string path = dlg.FileName;

                ExportScheduleToCsv(doc, vs, path, vm);

                vm.StatusText = $"Export completed: {Path.GetFileName(path)}";
                vm.Progress = 100;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Luc Nguyen – Schedule CSV – Export error", ex.ToString());
                if (DataContext != null)
                {
                    DataContext.StatusText = "Export failed: " + ex.Message;
                    DataContext.Progress = 0;
                }
            }
        }

        public string GetName()
        {
            return "Schedule CSV Export Handler";
        }

        private static void ExportScheduleToCsv(
            Document doc,
            ViewSchedule vs,
            string path,
            ScheduleExportImportDataContext vm)
        {
            if (vs == null) throw new ArgumentNullException(nameof(vs));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            // 1) Ghi ElementId vào parameter "ElementId" (nếu có) trước khi export
            FillElementIdParameter(doc, vs);

            // 2) Export schedule bằng API có sẵn
            string folder = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);

            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
                throw new ArgumentException("Invalid path for export.");

            var options = new ViewScheduleExportOptions
            {
                ColumnHeaders = ExportColumnHeaders.OneRow,
                FieldDelimiter = ",",                         // CSV
                HeadersFootersBlanks = false,
                TextQualifier = ExportTextQualifier.DoubleQuote,
                Title = false
            };

            vs.Export(folder, name, options);

            if (vm != null)
                vm.Progress = 100;
        }

        private static void FillElementIdParameter(Document doc, ViewSchedule vs)
        {
            using (var tx = new Transaction(doc, "Fill ElementId for schedule"))
            {
                tx.Start();

                // Lấy tất cả element xuất hiện trong schedule view
                var collector = new FilteredElementCollector(doc, vs.Id)
                    .WhereElementIsNotElementType();

                foreach (var element in collector)
                {
                    Parameter p = element.LookupParameter(ElementIdColumnName);
                    if (p == null || p.IsReadOnly) continue;

                    int id = element.Id.IntegerValue;

                    if (p.StorageType == StorageType.String)
                    {
                        p.Set(id.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (p.StorageType == StorageType.Integer)
                    {
                        p.Set(id);
                    }
                }

                tx.Commit();
            }
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "schedule";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char ch in name)
            {
                if (Array.IndexOf(invalid, ch) >= 0)
                    sb.Append('_');
                else
                    sb.Append(ch);
            }

            return sb.ToString();
        }
    }
}
