using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.InsulationDuct
{
    public class DuctTypeVM
    {
        public string TypeName { get; set; }
        public ElementId id { get; set; }
    }
}
