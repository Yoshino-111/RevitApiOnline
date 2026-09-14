using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitHotLoader2025;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunUnifiedFamilyCreatorCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.UnifiedFamilyCreatorPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP — Family Creator", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunLatestCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(commandData, ref message, elements);
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("Revit Hot Reload", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunDrainConnectionCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.DrainConnectionPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP — Drain Connection", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunSprinklerModelerCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.SprinklerModelerPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Spinkler", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunExteriorWallMapperCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.ExteriorWallMapperPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Exterior Wall Mapper", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunSmartTagCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.SmartTagPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Smart Tag", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunValveBuilderCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.ValveBuilderPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Valve Builder", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunGateValveBuilderCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                "FamilyMEP.Plugin.GateValveBuilderPlugin");
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Gate Valve Builder", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunGlobeValveBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.GlobeValveBuilderPlugin",
            "Globe Valve");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunAngleValveBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.AngleValveBuilderPlugin",
            "Angle Valve");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunSwingCheckValveBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.SwingCheckValveBuilderPlugin",
            "Swing Check Valve");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunRingCheckValveBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.RingCheckValveBuilderPlugin",
            "Ring Check Valve");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunButterflyValveBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.ButterflyValveBuilderPlugin",
            "Butterfly Valve");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunPerforatedDiffuserBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.PerforatedDiffuserBuilderPlugin",
            "1100 Perforated Diffuser");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunPlaqueDiffuserBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.PlaqueDiffuserBuilderPlugin",
            "PLQ Plaque Diffuser");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunLinearSlotDiffuserBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.LinearSlotDiffuserBuilderPlugin",
            "1900 Linear Slot Diffuser");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunMultiDeflectionGrilleBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.MultiDeflectionGrilleBuilderPlugin",
            "5810 / 5815 Grille");
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RunReturnGrilleBuilderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        ValveCommandRunner.Execute(
            data,
            ref message,
            elements,
            "FamilyMEP.Plugin.ReturnGrilleBuilderPlugin",
            "S80 / S85 Return Grille");
}

internal static class ValveCommandRunner
{
    public static Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements,
        string pluginType,
        string title)
    {
        try
        {
            return HotReloadManager.ReloadAndExecute(
                commandData,
                ref message,
                elements,
                pluginType);
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show($"FamilyMEP - {title}", exception.ToString());
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class UnloadCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            UnloadResult result = HotReloadManager.Unload();
            string status = !result.HadPlugin
                ? "No plugin DLL is currently loaded."
                : result.Completed
                    ? "The plugin DLL was unloaded successfully."
                    : "Unload is pending because the plugin still has live references. See the log file.";
            TaskDialog.Show("Revit Hot Reload", status);
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("Revit Hot Reload", exception.ToString());
            return Result.Failed;
        }
    }
}
