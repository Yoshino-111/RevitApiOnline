using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitApiOnline.Add_in
{
    public static  class AirTerminalAppShow
    {
        public static PutAirTerminalWpf formAirTerminalWpf;
        public static void ShowForm()
        {
            GetFamilyTypeHandler  getFamilyTypeHandler = new GetFamilyTypeHandler();
            ExternalEvent eventFamilyType = ExternalEvent.Create(getFamilyTypeHandler);

            PutAirTerminalHandler putAirTerminalHandler = new PutAirTerminalHandler();
            ExternalEvent eventAirTerminal = ExternalEvent.Create(putAirTerminalHandler);

            formAirTerminalWpf = new PutAirTerminalWpf(eventFamilyType,eventAirTerminal);
            formAirTerminalWpf.Show();
        }

    }
}
