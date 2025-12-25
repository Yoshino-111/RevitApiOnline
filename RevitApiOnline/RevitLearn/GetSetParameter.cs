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
    public class GetSetParameter
    {
        public void Parameter(UIDocument uiDoc, Document doc)
        {
           
            // pick 1 doi tuong trong revit
            //ICollection<ElementId> ids = uiDoc.Selection.GetElementIds();

            ////implements interface
            //ICurveUttilities curveUttilities = new CurveUttilities();
            //List<Curve> listWall = curveUttilities.GetCurveFromRevit(doc, ids);

            //View view = doc.ActiveView;
            //Level level = view.GenLevel;

            //Parameter levelParameter = view.get_Parameter(BuiltInParameter.PLAN_VIEW_LEVEL);
            //string levelName = levelParameter.AsString();

            //using(Transaction t = new Transaction(doc, "CreateWall"))
            //{
            //    t.Start();
            //    foreach(Curve curve in listWall)
            //    {
            //        Wall wall = Wall.Create(doc, curve, level.Id, false);
            //    }
            //    t.Commit();
            //}

            //Chon truoc 1 doi tuong

            // IEnumerable<ElementId> selectedID = uiDoc.Selection.GetElementIds();
            // ElementId id = null;
            // Element element = doc.GetElement(id); // truy cap nguoc lai doi tuong

            // Duct duct = element as Duct; // ep kieu qua duct vi element chua biet la catagory minh mong muon la gi
            // Pipe pipe = element as Pipe;
            // bool isDuct = element is Duct; // check dieu kien dung khong

            // // trong model co 2 dang family là family trong môi trường gọi là family, khi load vào dự án đặt trên model được gọi là familyinstance
            // //Family family = null;
            // //FamilyInstance familyInstance = null;

            // // trong family thi co familytype
            // DuctType ductType = element as DuctType;
            // PipeType pipeType = element as PipeType;
            // // family symbol co nghia la truy cap vao type cua family chu ko phai instance family
            // FamilySymbol familySymbol = element as FamilySymbol;

            // // De lay thong tin cua 1 element chua biet catagory la gi ta truy cap nhu sau.
            //Category category = doc.Settings.Categories.get_Item(BuiltInCategory.OST_DuctCurves); // thong tin cua category la duct

            // // Lay parameter cua family duct nay.

            // FamilyInstance familyInstance = null;
            // Parameter parameter = familyInstance.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM); // lay thong tin instance trong family.
            // double valueParameter = parameter.AsDouble(); // ep kieu ve double trong thong tin cua parameter (Storage type).

            // // Lay parameter cua family duct type (200x200 : co chieu rong W = 800, lay thong tin cua W)

            // ElementId elementId = familyInstance.GetTypeId(); // GettypeID co nghia la trong revitlookup tim kiem toi gettypeID
            // FamilySymbol familySymbol1 = doc.GetElement(elementId) as FamilySymbol;
            // Parameter parameterDuct = familyInstance.get_Parameter(BuiltInParameter.DUCT_ROUGHNESS);
            // double valueDuct = parameterDuct.AsDouble();

            // // parameter custorm do nguoi dung tao ra. vi du ( a =10 ,cx =5,...) su dung lai cac thuoc tinh o tren.

            // FamilyInstance familyAirTerminal = null;
            // //ElementId elementId = familyInstance.GetTypeId(); su dung lai o tren
            // FamilySymbol typeAirTerminal = doc.GetElement(elementId) as FamilySymbol;
            // Parameter parameterAirTerminal = familyAirTerminal.LookupParameter("aa");
            // double valueAirTerminal = parameterAirTerminal.AsDouble();

            // // doi don vi tu he thong sang milimet, hoac nguoc lai

            // double valueConvert = UnitUtils.ConvertFromInternalUnits(valueAirTerminal, UnitTypeId.Millimeters);
            // double valueConvert2 = UnitUtils.ConvertToInternalUnits(valueAirTerminal, UnitTypeId.Millimeters);


            // pick 1 diem trong revit
            //XYZ pointA = uiDoc.Selection.PickPoint("Pick a point");
            //XYZ pointB = uiDoc.Selection.PickPoint("Pick a point");

            // pick 1 doi face  trong revit

            //Reference reference = uiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Face, "Pick a face");
            //Element element = doc.GetElement(reference);

            // pick 1 doi tuong element trong revit bat ki

            //Reference reference1 = uiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, "Pick a element");

            // pick 1 doi tuong trong revit co bo loc:  Vi du chi pick duoc duct, pipe...

            //Reference reference2 = uiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, new DuctSelection(),"Pick a Duct");

            // pick nhieu doi tuong trong revit co bo loc :Vi du chi pick duoc duct, pipe...
            //IList<Reference> ilistDuct = uiDoc.Selection.PickObjects(ObjectType.Element, new DuctSelection(), "Pick a Duct"); // IList co reference ta can lay 1 list cua refer

            //List<Duct> ducts = new List<Duct>(); // tao 1 list cho duct
            //foreach (Reference listDuct in ilistDuct) // loc dieu kien
            //{
            //    Duct ductItem = doc.GetElement(listDuct) as Duct; // ep elements qua duct
            //    if (ductItem != null)
            //    {
            //        ducts.Add(ductItem);
            //    }
            //}

            //pick 1 box trong revit, quet qua trong revit

            //PickedBox pickedBox = uiDoc.Selection.PickBox(PickBoxStyle.Directional, "Pick Box");
            //XYZ min = pickedBox.Min;
            //XYZ max = pickedBox.Max;

            // pick rectange trong revit
            //IList<Element> pickRectange = uiDoc.Selection.PickElementsByRectangle(new DuctSelection(),"Pick Duct");

            #region Set 1 doi tuong revit cho gia tri vao vi du offset duct la 3000mm 

            //IEnumerable<Reference> references = uiDoc.Selection.PickObjects(ObjectType.Element, new DuctSelection(), "Pick a Duct");
            //List<Duct> ducts = new List<Duct>();
            //foreach (Reference reference in references)
            //{
            //    Duct duct = doc.GetElement(reference) as Duct;
            //    if (duct != null)
            //    {
            //        ducts.Add(duct);
            //    }
            //}
            //using (Transaction t = new Transaction(doc, "SetOffsetDuct"))
            //{
            //    t.Start();
            //    foreach (Duct item in ducts)
            //    {
            //        Parameter parameter = item.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            //        if (parameter != null && !parameter.IsReadOnly)
            //        {
            //            double offsetDuct = 3000;
            //            double newOffsetDuct = UnitUtils.ConvertToInternalUnits(offsetDuct, UnitTypeId.Millimeters);
            //            parameter.Set(newOffsetDuct);

            //            //parameter?.Set(newOffsetDuct); // cau nay bang voi (if (parameter!= null)
            //        }
            //    }
            //    t.Commit();
            //}
            #endregion

            #region tao 1 dimention cho 2 ong duct.(chua lam duoc cho duct, nay dung cho wall, ceiling, floor), duct phai la geometry.
            IEnumerable<Reference> references = uiDoc.Selection.PickObjects(ObjectType.Element, new DuctSelection(), "Pick a Duct");
            List<Duct> ducts = new List<Duct>();
            foreach (Reference reference in references)
            {
                Duct duct = doc.GetElement(reference) as Duct;
                if (duct != null)
                {
                    ducts.Add(duct);
                }
            }
            View actiview = doc.ActiveView;// set view activew

            ReferenceArray referenceArray = new ReferenceArray(); // NewDimention can ham ReferenceArray nen ta can tao 1 ham
            foreach (Duct reference in ducts)
            {
                Reference refInternal = HostObjectUtils.GetSideFaces(reference, ShellLayerType.Interior).First(); // Interior mat truoc cua ref
                Reference refExternal = HostObjectUtils.GetSideFaces(reference, ShellLayerType.Exterior).First(); // Exterior mat sau cua ref
                referenceArray.Append(refInternal);
                referenceArray.Append(refExternal);
            }

            Reference refFist = referenceArray.get_Item(0); // lay ref dau tien cua list
            Element element = doc.GetElement(refFist);
            Face face = element.GetGeometryObjectFromReference(refFist) as Face; // ep kieu qua face
            PlanarFace planarFace = face as PlanarFace; // PlanarFace la face thang.
            XYZ orinalFace = planarFace.Origin;
            XYZ normalFace = planarFace.FaceNormal.Normalize(); // Normalize la chuyen ve do dai` bang 1.
            XYZ point = uiDoc.Selection.PickPoint("Pick a point");

            Line linePutDim = Line.CreateUnbound(point, normalFace);

            using (Transaction t = new Transaction(doc, "Dimention Duct"))
            {
                t.Start();
                doc.Create.NewDimension(actiview, linePutDim, referenceArray);
                t.Commit();
            }


            //XYZ vectorA = null;
            //XYZ vectorB = null;
            //double dotProduct = vectorA.Normalize().DotProduct(vectorB.Normalize()); // hoc sau
            //if(Math.Abs(dotProduct-1) < 0,001)



            // pick diem dat cho dimention




            #endregion
        }
    }
    // class dieu kien chay duoc DuctSelection
    public class DuctSelection : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {

            if (elem != null && elem is Duct)
            {
                return true;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }
}
