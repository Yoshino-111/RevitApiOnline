using System.ComponentModel;

namespace RevitApiOnline.SpaceMapping
{
    /// <summary>
    /// Một dòng mapping: 1 tham số bên Link + 1 tham số trong model chính
    /// </summary>
    public class SpaceMappingParameterSelector : INotifyPropertyChanged
    {
        private string _linkParameterName;
        public string LinkParameterName
        {
            get => _linkParameterName;
            set
            {
                if (_linkParameterName != value)
                {
                    _linkParameterName = value;
                    OnPropertyChanged(nameof(LinkParameterName));
                }
            }
        }

        private string _selectedMainParameterName;
        public string SelectedMainParameterName
        {
            get => _selectedMainParameterName;
            set
            {
                if (_selectedMainParameterName != value)
                {
                    _selectedMainParameterName = value;
                    OnPropertyChanged(nameof(SelectedMainParameterName));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
