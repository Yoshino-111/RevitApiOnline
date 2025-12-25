using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Collections.Generic;
using Document = Autodesk.Revit.DB.Document;

namespace RevitApiOnline.WPFDuct
{
    public class CommandDuctWPF
    {
        public void CommandDuct(Document doc, UIDocument uiDoc)
        {
            var pickElement = uiDoc.Selection.PickObject(ObjectType.Element, "Pick a element");
            Duct duct = doc.GetElement(pickElement) as Duct;

            DuctType ductType = duct.DuctType;
            Parameter offsetPara = duct.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            double ductOffset = UnitUtils.ConvertFromInternalUnits(offsetPara.AsDouble(), UnitTypeId.Millimeters);
            DuctInforVM ductInfor = new DuctInforVM(ductType.Name, ductType.Id, ductOffset, duct.Id); // tao ctor ben class DuctInforVM cac thuoc tinh can lay cua duct.

            ParameterSet listParameter = duct.Parameters; // cac parameter instant cua Duct, nen moi duct.paramerter
            List<ParameterVM> listParameterVM = new List<ParameterVM>(); // list duoc tao o DuctInfor
            foreach (Parameter para in listParameter)
            {
                ParameterVM parameterVM = new ParameterVM(para.Id, para.Definition.Name);
                listParameterVM.Add(parameterVM);
            }

            // lien ket 2 class lai trong DuctInFor

            ductInfor.ListPara = listParameterVM; // neu ko chấm tới lisPara sẽ không có thông tin của nó.

            // tuong tac voi WPF
            DuctWPF form = new DuctWPF();
            form.DataContext = ductInfor;
            bool? check = form.ShowDialog();
            if (check != null)
            {
                DuctInforVM dataDuctInfor = form.DataContext as DuctInforVM; // kiểm tra xem khi click nhận thông tin gì.
            }
        }
    }
}