using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.UI;
using RevitApiOnline.WPFDuct;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.ListBoxWPF
{
    public class CommandListBoxDuct
    {
        public void CommandListBox(Document doc, UIDocument uiDoc)
        {
            // lay tat ca duct trong model
            var listDuct = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctCurves)
                .WhereElementIsNotElementType().OfClass(typeof(Duct)).Cast<Duct>().Where(x => x.DuctType != null).ToList(); // bo qua duct type

            List<DuctListInfor> listDuctInfor = new List<DuctListInfor>(); // new list duct infor class

            foreach (Duct duct in listDuct)
            {
                DuctListInfor ductInforVM = new DuctListInfor();
                ductInforVM.NameDuct = duct.Name;
                ductInforVM.IdNameDuct = duct.Id;
                ductInforVM.LevelDuct = doc.GetElement(duct.LevelId).Name;
                ductInforVM.IdLevelDuct = duct.LevelId;

                listDuctInfor.Add(ductInforVM);
            }
            listDuctInfor = listDuctInfor.OrderBy(x => x.NameDuct).ToList(); // sap xem theo thu tu A-Z
            DuctListBoxVM dataContext = new DuctListBoxVM();

            // show ra gia tri 
            var form = new ListBoxDuctWPF();
            form.listBoxOfDuct.ItemsSource = listDuctInfor;
            form.DataContext = dataContext;

            var resultForm = form.ShowDialog();
            if (resultForm == true)
            {
                var selectedItem = (form.DataContext as DuctListBoxVM).DuctSelected;
            }

        }
    }
}
