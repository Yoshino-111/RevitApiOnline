using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.ListBoxWPF
{
    public class DuctListBoxVM
    {
        public DuctListInfor DuctSelected {set; get; }
    }

    public class DuctListInfor
    {
        public string NameDuct {  get; set; }
        public ElementId IdNameDuct { get; set; }
        public string LevelDuct { get; set; }
        public ElementId IdLevelDuct { get; set; }


    }
}
