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
    public class CmdAlignBranch3D : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Selection sel = uidoc.Selection;

                // 1. Chọn ống/Duct TRỤC (click lên mặt top / mặt bên đều được)
                Reference rMain = sel.PickObject(
                    ObjectType.PointOnElement,
                    new MepCurveSelectionFilter(),
                    "Chọn ống/Duct TRỤC (click mặt top / side)");

                MEPCurve mainMep = doc.GetElement(rMain.ElementId) as MEPCurve;
                if (mainMep == null)
                {
                    TaskDialog.Show("Align Branch Center", "Phải chọn Pipe/Duct làm ống trục.");
                    return Result.Failed;
                }

                LocationCurve mainLoc = mainMep.Location as LocationCurve;
                Line mainLine = mainLoc?.Curve as Line;
                if (mainLine == null)
                {
                    TaskDialog.Show("Align Branch Center", "Chỉ hỗ trợ ống/duct thẳng (Line) làm ống trục.");
                    return Result.Failed;
                }

                using (TransactionGroup tg = new TransactionGroup(doc, "Align Branch Center"))
                {
                    tg.Start();

                    while (true)
                    {
                        Reference rBranch = null;
                        try
                        {
                            rBranch = sel.PickObject(
                                ObjectType.Element,
                                new MepCurveSelectionFilter(),
                                "Chọn ống/Duct NHÁNH cần move vào center (ESC để kết thúc)");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            // ESC -> kết thúc
                            break;
                        }

                        if (rBranch == null) break;

                        MEPCurve branch = doc.GetElement(rBranch.ElementId) as MEPCurve;
                        if (branch == null) continue;

                        using (Transaction t = new Transaction(doc, "Align branch to center"))
                        {
                            t.Start();

                            bool ok = AlignBranchToCenter(doc, mainLine, branch);
                            if (!ok)
                            {
                                TaskDialog.Show("Align Branch Center",
                                    "Không move được ống nhánh này (có thể không tìm được connector phù hợp).");
                            }

                            t.Commit();
                        }
                    }

                    tg.Assimilate();
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
        /// Move 1 ống nhánh sao cho connector gần ống trục nhất
        /// nằm đúng trên center line của ống trục.
        /// Không xoay, chỉ tịnh tiến => slope / hướng ống nhánh giữ nguyên.
        /// </summary>
        private static bool AlignBranchToCenter(Document doc, Line mainLine, MEPCurve branch)
        {
            // Connector của nhánh gần ống trục nhất -> pivot
            Connector pivotConn = GetConnectorClosestToAxis(branch, mainLine);
            if (pivotConn == null)
                return false;

            XYZ pivot = pivotConn.Origin;

            // Điểm chiếu pivot lên center line của ống trục
            IntersectionResult proj = mainLine.Project(pivot);
            if (proj == null)
                return false;

            XYZ target = proj.XYZPoint;

            // Vector move: đưa pivot về đúng center line
            XYZ move = target - pivot;
            if (move.GetLength() < 1e-6)
                return true; // đã nằm trên center rồi

            ElementTransformUtils.MoveElement(doc, branch.Id, move);
            return true;
        }

        /// <summary>
        /// Lấy connector của MEPCurve gần center line (trục chính) nhất.
        /// </summary>
        private static Connector GetConnectorClosestToAxis(MEPCurve mep, Line axis)
        {
            ConnectorManager cm = mep.ConnectorManager;
            if (cm == null) return null;

            Connector best = null;
            double minDist = double.MaxValue;

            foreach (Connector c in cm.Connectors)
            {
                if (c == null || c.ConnectorType == ConnectorType.Logical)
                    continue;

                // Khoảng cách từ điểm connector đến line trục
                IntersectionResult proj = axis.Project(c.Origin);
                if (proj == null) continue;

                double d = c.Origin.DistanceTo(proj.XYZPoint);
                if (d < minDist)
                {
                    minDist = d;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// Cho phép chọn mọi MEPCurve (Pipe, Duct, Pipe slope ...).
        /// </summary>
        private class MepCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is MEPCurve;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}
