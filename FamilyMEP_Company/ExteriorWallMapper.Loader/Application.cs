using System.Reflection;
using Autodesk.Revit.UI;

namespace ExteriorWallMapper.Loader;

public sealed class Application : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "FamilyMEP";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Another FamilyMEP add-in may already own the shared ribbon tab.
        }

        RibbonPanel panel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Building Analysis")
            ?? application.CreateRibbonPanel(tabName, "Building Analysis");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var buttonData = new PushButtonData(
            "ExteriorWallMapper2025_Command",
            "Exterior Wall\nMapper",
            assemblyPath,
            typeof(RunExteriorWallMapperCommand).FullName)
        {
            ToolTip =
                "Scan MEP Spaces and linked Revit/IFC architecture, then prepare LINEAR EWA, EWI and IWA batch plans."
        };
        panel.AddItem(buttonData);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            RevitHotLoader2025.HotReloadManager.Unload();
        }
        catch
        {
            // Do not block Revit shutdown because a modeless window is still releasing.
        }
        return Result.Succeeded;
    }
}
