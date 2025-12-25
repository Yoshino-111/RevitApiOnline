using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitApiOnline.Align3D
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdElbow90_500 : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. User click 1 điểm trên Pipe/Duct
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    new PipeDuctPointFilter(),
                    "Click lên Pipe/Duct để vẽ elbow 90° + đoạn ống 500mm");

                Element e = doc.GetElement(r.ElementId);
                MEPCurve mep = e as MEPCurve;

                if (mep == null || !(mep is Pipe || mep is Duct))
                {
                    TaskDialog.Show("Elbow90", "Vui lòng click vào Pipe hoặc Duct.");
                    return Result.Failed;
                }

                XYZ pickPoint = r.GlobalPoint;

                using (Transaction t = new Transaction(doc, "Elbow 90° + 500mm"))
                {
                    t.Start();

                    // 2. Connector ở đầu ống gần điểm click nhất
                    Connector mainConn = GetConnectorClosestTo(mep, pickPoint);
                    if (mainConn == null)
                    {
                        message = "Không tìm thấy connector tại đầu ống.";
                        t.RollBack();
                        return Result.Failed;
                    }

                    Transform cs = mainConn.CoordinateSystem;

                    // Trục dọc ống
                    XYZ axisDir = cs.BasisZ.Normalize();
                    // Hai trục local quanh tiết diện
                    XYZ xDir = cs.BasisX.Normalize(); // "Right"
                    XYZ yDir = cs.BasisY.Normalize(); // "Up"

                    // 3. Vector từ connector tới vị trí click
                    XYZ offset = pickPoint - mainConn.Origin;

                    // Bỏ thành phần dọc ống -> chỉ còn vector quanh tiết diện
                    double along = offset.DotProduct(axisDir);
                    XYZ radial = offset - along * axisDir;
                    if (radial.GetLength() < 1e-6)
                    {
                        // Nếu click ngay tâm thì coi như click hướng +Y
                        radial = yDir;
                    }
                    radial = radial.Normalize();

                    // 4. Snap về 4 phía: ±X, ±Y
                    double dx = radial.DotProduct(xDir);
                    double dy = radial.DotProduct(yDir);

                    XYZ radialDir;
                    if (Math.Abs(dx) >= Math.Abs(dy))
                    {
                        radialDir = dx >= 0 ? xDir : -xDir;   // Right / Left
                    }
                    else
                    {
                        radialDir = dy >= 0 ? yDir : -yDir;   // Up / Down
                    }

                    // 5. Elbow 90°: nhánh vuông góc với trục ống ⇒ dùng luôn radialDir
                    XYZ branchDir = radialDir;

                    // 6. Độ dài 500mm -> feet
                    double lengthMm = 500.0;
                    double length = UnitUtils.ConvertToInternalUnits(
                        lengthMm, UnitTypeId.Millimeters);

                    XYZ endPoint = mainConn.Origin + branchDir * length;

                    bool ok = false;

                    // 7. Để Revit tự tạo ống + elbow từ connector đến endPoint
                    if (mep is Pipe pipe)
                    {
                        ElementId systemTypeId =
                            pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                                .AsElementId();
                        ElementId pipeTypeId = pipe.PipeType.Id;

                        try
                        {
                            Pipe branchPipe = Pipe.Create(
                                doc,
                                systemTypeId,
                                pipeTypeId,
                                mainConn,   // connector gốc
                                endPoint);  // điểm cuối nhánh

                            ok = branchPipe != null;
                        }
                        catch
                        {
                            ok = false;
                        }
                    }
                    else if (mep is Duct duct)
                    {
                        ElementId systemTypeId =
                            duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)
                                .AsElementId();
                        ElementId ductTypeId = duct.DuctType.Id;

                        try
                        {
                            Duct branchDuct = Duct.Create(
                                doc,
                                systemTypeId,
                                ductTypeId,
                                mainConn,   // connector gốc
                                endPoint);  // điểm cuối nhánh

                            ok = branchDuct != null;
                        }
                        catch
                        {
                            ok = false;
                        }
                    }

                    if (!ok)
                    {
                        TaskDialog.Show("Elbow90",
                            "Không tạo được elbow/nhánh.\n" +
                            "- Kiểm tra Routing Preferences của type (đã có elbow 90° chưa, có reducer lạ không).\n" +
                            "- Thử click lại phía khác quanh đầu ống.");
                        t.RollBack();
                        return Result.Failed;
                    }

                    t.Commit();
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Lấy connector của MEPCurve gần 1 điểm nhất (thường là 1 trong 2 đầu).
        /// </summary>
        private static Connector GetConnectorClosestTo(MEPCurve mep, XYZ point)
        {
            ConnectorManager cm = mep.ConnectorManager;
            if (cm == null) return null;

            Connector best = null;
            double minDist = double.MaxValue;

            foreach (Connector c in cm.Connectors)
            {
                if (c == null || c.ConnectorType == ConnectorType.Logical)
                    continue;

                double d = c.Origin.DistanceTo(point);
                if (d < minDist)
                {
                    minDist = d;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// Filter cho phép chọn Pipe/Duct.
        /// </summary>
        private class PipeDuctPointFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Pipe || elem is Duct;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}
