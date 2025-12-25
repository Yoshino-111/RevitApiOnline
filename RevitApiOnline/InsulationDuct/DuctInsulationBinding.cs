using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.InsulationDuct
{
    [Transaction(TransactionMode.Manual)]
    public class DuctInsulationBinding : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            InsulationDuctWPF form = new InsulationDuctWPF();
            form.DataContext = doc;
            form.ShowDialog();

            return Result.Succeeded;
        }
    }
}
