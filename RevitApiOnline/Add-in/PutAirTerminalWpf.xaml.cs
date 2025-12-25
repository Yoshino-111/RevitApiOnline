using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;

namespace RevitApiOnline.Add_in
{
    /// <summary>
    /// Interaction logic for PutAirTerminalWpf.xaml
    /// </summary>
    public partial class PutAirTerminalWpf : Window
    {
        private ExternalEvent _familyTypeEvent;
        private ExternalEvent _putAirEvent;

        public PutAirTerminalWpf(ExternalEvent familyTypeEvent, ExternalEvent putAirEvent)
        {
            _putAirEvent = putAirEvent;
            _familyTypeEvent = familyTypeEvent;
        }

        private void clickData(object sender, RoutedEventArgs e)
        {
            _putAirEvent.Raise();
        }

        private void comboboxFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            _familyTypeEvent.Raise();
        }
    }
}