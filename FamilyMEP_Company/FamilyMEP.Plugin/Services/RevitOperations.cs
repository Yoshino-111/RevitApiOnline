using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class RevitOperations
{
    private const char SharedDefinitionSeparator = '\u001F';
    private const string PreviewCacheVersion = "mesh_v2";
    private const string Preview2DCacheVersion = "ref2d_v1";
    private const string DefaultPreviewColor = "#4B5563";

    public static void OpenFamily(UIApplication uiApplication, string path)
    {
        EnsureRfa(path);
        EnsureVersionCompatible(uiApplication, path);
        uiApplication.OpenAndActivateDocument(path);
    }

    public static List<ProjectDocumentItem> GetOpenProjects(UIApplication uiApplication) =>
        uiApplication.Application.Documents
            .Cast<Document>()
            .Where(document => !document.IsFamilyDocument)
            .Select(document => new ProjectDocumentItem(DocumentKey(document), ProjectDisplayName(document)))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static ViewStandardsReadResult ReadViewStandards(UIApplication uiApplication, string sourcePath)
    {
        EnsureRvt(sourcePath);
        EnsureVersionCompatible(uiApplication, sourcePath);
        Document? source = null;
        try
        {
            source = OpenStandardsDocument(uiApplication, sourcePath);
            List<View> templates = new FilteredElementCollector(source)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => view.IsTemplate)
                .OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            List<ParameterFilterElement> filters = new FilteredElementCollector(source)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .OrderBy(filter => filter.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Dictionary<long, string> filterColors = new();
            foreach (View template in templates)
            {
                foreach (ElementId filterId in SafeViewFilters(template))
                {
                    long number = ElementIdNumber(filterId);
                    if (filterColors.ContainsKey(number)) continue;
                    filterColors[number] = FirstOverrideColor(template, filterId);
                }
            }

            var items = new List<ViewStandardItem>(templates.Count + filters.Count);
            foreach (View template in templates)
            {
                List<ElementId> templateFilters = SafeViewFilters(template);
                string color = templateFilters
                    .Select(filterId => FirstOverrideColor(template, filterId))
                    .FirstOrDefault(value => !value.Equals("#CBD5E1", StringComparison.OrdinalIgnoreCase))
                    ?? "#CBD5E1";
                int controlled = 0;
                try { controlled = template.GetTemplateParameterIds().Count; } catch { }
                items.Add(new ViewStandardItem
                {
                    ElementIdValue = ElementIdNumber(template.Id),
                    Kind = ViewStandardKind.ViewTemplate,
                    Name = template.Name,
                    Summary = $"{template.ViewType} view template",
                    Categories = string.Join(", ", templateFilters
                        .Select(id => source.GetElement(id)?.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Take(4)),
                    ColorHex = color,
                    FilterCount = templateFilters.Count,
                    ControlledParameterCount = controlled
                });
            }

            foreach (ParameterFilterElement filter in filters)
            {
                List<string> categories = CategoryNames(source, filter.GetCategories());
                items.Add(new ViewStandardItem
                {
                    ElementIdValue = ElementIdNumber(filter.Id),
                    Kind = ViewStandardKind.ViewFilter,
                    Name = filter.Name,
                    Summary = $"{categories.Count:N0} categor{(categories.Count == 1 ? "y" : "ies")} • rules preserved in RVT",
                    Categories = string.Join(", ", categories),
                    ColorHex = filterColors.TryGetValue(ElementIdNumber(filter.Id), out string? color)
                        ? color
                        : "#CBD5E1"
                });
            }

            string version = string.Empty;
            try
            {
                string format = BasicFileInfo.Extract(sourcePath).Format ?? string.Empty;
                version = new string(format.Where(char.IsDigit).Take(4).ToArray());
            }
            catch { }
            return new ViewStandardsReadResult { RevitVersion = version, Items = items };
        }
        finally
        {
            if (source is not null)
            {
                try { source.Close(false); } catch { }
            }
        }
    }

    public static ViewStandardsApplyResult ApplyViewStandards(
        UIApplication uiApplication,
        string sourcePath,
        IEnumerable<ViewStandardItem> selectedItems)
    {
        EnsureRvt(sourcePath);
        EnsureVersionCompatible(uiApplication, sourcePath);
        Document target = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Open the destination Revit project before loading standards.");
        if (target.IsFamilyDocument)
        {
            throw new InvalidOperationException("View filters and templates can only be loaded into a Revit project.");
        }

        List<ViewStandardItem> requested = selectedItems
            .Where(item => item.Selected)
            .OrderBy(item => item.Kind == ViewStandardKind.ViewTemplate ? 0 : 1)
            .ToList();
        if (requested.Count == 0) throw new InvalidOperationException("Select at least one filter or view template.");

        var result = new ViewStandardsApplyResult();
        Document? source = null;
        try
        {
            source = OpenStandardsDocument(uiApplication, sourcePath);
            using TransactionGroup group = new(target, "FamilyMEP - Load view standards");
            group.Start();
            foreach (ViewStandardItem item in requested)
            {
                if (HasNamedViewStandard(target, item))
                {
                    result.Skipped++;
                    result.Messages.Add($"{item.Name}: already exists");
                    continue;
                }

                ElementId sourceId = CreateElementId(item.ElementIdValue);
                if (source.GetElement(sourceId) is null)
                {
                    result.Failed++;
                    result.Messages.Add($"{item.Name}: source element no longer exists");
                    continue;
                }

                using Transaction transaction = new(target, $"Load {item.KindLabel}: {item.Name}");
                try
                {
                    transaction.Start();
                    using var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new UseDestinationDuplicateTypesHandler());
                    ElementTransformUtils.CopyElements(
                        source,
                        new List<ElementId> { sourceId },
                        target,
                        Autodesk.Revit.DB.Transform.Identity,
                        options);
                    transaction.Commit();
                    result.Loaded++;
                }
                catch (Exception exception)
                {
                    if (transaction.HasStarted()) transaction.RollBack();
                    result.Failed++;
                    result.Messages.Add($"{item.Name}: {exception.Message}");
                }
            }
            group.Assimilate();
            return result;
        }
        finally
        {
            if (source is not null)
            {
                try { source.Close(false); } catch { }
            }
        }
    }

    public static FamilyLoadResult LoadFamiliesToProject(
        UIApplication uiApplication,
        string projectKey,
        IEnumerable<string> paths)
    {
        Document project = FindOpenProject(uiApplication, projectKey)
            ?? throw new InvalidOperationException("The selected destination project is no longer open.");
        int loaded = 0;
        int skipped = 0;
        HashSet<string> existingNames = new FilteredElementCollector(project)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Select(family => family.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string path in distinctPaths)
        {
            EnsureRfa(path);
            EnsureVersionCompatible(uiApplication, path);
        }
        using Transaction transaction = new(project, "FamilyMEP - Load selected families");
        transaction.Start();
        foreach (string path in distinctPaths)
        {
            string familyName = Path.GetFileNameWithoutExtension(path);
            if (!existingNames.Add(familyName))
            {
                skipped++;
                continue;
            }
            if (project.LoadFamily(path, new OverwriteFamilyLoadOptions(), out _)) loaded++;
            else skipped++;
        }
        transaction.Commit();
        return new FamilyLoadResult(loaded, skipped);
    }

    public static List<ProjectFamilyItem> InspectProjectFamilies(
        UIApplication uiApplication,
        string projectPath)
    {
        Document? project = null;
        bool closeWhenDone = false;
        try
        {
            project = FindOpenProjectByPath(uiApplication, projectPath);
            if (project is null)
            {
                project = OpenLibraryScanDocument(uiApplication, projectPath);
                closeWhenDone = true;
            }
            if (project.IsFamilyDocument)
            {
                throw new InvalidOperationException("Select an RVT project, not an RFA family file.");
            }

            return new FilteredElementCollector(project)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(family => !family.IsInPlace && family.IsEditable)
                .Select(family => new ProjectFamilyItem
                {
                    UniqueId = family.UniqueId,
                    Name = family.Name,
                    Category = family.FamilyCategory?.Name ?? family.Category?.Name ?? "Unclassified"
                })
                .OrderBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            if (closeWhenDone) project?.Close(false);
        }
    }

    public static ProjectFamilyExportResult ExportProjectFamilies(
        UIApplication uiApplication,
        string projectPath,
        IReadOnlyCollection<ProjectFamilyItem> selectedFamilies,
        string outputRoot)
    {
        ProjectFamilyExportSession? session = null;
        try
        {
            session = BeginProjectFamilyExport(uiApplication, projectPath, outputRoot);
            return ExportProjectFamilyBatch(session, selectedFamilies, complete: true)
                ?? throw new InvalidOperationException("The project family export did not complete.");
        }
        catch
        {
            if (session is not null) AbortProjectFamilyExport(session);
            throw;
        }
    }

    internal sealed class ProjectFamilyExportSession
    {
        internal ProjectFamilyExportSession(
            Document project,
            bool closeWhenDone,
            string outputRoot,
            Dictionary<string, Family> families,
            HashSet<string> existingNames)
        {
            Project = project;
            CloseWhenDone = closeWhenDone;
            OutputRoot = outputRoot;
            Families = families;
            ExistingNames = existingNames;
        }

        internal Document Project { get; }
        internal bool CloseWhenDone { get; }
        internal string OutputRoot { get; }
        internal Dictionary<string, Family> Families { get; }
        internal HashSet<string> ExistingNames { get; }
        internal List<ResolvedFamilyCategory> ExportedFamilies { get; } = [];
        internal int Exported { get; set; }
        internal int Skipped { get; set; }
        internal bool Completed { get; set; }
    }

    public static ProjectFamilyExportSession BeginProjectFamilyExport(
        UIApplication uiApplication,
        string projectPath,
        string outputRoot)
    {
        EnsureFolderOnF(outputRoot);
        Document? project = null;
        bool closeWhenDone = false;
        try
        {
            project = FindOpenProjectByPath(uiApplication, projectPath);
            if (project is null)
            {
                project = OpenLibraryScanDocument(uiApplication, projectPath);
                closeWhenDone = true;
            }

            Dictionary<string, Family> families = new FilteredElementCollector(project)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToDictionary(family => family.UniqueId, StringComparer.OrdinalIgnoreCase);
            HashSet<string> existingNames = Directory.EnumerateFiles(outputRoot, "*.rfa", SearchOption.AllDirectories)
                .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ProjectFamilyExportSession(project, closeWhenDone, outputRoot, families, existingNames);
        }
        catch
        {
            if (closeWhenDone) project?.Close(false);
            throw;
        }
    }

    public static ProjectFamilyExportResult? ExportProjectFamilyBatch(
        ProjectFamilyExportSession session,
        IReadOnlyCollection<ProjectFamilyItem> selectedFamilies,
        bool complete)
    {
        if (session.Completed) throw new InvalidOperationException("The project family export session is already closed.");
        try
        {
            foreach (ProjectFamilyItem selected in selectedFamilies)
            {
                if (!session.Families.TryGetValue(selected.UniqueId, out Family? family)
                    || family.IsInPlace
                    || !family.IsEditable)
                {
                    session.Skipped++;
                    continue;
                }
                if (!session.ExistingNames.Add(selected.Name))
                {
                    session.Skipped++;
                    continue;
                }

                Document? familyDocument = null;
                try
                {
                    familyDocument = session.Project.EditFamily(family);
                    string categoryFolder = Path.Combine(session.OutputRoot, BuildCategoryFolderName(selected.Category));
                    Directory.CreateDirectory(categoryFolder);
                    string outputPath = Path.Combine(categoryFolder, SanitizeFileName(selected.Name) + ".rfa");
                    familyDocument.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = false });
                    session.Exported++;
                    session.ExportedFamilies.Add(new ResolvedFamilyCategory(outputPath, selected.Category));
                }
                finally
                {
                    familyDocument?.Close(false);
                }
            }

            return complete ? CompleteProjectFamilyExport(session) : null;
        }
        catch
        {
            AbortProjectFamilyExport(session);
            throw;
        }
    }

    private static ProjectFamilyExportResult CompleteProjectFamilyExport(ProjectFamilyExportSession session)
    {
        if (!session.Completed)
        {
            session.Completed = true;
            if (session.CloseWhenDone) session.Project.Close(false);
        }
        return new ProjectFamilyExportResult(
            session.Exported,
            session.Skipped,
            session.ExportedFamilies.ToList());
    }

    private static void AbortProjectFamilyExport(ProjectFamilyExportSession session)
    {
        if (session.Completed) return;
        session.Completed = true;
        if (session.CloseWhenDone)
        {
            try { session.Project.Close(false); } catch { }
        }
    }

    public static List<ParameterItem> InspectParameters(
        UIApplication uiApplication,
        IReadOnlyCollection<string> paths)
    {
        var aggregate = new Dictionary<string, ParameterItem>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            EnsureRfa(path);
            EnsureVersionCompatible(uiApplication, path);
            Document? familyDocument = null;
            try
            {
                familyDocument = uiApplication.Application.OpenDocumentFile(path);
                foreach (FamilyParameter parameter in familyDocument.FamilyManager.Parameters)
                {
                    string name = parameter.Definition.Name;
                    if (!aggregate.TryGetValue(name, out ParameterItem? item))
                    {
                        item = new ParameterItem
                        {
                            Name = name,
                            NewName = name,
                            Group = SafeGroupName(parameter),
                            DataType = SafeDataType(parameter),
                            InstanceType = parameter.IsInstance ? "Instance" : "Type",
                            Shared = parameter.IsShared,
                            Formula = parameter.Formula ?? string.Empty,
                            FoundIn = 0
                        };
                        aggregate.Add(name, item);
                    }

                    item.FoundIn++;
                }
            }
            finally
            {
                familyDocument?.Close(false);
            }
        }

        return aggregate.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<BatchLogItem> RenameParameters(
        UIApplication uiApplication,
        IReadOnlyCollection<FamilyItem> families,
        string findText,
        string replaceText,
        bool createBackup,
        bool saveAfterEdit)
    {
        if (string.IsNullOrWhiteSpace(findText))
        {
            throw new ArgumentException("Find text cannot be empty.", nameof(findText));
        }

        string backupSession = Path.Combine(AppPaths.BackupFolder, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var backupManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (createBackup)
        {
            Directory.CreateDirectory(backupSession);
        }

        var logs = new List<BatchLogItem>();
        foreach (FamilyItem family in families)
        {
            Document? familyDocument = null;
            try
            {
                EnsureRfa(family.Path);
                EnsureWritableOnF(family.Path);
                if (createBackup)
                {
                    string backupPath = Path.Combine(
                        backupSession,
                        $"{Guid.NewGuid():N}_{Path.GetFileName(family.Path)}");
                    File.Copy(family.Path, backupPath, true);
                    backupManifest[family.Path] = backupPath;
                }

                familyDocument = uiApplication.Application.OpenDocumentFile(family.Path);
                List<FamilyParameter> matches = familyDocument.FamilyManager.Parameters
                    .Cast<FamilyParameter>()
                    .Where(parameter => parameter.Definition.Name.Contains(findText, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    logs.Add(Log(family, "Warning", $"No parameter contains '{findText}'."));
                    continue;
                }

                int renamed = 0;
                using (Transaction transaction = new(familyDocument, "FamilyMEP - Rename parameters"))
                {
                    transaction.Start();
                    foreach (FamilyParameter parameter in matches)
                    {
                        string oldName = parameter.Definition.Name;
                        string newName = ReplaceInsensitive(oldName, findText, replaceText);
                        if (string.Equals(oldName, newName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        familyDocument.FamilyManager.RenameParameter(parameter, newName);
                        renamed++;
                    }

                    transaction.Commit();
                }

                if (saveAfterEdit)
                {
                    familyDocument.Save();
                }

                logs.Add(Log(family, "Success", $"Renamed {renamed} parameter(s)."));
            }
            catch (Exception exception)
            {
                logs.Add(Log(family, "Error", exception.Message));
            }
            finally
            {
                familyDocument?.Close(false);
            }
        }

        if (createBackup)
        {
            File.WriteAllText(
                Path.Combine(backupSession, "manifest.json"),
                JsonSerializer.Serialize(backupManifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        return logs;
    }

    public static BatchFamilyPreviewResult PreviewBatchOperation(
        UIApplication uiApplication,
        FamilyItem family,
        BatchOperationRequest request)
    {
        EnsureRfa(family.Path);
        EnsureWritableOnF(family.Path);
        var result = new BatchFamilyPreviewResult();
        if ((File.GetAttributes(family.Path) & FileAttributes.ReadOnly) != 0)
        {
            result.Changes.Add(Change(family, "RFA file", family.FamilyName, string.Empty, "Error", OperationAction(request.Kind), "The RFA file is read-only."));
            return result;
        }

        if (request.Kind is BatchOperationKind.RenameFiles or BatchOperationKind.NamingStandard)
        {
            string currentName = Path.GetFileNameWithoutExtension(family.Path);
            string proposedName = request.Kind == BatchOperationKind.NamingStandard
                ? $"{request.Source}{currentName}{request.Target}"
                : TransformName(currentName, request.Source, request.Target, request.MatchRule);
            string proposedPath = Path.Combine(Path.GetDirectoryName(family.Path)!, SanitizeFileName(proposedName) + ".rfa");
            string validation = string.Equals(currentName, proposedName, StringComparison.Ordinal) ? "Skip" : "Ready";
            string message = validation == "Skip" ? "The file name would not change." : string.Empty;
            if (!string.Equals(family.Path, proposedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(proposedPath))
            {
                (proposedPath, validation, message) = ResolveFileConflict(proposedPath, request.ConflictHandling);
                proposedName = Path.GetFileNameWithoutExtension(proposedPath);
            }
            result.Changes.Add(Change(family, "RFA file", currentName, proposedName, validation, "Rename", message));
            return result;
        }

        Document? document = null;
        try
        {
            document = uiApplication.Application.OpenDocumentFile(family.Path);
            FamilyManager manager = document.FamilyManager;
            List<FamilyParameter> parameters = manager.Parameters.Cast<FamilyParameter>().ToList();
            foreach (FamilyParameter parameter in parameters)
            {
                result.Parameters.Add(new ParameterItem
                {
                    Name = parameter.Definition.Name,
                    NewName = parameter.Definition.Name,
                    Group = SafeGroupName(parameter),
                    DataType = SafeDataType(parameter),
                    InstanceType = parameter.IsInstance ? "Instance" : "Type",
                    Shared = parameter.IsShared,
                    Formula = parameter.Formula ?? string.Empty,
                    FoundIn = 1
                });
            }

            switch (request.Kind)
            {
                case BatchOperationKind.RenameParameter:
                    foreach (FamilyParameter parameter in parameters.Where(item => NameMatches(item.Definition.Name, request.Source, request.MatchRule)))
                    {
                        string current = parameter.Definition.Name;
                        string target = TransformName(current, request.Source, request.Target, request.MatchRule);
                        string validation = "Ready";
                        string message = string.Empty;
                        if (parameter.IsShared)
                        {
                            validation = "Skip";
                            message = "Shared parameters cannot be renamed directly.";
                        }
                        else if (parameters.Any(item => !ReferenceEquals(item, parameter) && item.Definition.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
                        {
                            (target, validation, message) = ResolveNameConflict(target, parameters.Select(item => item.Definition.Name), request.ConflictHandling);
                        }
                        result.Changes.Add(Change(family, current, current, target, validation, "Rename", message));
                    }
                    break;

                case BatchOperationKind.AddSharedParameter:
                    PreviewAddShared(uiApplication, family, request, parameters, result);
                    break;

                case BatchOperationKind.ReplaceParameter:
                    PreviewReplaceShared(uiApplication, family, request, parameters, result);
                    break;

                case BatchOperationKind.SetValue:
                    foreach (FamilyParameter parameter in parameters.Where(item => NameMatches(item.Definition.Name, request.Source, request.MatchRule)))
                    {
                        string validation = parameter.IsReadOnly ? "Skip" : "Ready";
                        result.Changes.Add(Change(family, parameter.Definition.Name, "Current values", request.Target, validation, "Set", parameter.IsReadOnly ? "Parameter is read-only." : string.Empty));
                    }
                    break;

                case BatchOperationKind.SetFormula:
                    foreach (FamilyParameter parameter in parameters.Where(item => NameMatches(item.Definition.Name, request.Source, request.MatchRule)))
                    {
                        string validation = parameter.IsReadOnly ? "Skip" : "Ready";
                        result.Changes.Add(Change(family, parameter.Definition.Name, parameter.Formula ?? string.Empty, request.Target, validation, "Formula", parameter.IsReadOnly ? "Parameter is read-only." : string.Empty));
                    }
                    break;

                case BatchOperationKind.RemoveParameter:
                    foreach (FamilyParameter parameter in parameters.Where(item => NameMatches(item.Definition.Name, request.Source, request.MatchRule)))
                    {
                        string validation = parameter.IsShared || ElementIdNumber(parameter.Id) < 0 ? "Warning" : "Ready";
                        string message = validation == "Warning" ? "This parameter may be shared or built-in; Revit will validate removal." : string.Empty;
                        result.Changes.Add(Change(family, parameter.Definition.Name, parameter.Definition.Name, "Removed", validation, "Remove", message));
                    }
                    break;

                case BatchOperationKind.RenameTypes:
                    foreach (FamilyType type in manager.Types.Cast<FamilyType>().Where(item => NameMatches(item.Name, request.Source, request.MatchRule)))
                    {
                        string target = TransformName(type.Name, request.Source, request.Target, request.MatchRule);
                        string validation = "Ready";
                        string message = string.Empty;
                        if (manager.Types.Cast<FamilyType>().Any(item => !ReferenceEquals(item, type) && item.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
                        {
                            (target, validation, message) = ResolveNameConflict(target, manager.Types.Cast<FamilyType>().Select(item => item.Name), request.ConflictHandling);
                        }
                        result.Changes.Add(Change(family, type.Name, type.Name, target, validation, "Rename", message));
                    }
                    break;

                case BatchOperationKind.CreateTypes:
                {
                    FamilyType? baseType = manager.Types.Cast<FamilyType>().FirstOrDefault(item => item.Name.Equals(request.Source, StringComparison.OrdinalIgnoreCase))
                        ?? manager.Types.Cast<FamilyType>().FirstOrDefault();
                    string validation = baseType is null ? "Error" : manager.Types.Cast<FamilyType>().Any(item => item.Name.Equals(request.Target, StringComparison.OrdinalIgnoreCase)) ? "Skip" : "Ready";
                    string message = baseType is null ? "The family has no source type." : validation == "Skip" ? "The target type already exists." : string.Empty;
                    result.Changes.Add(Change(family, baseType?.Name ?? request.Source, baseType?.Name ?? string.Empty, request.Target, validation, "Add", message));
                    break;
                }

                case BatchOperationKind.DeleteTypes:
                {
                    List<FamilyType> matches = manager.Types.Cast<FamilyType>().Where(item => NameMatches(item.Name, request.Source, request.MatchRule)).ToList();
                    int totalTypes = manager.Types.Size;
                    foreach (FamilyType type in matches)
                    {
                        string validation = totalTypes - matches.Count < 1 ? "Error" : "Warning";
                        string message = validation == "Error" ? "At least one family type must remain." : "Deleting a type cannot be undone without restoring the backup.";
                        result.Changes.Add(Change(family, type.Name, type.Name, "Deleted", validation, "Remove", message));
                    }
                    break;
                }

                case BatchOperationKind.ChangeCategory:
                {
                    string current = document.OwnerFamily?.FamilyCategory?.Name ?? "Unclassified";
                    Category? target = document.Settings.Categories.Cast<Category>().FirstOrDefault(item => item.Name.Equals(request.Target, StringComparison.OrdinalIgnoreCase));
                    string validation = target is null ? "Error" : current.Equals(request.Target, StringComparison.OrdinalIgnoreCase) ? "Skip" : "Warning";
                    string message = target is null ? "The Revit category was not found." : validation == "Skip" ? "The family already uses this category." : "Category changes can affect connectors and project behavior.";
                    result.Changes.Add(Change(family, "Family Category", current, request.Target, validation, "Set", message));
                    break;
                }
            }

            if (result.Changes.Count == 0)
            {
                result.Changes.Add(Change(family, request.Source, string.Empty, string.Empty, "Skip", OperationAction(request.Kind), "No matching item was found."));
            }
            return result;
        }
        finally
        {
            document?.Close(false);
        }
    }

    public static BatchFamilyExecutionResult ExecuteBatchOperation(
        UIApplication uiApplication,
        FamilyItem family,
        BatchOperationRequest request,
        IReadOnlyCollection<BatchChangeItem> selectedChanges)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            EnsureRfa(family.Path);
            EnsureWritableOnF(family.Path);
            List<BatchChangeItem> changes = selectedChanges.Where(item => item.CanRun).ToList();
            if (changes.Count == 0)
            {
                return ExecutionLog(family, "Warning", "No runnable change was selected.", stopwatch);
            }

            if (request.Kind is BatchOperationKind.RenameFiles or BatchOperationKind.NamingStandard)
            {
                string targetName = changes[0].NewValue;
                string targetPath = Path.Combine(Path.GetDirectoryName(family.Path)!, SanitizeFileName(targetName) + ".rfa");
                if (!string.Equals(family.Path, targetPath, StringComparison.OrdinalIgnoreCase)) File.Move(family.Path, targetPath);
                return ExecutionLog(family, "Success", $"Renamed file to {Path.GetFileName(targetPath)}.", stopwatch, targetPath);
            }

            Document? document = null;
            try
            {
                document = uiApplication.Application.OpenDocumentFile(family.Path);
                FamilyManager manager = document.FamilyManager;
                using Transaction transaction = new(document, $"FamilyMEP - {OperationTitle(request.Kind)}");
                transaction.Start();
                ApplyBatchChanges(uiApplication, document, manager, request, changes);
                transaction.Commit();
                document.Save();
            }
            finally
            {
                document?.Close(false);
            }
            return ExecutionLog(family, "Success", $"Applied {changes.Count:N0} change(s).", stopwatch);
        }
        catch (Exception exception)
        {
            return ExecutionLog(family, "Error", exception.Message, stopwatch);
        }
    }

    public static string? FindCachedPreview(
        string familyPath,
        string previewColor,
        string? familyCategory = null)
    {
        string previewPath = GetPreviewPath(
            familyPath,
            previewColor,
            RequiresTwoDimensionalPreview(familyCategory));
        return IsPreviewCacheFresh(previewPath, familyPath) ? previewPath : null;
    }

    public static string GetPreviewCachePath(
        string familyPath,
        string previewColor,
        string? familyCategory = null)
    {
        AppPaths.EnsureCreated();
        return GetPreviewPath(
            familyPath,
            previewColor,
            RequiresTwoDimensionalPreview(familyCategory));
    }

    public static PreviewCreationResult CreatePreview(
        UIApplication uiApplication,
        string familyPath,
        string previewColor,
        bool force = false,
        string? familyCategory = null)
    {
        EnsureRfa(familyPath);
        EnsureVersionCompatible(uiApplication, familyPath);
        AppPaths.EnsureCreated();
        string previewPath = GetPreviewPath(
            familyPath,
            previewColor,
            RequiresTwoDimensionalPreview(familyCategory));
        if (!force && FindCachedPreview(familyPath, previewColor, familyCategory) is { } cached)
        {
            return new PreviewCreationResult(cached, null);
        }

        Document? document = null;
        try
        {
            document = uiApplication.Application.OpenDocumentFile(familyPath);
            string? revitCategory = document.IsFamilyDocument
                ? document.OwnerFamily?.FamilyCategory?.Name
                : null;
            bool renderAs2D = IsAnnotationFamily(document);
            previewPath = GetPreviewPath(familyPath, previewColor, renderAs2D);
            if (!force && IsPreviewCacheFresh(previewPath, familyPath))
            {
                return new PreviewCreationResult(previewPath, revitCategory);
            }

            if (renderAs2D)
            {
                RenderRefLevelPreview(uiApplication.Application, document, previewPath, previewColor);
            }
            else
            {
                PreviewMeshData mesh = ExtractPreviewMesh(document);
                if (mesh.TriangleIndices.Count > 0)
                {
                    RenderPreviewMesh(mesh, previewPath, previewColor);
                }
                else
                {
                    RenderRefLevelPreview(uiApplication.Application, document, previewPath, previewColor);
                }
            }
            return new PreviewCreationResult(previewPath, revitCategory);
        }
        finally
        {
            document?.Close(false);
        }
    }

    public static FamilyInspectionResult InspectFamily(UIApplication uiApplication, string familyPath)
    {
        EnsureRfa(familyPath);
        EnsureVersionCompatible(uiApplication, familyPath);
        Document? document = null;
        try
        {
            document = uiApplication.Application.OpenDocumentFile(familyPath);
            if (!document.IsFamilyDocument)
            {
                throw new InvalidOperationException("The selected file is not a Revit family document.");
            }

            FamilyManager manager = document.FamilyManager;
            List<FamilyType> familyTypes = manager.Types.Cast<FamilyType>()
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var result = new FamilyInspectionResult
            {
                Types = familyTypes.Select((type, index) => new FamilyTypeInfo
                {
                    Name = type.Name,
                    Description = $"Family type {index + 1:N0} of {familyTypes.Count:N0}"
                }).ToList()
            };

            foreach (FamilyParameter parameter in manager.Parameters.Cast<FamilyParameter>()
                         .OrderBy(item => item.Definition.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string value = string.Empty;
                foreach (FamilyType type in familyTypes)
                {
                    try
                    {
                        value = type.AsValueString(parameter) ?? type.AsString(parameter) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value)) break;
                    }
                    catch { }
                }

                result.Parameters.Add(new FamilyParameterInfo
                {
                    Name = parameter.Definition.Name,
                    Group = ParameterGroupLabel(parameter.Definition),
                    DataType = ParameterDataTypeLabel(parameter.Definition),
                    Scope = parameter.IsInstance ? "Instance" : "Type",
                    Shared = parameter.IsShared,
                    Formula = parameter.Formula ?? string.Empty,
                    Value = value
                });
            }
            return result;
        }
        finally
        {
            document?.Close(false);
        }
    }

    public static List<ResolvedFamilyCategory> ResolveFamilyCategories(
        UIApplication uiApplication,
        IEnumerable<string> familyPaths)
    {
        var results = new List<ResolvedFamilyCategory>();
        foreach (string familyPath in familyPaths)
        {
            EnsureRfa(familyPath);
            EnsureVersionCompatible(uiApplication, familyPath);
            Document? document = null;
            try
            {
                document = uiApplication.Application.OpenDocumentFile(familyPath);
                string? category = document.IsFamilyDocument
                    ? document.OwnerFamily?.FamilyCategory?.Name
                    : null;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    results.Add(new ResolvedFamilyCategory(familyPath, category));
                }
            }
            finally
            {
                document?.Close(false);
            }
        }
        return results;
    }

    private static string ParameterGroupLabel(Definition definition)
    {
#if REVIT2020 || REVIT2021
        return LegacyTypeLabel(definition.ParameterGroup.ToString());
#else
        return ForgeTypeLabel(definition.GetGroupTypeId());
#endif
    }

    private static string ParameterDataTypeLabel(Definition definition)
    {
#if REVIT2020 || REVIT2021
        return LegacyTypeLabel(definition.ParameterType.ToString());
#else
        return ForgeTypeLabel(definition.GetDataType());
#endif
    }

#if REVIT2020 || REVIT2021
    private static string LegacyTypeLabel(string value) => string.Join(" ",
        value.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
#else
    private static string ForgeTypeLabel(ForgeTypeId? id)
    {
        string typeId = id?.TypeId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(typeId)) return "Other";
        int colon = typeId.LastIndexOf(':');
        string label = colon >= 0 ? typeId.Substring(colon + 1) : typeId;
        int version = label.LastIndexOf('-');
        if (version > 0 && label.Substring(version + 1).Any(char.IsDigit)) label = label.Substring(0, version);
        return string.Join(" ", label.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
    }
#endif

    private static PreviewMeshData ExtractPreviewMesh(Document document)
    {
        var result = new PreviewMeshData();
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            if (IsPreviewClutter(element)) continue;
            Category? category = element.Category;
            if (category is not null && category.CategoryType != CategoryType.Model) continue;

            GeometryElement? geometry;
            try { geometry = element.get_Geometry(options); }
            catch { continue; }
            if (geometry is null) continue;
            AppendGeometry(geometry, result);
        }

        result.Normalize();
        return result;
    }

    private static void AppendGeometry(GeometryElement geometry, PreviewMeshData target)
    {
        foreach (GeometryObject geometryObject in geometry)
        {
            switch (geometryObject)
            {
                case Solid solid when solid.Faces.Size > 0:
                    foreach (Face face in solid.Faces)
                    {
                        try { AppendMesh(face.Triangulate(), target); }
                        catch { }
                    }
                    break;

                case Autodesk.Revit.DB.Mesh mesh:
                    AppendMesh(mesh, target);
                    break;

                case GeometryInstance instance:
                    try { AppendGeometry(instance.GetInstanceGeometry(), target); }
                    catch { }
                    break;
            }
        }
    }

    private static void AppendMesh(Autodesk.Revit.DB.Mesh mesh, PreviewMeshData target)
    {
        for (int index = 0; index < mesh.NumTriangles; index++)
        {
            MeshTriangle triangle = mesh.get_Triangle(index);
            XYZ a = triangle.get_Vertex(0);
            XYZ b = triangle.get_Vertex(1);
            XYZ c = triangle.get_Vertex(2);
            target.AddTriangle(
                new Point3D(a.X, a.Y, a.Z),
                new Point3D(b.X, b.Y, b.Z),
                new Point3D(c.X, c.Y, c.Z));
        }
    }

    private static void RenderPreviewMesh(PreviewMeshData data, string destinationPath, string previewColor)
    {
        const int pixelWidth = 900;
        const int pixelHeight = 600;
        var lookDirection = new Vector3D(-3.2, 3.2, -2.6);
        var upDirection = new Vector3D(0, 0, 1);
        double cameraWidth = data.CenterAndFitToCamera(
            lookDirection,
            upDirection,
            (double)pixelWidth / pixelHeight);

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(data.Positions),
            TriangleIndices = new Int32Collection(data.TriangleIndices),
            Normals = new Vector3DCollection(data.Normals)
        };
        mesh.Freeze();

        var materials = new MaterialGroup();
        System.Windows.Media.Color geometryColor = ParsePreviewColor(previewColor);
        materials.Children.Add(new DiffuseMaterial(new SolidColorBrush(geometryColor)));
        materials.Children.Add(new SpecularMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 213, 224)), 28));
        materials.Freeze();

        var model = new GeometryModel3D(mesh, materials) { BackMaterial = materials };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(System.Windows.Media.Color.FromRgb(112, 116, 122)));
        scene.Children.Add(new DirectionalLight(System.Windows.Media.Color.FromRgb(255, 255, 255), new Vector3D(-1.2, 1.6, -2.4)));
        scene.Children.Add(new DirectionalLight(System.Windows.Media.Color.FromRgb(175, 184, 198), new Vector3D(1.8, -1.0, -0.8)));
        scene.Children.Add(model);

        var viewport = new Viewport3D
        {
            Width = pixelWidth,
            Height = pixelHeight,
            ClipToBounds = true,
            Camera = new OrthographicCamera
            {
                Position = new Point3D(3.2, -3.2, 2.6),
                LookDirection = lookDirection,
                UpDirection = upDirection,
                Width = cameraWidth,
                NearPlaneDistance = 0.01,
                FarPlaneDistance = 100
            }
        };
        viewport.Children.Add(new ModelVisual3D { Content = scene });

        var host = new Border
        {
            Width = pixelWidth,
            Height = pixelHeight,
            Background = Brushes.White,
            Child = viewport
        };
        host.Measure(new System.Windows.Size(pixelWidth, pixelHeight));
        host.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(destinationPath);
        encoder.Save(output);
    }

    private static void RenderRefLevelPreview(
        Autodesk.Revit.ApplicationServices.Application application,
        Document document,
        string destinationPath,
        string previewColor)
    {
        bool preserveAnnotationContent = IsAnnotationFamily(document);
        View? view = FindRefLevelView(document);
        if (view is null)
        {
            if (preserveAnnotationContent)
            {
                RenderAnnotationTypePreview(application, document, destinationPath, previewColor);
                return;
            }
            throw new InvalidOperationException("No visible 3D geometry or Ref. Level plan view was found in this family.");
        }
        string outputFolder = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(outputFolder);
        string temporaryFolder = Path.Combine(outputFolder, "temp-exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryFolder);
        string exportBase = Path.Combine(temporaryFolder, "preview");
        var exportedFiles = new List<string>();
        string cleanedPath = Path.Combine(temporaryFolder, "cleaned.png");

        try
        {
            using (var transaction = new Transaction(document, "FamilyMEP - Prepare 2D preview"))
            {
                transaction.Start();
                if (preserveAnnotationContent)
                {
                    ActivateRepresentativeAnnotationType(document);
                }
                HashSet<ElementId> visibleGeometry = new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .Where(element => !Is2DPreviewClutter(element, preserveAnnotationContent))
                    .Select(element => element.Id)
                    .ToHashSet();
                HidePreviewClutter(document, view, visibleGeometry);
                try { view.CropBoxVisible = false; } catch { }
                transaction.Commit();
            }

            var exportOptions = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = exportBase,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = 1200
            };
            exportOptions.SetViewsAndSheets([view.Id]);
            document.ExportImage(exportOptions);

            exportedFiles = Directory.EnumerateFiles(temporaryFolder, "*.png", SearchOption.TopDirectoryOnly)
                .Where(path => !path.Equals(cleanedPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            string exported = exportedFiles.FirstOrDefault()
                ?? throw new InvalidOperationException("Revit did not create the Ref. Level preview image.");
            CleanPreviewImage(exported, cleanedPath);
            CropAndCenter2DPreview(cleanedPath, destinationPath);
        }
        finally
        {
            foreach (string path in exportedFiles.Append(cleanedPath))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            try { if (Directory.Exists(temporaryFolder)) Directory.Delete(temporaryFolder, recursive: true); } catch { }
        }
    }

    private static View? FindRefLevelView(Document document)
    {
        var views = new List<View>();
        try
        {
            DocumentPreviewSettings previewSettings = document.GetDocumentPreviewSettings();
            ElementId previewViewId = previewSettings.PreviewViewId;
            if (previewViewId != ElementId.InvalidElementId
                && document.GetElement(previewViewId) is View previewView)
            {
                views.Add(previewView);
            }
        }
        catch
        {
            // Some legacy family files do not store a dedicated document preview view.
        }

        try
        {
            View activeView = document.ActiveView;
            if (activeView is not null) views.Add(activeView);
        }
        catch
        {
            // A document opened in the background may not expose ActiveView.
        }

        try
        {
            views.AddRange(new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .ToElements()
                .OfType<View>());
        }
        catch
        {
            // Keep any preview or active view already found above.
        }

        views = views
            .Where(IsUsable2DPreviewView)
            .GroupBy(view => ElementIdNumber(view.Id))
            .Select(group => group.First())
            .ToList();
        return views
            .OrderByDescending(view => view.Name.Equals("Ref. Level", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(view => view.Name.Contains("Ref", StringComparison.OrdinalIgnoreCase)
                && view.Name.Contains("Level", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(view => view is ViewPlan)
            .ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsUsable2DPreviewView(View view)
    {
        try
        {
            return !view.IsTemplate
                && view is not View3D
                && view.ViewType is not ViewType.Schedule
                && view.ViewType is not ViewType.DrawingSheet
                && view.ViewType is not ViewType.ProjectBrowser
                && view.ViewType is not ViewType.SystemBrowser;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAnnotationFamily(Document document)
    {
        try
        {
            Category? category = document.OwnerFamily?.FamilyCategory;
            if (category is null) return false;
            if (category.CategoryType == CategoryType.Annotation) return true;
            string name = category.Name ?? string.Empty;
            string[] annotationTerms =
            [
                "Tag", "Annotation", "Detail Item", "Detail Component", "Symbol",
                "View Title", "Section Head", "Elevation Mark", "Callout Head", "Keynote"
            ];
            return annotationTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void ActivateRepresentativeAnnotationType(Document document)
    {
        if (!document.IsFamilyDocument) return;
        try
        {
            FamilyManager manager = document.FamilyManager;
            FamilyType? previewType = RepresentativeAnnotationType(manager);
            if (previewType is null) return;
            manager.CurrentType = previewType;
            document.Regenerate();
        }
        catch
        {
            // Some annotation templates have no editable family types. Their visible content can still be exported.
        }
    }

    private static FamilyType? RepresentativeAnnotationType(FamilyManager manager) =>
        manager.Types.Cast<FamilyType>()
            .OrderBy(type => type.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

    private static void RenderAnnotationTypePreview(
        Autodesk.Revit.ApplicationServices.Application application,
        Document familyDocument,
        string destinationPath,
        string previewColor)
    {
        string typeName = RepresentativeAnnotationType(familyDocument.FamilyManager)?.Name ?? "Annotation";
        Document? previewProject = null;
        try
        {
            previewProject = application.NewProjectDocument(UnitSystem.Metric);
            Family loadedFamily = familyDocument.LoadFamily(previewProject);
            FamilySymbol? symbol = loadedFamily.GetFamilySymbolIds()
                .Select(previewProject.GetElement)
                .OfType<FamilySymbol>()
                .OrderByDescending(item => item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (symbol is not null && TrySaveElementTypePreview(symbol, destinationPath))
            {
                return;
            }
        }
        catch
        {
            // Some tag categories cannot be loaded into a blank project. A clean type card is the final fallback.
        }
        finally
        {
            try { previewProject?.Close(false); } catch { }
        }

        RenderAnnotationTypeCard(familyDocument, destinationPath, previewColor, typeName);
    }

    private static bool TrySaveElementTypePreview(ElementType elementType, string destinationPath)
    {
        object? bitmap = null;
        try
        {
            System.Reflection.MethodInfo? getPreview = typeof(ElementType).GetMethod(
                "GetPreviewImage",
                [typeof(System.Drawing.Size)]);
            bitmap = getPreview?.Invoke(elementType, [new System.Drawing.Size(900, 600)]);
            if (bitmap is null) return false;
            Type bitmapType = bitmap.GetType();
            Type? imageFormatType = bitmapType.Assembly.GetType("System.Drawing.Imaging.ImageFormat");
            object? pngFormat = imageFormatType?.GetProperty("Png")?.GetValue(null);
            System.Reflection.MethodInfo? save = imageFormatType is null
                ? null
                : bitmapType.GetMethod("Save", [typeof(string), imageFormatType]);
            if (save is null || pngFormat is null) return false;
            save.Invoke(bitmap, [destinationPath, pngFormat]);
            return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bitmap is IDisposable disposable) disposable.Dispose();
        }
    }

    private static void RenderAnnotationTypeCard(
        Document familyDocument,
        string destinationPath,
        string previewColor,
        string typeName)
    {
        const int width = 900;
        const int height = 600;
        System.Windows.Media.Color color = ParsePreviewColor(previewColor);
        string categoryName = familyDocument.OwnerFamily?.FamilyCategory?.Name ?? "Annotation";
        var title = new TextBlock
        {
            Text = typeName,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 64,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 700,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var category = new TextBlock
        {
            Text = categoryName,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 23,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 112, 138)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { title, category }
        };
        var frame = new Border
        {
            Width = 760,
            MinHeight = 260,
            Padding = new Thickness(46),
            CornerRadius = new CornerRadius(18),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 225, 237)),
            BorderThickness = new Thickness(2),
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var host = new System.Windows.Controls.Grid
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 251, 254)),
            Children = { frame }
        };
        host.Measure(new System.Windows.Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using FileStream output = File.Create(destinationPath);
        encoder.Save(output);
    }

    private static void CropAndCenter2DPreview(string sourcePath, string destinationPath)
    {
        BitmapSource source;
        using (FileStream input = File.OpenRead(sourcePath))
        {
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * 4);
                if (pixels[offset] > 244 && pixels[offset + 1] > 244 && pixels[offset + 2] > 244) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        if (maxX < minX || maxY < minY)
        {
            throw new InvalidOperationException("The Ref. Level view contains no visible geometry after dimensions and references were hidden.");
        }

        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        int padding = Math.Max(8, (int)Math.Ceiling(Math.Max(contentWidth, contentHeight) * 0.06));
        int cropX = Math.Max(0, minX - padding);
        int cropY = Math.Max(0, minY - padding);
        int cropWidth = Math.Min(width - cropX, contentWidth + (padding * 2));
        int cropHeight = Math.Min(height - cropY, contentHeight + (padding * 2));
        var crop = new CroppedBitmap(converted, new Int32Rect(cropX, cropY, cropWidth, cropHeight));

        const int outputWidth = 900;
        const int outputHeight = 600;
        const double outerMargin = 36;
        double scale = Math.Min(
            (outputWidth - (outerMargin * 2)) / crop.PixelWidth,
            (outputHeight - (outerMargin * 2)) / crop.PixelHeight);
        double drawWidth = crop.PixelWidth * scale;
        double drawHeight = crop.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, outputWidth, outputHeight));
            context.DrawImage(crop, new Rect(
                (outputWidth - drawWidth) / 2,
                (outputHeight - drawHeight) / 2,
                drawWidth,
                drawHeight));
        }
        var bitmap = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(destinationPath);
        encoder.Save(output);
    }

    private sealed class PreviewMeshData
    {
        public List<Point3D> Positions { get; } = [];
        public List<int> TriangleIndices { get; } = [];
        public List<Vector3D> Normals { get; } = [];

        public void AddTriangle(Point3D a, Point3D b, Point3D c)
        {
            Vector3D normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.LengthSquared < 1e-16) return;
            normal.Normalize();
            int start = Positions.Count;
            Positions.Add(a);
            Positions.Add(b);
            Positions.Add(c);
            Normals.Add(normal);
            Normals.Add(normal);
            Normals.Add(normal);
            TriangleIndices.Add(start);
            TriangleIndices.Add(start + 1);
            TriangleIndices.Add(start + 2);
        }

        public void Normalize()
        {
            if (Positions.Count == 0) return;
            double minX = Positions.Min(point => point.X);
            double minY = Positions.Min(point => point.Y);
            double minZ = Positions.Min(point => point.Z);
            double maxX = Positions.Max(point => point.X);
            double maxY = Positions.Max(point => point.Y);
            double maxZ = Positions.Max(point => point.Z);
            var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
            double largestDimension = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            double scale = largestDimension > 1e-9 ? 2.0 / largestDimension : 1.0;
            for (int index = 0; index < Positions.Count; index++)
            {
                Vector3D centered = Positions[index] - center;
                Positions[index] = new Point3D(centered.X * scale, centered.Y * scale, centered.Z * scale);
            }
        }

        public double CenterAndFitToCamera(Vector3D lookDirection, Vector3D upDirection, double aspectRatio)
        {
            Vector3D forward = lookDirection;
            forward.Normalize();
            Vector3D right = Vector3D.CrossProduct(forward, upDirection);
            right.Normalize();
            Vector3D screenUp = Vector3D.CrossProduct(right, forward);
            screenUp.Normalize();

            double minRight = double.PositiveInfinity;
            double maxRight = double.NegativeInfinity;
            double minUp = double.PositiveInfinity;
            double maxUp = double.NegativeInfinity;
            foreach (Point3D point in Positions)
            {
                var vector = new Vector3D(point.X, point.Y, point.Z);
                double horizontal = Vector3D.DotProduct(vector, right);
                double vertical = Vector3D.DotProduct(vector, screenUp);
                minRight = Math.Min(minRight, horizontal);
                maxRight = Math.Max(maxRight, horizontal);
                minUp = Math.Min(minUp, vertical);
                maxUp = Math.Max(maxUp, vertical);
            }

            double horizontalCenter = (minRight + maxRight) / 2;
            double verticalCenter = (minUp + maxUp) / 2;
            Vector3D shift = (-horizontalCenter * right) + (-verticalCenter * screenUp);
            for (int index = 0; index < Positions.Count; index++) Positions[index] += shift;

            double projectedWidth = maxRight - minRight;
            double projectedHeight = maxUp - minUp;
            return Math.Max(0.25, Math.Max(projectedWidth, projectedHeight * aspectRatio) * 1.16);
        }
    }

    private static bool IsPreviewCacheFresh(string previewPath, string familyPath)
    {
        if (!File.Exists(previewPath) || !File.Exists(familyPath)) return false;
        return File.GetLastWriteTimeUtc(previewPath) >= File.GetLastWriteTimeUtc(familyPath);
    }

    public static bool RequiresTwoDimensionalPreview(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        string[] twoDimensionalTerms =
        [
            "Tag", "Annotation", "Detail Item", "Detail Component", "Symbol",
            "View Title", "Section Head", "Elevation Mark", "Callout Head", "Keynote"
        ];
        return twoDimensionalTerms.Any(term =>
            category.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPreviewPath(
        string familyPath,
        string previewColor,
        bool useTwoDimensionalCache)
    {
        string key = PortableFramework.StableHash16(Path.GetFullPath(familyPath).ToUpperInvariant());
        string colorKey = NormalizePreviewColor(previewColor).TrimStart('#');
        string name = string.Concat(Path.GetFileNameWithoutExtension(familyPath)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string cacheVersion = useTwoDimensionalCache ? Preview2DCacheVersion : PreviewCacheVersion;
        return Path.Combine(AppPaths.PreviewFolder, $"{name}_{key}_{cacheVersion}_{colorKey}.png");
    }

    private static string NormalizePreviewColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultPreviewColor;
        string color = value.Trim().ToUpperInvariant();
        if (!color.StartsWith("#", StringComparison.Ordinal)) color = "#" + color;
        return color.Length == 7 && color.Skip(1).All(Uri.IsHexDigit)
            ? color
            : DefaultPreviewColor;
    }

    private static System.Windows.Media.Color ParsePreviewColor(string? value)
    {
        string color = NormalizePreviewColor(value);
        return System.Windows.Media.Color.FromRgb(
            Convert.ToByte(color.Substring(1, 2), 16),
            Convert.ToByte(color.Substring(3, 2), 16),
            Convert.ToByte(color.Substring(5, 2), 16));
    }

    private static void CleanPreviewImage(string sourcePath, string destinationPath)
    {
        BitmapSource source;
        using (FileStream input = File.OpenRead(sourcePath))
        {
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var mask = new bool[width * height];
        for (int pixelIndex = 0; pixelIndex < mask.Length; pixelIndex++)
        {
            int index = pixelIndex * 4;
            int blue = pixels[index];
            int green = pixels[index + 1];
            int red = pixels[index + 2];
            int maximum = Math.Max(red, Math.Max(green, blue));
            int minimum = Math.Min(red, Math.Min(green, blue));
            int spread = maximum - minimum;
            double saturation = maximum == 0 ? 0 : spread / (double)maximum;

            // Revit connector axes, reference controls and parameter graphics use saturated
            // primary colors. Mark them for removal instead of painting over the model.
            if (maximum >= 60 && spread >= 38 && saturation >= 0.20)
            {
                mask[pixelIndex] = true;
            }
        }

        // Include antialiased fringes around connector/reference graphics.
        bool[] expandedMask = (bool[])mask.Clone();
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int pixelIndex = (y * width) + x;
                if (!mask[pixelIndex]) continue;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        expandedMask[pixelIndex + (offsetY * width) + offsetX] = true;
                    }
                }
            }
        }
        mask = expandedMask;

        // Fill the mask from its boundary inward. A queue visits each masked pixel once,
        // avoiding a costly full-image scan for every layer of a thick connector graphic.
        var fillQueue = new Queue<int>();
        var queued = new bool[mask.Length];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int pixelIndex = (y * width) + x;
                if (!mask[pixelIndex] || !HasUnmaskedNeighbor(mask, width, pixelIndex)) continue;
                fillQueue.Enqueue(pixelIndex);
                queued[pixelIndex] = true;
            }
        }

        while (fillQueue.Count > 0)
        {
            int pixelIndex = fillQueue.Dequeue();
            if (!mask[pixelIndex]) continue;
            int blueTotal = 0;
            int greenTotal = 0;
            int redTotal = 0;
            int count = 0;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0) continue;
                    int neighbor = pixelIndex + (offsetY * width) + offsetX;
                    if (mask[neighbor]) continue;
                    int neighborOffset = neighbor * 4;
                    blueTotal += pixels[neighborOffset];
                    greenTotal += pixels[neighborOffset + 1];
                    redTotal += pixels[neighborOffset + 2];
                    count++;
                }
            }
            if (count == 0) continue;

            int pixelOffset = pixelIndex * 4;
            pixels[pixelOffset] = (byte)(blueTotal / count);
            pixels[pixelOffset + 1] = (byte)(greenTotal / count);
            pixels[pixelOffset + 2] = (byte)(redTotal / count);
            pixels[pixelOffset + 3] = 255;
            mask[pixelIndex] = false;

            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int neighbor = pixelIndex + (offsetY * width) + offsetX;
                    if (mask[neighbor] && !queued[neighbor])
                    {
                        fillQueue.Enqueue(neighbor);
                        queued[neighbor] = true;
                    }
                }
            }
        }

        // Any isolated remainder has no reliable neighboring model color and is background.
        for (int pixelIndex = 0; pixelIndex < mask.Length; pixelIndex++)
        {
            if (!mask[pixelIndex]) continue;
            int pixelOffset = pixelIndex * 4;
            pixels[pixelOffset] = 250;
            pixels[pixelOffset + 1] = 250;
            pixels[pixelOffset + 2] = 250;
            pixels[pixelOffset + 3] = 255;
        }

        var cleaned = new WriteableBitmap(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null);
        cleaned.WritePixels(
            new Int32Rect(0, 0, converted.PixelWidth, converted.PixelHeight),
            pixels,
            stride,
            0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cleaned));
        using FileStream output = File.Create(destinationPath);
        encoder.Save(output);
    }

    private static bool HasUnmaskedNeighbor(bool[] mask, int width, int pixelIndex)
    {
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0) continue;
                if (!mask[pixelIndex + (offsetY * width) + offsetX]) return true;
            }
        }
        return false;
    }

    private static void HidePreviewClutter(
        Document document,
        View view,
        ISet<ElementId> geometryElements)
    {
        string[] categoryNames =
        [
            "OST_CLines",
            "OST_ReferencePoints",
            "OST_Dimensions",
            "OST_Constraints",
            "OST_IOSSketchGrid",
            "OST_Levels",
            "OST_Grids",
            "OST_ConnectorElem",
            "OST_Connectors",
            "OST_MEPConnectors",
            "OST_DuctConnectors",
            "OST_PipeConnectors",
            "OST_ElectricalConnectors"
        ];
        foreach (string categoryName in categoryNames)
        {
            try
            {
                if (!Enum.TryParse(categoryName, out BuiltInCategory builtInCategory)) continue;
                Category? category = Category.GetCategory(document, builtInCategory);
                if (category is not null) HideOrSoftenCategory(view, category);
            }
            catch
            {
                // Category availability varies by family template.
            }
        }

        try
        {
            foreach (Category category in document.Settings.Categories)
            {
                if (category.Name.Contains("connector", StringComparison.OrdinalIgnoreCase))
                {
                    HideOrSoftenCategory(view, category);
                }
            }
        }
        catch
        {
            // Some family templates expose a partial category collection.
        }

        var elementsToHide = new List<ElementId>();
        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            try
            {
                if (!geometryElements.Contains(element.Id) && element.CanBeHidden(view))
                {
                    elementsToHide.Add(element.Id);
                }
                else if (IsConnectorLike(element) && !element.CanBeHidden(view))
                {
                    SoftenElement(view, element.Id);
                }
            }
            catch
            {
                // Ignore internal elements that cannot participate in view visibility.
            }
        }
        if (elementsToHide.Count > 0)
        {
            try { view.HideElements(elementsToHide); } catch { }
        }
    }

    private static HashSet<ElementId> FindGeometryElements(Document document, View view)
    {
        var result = new HashSet<ElementId>();
        var options = new Options
        {
            View = view,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };
        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            try
            {
                if (IsPreviewClutter(element)) continue;
                GeometryElement? geometry = element.get_Geometry(options);
                if (geometry is not null && HasRenderableGeometry(geometry)) result.Add(element.Id);
            }
            catch
            {
                // Internal elements and view helpers can reject geometry extraction.
            }
        }
        return result;
    }

    private static HashSet<ElementId> FindModelElementsWithBounds(Document document)
    {
        var result = new HashSet<ElementId>();
        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            try
            {
                if (IsPreviewClutter(element)) continue;
                Category? category = element.Category;
                if (category is null || category.CategoryType != CategoryType.Model) continue;
                if (element.get_BoundingBox(null) is not null) result.Add(element.Id);
            }
            catch
            {
                // Skip internal elements without stable family geometry.
            }
        }
        return result;
    }

    private static bool HasRenderableGeometry(IEnumerable<GeometryObject> geometry)
    {
        foreach (GeometryObject item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Faces.Size > 0 && solid.Edges.Size > 0:
                    return true;
                case Mesh mesh when mesh.NumTriangles > 0:
                    return true;
                case GeometryInstance instance when HasRenderableGeometry(instance.GetInstanceGeometry()):
                    return true;
            }
        }
        return false;
    }

    private static BoundingBoxXYZ? GetModelBounds(
        Document document,
        IReadOnlyCollection<ElementId> geometryElements)
    {
        XYZ? minimum = null;
        XYZ? maximum = null;
        foreach (ElementId elementId in geometryElements)
        {
            try
            {
                Element? element = document.GetElement(elementId);
                if (element is null) continue;
                BoundingBoxXYZ? box = element.get_BoundingBox(null);
                if (box is null) continue;
                minimum = minimum is null
                    ? box.Min
                    : new XYZ(
                        Math.Min(minimum.X, box.Min.X),
                        Math.Min(minimum.Y, box.Min.Y),
                        Math.Min(minimum.Z, box.Min.Z));
                maximum = maximum is null
                    ? box.Max
                    : new XYZ(
                        Math.Max(maximum.X, box.Max.X),
                        Math.Max(maximum.Y, box.Max.Y),
                        Math.Max(maximum.Z, box.Max.Z));
            }
            catch
            {
                // Some internal family elements do not expose a usable bounding box.
            }
        }

        if (minimum is null || maximum is null) return null;
        double paddingX = Math.Max((maximum.X - minimum.X) * 0.12, 0.1);
        double paddingY = Math.Max((maximum.Y - minimum.Y) * 0.12, 0.1);
        double paddingZ = Math.Max((maximum.Z - minimum.Z) * 0.12, 0.1);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(minimum.X - paddingX, minimum.Y - paddingY, minimum.Z - paddingZ),
            Max = new XYZ(maximum.X + paddingX, maximum.Y + paddingY, maximum.Z + paddingZ)
        };
    }

    private static bool IsPreviewClutter(Element element)
    {
        if (IsConnectorLike(element)) return true;
        string typeName = element.GetType().Name;
        if (typeName is "Dimension" or "ReferencePlane" or "SketchPlane" or "Level" or "Grid"
            or "ConnectorElement" or "Control" or "SpatialElement")
        {
            return true;
        }

        Category? category = element.Category;
        if (category is null) return false;
        if (category.CategoryType == CategoryType.Annotation) return true;
        string name = category.Name ?? string.Empty;
        string[] excludedTerms =
        [
            "Reference", "Dimension", "Constraint", "Connector", "Analytical",
            "Sketch Grid", "Level", "Grid", "Center Line"
        ];
        return excludedTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Is2DPreviewClutter(Element element, bool preserveAnnotationContent)
    {
        if (IsConnectorLike(element)) return true;
        string typeName = element.GetType().Name;
        if (typeName is "Dimension" or "ReferencePlane" or "SketchPlane" or "Level" or "Grid"
            or "ConnectorElement" or "Control" or "SpatialElement")
        {
            return true;
        }
        if (!preserveAnnotationContent && typeName is "TextNote" or "IndependentTag") return true;

        string categoryName = element.Category?.Name ?? string.Empty;
        string[] excludedTerms =
        [
            "Reference", "Dimension", "Constraint", "Connector", "Analytical",
            "Sketch Grid", "Level", "Grid", "Center Line"
        ];
        if (excludedTerms.Any(term => categoryName.Contains(term, StringComparison.OrdinalIgnoreCase))) return true;
        return !preserveAnnotationContent
            && (categoryName.Contains("Text", StringComparison.OrdinalIgnoreCase)
                || categoryName.Contains("Tag", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConnectorLike(Element element)
    {
        try
        {
            if (element is ConnectorElement) return true;
        }
        catch { }

        try
        {
            if (element.GetType().Name.Contains("connector", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }

        try
        {
            if (element.Category?.Name.Contains("connector", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        catch { }

        try
        {
            string name = element.Name ?? string.Empty;
            if (name.Contains("connector", StringComparison.OrdinalIgnoreCase)
                || name.Contains("diameter", StringComparison.OrdinalIgnoreCase)
                || name.Contains("radius", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void HideOrSoftenCategory(View view, Category category)
    {
        try
        {
            if (view.CanCategoryBeHidden(category.Id))
            {
                view.SetCategoryHidden(category.Id, true);
                return;
            }
        }
        catch { }

        try
        {
            var overrides = CreateSoftConnectorOverrides();
            view.SetCategoryOverrides(category.Id, overrides);
        }
        catch { }
    }

    private static void SoftenElement(View view, ElementId elementId)
    {
        try { view.SetElementOverrides(elementId, CreateSoftConnectorOverrides()); }
        catch { }
    }

    private static OverrideGraphicSettings CreateSoftConnectorOverrides()
    {
        var overrides = new OverrideGraphicSettings();
        try { overrides.SetProjectionLineWeight(1); } catch { }
        try { overrides.SetProjectionLineColor(new Autodesk.Revit.DB.Color(248, 249, 252)); } catch { }
        try { overrides.SetSurfaceTransparency(100); } catch { }
        try { overrides.SetHalftone(true); } catch { }
        return overrides;
    }

    private static void CenterPreviewCamera(View3D view, BoundingBoxXYZ bounds)
    {
        XYZ center = (bounds.Min + bounds.Max) * 0.5;
        XYZ size = bounds.Max - bounds.Min;
        double distance = Math.Max(Math.Max(size.X, size.Y), size.Z) * 3.0;
        distance = Math.Max(distance, 1.0);
        XYZ forward = new XYZ(-1.0, 1.0, -0.75).Normalize();
        XYZ eye = center - forward * distance;
        view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward));
    }

    private static void PreviewAddShared(
        UIApplication uiApplication,
        FamilyItem family,
        BatchOperationRequest request,
        IReadOnlyCollection<FamilyParameter> parameters,
        BatchFamilyPreviewResult result)
    {
        List<string> names = SharedDefinitionNames(request.Source);
        Dictionary<string, ExternalDefinition> definitions = FindExternalDefinitions(
            uiApplication.Application,
            request.SharedParameterFile,
            names);
        foreach (string name in names)
        {
            definitions.TryGetValue(name, out ExternalDefinition? definition);
            bool guidExists = definition is not null && parameters.Any(item => item.IsShared && item.GUID == definition.GUID);
            bool nameExists = definition is not null && parameters.Any(item => item.Definition.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase));
            string validation = definition is null
                ? "Error"
                : guidExists || nameExists
                    ? request.ConflictHandling == "Stop on conflict" ? "Error" : "Skip"
                    : "Ready";
            string message = definition is null
                ? "The shared parameter definition was not found."
                : guidExists
                    ? "This shared parameter GUID already exists."
                    : nameExists
                        ? "A parameter with the same name already exists. Shared definition names cannot receive an automatic suffix."
                        : string.Empty;
            result.Changes.Add(Change(family, name, string.Empty, name, validation, "Add", message));
        }
    }

    private static void PreviewReplaceShared(
        UIApplication uiApplication,
        FamilyItem family,
        BatchOperationRequest request,
        IReadOnlyCollection<FamilyParameter> parameters,
        BatchFamilyPreviewResult result)
    {
        ExternalDefinition? definition = FindExternalDefinition(uiApplication.Application, request.SharedParameterFile, request.Target);
        foreach (FamilyParameter parameter in parameters.Where(item => NameMatches(item.Definition.Name, request.Source, request.MatchRule)))
        {
            bool guidConflict = definition is not null && parameters.Any(item => !ReferenceEquals(item, parameter) && item.IsShared && item.GUID == definition.GUID);
            string validation = definition is null ? "Error" : parameter.IsShared || guidConflict ? "Skip" : "Warning";
            string message = definition is null
                ? "The shared parameter definition was not found."
                : parameter.IsShared
                    ? "The source parameter is already shared."
                    : guidConflict
                        ? "The target shared parameter GUID already exists elsewhere in this family."
                        : "Values and formulas will be preserved where Revit permits.";
            result.Changes.Add(Change(family, parameter.Definition.Name, parameter.Definition.Name, request.Target, validation, "Replace", message));
        }
    }

    private static void ApplyBatchChanges(
        UIApplication uiApplication,
        Document document,
        FamilyManager manager,
        BatchOperationRequest request,
        IReadOnlyCollection<BatchChangeItem> changes)
    {
        List<FamilyParameter> parameters = manager.Parameters.Cast<FamilyParameter>().ToList();
        switch (request.Kind)
        {
            case BatchOperationKind.RenameParameter:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyParameter? parameter = parameters.FirstOrDefault(item => item.Definition.Name.Equals(change.CurrentValue, StringComparison.OrdinalIgnoreCase));
                    if (parameter is not null) manager.RenameParameter(parameter, change.NewValue);
                }
                break;

            case BatchOperationKind.AddSharedParameter:
            {
                List<string> names = changes.Select(item => item.NewValue)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, ExternalDefinition> definitions = FindExternalDefinitions(
                    uiApplication.Application,
                    request.SharedParameterFile,
                    names);
                foreach (string name in names)
                {
                    if (!definitions.TryGetValue(name, out ExternalDefinition? definition))
                        throw new InvalidOperationException($"The shared parameter definition '{name}' was not found.");
                    AddSharedParameter(manager, definition, request.ParameterGroup, request.IsInstance);
                }
                break;
            }

            case BatchOperationKind.ReplaceParameter:
            {
                ExternalDefinition definition = FindExternalDefinition(uiApplication.Application, request.SharedParameterFile, request.Target)
                    ?? throw new InvalidOperationException("The shared parameter definition was not found.");
                foreach (BatchChangeItem change in changes)
                {
                    FamilyParameter? parameter = parameters.FirstOrDefault(item => item.Definition.Name.Equals(change.CurrentValue, StringComparison.OrdinalIgnoreCase));
                    if (parameter is not null) ReplaceWithSharedParameter(manager, parameter, definition, request.ParameterGroup, request.IsInstance);
                }
                break;
            }

            case BatchOperationKind.SetValue:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyParameter? parameter = parameters.FirstOrDefault(item => item.Definition.Name.Equals(change.ItemName, StringComparison.OrdinalIgnoreCase));
                    if (parameter is null) continue;
                    foreach (FamilyType type in manager.Types.Cast<FamilyType>().ToList())
                    {
                        manager.CurrentType = type;
                        SetFamilyParameterValue(document, manager, parameter, request.Target);
                    }
                }
                break;

            case BatchOperationKind.SetFormula:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyParameter? parameter = parameters.FirstOrDefault(item => item.Definition.Name.Equals(change.ItemName, StringComparison.OrdinalIgnoreCase));
                    if (parameter is not null) manager.SetFormula(parameter, request.Target);
                }
                break;

            case BatchOperationKind.RemoveParameter:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyParameter? parameter = parameters.FirstOrDefault(item => item.Definition.Name.Equals(change.ItemName, StringComparison.OrdinalIgnoreCase));
                    if (parameter is not null) manager.RemoveParameter(parameter);
                }
                break;

            case BatchOperationKind.RenameTypes:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyType? type = manager.Types.Cast<FamilyType>().FirstOrDefault(item => item.Name.Equals(change.CurrentValue, StringComparison.OrdinalIgnoreCase));
                    if (type is null) continue;
                    manager.CurrentType = type;
                    manager.RenameCurrentType(change.NewValue);
                }
                break;

            case BatchOperationKind.CreateTypes:
            {
                BatchChangeItem change = changes.First();
                FamilyType? baseType = manager.Types.Cast<FamilyType>().FirstOrDefault(item => item.Name.Equals(change.CurrentValue, StringComparison.OrdinalIgnoreCase));
                if (baseType is not null) manager.CurrentType = baseType;
                manager.NewType(change.NewValue);
                break;
            }

            case BatchOperationKind.DeleteTypes:
                foreach (BatchChangeItem change in changes)
                {
                    FamilyType? type = manager.Types.Cast<FamilyType>().FirstOrDefault(item => item.Name.Equals(change.CurrentValue, StringComparison.OrdinalIgnoreCase));
                    if (type is null) continue;
                    manager.CurrentType = type;
                    manager.DeleteCurrentType();
                }
                break;

            case BatchOperationKind.ChangeCategory:
            {
                Category target = document.Settings.Categories.Cast<Category>().FirstOrDefault(item => item.Name.Equals(request.Target, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Revit category '{request.Target}' was not found.");
                if (document.OwnerFamily is null) throw new InvalidOperationException("The family owner is unavailable.");
                document.OwnerFamily.FamilyCategory = target;
                break;
            }
        }
    }

    private static void SetFamilyParameterValue(Document document, FamilyManager manager, FamilyParameter parameter, string value)
    {
        switch (parameter.StorageType)
        {
            case StorageType.String:
                manager.Set(parameter, value);
                break;
            case StorageType.Integer:
                if (bool.TryParse(value, out bool boolean)) manager.Set(parameter, boolean ? 1 : 0);
                else if (int.TryParse(value, out int integer)) manager.Set(parameter, integer);
                else throw new InvalidOperationException($"'{value}' is not a valid integer or Yes/No value.");
                break;
            case StorageType.Double:
#if REVIT2020 || REVIT2021
                if (!UnitFormatUtils.TryParse(document.GetUnits(), parameter.Definition.UnitType, value, out double number))
                    throw new InvalidOperationException($"'{value}' is not a valid {ParameterDataTypeLabel(parameter.Definition)} value.");
#else
                if (!UnitFormatUtils.TryParse(document.GetUnits(), parameter.Definition.GetDataType(), value, out double number))
                    throw new InvalidOperationException($"'{value}' is not a valid {ForgeTypeLabel(parameter.Definition.GetDataType())} value.");
#endif
                manager.Set(parameter, number);
                break;
            default:
                throw new InvalidOperationException("ElementId/material values are not supported by this batch value editor.");
        }
    }

    private static void AddSharedParameter(FamilyManager manager, ExternalDefinition definition, string group, bool isInstance)
    {
#if REVIT2020 || REVIT2021
        manager.AddParameter(definition, ResolveLegacyParameterGroup(group), isInstance);
#else
        manager.AddParameter(definition, ResolveParameterGroup(group), isInstance);
#endif
    }

    private static void ReplaceWithSharedParameter(FamilyManager manager, FamilyParameter parameter, ExternalDefinition definition, string group, bool isInstance)
    {
#if REVIT2020 || REVIT2021
        manager.ReplaceParameter(parameter, definition, ResolveLegacyParameterGroup(group), isInstance);
#else
        manager.ReplaceParameter(parameter, definition, ResolveParameterGroup(group), isInstance);
#endif
    }

#if REVIT2020 || REVIT2021
    private static BuiltInParameterGroup ResolveLegacyParameterGroup(string group) => group switch
    {
        "Constraints" => BuiltInParameterGroup.PG_CONSTRAINTS,
        "Construction" => BuiltInParameterGroup.PG_CONSTRUCTION,
        "Data" => BuiltInParameterGroup.PG_DATA,
        "Identity Data" => BuiltInParameterGroup.PG_IDENTITY_DATA,
        "Materials and Finishes" => BuiltInParameterGroup.PG_MATERIALS,
        "Mechanical" => BuiltInParameterGroup.PG_MECHANICAL,
        "Electrical" => BuiltInParameterGroup.PG_ELECTRICAL,
        "Plumbing" => BuiltInParameterGroup.PG_PLUMBING,
        "Structural" => BuiltInParameterGroup.PG_STRUCTURAL,
        "Text" => BuiltInParameterGroup.PG_TEXT,
        "Other" => BuiltInParameterGroup.PG_GENERAL,
        _ => BuiltInParameterGroup.PG_GEOMETRY
    };
#else
    private static ForgeTypeId ResolveParameterGroup(string group) => group switch
    {
        "Constraints" => GroupTypeId.Constraints,
        "Construction" => GroupTypeId.Construction,
        "Data" => GroupTypeId.Data,
        "Identity Data" => GroupTypeId.IdentityData,
        "Materials and Finishes" => GroupTypeId.Materials,
        "Mechanical" => GroupTypeId.Mechanical,
        "Electrical" => GroupTypeId.Electrical,
        "Plumbing" => GroupTypeId.Plumbing,
        "Structural" => GroupTypeId.Structural,
        "Text" => GroupTypeId.Text,
        "Other" => GroupTypeId.General,
        _ => GroupTypeId.Geometry
    };
#endif

    private static ExternalDefinition? FindExternalDefinition(
        Autodesk.Revit.ApplicationServices.Application application,
        string filePath,
        string definitionName)
    {
        Dictionary<string, ExternalDefinition> definitions = FindExternalDefinitions(application, filePath, [definitionName]);
        return definitions.TryGetValue(definitionName, out ExternalDefinition? definition) ? definition : null;
    }

    private static List<string> SharedDefinitionNames(string value) => value
        .Split([SharedDefinitionSeparator], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim())
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static Dictionary<string, ExternalDefinition> FindExternalDefinitions(
        Autodesk.Revit.ApplicationServices.Application application,
        string filePath,
        IReadOnlyCollection<string> definitionNames)
    {
        var result = new Dictionary<string, ExternalDefinition>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || definitionNames.Count == 0) return result;
        var requested = definitionNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string previous = application.SharedParametersFilename;
        try
        {
            application.SharedParametersFilename = filePath;
            DefinitionFile? file = application.OpenSharedParameterFile();
            if (file is null) return result;
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (ExternalDefinition definition in group.Definitions.Cast<Definition>().OfType<ExternalDefinition>())
                {
                    if (requested.Contains(definition.Name)) result[definition.Name] = definition;
                }
            }
            return result;
        }
        finally
        {
            application.SharedParametersFilename = previous;
        }
    }

    private static BatchChangeItem Change(
        FamilyItem family,
        string itemName,
        string currentValue,
        string newValue,
        string validation,
        string action,
        string message) => new()
        {
            FamilyPath = family.Path,
            FamilyName = family.FamilyName,
            Category = family.Category,
            ItemName = itemName,
            CurrentValue = currentValue,
            NewValue = newValue,
            Validation = validation,
            Action = action,
            Message = message,
            Selected = validation is "Ready" or "Warning"
        };

    private static bool NameMatches(string value, string source, string rule)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return rule switch
        {
            "Exact" => value.Equals(source, StringComparison.OrdinalIgnoreCase),
            "Starts With" => value.StartsWith(source, StringComparison.OrdinalIgnoreCase),
            "Ends With" => value.EndsWith(source, StringComparison.OrdinalIgnoreCase),
            _ => value.Contains(source, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string TransformName(string value, string source, string target, string rule)
    {
        if (!NameMatches(value, source, rule)) return value;
        return rule switch
        {
            "Exact" => target,
            "Starts With" => target + value.Substring(source.Length),
            "Ends With" => value.Substring(0, value.Length - source.Length) + target,
            _ => ReplaceInsensitive(value, source, target)
        };
    }

    private static (string Name, string Validation, string Message) ResolveNameConflict(
        string target,
        IEnumerable<string> existing,
        string policy)
    {
        if (policy == "Add numeric suffix")
        {
            HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
            int index = 1;
            string candidate;
            do candidate = $"{target}_{index++:000}"; while (names.Contains(candidate));
            return (candidate, "Warning", "A numeric suffix will be added to avoid a duplicate.");
        }
        return policy == "Stop on conflict"
            ? (target, "Error", "The target name already exists.")
            : (target, "Skip", "Skipped because the target name already exists.");
    }

    private static (string Path, string Validation, string Message) ResolveFileConflict(string targetPath, string policy)
    {
        if (policy == "Add numeric suffix")
        {
            string folder = Path.GetDirectoryName(targetPath)!;
            string name = Path.GetFileNameWithoutExtension(targetPath);
            int index = 1;
            string candidate;
            do candidate = Path.Combine(folder, $"{name}_{index++:000}.rfa"); while (File.Exists(candidate));
            return (candidate, "Warning", "A numeric suffix will be added to avoid a duplicate file.");
        }
        return policy == "Stop on conflict"
            ? (targetPath, "Error", "The target RFA file already exists.")
            : (targetPath, "Skip", "Skipped because the target RFA file already exists.");
    }

    private static string OperationAction(BatchOperationKind kind) => kind switch
    {
        BatchOperationKind.AddSharedParameter or BatchOperationKind.CreateTypes => "Add",
        BatchOperationKind.ReplaceParameter => "Replace",
        BatchOperationKind.RemoveParameter or BatchOperationKind.DeleteTypes => "Remove",
        BatchOperationKind.SetValue or BatchOperationKind.SetFormula or BatchOperationKind.ChangeCategory => "Set",
        _ => "Rename"
    };

    private static string OperationTitle(BatchOperationKind kind) => kind switch
    {
        BatchOperationKind.RenameParameter => "Rename parameter",
        BatchOperationKind.AddSharedParameter => "Add shared parameter",
        BatchOperationKind.ReplaceParameter => "Replace parameter",
        BatchOperationKind.SetValue => "Set parameter value",
        BatchOperationKind.SetFormula => "Set formula",
        BatchOperationKind.RemoveParameter => "Remove parameter",
        BatchOperationKind.RenameTypes => "Rename family types",
        BatchOperationKind.CreateTypes => "Create family type",
        BatchOperationKind.DeleteTypes => "Delete family types",
        BatchOperationKind.RenameFiles => "Rename RFA file",
        BatchOperationKind.ChangeCategory => "Change family category",
        BatchOperationKind.NamingStandard => "Apply naming standard",
        _ => "Batch edit"
    };

    private static BatchFamilyExecutionResult ExecutionLog(
        FamilyItem family,
        string level,
        string message,
        Stopwatch stopwatch,
        string? newPath = null)
    {
        stopwatch.Stop();
        return new BatchFamilyExecutionResult(new BatchLogItem
        {
            FamilyName = family.FamilyName,
            Level = level,
            Status = level,
            Message = message,
            Duration = $"{stopwatch.Elapsed.TotalSeconds:0.0}s"
        }, newPath);
    }

    private static BatchLogItem Log(FamilyItem family, string level, string message) => new()
    {
        FamilyName = family.FamilyName,
        Level = level,
        Status = level,
        Message = message
    };

    private static string SafeGroupName(FamilyParameter parameter)
    {
        try
        {
#if REVIT2020 || REVIT2021
            return parameter.Definition.ParameterGroup.ToString();
#else
            return parameter.Definition.GetGroupTypeId().TypeId;
#endif
        }
        catch { return "Other"; }
    }

    private static string SafeDataType(FamilyParameter parameter)
    {
        try
        {
#if REVIT2020 || REVIT2021
            return parameter.Definition.ParameterType.ToString();
#else
            return parameter.Definition.GetDataType().TypeId;
#endif
        }
        catch { return "Unknown"; }
    }

    private static string ReplaceInsensitive(string input, string oldValue, string newValue)
    {
        int index = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return input;
        return input.Substring(0, index) + newValue + input.Substring(index + oldValue.Length);
    }

    private static long ElementIdNumber(ElementId id)
    {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return id.IntegerValue;
#else
        return id.Value;
#endif
    }

    private static string DocumentKey(Document document) =>
        string.IsNullOrWhiteSpace(document.PathName)
            ? $"UNTITLED::{document.Title}"
            : Path.GetFullPath(document.PathName);

    private static string ProjectDisplayName(Document document) =>
        string.IsNullOrWhiteSpace(document.PathName)
            ? $"{document.Title} (unsaved)"
            : $"{document.Title} — {document.PathName}";

    private static Document? FindOpenProject(UIApplication uiApplication, string key) =>
        uiApplication.Application.Documents.Cast<Document>()
            .FirstOrDefault(document =>
                !document.IsFamilyDocument
                && string.Equals(DocumentKey(document), key, StringComparison.OrdinalIgnoreCase));

    private static Document? FindOpenProjectByPath(UIApplication uiApplication, string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        return uiApplication.Application.Documents.Cast<Document>()
            .FirstOrDefault(document =>
                !document.IsFamilyDocument
                && !string.IsNullOrWhiteSpace(document.PathName)
                && string.Equals(Path.GetFullPath(document.PathName), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "Unclassified" : sanitized.Trim();
    }

    private static string BuildCategoryFolderName(string category)
    {
        string normalized = category.Trim();
        string? code = normalized.ToUpperInvariant() switch
        {
            var value when value.Contains("DUCT ACCESSOR") => "DA",
            var value when value.Contains("DUCT FITTING") => "DF",
            var value when value.Contains("AIR TERMINAL") => "AT",
            var value when value.Contains("FIRE DAMPER") => "FD",
            var value when value.Contains("MECHANICAL EQUIPMENT") => "ME",
            var value when value.Contains("PIPE FITTING") => "PF",
            var value when value.Contains("PIPE ACCESSOR") => "PA",
            var value when value.Contains("PLUMBING FIXTURE") => "PL",
            var value when value.Contains("SPRINKLER") => "SP",
            var value when value.Contains("ELECTRICAL") || value.Contains("LIGHTING") => "EL",
            var value when value.Contains("CABLE") || value.Contains("CONDUIT") => "CF",
            _ => null
        };
        string folder = code is null ? normalized : $"{code} - {normalized}";
        return SanitizeFileName(folder);
    }

    private static void EnsureFolderOnF(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (!string.Equals(root, @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The export folder must be on drive F:.");
        }
        Directory.CreateDirectory(fullPath);
    }

    private static void EnsureRfa(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Family file was not found.", path);
        }
    }

    private static void EnsureRvt(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".rvt", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Revit project file was not found.", path);
        }
    }

    private static Document OpenStandardsDocument(UIApplication uiApplication, string path)
        => OpenLibraryScanDocument(uiApplication, path);

    private static Document OpenLibraryScanDocument(UIApplication uiApplication, string path)
    {
        var options = new OpenOptions();
        try
        {
            BasicFileInfo info = BasicFileInfo.Extract(path);
            if (info.IsWorkshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                var worksets = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
                options.SetOpenWorksetsConfiguration(worksets);
            }
        }
        catch
        {
            // Revit will provide the authoritative message if the RVT cannot be opened.
            // The targeted collectors below still avoid scanning model instances and geometry.
        }

        ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        return uiApplication.Application.OpenDocumentFile(modelPath, options);
    }

    private static List<ElementId> SafeViewFilters(View view)
    {
        try { return view.GetFilters().ToList(); }
        catch { return []; }
    }

    private static string FirstOverrideColor(View view, ElementId filterId)
    {
        try
        {
            OverrideGraphicSettings settings = view.GetFilterOverrides(filterId);
            Autodesk.Revit.DB.Color[] colors =
            [
                settings.ProjectionLineColor,
                settings.SurfaceForegroundPatternColor,
                settings.CutLineColor,
                settings.CutForegroundPatternColor
            ];
            Autodesk.Revit.DB.Color? color = colors.FirstOrDefault(candidate => candidate.IsValid);
            if (color is not null) return $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        }
        catch { }
        return "#CBD5E1";
    }

    private static List<string> CategoryNames(Document document, ICollection<ElementId> categoryIds)
    {
        Dictionary<long, string> names = document.Settings.Categories
            .Cast<Category>()
            .ToDictionary(category => ElementIdNumber(category.Id), category => category.Name);
        return categoryIds
            .Select(id => names.TryGetValue(ElementIdNumber(id), out string? name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool HasNamedViewStandard(Document document, ViewStandardItem item)
    {
        if (item.Kind == ViewStandardKind.ViewTemplate)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(view => view.IsTemplate && view.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        }

        return new FilteredElementCollector(document)
            .OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .Any(filter => filter.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static ElementId CreateElementId(long value)
    {
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
        return PortableApi.ElementId(value);
#else
        return PortableApi.ElementId(checked((int)value));
#endif
    }

    private static void EnsureVersionCompatible(UIApplication uiApplication, string path)
    {
        int currentVersion = 0;
        int.TryParse(uiApplication.Application.VersionNumber, out currentVersion);
        if (currentVersion <= 0) return;
        try
        {
            string format = BasicFileInfo.Extract(path).Format ?? string.Empty;
            string yearText = new(format.Where(char.IsDigit).Take(4).ToArray());
            if (int.TryParse(yearText, out int familyVersion) && familyVersion > currentVersion)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}' was saved in Revit {familyVersion} and cannot be opened by Revit {currentVersion}. "
                    + "Choose the same or a newer Revit version.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Revit {currentVersion} cannot read the version information for '{Path.GetFileName(path)}'.",
                exception);
        }
    }

    private static void EnsureWritableOnF(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        if (!string.Equals(root, @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Batch Edit is restricted to family files on drive F:.");
        }
    }

    private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    private sealed class UseDestinationDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) =>
            DuplicateTypeAction.UseDestinationTypes;
    }
}
