using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Services;
using FamilyMEP.Plugin.Ui;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Plugin;

public sealed class GateValveBuilderPlugin : IHotReloadPlugin
{
    private ValveBuilderWindow? _window;

    public string Name => "FamilyMEP Gate Valve Builder";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Shutdown();
        UIApplication uiApplication = commandData.Application;
        UIDocument? uiDocument = uiApplication.ActiveUIDocument;
        Document? project = uiDocument?.Document;
        if (uiDocument is null || project is null || project.IsFamilyDocument)
        {
            TaskDialog.Show("FamilyMEP - Gate Valve Builder", "Open an RVT project before running Gate Family.");
            return Result.Cancelled;
        }

        List<ElementId> selectedIds = uiDocument.Selection.GetElementIds().ToList();
        List<FamilyInstance> selectedValves = selectedIds
            .Select(project.GetElement)
            .OfType<FamilyInstance>()
            .Where(IsEditablePipeAccessory)
            .ToList();
        Family? selectedFamily = selectedValves.FirstOrDefault()?.Symbol?.Family;
        if (selectedFamily is not null)
        {
            selectedIds = selectedValves
                .Where(instance => instance.Symbol?.Family?.Id == selectedFamily.Id)
                .Select(instance => instance.Id)
                .ToList();
        }

        string selectedDescription = selectedFamily is null
            ? string.Empty
            : $"{selectedFamily.Name} ({selectedIds.Count} selected)";
        _window = new ValveBuilderWindow(
            selectedFamily is not null,
            selectedDescription,
            string.Empty,
            initialCatalogIndex: 1);
        new WindowInteropHelper(_window) { Owner = uiApplication.MainWindowHandle };
        bool accepted = _window.ShowDialog() == true;
        if (!accepted)
        {
            _window = null;
            return Result.Cancelled;
        }

        try
        {
            Models.ValveBuilderResult result = ValveBuilderService.Execute(
                uiApplication,
                _window.Request,
                selectedIds,
                selectedFamily);
            if (_window.Request.OpenGeneratedFamily)
            {
                try
                {
                    uiApplication.OpenAndActivateDocument(result.OutputPath);
                }
                catch (Exception exception)
                {
                    result.Warnings.Add($"Generated Family could not be opened automatically: {exception.Message}");
                }
            }
            TaskDialog.Show("FamilyMEP - Gate Valve Builder", result.BuildReport());
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.ToString();
            TaskDialog.Show("FamilyMEP - Gate Valve Builder", exception.Message);
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

    private static bool IsEditablePipeAccessory(FamilyInstance instance)
    {
        Family? family = instance.Symbol?.Family;
        if (family is null || family.IsInPlace || !family.IsEditable) return false;
        Category? category = family.FamilyCategory ?? family.Category;
        if (category is null) return false;
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
        return category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory;
#else
        return category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory;
#endif
    }
}
