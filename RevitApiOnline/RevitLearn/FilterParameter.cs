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
    public class FilterParameter
    {
        public void FilterParameterAddInRevit(UIDocument uiDoc, Document doc)
        {
            // parameter can filter
            double offsetDuct = 300;
            double offsetDuctInch = UnitUtils.ConvertToInternalUnits(offsetDuct, UnitTypeId.Millimeters);

            // filter cho model actiview
            FilteredElementCollector filterForDuct = new FilteredElementCollector(doc, doc.ActiveView.Id)
                                                    .OfCategory(BuiltInCategory.OST_DuctCurves).WhereElementIsNotElementType();



            ElementId elementId = new ElementId((long)BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            FilterRule filterRuleDuct = ParameterFilterRuleFactory.CreateEqualsRule(elementId, offsetDuctInch, 0.001); // b2 tim kiem bang filterrule 
            ElementParameterFilter filterElement = new ElementParameterFilter(filterRuleDuct); //b1 muon filter can ElementParameterFilter ma truyen vao filterrule

            string ductSystemType = "Supply Air";
            ElementId elementId1 = new ElementId((long)BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            FilterRule filterRuleDuct1 = ParameterFilterRuleFactory.CreateEqualsRule(elementId1, ductSystemType);
            ElementParameterFilter filterDuctSystem = new ElementParameterFilter(filterRuleDuct1);

            LogicalAndFilter logicalAndFilter = new LogicalAndFilter(filterDuctSystem, filterElement);
            // ket hop voi or
            double ductHeight = 300;
            double heightDuctInch = UnitUtils.ConvertToInternalUnits(ductHeight, UnitTypeId.Millimeters);
            ElementId elementIdHeight = new ElementId((long)BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            FilterRule filterRuleDuct2 = ParameterFilterRuleFactory.CreateEqualsRule(elementIdHeight, heightDuctInch, 0.001);
            ElementParameterFilter filterDuctHeight = new ElementParameterFilter(filterRuleDuct1);
            LogicalOrFilter logicalOrFilterDuct = new LogicalOrFilter(logicalAndFilter, filterDuctHeight);

            IEnumerable<Element> listDuct = filterForDuct.WherePasses(logicalOrFilterDuct).ToElements();
            IEnumerable<ElementId> elementIdDuct = listDuct.Select(x => x.Id);
            //uiDoc.Selection.SetElementIds(elementIdDuct.ToList());

            Category ductCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_DuctCurves);
            List<ElementId> listCategory = new List<ElementId> { ductCategory.Id };
            using (Transaction t = new Transaction(doc, "Create filter"))
            {
                t.Start();
                ParameterFilterElement filterParameterDuct = ParameterFilterElement.Create(doc, "Duct", listCategory);
                filterParameterDuct.SetElementFilter(logicalOrFilterDuct);
                doc.ActiveView.SetFilterVisibility(filterParameterDuct.Id, true);

                t.Commit();
            }
        }
    }
}
