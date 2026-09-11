using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Models;
using FamilyMEP.Plugin.Services;
using FamilyMEP.Plugin.Ui;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Plugin;

public sealed class UnifiedFamilyCreatorPlugin : IHotReloadPlugin
{
    private UnifiedFamilyCreatorWindow? _window;

    public string Name => "FamilyMEP Unified Family Creator";

    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        Shutdown();
        UIApplication uiApplication = commandData.Application;
        Document? project = uiApplication.ActiveUIDocument?.Document;
        if (project is null || project.IsFamilyDocument)
        {
            TaskDialog.Show(
                "FamilyMEP — Family Creator",
                "Open an RVT project before running Family Creator.");
            return Result.Cancelled;
        }

        try
        {
            _window = new UnifiedFamilyCreatorWindow(uiApplication);
            new WindowInteropHelper(_window)
            {
                Owner = uiApplication.MainWindowHandle
            };
            if (_window.ShowDialog() != true)
                return Result.Cancelled;

            string outputPath;
            bool openAfterCreate;
            string report;
            if (_window.ValveRequest is ValveBuilderRequest valveRequest)
            {
                ValveBuilderResult result = ValveBuilderService.Execute(
                    uiApplication,
                    valveRequest,
                    Array.Empty<ElementId>(),
                    null);
                outputPath = result.OutputPath;
                openAfterCreate = valveRequest.OpenGeneratedFamily;
                report = result.BuildReport();
            }
            else if (_window.PipeFittingRequest is PipeFittingBuilderRequest fittingRequest)
            {
                PipeFittingBuilderResult result =
                    GeberitMapressBendBuilderService.Execute(
                        uiApplication,
                        fittingRequest);
                outputPath = result.OutputPath;
                openAfterCreate = fittingRequest.OpenGeneratedFamily;
                report = result.BuildReport();
            }
            else if (_window.AirTerminalRequest is AirTerminalBuilderRequest airRequest)
            {
                AirTerminalBuilderResult result =
                    AirTerminalBuilderService.Execute(uiApplication, airRequest);
                outputPath = result.OutputPath;
                openAfterCreate = airRequest.OpenGeneratedFamily;
                report = result.BuildReport();
            }
            else if (_window.FireDamperRequest is FireDamperBuilderRequest damperRequest)
            {
                FireDamperBuilderResult result =
                    TroxKa2BuilderService.Execute(uiApplication, damperRequest);
                outputPath = result.OutputPath;
                openAfterCreate = damperRequest.OpenGeneratedFamily;
                report = result.BuildReport();
            }
            else
            {
                throw new InvalidOperationException(
                    "No Family build request was created.");
            }

            string? openWarning = null;
            if (openAfterCreate)
            {
                try
                {
                    uiApplication.OpenAndActivateDocument(outputPath);
                }
                catch (Exception exception)
                {
                    openWarning =
                        $"Generated Family could not be opened automatically: {exception.Message}";
                }
            }
            if (!string.IsNullOrWhiteSpace(openWarning))
                report += Environment.NewLine + Environment.NewLine + "Warning:" +
                          Environment.NewLine + "- " + openWarning;
            TaskDialog.Show("FamilyMEP — Family Creator", report);
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show(
                "FamilyMEP — Family Creator",
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
