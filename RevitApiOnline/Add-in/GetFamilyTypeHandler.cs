using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace RevitApiOnline.Add_in
{
    internal class GetFamilyTypeHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument.Document;
            AirTerminalDataContext dataContext = AirTerminalAppShow.formAirTerminalWpf.DataContext as AirTerminalDataContext;

            FamilyVM selectedFamilyVM = dataContext.familySelected;
            Family selectedFamily = doc.GetElement(selectedFamilyVM.id) as Family;
            List<FamilyTypeVM> listFamilyTypeVM = new List<FamilyTypeVM>();

            foreach(ElementId id in selectedFamily.GetFamilySymbolIds())
            {
                FamilySymbol familySymbol = doc.GetElement(id) as FamilySymbol;
                FamilyTypeVM familyTypeVM = new FamilyTypeVM();
                familyTypeVM.id = familySymbol.Id;
                familyTypeVM.Name = familySymbol.Name;

                listFamilyTypeVM.Add(familyTypeVM);
            }
         
            AirTerminalAppShow.formAirTerminalWpf.comboboxFamilyType.ItemsSource = listFamilyTypeVM;
             
        }

        public string GetName()
        {
            return "GetFamilyTypeHandler1";
        }
    }
}
