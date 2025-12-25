using System;
using System.Linq;
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
    public class CmdElbow45_500 : IExternalCommand
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
                    "Click lên Pipe/Duct để vẽ elbow 45° + đoạn ống 500mm");

                Element e = doc.GetElement(r.ElementId);
                MEPCurve mep = e as MEPCurve;

                if (mep == null || !(mep is Pipe || mep is Duct))
                {
                    TaskDialog.Show("Elbow45", "Vui lòng click vào Pipe hoặc Duct.");
                    return Result.Failed;
                }

                XYZ pickPoint = r.GlobalPoint;

                using (Transaction t = new Transaction(doc, "Elbow 45° + 500mm"))
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

                    // 5. Hướng nhánh 45° giữa trục ống và hướng click
                    double angle = Math.PI / 4.0; // 45°
                    double c = Math.Cos(angle);
                    double s = Math.Sin(angle);
                    XYZ branchDir = (axisDir * c + radialDir * s).Normalize();

                    // 6. Độ dài 500mm -> feet
                    double lengthMm = 500.0;
                    double length = UnitUtils.ConvertToInternalUnits(
                        lengthMm, UnitTypeId.Millimeters);

                    XYZ startPoint = mainConn.Origin;
                    XYZ endPoint = startPoint + branchDir * length;

                    bool ok = false;

                    if (mep is Pipe pipe)
                    {
                        // Lấy system type từ pipe hiện tại
                        ElementId systemTypeId =
                            pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                                .AsElementId();
                        ElementId pipeTypeId = pipe.PipeType.Id;
                        ElementId levelId = mep.ReferenceLevel.Id;

                        // 7. Tạo đoạn pipe branch từ 2 điểm (chưa có fitting)
                        Pipe branchPipe = Pipe.Create(
                            doc,
                            systemTypeId,
                            pipeTypeId,
                            levelId,
                            startPoint,
                            endPoint);

                        // 7.1. Ép size của branch = size ống gốc (tránh bị giảm)
                        MatchSize(mep, branchPipe);

                        // 8. Tạo elbow giữa mainConn và 1 connector của branchPipe
                        ok = TryConnectWithElbow(doc, mainConn, branchPipe.ConnectorManager);
                        if (!ok)
                        {
                            // Nếu fail thì xóa đoạn branch vừa tạo
                            doc.Delete(branchPipe.Id);
                        }
                    }
                    else if (mep is Duct duct)
                    {
                        ElementId systemTypeId =
                            duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)
                                .AsElementId();
                        ElementId ductTypeId = duct.DuctType.Id;
                        ElementId levelId = mep.ReferenceLevel.Id;

                        Duct branchDuct = Duct.Create(
                            doc,
                            systemTypeId,
                            ductTypeId,
                            levelId,
                            startPoint,
                            endPoint);

                        // 7.1. Ép size branch = size duct gốc
                        MatchSize(mep, branchDuct);

                        ok = TryConnectWithElbow(doc, mainConn, branchDuct.ConnectorManager);
                        if (!ok)
                        {
                            doc.Delete(branchDuct.Id);
                        }
                    }

                    if (!ok)
                    {
                        TaskDialog.Show("Elbow45",
                            "Không tạo được elbow.\n" +
                            "- Kiểm tra lại Routing Preferences của type (đã có elbow 45° chưa).\n" +
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
        /// Copy size (đường kính / rộng / cao) từ MEPCurve gốc sang nhánh,
        /// để tránh Revit chèn thêm reducer.
        /// </summary>
        private static void MatchSize(MEPCurve main, MEPCurve branch)
        {
            if (main is Pipe && branch is Pipe)
            {
                Parameter mainDia = main.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                Parameter brDia = branch.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);

                if (mainDia != null && brDia != null && !brDia.IsReadOnly)
                {
                    brDia.Set(mainDia.AsDouble());
                }
            }
            else if (main is Duct && branch is Duct)
            {
                // Thử copy đường kính (round duct)
                Parameter mainDia = main.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                Parameter brDia = branch.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

                if (mainDia != null && brDia != null &&
                    mainDia.AsDouble() > 0 && !brDia.IsReadOnly)
                {
                    brDia.Set(mainDia.AsDouble());
                }
                else
                {
                    // Nếu không phải round (rectangular) thì copy Width/Height
                    Parameter mainW = main.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    Parameter mainH = main.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                    Parameter brW = branch.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    Parameter brH = branch.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                    if (mainW != null && mainH != null &&
                        brW != null && brH != null &&
                        !brW.IsReadOnly && !brH.IsReadOnly)
                    {
                        brW.Set(mainW.AsDouble());
                        brH.Set(mainH.AsDouble());
                    }
                }
            }
        }

        /// <summary>
        /// Tạo elbow bằng NewElbowFitting giữa mainConn và 1 connector của branch.
        /// </summary>
        private static bool TryConnectWithElbow(
            Document doc,
            Connector mainConn,
            ConnectorManager branchCm)
        {
            var branchCons = branchCm.Connectors
                .Cast<Connector>()
                .Where(c => c != null && c.ConnectorType != ConnectorType.Logical)
                .ToList();

            foreach (Connector bc in branchCons)
            {
                try
                {
                    doc.Create.NewElbowFitting(mainConn, bc);
                    return true;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // pair này không hợp lệ -> thử connector khác
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    // góc / khoảng cách không hợp lệ -> thử connector khác
                }
            }

            return false;
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
