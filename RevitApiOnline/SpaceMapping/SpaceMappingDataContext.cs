using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;

namespace RevitApiOnline.SpaceMapping
{
    public class SpaceMappingDataContext : INotifyPropertyChanged
    {
        private readonly UIApplication _uiApp;
        public Document MainDocument => _uiApp.ActiveUIDocument.Document;

        // ----- ComboBox Link & Category -----
        public ObservableCollection<LinkItem> LinkOptions { get; private set; }
        public ObservableCollection<CategoryItem> CategoryOptions { get; private set; }

        private LinkItem _selectedLink;
        public LinkItem SelectedLink
        {
            get => _selectedLink;
            set
            {
                if (_selectedLink != value)
                {
                    _selectedLink = value;
                    OnPropertyChanged(nameof(SelectedLink));
                    LoadLinkParameters();   // update param link
                    // xóa mapping cũ vì đổi link
                    Mappings.Clear();
                }
            }
        }

        private CategoryItem _selectedCategory;
        public CategoryItem SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory != value)
                {
                    _selectedCategory = value;
                    OnPropertyChanged(nameof(SelectedCategory));
                    LoadMainParameters();    // update param model
                    // xóa mapping cũ vì đổi category
                    Mappings.Clear();
                }
            }
        }

        // ----- Parameter options -----
        private List<string> _linkParameterOptions = new List<string>();
        public List<string> LinkParameterOptions
        {
            get => _linkParameterOptions;
            private set
            {
                _linkParameterOptions = value;
                OnPropertyChanged(nameof(LinkParameterOptions));
            }
        }

        private List<string> _mainParameterOptions = new List<string>();
        public List<string> MainParameterOptions
        {
            get => _mainParameterOptions;
            private set
            {
                _mainParameterOptions = value;
                OnPropertyChanged(nameof(MainParameterOptions));
            }
        }

        public string SelectedLinkParameter { get; set; }
        public string SelectedMainParameter { get; set; }

        // ---- Danh sách nhiều mapping ----
        public ObservableCollection<SpaceMappingParameterSelector> Mappings { get; private set; }

        // Khoảng cách search (mm)
        private double _searchDistanceMm = 1000;   // default 1000mm
        public double SearchDistanceMm
        {
            get => _searchDistanceMm;
            set
            {
                if (Math.Abs(_searchDistanceMm - value) > 0.0001)
                {
                    _searchDistanceMm = value;
                    OnPropertyChanged(nameof(SearchDistanceMm));
                }
            }
        }

        public SpaceMappingDataContext(UIApplication uiApp)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));

            LinkOptions = new ObservableCollection<LinkItem>();
            CategoryOptions = new ObservableCollection<CategoryItem>();
            Mappings = new ObservableCollection<SpaceMappingParameterSelector>();

            LoadLinkOptions();
            LoadCategoryOptions();

            if (LinkOptions.Count > 0)
                SelectedLink = LinkOptions[0];
        }

        // ================== LOAD LINK & CATEGORY ==================

        private void LoadLinkOptions()
        {
            LinkOptions.Clear();
            Document doc = MainDocument;

            var linkInstances = new FilteredElementCollector(doc)
                                    .OfClass(typeof(RevitLinkInstance))
                                    .Cast<RevitLinkInstance>();

            foreach (var linkInst in linkInstances)
            {
                Document linkDoc = linkInst.GetLinkDocument();
                string name = linkDoc != null ? linkDoc.Title : linkInst.Name;
                LinkOptions.Add(new LinkItem { Name = name, LinkInstance = linkInst });
            }
        }

        private void LoadCategoryOptions()
        {
            CategoryOptions.Clear();
            Document doc = MainDocument;

            HashSet<ElementId> seen = new HashSet<ElementId>();
            List<Category> cats = new List<Category>();

            var collector = new FilteredElementCollector(doc)
                                .WhereElementIsNotElementType()
                                .ToElements();

            foreach (var elem in collector)
            {
                Category cat = elem.Category;
                if (cat == null) continue;
                // CHỈ lấy Model Category (bỏ Annotation/Internal)
                if (cat.CategoryType != CategoryType.Model) continue;

                if (seen.Add(cat.Id))
                    cats.Add(cat);
            }

            foreach (var cat in cats.OrderBy(c => c.Name))
            {
                CategoryOptions.Add(new CategoryItem { Name = cat.Name, Category = cat });
            }
        }

        // ================== LOAD PARAMETERS ==================

        // Lấy danh sách parameter bên file Link (Rooms + Spaces)
        private void LoadLinkParameters()
        {
            LinkParameterOptions = new List<string>();
            SelectedLinkParameter = null;

            if (SelectedLink == null || SelectedLink.LinkInstance == null)
                return;

            Document linkDoc = SelectedLink.LinkInstance.GetLinkDocument();
            if (linkDoc == null)
                return;

            HashSet<string> paramNames = new HashSet<string>();

            var rooms = new FilteredElementCollector(linkDoc)
                            .OfCategory(BuiltInCategory.OST_Rooms)
                            .WhereElementIsNotElementType()
                            .ToElements();

            var spaces = new FilteredElementCollector(linkDoc)
                            .OfCategory(BuiltInCategory.OST_MEPSpaces)
                            .WhereElementIsNotElementType()
                            .ToElements();

            foreach (var spatial in rooms.Concat(spaces))
            {
                foreach (Parameter p in spatial.Parameters)
                {
                    paramNames.Add(p.Definition.Name);
                }
            }

            LinkParameterOptions = paramNames.OrderBy(n => n).ToList();
            if (LinkParameterOptions.Count > 0)
                SelectedLinkParameter = LinkParameterOptions[0];
        }

        // Lấy danh sách parameter của Category trong model chính
        private void LoadMainParameters()
        {
            MainParameterOptions = new List<string>();
            SelectedMainParameter = null;

            if (SelectedCategory == null || SelectedCategory.Category == null)
                return;

            Document doc = MainDocument;
            Category cat = SelectedCategory.Category;

            var filter = new ElementCategoryFilter(cat.Id);
            var elems = new FilteredElementCollector(doc)
                            .WhereElementIsNotElementType()
                            .WherePasses(filter)
                            .ToElements();

            HashSet<string> names = new HashSet<string>();

            foreach (var e in elems)
            {
                foreach (Parameter p in e.Parameters)
                {
                    if (p.IsReadOnly) continue;
                    names.Add(p.Definition.Name);
                }
            }

            MainParameterOptions = names.OrderBy(n => n).ToList();
            if (MainParameterOptions.Count > 0)
                SelectedMainParameter = MainParameterOptions[0];
        }

        // ================== THÊM MAPPING ==================

        public void AddCurrentMapping()
        {
            if (string.IsNullOrEmpty(SelectedLinkParameter) ||
                string.IsNullOrEmpty(SelectedMainParameter))
                return;

            // tránh trùng cặp
            if (Mappings.Any(m => m.LinkParameterName == SelectedLinkParameter &&
                                  m.SelectedMainParameterName == SelectedMainParameter))
                return;

            Mappings.Add(new SpaceMappingParameterSelector
            {
                LinkParameterName = SelectedLinkParameter,
                SelectedMainParameterName = SelectedMainParameter
            });
        }

        // ================== RUN MAPPING ==================

        public void RunMapping()
        {
            if (SelectedLink == null || SelectedLink.LinkInstance == null)
            {
                TaskDialog.Show("Space Mapping", "Chưa chọn file Link.");
                return;
            }
            if (SelectedCategory == null || SelectedCategory.Category == null)
            {
                TaskDialog.Show("Space Mapping", "Chưa chọn Category trong model.");
                return;
            }

            // nếu user chưa bấm Thêm, nhưng có chọn 1 cặp -> tự coi như 1 mapping
            List<SpaceMappingParameterSelector> mappingsToUse;
            if (Mappings.Count == 0)
            {
                if (string.IsNullOrEmpty(SelectedLinkParameter) ||
                    string.IsNullOrEmpty(SelectedMainParameter))
                {
                    TaskDialog.Show("Space Mapping",
                        "Chưa có mapping nào. Hãy chọn parameter và bấm 'Thêm mapping'.");
                    return;
                }

                mappingsToUse = new List<SpaceMappingParameterSelector>
                {
                    new SpaceMappingParameterSelector
                    {
                        LinkParameterName = SelectedLinkParameter,
                        SelectedMainParameterName = SelectedMainParameter
                    }
                };
            }
            else
            {
                mappingsToUse = Mappings.ToList();
            }

            Document mainDoc = MainDocument;
            RevitLinkInstance linkInst = SelectedLink.LinkInstance;
            Document linkDoc = linkInst.GetLinkDocument();
            if (linkDoc == null)
            {
                TaskDialog.Show("Space Mapping", "Không lấy được Document của file Link.");
                return;
            }

            // Phase cuối của file link để lấy Space
            Phase linkPhase = null;
            if (linkDoc.Phases.Size > 0)
                linkPhase = linkDoc.Phases.get_Item(linkDoc.Phases.Size - 1);

            Transform linkTransform = linkInst.GetTotalTransform();
            Category cat = SelectedCategory.Category;

            double maxDistFt = SearchDistanceMm > 0 ? SearchDistanceMm / 304.8 : 0.0; // mm -> ft

            var catFilter = new ElementCategoryFilter(cat.Id);
            var elements = new FilteredElementCollector(mainDoc)
                                .WhereElementIsNotElementType()
                                .WherePasses(catFilter)
                                .ToElements();

            using (Transaction tx = new Transaction(mainDoc, "Space Mapping"))
            {
                tx.Start();

                foreach (var elem in elements)
                {
                    XYZ pt = GetElementPoint(elem);
                    if (pt == null) continue;

                    // Đổi qua tọa độ file link
                    XYZ ptInLink = linkTransform.Inverse.OfPoint(pt);

                    // Hướng ưu tiên của element (Facing / hướng ống...)
                    List<XYZ> preferredDirs = GetPreferredDirections(elem, linkTransform);

                    // Tìm Space/Room
                    SpatialElement spatial = FindSpatialElement(linkDoc, linkPhase,
                                                                ptInLink, maxDistFt,
                                                                preferredDirs);
                    if (spatial == null)
                        continue;

                    foreach (var map in mappingsToUse)
                    {
                        Parameter src = spatial.LookupParameter(map.LinkParameterName);
                        Parameter dst = elem.LookupParameter(map.SelectedMainParameterName);

                        if (src == null || dst == null) continue;
                        if (dst.IsReadOnly) continue;

                        CopyParameterValue(src, dst);
                    }
                }

                tx.Commit();
            }
        }

        private void CopyParameterValue(Parameter src, Parameter dst)
        {
            switch (dst.StorageType)
            {
                case StorageType.String:
                    string s = src.AsString();
                    if (s == null) s = src.AsValueString();
                    dst.Set(s);
                    break;

                case StorageType.Double:
                    double d;
                    if (src.StorageType == StorageType.Double)
                    {
                        d = src.AsDouble();
                        dst.Set(d);
                    }
                    else if (double.TryParse(src.AsString(), out d))
                    {
                        dst.Set(d);
                    }
                    break;

                case StorageType.Integer:
                    int i;
                    if (src.StorageType == StorageType.Integer)
                    {
                        i = src.AsInteger();
                        dst.Set(i);
                    }
                    else if (int.TryParse(src.AsString(), out i))
                    {
                        dst.Set(i);
                    }
                    break;

                case StorageType.ElementId:
                    if (src.StorageType == StorageType.ElementId)
                        dst.Set(src.AsElementId());
                    break;
            }
        }

        // ======== Hình học & tìm Space ========

        private XYZ GetElementPoint(Element elem)
        {
            Location loc = elem.Location;
            if (loc is LocationPoint lp)
                return lp.Point;

            if (loc is LocationCurve lc)
            {
                Curve c = lc.Curve;
                return (c.GetEndPoint(0) + c.GetEndPoint(1)) * 0.5;
            }

            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb != null)
                return (bb.Min + bb.Max) * 0.5;

            return null;
        }

        private List<XYZ> GetPreferredDirections(Element elem, Transform linkTransform)
        {
            List<XYZ> dirs = new List<XYZ>();

            // 1. FamilyInstance có FacingOrientation
            FamilyInstance fi = elem as FamilyInstance;
            if (fi != null)
            {
                XYZ dirModel = fi.FacingOrientation;
                if (dirModel != null && dirModel.GetLength() > 1e-6)
                {
                    XYZ dirLink = linkTransform.Inverse.OfVector(dirModel).Normalize();
                    dirs.Add(dirLink);
                    dirs.Add(-dirLink);
                }
            }

            // 2. Element có LocationCurve (ống, ống gió...)
            LocationCurve lc = elem.Location as LocationCurve;
            if (lc != null)
            {
                Curve c = lc.Curve;
                XYZ dModel = (c.GetEndPoint(1) - c.GetEndPoint(0));
                if (dModel.GetLength() > 1e-6)
                {
                    XYZ dLink = linkTransform.Inverse.OfVector(dModel).Normalize();
                    dirs.Add(dLink);
                    dirs.Add(-dLink);
                }
            }

            return dirs;
        }

        private SpatialElement FindSpatialElement(Document linkDoc, Phase linkPhase,
                                                  XYZ ptInLink, double maxDistFt,
                                                  List<XYZ> preferredDirs)
        {
            // 0. Ngay tại vị trí element
            SpatialElement s = TryGetSpatialAtPoint(linkDoc, linkPhase, ptInLink);
            if (s != null) return s;

            if (maxDistFt <= 0) return null;

            // 1. Theo hướng ưu tiên
            if (preferredDirs != null && preferredDirs.Count > 0)
            {
                foreach (XYZ dir in preferredDirs)
                {
                    SpatialElement sDir = ShootAlongDirection(linkDoc, linkPhase,
                                                              ptInLink, dir, maxDistFt);
                    if (sDir != null) return sDir;
                }
            }

            // 2. Fallback 6 hướng ±Z, ±X, ±Y
            XYZ[] fallbackDirs = new[]
            {
                XYZ.BasisZ, -XYZ.BasisZ,
                XYZ.BasisX, -XYZ.BasisX,
                XYZ.BasisY, -XYZ.BasisY
            };

            foreach (XYZ dir in fallbackDirs)
            {
                SpatialElement sDir = ShootAlongDirection(linkDoc, linkPhase,
                                                          ptInLink, dir, maxDistFt);
                if (sDir != null) return sDir;
            }

            return null;
        }

        private SpatialElement ShootAlongDirection(Document linkDoc, Phase linkPhase,
                                                   XYZ start, XYZ dir, double maxDistFt)
        {
            if (dir == null || dir.GetLength() < 1e-6)
                return null;

            XYZ unitDir = dir.Normalize();
            int steps = 10;
            double step = maxDistFt / steps;

            for (int i = 1; i <= steps; i++)
            {
                XYZ p = start + unitDir * (step * i);
                SpatialElement s = TryGetSpatialAtPoint(linkDoc, linkPhase, p);
                if (s != null) return s;
            }

            return null;
        }

        private SpatialElement TryGetSpatialAtPoint(Document linkDoc, Phase linkPhase, XYZ pt)
        {
            SpatialElement s = null;

            if (linkPhase != null)
            {
                try { s = linkDoc.GetSpaceAtPoint(pt, linkPhase); } catch { }
            }

            if (s == null)
            {
                try { s = linkDoc.GetRoomAtPoint(pt); } catch { }
            }

            return s;
        }

        // ====== INotifyPropertyChanged ======
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
