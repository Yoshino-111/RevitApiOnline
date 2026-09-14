using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitHotReload.Abstractions;

public interface IHotReloadPlugin
{
    string Name { get; }

    Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements);

    void Shutdown();
}

