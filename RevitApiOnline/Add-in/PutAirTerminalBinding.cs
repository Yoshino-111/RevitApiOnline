using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.Add_in
{
    [Transaction(TransactionMode.Manual)]
    public class PutAirTerminalBinding : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            IEnumerable<Family> familyColection = new FilteredElementCollector(doc).OfClass(typeof(Family))
                .Cast<Family>().Where(x => x.FamilyCategoryId.Value == (long)BuiltInCategory.OST_DuctTerminal); // lay tat ca family airterminal trong model
            var listFamilyVM = familyColection.Select(x => new FamilyVM { Name = x.Name, id = x.Id } ); // add vao list family

            AirTerminalAppShow.ShowForm();
            AirTerminalAppShow.formAirTerminalWpf.comboboxFamily.ItemsSource = listFamilyVM;

            AirTerminalDataContext dataContext = new AirTerminalDataContext();
            dataContext.offset = 0;
            AirTerminalAppShow.formAirTerminalWpf.DataContext = dataContext;

           return Result.Succeeded;
        }
    }
}
