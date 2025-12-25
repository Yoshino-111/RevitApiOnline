using System.Windows;

namespace RevitApiOnline.Schedule
{
    public partial class ScheduleExportImportWpf : Window
    {
        public ScheduleExportImportWpf()
        {
            InitializeComponent();
        }

        private void ButtonExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ScheduleExportImportDataContext vm)
            {
                vm.RequestExport();
            }
        }

        private void ButtonImportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ScheduleExportImportDataContext vm)
            {
                vm.RequestImport();
            }
        }

        private void ButtonGenerateKeys_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ScheduleExportImportDataContext vm)
            {
                vm.RequestGenerateKeys();
            }
        }

        private void ButtonClearKeys_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ScheduleExportImportDataContext vm)
            {
                vm.RequestClearKeys();
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
