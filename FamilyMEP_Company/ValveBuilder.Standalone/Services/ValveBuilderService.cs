using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class ValveBuilderService
{
    public static ValveBuilderResult Execute(
        UIApplication uiApplication,
        ValveBuilderRequest request,
        IReadOnlyCollection<ElementId> selectedInstanceIds,
        Family? selectedFamily)
    {
        UIDocument? uiDocument = uiApplication.ActiveUIDocument;
        Document project = uiDocument?.Document
            ?? throw new InvalidOperationException("Open an RVT project before running Valve Builder.");
        if (project.IsFamilyDocument)
            throw new InvalidOperationException("Valve Builder must be started from an RVT project.");

        Document? familyDocument = null;
        try
        {
            familyDocument = request.SourceMode switch
            {
                ValveSourceMode.NewGenericModel =>
                    CreateNewFamilyDocument(uiApplication, request.FamilyPath),
                ValveSourceMode.FamilyFile when request.UseLookupTableGeometry =>
                    OpenFamilyFile(uiApplication, request.FamilyPath),
                ValveSourceMode.SelectedProjectFamily when selectedFamily is not null =>
                    project.EditFamily(selectedFamily),
                ValveSourceMode.SelectedProjectFamily =>
                    throw new InvalidOperationException("The selected valve family is no longer available."),
                _ => OpenFamilyFile(uiApplication, request.FamilyPath)
            };

            if (!familyDocument.IsFamilyDocument)
                throw new InvalidOperationException("The selected source is not a Revit Family document.");

            var result = new ValveBuilderResult
            {
                FamilyName = request.SourceMode == ValveSourceMode.NewGenericModel
                    || request.PreserveSourceParameters
                    ? request.FamilyName
                    : ResolveFamilyName(familyDocument, request.FamilyPath),
                TypeName = request.TypeName,
            };

            if (request.SourceMode == ValveSourceMode.NewGenericModel)
                BuildNewValveFamily(familyDocument, request, result);
            else
                UpdateFamilyType(familyDocument, request, result);

            result.CategoryName = familyDocument.OwnerFamily?.FamilyCategory?.Name ?? "Unclassified";
            result.ConnectorCount = new FilteredElementCollector(familyDocument)
                .OfClass(typeof(ConnectorElement))
                .GetElementCount();

            if (!IsPipeAccessory(familyDocument.OwnerFamily))
                result.Warnings.Add("The source family category is not Pipe Accessories.");
            if (result.ConnectorCount != 2)
                result.Warnings.Add($"Expected 2 MEP connectors for an inline valve, found {result.ConnectorCount}.");

            result.OutputPath = SaveGeneratedCopy(
                familyDocument,
                result.FamilyName,
                result.TypeName,
                request.OutputPath);
            Family loadedFamily = familyDocument.LoadFamily(project, new OverwriteFamilyLoadOptions());

            result.UpdatedInstanceCount = request.ApplyToSelectedInstances
                ? ApplyTypeToSelection(project, loadedFamily, result.TypeName, selectedInstanceIds, result)
                : 0;

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

    private static Document CreateNewFamilyDocument(UIApplication uiApplication, string templatePath)
    {
        if (!File.Exists(templatePath)
            || !Path.GetExtension(templatePath).Equals(".rft", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The Generic Model RFT template was not found.", templatePath);
        }

        Document? document = uiApplication.Application.NewFamilyDocument(templatePath);
        return document
            ?? throw new InvalidOperationException("Revit could not create a Family document from the selected template.");
    }

    private static Document OpenFamilyFile(UIApplication uiApplication, string path)
    {
        if (!File.Exists(path)
            || !Path.GetExtension(path).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The Master Valve RFA file was not found.", path);
        }
        return uiApplication.Application.OpenDocumentFile(path);
    }

    private static Document OpenCatalogFamilyType(
        UIApplication uiApplication,
        Document project,
        string familyPath,
        string typeName)
    {
        if (!File.Exists(familyPath)
            || !Path.GetExtension(familyPath).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "The official manufacturer RFA file was not found.",
                familyPath);
        }

        string typeCatalogPath = Path.ChangeExtension(familyPath, ".txt");
        if (!File.Exists(typeCatalogPath))
        {
            throw new FileNotFoundException(
                "The external Type Catalog (.txt) was not found beside the RFA.",
                typeCatalogPath);
        }

        FamilySymbol? symbol = null;
        project.LoadFamilySymbol(
            familyPath,
            typeName,
            new OverwriteFamilyLoadOptions(),
            out symbol);
        symbol ??= new FilteredElementCollector(project)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(item =>
                item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (symbol is null)
        {
            throw new InvalidOperationException(
                $"The Type Catalog could not load Type '{typeName}'.");
        }
        if (!symbol.Family.IsEditable)
        {
            throw new InvalidOperationException(
                $"The loaded reference family for Type '{typeName}' is not editable.");
        }
        return project.EditFamily(symbol.Family);
    }

    private static bool ApplyExternalTypeCatalogRow(
        FamilyManager manager,
        string familyPath,
        string typeName,
        ValveBuilderResult result)
    {
        string catalogPath = Path.ChangeExtension(familyPath, ".txt");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                "The manufacturer Type Catalog (.txt) was not found beside the RFA.",
                catalogPath);
        }

        string[] lines = File.ReadAllLines(catalogPath);
        if (lines.Length < 2)
            throw new InvalidOperationException("The manufacturer Type Catalog is empty.");

        List<string> headers = ParseCsvLine(lines[0]);
        List<string>? values = lines
            .Skip(1)
            .Select(ParseCsvLine)
            .FirstOrDefault(row =>
                row.Count > 0
                && row[0].Trim().Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (values is null)
        {
            throw new InvalidOperationException(
                $"Type '{typeName}' was not found in the manufacturer Type Catalog.");
        }

        Dictionary<string, FamilyParameter> parameters = manager.Parameters
            .Cast<FamilyParameter>()
            .GroupBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        int applied = 0;
        int count = Math.Min(headers.Count, values.Count);
        for (int index = 1; index < count; index++)
        {
            string header = headers[index].Trim();
            string value = values[index].Trim();
            if (string.IsNullOrWhiteSpace(header)
                || !TryParseTypeCatalogHeader(header, out string parameterName, out string unitName)
                || !parameters.TryGetValue(parameterName, out FamilyParameter? parameter)
                || parameter.IsReadOnly
                || !string.IsNullOrWhiteSpace(parameter.Formula)
                || parameter.StorageType == StorageType.ElementId)
            {
                continue;
            }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        manager.Set(parameter, value);
                        applied++;
                        break;
                    case StorageType.Integer when int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int integerValue):
                        manager.Set(parameter, integerValue);
                        applied++;
                        break;
                    case StorageType.Double when TryParseCatalogNumber(
                        value,
                        out double numericValue):
                        manager.Set(
                            parameter,
                            unitName.Equals("inches", StringComparison.OrdinalIgnoreCase)
                                ? numericValue / 12.0
                                : numericValue);
                        applied++;
                        break;
                }
            }
            catch
            {
                // Some manufacturer reporting parameters are not writable in Family Editor.
                // Geometry-driving catalog values continue to be applied.
            }
        }

        if (applied == 0)
        {
            throw new InvalidOperationException(
                $"Type '{typeName}' was found, but no matching writable Family parameters were found.");
        }
        result.AppliedParameters.Add(
            $"{applied} manufacturer Type Catalog parameters applied");
        return true;
    }

    private static bool TryParseCatalogNumber(string value, out double number)
    {
        string text = value.Trim();
        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number))
        {
            return true;
        }

        double whole = 0;
        string fractionText = text;
        int space = text.LastIndexOf(' ');
        if (space > 0
            && double.TryParse(
                text[..space],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double wholeValue))
        {
            whole = wholeValue;
            fractionText = text[(space + 1)..];
        }
        string[] fraction = fractionText.Split('/');
        if (fraction.Length == 2
            && double.TryParse(
                fraction[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double numerator)
            && double.TryParse(
                fraction[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double denominator)
            && Math.Abs(denominator) > 1e-9)
        {
            number = whole + numerator / denominator;
            return true;
        }
        number = 0;
        return false;
    }

    private static bool TryParseTypeCatalogHeader(
        string header,
        out string parameterName,
        out string unitName)
    {
        string[] parts = header.Split(["##"], StringSplitOptions.None);
        parameterName = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        unitName = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        return !string.IsNullOrWhiteSpace(parameterName);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        fields.Add(value.ToString());
        return fields;
    }

    private static string ResolveFamilyName(Document familyDocument, string sourcePath)
    {
        string? ownerName = familyDocument.OwnerFamily?.Name;
        if (!string.IsNullOrWhiteSpace(ownerName)) return ownerName;
        if (!string.IsNullOrWhiteSpace(familyDocument.PathName))
            return Path.GetFileNameWithoutExtension(familyDocument.PathName);
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrWhiteSpace(familyDocument.Title) ? "Valve" : familyDocument.Title;
    }

    private static void UpdateFamilyType(
        Document familyDocument,
        ValveBuilderRequest request,
        ValveBuilderResult result)
    {
        FamilyManager manager = familyDocument.FamilyManager;
        using Transaction transaction = new(familyDocument, "FamilyMEP - Build valve type");
        transaction.Start();

        FamilyType? targetType;
        int importedTypeCount = 0;
        if (request.UseLookupTableGeometry && request.SizeCatalog.Count > 0)
        {
            FamilyParameter metricDn = EnsureLengthParameter(manager, "FT_LE_ZZ_DN");
            foreach (ValveSizeDefinition size in request.SizeCatalog)
            {
                FamilyType? catalogType = FindFamilyType(manager, size.TypeName);
                if (catalogType is null)
                {
                    catalogType = manager.NewType(size.TypeName);
                    ApplyExternalTypeCatalogRow(
                        manager,
                        request.FamilyPath,
                        size.SourceTypeName,
                        result);
                    importedTypeCount++;
                }
                else
                {
                    manager.CurrentType = catalogType;
                }
                manager.Set(metricDn, Mm(size.NominalDiameterMm));
            }
            FamilyParameter? connectionRadius = manager.Parameters
                .Cast<FamilyParameter>()
                .FirstOrDefault(parameter =>
                    parameter.Definition.Name.Equals(
                        "Connection Radius",
                        StringComparison.OrdinalIgnoreCase));
            if (connectionRadius is not null && !connectionRadius.IsReadOnly)
                manager.SetFormula(connectionRadius, "FT_LE_ZZ_DN / 2");
            DeleteNonCatalogTypes(
                manager,
                request.SizeCatalog.Select(size => size.TypeName),
                result);
            targetType = FindFamilyType(manager, request.TypeName)
                ?? throw new InvalidOperationException(
                    $"Generated catalog Type '{request.TypeName}' was not found.");
        }
        else
        {
            targetType = FindFamilyType(manager, request.TypeName);
            if (targetType is null)
                targetType = manager.NewType(request.TypeName);
            else
                manager.CurrentType = targetType;
        }
        result.TypeName = targetType.Name;

        if (request.UseLookupTableGeometry)
        {
            result.AppliedParameters.Add(
                $"{request.SizeCatalog.Count} verified catalog sizes available as DN Types");
            result.AppliedParameters.Add(
                importedTypeCount > 0
                    ? $"{importedTypeCount} Types imported from the external catalog"
                    : "Existing DN catalog Types preserved");
            result.AppliedParameters.Add(
                request.PreserveSourceParameters
                    ? "Geometry, constraints and manufacturer parameters preserved"
                    : "L/H/component dimensions → calculated by the Family size table");
        }
        else
        {
            ApplyLength(manager, request.DiameterParameterAliases, request.NominalDiameterMm, "DN", result);
            ApplyLength(manager, request.LengthParameterAliases, request.BodyLengthMm, "L", result);
            ApplyLength(manager, request.HeightParameterAliases, request.BodyHeightMm, "H", result);
            ApplyAngle(manager, request.AngleParameterAliases, request.HandleAngleDegrees, result);
            ApplyText(manager, request.ConnectionParameterAliases, request.ConnectionType, "Connection", result);
        }
        if (request.PreserveSourceParameters)
        {
            SetMetricFamilyUnits(familyDocument);
            bool connectorUsesDiameter = ConfigureConnectorDiameterDisplay(
                familyDocument,
                manager);
            ApplyNeutralGrayMaterials(familyDocument, manager);
            ClearManufacturerInformation(manager);
            RenameCompactParameters(manager);
            manager.CurrentType = targetType;
            result.AppliedParameters.Add("Family display units → millimetres");
            result.AppliedParameters.Add(
                connectorUsesDiameter
                    ? "Round connectors → Diameter driven directly by FT_LE_ZZ_DN"
                    : "Round connectors → existing radius display retained");
            result.AppliedParameters.Add("Valve and operator materials → neutral gray");
            result.AppliedParameters.Add("Manufacturer, catalog and identity information → cleared");
            result.AppliedParameters.Add("Custom parameters → compact FT_LE_ZZ_* names");
        }
        if (!request.PreserveSourceParameters)
            ApplyMaterial(familyDocument, manager, request.MaterialParameterAliases, request.MaterialName, result);

        familyDocument.Regenerate();
        transaction.Commit();

        if (request.PreserveSourceParameters)
        {
            bool originCentered = TryCenterFamilyOriginOnConnectors(familyDocument);
            result.AppliedParameters.Add(
                originCentered
                    ? "Family X/Y reference origin → connector midpoint"
                    : "Family X/Y reference origin → unchanged (constraint-safe fallback)");
        }
    }

    private static bool ConfigureConnectorDiameterDisplay(
        Document familyDocument,
        FamilyManager manager)
    {
        FamilyParameter? metricDn = manager.Parameters
            .Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(
                    "FT_LE_ZZ_DN",
                    StringComparison.OrdinalIgnoreCase));
        if (metricDn is null) return false;

        Parameter? dimensionMode = familyDocument.OwnerFamily?.get_Parameter(
            BuiltInParameter.FAMILY_ROUNDCONNECTOR_DIMENSIONTYPE);
        if (dimensionMode is not null && !dimensionMode.IsReadOnly)
        {
            try
            {
                dimensionMode.Set(1);
                familyDocument.Regenerate();
            }
            catch { }
        }

        bool associated = false;
        foreach (ConnectorElement connector in new FilteredElementCollector(familyDocument)
                     .OfClass(typeof(ConnectorElement))
                     .Cast<ConnectorElement>())
        {
            Parameter? diameter = connector.get_Parameter(
                BuiltInParameter.CONNECTOR_DIAMETER);
            if (diameter is null
                || diameter.IsReadOnly
                || !manager.CanElementParameterBeAssociated(diameter))
            {
                continue;
            }
            try
            {
                FamilyParameter? current =
                    manager.GetAssociatedFamilyParameter(diameter);
                if (current is not null && current.Id != metricDn.Id)
                    manager.AssociateElementParameterToFamilyParameter(diameter, null!);
                manager.AssociateElementParameterToFamilyParameter(diameter, metricDn);
                associated = true;
            }
            catch { }
        }
        return associated;
    }

    private static FamilyType? FindFamilyType(FamilyManager manager, string name) =>
        manager.Types.Cast<FamilyType>().FirstOrDefault(type =>
            type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void DeleteNonCatalogTypes(
        FamilyManager manager,
        IEnumerable<string> catalogTypeNames,
        ValveBuilderResult result)
    {
        HashSet<string> keep = new(catalogTypeNames, StringComparer.OrdinalIgnoreCase);
        List<FamilyType> removable = manager.Types
            .Cast<FamilyType>()
            .Where(type => !keep.Contains(type.Name))
            .ToList();
        foreach (FamilyType type in removable)
        {
            try
            {
                manager.CurrentType = type;
                manager.DeleteCurrentType();
            }
            catch (Exception exception)
            {
                result.Warnings.Add(
                    $"Unused source Type '{type.Name}' could not be removed: {exception.Message}");
            }
        }
    }

    private static void SetMetricFamilyUnits(Document familyDocument)
    {
        Units units = familyDocument.GetUnits();
        var millimetres = new FormatOptions(UnitTypeId.Millimeters)
        {
            Accuracy = 0.1,
            UseDigitGrouping = false
        };
        units.SetFormatOptions(SpecTypeId.Length, millimetres);
        try
        {
            units.SetFormatOptions(
                SpecTypeId.PipeSize,
                new FormatOptions(UnitTypeId.Millimeters)
                {
                    Accuracy = 0.1,
                    UseDigitGrouping = false
                });
        }
        catch
        {
            // Some older family schemas expose pipe size through the general length format.
        }
        familyDocument.SetUnits(units);
    }

    private static void ApplyNeutralGrayMaterials(
        Document familyDocument,
        FamilyManager manager)
    {
        ElementId grayMaterialId = EnsureMaterial(
            familyDocument,
            "FamilyMEP - Neutral Gray");
        if (familyDocument.GetElement(grayMaterialId) is Material grayMaterial)
        {
            grayMaterial.Color = new Autodesk.Revit.DB.Color(160, 160, 160);
            grayMaterial.Transparency = 0;
        }

        string[] materialParameterNames = ["Valve Material", "Operator Material"];
        List<FamilyParameter> materialParameters = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter => materialParameterNames.Contains(
                parameter.Definition.Name,
                StringComparer.OrdinalIgnoreCase))
            .ToList();
        List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
        foreach (FamilyType type in types)
        {
            manager.CurrentType = type;
            foreach (FamilyParameter parameter in materialParameters)
            {
                if (parameter.IsReadOnly || !string.IsNullOrWhiteSpace(parameter.Formula)) continue;
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

    private static void ClearManufacturerInformation(FamilyManager manager)
    {
        string[] names =
        [
            "Version",
            "Manufacturer",
            "URL",
            "Series",
            "Product Page URL",
            "Model",
            "Material Number",
            "Description",
            "Standards",
            "Assembly Code",
            "Type Image",
            "Keynote",
            "Type Comments",
            "Cost",
            "ENGworks URL",
            "ThomasNet URL",
            "Flow Configuration",
            "Unit Weight",
            "Unit Weight Value",
            "Cv Coefficient",
            "K Coefficient",
            "K Coefficient Table",
            "Loss Method",
            "Maximum Operating Temperature",
            "Minimum Operating Temperature",
            "Maximum Working Pressure"
        ];
        HashSet<string> targets = new(names, StringComparer.OrdinalIgnoreCase);
        List<FamilyParameter> parameters = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter => targets.Contains(parameter.Definition.Name))
            .ToList();
        List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
        foreach (FamilyType type in types)
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
        foreach (FamilyParameter parameter in parameters.ToList())
        {
            try
            {
                manager.RemoveParameter(parameter);
            }
            catch
            {
                // Revit built-in family parameters cannot be removed; their values stay blank/zero.
            }
        }
    }

    private static void RenameCompactParameters(FamilyManager manager)
    {
        (string OldName, string NewName)[] mappings =
        [
            ("Actual Valve Body Radius", "FT_LE_ZZ_BodyR"),
            ("Body Diameter", "FT_LE_ZZ_BodyD"),
            ("Center to Handle End", "FT_LE_ZZ_HandleL"),
            ("Centerline to Handle Height", "FT_LE_ZZ_HandleH"),
            ("Center to End 1", "FT_LE_ZZ_EndL"),
            ("Connection Radius", "FT_LE_ZZ_ConnR"),
            ("Connection to Connection", "FT_LE_ZZ_ConnL"),
            ("Hex Nut Circumscribe Length", "FT_LE_ZZ_HexD"),
            ("Hex Nut Height", "FT_LE_ZZ_HexH"),
            ("Octagon Nut Circumscribe Length 1", "FT_LE_ZZ_Nut1D"),
            ("Octagon Nut Circumscribe Length 2", "FT_LE_ZZ_Nut2D"),
            ("Operator 1 Handle Thickness", "FT_LE_ZZ_GripT"),
            ("Female Thread End Inside Radius 1", "FT_LE_ZZ_ThreadR1"),
            ("Female Thread End Inside Radius 2", "FT_LE_ZZ_ThreadR2"),
            ("Female Thread End Length 1", "FT_LE_ZZ_ThreadL1"),
            ("Female Thread End Length 2", "FT_LE_ZZ_ThreadL2"),
            ("Stem Operator Offset", "FT_LE_ZZ_StemOff"),
            ("Valve Body Radius", "FT_LE_ZZ_ValveR"),
            ("Valve Stem Inside Radius", "FT_LE_ZZ_StemIR"),
            ("Valve Stem Outside Radius", "FT_LE_ZZ_StemOR"),
            ("Body Radius", "FT_LE_ZZ_OuterR"),
            ("Primary End Radius", "FT_LE_ZZ_EndR"),
            ("Handle Height", "FT_LE_ZZ_HandleT"),
            ("Body Primary End Offset", "FT_LE_ZZ_EndOff1"),
            ("Body Secondary End Offset", "FT_LE_ZZ_EndOff2"),
            ("Operator Vertical Offset", "FT_LE_ZZ_OpOff"),
            ("Valve Raised Body Depth", "FT_LE_ZZ_RaisedD"),
            ("Body Length", "FT_LE_ZZ_BodyL"),
            ("Valve Body Chamfer Offset", "FT_LE_ZZ_Chamfer"),
            ("Nut Size Selector Length", "FT_LE_ZZ_NutSel"),
            ("Operator 1 Selector Length", "FT_LE_ZZ_Op1L"),
            ("Operator 2 Selector Length", "FT_LE_ZZ_Op2L"),
            ("Operator 3 Selector Length", "FT_LE_ZZ_Op3L"),
            ("Operator 6 Selector Length", "FT_LE_ZZ_Op6L"),
            ("Operator 7 Selector Length", "FT_LE_ZZ_Op7L"),
            ("Operator 8 Selector Length", "FT_LE_ZZ_Op8L"),
            ("Operator 9 Selector Length", "FT_LE_ZZ_Op9L"),
            ("Operator Selector Length", "FT_LE_ZZ_OpSelL"),
            ("Operator Selector", "FT_LE_ZZ_OpSel"),
            ("Operator 1", "FT_LE_ZZ_Op1"),
            ("Operator 2", "FT_LE_ZZ_Op2"),
            ("Operator 3", "FT_LE_ZZ_Op3"),
            ("Operator 6", "FT_LE_ZZ_Op6"),
            ("Operator 7", "FT_LE_ZZ_Op7"),
            ("Operator 9", "FT_LE_ZZ_Op9"),
            ("Operator In Use", "FT_LE_ZZ_Operator"),
            ("Valve Material", "FT_LE_ZZ_BodyMat"),
            ("Operator Material", "FT_LE_ZZ_OpMat")
        ];

        Dictionary<string, FamilyParameter> parameters = manager.Parameters
            .Cast<FamilyParameter>()
            .GroupBy(parameter => parameter.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> existingNames = new(
            parameters.Keys,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string oldName, string newName) in mappings)
        {
            if (!parameters.TryGetValue(oldName, out FamilyParameter? parameter)
                || existingNames.Contains(newName))
            {
                continue;
            }
            try
            {
                manager.RenameParameter(parameter, newName);
                existingNames.Remove(oldName);
                existingNames.Add(newName);
            }
            catch
            {
                // Revit built-in parameters cannot be renamed.
            }
        }
    }

    private static bool TryCenterFamilyOriginOnConnectors(Document familyDocument)
    {
        using Transaction transaction = new(
            familyDocument,
            "FamilyMEP - Center family X/Y origin");
        transaction.Start();
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new RollBackOriginMoveOnError());
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        try
        {
            XYZ midpoint = ConnectorMidpoint(familyDocument)
                ?? throw new InvalidOperationException("Two connector origins were not found.");
            List<ReferencePlane> originPlanes = new FilteredElementCollector(familyDocument)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .Where(plane =>
                {
                    Parameter? parameter = plane.get_Parameter(
                        BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN);
                    return parameter is not null && parameter.AsInteger() == 1;
                })
                .ToList();
            ReferencePlane? xOrigin = originPlanes.FirstOrDefault(
                plane => Math.Abs(plane.Normal.X) > 0.8);
            ReferencePlane? yOrigin = originPlanes.FirstOrDefault(
                plane => Math.Abs(plane.Normal.Y) > 0.8);
            if (xOrigin is null || yOrigin is null)
                throw new InvalidOperationException("The X/Y origin reference planes were not found.");

            double xOffset = midpoint.X - ReferencePlaneCenter(xOrigin).X;
            double yOffset = midpoint.Y - ReferencePlaneCenter(yOrigin).Y;
            if (Math.Abs(xOffset) > 1e-7)
                ElementTransformUtils.MoveElement(
                    familyDocument,
                    xOrigin.Id,
                    new XYZ(xOffset, 0, 0));
            if (Math.Abs(yOffset) > 1e-7)
                ElementTransformUtils.MoveElement(
                    familyDocument,
                    yOrigin.Id,
                    new XYZ(0, yOffset, 0));
            familyDocument.Regenerate();

            XYZ verifiedMidpoint = ConnectorMidpoint(familyDocument)
                ?? throw new InvalidOperationException("Connector origins were lost after centering.");
            double tolerance = Mm(0.2);
            if (Math.Abs(ReferencePlaneCenter(xOrigin).X - verifiedMidpoint.X) > tolerance
                || Math.Abs(ReferencePlaneCenter(yOrigin).Y - verifiedMidpoint.Y) > tolerance)
            {
                transaction.RollBack();
                return false;
            }
            return transaction.Commit() == TransactionStatus.Committed;
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
            return false;
        }
    }

    private static XYZ? ConnectorMidpoint(Document familyDocument)
    {
        List<XYZ> points = new FilteredElementCollector(familyDocument)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .Select(connector => connector.Origin)
            .ToList();
        return points.Count < 2
            ? null
            : new XYZ(
                points.Average(point => point.X),
                points.Average(point => point.Y),
                points.Average(point => point.Z));
    }

    private static XYZ ReferencePlaneCenter(ReferencePlane plane) =>
        (plane.BubbleEnd + plane.FreeEnd) / 2.0;

    private static void BuildNewValveFamily(
        Document familyDocument,
        ValveBuilderRequest request,
        ValveBuilderResult result)
    {
        if (familyDocument.OwnerFamily is null)
            throw new InvalidOperationException("The Generic Model template does not expose an OwnerFamily.");

        IReadOnlyList<ValveSizeDefinition> catalog = ResolveCatalog(request);
        string lookupCsvPath = WriteLookupCsv(request);
        using Transaction transaction = new(familyDocument, "FamilyMEP - Create procedural valve");
        transaction.Start();

        Category pipeAccessory = familyDocument.Settings.Categories.get_Item(
            BuiltInCategory.OST_PipeAccessory);
        familyDocument.OwnerFamily.FamilyCategory = pipeAccessory;
        Parameter? partType = familyDocument.OwnerFamily.get_Parameter(
            BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
        if (partType is not null && !partType.IsReadOnly)
            partType.Set((int)PartType.ValveBreaksInto);

        FamilyManager manager = familyDocument.FamilyManager;
        Dictionary<string, FamilyType> familyTypes = EnsureCatalogTypes(manager, catalog);

        FamilyParameter dn = EnsureLengthParameter(manager, "DN");
        FamilyParameter length = EnsureLengthParameter(manager, "L");
        FamilyParameter bodyDiameter = EnsureLengthParameter(manager, "Body OD");
        FamilyParameter portDiameter = EnsureLengthParameter(manager, "Port ID");
        FamilyParameter height = EnsureLengthParameter(manager, "H");
        FamilyParameter handleLength = EnsureLengthParameter(manager, "Handle L");
        FamilyParameter bodyBarrelLength = EnsureLengthParameter(manager, "Body Barrel L");
        FamilyParameter portLength = EnsureLengthParameter(manager, "Port L");
        FamilyParameter nutLength = EnsureLengthParameter(manager, "Nut L");
        FamilyParameter ringWidth = EnsureLengthParameter(manager, "Ring W");
        FamilyParameter collarDiameter = EnsureLengthParameter(manager, "Collar OD");
        FamilyParameter socketDiameter = EnsureLengthParameter(manager, "Socket OD");
        FamilyParameter socketLipDiameter = EnsureLengthParameter(manager, "Socket Lip OD");
        FamilyParameter nutDiameter = EnsureLengthParameter(manager, "Union Nut OD");
        FamilyParameter bonnetDiameter = EnsureLengthParameter(manager, "Bonnet OD");
        FamilyParameter stemDiameter = EnsureLengthParameter(manager, "Stem OD");
        FamilyParameter handleWidth = EnsureLengthParameter(manager, "Handle W");
        FamilyParameter handleThickness = EnsureLengthParameter(manager, "Handle T");

        string tableName = Path.GetFileNameWithoutExtension(lookupCsvPath);
        foreach (ValveSizeDefinition size in catalog)
        {
            ValveDetailDimensions detail = CalculateValveDetails(size);
            manager.CurrentType = familyTypes[size.TypeName];
            manager.Set(dn, Mm(size.NominalDiameterMm));
            manager.Set(length, Mm(size.BodyLengthMm));
            manager.Set(bodyDiameter, Mm(size.BodyDiameterMm));
            manager.Set(portDiameter, Mm(size.PortDiameterMm));
            manager.Set(height, Mm(size.BodyHeightMm));
            manager.Set(handleLength, Mm(size.HandleLengthMm));
            SetValveDetailValues(
                manager,
                detail,
                bodyBarrelLength,
                portLength,
                nutLength,
                ringWidth,
                collarDiameter,
                socketDiameter,
                socketLipDiameter,
                nutDiameter,
                bonnetDiameter,
                stemDiameter,
                handleWidth,
                handleThickness);
        }
        familyDocument.Regenerate();

        bool lookupImported = false;
        try
        {
            FamilySizeTableManager? tableManager =
                FamilySizeTableManager.GetFamilySizeTableManager(
                    familyDocument,
                    familyDocument.OwnerFamily.Id);
            if (tableManager is null || !tableManager.IsValidObject)
            {
                FamilySizeTableManager.CreateFamilySizeTableManager(
                    familyDocument,
                    familyDocument.OwnerFamily.Id);
                tableManager = FamilySizeTableManager.GetFamilySizeTableManager(
                    familyDocument,
                    familyDocument.OwnerFamily.Id);
            }
            if (tableManager is null || !tableManager.IsValidObject)
                throw new InvalidOperationException("Revit could not create the Family lookup-table manager.");
            var errorInfo = new FamilySizeTableErrorInfo();
            bool imported = tableManager.ImportSizeTable(familyDocument, lookupCsvPath, errorInfo);
            if (imported)
            {
                lookupImported = true;
                result.AppliedParameters.Add(
                    $"Lookup table imported: {Path.GetFileNameWithoutExtension(lookupCsvPath)} ({catalog.Count} rows)");
                ApplyLookupFormula(manager, length, "L", tableName, result);
                ApplyLookupFormula(manager, bodyDiameter, "Body OD", tableName, result);
                ApplyLookupFormula(manager, portDiameter, "Port ID", tableName, result);
                ApplyLookupFormula(manager, height, "H", tableName, result);
                ApplyLookupFormula(manager, handleLength, "Handle L", tableName, result);
                ApplyLookupFormula(manager, bodyBarrelLength, "Body Barrel L", tableName, result);
                ApplyLookupFormula(manager, portLength, "Port L", tableName, result);
                ApplyLookupFormula(manager, nutLength, "Nut L", tableName, result);
                ApplyLookupFormula(manager, ringWidth, "Ring W", tableName, result);
                ApplyLookupFormula(manager, collarDiameter, "Collar OD", tableName, result);
                ApplyLookupFormula(manager, socketDiameter, "Socket OD", tableName, result);
                ApplyLookupFormula(manager, socketLipDiameter, "Socket Lip OD", tableName, result);
                ApplyLookupFormula(manager, nutDiameter, "Union Nut OD", tableName, result);
                ApplyLookupFormula(manager, bonnetDiameter, "Bonnet OD", tableName, result);
                ApplyLookupFormula(manager, stemDiameter, "Stem OD", tableName, result);
                ApplyLookupFormula(manager, handleWidth, "Handle W", tableName, result);
                ApplyLookupFormula(manager, handleThickness, "Handle T", tableName, result);
            }
            else
                result.Warnings.Add(
                    "The lookup CSV was created but Revit did not import it. "
                    + $"Error: {errorInfo.FamilySizeTableErrorType}; "
                    + $"row {errorInfo.InvalidRowIndex}, column {errorInfo.InvalidColumnIndex}, "
                    + $"header '{errorInfo.InvalidHeaderText}'.");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"Lookup table import failed: {exception.Message}");
        }

        foreach (ValveSizeDefinition size in catalog)
        {
            ValveDetailDimensions detail = CalculateValveDetails(size);
            manager.CurrentType = familyTypes[size.TypeName];
            manager.Set(dn, Mm(size.NominalDiameterMm));
            if (!lookupImported)
            {
                manager.Set(length, Mm(size.BodyLengthMm));
                manager.Set(bodyDiameter, Mm(size.BodyDiameterMm));
                manager.Set(portDiameter, Mm(size.PortDiameterMm));
                manager.Set(height, Mm(size.BodyHeightMm));
                manager.Set(handleLength, Mm(size.HandleLengthMm));
                SetValveDetailValues(
                    manager,
                    detail,
                    bodyBarrelLength,
                    portLength,
                    nutLength,
                    ringWidth,
                    collarDiameter,
                    socketDiameter,
                    socketLipDiameter,
                    nutDiameter,
                    bonnetDiameter,
                    stemDiameter,
                    handleWidth,
                    handleThickness);
            }
            familyDocument.Regenerate();
        }

        FamilyType geometrySeedType =
            familyTypes.TryGetValue(request.TypeName, out FamilyType? requestedSeedType)
                ? requestedSeedType
                : familyTypes[catalog[catalog.Count / 2].TypeName];
        manager.CurrentType = geometrySeedType;
        familyDocument.Regenerate();

        ElementId materialId = EnsureMaterial(familyDocument, request.MaterialName);
        try
        {
            BuildNativeParametricValve(
                familyDocument,
                manager,
                length,
                dn,
                bodyDiameter,
                portDiameter,
                height,
                handleLength,
                bodyBarrelLength,
                portLength,
                nutLength,
                ringWidth,
                collarDiameter,
                socketDiameter,
                socketLipDiameter,
                nutDiameter,
                bonnetDiameter,
                stemDiameter,
                handleWidth,
                handleThickness,
                materialId,
                result);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Native parametric valve construction failed: {exception.Message}",
                exception);
        }

        foreach (ValveSizeDefinition size in catalog)
        {
            try
            {
                manager.CurrentType = familyTypes[size.TypeName];
                familyDocument.Regenerate();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Automatic flex test failed at {size.TypeName}: {exception.Message}",
                    exception);
            }
        }
        result.AppliedParameters.Add(
            $"Flex test passed: {string.Join(", ", catalog.Select(item => item.TypeName))}");

        FamilyType selectedType = familyTypes.TryGetValue(request.TypeName, out FamilyType? requestedType)
            ? requestedType
            : familyTypes.Values.First();
        manager.CurrentType = selectedType;
        result.TypeName = selectedType.Name;

        result.AppliedParameters.Add("Category → Pipe Accessories");
        result.AppliedParameters.Add("Part Type → Valve - Breaks Into");
        result.AppliedParameters.Add(
            $"Geometry → one native parametric form set for {catalog.Count} Types "
            + $"({string.Join(", ", catalog.Select(item => item.TypeName))})");
        result.AppliedParameters.Add(
            "Native constraints → reference-plane skeleton, EQ chains and labeled dimensions driven by lookup parameters");
        result.AppliedParameters.Add(
            "Connectors → 2 coaxial pipe connectors driven by DN and L");

        familyDocument.Regenerate();
        transaction.Commit();
    }

    private sealed record NestedValveArtifact(
        ValveSizeDefinition Size,
        string Path);

    private static IReadOnlyList<NestedValveArtifact> CreateNestedValveFamilies(
        Document parentDocument,
        ValveBuilderRequest request,
        IReadOnlyList<ValveSizeDefinition> catalog,
        ValveBuilderResult result)
    {
        string outputFolder = Path.GetDirectoryName(request.OutputPath)
            ?? AppPaths.GeneratedValveFolder;
        string nestedFolder = Path.Combine(outputFolder, "nested-valve-geometry");
        Directory.CreateDirectory(nestedFolder);
        string familyStem = SanitizeFileName(request.FamilyName);
        var artifacts = new List<NestedValveArtifact>();

        foreach (ValveSizeDefinition size in catalog)
        {
            Document? nestedDocument = null;
            try
            {
                nestedDocument = parentDocument.Application.NewFamilyDocument(request.FamilyPath)
                    ?? throw new InvalidOperationException(
                        $"Could not create nested geometry Family for {size.TypeName}.");
                using Transaction nestedTransaction = new(
                    nestedDocument,
                    $"FamilyMEP - Build nested {size.TypeName}");
                nestedTransaction.Start();

                Category genericModel = nestedDocument.Settings.Categories.get_Item(
                    BuiltInCategory.OST_GenericModel);
                if (nestedDocument.OwnerFamily is not null)
                    nestedDocument.OwnerFamily.FamilyCategory = genericModel;
                FamilyManager nestedManager = nestedDocument.FamilyManager;
                if (nestedManager.CurrentType is null)
                    nestedManager.NewType(size.TypeName);
                else
                    nestedManager.RenameCurrentType(size.TypeName);

                ElementId bodyMaterial = EnsureMaterial(
                    nestedDocument,
                    request.MaterialName);
                ValveBuilderRequest nestedRequest = request with
                {
                    TypeName = size.TypeName,
                    NominalDiameterMm = size.NominalDiameterMm,
                    BodyLengthMm = size.BodyLengthMm,
                    BodyHeightMm = size.BodyHeightMm,
                    BodyDiameterMm = size.BodyDiameterMm,
                    PortDiameterMm = size.PortDiameterMm,
                    HandleLengthMm = size.HandleLengthMm
                };
                BuildValveGeometry(nestedDocument, nestedRequest, bodyMaterial);
                nestedDocument.Regenerate();
                nestedTransaction.Commit();

                string nestedPath = Path.Combine(
                    nestedFolder,
                    $"{familyStem}_{size.TypeName}_Geometry.rfa");
                var saveOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    MaximumBackups = 1
                };
                nestedDocument.SaveAs(nestedPath, saveOptions);
                artifacts.Add(new NestedValveArtifact(size, nestedPath));
            }
            finally
            {
                if (nestedDocument is not null)
                {
                    try { nestedDocument.Close(false); }
                    catch { }
                }
            }
        }

        result.AppliedParameters.Add(
            $"Nested high-detail geometry: {artifacts.Count} size Families");
        return artifacts;
    }

    private static void PlaceNestedValveGeometry(
        Document document,
        FamilyManager manager,
        IReadOnlyDictionary<string, FamilyType> familyTypes,
        IReadOnlyList<NestedValveArtifact> artifacts,
        ValveBuilderResult result)
    {
        var visibilityParameters =
            new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);

        foreach (NestedValveArtifact artifact in artifacts)
        {
            bool loaded = document.LoadFamily(
                artifact.Path,
                new OverwriteFamilyLoadOptions(),
                out Family loadedFamily);
            if (!loaded && loadedFamily is null)
                throw new InvalidOperationException(
                    $"Could not load nested geometry for {artifact.Size.TypeName}.");
            FamilySymbol symbol = loadedFamily.GetFamilySymbolIds()
                .Select(id => document.GetElement(id))
                .OfType<FamilySymbol>()
                .FirstOrDefault(item =>
                    item.Name.Equals(artifact.Size.TypeName, StringComparison.OrdinalIgnoreCase))
                ?? loadedFamily.GetFamilySymbolIds()
                    .Select(id => document.GetElement(id))
                    .OfType<FamilySymbol>()
                    .First();
            if (!symbol.IsActive) symbol.Activate();

            FamilyInstance nestedInstance = document.FamilyCreate.NewFamilyInstance(
                XYZ.Zero,
                symbol,
                StructuralType.NonStructural);
            Parameter visibility = nestedInstance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)
                ?? throw new InvalidOperationException(
                    $"Nested geometry {artifact.Size.TypeName} does not expose Visibility.");
            FamilyParameter visibilityParameter = EnsureYesNoParameter(
                manager,
                $"Geometry_{artifact.Size.TypeName}");
            if (!manager.CanElementParameterBeAssociated(visibility))
                throw new InvalidOperationException(
                    $"Visibility of nested geometry {artifact.Size.TypeName} cannot be associated.");
            manager.AssociateElementParameterToFamilyParameter(
                visibility,
                visibilityParameter);
            visibilityParameters[artifact.Size.TypeName] = visibilityParameter;
        }

        foreach ((string typeName, FamilyType parentType) in familyTypes)
        {
            manager.CurrentType = parentType;
            foreach ((string geometryType, FamilyParameter visibilityParameter)
                     in visibilityParameters)
            {
                manager.Set(
                    visibilityParameter,
                    geometryType.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0);
            }
        }

        document.Regenerate();
        result.AppliedParameters.Add(
            "Nested geometry: one fixed high-detail Family per DN; parent Type controls visibility");
    }

    private static FamilyParameter EnsureYesNoParameter(
        FamilyManager manager,
        string name)
    {
        FamilyParameter? existing = manager.Parameters.Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return manager.AddParameter(
            name,
            BuiltInParameterGroup.PG_GRAPHICS,
            ParameterType.YesNo,
            false);
#else
        return manager.AddParameter(
            name,
            GroupTypeId.Visibility,
            SpecTypeId.Boolean.YesNo,
            false);
#endif
    }

    private static void BuildParentConnectorHosts(
        Document document,
        FamilyManager manager,
        FamilyParameter length,
        FamilyParameter dn,
        FamilyParameter portDiameter,
        FamilyParameter socketDiameter,
        ElementId materialId,
        ValveBuilderResult result)
    {
        View xView = FindFamilyView(document, XYZ.BasisX);
        FamilyParameter leftOuter = EnsureFormulaLengthParameter(
            manager, "Connector Left Face", "-L / 2");
        FamilyParameter leftInner = EnsureFormulaLengthParameter(
            manager, "Connector Left Host End", "Connector Left Face + 2 mm");
        FamilyParameter rightOuter = EnsureFormulaLengthParameter(
            manager, "Connector Right Face", "L / 2");
        FamilyParameter rightInner = EnsureFormulaLengthParameter(
            manager, "Connector Right Host Start", "Connector Right Face - 2 mm");
        Extrusion leftHost = CreateNativeAnnularExtrusion(
            document, manager, XYZ.BasisX, xView,
            socketDiameter, portDiameter, leftOuter, leftInner, materialId);
        Extrusion rightHost = CreateNativeAnnularExtrusion(
            document, manager, XYZ.BasisX, xView,
            socketDiameter, portDiameter, rightInner, rightOuter, materialId);
        document.Regenerate();

        double currentLength = CurrentValue(manager, length, Mm(100));
        Reference leftFace = FindPlanarEndFace(leftHost, -currentLength / 2, -1);
        Reference rightFace = FindPlanarEndFace(rightHost, currentLength / 2, 1);
        ConnectorElement connector1 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            leftFace);
        ConnectorElement connector2 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            rightFace);
        AssociateConnectorDiameter(manager, connector1, dn, result);
        AssociateConnectorDiameter(manager, connector2, dn, result);
        connector1.SetLinkedConnectorElement(connector2);
    }

    private sealed record ValveDetailDimensions(
        double BodyBarrelLengthMm,
        double PortLengthMm,
        double NutLengthMm,
        double RingWidthMm,
        double CollarDiameterMm,
        double SocketDiameterMm,
        double SocketLipDiameterMm,
        double NutDiameterMm,
        double BonnetDiameterMm,
        double StemDiameterMm,
        double HandleWidthMm,
        double HandleThicknessMm);

    private static ValveDetailDimensions CalculateValveDetails(ValveSizeDefinition size)
    {
        double bodyBarrelLength = size.BodyLengthMm * 0.46;
        double portLength = Math.Max(11, size.BodyLengthMm * 0.16);
        double nutLength = Math.Max(
            8,
            (size.BodyLengthMm - bodyBarrelLength - (portLength * 2)) / 2);
        double ringWidth = Math.Max(3, size.BodyDiameterMm * 0.035);
        double socketDiameter = Math.Max(
            size.PortDiameterMm + 12,
            size.BodyDiameterMm * 0.54);
        double nutFactor = Math.Max(0.84, 0.94 - (size.NominalDiameterMm * 0.001));

        return new ValveDetailDimensions(
            bodyBarrelLength,
            portLength,
            nutLength,
            ringWidth,
            size.BodyDiameterMm * 0.92,
            socketDiameter,
            socketDiameter * 1.12,
            size.BodyDiameterMm * nutFactor,
            size.BodyDiameterMm * 0.52,
            Math.Max(8, size.BodyDiameterMm * 0.14),
            Math.Max(12, size.BodyDiameterMm * 0.11),
            Math.Max(6, size.BodyDiameterMm * 0.055));
    }

    private static void SetValveDetailValues(
        FamilyManager manager,
        ValveDetailDimensions detail,
        FamilyParameter bodyBarrelLength,
        FamilyParameter portLength,
        FamilyParameter nutLength,
        FamilyParameter ringWidth,
        FamilyParameter collarDiameter,
        FamilyParameter socketDiameter,
        FamilyParameter socketLipDiameter,
        FamilyParameter nutDiameter,
        FamilyParameter bonnetDiameter,
        FamilyParameter stemDiameter,
        FamilyParameter handleWidth,
        FamilyParameter handleThickness)
    {
        manager.Set(bodyBarrelLength, Mm(detail.BodyBarrelLengthMm));
        manager.Set(portLength, Mm(detail.PortLengthMm));
        manager.Set(nutLength, Mm(detail.NutLengthMm));
        manager.Set(ringWidth, Mm(detail.RingWidthMm));
        manager.Set(collarDiameter, Mm(detail.CollarDiameterMm));
        manager.Set(socketDiameter, Mm(detail.SocketDiameterMm));
        manager.Set(socketLipDiameter, Mm(detail.SocketLipDiameterMm));
        manager.Set(nutDiameter, Mm(detail.NutDiameterMm));
        manager.Set(bonnetDiameter, Mm(detail.BonnetDiameterMm));
        manager.Set(stemDiameter, Mm(detail.StemDiameterMm));
        manager.Set(handleWidth, Mm(detail.HandleWidthMm));
        manager.Set(handleThickness, Mm(detail.HandleThicknessMm));
    }

    private static void SetManufacturerData(
        FamilyManager manager,
        ValveBuilderRequest request,
        ValveSizeDefinition size,
        FamilyParameter manufacturer,
        FamilyParameter model,
        FamilyParameter productCode,
        FamilyParameter nominalSize,
        FamilyParameter catalogRevision,
        FamilyParameter catalogUrl,
        FamilyParameter catalogBasis,
        FamilyParameter geometryAccuracy,
        FamilyParameter lodStatus,
        FamilyParameter cv,
        FamilyParameter torque,
        FamilyParameter weight)
    {
        manager.Set(manufacturer, request.Manufacturer);
        manager.Set(model, request.ProductSeries);
        manager.Set(productCode, size.PartNumber);
        manager.Set(nominalSize, string.IsNullOrWhiteSpace(size.NominalSize)
            ? size.TypeName
            : size.NominalSize);
        manager.Set(catalogRevision, request.CatalogRevision);
        manager.Set(catalogUrl, request.CatalogUrl);
        manager.Set(catalogBasis, request.CatalogDimensionBasis);
        manager.Set(geometryAccuracy, request.GeometryAccuracy);
        manager.Set(lodStatus, request.LodStatus);
        manager.Set(cv, size.Cv > 0
            ? size.Cv.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty);
        manager.Set(torque, size.TorqueNm > 0
            ? $"{size.TorqueNm.ToString("0.###", CultureInfo.InvariantCulture)} N·m"
            : string.Empty);
        manager.Set(weight, size.WeightKg > 0
            ? $"{size.WeightKg.ToString("0.###", CultureInfo.InvariantCulture)} kg"
            : string.Empty);
    }

    private static IReadOnlyList<ValveSizeDefinition> ResolveCatalog(ValveBuilderRequest request)
    {
        if (request.SizeCatalog.Count > 0) return request.SizeCatalog;
        return
        [
            new ValveSizeDefinition(
                request.TypeName,
                request.NominalDiameterMm,
                request.BodyLengthMm,
                request.BodyDiameterMm,
                request.PortDiameterMm > 0
                    ? request.PortDiameterMm
                    : request.NominalDiameterMm,
                request.BodyHeightMm,
                request.HandleLengthMm)
        ];
    }

    private static Dictionary<string, FamilyType> EnsureCatalogTypes(
        FamilyManager manager,
        IReadOnlyList<ValveSizeDefinition> catalog)
    {
        var result = new Dictionary<string, FamilyType>(StringComparer.OrdinalIgnoreCase);
        List<FamilyType> existing = manager.Types.Cast<FamilyType>().ToList();
        FamilyType? reusableTemplateType = existing.Count == 1
            && !existing[0].Name.StartsWith("DN", StringComparison.OrdinalIgnoreCase)
                ? existing[0]
                : null;

        foreach (ValveSizeDefinition size in catalog)
        {
            FamilyType? type = manager.Types.Cast<FamilyType>()
                .FirstOrDefault(item =>
                    item.Name.Equals(size.TypeName, StringComparison.OrdinalIgnoreCase));
            if (type is null && reusableTemplateType is not null)
            {
                manager.CurrentType = reusableTemplateType;
                manager.RenameCurrentType(size.TypeName);
                type = manager.CurrentType;
                reusableTemplateType = null;
            }
            type ??= manager.NewType(size.TypeName);
            result[size.TypeName] = type;
        }
        return result;
    }

    private static void BuildNativeParametricValve(
        Document document,
        FamilyManager manager,
        FamilyParameter length,
        FamilyParameter dn,
        FamilyParameter bodyDiameter,
        FamilyParameter portDiameter,
        FamilyParameter height,
        FamilyParameter handleLength,
        FamilyParameter bodyBarrelLength,
        FamilyParameter portLength,
        FamilyParameter nutLength,
        FamilyParameter ringWidth,
        FamilyParameter collarDiameter,
        FamilyParameter socketDiameter,
        FamilyParameter socketLipDiameter,
        FamilyParameter nutDiameter,
        FamilyParameter bonnetDiameter,
        FamilyParameter stemDiameter,
        FamilyParameter handleWidth,
        FamilyParameter handleThickness,
        ElementId materialId,
        ValveBuilderResult result)
    {
        View xView = FindFamilyView(document, XYZ.BasisX);
        View zView = FindFamilyView(document, XYZ.BasisZ);

        FamilyParameter bodyLeft = EnsureFormulaLengthParameter(
            manager, "Body Left", "-Body Barrel L / 2");
        FamilyParameter bodyRight = EnsureFormulaLengthParameter(
            manager, "Body Right", "Body Barrel L / 2");
        FamilyParameter portLeftOuter = EnsureFormulaLengthParameter(manager, "Port Left Outer", "-L / 2");
        FamilyParameter portLeftInner = EnsureFormulaLengthParameter(
            manager, "Port Left Inner", "Port Left Outer + Port L");
        FamilyParameter portRightOuter = EnsureFormulaLengthParameter(manager, "Port Right Outer", "L / 2");
        FamilyParameter portRightInner = EnsureFormulaLengthParameter(
            manager, "Port Right Inner", "Port Right Outer - Port L");
        FamilyParameter leftRingEnd = EnsureFormulaLengthParameter(
            manager,
            "Body Left Ring End",
            "Body Left + Ring W");
        FamilyParameter rightRingStart = EnsureFormulaLengthParameter(
            manager,
            "Body Right Ring Start",
            "Body Right - Ring W");
        FamilyParameter nutRingDiameter = EnsureFormulaLengthParameter(
            manager,
            "Union Nut Ring OD",
            "Union Nut OD * 1.04");
        FamilyParameter leftNutRingEnd = EnsureFormulaLengthParameter(
            manager,
            "Left Nut Ring End",
            "Port Left Inner + Ring W");
        FamilyParameter rightNutRingStart = EnsureFormulaLengthParameter(
            manager,
            "Right Nut Ring Start",
            "Port Right Inner - Ring W");
        FamilyParameter leftNutEnd = EnsureFormulaLengthParameter(
            manager,
            "Left Nut End",
            "Port Left Inner + Nut L");
        FamilyParameter rightNutStart = EnsureFormulaLengthParameter(
            manager,
            "Right Nut Start",
            "Port Right Inner - Nut L");
        FamilyParameter leftSocketLipEnd = EnsureFormulaLengthParameter(
            manager,
            "Left Socket Lip End",
            "Port Left Outer + Ring W");
        FamilyParameter rightSocketLipStart = EnsureFormulaLengthParameter(
            manager,
            "Right Socket Lip Start",
            "Port Right Outer - Ring W");
        FamilyParameter leftBodyBeadStart = EnsureFormulaLengthParameter(
            manager,
            "Left Body Bead Start",
            "Body Left + Ring W * 1.65");
        FamilyParameter leftBodyBeadEnd = EnsureFormulaLengthParameter(
            manager,
            "Left Body Bead End",
            "Left Body Bead Start + Ring W * 0.42");
        FamilyParameter rightBodyBeadEnd = EnsureFormulaLengthParameter(
            manager,
            "Right Body Bead End",
            "Body Right - Ring W * 1.65");
        FamilyParameter rightBodyBeadStart = EnsureFormulaLengthParameter(
            manager,
            "Right Body Bead Start",
            "Right Body Bead End - Ring W * 0.42");
        FamilyParameter bodyBeadDiameter = EnsureFormulaLengthParameter(
            manager,
            "Body Bead OD",
            "Body OD * 0.91");
        FamilyParameter bodyShellDiameter = EnsureFormulaLengthParameter(
            manager,
            "Body Shell OD",
            "Body OD * 0.84");
        FamilyParameter bodyShoulderDiameter = EnsureFormulaLengthParameter(
            manager,
            "Body Shoulder OD",
            "Body OD * 0.95");
        FamilyParameter bodyCoreLeft = EnsureFormulaLengthParameter(
            manager,
            "Body Core Left",
            "-Body Barrel L * 0.29");
        FamilyParameter bodyCoreRight = EnsureFormulaLengthParameter(
            manager,
            "Body Core Right",
            "Body Barrel L * 0.29");
        FamilyParameter leftShoulderStart = EnsureFormulaLengthParameter(
            manager,
            "Left Shoulder Start",
            "Body Core Left - Ring W * 1.6");
        FamilyParameter leftShoulderEnd = EnsureFormulaLengthParameter(
            manager,
            "Left Shoulder End",
            "Body Core Left + Ring W * 0.45");
        FamilyParameter rightShoulderStart = EnsureFormulaLengthParameter(
            manager,
            "Right Shoulder Start",
            "Body Core Right - Ring W * 0.45");
        FamilyParameter rightShoulderEnd = EnsureFormulaLengthParameter(
            manager,
            "Right Shoulder End",
            "Body Core Right + Ring W * 1.6");
        FamilyParameter bodyTop = EnsureFormulaLengthParameter(
            manager,
            "Body Top",
            "Body OD / 2");
        FamilyParameter valveTop = EnsureFormulaLengthParameter(
            manager,
            "Valve Top",
            "H");
        FamilyParameter handleTop = EnsureFormulaLengthParameter(
            manager,
            "Handle Top",
            "Valve Top - 6 mm");
        FamilyParameter handleBottom = EnsureFormulaLengthParameter(
            manager,
            "Handle Bottom",
            "Handle Top - Handle T");
        FamilyParameter bonnetStart = EnsureFormulaLengthParameter(
            manager,
            "Bonnet Start",
            "Body Top * 0.72");
        FamilyParameter bonnetEnd = EnsureFormulaLengthParameter(
            manager,
            "Bonnet End",
            "Body Top + Bonnet OD * 0.32");
        FamilyParameter boltDiameter = EnsureFormulaLengthParameter(
            manager,
            "Bolt OD",
            "Stem OD * 1.8");
        document.Regenerate();
        BuildParametricReferenceSkeleton(
            document,
            manager,
            length,
            bodyBarrelLength,
            bodyDiameter,
            portDiameter,
            height,
            result);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            bodyShellDiameter, bodyLeft, bodyRight, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            bodyDiameter, bodyCoreLeft, bodyCoreRight, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            bodyShoulderDiameter, leftShoulderStart, leftShoulderEnd, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            bodyShoulderDiameter, rightShoulderStart, rightShoulderEnd, materialId);

        Extrusion leftPort = CreateNativeHollowHexExtrusionX(
            document, manager, xView,
            socketLipDiameter, portDiameter, portLeftOuter, bodyLeft, materialId);
        Extrusion rightPort = CreateNativeHollowHexExtrusionX(
            document, manager, xView,
            nutDiameter, portDiameter, bodyRight, portRightOuter, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            collarDiameter, bodyLeft, leftRingEnd, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisX, xView,
            collarDiameter, rightRingStart, bodyRight, materialId);

        ElementId steelMaterial = EnsureMaterial(document, "FamilyMEP Zinc Plated Steel");
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisZ, zView,
            bonnetDiameter, bonnetStart, bonnetEnd, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisZ, zView,
            stemDiameter, bonnetEnd, handleBottom, materialId);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisZ, zView,
            bonnetDiameter, EnsureFormulaLengthParameter(
                manager, "Retainer Bottom", "Handle Bottom - 2 mm"),
            handleBottom, steelMaterial);

        ElementId gripMaterial = EnsureMaterial(document, "FamilyMEP Blue Handle Grip");
        CreateNativeHandleExtrusion(
            document,
            manager,
            zView,
            handleLength,
            handleWidth,
            handleBottom,
            handleTop,
            gripMaterial);
        FamilyParameter metalHandleLength = EnsureFormulaLengthParameter(
            manager, "Handle Metal L", "Handle L * 0.34");
        FamilyParameter metalHandleWidth = EnsureFormulaLengthParameter(
            manager, "Handle Metal W", "Handle W * 0.62");
        FamilyParameter metalHandleBottom = EnsureFormulaLengthParameter(
            manager, "Handle Metal Bottom", "Handle Bottom - 1 mm");
        FamilyParameter metalHandleTop = EnsureFormulaLengthParameter(
            manager, "Handle Metal Top", "Handle Top + 1 mm");
        CreateNativeHandleExtrusion(
            document,
            manager,
            zView,
            metalHandleLength,
            metalHandleWidth,
            metalHandleBottom,
            metalHandleTop,
            steelMaterial);
        CreateNativeCircularExtrusion(
            document, manager, true, XYZ.BasisZ, zView,
            boltDiameter, handleTop, valveTop, steelMaterial);

        document.Regenerate();
        double currentLength = CurrentValue(manager, length, Mm(100));
        Reference leftFace = FindPlanarEndFace(leftPort, -currentLength / 2, -1);
        Reference rightFace = FindPlanarEndFace(rightPort, currentLength / 2, 1);
        ConnectorElement connector1 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            leftFace);
        ConnectorElement connector2 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            rightFace);
        AssociateConnectorDiameter(manager, connector1, dn, result);
        AssociateConnectorDiameter(manager, connector2, dn, result);
        connector1.SetLinkedConnectorElement(connector2);
    }

    private static void BuildParametricReferenceSkeleton(
        Document document,
        FamilyManager manager,
        FamilyParameter length,
        FamilyParameter bodyBarrelLength,
        FamilyParameter bodyDiameter,
        FamilyParameter portDiameter,
        FamilyParameter height,
        ValveBuilderResult result)
    {
        string stage = "initialize reference skeleton";
        try
        {
            stage = "resolve Ref. Level and elevation views";
            View planView = FindFamilyView(document, XYZ.BasisZ);
            View elevationView = FindFamilyView(document, XYZ.BasisY);
            double currentLength = CurrentValue(manager, length, Mm(100));
            double currentBodyLength = CurrentValue(manager, bodyBarrelLength, currentLength * 0.46);
            double currentBodyDiameter = CurrentValue(manager, bodyDiameter, Mm(50));
            double currentPortDiameter = CurrentValue(manager, portDiameter, Mm(25));
            double currentHeight = CurrentValue(manager, height, Mm(80));
            double axialExtent = Math.Max(currentLength * 0.72, currentBodyDiameter * 1.25);
            double verticalExtent = Math.Max(currentHeight * 1.16, currentBodyDiameter * 1.55);

            stage = "create axial reference planes";
            ReferencePlane axialCenter = CreateAxialReferencePlane(
                document,
                planView,
                0,
                verticalExtent,
                "RP_FamilyMEP_Center_Left_Right");
            ReferencePlane connectionLeft = CreateAxialReferencePlane(
                document,
                planView,
                -currentLength / 2,
                verticalExtent,
                "RP_Connection_Left");
            ReferencePlane connectionRight = CreateAxialReferencePlane(
                document,
                planView,
                currentLength / 2,
                verticalExtent,
                "RP_Connection_Right");
            ReferencePlane bodyLeft = CreateAxialReferencePlane(
                document,
                planView,
                -currentBodyLength / 2,
                verticalExtent * 0.92,
                "RP_Body_Left");
            ReferencePlane bodyRight = CreateAxialReferencePlane(
                document,
                planView,
                currentBodyLength / 2,
                verticalExtent * 0.92,
                "RP_Body_Right");
            document.Regenerate();

            stage = "create overall-length EQ chain";
            Dimension connectionEq = CreateLinearDimension(
                document,
                planView,
                [
                    connectionLeft.GetReference(),
                    axialCenter.GetReference(),
                    connectionRight.GetReference()
                ],
                new XYZ(-currentLength / 2, verticalExtent * 0.72, 0),
                new XYZ(currentLength / 2, verticalExtent * 0.72, 0));
            connectionEq.AreSegmentsEqual = true;
            Dimension overallLength = CreateLinearDimension(
                document,
                planView,
                [connectionLeft.GetReference(), connectionRight.GetReference()],
                new XYZ(-currentLength / 2, verticalExtent * 0.90, 0),
                new XYZ(currentLength / 2, verticalExtent * 0.90, 0));
            overallLength.FamilyLabel = length;

            stage = "create body-length EQ chain";
            Dimension bodyEq = CreateLinearDimension(
                document,
                planView,
                [bodyLeft.GetReference(), axialCenter.GetReference(), bodyRight.GetReference()],
                new XYZ(-currentBodyLength / 2, -verticalExtent * 0.72, 0),
                new XYZ(currentBodyLength / 2, -verticalExtent * 0.72, 0));
            bodyEq.AreSegmentsEqual = true;
            Dimension bodyLengthDimension = CreateLinearDimension(
                document,
                planView,
                [bodyLeft.GetReference(), bodyRight.GetReference()],
                new XYZ(-currentBodyLength / 2, -verticalExtent * 0.90, 0),
                new XYZ(currentBodyLength / 2, -verticalExtent * 0.90, 0));
            bodyLengthDimension.FamilyLabel = bodyBarrelLength;

            stage = "create elevation reference planes";
            ReferencePlane pipeCenter = CreateHorizontalReferencePlane(
                document,
                elevationView,
                0,
                axialExtent,
                "RP_Pipe_Center");
            ReferencePlane bodyBottom = CreateHorizontalReferencePlane(
                document,
                elevationView,
                -currentBodyDiameter / 2,
                axialExtent,
                "RP_Body_Bottom");
            ReferencePlane bodyTop = CreateHorizontalReferencePlane(
                document,
                elevationView,
                currentBodyDiameter / 2,
                axialExtent,
                "RP_Body_Top");
            ReferencePlane portBottom = CreateHorizontalReferencePlane(
                document,
                elevationView,
                -currentPortDiameter / 2,
                axialExtent * 0.92,
                "RP_Port_Bottom");
            ReferencePlane portTop = CreateHorizontalReferencePlane(
                document,
                elevationView,
                currentPortDiameter / 2,
                axialExtent * 0.92,
                "RP_Port_Top");
            ReferencePlane valveTop = CreateHorizontalReferencePlane(
                document,
                elevationView,
                currentHeight,
                axialExtent * 0.72,
                "RP_Valve_Top");
            document.Regenerate();

            stage = "create body-diameter EQ chain";
            Dimension bodyDiameterEq = CreateLinearDimension(
                document,
                elevationView,
                [bodyBottom.GetReference(), pipeCenter.GetReference(), bodyTop.GetReference()],
                new XYZ(-axialExtent * 0.62, 0, -currentBodyDiameter / 2),
                new XYZ(-axialExtent * 0.62, 0, currentBodyDiameter / 2));
            bodyDiameterEq.AreSegmentsEqual = true;
            Dimension bodyDiameterDimension = CreateLinearDimension(
                document,
                elevationView,
                [bodyBottom.GetReference(), bodyTop.GetReference()],
                new XYZ(-axialExtent * 0.78, 0, -currentBodyDiameter / 2),
                new XYZ(-axialExtent * 0.78, 0, currentBodyDiameter / 2));
            bodyDiameterDimension.FamilyLabel = bodyDiameter;

            stage = "create port-diameter EQ chain";
            Dimension portDiameterEq = CreateLinearDimension(
                document,
                elevationView,
                [portBottom.GetReference(), pipeCenter.GetReference(), portTop.GetReference()],
                new XYZ(axialExtent * 0.62, 0, -currentPortDiameter / 2),
                new XYZ(axialExtent * 0.62, 0, currentPortDiameter / 2));
            portDiameterEq.AreSegmentsEqual = true;
            Dimension portDiameterDimension = CreateLinearDimension(
                document,
                elevationView,
                [portBottom.GetReference(), portTop.GetReference()],
                new XYZ(axialExtent * 0.78, 0, -currentPortDiameter / 2),
                new XYZ(axialExtent * 0.78, 0, currentPortDiameter / 2));
            portDiameterDimension.FamilyLabel = portDiameter;

            stage = "create valve-height dimension";
            Dimension heightDimension = CreateLinearDimension(
                document,
                elevationView,
                [pipeCenter.GetReference(), valveTop.GetReference()],
                new XYZ(axialExtent * 0.94, 0, 0),
                new XYZ(axialExtent * 0.94, 0, currentHeight));
            heightDimension.FamilyLabel = height;
            document.Regenerate();

            result.AppliedParameters.Add(
                "Reference skeleton: L/Body L and Body OD/Port ID centered with EQ constraints");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Reference-plane and EQ dimension skeleton failed at '{stage}': {exception.Message}",
                exception);
        }
    }

    private static ReferencePlane CreateAxialReferencePlane(
        Document document,
        View view,
        double x,
        double extent,
        string name)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            new XYZ(x, -extent, 0),
            new XYZ(x, extent, 0),
            XYZ.BasisZ,
            view);
        plane.Name = name;
        return plane;
    }

    private static ReferencePlane CreateHorizontalReferencePlane(
        Document document,
        View view,
        double z,
        double extent,
        string name)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            new XYZ(-extent, 0, z),
            new XYZ(extent, 0, z),
            XYZ.BasisY,
            view);
        plane.Name = name;
        return plane;
    }

    private static Dimension CreateLinearDimension(
        Document document,
        View view,
        IReadOnlyList<Reference> references,
        XYZ start,
        XYZ end)
    {
        var referenceArray = new ReferenceArray();
        foreach (Reference reference in references)
            referenceArray.Append(reference);
        return document.FamilyCreate.NewLinearDimension(
            view,
            Line.CreateBound(start, end),
            referenceArray);
    }

    private static FamilyParameter EnsureFormulaLengthParameter(
        FamilyManager manager,
        string name,
        string formula)
    {
        FamilyParameter parameter = EnsureLengthParameter(manager, name);
        if (!string.Equals(parameter.Formula, formula, StringComparison.Ordinal))
            manager.SetFormula(parameter, formula);
        return parameter;
    }

    private static Extrusion CreateNativeCircularExtrusion(
        Document document,
        FamilyManager manager,
        bool isSolid,
        XYZ axis,
        View dimensionView,
        FamilyParameter diameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double initialDiameter = CurrentValue(manager, diameter, Mm(20));
        double radius = Math.Max(initialDiameter / 2, Mm(0.5));
        (XYZ profileX, XYZ profileY) = ProfileAxes(axis);
        var curves = new CurveArray();
        curves.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, profileX, profileY));
        curves.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, profileX, profileY));
        var profile = new CurveArrArray();
        profile.Append(curves);
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(axis, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            isSolid,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        Reference arcReference = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Arc>()
            .Select(arc => arc.Reference)
            .First(reference => reference is not null);
        XYZ dimensionOrigin = axis.IsAlmostEqualTo(XYZ.BasisX)
            ? new XYZ(0, radius * 1.35, 0)
            : new XYZ(radius * 1.35, 0, 0);
        Dimension dimension = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            arcReference,
            dimensionOrigin);
        dimension.FamilyLabel = diameter;
        return extrusion;
    }

    private static Extrusion CreateNativeAnnularExtrusion(
        Document document,
        FamilyManager manager,
        XYZ axis,
        View dimensionView,
        FamilyParameter outerDiameter,
        FamilyParameter innerDiameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double initialOuterDiameter = CurrentValue(manager, outerDiameter, Mm(24));
        double initialInnerDiameter = CurrentValue(manager, innerDiameter, Mm(16));
        double outerRadius = Math.Max(initialOuterDiameter / 2, Mm(1));
        double innerRadius = Math.Clamp(
            initialInnerDiameter / 2,
            Mm(0.5),
            outerRadius - Mm(0.5));
        (XYZ profileX, XYZ profileY) = ProfileAxes(axis);

        static CurveArray CreateCircle(
            double radius,
            XYZ xDirection,
            XYZ yDirection)
        {
            var circle = new CurveArray();
            circle.Append(Arc.Create(
                XYZ.Zero, radius, 0, Math.PI, xDirection, yDirection));
            circle.Append(Arc.Create(
                XYZ.Zero, radius, Math.PI, 2 * Math.PI, xDirection, yDirection));
            return circle;
        }

        var profile = new CurveArrArray();
        profile.Append(CreateCircle(outerRadius, profileX, profileY));
        profile.Append(CreateCircle(innerRadius, profileX, profileY));
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(axis, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Arc> arcs = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Arc>()
            .Where(arc => arc.Reference is not null)
            .OrderByDescending(arc => arc.Radius)
            .ToList();
        Arc outerArc = arcs.First();
        Arc innerArc = arcs.Last();
        XYZ outerDimensionOrigin = axis.IsAlmostEqualTo(XYZ.BasisX)
            ? new XYZ(0, outerRadius * 1.35, 0)
            : new XYZ(outerRadius * 1.35, 0, 0);
        XYZ innerDimensionOrigin = axis.IsAlmostEqualTo(XYZ.BasisX)
            ? new XYZ(0, innerRadius * 0.65, 0)
            : new XYZ(innerRadius * 0.65, 0, 0);
        Dimension outerDimension = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            outerArc.Reference,
            outerDimensionOrigin);
        outerDimension.FamilyLabel = outerDiameter;
        Dimension innerDimension = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            innerArc.Reference,
            innerDimensionOrigin);
        innerDimension.FamilyLabel = innerDiameter;
        return extrusion;
    }

    private static Extrusion CreateNativeHexExtrusionX(
        Document document,
        FamilyManager manager,
        View dimensionView,
        FamilyParameter acrossFlats,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double initialAf = CurrentValue(manager, acrossFlats, Mm(30));
        double radius = initialAf / Math.Sqrt(3);
        List<XYZ> points = Enumerable.Range(0, 6)
            .Select(index =>
            {
                double angle = Math.PI / 6 + index * Math.PI / 3;
                return new XYZ(0, radius * Math.Cos(angle), radius * Math.Sin(angle));
            })
            .ToList();
        var polygon = new CurveArray();
        for (int index = 0; index < points.Count; index++)
            polygon.Append(Line.CreateBound(points[index], points[(index + 1) % points.Count]));
        var profile = new CurveArrArray();
        profile.Append(polygon);
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Line> verticalSides = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => Math.Abs(line.Direction.Z) > 0.99)
            .OrderBy(line => line.Evaluate(0.5, true).Y)
            .ToList();
        if (verticalSides.Count >= 2)
        {
            var references = new ReferenceArray();
            references.Append(verticalSides.First().Reference);
            references.Append(verticalSides.Last().Reference);
            Line dimensionLine = Line.CreateBound(
                new XYZ(0, -initialAf, radius * 1.35),
                new XYZ(0, initialAf, radius * 1.35));
            Dimension dimension = document.FamilyCreate.NewLinearDimension(
                dimensionView,
                dimensionLine,
                references);
            dimension.FamilyLabel = acrossFlats;
        }
        return extrusion;
    }

    private static Extrusion CreateNativeHollowHexExtrusionX(
        Document document,
        FamilyManager manager,
        View dimensionView,
        FamilyParameter acrossFlats,
        FamilyParameter innerDiameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double initialAf = CurrentValue(manager, acrossFlats, Mm(30));
        double initialInnerDiameter = CurrentValue(manager, innerDiameter, Mm(18));
        double outerRadius = initialAf / Math.Sqrt(3);
        double innerRadius = Math.Clamp(
            initialInnerDiameter / 2,
            Mm(0.5),
            initialAf / 2 - Mm(0.5));
        List<XYZ> points = Enumerable.Range(0, 6)
            .Select(index =>
            {
                double angle = Math.PI / 6 + index * Math.PI / 3;
                return new XYZ(
                    0,
                    outerRadius * Math.Cos(angle),
                    outerRadius * Math.Sin(angle));
            })
            .ToList();
        var polygon = new CurveArray();
        for (int index = 0; index < points.Count; index++)
            polygon.Append(Line.CreateBound(
                points[index],
                points[(index + 1) % points.Count]));
        var bore = new CurveArray();
        bore.Append(Arc.Create(
            XYZ.Zero,
            innerRadius,
            0,
            Math.PI,
            XYZ.BasisY,
            XYZ.BasisZ));
        bore.Append(Arc.Create(
            XYZ.Zero,
            innerRadius,
            Math.PI,
            2 * Math.PI,
            XYZ.BasisY,
            XYZ.BasisZ));
        var profile = new CurveArrArray();
        profile.Append(polygon);
        profile.Append(bore);
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Line> verticalSides = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => Math.Abs(line.Direction.Z) > 0.99)
            .OrderBy(line => line.Evaluate(0.5, true).Y)
            .ToList();
        if (verticalSides.Count >= 2)
        {
            var references = new ReferenceArray();
            references.Append(verticalSides.First().Reference);
            references.Append(verticalSides.Last().Reference);
            Dimension outerDimension = document.FamilyCreate.NewLinearDimension(
                dimensionView,
                Line.CreateBound(
                    new XYZ(0, -initialAf, outerRadius * 1.35),
                    new XYZ(0, initialAf, outerRadius * 1.35)),
                references);
            outerDimension.FamilyLabel = acrossFlats;
        }

        Arc innerArc = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Arc>()
            .First(arc => arc.Reference is not null);
        Dimension innerDimension = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            innerArc.Reference,
            new XYZ(0, innerRadius * 0.65, 0));
        innerDimension.FamilyLabel = innerDiameter;
        return extrusion;
    }

    private static Extrusion CreateNativeHandleExtrusion(
        Document document,
        FamilyManager manager,
        View dimensionView,
        FamilyParameter handleLength,
        FamilyParameter handleWidth,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double initialLength = CurrentValue(manager, handleLength, Mm(100));
        double initialWidth = CurrentValue(manager, handleWidth, Mm(12));
        double halfWidth = initialWidth / 2;
        List<XYZ> points =
        [
            new XYZ(0, -halfWidth, 0),
            new XYZ(initialLength, -halfWidth, 0),
            new XYZ(initialLength, halfWidth, 0),
            new XYZ(0, halfWidth, 0)
        ];
        var rectangle = new CurveArray();
        for (int index = 0; index < points.Count; index++)
            rectangle.Append(Line.CreateBound(points[index], points[(index + 1) % points.Count]));
        var profile = new CurveArrArray();
        profile.Append(rectangle);
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Line> ySides = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => Math.Abs(line.Direction.Y) > 0.99)
            .OrderBy(line => line.Evaluate(0.5, true).X)
            .ToList();
        if (ySides.Count >= 2)
        {
            Reference startReference = ySides.First().Reference;
            Reference endReference = ySides.Last().Reference;
            var references = new ReferenceArray();
            references.Append(startReference);
            references.Append(endReference);
            Dimension dimension = document.FamilyCreate.NewLinearDimension(
                dimensionView,
                Line.CreateBound(
                    new XYZ(0, halfWidth * 1.8, 0),
                    new XYZ(initialLength, halfWidth * 1.8, 0)),
                references);
            dimension.FamilyLabel = handleLength;

            ReferencePlane? centerPlane = FindCenterReferencePlane(document, XYZ.BasisX);
            if (centerPlane is not null)
            {
                try
                {
                    document.FamilyCreate.NewAlignment(
                        dimensionView,
                        centerPlane.GetReference(),
                        startReference);
                }
                catch
                {
                    // The labeled length still flexes even if the template center plane cannot be locked.
                }
            }
        }

        List<Line> xSides = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => Math.Abs(line.Direction.X) > 0.99)
            .OrderBy(line => line.Evaluate(0.5, true).Y)
            .ToList();
        if (xSides.Count >= 2)
        {
            var widthReferences = new ReferenceArray();
            widthReferences.Append(xSides.First().Reference);
            widthReferences.Append(xSides.Last().Reference);
            Dimension widthDimension = document.FamilyCreate.NewLinearDimension(
                dimensionView,
                Line.CreateBound(
                    new XYZ(initialLength * 0.72, -halfWidth, 0),
                    new XYZ(initialLength * 0.72, halfWidth, 0)),
                widthReferences);
            widthDimension.FamilyLabel = handleWidth;
        }
        return extrusion;
    }

    private static void AssociateExtrusionBounds(
        FamilyManager manager,
        Extrusion extrusion,
        FamilyParameter start,
        FamilyParameter end)
    {
        Parameter startParameter = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM)
            ?? throw new InvalidOperationException("Extrusion Start parameter is unavailable.");
        Parameter endParameter = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM)
            ?? throw new InvalidOperationException("Extrusion End parameter is unavailable.");
        manager.AssociateElementParameterToFamilyParameter(startParameter, start);
        manager.AssociateElementParameterToFamilyParameter(endParameter, end);
    }

    private static void SetFormMaterial(Extrusion extrusion, ElementId materialId)
    {
        if (materialId == ElementId.InvalidElementId) return;
        Parameter? material = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
        {
            try { material.Set(materialId); }
            catch { }
        }
    }

    private static (XYZ X, XYZ Y) ProfileAxes(XYZ normal)
    {
        if (normal.IsAlmostEqualTo(XYZ.BasisX))
            return (XYZ.BasisY, XYZ.BasisZ);
        if (normal.IsAlmostEqualTo(XYZ.BasisZ))
            return (XYZ.BasisX, XYZ.BasisY);
        throw new NotSupportedException("Only X-axis and Z-axis native extrusions are supported.");
    }

    private static View FindFamilyView(Document document, XYZ direction)
    {
        View? view = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(item => !item.IsTemplate && item.ViewType != ViewType.ThreeD)
            .OrderBy(item =>
                item.Name.Equals("Ref. Level", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(item =>
                Math.Abs(item.ViewDirection.Normalize().DotProduct(direction.Normalize())) > 0.99);
        return view
            ?? throw new InvalidOperationException(
                $"The Generic Model template does not contain a 2D view normal to ({direction.X}, {direction.Y}, {direction.Z}).");
    }

    private static ReferencePlane? FindCenterReferencePlane(Document document, XYZ normal)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(ReferencePlane))
            .Cast<ReferencePlane>()
            .FirstOrDefault(plane =>
            {
                try
                {
                    return Math.Abs(plane.GetPlane().Normal.Normalize().DotProduct(normal.Normalize())) > 0.99
                           && Math.Abs(plane.GetPlane().Origin.DotProduct(normal)) < 1e-6;
                }
                catch
                {
                    return false;
                }
            });
    }

    private static double CurrentValue(
        FamilyManager manager,
        FamilyParameter parameter,
        double fallback)
    {
        try
        {
            double value = manager.CurrentType?.AsDouble(parameter) ?? fallback;
            return value > 1e-8 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void ApplyLookupFormula(
        FamilyManager manager,
        FamilyParameter parameter,
        string lookupColumn,
        string tableName,
        ValveBuilderResult result)
    {
        string formula = $"size_lookup(\"{tableName}\", \"{lookupColumn}\", 1 mm, DN)";
        try
        {
            manager.SetFormula(parameter, formula);
            result.AppliedParameters.Add($"{parameter.Definition.Name} → {formula}");
        }
        catch (Exception exception)
        {
            result.Warnings.Add(
                $"Formula for '{parameter.Definition.Name}' could not be assigned: {exception.Message}");
        }
    }

    private static FamilyParameter EnsureLengthParameter(FamilyManager manager, string name)
    {
        FamilyParameter? existing = manager.Parameters.Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return manager.AddParameter(name, BuiltInParameterGroup.PG_GEOMETRY, ParameterType.Length, false);
#else
        return manager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
#endif
    }

    private static FamilyParameter EnsureTextParameter(FamilyManager manager, string name)
    {
        FamilyParameter? existing = manager.Parameters.Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return manager.AddParameter(name, BuiltInParameterGroup.PG_IDENTITY_DATA, ParameterType.Text, false);
#else
        return manager.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
#endif
    }

    private static void BuildValveGeometry(
        Document document,
        ValveBuilderRequest request,
        ElementId materialId)
    {
        double totalLength = Mm(request.BodyLengthMm);
        double dn = Mm(request.NominalDiameterMm);
        double bodyDiameter = Mm(
            request.BodyDiameterMm > 0
                ? request.BodyDiameterMm
                : Math.Max(request.NominalDiameterMm * 1.35, request.NominalDiameterMm + 12));
        double height = Mm(request.BodyHeightMm);
        double handleLength = Mm(
            request.HandleLengthMm > 0 ? request.HandleLengthMm : request.BodyLengthMm);

        double bodyLength = totalLength * 0.48;
        double bodyStart = -bodyLength * 0.53;
        double bodyEnd = bodyStart + bodyLength;
        double bodyRadius = bodyDiameter / 2;
        double portDiameter = Mm(
            request.PortDiameterMm > 0
                ? request.PortDiameterMm
                : request.NominalDiameterMm);
        double boreRadius = Math.Max(portDiameter / 2, Mm(5));
        double bandWidth = Math.Clamp(bodyLength * 0.055, Mm(2.2), Mm(5.5));
        double leftEndLength = bodyStart + totalLength / 2 + bandWidth;
        double rightEndStart = bodyEnd - bandWidth;
        double rightEndLength = totalLength / 2 - rightEndStart;
        double endHexRadius = Math.Max(boreRadius * 1.34, bodyRadius * 0.76);
        double endLipRadius = endHexRadius * 0.86;

        // Cast body: a rounded central chamber with smaller shoulders instead of
        // a uniform pipe-like cylinder.
        CreateCylinderX(
            document,
            bodyStart,
            bodyLength,
            bodyRadius * 0.88,
            materialId);
        CreateCylinderX(
            document,
            bodyStart + bodyLength * 0.13,
            bodyLength * 0.69,
            bodyRadius,
            materialId);
        CreateCylinderX(
            document,
            bodyStart + bodyLength * 0.08,
            bandWidth,
            bodyRadius * 0.95,
            materialId);
        CreateCylinderX(
            document,
            bodyEnd - bodyLength * 0.13,
            bandWidth,
            bodyRadius * 0.95,
            materialId);

        // Fixed-size nested geometry allows true polygonal wrench flats.
        CreateHollowHexPrismX(
            document,
            -totalLength / 2,
            leftEndLength,
            endHexRadius,
            boreRadius * 0.90,
            materialId);
        CreateHollowHexPrismX(
            document,
            rightEndStart,
            rightEndLength,
            endHexRadius * 1.02,
            boreRadius * 0.90,
            materialId);
        CreateHollowCylinderX(
            document,
            -totalLength / 2,
            bandWidth,
            endLipRadius,
            boreRadius * 0.90,
            materialId);
        CreateHollowCylinderX(
            document,
            totalLength / 2 - bandWidth,
            bandWidth,
            endLipRadius,
            boreRadius * 0.90,
            materialId);

        // Internal thread crests. They are individual annular solids so the opening
        // reads like a female threaded valve in shaded and section views.
        double threadZone = Math.Min(leftEndLength * 0.72, rightEndLength * 0.72);
        double threadWidth = Math.Max(Mm(0.8), threadZone * 0.08);
        double threadOuter = boreRadius * 0.99;
        double threadInner = boreRadius * 0.81;
        for (int index = 0; index < 4; index++)
        {
            double inset = Mm(1.5) + index * threadZone * 0.18;
            if (inset + threadWidth >= threadZone) break;
            CreateHollowCylinderX(
                document,
                -totalLength / 2 + inset,
                threadWidth,
                threadOuter,
                threadInner,
                materialId);
            CreateHollowCylinderX(
                document,
                totalLength / 2 - inset - threadWidth,
                threadWidth,
                threadOuter,
                threadInner,
                materialId);
        }

        // Integrated cast bonnet, packing gland, stem plate and handle nut.
        double handleBottom = Math.Max(
            bodyRadius * 1.14,
            height - Mm(23));
        double bonnetStart = bodyRadius * 0.58;
        double bonnetHeight = Math.Clamp(bodyDiameter * 0.23, Mm(10), Mm(34));
        double stemStart = bonnetStart + bonnetHeight * 0.62;
        double stemHeight = Math.Max(handleBottom - stemStart, Mm(8));
        CreateCylinderZ(
            document, 0, bonnetStart, bonnetHeight,
            bodyRadius * 0.36, materialId);
        CreateCylinderZ(
            document, 0, bonnetStart + bonnetHeight * 0.10, bonnetHeight * 0.22,
            bodyRadius * 0.43, materialId);
        CreateCylinderZ(
            document, 0, stemStart, stemHeight,
            Math.Max(bodyRadius * 0.13, Mm(4)), materialId);
        CreateCylinderZ(
            document, 0, handleBottom - Mm(4), Mm(4),
            bodyRadius * 0.34, materialId);
        ElementId steelMaterial = EnsureMaterial(
            document,
            "FamilyMEP Zinc Plated Steel");
        CreateHexPrismZ(
            document,
            0,
            handleBottom + Mm(7),
            Mm(8),
            bodyRadius * 0.28,
            steelMaterial);

        ElementId gripMaterial = EnsureMaterial(document, "FamilyMEP Blue Handle Grip");
        CreateLeverHandle(
            document,
            handleBottom,
            Math.Max(handleLength, Mm(70)),
            steelMaterial,
            gripMaterial);

        document.Regenerate();
    }

    private static FreeFormElement CreateHollowCylinderX(
        Document document,
        double xStart,
        double length,
        double outerRadius,
        double innerRadius,
        ElementId materialId)
    {
        XYZ center = new(xStart, 0, 0);
        CurveLoop outer = CircleLoop(center, outerRadius, XYZ.BasisY, XYZ.BasisZ);
        CurveLoop inner = CircleLoop(center, innerRadius, XYZ.BasisY, XYZ.BasisZ);
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
            [outer, inner],
            XYZ.BasisX,
            length);
        return CreateFreeForm(document, solid, materialId);
    }

    private static FreeFormElement CreateHollowHexPrismX(
        Document document,
        double xStart,
        double length,
        double outerRadius,
        double innerRadius,
        ElementId materialId)
    {
        var points = Enumerable.Range(0, 6)
            .Select(index =>
            {
                double angle = Math.PI / 6 + index * Math.PI / 3;
                return new XYZ(xStart, outerRadius * Math.Cos(angle), outerRadius * Math.Sin(angle));
            })
            .ToList();
        CurveLoop outer = PolygonLoop(points);
        CurveLoop inner = CircleLoop(new XYZ(xStart, 0, 0), innerRadius, XYZ.BasisY, XYZ.BasisZ);
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
            [outer, inner],
            XYZ.BasisX,
            length);
        return CreateFreeForm(document, solid, materialId);
    }

    private static CurveLoop CircleLoop(
        XYZ center,
        double radius,
        XYZ xAxis,
        XYZ yAxis)
    {
        var loop = new CurveLoop();
        loop.Append(Arc.Create(center, radius, 0, Math.PI, xAxis, yAxis));
        loop.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, xAxis, yAxis));
        return loop;
    }

    private static void CreateLeverHandle(
        Document document,
        double baseZ,
        double handleLength,
        ElementId metalMaterial,
        ElementId gripMaterial)
    {
        double halfWidth = Mm(6);
        double rise = Mm(15);
        double elbowLength = Math.Min(handleLength * 0.24, Mm(35));
        double metalEnd = Math.Min(handleLength * 0.42, Mm(58));
        double thickness = Mm(8);

        List<XYZ> sideProfile =
        [
            new XYZ(-Mm(10), -halfWidth, baseZ),
            new XYZ(Mm(10), -halfWidth, baseZ),
            new XYZ(elbowLength, -halfWidth, baseZ + rise),
            new XYZ(metalEnd, -halfWidth, baseZ + rise),
            new XYZ(metalEnd, -halfWidth, baseZ + rise + thickness),
            new XYZ(elbowLength - Mm(4), -halfWidth, baseZ + rise + thickness),
            new XYZ(Mm(5), -halfWidth, baseZ + thickness),
            new XYZ(-Mm(10), -halfWidth, baseZ + thickness)
        ];
        CurveLoop metalLoop = PolygonLoop(sideProfile);
        Solid metal = GeometryCreationUtilities.CreateExtrusionGeometry(
            [metalLoop],
            XYZ.BasisY,
            halfWidth * 2);
        CreateFreeForm(document, metal, metalMaterial);

        double gripStart = metalEnd - Mm(3);
        double gripLength = Math.Max(handleLength - gripStart, Mm(35));
        List<XYZ> gripProfile =
        [
            new XYZ(gripStart, -halfWidth * 1.12, baseZ + rise - Mm(1)),
            new XYZ(gripStart + gripLength, -halfWidth * 1.12, baseZ + rise - Mm(1)),
            new XYZ(gripStart + gripLength, -halfWidth * 1.12, baseZ + rise + thickness + Mm(1)),
            new XYZ(gripStart, -halfWidth * 1.12, baseZ + rise + thickness + Mm(1))
        ];
        CurveLoop gripLoop = PolygonLoop(gripProfile);
        Solid grip = GeometryCreationUtilities.CreateExtrusionGeometry(
            [gripLoop],
            XYZ.BasisY,
            halfWidth * 2.24);
        CreateFreeForm(document, grip, gripMaterial);
    }

    private static FreeFormElement CreateCylinderX(
        Document document,
        double xStart,
        double length,
        double radius,
        ElementId materialId)
    {
        XYZ center = new(xStart, 0, 0);
        var loop = new CurveLoop();
        loop.Append(Arc.Create(center, radius, 0, Math.PI, XYZ.BasisY, XYZ.BasisZ));
        loop.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, XYZ.BasisY, XYZ.BasisZ));
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisX, length);
        return CreateFreeForm(document, solid, materialId);
    }

    private static FreeFormElement CreateCylinderZ(
        Document document,
        double x,
        double zStart,
        double height,
        double radius,
        ElementId materialId)
    {
        XYZ center = new(x, 0, zStart);
        var loop = new CurveLoop();
        loop.Append(Arc.Create(center, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        loop.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisZ, height);
        return CreateFreeForm(document, solid, materialId);
    }

    private static FreeFormElement CreateHexPrismX(
        Document document,
        double xStart,
        double length,
        double radius,
        ElementId materialId)
    {
        var points = Enumerable.Range(0, 6)
            .Select(index =>
            {
                double angle = Math.PI / 6 + index * Math.PI / 3;
                return new XYZ(xStart, radius * Math.Cos(angle), radius * Math.Sin(angle));
            })
            .ToList();
        CurveLoop loop = PolygonLoop(points);
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisX, length);
        return CreateFreeForm(document, solid, materialId);
    }

    private static FreeFormElement CreateHexPrismZ(
        Document document,
        double x,
        double zStart,
        double height,
        double radius,
        ElementId materialId)
    {
        var points = Enumerable.Range(0, 6)
            .Select(index =>
            {
                double angle = Math.PI / 6 + index * Math.PI / 3;
                return new XYZ(x + radius * Math.Cos(angle), radius * Math.Sin(angle), zStart);
            })
            .ToList();
        CurveLoop loop = PolygonLoop(points);
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisZ, height);
        return CreateFreeForm(document, solid, materialId);
    }

    private static FreeFormElement CreateBox(
        Document document,
        XYZ origin,
        double length,
        double width,
        double height,
        ElementId materialId)
    {
        List<XYZ> points =
        [
            origin,
            origin + new XYZ(length, 0, 0),
            origin + new XYZ(length, width, 0),
            origin + new XYZ(0, width, 0)
        ];
        CurveLoop loop = PolygonLoop(points);
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisZ, height);
        return CreateFreeForm(document, solid, materialId);
    }

    private static CurveLoop PolygonLoop(IReadOnlyList<XYZ> points)
    {
        var loop = new CurveLoop();
        for (int index = 0; index < points.Count; index++)
            loop.Append(Line.CreateBound(points[index], points[(index + 1) % points.Count]));
        return loop;
    }

    private static FreeFormElement CreateFreeForm(
        Document document,
        Solid solid,
        ElementId materialId)
    {
        FreeFormElement element = FreeFormElement.Create(document, solid);
        Parameter? material = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
        {
            try { material.Set(materialId); }
            catch { }
        }
        return element;
    }

    private static void CreateParametricConnectorPair(
        Document document,
        FamilyManager manager,
        FamilyParameter lengthParameter,
        FamilyParameter diameterParameter,
        ElementId materialId,
        ValveBuilderResult result)
    {
        FamilyParameter leftPosition = EnsureLengthParameter(manager, "Connector Left Position");
        FamilyParameter rightPosition = EnsureLengthParameter(manager, "Connector Right Position");
        manager.SetFormula(leftPosition, "-L / 2");
        manager.SetFormula(rightPosition, "L / 2");

        Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero);
        SketchPlane sketchPlane = SketchPlane.Create(document, plane);
        var circle = new CurveArray();
        double hostRadius = Mm(0.75);
        circle.Append(Arc.Create(XYZ.Zero, hostRadius, 0, Math.PI, XYZ.BasisY, XYZ.BasisZ));
        circle.Append(Arc.Create(XYZ.Zero, hostRadius, Math.PI, 2 * Math.PI, XYZ.BasisY, XYZ.BasisZ));
        var profile = new CurveArrArray();
        profile.Append(circle);
        Extrusion host = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(20));

        Parameter? start = host.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
        Parameter? end = host.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
        if (start is null || end is null)
            throw new InvalidOperationException("The connector host extrusion does not expose Start/End parameters.");
        manager.AssociateElementParameterToFamilyParameter(start, leftPosition);
        manager.AssociateElementParameterToFamilyParameter(end, rightPosition);

        Parameter? material = host.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
        {
            try { material.Set(materialId); }
            catch { }
        }

        document.Regenerate();
        double currentLength = manager.CurrentType?.AsDouble(lengthParameter) ?? Mm(100);
        Reference leftFace = FindPlanarEndFace(host, -currentLength / 2, -1);
        Reference rightFace = FindPlanarEndFace(host, currentLength / 2, 1);
        ConnectorElement connector1 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            leftFace);
        ConnectorElement connector2 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            rightFace);
        AssociateConnectorDiameter(manager, connector1, diameterParameter, result);
        AssociateConnectorDiameter(manager, connector2, diameterParameter, result);
        connector1.SetLinkedConnectorElement(connector2);
    }

    private static Reference FindPlanarEndFace(
        Element element,
        double targetX,
        int normalDirection)
    {
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        PlanarFace? best = element.get_Geometry(options)
            .OfType<Solid>()
            .Where(solid => solid.Faces.Size > 0)
            .SelectMany(solid => solid.Faces.Cast<Face>())
            .OfType<PlanarFace>()
            .Where(face => Math.Abs(face.FaceNormal.X - normalDirection) < 0.01)
            .OrderBy(face => Math.Abs(face.Origin.X - targetX))
            .FirstOrDefault();
        return best?.Reference
            ?? throw new InvalidOperationException("Could not resolve a planar end face for a pipe connector.");
    }

    private static void AssociateConnectorDiameter(
        FamilyManager manager,
        ConnectorElement connector,
        FamilyParameter diameterParameter,
        ValveBuilderResult result)
    {
        Parameter? parameter = connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
        if (parameter is null || parameter.IsReadOnly)
        {
            result.Warnings.Add(
                "A pipe connector was created but its Diameter parameter could not be associated with DN.");
            return;
        }
        try
        {
            manager.AssociateElementParameterToFamilyParameter(parameter, diameterParameter);
        }
        catch (Exception exception)
        {
            result.Warnings.Add(
                $"Connector Diameter association failed: {exception.Message}");
        }
    }

    private static void SetConnectorDiameter(
        ConnectorElement connector,
        double diameter,
        ValveBuilderResult result)
    {
        Parameter? parameter = connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
        if (parameter is null || parameter.IsReadOnly)
        {
            result.Warnings.Add("A pipe connector was created but its Diameter parameter could not be set.");
            return;
        }
        parameter.Set(diameter);
    }

    private static ElementId EnsureMaterial(Document document, string materialName)
    {
        Material? material = new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(item => item.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
        if (material is not null) return material.Id;

        ElementId id = Material.Create(
            document,
            string.IsNullOrWhiteSpace(materialName) ? "FamilyMEP Valve Material" : materialName);
        material = (Material)document.GetElement(id);
        material.Color =
            materialName.Contains("Blue", StringComparison.OrdinalIgnoreCase)
                ? new Autodesk.Revit.DB.Color(30, 94, 190)
                : materialName.Contains("Brass", StringComparison.OrdinalIgnoreCase)
                    ? new Autodesk.Revit.DB.Color(184, 132, 43)
                    : materialName.Contains("Grip", StringComparison.OrdinalIgnoreCase)
                        ? new Autodesk.Revit.DB.Color(42, 55, 72)
                        : new Autodesk.Revit.DB.Color(184, 190, 198);
        return id;
    }

    private static string WriteLookupCsv(ValveBuilderRequest request)
    {
        string outputFolder = Path.GetDirectoryName(request.OutputPath)
            ?? AppPaths.GeneratedValveFolder;
        Directory.CreateDirectory(outputFolder);
        string safeFamilyName = SanitizeFileName(request.FamilyName);
        string path = Path.Combine(outputFolder, $"{safeFamilyName}_LTN.csv");
        var builder = new StringBuilder();
        builder.AppendLine(
            ",DN##length##millimeters,L##length##millimeters,Body OD##length##millimeters,"
            + "Port ID##length##millimeters,H##length##millimeters,Handle L##length##millimeters,"
            + "Body Barrel L##length##millimeters,Port L##length##millimeters,"
            + "Nut L##length##millimeters,Ring W##length##millimeters,"
            + "Collar OD##length##millimeters,Socket OD##length##millimeters,"
            + "Socket Lip OD##length##millimeters,Union Nut OD##length##millimeters,"
            + "Bonnet OD##length##millimeters,Stem OD##length##millimeters,"
            + "Handle W##length##millimeters,Handle T##length##millimeters");
        IReadOnlyList<ValveSizeDefinition> rows = request.SizeCatalog.Count > 0
            ? request.SizeCatalog
            :
            [
                new ValveSizeDefinition(
                    request.TypeName,
                    request.NominalDiameterMm,
                    request.BodyLengthMm,
                    request.BodyDiameterMm,
                    request.NominalDiameterMm,
                    request.BodyHeightMm,
                    request.HandleLengthMm)
            ];
        foreach (ValveSizeDefinition row in rows)
        {
            ValveDetailDimensions detail = CalculateValveDetails(row);
            builder.Append(row.NominalDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.NominalDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.BodyLengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.BodyDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.PortDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.BodyHeightMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.HandleLengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.BodyBarrelLengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.PortLengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.NutLengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.RingWidthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.CollarDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.SocketDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.SocketLipDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.NutDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.BonnetDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.StemDiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(detail.HandleWidthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(detail.HandleThicknessMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static double Mm(double value)
    {
#if REVIT2020
        return UnitUtils.ConvertToInternalUnits(value, DisplayUnitType.DUT_MILLIMETERS);
#else
        return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
#endif
    }

    private static void ApplyLength(
        FamilyManager manager,
        string aliases,
        double millimetres,
        string fieldName,
        ValveBuilderResult result)
    {
        FamilyParameter? parameter = FindParameter(manager, aliases);
        if (parameter is null)
        {
            result.Warnings.Add($"{fieldName}: no matching parameter ({DisplayAliases(aliases)}).");
            return;
        }
        if (!CanSet(parameter, fieldName, result)) return;

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
#if REVIT2020
                    manager.Set(parameter, UnitUtils.ConvertToInternalUnits(millimetres, DisplayUnitType.DUT_MILLIMETERS));
#else
                    manager.Set(parameter, UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters));
#endif
                    break;
                case StorageType.Integer:
                    manager.Set(parameter, (int)Math.Round(millimetres));
                    break;
                case StorageType.String:
                    manager.Set(parameter, millimetres.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    result.Warnings.Add($"{fieldName}: parameter '{parameter.Definition.Name}' is not numeric/text.");
                    return;
            }
            result.AppliedParameters.Add(
                $"{fieldName} → {parameter.Definition.Name} = {millimetres:0.###} mm");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{fieldName}: could not set '{parameter.Definition.Name}': {exception.Message}");
        }
    }

    private static void ApplyAngle(
        FamilyManager manager,
        string aliases,
        double degrees,
        ValveBuilderResult result)
    {
        const string fieldName = "Handle angle";
        FamilyParameter? parameter = FindParameter(manager, aliases);
        if (parameter is null)
        {
            result.Warnings.Add($"{fieldName}: no matching parameter ({DisplayAliases(aliases)}).");
            return;
        }
        if (!CanSet(parameter, fieldName, result)) return;

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    manager.Set(parameter, degrees * Math.PI / 180.0);
                    break;
                case StorageType.Integer:
                    manager.Set(parameter, (int)Math.Round(degrees));
                    break;
                case StorageType.String:
                    manager.Set(parameter, degrees.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    result.Warnings.Add($"{fieldName}: parameter '{parameter.Definition.Name}' is not numeric/text.");
                    return;
            }
            result.AppliedParameters.Add(
                $"{fieldName} → {parameter.Definition.Name} = {degrees:0.###}°");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{fieldName}: could not set '{parameter.Definition.Name}': {exception.Message}");
        }
    }

    private static void ApplyText(
        FamilyManager manager,
        string aliases,
        string value,
        string fieldName,
        ValveBuilderResult result)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        FamilyParameter? parameter = FindParameter(manager, aliases);
        if (parameter is null)
        {
            result.Warnings.Add($"{fieldName}: no matching parameter ({DisplayAliases(aliases)}).");
            return;
        }
        if (!CanSet(parameter, fieldName, result)) return;

        try
        {
            if (parameter.StorageType != StorageType.String)
            {
                result.Warnings.Add($"{fieldName}: parameter '{parameter.Definition.Name}' is not Text.");
                return;
            }
            manager.Set(parameter, value);
            result.AppliedParameters.Add($"{fieldName} → {parameter.Definition.Name} = {value}");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{fieldName}: could not set '{parameter.Definition.Name}': {exception.Message}");
        }
    }

    private static void ApplyMaterial(
        Document familyDocument,
        FamilyManager manager,
        string aliases,
        string materialName,
        ValveBuilderResult result)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return;
        const string fieldName = "Material";
        FamilyParameter? parameter = FindParameter(manager, aliases);
        if (parameter is null)
        {
            result.Warnings.Add($"{fieldName}: no matching parameter ({DisplayAliases(aliases)}).");
            return;
        }
        if (!CanSet(parameter, fieldName, result)) return;

        try
        {
            if (parameter.StorageType == StorageType.String)
            {
                manager.Set(parameter, materialName);
            }
            else if (parameter.StorageType == StorageType.ElementId)
            {
                Material? material = new FilteredElementCollector(familyDocument)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(item => item.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                ElementId materialId = material?.Id ?? Material.Create(familyDocument, materialName);
                manager.Set(parameter, materialId);
            }
            else
            {
                result.Warnings.Add($"{fieldName}: parameter '{parameter.Definition.Name}' is not Material/Text.");
                return;
            }
            result.AppliedParameters.Add($"{fieldName} → {parameter.Definition.Name} = {materialName}");
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"{fieldName}: could not set '{parameter.Definition.Name}': {exception.Message}");
        }
    }

    private static FamilyParameter? FindParameter(FamilyManager manager, string aliases)
    {
        List<string> names = SplitAliases(aliases);
        List<FamilyParameter> parameters = manager.Parameters.Cast<FamilyParameter>().ToList();
        foreach (string name in names)
        {
            FamilyParameter? exact = parameters.FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        foreach (string name in names)
        {
            string normalized = NormalizeName(name);
            FamilyParameter? match = parameters.FirstOrDefault(parameter =>
                NormalizeName(parameter.Definition.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private static bool CanSet(FamilyParameter parameter, string fieldName, ValveBuilderResult result)
    {
        if (parameter.IsReadOnly)
        {
            result.Warnings.Add($"{fieldName}: parameter '{parameter.Definition.Name}' is read-only.");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(parameter.Formula))
        {
            result.Warnings.Add(
                $"{fieldName}: parameter '{parameter.Definition.Name}' is driven by formula '{parameter.Formula}'.");
            return false;
        }
        return true;
    }

    private static int ApplyTypeToSelection(
        Document project,
        Family family,
        string typeName,
        IReadOnlyCollection<ElementId> selectedIds,
        ValveBuilderResult result)
    {
        FamilySymbol? symbol = family.GetFamilySymbolIds()
            .Select(project.GetElement)
            .OfType<FamilySymbol>()
            .FirstOrDefault(item => item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (symbol is null)
        {
            result.Warnings.Add($"Loaded family does not expose Type '{typeName}' in the project.");
            return 0;
        }

        int updated = 0;
        using Transaction transaction = new(project, "FamilyMEP - Apply valve type");
        transaction.Start();
        if (!symbol.IsActive)
        {
            symbol.Activate();
            project.Regenerate();
        }

        foreach (ElementId id in selectedIds)
        {
            if (project.GetElement(id) is not FamilyInstance instance) continue;
            if (!IsPipeAccessory(instance.Symbol?.Family)) continue;
            try
            {
                instance.Symbol = symbol;
                updated++;
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"Element {id.Value}: could not change Type: {exception.Message}");
            }
        }
        transaction.Commit();
        return updated;
    }

    private static string SaveGeneratedCopy(
        Document familyDocument,
        string familyName,
        string typeName,
        string requestedOutputPath)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.GeneratedValveFolder);
        string safeName = SanitizeFileName(familyName);
        string safeTypeName = SanitizeFileName(typeName);
        string outputPath = string.IsNullOrWhiteSpace(requestedOutputPath)
            ? Path.Combine(AppPaths.GeneratedValveFolder, $"{safeName}_{safeTypeName}.rfa")
            : Path.GetFullPath(requestedOutputPath);
        string? outputFolder = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputFolder)) Directory.CreateDirectory(outputFolder);
        bool outputIsCurrentDocument = !string.IsNullOrWhiteSpace(familyDocument.PathName)
            && PathsEqual(familyDocument.PathName, outputPath);
        bool outputIsAlreadyOpen = familyDocument.Application.Documents
            .Cast<Document>()
            .Any(document =>
                document != familyDocument
                && !string.IsNullOrWhiteSpace(document.PathName)
                && PathsEqual(document.PathName, outputPath));
        if (outputIsCurrentDocument || outputIsAlreadyOpen)
        {
            outputPath = Path.Combine(
                AppPaths.GeneratedValveFolder,
                $"{safeName}_{safeTypeName}_{DateTime.Now:yyyyMMdd_HHmmss}.rfa");
        }
        familyDocument.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = true });
        return outputPath;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return Path.GetFullPath(first)
                .Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPipeAccessory(Family? family)
    {
        Category? category = family?.FamilyCategory ?? family?.Category;
        if (category is null) return false;
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
        return category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory;
#else
        return category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory;
#endif
    }

    private static List<string> SplitAliases(string aliases) => aliases
        .Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim())
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string DisplayAliases(string aliases) => string.Join(", ", SplitAliases(aliases));

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private static bool TryParseDnTypeName(string typeName, out int diameter)
    {
        diameter = 0;
        string value = typeName.Trim();
        if (!value.StartsWith("DN", StringComparison.OrdinalIgnoreCase)) return false;
        string digits = new(value.Skip(2).TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out diameter);
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "Valve" : sanitized.Trim();
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

    private sealed class RollBackOriginMoveOnError : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(
            FailuresAccessor failuresAccessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
                else
                    hasError = true;
            }
            return hasError
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }
}
