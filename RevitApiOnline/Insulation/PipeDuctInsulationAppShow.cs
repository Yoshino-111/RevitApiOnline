using Autodesk.Revit.UI;
using RevitApiOnline.Insulation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitApiOnline.Insulation
{
    public class PipeDuctInsulationAppShow
    {
        public static PipeDuctInsulationWpf Form;
        public static ExternalEvent ApplyInsulationEvent;

        public static void ShowForm()
        {
            if (Form != null && Form.IsVisible)
            {
                Form.Activate();
                return;
            }

            var handler = new ApplyInsulationHandler();
            ApplyInsulationEvent = ExternalEvent.Create(handler);

            Form = new PipeDuctInsulationWpf(ApplyInsulationEvent);
            Form.Show();
        }
    }
}
