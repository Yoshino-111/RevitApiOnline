using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitApiOnline.Insulation;
using System;
using System.Collections.Generic;

namespace RevitApiOnline.Insulation
{
    internal class ApplyInsulationHandler : IExternalEventHandler
    {
        // tolerance cho so sánh chiều dài (ft) ~0.03 mm
        private const double LENGTH_TOL = 1e-4;

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Insulation", "Không có active document.");
                return;
            }

            Document doc = uidoc.Document;
            View activeView = uidoc.ActiveView;

            var form = PipeDuctInsulationAppShow.Form;
            if (form == null)
                return;

            var data = form.DataContext as PipeDuctInsulationDataContext;
            if (data == null)
            {
                TaskDialog.Show("Insulation", "DataContext null – kiểm tra lại khi gán DataContext.");
                return;
            }

            bool scopeActiveView = (data.ScopeOption == 1);
            if (scopeActiveView && (activeView == null || !activeView.IsValidObject))
            {
                scopeActiveView = false;
            }

            if (!data.EnablePipe &&
                !data.EnablePipeFittings &&
                !data.EnableDuctRound &&
                !data.EnableDuctRect &&
                !data.EnableDuctFittings)
            {
                TaskDialog.Show("Insulation", "Bạn chưa chọn đối tượng nào để bọc cách nhiệt.");
                return;
            }

            // Type ids (có thể Invalid nếu user không chọn)
            ElementId pipeInsTypeId = data.SelectedPipeInsulationType != null
                ? data.SelectedPipeInsulationType.TypeId
                : ElementId.InvalidElementId;

            ElementId ductInsTypeId = data.SelectedDuctInsulationType != null
                ? data.SelectedDuctInsulationType.TypeId
                : ElementId.InvalidElementId;

            // ---- convert mm -> ft ----
            double pipeSizeFt =
                UnitUtils.ConvertToInternalUnits(data.PipeMinSizeMm, UnitTypeId.Millimeters);
            double pipeThkFt =
                UnitUtils.ConvertToInternalUnits(data.PipeInsulationThicknessMm, UnitTypeId.Millimeters);
            double pipeFittingThkFt =
                UnitUtils.ConvertToInternalUnits(data.PipeFittingInsulationThicknessMm, UnitTypeId.Millimeters);

            double ductRoundSizeFt =
                UnitUtils.ConvertToInternalUnits(data.DuctRoundMinDiameterMm, UnitTypeId.Millimeters);
            double ductRectWidthFt =
                UnitUtils.ConvertToInternalUnits(data.DuctRectMinWidthMm, UnitTypeId.Millimeters);
            double ductRectHeightFt =
                UnitUtils.ConvertToInternalUnits(data.DuctRectMinHeightMm, UnitTypeId.Millimeters);

            double ductThkFt =
                UnitUtils.ConvertToInternalUnits(data.DuctInsulationThicknessMm, UnitTypeId.Millimeters);
            double ductFittingThkFt =
                UnitUtils.ConvertToInternalUnits(data.DuctFittingInsulationThicknessMm, UnitTypeId.Millimeters);

            using (TransactionGroup tg = new TransactionGroup(doc, "Pipe & Duct Insulation"))
            {
                tg.Start();

                try
                {
                    // PIPES
                    if (data.EnablePipe && pipeThkFt > 0)
                    {
                        using (Transaction t = new Transaction(doc, "Pipe insulation"))
                        {
                            t.Start();
                            ApplyPipeInsulationToPipes(
                                doc, activeView, scopeActiveView,
                                pipeSizeFt, pipeThkFt, data.PipeSystemNameFilter, pipeInsTypeId);
                            t.Commit();
                        }
                    }

                    // PIPE FITTINGS
                    if (data.EnablePipeFittings && pipeFittingThkFt > 0)
                    {
                        using (Transaction t = new Transaction(doc, "Pipe fitting insulation"))
                        {
                            t.Start();
                            ApplyPipeInsulationToFittings(
                                doc, activeView, scopeActiveView,
                                pipeSizeFt, pipeFittingThkFt, data.PipeFittingSystemNameFilter, pipeInsTypeId);
                            t.Commit();
                        }
                    }

                    // DUCTS
                    if ((data.EnableDuctRound || data.EnableDuctRect) && ductThkFt > 0)
                    {
                        using (Transaction t = new Transaction(doc, "Duct insulation"))
                        {
                            t.Start();
                            ApplyDuctInsulationToDucts(
                                doc, activeView, scopeActiveView,
                                data.EnableDuctRound, ductRoundSizeFt,
                                data.EnableDuctRect, ductRectWidthFt, ductRectHeightFt,
                                ductThkFt, data.DuctSystemNameFilter, ductInsTypeId);
                            t.Commit();
                        }
                    }

                    // DUCT FITTINGS
                    if (data.EnableDuctFittings && ductFittingThkFt > 0)
                    {
                        using (Transaction t = new Transaction(doc, "Duct fitting insulation"))
                        {
                            t.Start();
                            ApplyDuctInsulationToFittings(
                                doc, activeView, scopeActiveView,
                                data.EnableDuctRound, ductRoundSizeFt,
                                data.EnableDuctRect, ductRectWidthFt, ductRectHeightFt,
                                ductFittingThkFt, data.DuctFittingSystemNameFilter, ductInsTypeId);
                            t.Commit();
                        }
                    }

                    tg.Assimilate();
                    TaskDialog.Show("Insulation", "Đã bọc cách nhiệt xong (xem lại trong model / view).");
                }
                catch (Exception ex)
                {
                    tg.RollBack();
                    TaskDialog.Show("Insulation - Error", ex.Message);
                }
            }
        }

        public string GetName()
        {
            return "ApplyPipeDuctInsulationHandler";
        }

        // ===== COMMON HELPERS =====

        private static FilteredElementCollector CreateCollector(Document doc, View view, bool activeViewOnly)
        {
            if (activeViewOnly && view != null && view.IsValidObject)
                return new FilteredElementCollector(doc, view.Id);

            return new FilteredElementCollector(doc);
        }

        private static bool PassSystemNameFilter(Element e, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            string sysName = null;
            Parameter p = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
            if (p != null && p.StorageType == StorageType.String)
                sysName = p.AsString();

            if (string.IsNullOrEmpty(sysName))
                return false;

            return sysName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsEqualLength(double a, double b)
        {
            return Math.Abs(a - b) <= LENGTH_TOL;
        }

        private static double GetDoubleParam(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            if (p != null && p.StorageType == StorageType.Double)
                return p.AsDouble();
            return 0.0;
        }

        private static HashSet<long> GetPipeInsulationHosts(Document doc)
        {
            HashSet<long> result = new HashSet<long>();

            FilteredElementCollector coll =
                new FilteredElementCollector(doc).OfClass(typeof(PipeInsulation));

            foreach (Element e in coll)
            {
                PipeInsulation ins = e as PipeInsulation;
                if (ins == null || !ins.IsValidObject) continue;

                result.Add(ins.HostElementId.IntegerValue);
            }

            return result;
        }

        private static HashSet<long> GetDuctInsulationHosts(Document doc)
        {
            HashSet<long> result = new HashSet<long>();

            FilteredElementCollector coll =
                new FilteredElementCollector(doc).OfClass(typeof(DuctInsulation));

            foreach (Element e in coll)
            {
                DuctInsulation ins = e as DuctInsulation;
                if (ins == null || !ins.IsValidObject) continue;

                result.Add(ins.HostElementId.IntegerValue);
            }

            return result;
        }

        /// <summary>
        /// Lấy size fitting qua connectors:
        ///   - roundDiameterFt = đường kính lớn nhất
        ///   - rectWidth/Height = kích thước lớn nhất với connector chữ nhật
        /// </summary>
        private static void GetFittingConnectorSizes(
            Element e,
            out bool hasRound, out double roundDiameterFt,
            out bool hasRect, out double rectWidthFt, out double rectHeightFt)
        {
            hasRound = false;
            hasRect = false;
            roundDiameterFt = 0.0;
            rectWidthFt = 0.0;
            rectHeightFt = 0.0;

            FamilyInstance fi = e as FamilyInstance;
            if (fi == null) return;
            MEPModel mepModel = fi.MEPModel;
            if (mepModel == null) return;
            ConnectorManager cm = mepModel.ConnectorManager;
            if (cm == null) return;

            double maxRadius = 0.0;
            double maxWidth = 0.0;
            double maxHeight = 0.0;

            foreach (Connector c in cm.Connectors)
            {
                if (c == null) continue;

                if (c.Shape == ConnectorProfileType.Round)
                {
                    hasRound = true;
                    if (c.Radius > maxRadius)
                        maxRadius = c.Radius;
                }
                else if (c.Shape == ConnectorProfileType.Rectangular)
                {
                    hasRect = true;
                    if (c.Width > maxWidth)
                        maxWidth = c.Width;
                    if (c.Height > maxHeight)
                        maxHeight = c.Height;
                }
            }

            if (hasRound)
                roundDiameterFt = 2.0 * maxRadius;
            if (hasRect)
            {
                rectWidthFt = maxWidth;
                rectHeightFt = maxHeight;
            }
        }
        /// <summary>
        /// Kiểm tra xem fitting có connector chữ nhật nào đúng cặp (width, height)
        /// (cho phép width/height đảo vị trí, và có tolerance).
        /// </summary>
        private static bool HasRectConnectorWithSize(
            Element e,
            double targetWidthFt,
            double targetHeightFt)
        {
            if (targetWidthFt <= 0 && targetHeightFt <= 0)
                return false;

            FamilyInstance fi = e as FamilyInstance;
            if (fi == null) return false;
            MEPModel mepModel = fi.MEPModel;
            if (mepModel == null) return false;
            ConnectorManager cm = mepModel.ConnectorManager;
            if (cm == null) return false;

            foreach (Connector c in cm.Connectors)
            {
                if (c == null) continue;
                if (c.Shape != ConnectorProfileType.Rectangular) continue;

                double w = c.Width;
                double h = c.Height;

                // trùng đúng (W,H)
                bool direct =
                    IsEqualLength(w, targetWidthFt) &&
                    IsEqualLength(h, targetHeightFt);

                // hoặc (H,W) – trường hợp connector quay trục
                bool swapped =
                    IsEqualLength(w, targetHeightFt) &&
                    IsEqualLength(h, targetWidthFt);

                if (direct || swapped)
                    return true;
            }

            return false;
        }

        // ===== PIPE =====

        private void ApplyPipeInsulationToPipes(
            Document doc, View view, bool activeViewOnly,
            double targetDiameterFt, double thicknessFt, string systemFilter,
            ElementId insTypeId)
        {
            HashSet<long> insulatedHosts = GetPipeInsulationHosts(doc);

            FilteredElementCollector coll = CreateCollector(doc, view, activeViewOnly)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType();

            foreach (Element e in coll)
            {
                Pipe pipe = e as Pipe;
                if (pipe == null || !pipe.IsValidObject) continue;

                if (!PassSystemNameFilter(pipe, systemFilter))
                    continue;

                Parameter diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diamParam == null) continue;

                double diameterFt = diamParam.AsDouble();
                if (diameterFt <= 0) continue;

                if (targetDiameterFt > 0 && !IsEqualLength(diameterFt, targetDiameterFt))
                    continue;

                if (insulatedHosts.Contains(pipe.Id.IntegerValue))
                    continue;

                PipeInsulation.Create(doc, pipe.Id, insTypeId, thicknessFt);
                insulatedHosts.Add(pipe.Id.IntegerValue);
            }
        }

        private void ApplyPipeInsulationToFittings(
            Document doc, View view, bool activeViewOnly,
            double targetDiameterFt, double thicknessFt, string systemFilter,
            ElementId insTypeId)
        {
            HashSet<long> insulatedHosts = GetPipeInsulationHosts(doc);

            FilteredElementCollector coll = CreateCollector(doc, view, activeViewOnly)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsNotElementType();

            foreach (Element e in coll)
            {
                if (e == null || !e.IsValidObject) continue;

                if (!PassSystemNameFilter(e, systemFilter))
                    continue;

                bool hasRound, hasRect;
                double roundDiameterFt, rectW, rectH;
                GetFittingConnectorSizes(e, out hasRound, out roundDiameterFt, out hasRect, out rectW, out rectH);

                if (!hasRound)
                    continue;

                if (targetDiameterFt > 0 && !IsEqualLength(roundDiameterFt, targetDiameterFt))
                    continue;

                if (insulatedHosts.Contains(e.Id.IntegerValue))
                    continue;

                PipeInsulation.Create(doc, e.Id, insTypeId, thicknessFt);
                insulatedHosts.Add(e.Id.IntegerValue);
            }
        }

        // ===== DUCT =====

        private void ApplyDuctInsulationToDucts(
            Document doc, View view, bool activeViewOnly,
            bool enableRound, double targetRoundFt,
            bool enableRect, double targetRectWidthFt, double targetRectHeightFt,
            double thicknessFt, string systemFilter,
            ElementId insTypeId)
        {
            HashSet<long> insulatedHosts = GetDuctInsulationHosts(doc);

            FilteredElementCollector coll = CreateCollector(doc, view, activeViewOnly)
                .OfClass(typeof(Duct))
                .WhereElementIsNotElementType();

            foreach (Element e in coll)
            {
                Duct duct = e as Duct;
                if (duct == null || !duct.IsValidObject) continue;

                if (!PassSystemNameFilter(duct, systemFilter))
                    continue;

                double diameterFt = GetDoubleParam(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                double widthFt = GetDoubleParam(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                double heightFt = GetDoubleParam(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                bool isRound = diameterFt > 0;
                bool isRect = !isRound && widthFt > 0 && heightFt > 0;

                if (isRound)
                {
                    if (!enableRound) continue;
                    if (targetRoundFt > 0 && !IsEqualLength(diameterFt, targetRoundFt))
                        continue;
                }
                else if (isRect)
                {
                    if (!enableRect) continue;
                    if (targetRectWidthFt > 0 && !IsEqualLength(widthFt, targetRectWidthFt))
                        continue;
                    if (targetRectHeightFt > 0 && !IsEqualLength(heightFt, targetRectHeightFt))
                        continue;
                }
                else
                {
                    continue;
                }

                if (insulatedHosts.Contains(duct.Id.IntegerValue))
                    continue;

                DuctInsulation.Create(doc, duct.Id, insTypeId, thicknessFt);
                insulatedHosts.Add(duct.Id.IntegerValue);
            }
        }

        private void ApplyDuctInsulationToFittings(
            Document doc, View view, bool activeViewOnly,
             bool enableRound, double targetRoundFt,
             bool enableRect, double targetRectWidthFt, double targetRectHeightFt,
              double thicknessFt, string systemFilter,
    ElementId insTypeId)
        {
            HashSet<long> insulatedHosts = GetDuctInsulationHosts(doc);

            FilteredElementCollector coll = CreateCollector(doc, view, activeViewOnly)
                .OfCategory(BuiltInCategory.OST_DuctFitting)
                .WhereElementIsNotElementType();

            foreach (Element e in coll)
            {
                if (e == null || !e.IsValidObject) continue;

                if (!PassSystemNameFilter(e, systemFilter))
                    continue;

                // ==== 1. Lấy size từ PARAM (nếu có) ====
                double paramWidthFt = GetDoubleParam(e, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                double paramHeightFt = GetDoubleParam(e, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                bool hasRectParam = paramWidthFt > 0 && paramHeightFt > 0;

                // ==== 2. Lấy size qua CONNECTOR (max) ====
                bool hasRoundConn, hasRectConn;
                double roundDiameterFt, rectWidthConnFt, rectHeightConnFt;
                GetFittingConnectorSizes(
                    e,
                    out hasRoundConn, out roundDiameterFt,
                    out hasRectConn, out rectWidthConnFt, out rectHeightConnFt);

                bool hasRound = hasRoundConn;
                double roundDiaFt = roundDiameterFt;

                bool hasRect = hasRectParam || hasRectConn;
                double rectWidthFt = hasRectParam ? paramWidthFt : rectWidthConnFt;
                double rectHeightFt = hasRectParam ? paramHeightFt : rectHeightConnFt;

                bool pass = false;

                // ==== 3. Điều kiện cho fitting TRÒN ====
                if (hasRound && enableRound)
                {
                    // targetRoundFt = 0 -> không lọc size; >0 -> đúng size nhập
                    if (targetRoundFt <= 0 || IsEqualLength(roundDiaFt, targetRoundFt))
                        pass = true;
                }

                // ==== 4. Điều kiện cho fitting CHỮ NHẬT ====
                if (!pass && enableRect)
                {
                    if (targetRectWidthFt <= 0 && targetRectHeightFt <= 0)
                    {
                        // Không lọc size => chỉ cần fitting là chữ nhật
                        if (hasRect) pass = true;
                    }
                    else
                    {
                        // 4.1. So theo PARAM width/height (nếu có)
                        bool matchParam = false;
                        if (hasRect)
                        {
                            bool widthOk =
                                targetRectWidthFt <= 0 ||
                                IsEqualLength(rectWidthFt, targetRectWidthFt) ||
                                IsEqualLength(rectWidthFt, targetRectHeightFt);   // cho phép đảo

                            bool heightOk =
                                targetRectHeightFt <= 0 ||
                                IsEqualLength(rectHeightFt, targetRectHeightFt) ||
                                IsEqualLength(rectHeightFt, targetRectWidthFt);  // cho phép đảo

                            matchParam = widthOk && heightOk;
                        }

                        // 4.2. So theo từng connector chữ nhật (ANY connector khớp là ok)
                        bool matchConn =
                            HasRectConnectorWithSize(e, targetRectWidthFt, targetRectHeightFt);

                        if (matchParam || matchConn)
                            pass = true;
                    }
                }

                if (!pass)
                    continue;

                if (insulatedHosts.Contains(e.Id.IntegerValue))
                    continue;

                DuctInsulation.Create(doc, e.Id, insTypeId, thicknessFt);
                insulatedHosts.Add(e.Id.IntegerValue);
            }
        }
    }
}
