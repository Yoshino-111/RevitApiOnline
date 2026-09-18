using System.Reflection;
using Autodesk.Revit.UI;
using FamilyMEP.Ribbon;

namespace RevitHotLoader2025;

public sealed class Application : IExternalApplication
{
    private DrainConnectionRibbonAnimator? _drainConnectionIconAnimator;
    private static DrainConnectionRibbonAnimator? _activeDrainConnectionIconAnimator;
    private SprinklerModelerRibbonAnimator? _sprinklerModelerIconAnimator;

    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "FamilyMEP";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The tab already exists.
        }

        RibbonPanel panel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Family Tools")
            ?? application.CreateRibbonPanel(tabName, "Family Tools");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var familyCreatorButton = new PushButtonData(
            "FamilyMEP_UnifiedFamilyCreator",
            "Family\nCreator",
            assemblyPath,
            typeof(RunUnifiedFamilyCreatorCommand).FullName)
        {
            ToolTip =
                "Open one unified FamilyMEP tool for all Pipe Accessory valves and Air Terminals."
        };
        panel.AddItem(familyCreatorButton);

        var unloadButton = new PushButtonData(
            "FamilyMEP_Unload",
            "Reload\nRelease",
            assemblyPath,
            typeof(UnloadCommand).FullName)
        {
            ToolTip = "Close the current FamilyMEP window and release its hot-loaded DLL.",
        };
        panel.AddItem(unloadButton);

        RibbonPanel drainagePanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Drainage Tools")
            ?? application.CreateRibbonPanel(tabName, "Drainage Tools");
        var drainConnectionButton = new PushButtonData(
            "FamilyMEP_DrainConnection",
            "Drain\nConnection",
            assemblyPath,
            typeof(RunDrainConnectionCommand).FullName)
        {
            ToolTip =
                "Create an automatic sloped Case 01 connection from a floor drain to a horizontal main using two 45-degree elbows."
        };
        var drainButton = (PushButton)drainagePanel.AddItem(drainConnectionButton);
        _drainConnectionIconAnimator = DrainConnectionRibbonAnimator.TryStart(
            drainButton,
            Assembly.GetExecutingAssembly());
        _activeDrainConnectionIconAnimator = _drainConnectionIconAnimator;

        RibbonPanel fireProtectionPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Fire Protection")
            ?? application.CreateRibbonPanel(tabName, "Fire Protection");
        var sprinklerModelerButton = new PushButtonData(
            "FamilyMEP_SprinklerModeler",
            "Spinkler",
            assemblyPath,
            typeof(RunSprinklerModelerCommand).FullName)
        {
            ToolTip =
                "Interpret PDF or DWG spinkler drawings, verify numbered layer overlays, define connection rules, and preview pipe sizing."
        };
        var sprinklerButton = (PushButton)fireProtectionPanel.AddItem(sprinklerModelerButton);
        _sprinklerModelerIconAnimator = SprinklerModelerRibbonAnimator.TryStart(
            sprinklerButton,
            Assembly.GetExecutingAssembly());

        RibbonPanel buildingAnalysisPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Building Analysis")
            ?? application.CreateRibbonPanel(tabName, "Building Analysis");
        var exteriorWallMapperButton = new PushButtonData(
            "FamilyMEP_ExteriorWallMapper",
            "Exterior Wall\nMapper",
            assemblyPath,
            typeof(RunExteriorWallMapperCommand).FullName)
        {
            ToolTip =
                "Scan exterior MEP Space boundaries in linked Revit/IFC models, group wall layers, export Excel, and isolate results for verification."
        };
        buildingAnalysisPanel.AddItem(exteriorWallMapperButton);

        RibbonPanel annotationPanel = application.GetRibbonPanels(tabName)
            .FirstOrDefault(item => item.Name == "Annotation Tools")
            ?? application.CreateRibbonPanel(tabName, "Annotation Tools");
        var smartTagButton = new PushButtonData(
            "FamilyMEP_SmartTag",
            "Smart\nTag",
            assemblyPath,
            typeof(RunSmartTagCommand).FullName)
        {
            ToolTip =
                "Preview and arrange existing MEP tags in the active view with top-to-bottom priority and clash-aware leaders."
        };
        annotationPanel.AddItem(smartTagButton);
        RibbonPanelColorizer.ApplyAfterRibbonBuild();

        return Result.Succeeded;
    }

    internal static void ShuffleDrainConnectionIcon()
    {
        _activeDrainConnectionIconAnimator?.ShuffleVariant();
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _drainConnectionIconAnimator?.Dispose();
        if (ReferenceEquals(_activeDrainConnectionIconAnimator, _drainConnectionIconAnimator))
        {
            _activeDrainConnectionIconAnimator = null;
        }
        _drainConnectionIconAnimator = null;
        _sprinklerModelerIconAnimator?.Dispose();
        _sprinklerModelerIconAnimator = null;

        try
        {
            HotReloadManager.Unload();
        }
        catch
        {
            // Revit is shutting down; do not block it because of debug cleanup.
        }

        return Result.Succeeded;
    }
}
