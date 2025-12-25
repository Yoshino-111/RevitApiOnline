using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitApiOnline.Align3D
{
    [Transaction(TransactionMode.Manual)]
    public class CmdWyeBranch500 : IExternalCommand
    {
        private const double BranchLenMm = 500.0;
        private const double MmToFt = 1.0 / 304.8;
        private const double AngleDeg = 45.0;
        private const double EPS = 1e-8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    new PipePointFilter(),
                    "Click 1 điểm trên ống (bên trái/phải) để tạo Wye 45°"
                );

                Pipe mainPipe = doc.GetElement(r) as Pipe;
                if (mainPipe == null) return Result.Failed;

                XYZ clickPt = r.GlobalPoint;

                LocationCurve lc = mainPipe.Location as LocationCurve;
                if (lc == null) return Result.Failed;

                Curve mainCurve = lc.Curve;
                XYZ proj = mainCurve.Project(clickPt)?.XYZPoint ?? clickPt;

                XYZ mainDir = GetTangent(mainCurve, proj) ?? GetCurveDir(mainCurve);
                if (mainDir == null || mainDir.GetLength() < EPS) mainDir = XYZ.BasisX;
                mainDir = mainDir.Normalize();
                if (mainDir.Z < 0) mainDir = -mainDir;

                double slope = GetPipeSlope(mainPipe);
                if (slope < 0) slope = -slope;

                View v = uidoc.ActiveView;
                XYZ viewRight = v.RightDirection.Normalize();

                XYZ side0 = viewRight - mainDir.Multiply(viewRight.DotProduct(mainDir));
                if (side0.GetLength() < EPS)
                {
                    XYZ viewUp = v.UpDirection.Normalize();
                    side0 = viewUp - mainDir.Multiply(viewUp.DotProduct(mainDir));
                }
                if (side0.GetLength() < EPS) side0 = XYZ.BasisZ.CrossProduct(mainDir);
                if (side0.GetLength() < EPS) side0 = XYZ.BasisX;
                side0 = side0.Normalize();

                XYZ vPick = clickPt - proj;
                double sign = (vPick.DotProduct(side0) >= 0) ? 1.0 : -1.0;
                XYZ side = side0.Multiply(sign).Normalize();

                double a = AngleDeg * Math.PI / 180.0;
                XYZ branch45 = (mainDir.Multiply(Math.Cos(a)) + side.Multiply(Math.Sin(a))).Normalize();

                XYZ branchXY = new XYZ(branch45.X, branch45.Y, 0);
                if (branchXY.GetLength() < EPS) branchXY = new XYZ(side.X, side.Y, 0);
                if (branchXY.GetLength() < EPS) branchXY = XYZ.BasisX;
                branchXY = branchXY.Normalize();

                XYZ branchDir = (slope > 1e-10)
                    ? (branchXY + XYZ.BasisZ.Multiply(slope)).Normalize()
                    : branch45;

                if (branchDir.Z < 0) branchDir = new XYZ(branchDir.X, branchDir.Y, -branchDir.Z).Normalize();
                if (branchDir.Z < 1e-6) branchDir = (branchDir + XYZ.BasisZ).Normalize();

                double lenFt = BranchLenMm * MmToFt;
                XYZ branchEnd = proj + branchDir.Multiply(lenFt);

                using (Transaction t = new Transaction(doc, "Create Wye Branch 500 (Stable)"))
                {
                    t.Start();

                    ElementId newSegId = PlumbingUtils.BreakCurve(doc, mainPipe.Id, proj);
                    Pipe seg1 = mainPipe;
                    Pipe seg2 = doc.GetElement(newSegId) as Pipe;

                    ElementId sysTypeId = seg1.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                                         ?? ElementId.InvalidElementId;
                    ElementId typeId = seg1.GetTypeId();
                    ElementId levelId = seg1.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)?.AsElementId()
                                      ?? seg1.LevelId;

                    if (sysTypeId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Không lấy được System Type của ống.");
                    if (levelId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Không lấy được Level của ống.");

                    Pipe branch = Pipe.Create(doc, sysTypeId, typeId, levelId, proj, branchEnd);
                    CopyDiameter(seg1, branch);
                    ApplySlopeToPipe(branch, slope);

                    FamilySymbol junctionSym = TryGetJunctionSymbolFromPipeType(doc, seg1);
                    if (junctionSym == null)
                    {
                        TryAutoConnect(seg1, seg2, branch, proj);
                        t.Commit();
                        TaskDialog.Show("Info",
                            "Không lấy được Junction family từ Routing Preferences.\n" +
                            "Đã fallback auto connect (có thể ra Tee).");
                        return Result.Succeeded;
                    }

                    if (!junctionSym.IsActive) junctionSym.Activate();

                    Level lv = doc.GetElement(levelId) as Level;
                    FamilyInstance fit = (lv != null)
                        ? doc.Create.NewFamilyInstance(proj, junctionSym, lv, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(proj, junctionSym, StructuralType.NonStructural);

                    doc.Regenerate();

                    bool oriented = OrientJunctionToDirections(doc, fit, proj, mainDir, branchDir);
                    doc.Regenerate();

                    bool connected = ConnectThreePipesToJunction(fit, seg1, seg2, branch, proj);

                    if (!connected)
                    {
                        TryAutoConnect(seg1, seg2, branch, proj);
                        TaskDialog.Show("Info",
                            "Đã place Junction nhưng connect không thành công.\n" +
                            "Đã fallback auto connect (có thể ra Tee).");
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

        private class PipePointFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private static XYZ GetCurveDir(Curve c)
        {
            XYZ v = c.GetEndPoint(1) - c.GetEndPoint(0);
            return v.GetLength() < EPS ? null : v.Normalize();
        }

        private static XYZ GetTangent(Curve c, XYZ p)
        {
            var ir = c.Project(p);
            if (ir == null) return null;
            XYZ t = c.ComputeDerivatives(ir.Parameter, false).BasisX;
            if (t == null || t.GetLength() < EPS) return null;
            return t.Normalize();
        }

        private static double GetPipeSlope(Pipe p)
        {
            Parameter sp = p.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
            if (sp != null)
            {
                try { return sp.AsDouble(); } catch { }
            }
            return 0.0;
        }

        private static void ApplySlopeToPipe(Pipe p, double slope)
        {
            Parameter sp = p.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
            if (sp != null && !sp.IsReadOnly)
            {
                try { sp.Set(slope); } catch { }
            }
        }

        private static void CopyDiameter(Pipe src, Pipe dst)
        {
            Parameter d1 = src.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            Parameter d2 = dst.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (d1 != null && d2 != null && !d2.IsReadOnly)
            {
                try { d2.Set(d1.AsDouble()); } catch { }
            }
        }

        private static FamilySymbol TryGetJunctionSymbolFromPipeType(Document doc, Pipe pipe)
        {
            PipeType pt = doc.GetElement(pipe.GetTypeId()) as PipeType;
            if (pt == null) return null;

            RoutingPreferenceManager rpm = pt.RoutingPreferenceManager;
            if (rpm == null) return null;

            int n = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions);
            for (int i = 0; i < n; i++)
            {
                RoutingPreferenceRule rule = rpm.GetRule(RoutingPreferenceRuleGroupType.Junctions, i);
                if (rule == null) continue;

                ElementId partId = rule.MEPPartId;
                if (partId == ElementId.InvalidElementId) continue;

                FamilySymbol fs = doc.GetElement(partId) as FamilySymbol;
                if (fs != null) return fs;
            }
            return null;
        }

        private static List<Connector> GetFittingConnectors(FamilyInstance fi)
        {
            var cm = fi?.MEPModel?.ConnectorManager;
            if (cm == null) return new List<Connector>();
            return cm.Connectors.Cast<Connector>().Where(x => x.ConnectorType == ConnectorType.End).ToList();
        }

        private static Connector GetClosestConnector(MEPCurve c, XYZ p)
        {
            if (c?.ConnectorManager == null) return null;
            return c.ConnectorManager.Connectors
                .Cast<Connector>()
                .OrderBy(x => x.Origin.DistanceTo(p))
                .FirstOrDefault();
        }

        private static XYZ Dir(Connector c)
        {
            try
            {
                XYZ v = c.CoordinateSystem.BasisZ;
                if (v == null || v.GetLength() < EPS) return XYZ.BasisX;
                return v.Normalize();
            }
            catch { return XYZ.BasisX; }
        }

        private static double Clamp(double x) => Math.Max(-1.0, Math.Min(1.0, x));

        private static double SignedAngleOnAxis(XYZ from, XYZ to, XYZ axis)
        {
            from = from.Normalize();
            to = to.Normalize();
            axis = axis.Normalize();

            double cos = Clamp(from.DotProduct(to));
            double angle = Math.Acos(cos);

            double s = axis.DotProduct(from.CrossProduct(to));
            return (s >= 0) ? angle : -angle;
        }

        // ✅ FIX CS1628: không dùng lambda với out params
        // ✅ FIX: Connector.Id là int (không phải ElementId)
        private static void IdentifyRunAndBranch(List<Connector> fcs, out Connector runA, out Connector runB, out Connector br)
        {
            runA = null; runB = null; br = null;
            if (fcs == null || fcs.Count < 3) return;

            double best = -1.0;
            int ia = 0, ib = 1;

            for (int i = 0; i < fcs.Count; i++)
            {
                for (int j = i + 1; j < fcs.Count; j++)
                {
                    double v = Math.Abs(Dir(fcs[i]).DotProduct(Dir(fcs[j])));
                    if (v > best)
                    {
                        best = v;
                        ia = i;
                        ib = j;
                    }
                }
            }

            runA = fcs[ia];
            runB = fcs[ib];

            int idA = runA.Id;   // ✅ int
            int idB = runB.Id;   // ✅ int

            for (int k = 0; k < fcs.Count; k++)
            {
                int id = fcs[k].Id;
                if (id != idA && id != idB)
                {
                    br = fcs[k];
                    break;
                }
            }
        }


        private static bool OrientJunctionToDirections(Document doc, FamilyInstance fit, XYZ at, XYZ mainDir, XYZ branchDir)
        {
            var fcs = GetFittingConnectors(fit);
            if (fcs.Count < 3) return false;

            IdentifyRunAndBranch(fcs, out Connector runA, out Connector runB, out Connector br);
            if (runA == null || runB == null || br == null) return false;

            XYZ runAxis0 = (runB.Origin - runA.Origin);
            if (runAxis0.GetLength() < EPS) runAxis0 = Dir(runA);
            runAxis0 = runAxis0.Normalize();

            if (runAxis0.DotProduct(mainDir) < 0) runAxis0 = -runAxis0;

            double dot = Clamp(runAxis0.DotProduct(mainDir));
            double angle1 = Math.Acos(dot);

            XYZ axis1 = runAxis0.CrossProduct(mainDir);
            if (axis1.GetLength() < EPS)
            {
                if (dot < -0.999)
                {
                    axis1 = mainDir.CrossProduct(XYZ.BasisX);
                    if (axis1.GetLength() < EPS) axis1 = mainDir.CrossProduct(XYZ.BasisY);
                    axis1 = axis1.Normalize();
                    angle1 = Math.PI;
                }
                else angle1 = 0;
            }
            else axis1 = axis1.Normalize();

            if (Math.Abs(angle1) > 1e-6)
            {
                Line axLine = Line.CreateUnbound(at, axis1);
                ElementTransformUtils.RotateElement(doc, fit.Id, axLine, angle1);
                doc.Regenerate();
            }

            fcs = GetFittingConnectors(fit);
            if (fcs.Count < 3) return false;
            IdentifyRunAndBranch(fcs, out runA, out runB, out br);
            if (br == null) return false;

            XYZ brDirNow = Dir(br);

            XYZ brP = brDirNow - mainDir.Multiply(brDirNow.DotProduct(mainDir));
            XYZ targetP = branchDir - mainDir.Multiply(branchDir.DotProduct(mainDir));

            if (brP.GetLength() < EPS || targetP.GetLength() < EPS) return true;

            brP = brP.Normalize();
            targetP = targetP.Normalize();

            double angle2 = SignedAngleOnAxis(brP, targetP, mainDir);
            if (Math.Abs(angle2) > 1e-6)
            {
                Line axLine2 = Line.CreateUnbound(at, mainDir);
                ElementTransformUtils.RotateElement(doc, fit.Id, axLine2, angle2);
                doc.Regenerate();
            }

            return true;
        }

        private static bool ConnectThreePipesToJunction(FamilyInstance fit, Pipe seg1, Pipe seg2, Pipe branch, XYZ at)
        {
            var fcs = GetFittingConnectors(fit);
            if (fcs.Count < 3) return false;

            IdentifyRunAndBranch(fcs, out Connector runA, out Connector runB, out Connector br);
            if (runA == null || runB == null || br == null) return false;

            Connector p1 = GetClosestConnector(seg1, at);
            Connector p2 = seg2 != null ? GetClosestConnector(seg2, at) : null;
            Connector pb = GetClosestConnector(branch, at);

            if (p1 == null || pb == null) return false;

            XYZ p1d = Dir(p1);
            double s11 = Math.Abs(p1d.DotProduct(Dir(runA)));
            double s12 = Math.Abs(p1d.DotProduct(Dir(runB)));

            Connector runForP1 = (s11 >= s12) ? runA : runB;
            Connector runForP2 = (runForP1.Id == runA.Id) ? runB : runA;

            bool ok = true;
            ok &= TryConnect(pb, br);
            ok &= TryConnect(p1, runForP1);
            if (p2 != null) ok &= TryConnect(p2, runForP2);

            return ok;
        }

        private static bool TryConnect(Connector a, Connector b)
        {
            try
            {
                if (a == null || b == null) return false;
                a.ConnectTo(b);
                return true;
            }
            catch { return false; }
        }

        private static void TryAutoConnect(Pipe seg1, Pipe seg2, Pipe branch, XYZ at)
        {
            Connector cMain = GetClosestConnector(seg1, at) ?? (seg2 != null ? GetClosestConnector(seg2, at) : null);
            Connector cBranch = GetClosestConnector(branch, at);
            if (cMain == null || cBranch == null) return;

            try { cBranch.ConnectTo(cMain); } catch { }
        }
    }
}
