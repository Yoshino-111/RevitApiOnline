using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitApiOnline.Insulation;
using System.Collections.ObjectModel;

namespace RevitApiOnline.Insulation
{
    [Transaction(TransactionMode.Manual)]
    public class PipeDuctInsulationBinding : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Vui lòng mở 1 project trước khi chạy lệnh.";
                return Result.Failed;
            }

            Document doc = uidoc.Document;

            var dataContext = new PipeDuctInsulationDataContext();

            // ===== Lấy Pipe Insulation Types =====
            var pipeTypeList = new ObservableCollection<InsulationTypeVM>();
            var pipeTypeCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeInsulationType));

            foreach (PipeInsulationType t in pipeTypeCollector)
            {
                pipeTypeList.Add(new InsulationTypeVM
                {
                    Name = t.Name,
                    TypeId = t.Id
                });
            }

            dataContext.PipeInsulationTypes = pipeTypeList;
            if (pipeTypeList.Count > 0)
                dataContext.SelectedPipeInsulationType = pipeTypeList[0];

            // ===== Lấy Duct Insulation Types =====
            var ductTypeList = new ObservableCollection<InsulationTypeVM>();
            var ductTypeCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(DuctInsulationType));

            foreach (DuctInsulationType t in ductTypeCollector)
            {
                ductTypeList.Add(new InsulationTypeVM
                {
                    Name = t.Name,
                    TypeId = t.Id
                });
            }

            dataContext.DuctInsulationTypes = ductTypeList;
            if (ductTypeList.Count > 0)
                dataContext.SelectedDuctInsulationType = ductTypeList[0];

            // Show form
            PipeDuctInsulationAppShow.ShowForm();
            PipeDuctInsulationAppShow.Form.DataContext = dataContext;

            return Result.Succeeded;
        }
    }
}