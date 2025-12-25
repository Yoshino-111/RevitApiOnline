using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.RightsManagement;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.WPFDuct
{
    public class DuctInforVM
    {
        public DuctInforVM()
        {
            
        }

        public DuctInforVM(String ductType , ElementId elementId, double offset ,ElementId offsetID) // tao ctor de goi ben ham command. set,get cho no
        {
            (DuctType, DuctTypeId, Offset, OffsetID) = (ductType, elementId, offset, offsetID);
        }

        public string DuctType {  get; set; }
        public ElementId DuctTypeId { get; set; }


        public double Offset { get; set; }
        public ElementId OffsetID { get; set; }

        public List<ParameterVM> ListPara{ get; set; }
        
        public ParameterVM SelectedParameter { get; set; }


    }
    public class ParameterVM
    {
        public ParameterVM()
        {
            
        }
        public ParameterVM(ElementId parameterID, string parameterName)
        {
            ParameterID = parameterID;
            ParameterName = parameterName;
        }

        public ElementId ParameterID { get; set; }
        public string ParameterName { get; set; }
    }
}
