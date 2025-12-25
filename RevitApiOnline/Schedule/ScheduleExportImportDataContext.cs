using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Schedule
{
    public class ScheduleExportImportDataContext : INotifyPropertyChanged
    {
        private readonly UIDocument _uiDoc;
        private readonly ExternalEvent _exportEvent;
        private readonly ExternalEvent _importEvent;
        private readonly ExternalEvent _generateKeyEvent;
        private readonly ExternalEvent _clearKeyEvent;

        public ObservableCollection<ScheduleInfoVM> Schedules { get; } =
            new ObservableCollection<ScheduleInfoVM>();

        public ObservableCollection<ScheduleFieldVM> Fields { get; } =
            new ObservableCollection<ScheduleFieldVM>();

        // Danh sách parameter có trong schedule -> dùng cho ComboBox chọn key
        public ObservableCollection<string> KeyParameters { get; } =
            new ObservableCollection<string>();

        private ScheduleInfoVM _selectedSchedule;
        public ScheduleInfoVM SelectedSchedule
        {
            get => _selectedSchedule;
            set
            {
                if (_selectedSchedule != value)
                {
                    _selectedSchedule = value;
                    OnPropertyChanged();
                    LoadFieldsForSelectedSchedule();
                }
            }
        }

        private string _selectedKeyParameter;
        public string SelectedKeyParameter
        {
            get => _selectedKeyParameter;
            set
            {
                if (_selectedKeyParameter != value)
                {
                    _selectedKeyParameter = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _progress;
        public int Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged();
                }
            }
        }

        public ScheduleExportImportDataContext(
            UIDocument uiDoc,
            ExternalEvent exportEvent,
            ExternalEvent importEvent,
            ExternalEvent generateKeyEvent,
            ExternalEvent clearKeyEvent)
        {
            _uiDoc = uiDoc ?? throw new ArgumentNullException(nameof(uiDoc));
            _exportEvent = exportEvent ?? throw new ArgumentNullException(nameof(exportEvent));
            _importEvent = importEvent ?? throw new ArgumentNullException(nameof(importEvent));
            _generateKeyEvent = generateKeyEvent ?? throw new ArgumentNullException(nameof(generateKeyEvent));
            _clearKeyEvent = clearKeyEvent ?? throw new ArgumentNullException(nameof(clearKeyEvent));

            StatusText = "Ready.";
            Progress = 0;

            LoadSchedules();
        }

        public void RequestExport() => _exportEvent?.Raise();
        public void RequestImport() => _importEvent?.Raise();
        public void RequestGenerateKeys() => _generateKeyEvent?.Raise();
        public void RequestClearKeys() => _clearKeyEvent?.Raise();

        private void LoadSchedules()
        {
            Schedules.Clear();

            Document doc = _uiDoc.Document;

            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate && !vs.Name.StartsWith("<"))
                .OrderBy(vs => vs.Name);

            foreach (var vs in schedules)
            {
                Schedules.Add(new ScheduleInfoVM
                {
                    Id = vs.Id,
                    Name = vs.Name
                });
            }

            SelectedSchedule = Schedules.FirstOrDefault();
        }

        private void LoadFieldsForSelectedSchedule()
        {
            Fields.Clear();
            KeyParameters.Clear();

            if (_selectedSchedule == null)
                return;

            Document doc = _uiDoc.Document;
            var vs = doc.GetElement(_selectedSchedule.Id) as ViewSchedule;
            if (vs == null)
                return;

            var def = vs.Definition;
            int fieldCount = def.GetFieldCount();

            for (int i = 0; i < fieldCount; ++i)
            {
                var field = def.GetField(i);
                if (field.IsHidden) continue;

                string name = field.GetName();

                Fields.Add(new ScheduleFieldVM { Name = name });

                if (!KeyParameters.Contains(name))
                    KeyParameters.Add(name);
            }

            SelectedKeyParameter = KeyParameters.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
