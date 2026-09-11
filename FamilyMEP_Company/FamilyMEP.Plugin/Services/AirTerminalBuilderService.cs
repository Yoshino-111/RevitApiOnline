using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class AirTerminalBuilderService
{
    public static AirTerminalInspection Inspect(
        UIApplication uiApplication,
        AirTerminalDefinition definition)
    {
        if (definition.IsProcedural)
            return TroxRfdBuilderService.Inspect();
        if (!File.Exists(definition.SourcePath))
            throw new FileNotFoundException(
                $"Official source RFA was not found for {definition.DisplayName}.",
                definition.SourcePath);

        Document? document = null;
        try
        {
            document = uiApplication.Application.OpenDocumentFile(definition.SourcePath);
            ValidateFamilyDocument(document);
            FamilyManager manager = document.FamilyManager;
            List<string> typeNames = manager.Types
                .Cast<FamilyType>()
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (typeNames.Count == 0)
                throw new InvalidOperationException("The official RFA contains no Family Types.");

            return new AirTerminalInspection
            {
                CategoryName = document.OwnerFamily?.FamilyCategory?.Name ?? "Unclassified",
                ConnectorCount = ConnectorCount(document),
                TypeNames = typeNames
            };
        }
        finally
        {
            if (document is not null)
            {
                try { document.Close(false); }
                catch { }
            }
        }
    }

    public static AirTerminalBuilderResult Execute(
        UIApplication uiApplication,
        AirTerminalBuilderRequest request)
    {
        if (request.Definition.IsProcedural)
            return TroxRfdBuilderService.Execute(
                uiApplication,
                request);
        UIDocument? uiDocument = uiApplication.ActiveUIDocument;
        Document project = uiDocument?.Document
            ?? throw new InvalidOperationException(
                "Open an RVT project before creating an Air Terminal Family.");
        if (project.IsFamilyDocument)
            throw new InvalidOperationException(
                "Start Air Terminal Builder from an RVT project.");
        if (!File.Exists(request.Definition.SourcePath))
            throw new FileNotFoundException(
                "The official Air Terminal source RFA was not found.",
                request.Definition.SourcePath);

        Document? familyDocument = null;
        try
        {
            familyDocument = uiApplication.Application.OpenDocumentFile(
                request.Definition.SourcePath);
            ValidateFamilyDocument(familyDocument);

            var result = new AirTerminalBuilderResult
            {
                FamilyName = request.Definition.OutputFamilyName,
                ActiveTypeName = request.PreviewTypeName
            };

            using (Transaction transaction = new(
                       familyDocument,
                       "FamilyMEP - Normalize Air Terminal"))
            {
                transaction.Start();
                FamilyManager manager = familyDocument.FamilyManager;
                FamilyType? selectedType = manager.Types
                    .Cast<FamilyType>()
                    .FirstOrDefault(type =>
                        type.Name.Equals(
                            request.PreviewTypeName,
                            StringComparison.OrdinalIgnoreCase));
                if (selectedType is null)
                    throw new InvalidOperationException(
                        $"Type '{request.PreviewTypeName}' is not present in the official RFA.");

                manager.CurrentType = selectedType;
                SetMetricFamilyUnits(familyDocument);
                ApplyNeutralGrayMaterials(familyDocument, manager);
                ClearManufacturerInformation(manager);
                int parameterCount = RenameCompactParameters(manager);
                int lookupCount = RenameLookupTables(
                    familyDocument,
                    manager,
                    request.Definition.OutputFamilyName,
                    result);

                selectedType = manager.Types
                    .Cast<FamilyType>()
                    .First(type =>
                        type.Name.Equals(
                            request.PreviewTypeName,
                            StringComparison.OrdinalIgnoreCase));
                manager.CurrentType = selectedType;
                familyDocument.Regenerate();
                transaction.Commit();

                result.AppliedChanges.Add("Display units -> millimetres");
                result.AppliedChanges.Add("Manufacturer-authored geometry and all source Types preserved");
                result.AppliedChanges.Add("Materials -> neutral gray");
                result.AppliedChanges.Add("Manufacturer / model / URL / description -> blank");
                result.AppliedChanges.Add(
                    $"{parameterCount} editable custom parameters -> compact FT_* names");
                result.AppliedChanges.Add(
                    $"{lookupCount} embedded lookup tables -> Family-name prefix");
            }

            result.CategoryName =
                familyDocument.OwnerFamily?.FamilyCategory?.Name ?? "Unclassified";
            result.ConnectorCount = ConnectorCount(familyDocument);
            result.TypeCount = familyDocument.FamilyManager.Types.Cast<FamilyType>().Count();
            if (result.ConnectorCount == 0)
                result.Warnings.Add("No duct connector was found in the official source RFA.");

            result.OutputPath = SaveGeneratedCopy(
                familyDocument,
                request.OutputPath,
                request.Definition.OutputFamilyName);
            if (request.LoadIntoProject)
                familyDocument.LoadFamily(project, new OverwriteFamilyLoadOptions());
            return result;
        }
        finally
        {
            if (familyDocument is not null)
            {
                try { familyDocument.Close(false); }
                catch { }
            }
        }
    }

    private static void ValidateFamilyDocument(Document document)
    {
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("The selected source is not a Revit Family.");
        Category? category = document.OwnerFamily?.FamilyCategory;
        if (category is null || !IsAirTerminalCategory(category))
        {
            throw new InvalidOperationException(
                $"The official source category is '{category?.Name ?? "Unclassified"}', not Air Terminals.");
        }
    }

    private static bool IsAirTerminalCategory(Category category)
    {
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
        return category.Id.Value == (long)BuiltInCategory.OST_DuctTerminal;
#else
        return category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTerminal;
#endif
    }

    private static int ConnectorCount(Document document) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(ConnectorElement))
            .GetElementCount();

    private static void SetMetricFamilyUnits(Document familyDocument)
    {
        Units units = familyDocument.GetUnits();
        units.SetFormatOptions(
            SpecTypeId.Length,
            new FormatOptions(UnitTypeId.Millimeters)
            {
                Accuracy = 0.1,
                UseDigitGrouping = false
            });
        familyDocument.SetUnits(units);
    }

    private static void ApplyNeutralGrayMaterials(
        Document familyDocument,
        FamilyManager manager)
    {
        ElementId grayMaterialId = EnsureMaterial(
            familyDocument,
            "FamilyMEP - Neutral Gray");
        foreach (Material material in new FilteredElementCollector(familyDocument)
                     .OfClass(typeof(Material))
                     .Cast<Material>())
        {
            try
            {
                material.Color = new Autodesk.Revit.DB.Color(160, 160, 160);
                material.Transparency = 0;
            }
            catch { }
        }

        List<FamilyParameter> materialParameters = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter =>
                parameter.Definition.Name.Contains(
                    "Material",
                    StringComparison.OrdinalIgnoreCase)
                || parameter.Definition.Name.Contains(
                    "Finish",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (FamilyType type in manager.Types.Cast<FamilyType>().ToList())
        {
            manager.CurrentType = type;
            foreach (FamilyParameter parameter in materialParameters)
            {
                if (parameter.IsReadOnly || !string.IsNullOrWhiteSpace(parameter.Formula))
                    continue;
                try
                {
                    if (parameter.StorageType == StorageType.ElementId)
                        manager.Set(parameter, grayMaterialId);
                    else if (parameter.StorageType == StorageType.String)
                        manager.Set(parameter, string.Empty);
                }
                catch { }
            }
        }
    }

    private static ElementId EnsureMaterial(Document document, string name)
    {
        Material? material = new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return material?.Id ?? Material.Create(document, name);
    }

    private static void ClearManufacturerInformation(FamilyManager manager)
    {
        HashSet<string> targets = new(
            [
                "Manufacturer",
                "Model",
                "URL",
                "Product Page URL",
                "Series",
                "Product Code",
                "Material Number",
                "Description",
                "Standards",
                "Assembly Code",
                "Type Image",
                "Keynote",
                "Type Comments",
                "Cost",
                "Catalog URL",
                "Catalog Revision",
                "Catalog Dimension Basis",
                "Geometry Accuracy",
                "LOD Status"
            ],
            StringComparer.OrdinalIgnoreCase);
        List<FamilyParameter> parameters = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter => targets.Contains(parameter.Definition.Name))
            .ToList();

        foreach (FamilyType type in manager.Types.Cast<FamilyType>().ToList())
        {
            manager.CurrentType = type;
            foreach (FamilyParameter parameter in parameters)
            {
                if (parameter.IsReadOnly) continue;
                try
                {
                    if (!string.IsNullOrWhiteSpace(parameter.Formula))
                        manager.SetFormula(parameter, null);
                    switch (parameter.StorageType)
                    {
                        case StorageType.String:
                            manager.Set(parameter, string.Empty);
                            break;
                        case StorageType.Double:
                            manager.Set(parameter, 0.0);
                            break;
                        case StorageType.Integer:
                            manager.Set(parameter, 0);
                            break;
                        case StorageType.ElementId:
                            manager.Set(parameter, ElementId.InvalidElementId);
                            break;
                    }
                }
                catch { }
            }
        }

        foreach (FamilyParameter parameter in parameters)
        {
            try { manager.RemoveParameter(parameter); }
            catch { }
        }
    }

    private static int RenameCompactParameters(FamilyManager manager)
    {
        (string OldName, string NewName)[] mappings =
        [
            ("Neck Width", "FT_LE_ZZ_NeckW"),
            ("Neck Height", "FT_LE_ZZ_NeckH"),
            ("Neck Diameter", "FT_LE_ZZ_NeckD"),
            ("Inlet Diameter", "FT_LE_ZZ_InletD"),
            ("Face Width", "FT_LE_ZZ_FaceW"),
            ("Face Height", "FT_LE_ZZ_FaceH"),
            ("Face Length", "FT_LE_ZZ_FaceL"),
            ("Overall Width", "FT_LE_ZZ_OverallW"),
            ("Overall Height", "FT_LE_ZZ_OverallH"),
            ("Overall Length", "FT_LE_ZZ_OverallL"),
            ("Overall Depth", "FT_LE_ZZ_OverallD"),
            ("Border Width", "FT_LE_ZZ_BorderW"),
            ("Frame Width", "FT_LE_ZZ_FrameW"),
            ("Frame Depth", "FT_LE_ZZ_FrameD"),
            ("Slot Width", "FT_LE_ZZ_SlotW"),
            ("Slot Length", "FT_LE_ZZ_SlotL"),
            ("Slot Count", "FT_IN_ZZ_SlotCount"),
            ("Number of Slots", "FT_IN_ZZ_SlotCount"),
            ("Blade Angle", "FT_AN_ZZ_Blade"),
            ("Blade Spacing", "FT_LE_ZZ_BladeGap"),
            ("Core Width", "FT_LE_ZZ_CoreW"),
            ("Core Height", "FT_LE_ZZ_CoreH"),
            ("Plenum Height", "FT_LE_ZZ_PlenumH"),
            ("Plenum Width", "FT_LE_ZZ_PlenumW"),
            ("Plenum Length", "FT_LE_ZZ_PlenumL"),
            ("Diffuser Material", "FT_LE_ZZ_FaceMat"),
            ("Grille Material", "FT_LE_ZZ_FaceMat"),
            ("Frame Material", "FT_LE_ZZ_FrameMat"),
            ("Use Annotation Scale (default)", "FT_YN_ZZ_AnnoScale")
        ];

        Dictionary<string, FamilyParameter> parameters = manager.Parameters
            .Cast<FamilyParameter>()
            .GroupBy(parameter => parameter.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> existing = new(parameters.Keys, StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach ((string oldName, string newName) in mappings)
        {
            if (!parameters.TryGetValue(oldName, out FamilyParameter? parameter)
                || existing.Contains(newName))
                continue;
            try
            {
                manager.RenameParameter(parameter, newName);
                existing.Remove(oldName);
                existing.Add(newName);
                count++;
            }
            catch { }
        }

        foreach (FamilyParameter parameter in manager.Parameters
                     .Cast<FamilyParameter>()
                     .Where(parameter =>
                         !parameter.Definition.Name.StartsWith(
                             "FT_",
                             StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            string oldName = parameter.Definition.Name;
            if (IsIdentityParameter(oldName)) continue;
            string suffix = CompactParameterSuffix(oldName);
            if (string.IsNullOrWhiteSpace(suffix)) continue;
            string requested = $"FT_LE_ZZ_{suffix}";
            string newName = requested;
            int duplicate = 2;
            while (existing.Contains(newName))
                newName = $"{requested}_{duplicate++}";
            try
            {
                manager.RenameParameter(parameter, newName);
                existing.Remove(oldName);
                existing.Add(newName);
                count++;
            }
            catch
            {
                // Built-in and protected Autodesk parameters intentionally keep their names.
            }
        }
        return count;
    }

    private static bool IsIdentityParameter(string name) =>
        name.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Model", StringComparison.OrdinalIgnoreCase)
        || name.Equals("URL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Description", StringComparison.OrdinalIgnoreCase);

    private static string CompactParameterSuffix(string name)
    {
        Dictionary<string, string> words = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Air"] = "",
            ["Terminal"] = "",
            ["Diffuser"] = "",
            ["Grille"] = "",
            ["Center"] = "Ctr",
            ["Primary"] = "Pri",
            ["Secondary"] = "Sec",
            ["Connection"] = "Conn",
            ["Connector"] = "Conn",
            ["Horizontal"] = "X",
            ["Vertical"] = "Y",
            ["Overall"] = "Overall",
            ["Width"] = "W",
            ["Height"] = "H",
            ["Length"] = "L",
            ["Depth"] = "D",
            ["Diameter"] = "Dia",
            ["Radius"] = "R",
            ["Thickness"] = "T",
            ["Offset"] = "Off",
            ["Material"] = "Mat",
            ["Visibility"] = "Vis",
            ["Number"] = "No",
            ["Count"] = "Count",
            ["Angle"] = "Ang",
            ["Spacing"] = "Gap",
            ["Default"] = "Def"
        };
        var result = new StringBuilder();
        foreach (string token in Regex
                     .Split(name, @"[^A-Za-z0-9]+")
                     .Where(token => !string.IsNullOrWhiteSpace(token)))
        {
            string compact = words.TryGetValue(token, out string? mapped)
                ? mapped
                : token.Length <= 5
                    ? token
                    : char.ToUpperInvariant(token[0]) + token[1..];
            result.Append(compact);
        }
        string suffix = result.ToString();
        return suffix.Length <= 40 ? suffix : suffix[..40];
    }

    private static int RenameLookupTables(
        Document familyDocument,
        FamilyManager manager,
        string familyName,
        AirTerminalBuilderResult result)
    {
        Family? ownerFamily = familyDocument.OwnerFamily;
        if (ownerFamily is null) return 0;
        FamilySizeTableManager? tableManager =
            FamilySizeTableManager.GetFamilySizeTableManager(
                familyDocument,
                ownerFamily.Id);
        if (tableManager is null || !tableManager.IsValidObject) return 0;

        List<string> oldNames = tableManager.GetAllSizeTableNames().ToList();
        if (oldNames.Count == 0) return 0;
        string prefix = SanitizeName(familyName);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> occupied = new(oldNames, StringComparer.OrdinalIgnoreCase);
        int index = 1;
        foreach (string oldName in oldNames)
        {
            if (oldName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                continue;
            string role = SanitizeName(
                Regex.Replace(oldName, @"^n_(GM|DS)_", "", RegexOptions.IgnoreCase));
            role = Regex.Replace(role, @"_Type\d+$", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(role)) role = $"Lookup{index}";
            string requested = $"{prefix}_{role}";
            string newName = requested;
            int duplicate = 2;
            while (occupied.Contains(newName))
                newName = $"{requested}_{duplicate++}";
            occupied.Add(newName);
            mappings[oldName] = newName;
            index++;
        }
        if (mappings.Count == 0) return 0;

        string cache = Path.Combine(
            AppPaths.Root,
            "lookup-table-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        int renamed = 0;
        try
        {
            foreach ((string oldName, string newName) in mappings)
            {
                string csvPath = Path.Combine(cache, $"{newName}.csv");
                if (!tableManager.ExportSizeTable(oldName, csvPath))
                {
                    result.Warnings.Add(
                        $"Lookup table '{oldName}' could not be exported.");
                    continue;
                }

                using SubTransaction subTransaction = new(familyDocument);
                subTransaction.Start();
                try
                {
                    var errorInfo = new FamilySizeTableErrorInfo();
                    if (!tableManager.ImportSizeTable(
                            familyDocument,
                            csvPath,
                            errorInfo))
                    {
                        throw new InvalidOperationException(
                            $"CSV import failed: {errorInfo.FamilySizeTableErrorType}");
                    }
                    foreach (FamilyParameter parameter in manager.Parameters
                                 .Cast<FamilyParameter>()
                                 .Where(parameter =>
                                     !string.IsNullOrWhiteSpace(parameter.Formula)
                                     && parameter.Formula.Contains(
                                         $"\"{oldName}\"",
                                         StringComparison.Ordinal)))
                    {
                        manager.SetFormula(
                            parameter,
                            parameter.Formula.Replace(
                                $"\"{oldName}\"",
                                $"\"{newName}\""));
                    }
                    familyDocument.Regenerate();
                    if (!tableManager.RemoveSizeTable(oldName))
                        throw new InvalidOperationException(
                            "the original lookup table could not be removed");
                    subTransaction.Commit();
                    renamed++;
                }
                catch (Exception exception)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                        subTransaction.RollBack();
                    result.Warnings.Add(
                        $"Lookup table '{oldName}' kept unchanged: {exception.Message}");
                }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(cache))
                    Directory.Delete(cache, recursive: true);
            }
            catch { }
        }
        return renamed;
    }

    private static string SanitizeName(string value)
    {
        string result = Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_");
        result = Regex.Replace(result, @"_+", "_").Trim('_');
        return result.Length <= 48 ? result : result[..48];
    }

    private static string SaveGeneratedCopy(
        Document familyDocument,
        string requestedPath,
        string familyName)
    {
        string outputPath = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(AppPaths.GeneratedAirTerminalFolder, $"{familyName}.rfa")
            : Path.GetFullPath(requestedPath);
        if (!Path.GetExtension(outputPath).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
            outputPath += ".rfa";
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The output RFA folder is invalid.");
        Directory.CreateDirectory(directory);
        familyDocument.SaveAs(
            outputPath,
            new SaveAsOptions
            {
                OverwriteExistingFile = true,
                Compact = true
            });
        return outputPath;
    }

    private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(
            bool familyInUse,
            out bool overwriteParameterValues)
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
}
