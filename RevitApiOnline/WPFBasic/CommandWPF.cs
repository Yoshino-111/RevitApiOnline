using Autodesk.Revit.Creation;
using Autodesk.Revit.UI;
using RevitApiOnline.WPF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.WPFBasic
{
    public class CommandWPF
    {
        public void WPFBasic(Document doc, UIDocument uiDoc)
        {
            LearnWPF form = new LearnWPF();

            //form.Show(); // show ra nhung van co the tuong tac voi revit
            //form.ShowDialog(); // show ra nhung ko tuong tac duoc voi revit

            form.ShowDialog();
            string valueTextBox = form.TextBox.Text;
        }
    }
}
