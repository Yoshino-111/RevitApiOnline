using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace RevitApiOnline.Schedule
{
    internal class ScheduleImportHandler : IExternalEventHandler
    {
        public ScheduleExportImportDataContext DataContext { get; set; }

        public void Execute(UIApplication app)
        {
            var vm = DataContext;
            if (vm == null) return;

            try
            {
                vm.StatusText = "Importing CSV...";
                vm.Progress = 0;

                UIDocument uiDoc = app.ActiveUIDocument;
                if (uiDoc == null)
                {
                    TaskDialog.Show("Schedule CSV", "No active document.");
                    vm.StatusText = "Import failed: no active document.";
                    return;
                }

                Document doc = uiDoc.Document;

                var scheduleVm = vm.SelectedSchedule;
                if (scheduleVm == null)
                {
                    TaskDialog.Show("Schedule CSV", "Please select a schedule.");
                    vm.StatusText = "Import failed: no schedule selected.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(vm.SelectedKeyParameter))
                {
                    TaskDialog.Show("Schedule CSV", "Please select a key parameter.");
                    vm.StatusText = "Import failed: no key parameter selected.";
                    return;
                }

                var vs = doc.GetElement(scheduleVm.Id) as ViewSchedule;
                if (vs == null)
                {
                    TaskDialog.Show("Schedule CSV", "Selected schedule cannot be found.");
                    vm.StatusText = "Import failed: schedule not found.";
                    return;
                }

                var dlg = new OpenFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    Title = "Import schedule from CSV"
                };

                if (dlg.ShowDialog() != true)
                {
                    vm.StatusText = "Import cancelled.";
                    return;
                }

                string path = dlg.FileName;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);

                if (lines.Length < 2)
                {
                    TaskDialog.Show("Schedule CSV", "CSV file is empty.");
                    vm.StatusText = "Import failed: empty CSV.";
                    return;
                }

                var headerCells = SplitCsvLine(lines[0]);
                if (headerCells.Count == 0)
                {
                    TaskDialog.Show("Schedule CSV", "CSV header row is empty.");
                    vm.StatusText = "Import failed: invalid CSV.";
                    return;
                }

                string keyParamName = vm.SelectedKeyParameter;

                // Tìm cột key trong CSV
                int keyColumnIndex = -1;
                for (int i = 0; i < headerCells.Count; i++)
                {
                    string h = (headerCells[i] ?? string.Empty).Trim();
                    if (string.Equals(h, keyParamName, StringComparison.OrdinalIgnoreCase))
                    {
                        keyColumnIndex = i;
                        break;
                    }
                }

                if (keyColumnIndex == -1)
                {
                    TaskDialog.Show("Schedule CSV",
                        $"Cannot find column '{keyParamName}' in CSV header. Please export again from this tool.");
                    vm.StatusText = "Import failed: key column not found in CSV.";
                    return;
                }

                // Map key -> element
                var collector = new FilteredElementCollector(doc, vs.Id)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var elementsByKey = new Dictionary<string, Element>();

                foreach (var element in collector)
                {
                    Element owner = element;
                    Parameter p = owner.LookupParameter(keyParamName);

                    if (p == null)
                    {
                        Element typeElem = doc.GetElement(element.GetTypeId());
                        if (typeElem != null)
                        {
                            p = typeElem.LookupParameter(keyParamName);
                            owner = typeElem;
                        }
                    }

                    if (p == null || p.IsReadOnly)
                        continue;

                    string key = GetParameterKeyString(p);
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (!elementsByKey.ContainsKey(key))
                        elementsByKey.Add(key, owner);
                }

                int totalDataRows = lines.Length - 1;
                int processedRows = 0;

                using (var tx = new Transaction(doc, "Import schedule from CSV"))
                {
                    tx.Start();

                    for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
                    {
                        var values = SplitCsvLine(lines[lineIndex]);
                        if (values.Count == 0)
                            continue;

                        if (keyColumnIndex >= values.Count)
                            continue;

                        string keyValue = (values[keyColumnIndex] ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(keyValue))
                            continue;

                        if (!elementsByKey.TryGetValue(keyValue, out Element element))
                            continue;

                        for (int colIndex = 0; colIndex < headerCells.Count && colIndex < values.Count; colIndex++)
                        {
                            if (colIndex == keyColumnIndex)
                                continue;

                            string paramName = (headerCells[colIndex] ?? string.Empty).Trim();
                            if (string.IsNullOrEmpty(paramName))
                                continue;

                            string newValue = values[colIndex];
                            UpdateParameterValue(element, paramName, newValue);
                        }

                        processedRows++;
                        if (vm != null && totalDataRows > 0)
                        {
                            vm.Progress = (int)(processedRows * 100.0 / totalDataRows);
                        }
                    }

                    tx.Commit();
                }

                vm.StatusText = $"Import completed: {Path.GetFileName(path)}";
                vm.Progress = 100;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Luc Nguyen – Schedule CSV – Import error", ex.ToString());
                if (DataContext != null)
                {
                    DataContext.StatusText = "Import failed: " + ex.Message;
                    DataContext.Progress = 0;
                }
            }
        }

        public string GetName()
        {
            return "Schedule CSV Import Handler";
        }

        private static string GetParameterKeyString(Parameter p)
        {
            if (p == null) return null;

            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString();
                case StorageType.Integer:
                    return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return p.AsDouble().ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    return p.AsElementId().IntegerValue.ToString(CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        private static void UpdateParameterValue(Element element, string paramName, string value)
        {
            if (element == null)
                return;

            Document doc = element.Document;
            Parameter param = element.LookupParameter(paramName);

            if (param == null)
            {
                Element typeElem = doc.GetElement(element.GetTypeId());
                if (typeElem != null)
                {
                    param = typeElem.LookupParameter(paramName);
                    element = typeElem;
                }
            }

            if (param == null || param.IsReadOnly)
                return;

            switch (param.StorageType)
            {
                case StorageType.String:
                    param.Set(value);
                    break;

                case StorageType.Integer:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
                        param.Set(iVal);
                    break;

                case StorageType.Double:
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                        param.Set(dVal);
                    break;

                case StorageType.ElementId:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idInt))
                        param.Set(new ElementId(idInt));
                    break;
            }
        }

        private static System.Collections.Generic.List<string> SplitCsvLine(string line)
        {
            var result = new System.Collections.Generic.List<string>();

            if (line == null)
            {
                result.Add(string.Empty);
                return result;
            }

            bool inQuotes = false;
            var sb = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                else
                {
                    if (ch == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (ch == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
            }

            result.Add(sb.ToString());
            return result;
        }
    }
}
