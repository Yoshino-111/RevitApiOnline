// Các view-model phụ trợ cho binding
using Autodesk.Revit.DB;

namespace RevitApiOnline.SpaceMapping
{
    public class LinkItem
    {
        public string Name { get; set; }
        public RevitLinkInstance LinkInstance { get; set; }

        public override string ToString() => Name;
    }

    public class CategoryItem
    {
        public string Name { get; set; }
        public Category Category { get; set; }

        public override string ToString() => Name;
    }
}
