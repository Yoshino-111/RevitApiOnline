using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RevitApiOnline.Shared.Implements;

namespace RevitApiOnline.RevitLearn
{
    public class RevitLearn
    {
        public void Learn(UIDocument uiDoc, Document doc)
        {
            ICollection<ElementId> ids = uiDoc.Selection.GetElementIds();

            // implement interface;
            CurveUttilities curveUtilities = new CurveUttilities();
            List<Curve> listCurveWall = curveUtilities.GetCurvesFromDetailCurve(doc, ids);
         
            Autodesk.Revit.DB.View view = doc.ActiveView;
            Level level = view.GenLevel;

            Parameter levelParameter = view.get_Parameter(BuiltInParameter.PLAN_VIEW_LEVEL);
            string levelName = levelParameter.AsString();

            using (Transaction t = new Transaction(doc, "CreateWall"))
            {
                t.Start();
                foreach (Curve curve in listCurveWall)
                {
                    Wall wall = Wall.Create(doc, curve, level.Id, false);
                }
                t.Commit();
            }
           

        }
    }
}


