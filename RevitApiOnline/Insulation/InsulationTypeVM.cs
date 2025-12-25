using Autodesk.Revit.DB;

namespace RevitApiOnline.Insulation
{
    /// <summary>
    /// ViewModel đơn giản cho 1 Insulation Type.
    /// </summary>
    public class InsulationTypeVM
    {
        public string Name { get; set; }
        public ElementId TypeId { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}