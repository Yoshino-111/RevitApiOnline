using System.Reflection;
using Autodesk.Revit.UI;
using FamilyMEP.Ribbon;

namespace FamilyMEP.Entry;

public sealed class Application : IExternalApplication
{
    private DrainConnectionRibbonAnimator? _drainConnectionIconAnimator;
    private SprinklerModelerRibbonAnimator? _sprinklerModelerIconAnimator;

    internal static FamilyMEP.Plugin.FamilyManagerPlugin Plugin { get; } = new();
    internal static FamilyMEP.Plugin.ValveBuilderPlugin ValveBuilderPlugin { get; } = new();
    internal static FamilyMEP.Plugin.GateValveBuilderPlugin GateValveBuilderPlugin { get; } = new();
    internal static FamilyMEP.Plugin.DrainConnectionPlugin DrainConnectionPlugin { get; } = new();
    internal static FamilyMEP.Plugin.SprinklerModelerPlugin SprinklerModelerPlugin { get; } = new();
    internal static FamilyMEP.Plugin.SmartTagPlugin SmartTagPlugin { get; } = new();

    internal static FamilyMEP.Plugin.UnifiedFamilyCreatorPlugin FamilyCreatorPlugin { get; } = new();
    internal static FamilyMEP.Plugin.ExteriorWallMapperPlugin ExteriorWallMapperPlugin { get; } = new();

    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "FamilyMEP";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The shared tab may already exist when another FamilyMEP command is installed.
        }

        RibbonPanel panel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Family Tools")
            ?? application.CreateRibbonPanel(tabName, "Family Tools");

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        panel.AddItem(new PushButtonData(
            "FamilyMEP_UnifiedFamilyCreator", "Family\nCreator", assemblyPath,
            typeof(FamilyCreatorCommand).FullName)
        {
            ToolTip = "Create parametric MEP families."
        });

        RibbonPanel buildingPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Building Analysis")
            ?? application.CreateRibbonPanel(tabName, "Building Analysis");
        buildingPanel.AddItem(new PushButtonData(
            "FamilyMEP_ExteriorWallMapper", "Exterior Wall\nMapper", assemblyPath,
            typeof(ExteriorWallMapperCommand).FullName)
        {
            ToolTip = "Map exterior walls and review results."
        });
        RibbonPanel drainagePanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Drainage Tools")
            ?? application.CreateRibbonPanel(tabName, "Drainage Tools");
        var drainConnectionButton = new PushButtonData(
            "FamilyMEP_DrainConnection",
            "Drain\nConnection",
            assemblyPath,
            typeof(DrainConnectionCommand).FullName)
        {
            ToolTip =
                "Create an automatic sloped Case 01 connection from a floor drain to a horizontal main using two 45-degree elbows."
        };
        var drainButton = (PushButton)drainagePanel.AddItem(drainConnectionButton);
        _drainConnectionIconAnimator = DrainConnectionRibbonAnimator.TryStart(
            drainButton,
            Assembly.GetExecutingAssembly());

        RibbonPanel fireProtectionPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Fire Protection")
            ?? application.CreateRibbonPanel(tabName, "Fire Protection");
        var sprinklerModelerButton = new PushButtonData(
            "FamilyMEP_SprinklerModeler",
            "Spinkler",
            assemblyPath,
            typeof(SprinklerModelerCommand).FullName)
        {
            ToolTip =
                "Interpret PDF or DWG spinkler drawings, verify numbered layer overlays, define connection rules, and preview pipe sizing."
        };
        var sprinklerButton = (PushButton)fireProtectionPanel.AddItem(sprinklerModelerButton);
        _sprinklerModelerIconAnimator = SprinklerModelerRibbonAnimator.TryStart(
            sprinklerButton,
            Assembly.GetExecutingAssembly());

        RibbonPanel annotationPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Annotation Tools")
            ?? application.CreateRibbonPanel(tabName, "Annotation Tools");
        var smartTagButton = new PushButtonData(
            "FamilyMEP_SmartTag",
            "Smart\nTag",
            assemblyPath,
            typeof(SmartTagCommand).FullName)
        {
            ToolTip =
                "Preview and arrange existing MEP tags in the active view with top-to-bottom priority and clash-aware leaders."
        };
        annotationPanel.AddItem(smartTagButton);
        RibbonPanelColorizer.ApplyAfterRibbonBuild();
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _drainConnectionIconAnimator?.Dispose();
        _drainConnectionIconAnimator = null;
        _sprinklerModelerIconAnimator?.Dispose();
        _sprinklerModelerIconAnimator = null;

        try
        {
#if REVIT2020
            LegacyDrainHotReloadManager.Shutdown();
#endif
            FamilyCreatorPlugin.Shutdown();
            ExteriorWallMapperPlugin.Shutdown();
            Plugin.Shutdown();
            ValveBuilderPlugin.Shutdown();
            GateValveBuilderPlugin.Shutdown();
            DrainConnectionPlugin.Shutdown();
            SprinklerModelerPlugin.Shutdown();
            SmartTagPlugin.Shutdown();
        }
        catch
        {
            // Never block Revit shutdown because a modeless window is already closing.
        }
        return Result.Succeeded;
    }
}
