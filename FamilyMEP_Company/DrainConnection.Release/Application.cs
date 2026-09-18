using System.Reflection;
using Autodesk.Revit.UI;
using FamilyMEP.Ribbon;

namespace FamilyMEP.DrainConnection.Entry;

public sealed class Application : IExternalApplication
{
    private DrainConnectionRibbonAnimator? _iconAnimator;

    internal static FamilyMEP.Plugin.DrainConnectionPlugin Plugin { get; } = new();

    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "LucAddin";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Another LucAddin add-in may already own the shared tab.
        }

        RibbonPanel panel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Drainage Tools")
            ?? application.CreateRibbonPanel(tabName, "Drainage Tools");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var buttonData = new PushButtonData(
            "FamilyMEP_DrainConnection_Standalone",
            "Drain\nConnection",
            assemblyPath,
            typeof(DrainConnectionCommand).FullName)
        {
            ToolTip = "Create gravity drain branches for the supported connection cases."
        };
        var button = (PushButton)panel.AddItem(buttonData);
        _iconAnimator = DrainConnectionRibbonAnimator.TryStart(
            button,
            Assembly.GetExecutingAssembly());
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _iconAnimator?.Dispose();
        _iconAnimator = null;
        try
        {
            Plugin.Shutdown();
        }
        catch
        {
            // Do not block Revit shutdown if the modeless window is closing.
        }
        return Result.Succeeded;
    }
}
