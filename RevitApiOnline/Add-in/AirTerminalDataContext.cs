using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.Add_in
{
    public class AirTerminalDataContext
    {
        public FamilyVM familySelected { set; get; }
        public FamilyTypeVM typeSelected { set; get; }
        public Level levelSelected { set; get; }
        public double offset {  set; get; }

    }
}
