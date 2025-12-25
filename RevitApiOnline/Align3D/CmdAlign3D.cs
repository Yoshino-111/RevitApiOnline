using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitApiOnline.Align3D
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdAlign3D : IExternalCommand
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

                // 1. Pick element chuẩn (destination)
                Reference rDest = sel.PickObject(
                    ObjectType.Element,
                    new MepConnectorElementFilter(),
                    "Chọn ống/duct/fab làm chuẩn (destination)");
                Element eDest = doc.GetElement(rDest);

                using (TransactionGroup tg = new TransactionGroup(doc, "Align 3D MEP"))
                {
                    tg.Start();

                    while (true)
                    {
                        Reference rSource = null;

                        try
                        {
                            rSource = sel.PickObject(
                                ObjectType.Element,
                                new MepConnectorElementFilter(),
                                "Chọn element cần align (ESC để thoát)");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            // Người dùng ESC -> kết thúc
                            break;
                        }

                        if (rSource == null) break;

                        Element eSource = doc.GetElement(rSource);

                        using (Transaction t = new Transaction(doc, "Align 3D"))
                        {
                            t.Start();

                            bool ok = AlignElementToReference(doc, eDest, eSource);
                            if (!ok)
                            {
                                TaskDialog.Show("Align 3D",
                                    "Không tìm thấy connector phù hợp để align.");
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
        /// Align eSource cho trùng 3D với eDest dựa trên 2 connector gần nhất.
        /// </summary>
        private bool AlignElementToReference(Document doc, Element dest, Element source)
        {
            ConnectorPair pair = FindClosestConnectors(dest, source);
            if (pair == null || pair.Dest == null || pair.Source == null)
                return false;

            Connector cDest = pair.Dest;
            Connector cSource = pair.Source;

            XYZ originDest = cDest.Origin;
            XYZ originSource = cSource.Origin;

            Transform csDest = cDest.CoordinateSystem;
            Transform csSource = cSource.CoordinateSystem;

            // Hướng connector (BasisZ là hướng flow)
            XYZ dirDest = csDest.BasisZ.Normalize();
            XYZ dirSource = csSource.BasisZ.Normalize();

            // Muốn connector source quay ngược hướng dest (ready to connect)
            XYZ vFrom = dirSource;
            XYZ vTo = -dirDest;

            // 1. ROTATE source
            double dot = vFrom.DotProduct(vTo);
            dot = Math.Max(-1.0, Math.Min(1.0, dot)); // clamp
            double angle = Math.Acos(dot);

            if (angle > 1e-4)
            {
                XYZ axis = vFrom.CrossProduct(vTo);
                if (axis.GetLength() < 1e-6)
                {
                    // vFrom và vTo song song: chọn 1 trục bất kỳ vuông góc để quay 180°
                    XYZ fallback = XYZ.BasisZ;
                    if (Math.Abs(vFrom.DotProduct(fallback)) > 0.99)
                        fallback = XYZ.BasisX;

                    axis = vFrom.CrossProduct(fallback);
                }

                axis = axis.Normalize();
                Line axisLine = Line.CreateUnbound(originSource, axis);

                ElementTransformUtils.RotateElement(doc, source.Id, axisLine, angle);
            }

            // 2. MOVE (tịnh tiến)
            // Sau khi xoay quanh originSource, originSource vẫn giữ nguyên → chỉ cần move
            XYZ translation = originDest - originSource;
            if (translation.GetLength() > 1e-6)
            {
                ElementTransformUtils.MoveElement(doc, source.Id, translation);
            }

            return true;
        }

        /// <summary>
        /// Chứa cặp connector gần nhất giữa 2 element.
        /// </summary>
        private class ConnectorPair
        {
            public Connector Dest { get; set; }
            public Connector Source { get; set; }
        }

        private ConnectorPair FindClosestConnectors(Element dest, Element source)
        {
            IList<Connector> destCons = GetAllConnectors(dest);
            IList<Connector> sourceCons = GetAllConnectors(source);

            if (destCons.Count == 0 || sourceCons.Count == 0)
                return null;

            double minDist = double.MaxValue;
            Connector bestDest = null;
            Connector bestSource = null;

            foreach (Connector cd in destCons)
            {
                foreach (Connector cs in sourceCons)
                {
                    double d = cd.Origin.DistanceTo(cs.Origin);
                    if (d < minDist)
                    {
                        minDist = d;
                        bestDest = cd;
                        bestSource = cs;
                    }
                }
            }

            if (bestDest == null || bestSource == null)
                return null;

            return new ConnectorPair
            {
                Dest = bestDest,
                Source = bestSource
            };
        }

        /// <summary>
        /// Lấy tất cả connector "thật" của element (duct/pipe/fab/family MEP).
        /// </summary>
        private IList<Connector> GetAllConnectors(Element e)
        {
            var list = new List<Connector>();

            // MEPCurve: Pipe, Duct, Conduit, CableTray,...
            if (e is MEPCurve mepCurve)
            {
                foreach (Connector c in mepCurve.ConnectorManager.Connectors)
                {
                    if (c != null && c.ConnectorType != ConnectorType.Logical)
                        list.Add(c);
                }
            }

            // FamilyInstance với MEPModel
            if (e is FamilyInstance fi && fi.MEPModel != null)
            {
                ConnectorManager cm = fi.MEPModel.ConnectorManager;
                if (cm != null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c != null && c.ConnectorType != ConnectorType.Logical)
                            list.Add(c);
                    }
                }
            }

            // FabricationPart
            if (e is FabricationPart fab)
            {
                foreach (Connector c in fab.ConnectorManager.Connectors)
                {
                    if (c != null && c.ConnectorType != ConnectorType.Logical)
                        list.Add(c);
                }
            }

            return list;
        }

        /// <summary>
        /// Selection filter: chỉ cho pick element có connector MEP.
        /// </summary>
        private class MepConnectorElementFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is MEPCurve) return true;
                if (elem is FabricationPart) return true;

                if (elem is FamilyInstance fi &&
                    fi.MEPModel != null &&
                    fi.MEPModel.ConnectorManager != null)
                    return true;

                return false;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}
