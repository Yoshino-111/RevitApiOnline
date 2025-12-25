using System.Windows;

namespace RevitApiOnline.ListBoxWPF
{
    /// <summary>
    /// Interaction logic for ListBoxDuctWPF.xaml
    /// </summary>
    public partial class ListBoxDuctWPF : Window
    {
        public ListBoxDuctWPF()
        {
            InitializeComponent();
        }

        private void clickOK(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close();
        }
    }
}