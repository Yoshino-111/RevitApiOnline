using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Input;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Ui;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Plugin;

public sealed class FamilyManagerPlugin : IHotReloadPlugin
{
    private FamilyManagerController? _controller;
    private RevitRequestHandler? _requestHandler;
    private ExternalEvent? _externalEvent;

    public string Name => "FamilyMEP";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Shutdown();

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var valveBuilder = new ValveBuilderPlugin();
            return valveBuilder.Execute(commandData, ref message, elements);
        }

        _requestHandler = new RevitRequestHandler();
        _externalEvent = ExternalEvent.Create(_requestHandler);
        _controller = new FamilyManagerController(
            commandData.Application,
            _requestHandler,
            _externalEvent);
        _controller.Show();
        return Result.Succeeded;
    }

    public void Shutdown()
    {
        _controller?.Dispose();
        _controller = null;
        _externalEvent?.Dispose();
        _externalEvent = null;
        _requestHandler = null;
    }
}
