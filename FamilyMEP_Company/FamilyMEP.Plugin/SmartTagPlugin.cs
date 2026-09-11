using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.SmartTag;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Plugin;

public sealed class SmartTagPlugin : IHotReloadPlugin
{
    private SmartTagController? _controller;
    private RevitRequestHandler? _handler;
    private ExternalEvent? _externalEvent;

    public string Name => "FamilyMEP Smart Tag";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Shutdown();
        _handler = new RevitRequestHandler();
        _externalEvent = ExternalEvent.Create(_handler);
        _controller = new SmartTagController(commandData.Application, _handler, _externalEvent);
        _controller.Show();
        return Result.Succeeded;
    }

    public void Shutdown()
    {
        _controller?.Dispose();
        _controller = null;
        _externalEvent?.Dispose();
        _externalEvent = null;
        _handler = null;
    }
}
