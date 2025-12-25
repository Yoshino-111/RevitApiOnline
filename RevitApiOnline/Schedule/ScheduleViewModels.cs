using Autodesk.Revit.DB;

namespace RevitApiOnline.Schedule
{
    public class ScheduleInfoVM
    {
        public ElementId Id { get; set; }
        public string Name { get; set; }
    }

    public class ScheduleFieldVM
    {
        public string Name { get; set; }
    }
}
