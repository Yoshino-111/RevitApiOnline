using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RevitApiOnline.WPFDuct
{
    /// <summary>
    /// Interaction logic for DuctWPF.xaml
    /// </summary>
    public partial class DuctWPF : Window
    {
        public DuctWPF()
        {
            InitializeComponent();
        }

        private void dataOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close();
        }
    }
}
