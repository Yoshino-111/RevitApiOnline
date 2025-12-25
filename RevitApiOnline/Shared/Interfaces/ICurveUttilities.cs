using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.Shared.Interfaces
{
    public interface ICurveUttilities
    {
        List<Curve> GetCurveFromRevit(Document doc, ICollection<ElementId> ids);
    }
}
