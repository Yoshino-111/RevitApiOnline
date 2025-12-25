using Autodesk.Revit.UI;
using RevitApiOnline.Insulation;
using System;
using System.Windows;

namespace RevitApiOnline.Insulation
{
    public partial class PipeDuctInsulationWpf : Window
    {
        private readonly ExternalEvent _applyInsulationEvent;

        public PipeDuctInsulationWpf(ExternalEvent applyInsulationEvent)
        {
            InitializeComponent();
            _applyInsulationEvent = applyInsulationEvent;
        }

        private void ButtonApply_Click(object sender, RoutedEventArgs e)
        {
            if (_applyInsulationEvent != null)
            {
                _applyInsulationEvent.Raise();
            }
            else
            {
                MessageBox.Show("ExternalEvent chưa được khởi tạo. Kiểm tra lại PipeDuctInsulationAppShow.ShowForm().",
                                "Insulation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            PipeDuctInsulationAppShow.Form = null;
        }
    }
}