using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitApiOnline.Shared.Implements;
using RevitApiOnline.Shared.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using View = Autodesk.Revit.DB.View;
using System.DirectoryServices.ActiveDirectory;
using System.Xml.Linq;
using RevitApiOnline.WPF;
using RevitApiOnline.WPFDuct;
using RevitApiOnline.ListBoxWPF;

namespace RevitApiOnline
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            return Result.Succeeded;

        }
    }

}
