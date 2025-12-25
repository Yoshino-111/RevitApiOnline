using Autodesk.Revit.UI;

namespace RevitApiOnline.SpaceMapping
{
    /// <summary>
    /// Handler cũ không còn sử dụng.
    /// Để stub trống cho khỏi lỗi build.
    /// </summary>
    internal class SpaceMappingHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            // Không làm gì cả – mapping chạy trong SpaceMappingDataContext.RunMapping()
        }

        public string GetName()
        {
            return "SpaceMappingHandler (unused)";
        }
    }
}
