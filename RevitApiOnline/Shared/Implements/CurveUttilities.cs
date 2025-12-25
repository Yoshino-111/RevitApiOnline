using Autodesk.Revit.DB;
using RevitApiOnline.Shared.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.Shared.Implements
{
    public class CurveUttilities : ICurveUttilities
    {
        public List<Curve> GetCurveFromRevit(Document doc, ICollection<ElementId> ids)
        {
            List<Curve> curvesList = new List<Curve>();
            foreach (ElementId id in ids)
            {
                Element element = doc.GetElement(id);
                bool isDetail = element is DetailCurve;
                if (isDetail)
                {
                    DetailCurve detailCurve = element as DetailCurve;
                    Curve curve = detailCurve.GeometryCurve;
                    curvesList.Add(curve);
                }
            }
            return curvesList;
        }

        internal List<Curve> GetCurvesFromDetailCurve(Document doc, ICollection<ElementId> ids)
        {
            throw new NotImplementedException();
        }
    }
}
