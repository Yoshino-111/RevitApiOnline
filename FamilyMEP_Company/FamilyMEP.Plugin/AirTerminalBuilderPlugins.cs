using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Models;
using FamilyMEP.Plugin.Services;
using FamilyMEP.Plugin.Ui;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Plugin;

public sealed class PerforatedDiffuserBuilderPlugin : IHotReloadPlugin
{
    private readonly AirTerminalPluginHost _host = new(0);
    public string Name => "FamilyMEP Perforated Diffuser Builder";
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        _host.Execute(data, ref message);
    public void Shutdown() => _host.Shutdown();
}

public sealed class PlaqueDiffuserBuilderPlugin : IHotReloadPlugin
{
    private readonly AirTerminalPluginHost _host = new(1);
    public string Name => "FamilyMEP Plaque Diffuser Builder";
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        _host.Execute(data, ref message);
    public void Shutdown() => _host.Shutdown();
}

public sealed class LinearSlotDiffuserBuilderPlugin : IHotReloadPlugin
{
    private readonly AirTerminalPluginHost _host = new(2);
    public string Name => "FamilyMEP Linear Slot Diffuser Builder";
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        _host.Execute(data, ref message);
    public void Shutdown() => _host.Shutdown();
}

public sealed class MultiDeflectionGrilleBuilderPlugin : IHotReloadPlugin
{
    private readonly AirTerminalPluginHost _host = new(3);
    public string Name => "FamilyMEP Multi-Deflection Grille Builder";
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        _host.Execute(data, ref message);
    public void Shutdown() => _host.Shutdown();
}

public sealed class ReturnGrilleBuilderPlugin : IHotReloadPlugin
{
    private readonly AirTerminalPluginHost _host = new(4);
    public string Name => "FamilyMEP Return Grille Builder";
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        _host.Execute(data, ref message);
    public void Shutdown() => _host.Shutdown();
}

internal sealed class AirTerminalPluginHost
{
    private readonly int _definitionIndex;
    private AirTerminalBuilderWindow? _window;

    public AirTerminalPluginHost(int definitionIndex)
    {
        _definitionIndex = definitionIndex;
    }

    public Result Execute(ExternalCommandData commandData, ref string message)
    {
        Shutdown();
        UIApplication uiApplication = commandData.Application;
        Document? project = uiApplication.ActiveUIDocument?.Document;
        AirTerminalDefinition definition = AirTerminalCatalog.All[_definitionIndex];
        if (project is null || project.IsFamilyDocument)
        {
            TaskDialog.Show(
                $"FamilyMEP - {definition.DisplayName}",
                "Open an RVT project before running this tool.");
            return Result.Cancelled;
        }

        try
        {
            AirTerminalInspection inspection =
                AirTerminalBuilderService.Inspect(uiApplication, definition);
            _window = new AirTerminalBuilderWindow(definition, inspection);
            new WindowInteropHelper(_window)
            {
                Owner = uiApplication.MainWindowHandle
            };
            if (_window.ShowDialog() != true)
            {
                _window = null;
                return Result.Cancelled;
            }

            AirTerminalBuilderRequest request = _window.Request;
            AirTerminalBuilderResult result =
                AirTerminalBuilderService.Execute(uiApplication, request);
            if (request.OpenGeneratedFamily)
            {
                try
                {
                    uiApplication.OpenAndActivateDocument(result.OutputPath);
                }
                catch (Exception exception)
                {
                    result.Warnings.Add(
                        $"Generated Family could not be opened automatically: {exception.Message}");
                }
            }
            TaskDialog.Show(
                $"FamilyMEP - {definition.DisplayName}",
                result.BuildReport());
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show(
                $"FamilyMEP - {definition.DisplayName}",
                exception.Message);
            return Result.Failed;
        }
        finally
        {
            _window = null;
        }
    }

    public void Shutdown()
    {
        if (_window is null) return;
        try { _window.Close(); }
        catch { }
        _window = null;
    }
}
