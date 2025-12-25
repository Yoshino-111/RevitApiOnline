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

namespace RevitApiOnline.WPF
{
    /// <summary>
    /// Interaction logic for LearnWPF.xaml
    /// </summary>
    public partial class LearnWPF : Window
    {
        public LearnWPF()
        {
            InitializeComponent();
        }

        private void textChangedBox(object sender, TextChangedEventArgs e)
        {
            System.Windows.Controls.TextBox textBox = sender as System.Windows.Controls.TextBox;
            string valueText = textBox.Text;
        }

        private void clickOk(object sender, RoutedEventArgs e)
        {
            string valueText = TextBox.Text;
            bool isChecked = checkBoxAll.IsChecked == true;
            bool isCheckedYes = checkBoxYes.IsChecked == true;
            bool isCheckedNo = checkBoxNo.IsChecked == true;
        }
    }
}
