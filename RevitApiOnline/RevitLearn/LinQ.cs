using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using Document = Autodesk.Revit.DB.Document;
using View = Autodesk.Revit.DB.View;

namespace RevitApiOnline.RevitLearn
{
    public class LinQ
    {
        public void LearnLinQ(UIDocument uiDoc, Document doc)
        {
            //filter Elements View LinQ

            List<Element> listDuct = new FilteredElementCollector(doc, doc.ActiveView.Id).OfCategory(BuiltInCategory.OST_DuctCurves)
                                     .WhereElementIsNotElementType().ToElements().ToList(); // filter theo actiview.

            List<Element> listTypeDuct = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctCurves)
                                     .WhereElementIsElementType().ToElements().ToList(); // filter theo model, type ko co trong actiview

            // co 3 cach filter trong linkQ (3 cach viet nhung van dung 1 kieu). Cach dung nhieu nhat la cach 1.
            double withDuct = 300; // cach 1
            double withDuctInch = UnitUtils.ConvertToInternalUnits(withDuct, UnitTypeId.Millimeters);
            List<Element> withDuct300 = listDuct.Where(x => x.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuctInch)
                                        .ToList();

            List<Element> withDuct300type1 = listDuct.Where(x =>
            {
                bool isTrue = x.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuctInch;

                return isTrue;
            }).ToList(); // cach 2


            Func<Element, bool> funcDuct = (item) =>
            {
                bool isTrue = item.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuctInch;
                return isTrue;
            };

            List<Element> ductType = listDuct.Where(funcDuct).ToList(); // cach 3

            string ductName = "With";

            List<Element> ductType1 = (from duct in listDuct
                                       where duct.Name == ductName
                                       select duct).ToList(); // cach 4



            List<Element> listOrder = listDuct.OrderBy(x => x.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?
                                      .AsDouble() == withDuct).ToList(); // sap xep cac duct theo A-Z

            IEnumerable<IGrouping<bool, Element>> keyVal = listDuct.GroupBy(x => x.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?
                                                           .AsDouble() == withDuct).ToList(); // group cac element co with =?

            Element findFist = listDuct.First(x => x.get_Parameter
                               (BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuct); // tim thang dau tien, neu tim ko co thi vang loi~
            Element findFistOrDefault = listDuct.FirstOrDefault(x => x.get_Parameter
                                        (BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuct); // tim thang dau tien nhung ko vang loi~

            bool isExisted = listDuct.Exists(x => x.get_Parameter
                             (BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() == withDuct); // tim thu trong nay co element nao thoai man dieu kien nay khong
        }
    }
}
