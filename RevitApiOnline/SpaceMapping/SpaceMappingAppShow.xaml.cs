using System.Windows;
using Autodesk.Revit.UI;

namespace RevitApiOnline.SpaceMapping
{
    /// <summary>
    /// Code-behind cho form Space Mapping
    /// </summary>
    public partial class SpaceMappingAppShow : Window
    {
        private readonly SpaceMappingDataContext _viewModel;

        private SpaceMappingAppShow(UIApplication uiApp)
        {
            InitializeComponent();
            _viewModel = new SpaceMappingDataContext(uiApp);
            DataContext = _viewModel;
        }

        // Gọi từ IExternalCommand
        public static void ShowForm(UIApplication uiApp)
        {
            SpaceMappingAppShow win = new SpaceMappingAppShow(uiApp);
            win.ShowDialog();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.RunMapping();
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void AddMapping_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.AddCurrentMapping();
        }
    }
}
