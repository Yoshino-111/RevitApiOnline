using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

internal static class TroxRfdBuilderService
{
    private enum ConnectionKind
    {
        K,
        US,
        A,
        UO,
        UD,
        N
    }

    private sealed record SizeRow(
        int NominalSize,
        double BladeDiameter);

    private sealed record TypeDefinition(
        string Name,
        int NominalSize,
        bool IsRound,
        bool HasNozzle,
        ConnectionKind Connection,
        double FaceSize,
        double CollarDiameter,
        double BladeDiameter,
        double TotalHeight,
        double TransitionDiameter,
        double PlenumWidth,
        double PlenumDepth,
        double PlenumBodyHeight,
        double ConnectorCenter,
        double SpigotLength);

    private sealed record FamilyParameters(
        FamilyParameter Ns,
        FamilyParameter Face,
        FamilyParameter CollarD,
        FamilyParameter BladeD,
        FamilyParameter BladeR,
        FamilyParameter Height,
        FamilyParameter H1,
        FamilyParameter FaceT,
        FamilyParameter CollarId,
        FamilyParameter SpigotOd,
        FamilyParameter HubD,
        FamilyParameter BladeZ0,
        FamilyParameter BladeZ1,
        FamilyParameter TransitionD,
        FamilyParameter TransitionId,
        FamilyParameter PlenumW,
        FamilyParameter PlenumD,
        FamilyParameter PlenumH,
        FamilyParameter ConnZ,
        FamilyParameter SpigotC,
        FamilyParameter FaceZ0,
        FamilyParameter FaceZ1,
        FamilyParameter BoxZ0,
        FamilyParameter BoxZ1,
        FamilyParameter SpigotX0,
        FamilyParameter SpigotX1);

    private sealed record GeometryResult(
        int FormCount,
        GenericForm ConnectorHost,
        bool HorizontalConnector);

    private sealed record NestedCoreArtifact(
        ElementId FamilyId);

    private sealed record NestedBladeArtifact(
        ElementId FamilyId);

    // TROX RFD Product Data 03/2026, pages 21-27.
    private static readonly IReadOnlyList<SizeRow> Rows =
    [
        new(125, 120),
        new(160, 155),
        new(200, 195),
        new(250, 245),
        new(315, 310),
        new(400, 395)
    ];

    public static AirTerminalInspection Inspect()
    {
        IReadOnlyList<TypeDefinition> catalog = AllTypes();
        ValidateCatalog(catalog);
        return new AirTerminalInspection
        {
            CategoryName = "Air Terminals",
            ConnectorCount = 1,
            TypeNames = catalog.Select(type => type.Name).ToList()
        };
    }

    public static bool TryGetDimensions(
        string typeName,
        out double nominalSize,
        out double faceSize,
        out double collarDiameter,
        out double totalHeight)
    {
        TypeDefinition? type = AllTypes().FirstOrDefault(item =>
            item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        nominalSize = type?.NominalSize ?? 0;
        faceSize = type?.FaceSize ?? 0;
        collarDiameter = type?.CollarDiameter ?? 0;
        totalHeight = type?.TotalHeight ?? 0;
        return type is not null;
    }

    public static AirTerminalBuilderResult Execute(
        UIApplication uiApplication,
        AirTerminalBuilderRequest request)
    {
        IReadOnlyList<TypeDefinition> allTypes = AllTypes();
        ValidateCatalog(allTypes);
        TypeDefinition selected = allTypes.FirstOrDefault(item =>
            item.Name.Equals(request.PreviewTypeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"TROX RFD catalog Type '{request.PreviewTypeName}' is not supported.");
        IReadOnlyList<TypeDefinition> catalog = VariantTypes(selected);
        string template = AppPaths.GenericModel2020Template;
        if (!File.Exists(template))
            throw new FileNotFoundException(
                "The Metric Generic Model 2020 template was not found.",
                template);

        Document project = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException(
                "Open an RVT project before creating TROX RFD.");
        Document? familyDocument = null;
        string outputFolder = Path.GetDirectoryName(request.OutputPath)
            ?? throw new InvalidOperationException("The output folder is invalid.");
        string nestedWorkingFolder = Path.Combine(
            outputFolder,
            $".familymep-tmp-trox-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(nestedWorkingFolder);
            familyDocument = uiApplication.Application.NewFamilyDocument(template)
                ?? throw new InvalidOperationException(
                    "Revit could not create the TROX RFD Family document.");
            var result = new AirTerminalBuilderResult
            {
                FamilyName = Path.GetFileNameWithoutExtension(request.OutputPath),
                ActiveTypeName = $"NS{selected.NominalSize}",
                CategoryName = "Air Terminals",
                TypeCount = catalog.Count
            };
            string lookupCsv = WriteLookupCsv(request, selected, catalog);
            NestedCoreArtifact nestedCore =
                CreateNestedCoreFamily(
                    familyDocument,
                    template,
                    nestedWorkingFolder,
                    selected,
                    lookupCsv);

            using (Transaction transaction = new(
                       familyDocument,
                       "FamilyMEP - Build parametric TROX RFD"))
            {
                transaction.Start();
                SetMetricUnits(familyDocument);
                SetAirTerminalCategory(familyDocument);
                FamilyManager manager = familyDocument.FamilyManager;
                Dictionary<int, FamilyType> familyTypes =
                    CreateFamilyTypes(manager, catalog);
                FamilyParameters parameters = CreateParameters(manager);
                SeedCatalogValues(manager, familyTypes, parameters, catalog);
                ApplyDerivedFormulas(manager, parameters);
                string tableName = ImportLookupTable(
                    familyDocument,
                    lookupCsv);
                ApplyLookupFormulas(manager, parameters, tableName);
                manager.CurrentType = familyTypes[selected.NominalSize];
                familyDocument.Regenerate();

                ElementId materialId = EnsureNeutralGray(familyDocument);
                FamilyInstance nestedCoreInstance = PlaceNestedCore(
                    familyDocument,
                    manager,
                    nestedCore,
                    parameters);
                GeometryResult geometry;
                try
                {
                    geometry = BuildNativeGeometry(
                        familyDocument,
                        manager,
                        parameters,
                        selected,
                        materialId);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"TROX RFD '{selected.Name}' failed at native geometry: {exception.Message}",
                        exception);
                }
                familyDocument.Regenerate();
                ValidateControlledLengthParameters(
                    manager,
                    "parent TROX RFD",
                    parameters.Ns);
                try
                {
                    result.ConnectorCount = CreateDuctConnector(
                        familyDocument,
                        manager,
                        geometry,
                        parameters.CollarD);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"TROX RFD '{selected.Name}' failed at duct connector: {exception.Message}",
                        exception);
                }
                ValidateParentComposition(
                    familyDocument,
                    geometry.FormCount,
                    1,
                    result.ConnectorCount);
                try
                {
                    ValidateAllTypes(
                        familyDocument,
                        manager,
                        familyTypes,
                        selected.NominalSize,
                        nestedCoreInstance,
                        parameters.BladeD,
                        geometry,
                        parameters.CollarD,
                        parameters.ConnZ,
                        parameters.SpigotC);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"TROX RFD '{selected.Name}' failed while flexing NS types: {exception.Message}",
                        exception);
                }
                transaction.Commit();

                result.AppliedChanges.Add(
                    $"Embedded lookup table: {tableName} ({catalog.Count} rows)");
                result.AppliedChanges.Add(
                    "Six native Family Types: NS125, NS160, NS200, NS250, NS315, NS400");
                result.AppliedChanges.Add(
                    "One parametric blade Type arrayed as 16 nested instances; no Visibility switching");
                result.AppliedChanges.Add(
                    $"{geometry.FormCount} parent forms; nested NS drives its own embedded lookup table");
                result.AppliedChanges.Add(
                    "All variable dimensions use lookup formulas; fixed dimensions use locked formulas");
                result.AppliedChanges.Add(
                    "Open annular collar replaces the incorrect closed cylinder");
                result.AppliedChanges.Add(
                    "Bottom collar ØD4 -> NS; duct connector and spigot -> catalog ØD");
                result.AppliedChanges.Add(
                    "Origin -> X/Y centre; diffuser room face -> Ref. Level");
                result.AppliedChanges.Add(
                    "Material -> neutral gray; manufacturer metadata -> blank");
            }

            Directory.CreateDirectory(outputFolder);
            familyDocument.SaveAs(
                request.OutputPath,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true
                });
            result.OutputPath = request.OutputPath;
            if (request.LoadIntoProject)
                familyDocument.LoadFamily(
                    project,
                    new OverwriteFamilyLoadOptions());
            DeleteTemporaryNestedFolder(nestedWorkingFolder);
            return result;
        }
        finally
        {
            if (familyDocument is not null)
            {
                try { familyDocument.Close(false); }
                catch { }
            }
            DeleteTemporaryNestedFolder(nestedWorkingFolder);
        }
    }

    private static void ValidateCatalog(
        IReadOnlyList<TypeDefinition> catalog)
    {
        if (catalog.Count != 90)
            throw new InvalidOperationException(
                $"TROX RFD catalog integrity failed: expected 90 selections, found {catalog.Count}.");

        int[] expectedSizes = [125, 160, 200, 250, 315, 400];
        ILookup<string, TypeDefinition> series = catalog.ToLookup(item =>
        {
            int separator = item.Name.LastIndexOf('-');
            return separator > 0 ? item.Name[..separator] : item.Name;
        });
        if (series.Count != 15)
            throw new InvalidOperationException(
                $"TROX RFD catalog integrity failed: expected 15 series, found {series.Count}.");

        foreach (IGrouping<string, TypeDefinition> group in series)
        {
            int[] actualSizes = group
                .Select(item => item.NominalSize)
                .OrderBy(value => value)
                .ToArray();
            if (!actualSizes.SequenceEqual(expectedSizes))
                throw new InvalidOperationException(
                    $"TROX RFD series '{group.Key}' does not contain the six official nominal sizes.");

            foreach (TypeDefinition item in group)
            {
                if (item.FaceSize <= item.BladeDiameter
                    || item.CollarDiameter <= 0
                    || item.BladeDiameter <= 0
                    || item.TotalHeight <= 8)
                {
                    throw new InvalidOperationException(
                        $"TROX RFD catalog dimensions are invalid for '{item.Name}'.");
                }

                if (item.Connection is ConnectionKind.A or ConnectionKind.N)
                {
                    double boxBottom = item.TotalHeight - item.PlenumBodyHeight;
                    if (item.PlenumWidth <= 0
                        || item.PlenumDepth <= 0
                        || item.PlenumBodyHeight <= 0
                        || item.SpigotLength <= 0
                        || item.ConnectorCenter <= boxBottom
                        || item.ConnectorCenter >= item.TotalHeight)
                    {
                        throw new InvalidOperationException(
                            $"TROX RFD horizontal plenum dimensions are invalid for '{item.Name}'.");
                    }
                }
                else if (item.Connection is not ConnectionKind.K
                         && (item.TransitionDiameter <= item.CollarDiameter
                             || item.SpigotLength <= 0
                             || item.TotalHeight <= item.SpigotLength + 2))
                {
                    throw new InvalidOperationException(
                        $"TROX RFD vertical transition dimensions are invalid for '{item.Name}'.");
                }
            }
        }
    }

    private static IReadOnlyList<TypeDefinition> AllTypes()
    {
        var types = new List<TypeDefinition>();
        double[] dK = [123, 158, 198, 248, 313, 398];
        double[] dConnection = [98, 123, 158, 198, 248, 313];
        double[] d3 = [127, 162, 202, 252, 318, 403];
        double[] q3 = [216, 266, 266, 476, 567, 615];
        double[] h4 = [196, 221, 251, 296, 346, 411];
        double[] cAq = [48, 46, 48, 48, 46, 48];
        double[] cAr = [50, 48, 50, 50, 48, 50];

        AddSeries(types, "RFD-Q-K", false, false, ConnectionKind.K,
            [158, 198, 248, 298, 398, 498], dK,
            [41, 45, 45, 41, 43, 43]);
        AddSeries(types, "RFD-Q-D-K", false, true, ConnectionKind.K,
            [198, 248, 248, 298, 398, 498], dK,
            [66, 70, 68, 66, 78, 78]);
        AddSeries(types, "RFD-Q-US", false, false, ConnectionKind.US,
            [198, 198, 248, 298, 398, 498], dConnection,
            [120, 125, 128, 133, 140, 150],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-Q-D-US", false, true, ConnectionKind.US,
            [198, 248, 248, 298, 398, 498], dConnection,
            [145, 150, 153, 158, 175, 185],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-Q-A", false, false, ConnectionKind.A,
            [198, 198, 248, 298, 398, 498], dConnection,
            [248, 300, 304, 348, 395, 464],
            plenumW: q3, plenumD: q3, plenumH: h4,
            connZ: [168, 181, 193, 218, 252, 276], spigotC: cAq);
        AddSeries(types, "RFD-Q-D-A", false, true, ConnectionKind.A,
            [198, 248, 248, 298, 398, 498], dConnection,
            [275, 300, 330, 374, 444, 500],
            plenumW: q3, plenumD: q3, plenumH: h4,
            connZ: [194, 207, 219, 244, 289, 312], spigotC: cAq);

        AddSeries(types, "RFD-R-K", true, false, ConnectionKind.K,
            [158, 197, 241, 295, 364, 450], dK,
            [41, 43, 43, 41, 43, 43]);
        AddSeries(types, "RFD-R-D-K", true, true, ConnectionKind.K,
            [200, 250, 300, 350, 450, 580], dK,
            [74, 76, 76, 74, 85, 85]);
        AddSeries(types, "RFD-R-US", true, false, ConnectionKind.US,
            [158, 197, 241, 295, 364, 450], dConnection,
            [119, 124, 127, 132, 139, 149],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-R-D-US", true, true, ConnectionKind.US,
            [200, 250, 300, 350, 450, 580], dConnection,
            [152, 157, 160, 165, 182, 192],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-R-UO", true, false, ConnectionKind.UO,
            [158, 197, 241, 295, 364, 450], dConnection,
            [149, 154, 157, 162, 169, 179],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-R-D-UD", true, true, ConnectionKind.UD,
            [200, 250, 300, 350, 450, 580], dConnection,
            [189, 196, 197, 202, 219, 229],
            transition: d3, spigotC: Repeat(40));
        AddSeries(types, "RFD-R-A", true, false, ConnectionKind.A,
            [158, 197, 241, 295, 364, 450], dConnection,
            [249, 274, 304, 349, 408, 464],
            plenumW: q3, plenumD: q3, plenumH: h4,
            connZ: [168, 180, 193, 218, 253, 275], spigotC: cAr);
        AddSeries(types, "RFD-R-D-A", true, true, ConnectionKind.A,
            [200, 250, 300, 350, 450, 580], dConnection,
            [282, 307, 337, 382, 442, 507],
            plenumW: q3, plenumD: q3, plenumH: h4,
            connZ: [201, 213, 226, 251, 296, 318], spigotC: cAr);
        AddSeries(types, "RFD-R-D-N", true, true, ConnectionKind.N,
            [200, 250, 300, 350, 450, 580], dConnection,
            [152, 177, 212, 262, 312, 377],
            plenumW: [283, 335, 392, 435, 496, 728],
            plenumD: [264, 293, 373, 416, 476, 652],
            plenumH: [152, 177, 212, 262, 312, 377],
            connZ: [77, 89, 106, 131, 156, 189],
            spigotC: [48, 46, 48, 48, 56, 48]);
        return types;
    }

    private static void AddSeries(
        ICollection<TypeDefinition> target,
        string seriesName,
        bool isRound,
        bool hasNozzle,
        ConnectionKind connection,
        IReadOnlyList<double> face,
        IReadOnlyList<double> collarD,
        IReadOnlyList<double> height,
        IReadOnlyList<double>? transition = null,
        IReadOnlyList<double>? plenumW = null,
        IReadOnlyList<double>? plenumD = null,
        IReadOnlyList<double>? plenumH = null,
        IReadOnlyList<double>? connZ = null,
        IReadOnlyList<double>? spigotC = null)
    {
        for (int index = 0; index < Rows.Count; index++)
        {
            SizeRow row = Rows[index];
            target.Add(new TypeDefinition(
                $"{seriesName}-{row.NominalSize}",
                row.NominalSize,
                isRound,
                hasNozzle,
                connection,
                face[index],
                collarD[index],
                row.BladeDiameter,
                height[index],
                transition?[index] ?? 0,
                plenumW?[index] ?? 0,
                plenumD?[index] ?? 0,
                plenumH?[index] ?? 0,
                connZ?[index] ?? 0,
                spigotC?[index] ?? 0));
        }
    }

    private static double[] Repeat(double value) =>
        Enumerable.Repeat(value, Rows.Count).ToArray();

    private static IReadOnlyList<TypeDefinition> VariantTypes(
        TypeDefinition selected) =>
        AllTypes()
            .Where(row =>
                row.IsRound == selected.IsRound
                && row.HasNozzle == selected.HasNozzle
                && row.Connection == selected.Connection)
            .ToList();

    private static NestedCoreArtifact CreateNestedCoreFamily(
        Document parentDocument,
        string templatePath,
        string nestedWorkingFolder,
        TypeDefinition seed,
        string lookupCsv)
    {
        Directory.CreateDirectory(nestedWorkingFolder);
        Document? nestedDocument = null;
        try
        {
            nestedDocument = parentDocument.Application.NewFamilyDocument(
                    templatePath)
                ?? throw new InvalidOperationException(
                    "Could not create the parametric nested TROX core.");
            NestedBladeArtifact nestedBlade = CreateNestedBladeFamily(
                nestedDocument,
                templatePath,
                nestedWorkingFolder,
                seed,
                lookupCsv);
            using Transaction transaction = new(
                nestedDocument,
                "FamilyMEP - Parametric nested TROX core");
            transaction.Start();
            FailureHandlingOptions failureOptions =
                transaction.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(
                new SuppressIdenticalGeometryArrayWarning());
            transaction.SetFailureHandlingOptions(failureOptions);
            SetMetricUnits(nestedDocument);
            Category genericModel =
                nestedDocument.Settings.Categories.get_Item(
                    BuiltInCategory.OST_GenericModel);
            if (nestedDocument.OwnerFamily is not null)
                nestedDocument.OwnerFamily.FamilyCategory = genericModel;

            FamilyManager manager = nestedDocument.FamilyManager;
            if (manager.CurrentType is null)
                manager.NewType("Core");
            else
                manager.RenameCurrentType("Core");
            FamilyParameters parameters = CreateParameters(
                manager,
                instanceParameters: true);
            manager.Set(parameters.Ns, Mm(seed.NominalSize));
            manager.Set(parameters.BladeD, Mm(seed.BladeDiameter));
            ApplyDerivedFormulas(manager, parameters);
            string tableName = ImportLookupTable(
                nestedDocument,
                lookupCsv);
            ApplyLookupFormulas(manager, parameters, tableName);
            ValidateControlledLengthParameters(
                manager,
                "nested TROX core",
                parameters.Ns);
            nestedDocument.Regenerate();

            View plan = FindFamilyView(nestedDocument, XYZ.BasisZ);
            CreateNestedCoreReferenceSkeleton(
                nestedDocument,
                manager,
                plan,
                parameters);
            ElementId materialId = EnsureNeutralGray(nestedDocument);
            CreateNativeCircularExtrusion(
                nestedDocument,
                manager,
                plan,
                parameters.HubD,
                parameters.BladeZ0,
                parameters.BladeZ1,
                materialId);
            CreateNestedBladeArray(
                nestedDocument,
                manager,
                plan,
                nestedBlade,
                parameters.Ns);
            nestedDocument.Regenerate();
            transaction.Commit();

            string variant = seed.Name[..seed.Name.LastIndexOf('-')]
                .Replace('-', '_');
            string nestedPath = Path.Combine(
                nestedWorkingFolder,
                $"_TEMP_TROX_{variant}_ParametricCore.rfa");
            nestedDocument.SaveAs(
                nestedPath,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true,
                    MaximumBackups = 1
                });
            Family loadedFamily = nestedDocument.LoadFamily(
                parentDocument,
                new OverwriteFamilyLoadOptions());
            return new NestedCoreArtifact(loadedFamily.Id);
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

    private static NestedBladeArtifact CreateNestedBladeFamily(
        Document coreDocument,
        string templatePath,
        string nestedWorkingFolder,
        TypeDefinition seed,
        string lookupCsv)
    {
        Document? bladeDocument = null;
        try
        {
            bladeDocument = coreDocument.Application.NewFamilyDocument(
                    templatePath)
                ?? throw new InvalidOperationException(
                    "Could not create the parametric TROX blade family.");
            using Transaction transaction = new(
                bladeDocument,
                "FamilyMEP - Parametric TROX blade");
            transaction.Start();
            SetMetricUnits(bladeDocument);
            Category genericModel =
                bladeDocument.Settings.Categories.get_Item(
                    BuiltInCategory.OST_GenericModel);
            if (bladeDocument.OwnerFamily is not null)
                bladeDocument.OwnerFamily.FamilyCategory = genericModel;

            FamilyManager manager = bladeDocument.FamilyManager;
            if (manager.CurrentType is null)
                manager.NewType("Blade");
            else
                manager.RenameCurrentType("Blade");
            FamilyParameters parameters = CreateParameters(
                manager,
                instanceParameters: true);
            manager.Set(parameters.Ns, Mm(seed.NominalSize));
            manager.Set(parameters.BladeD, Mm(seed.BladeDiameter));
            ApplyDerivedFormulas(manager, parameters);
            string tableName = ImportLookupTable(
                bladeDocument,
                lookupCsv);
            ApplyLookupFormulas(manager, parameters, tableName);
            ValidateControlledLengthParameters(
                manager,
                "nested TROX blade",
                parameters.Ns);
            bladeDocument.Regenerate();

            View plan = FindFamilyView(bladeDocument, XYZ.BasisZ);
            ElementId materialId = EnsureNeutralGray(bladeDocument);
            CreateNativeBladeSector(
                bladeDocument,
                manager,
                plan,
                parameters,
                materialId,
                Math.PI / 20,
                constrainRadius: true);
            bladeDocument.Regenerate();
            transaction.Commit();

            string variant = seed.Name[..seed.Name.LastIndexOf('-')]
                .Replace('-', '_');
            string bladePath = Path.Combine(
                nestedWorkingFolder,
                $"_TEMP_TROX_{variant}_Blade.rfa");
            bladeDocument.SaveAs(
                bladePath,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true,
                    MaximumBackups = 1
                });
            Family loadedFamily = bladeDocument.LoadFamily(
                coreDocument,
                new OverwriteFamilyLoadOptions());
            return new NestedBladeArtifact(loadedFamily.Id);
        }
        finally
        {
            if (bladeDocument is not null)
            {
                try { bladeDocument.Close(false); }
                catch { }
            }
        }
    }

    private static void CreateNestedBladeArray(
        Document document,
        FamilyManager manager,
        View plan,
        NestedBladeArtifact artifact,
        FamilyParameter coreNominalSize)
    {
        const int bladeCount = 16;
        Family family = document.GetElement(artifact.FamilyId) as Family
            ?? throw new InvalidOperationException(
                "The parametric TROX blade was not loaded into the core.");
        FamilySymbol symbol = family.GetFamilySymbolIds()
            .Select(document.GetElement)
            .OfType<FamilySymbol>()
            .Single();
        if (!symbol.IsActive)
            symbol.Activate();
        document.Regenerate();

        FamilyInstance seed = document.FamilyCreate.NewFamilyInstance(
            XYZ.Zero,
            symbol,
            StructuralType.NonStructural);
        Parameter nominalSize = seed.LookupParameter(
                "FT_LE_ZZ_NS")
            ?? throw new InvalidOperationException(
                "The nested TROX blade does not expose FT_LE_ZZ_NS.");
        if (!manager.CanElementParameterBeAssociated(nominalSize))
            throw new InvalidOperationException(
                "Nested blade FT_LE_ZZ_NS cannot be associated to the core selector.");
        manager.AssociateElementParameterToFamilyParameter(
            nominalSize,
            coreNominalSize);
        document.Regenerate();

        RadialArray bladeArray = RadialArray.Create(
            document,
            plan,
            seed.Id,
            bladeCount,
            Line.CreateBound(XYZ.Zero, XYZ.BasisZ),
            Math.PI * 2 / bladeCount,
            ArrayAnchorMember.Second);
        if (bladeArray.NumMembers != bladeCount)
            throw new InvalidOperationException(
                $"The TROX radial blade array contains " +
                $"{bladeArray.NumMembers} members instead of {bladeCount}.");
    }

    private static void CreateNestedCoreReferenceSkeleton(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameters parameters)
    {
        double radius = CurrentValue(
            manager,
            parameters.BladeR,
            Mm(60));
        double extent = radius * 1.35;
        ReferencePlane? centerLeftRight =
            FindCenterReferencePlane(document, XYZ.BasisX);
        ReferencePlane? centerFrontBack =
            FindCenterReferencePlane(document, XYZ.BasisY);
        if (centerLeftRight is null || centerFrontBack is null)
            throw new InvalidOperationException(
                "Nested core origin reference planes were not found.");

        ReferencePlane left = CreatePlanReferencePlane(
            document, plan, -radius, true, extent, "RP_Core_Left");
        ReferencePlane right = CreatePlanReferencePlane(
            document, plan, radius, true, extent, "RP_Core_Right");
        ReferencePlane bottom = CreatePlanReferencePlane(
            document, plan, -radius, false, extent, "RP_Core_Bottom");
        ReferencePlane top = CreatePlanReferencePlane(
            document, plan, radius, false, extent, "RP_Core_Top");
        document.Regenerate();

        Dimension xEq = CreateLinearDimension(
            document,
            plan,
            [
                left.GetReference(),
                centerLeftRight.GetReference(),
                right.GetReference()
            ],
            new XYZ(-radius, extent * 1.10, 0),
            new XYZ(radius, extent * 1.10, 0));
        xEq.AreSegmentsEqual = true;
        Dimension xSize = CreateLinearDimension(
            document,
            plan,
            [left.GetReference(), right.GetReference()],
            new XYZ(-radius, extent * 1.28, 0),
            new XYZ(radius, extent * 1.28, 0));
        xSize.FamilyLabel = parameters.BladeD;

        Dimension yEq = CreateLinearDimension(
            document,
            plan,
            [
                bottom.GetReference(),
                centerFrontBack.GetReference(),
                top.GetReference()
            ],
            new XYZ(extent * 1.10, -radius, 0),
            new XYZ(extent * 1.10, radius, 0));
        yEq.AreSegmentsEqual = true;
    }

    private static ReferencePlane CreatePlanReferencePlane(
        Document document,
        View plan,
        double offset,
        bool vertical,
        double extent,
        string name)
    {
        XYZ start = vertical
            ? new XYZ(offset, -extent, 0)
            : new XYZ(-extent, offset, 0);
        XYZ end = vertical
            ? new XYZ(offset, extent, 0)
            : new XYZ(extent, offset, 0);
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            start,
            end,
            XYZ.BasisZ,
            plan);
        plane.Name = name;
        return plane;
    }

    private static FamilyInstance PlaceNestedCore(
        Document document,
        FamilyManager manager,
        NestedCoreArtifact artifact,
        FamilyParameters parentParameters)
    {
        View plan = FindFamilyView(document, XYZ.BasisZ);
        ReferencePlane? parentLeftRight =
            FindCenterReferencePlane(document, XYZ.BasisX);
        ReferencePlane? parentFrontBack =
            FindCenterReferencePlane(document, XYZ.BasisY);
        if (parentLeftRight is null || parentFrontBack is null)
            throw new InvalidOperationException(
                "Parent origin reference planes were not found.");

        Family family = document.GetElement(artifact.FamilyId) as Family
            ?? throw new InvalidOperationException(
                "The parametric nested TROX core was not loaded.");
        FamilySymbol symbol = family.GetFamilySymbolIds()
            .Select(document.GetElement)
            .OfType<FamilySymbol>()
            .Single();
        if (!symbol.IsActive)
            symbol.Activate();
        document.Regenerate();
        FamilyInstance instance = document.FamilyCreate.NewFamilyInstance(
            XYZ.Zero,
            symbol,
            StructuralType.NonStructural);
        document.Regenerate();

        AlignNestedCore(
            document,
            plan,
            parentLeftRight,
            parentFrontBack,
            instance);

        Parameter nominalSize = instance.LookupParameter(
                "FT_LE_ZZ_NS")
            ?? throw new InvalidOperationException(
                "The parametric nested core does not expose FT_LE_ZZ_NS.");
        if (!manager.CanElementParameterBeAssociated(nominalSize))
            throw new InvalidOperationException(
                "Nested core FT_LE_ZZ_NS cannot be associated to the parent selector.");
        manager.AssociateElementParameterToFamilyParameter(
            nominalSize,
            parentParameters.Ns);
        document.Regenerate();
        return instance;
    }

    private static void AlignNestedCore(
        Document document,
        View plan,
        ReferencePlane parentLeftRight,
        ReferencePlane parentFrontBack,
        FamilyInstance instance)
    {
        Reference? nestedLeftRight = instance.GetReferences(
                FamilyInstanceReferenceType.CenterLeftRight)
            .FirstOrDefault();
        Reference? nestedFrontBack = instance.GetReferences(
                FamilyInstanceReferenceType.CenterFrontBack)
            .FirstOrDefault();
        if (nestedLeftRight is not null)
            document.FamilyCreate.NewAlignment(
                plan,
                parentLeftRight.GetReference(),
                nestedLeftRight);
        if (nestedFrontBack is not null)
            document.FamilyCreate.NewAlignment(
                plan,
                parentFrontBack.GetReference(),
                nestedFrontBack);
    }

    private static Dictionary<int, FamilyType> CreateFamilyTypes(
        FamilyManager manager,
        IReadOnlyList<TypeDefinition> catalog)
    {
        var result = new Dictionary<int, FamilyType>();
        FamilyType first;
        if (manager.CurrentType is null)
            first = manager.NewType($"NS{catalog[0].NominalSize}");
        else
        {
            manager.RenameCurrentType($"NS{catalog[0].NominalSize}");
            first = manager.CurrentType;
        }
        result[catalog[0].NominalSize] = first;
        foreach (TypeDefinition type in catalog.Skip(1))
            result[type.NominalSize] = manager.NewType($"NS{type.NominalSize}");
        return result;
    }

    private static FamilyParameters CreateParameters(
        FamilyManager manager,
        bool instanceParameters = false)
    {
        FamilyParameter P(string name) =>
            EnsureLengthParameter(manager, name, instanceParameters);
        FamilyParameter ns = P("FT_LE_ZZ_NS");
        FamilyParameter face = P("FT_LE_ZZ_Face");
        FamilyParameter collarD = P("FT_LE_ZZ_CollarD");
        FamilyParameter bladeD = P("FT_LE_ZZ_BladeD");
        FamilyParameter bladeR = P("FT_LE_ZZ_BladeR");
        FamilyParameter height = P("FT_LE_ZZ_Height");
        FamilyParameter h1 = P("FT_LE_ZZ_H1");
        FamilyParameter faceT = P("FT_LE_ZZ_FaceT");
        FamilyParameter collarId = P("FT_LE_ZZ_CollarID");
        FamilyParameter spigotOd = P("FT_LE_ZZ_SpigotOD");
        FamilyParameter hubD = P("FT_LE_ZZ_HubD");
        FamilyParameter bladeZ0 = P("FT_LE_ZZ_BladeZ0");
        FamilyParameter bladeZ1 = P("FT_LE_ZZ_BladeZ1");
        FamilyParameter transitionD = P("FT_LE_ZZ_TransD");
        FamilyParameter transitionId = P("FT_LE_ZZ_TransID");
        FamilyParameter plenumW = P("FT_LE_ZZ_BoxW");
        FamilyParameter plenumD = P("FT_LE_ZZ_BoxD");
        FamilyParameter plenumH = P("FT_LE_ZZ_BoxH");
        FamilyParameter connZ = P("FT_LE_ZZ_ConnZ");
        FamilyParameter spigotC = P("FT_LE_ZZ_SpigotL");
        FamilyParameter faceZ0 = P("FT_LE_ZZ_FaceZ0");
        FamilyParameter faceZ1 = P("FT_LE_ZZ_FaceZ1");
        FamilyParameter boxZ0 = P("FT_LE_ZZ_BoxZ0");
        FamilyParameter boxZ1 = P("FT_LE_ZZ_BoxZ1");
        FamilyParameter spigotX0 = P("FT_LE_ZZ_SpigotX0");
        FamilyParameter spigotX1 = P("FT_LE_ZZ_SpigotX1");

        return new FamilyParameters(
            ns,
            face,
            collarD,
            bladeD,
            bladeR,
            height,
            h1,
            faceT,
            collarId,
            spigotOd,
            hubD,
            bladeZ0,
            bladeZ1,
            transitionD,
            transitionId,
            plenumW,
            plenumD,
            plenumH,
            connZ,
            spigotC,
            faceZ0,
            faceZ1,
            boxZ0,
            boxZ1,
            spigotX0,
            spigotX1);
    }

    private static void ApplyDerivedFormulas(
        FamilyManager manager,
        FamilyParameters parameters)
    {
        manager.SetFormula(parameters.FaceT, "2 mm");
        manager.SetFormula(parameters.H1, "8 mm");
        manager.SetFormula(
            parameters.BladeR,
            "FT_LE_ZZ_BladeD / 2");
        manager.SetFormula(
            parameters.CollarId,
            "FT_LE_ZZ_CollarD - 3 mm");
        manager.SetFormula(
            parameters.SpigotOd,
            "FT_LE_ZZ_CollarD + 3 mm");
        manager.SetFormula(
            parameters.HubD,
            "FT_LE_ZZ_BladeD * 0.16");
        manager.SetFormula(
            parameters.BladeZ0,
            "FT_LE_ZZ_FaceZ0 + FT_LE_ZZ_FaceT");
        manager.SetFormula(
            parameters.BladeZ1,
            "FT_LE_ZZ_BladeZ0 + 6 mm");
        manager.SetFormula(
            parameters.TransitionId,
            "if(FT_LE_ZZ_TransD > 4 mm, FT_LE_ZZ_TransD - 3 mm, 1 mm)");
        manager.SetFormula(
            parameters.FaceZ0,
            "0 mm");
        manager.SetFormula(
            parameters.FaceZ1,
            "FT_LE_ZZ_FaceZ0 + FT_LE_ZZ_FaceT");
        manager.SetFormula(
            parameters.BoxZ0,
            "FT_LE_ZZ_Height - FT_LE_ZZ_BoxH");
        manager.SetFormula(
            parameters.BoxZ1,
            "FT_LE_ZZ_Height");
        manager.SetFormula(
            parameters.SpigotX0,
            "FT_LE_ZZ_BoxW / 2");
        manager.SetFormula(
            parameters.SpigotX1,
            "FT_LE_ZZ_SpigotX0 + FT_LE_ZZ_SpigotL");
    }

    private static void SeedCatalogValues(
        FamilyManager manager,
        IReadOnlyDictionary<int, FamilyType> types,
        FamilyParameters parameters,
        IReadOnlyList<TypeDefinition> catalog)
    {
        foreach (TypeDefinition row in catalog)
        {
            manager.CurrentType = types[row.NominalSize];
            manager.Set(parameters.Ns, Mm(row.NominalSize));
            manager.Set(parameters.Face, Mm(row.FaceSize));
            manager.Set(parameters.CollarD, Mm(row.CollarDiameter));
            manager.Set(parameters.BladeD, Mm(row.BladeDiameter));
            manager.Set(parameters.Height, Mm(row.TotalHeight));
            manager.Set(parameters.TransitionD, Mm(Math.Max(row.TransitionDiameter, 1)));
            manager.Set(parameters.PlenumW, Mm(Math.Max(row.PlenumWidth, 1)));
            manager.Set(parameters.PlenumD, Mm(Math.Max(row.PlenumDepth, 1)));
            manager.Set(parameters.PlenumH, Mm(Math.Max(row.PlenumBodyHeight, 1)));
            manager.Set(parameters.ConnZ, Mm(Math.Max(row.ConnectorCenter, 0)));
            manager.Set(parameters.SpigotC, Mm(Math.Max(row.SpigotLength, 1)));
        }
    }

    private static string WriteLookupCsv(
        AirTerminalBuilderRequest request,
        TypeDefinition selected,
        IReadOnlyList<TypeDefinition> catalog)
    {
        string folder = Path.GetDirectoryName(request.OutputPath)
            ?? AppPaths.GeneratedValveFolder;
        Directory.CreateDirectory(folder);
        string variant = selected.Name[..selected.Name.LastIndexOf('-')];
        string safeVariant = new string(variant
            .Select(character =>
                Path.GetInvalidFileNameChars().Contains(character)
                    ? '_'
                    : character)
            .ToArray());
        string path = Path.Combine(
            folder,
            $"AirTerminal_TROX_{safeVariant}_LTN.csv");
        var builder = new StringBuilder();
        builder.AppendLine(
            ",NS##length##millimeters,Face##length##millimeters,"
            + "CollarD##length##millimeters,BladeD##length##millimeters,"
            + "Height##length##millimeters,TransD##length##millimeters,"
            + "BoxW##length##millimeters,BoxD##length##millimeters,"
            + "BoxH##length##millimeters,ConnZ##length##millimeters,"
            + "SpigotL##length##millimeters");
        foreach (TypeDefinition row in catalog)
        {
            builder.Append(row.NominalSize.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.NominalSize.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.FaceSize.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.CollarDiameter.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.BladeDiameter.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.TotalHeight.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(Math.Max(row.TransitionDiameter, 1).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(Math.Max(row.PlenumWidth, 1).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(Math.Max(row.PlenumDepth, 1).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(Math.Max(row.PlenumBodyHeight, 1).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(Math.Max(row.ConnectorCenter, 0).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(Math.Max(row.SpigotLength, 1).ToString("0.###", CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string ImportLookupTable(
        Document document,
        string csvPath)
    {
        FamilySizeTableManager? tableManager =
            FamilySizeTableManager.GetFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
        if (tableManager is null || !tableManager.IsValidObject)
        {
            FamilySizeTableManager.CreateFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
            tableManager = FamilySizeTableManager.GetFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
        }
        if (tableManager is null || !tableManager.IsValidObject)
            throw new InvalidOperationException(
                "Revit could not create the Family lookup-table manager.");

        var errorInfo = new FamilySizeTableErrorInfo();
        if (!tableManager.ImportSizeTable(document, csvPath, errorInfo))
        {
            throw new InvalidOperationException(
                "TROX RFD lookup-table import failed. "
                + $"Error: {errorInfo.FamilySizeTableErrorType}; "
                + $"row {errorInfo.InvalidRowIndex}, column {errorInfo.InvalidColumnIndex}, "
                + $"header '{errorInfo.InvalidHeaderText}'.");
        }
        return Path.GetFileNameWithoutExtension(csvPath);
    }

    private static void ApplyLookupFormulas(
        FamilyManager manager,
        FamilyParameters parameters,
        string tableName)
    {
        manager.SetFormula(
            parameters.Face,
            LookupFormula(tableName, "Face"));
        manager.SetFormula(
            parameters.CollarD,
            LookupFormula(tableName, "CollarD"));
        manager.SetFormula(
            parameters.BladeD,
            LookupFormula(tableName, "BladeD"));
        manager.SetFormula(
            parameters.Height,
            LookupFormula(tableName, "Height"));
        manager.SetFormula(
            parameters.TransitionD,
            LookupFormula(tableName, "TransD"));
        manager.SetFormula(
            parameters.PlenumW,
            LookupFormula(tableName, "BoxW"));
        manager.SetFormula(
            parameters.PlenumD,
            LookupFormula(tableName, "BoxD"));
        manager.SetFormula(
            parameters.PlenumH,
            LookupFormula(tableName, "BoxH"));
        manager.SetFormula(
            parameters.ConnZ,
            LookupFormula(tableName, "ConnZ"));
        manager.SetFormula(
            parameters.SpigotC,
            LookupFormula(tableName, "SpigotL"));
    }

    private static string LookupFormula(
        string tableName,
        string column) =>
        $"size_lookup(\"{tableName}\", \"{column}\", 1 mm, FT_LE_ZZ_NS)";

    private static GeometryResult BuildNativeGeometry(
        Document document,
        FamilyManager manager,
        FamilyParameters parameters,
        TypeDefinition selected,
        ElementId materialId)
    {
        View plan = FindFamilyView(document, XYZ.BasisZ);
        int formCount = 0;
        Extrusion face = selected.IsRound
            ? CreateNativeAnnularExtrusion(
                document,
                manager,
                plan,
                parameters.Face,
                parameters.BladeD,
                parameters.FaceZ0,
                parameters.FaceZ1,
                materialId)
            : CreateNativeSquarePlate(
                document,
                manager,
                plan,
                parameters.Face,
                parameters.BladeD,
                parameters.FaceZ0,
                parameters.FaceZ1,
                materialId);
        formCount++;

        if (selected.HasNozzle)
        {
            FamilyParameter nozzleD = FixedLength(
                manager,
                "FT_LE_ZZ_NozzleD",
                "FT_LE_ZZ_BladeD + 20 mm");
            FamilyParameter nozzleTop = FixedLength(
                manager,
                "FT_LE_ZZ_NozzleTop",
                "FT_LE_ZZ_FaceZ0 + FT_LE_ZZ_H1");
            CreateNativeAnnularExtrusion(
                document,
                manager,
                plan,
                nozzleD,
                parameters.BladeD,
                parameters.FaceZ0,
                nozzleTop,
                materialId);
            formCount++;
        }

        SetSubcategory(face, document, "RFD Face");

        if (selected.Connection is ConnectionKind.A or ConnectionKind.N)
        {
            Extrusion box = GeometryStage(
                "plenum box",
                () => CreateNativeBoxExtrusion(
                    document,
                    manager,
                    plan,
                    parameters.PlenumW,
                    parameters.PlenumD,
                    parameters.BoxZ0,
                    parameters.BoxZ1,
                    materialId));
            formCount++;
            SetSubcategory(box, document, "RFD Plenum");

            if (selected.Connection == ConnectionKind.A)
            {
                FamilyParameter throatD = FixedLength(
                    manager,
                    "FT_LE_ZZ_ThroatD",
                    "FT_LE_ZZ_NS");
                FamilyParameter throatId = FixedLength(
                    manager,
                    "FT_LE_ZZ_ThroatID",
                    "FT_LE_ZZ_NS - 3 mm");
                GeometryStage(
                    "plenum throat",
                    () => CreateNativeAnnularExtrusion(
                        document,
                        manager,
                        plan,
                        throatD,
                        throatId,
                        parameters.FaceZ1,
                        parameters.BoxZ0,
                        materialId));
                formCount++;
            }

            Revolution sideSpigot = GeometryStage(
                "horizontal spigot",
                () => CreateNativeHorizontalAnnularRevolution(
                    document,
                    manager,
                    parameters.SpigotOd,
                    parameters.CollarId,
                    parameters.SpigotX0,
                    parameters.SpigotX1,
                    parameters.ConnZ,
                    materialId));
            SetSubcategory(sideSpigot, document, "RFD Collar");
            formCount++;
            return new GeometryResult(formCount, sideSpigot, true);
        }

        GenericForm connectorHost;
        if (selected.Connection == ConnectionKind.K)
        {
            connectorHost = CreateNativeAnnularExtrusion(
                document,
                manager,
                plan,
                parameters.SpigotOd,
                parameters.CollarId,
                parameters.FaceZ1,
                parameters.Height,
                materialId);
            formCount++;
        }
        else
        {
            FamilyParameter transitionTop = FixedLength(
                manager,
                "FT_LE_ZZ_TransTop",
                "FT_LE_ZZ_Height - FT_LE_ZZ_SpigotL");
            CreateNativeAnnularExtrusion(
                document,
                manager,
                plan,
                parameters.TransitionD,
                parameters.TransitionId,
                parameters.FaceZ1,
                transitionTop,
                materialId);
            formCount++;
            connectorHost = CreateNativeAnnularExtrusion(
                document,
                manager,
                plan,
                parameters.SpigotOd,
                parameters.CollarId,
                transitionTop,
                parameters.Height,
                materialId);
            formCount++;
        }
        SetSubcategory(connectorHost, document, "RFD Collar");
        return new GeometryResult(formCount, connectorHost, false);
    }

    private static T GeometryStage<T>(
        string stage,
        Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{stage}: {exception.Message}",
                exception);
        }
    }

    private static Extrusion CreateNativeBoxExtrusion(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameter width,
        FamilyParameter depth,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double currentWidth = CurrentValue(manager, width, Mm(216));
        double currentDepth = CurrentValue(manager, depth, Mm(216));
        double halfX = currentWidth / 2;
        double halfY = currentDepth / 2;
        XYZ[] points =
        [
            new(-halfX, -halfY, 0),
            new(halfX, -halfY, 0),
            new(halfX, halfY, 0),
            new(-halfX, halfY, 0)
        ];
        var outline = new CurveArray();
        for (int index = 0; index < points.Length; index++)
            outline.Append(Line.CreateBound(
                points[index],
                points[(index + 1) % points.Length]));
        var profile = new CurveArrArray();
        profile.Append(outline);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Line> lines = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Line>()
            .ToList();
        List<Line> vertical = lines
            .Where(line => Math.Abs(line.Direction.Y) > .99)
            .OrderBy(line => line.Evaluate(.5, true).X)
            .ToList();
        List<Line> horizontal = lines
            .Where(line => Math.Abs(line.Direction.X) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Y)
            .ToList();
        Dimension widthDimension = CreateLinearDimension(
            document,
            plan,
            [vertical.First().Reference, vertical.Last().Reference],
            new XYZ(-halfX, halfY * 1.12, 0),
            new XYZ(halfX, halfY * 1.12, 0));
        widthDimension.FamilyLabel = width;
        Dimension depthDimension = CreateLinearDimension(
            document,
            plan,
            [horizontal.First().Reference, horizontal.Last().Reference],
            new XYZ(halfX * 1.12, -halfY, 0),
            new XYZ(halfX * 1.12, halfY, 0));
        depthDimension.FamilyLabel = depth;
        EqualizeAboutCenter(
            document,
            plan,
            vertical.First().Reference,
            vertical.Last().Reference,
            XYZ.BasisX,
            halfX);
        EqualizeAboutCenter(
            document,
            plan,
            horizontal.First().Reference,
            horizontal.Last().Reference,
            XYZ.BasisY,
            halfY);
        return extrusion;
    }

    private static Revolution CreateNativeHorizontalAnnularRevolution(
        Document document,
        FamilyManager manager,
        FamilyParameter outerDiameter,
        FamilyParameter innerDiameter,
        FamilyParameter start,
        FamilyParameter end,
        FamilyParameter centerHeight,
        ElementId materialId)
    {
        double outerD = CurrentValue(manager, outerDiameter, Mm(101));
        double innerD = CurrentValue(manager, innerDiameter, Mm(95));
        double centerZ = CurrentValue(manager, centerHeight, Mm(194));
        double startX = CurrentValue(manager, start, Mm(108));
        double endX = CurrentValue(manager, end, Mm(156));
        double outerR = Math.Max(outerD / 2, Mm(2));
        double innerR = Math.Clamp(innerD / 2, Mm(.5), outerR - Mm(.5));
        if (endX <= startX + Mm(.5))
            endX = startX + Mm(48);

        FamilyParameter outerRadius = FixedLength(
            manager,
            "FT_LE_ZZ_SpigotOR",
            "FT_LE_ZZ_SpigotOD / 2");
        FamilyParameter innerRadius = FixedLength(
            manager,
            "FT_LE_ZZ_SpigotIR",
            "FT_LE_ZZ_CollarID / 2");

        View elevation = FindFamilyView(document, XYZ.BasisY);
        double horizontalExtent = Math.Max(endX * 1.25, Mm(240));
        double verticalExtent = Math.Max(
            centerZ + outerR * 1.6,
            Mm(300));

        ReferencePlane originX = FindCenterReferencePlane(
                document,
                XYZ.BasisX)
            ?? throw new InvalidOperationException(
                "The Center (Left/Right) reference plane was not found.");
        ReferencePlane originZ = FindCenterReferencePlane(
                document,
                XYZ.BasisZ)
            ?? throw new InvalidOperationException(
                "The Ref. Level reference plane was not found.");
        ReferencePlane center = CreateFrontHorizontalReferencePlane(
            document,
            elevation,
            centerZ,
            horizontalExtent,
            "RP_Spigot_Center");
        ReferencePlane innerTop = CreateFrontHorizontalReferencePlane(
            document,
            elevation,
            centerZ + innerR,
            horizontalExtent,
            "RP_Spigot_ID_Top");
        ReferencePlane outerTop = CreateFrontHorizontalReferencePlane(
            document,
            elevation,
            centerZ + outerR,
            horizontalExtent,
            "RP_Spigot_OD_Top");
        ReferencePlane startPlane = CreateFrontVerticalReferencePlane(
            document,
            elevation,
            startX,
            verticalExtent,
            "RP_Spigot_Start");
        ReferencePlane endPlane = CreateFrontVerticalReferencePlane(
            document,
            elevation,
            endX,
            verticalExtent,
            "RP_Spigot_End");
        document.Regenerate();

        Dimension centerDimension = CreateLinearDimension(
            document,
            elevation,
            [originZ.GetReference(), center.GetReference()],
            new XYZ(-outerR * 1.35, 0, 0),
            new XYZ(-outerR * 1.35, 0, centerZ));
        centerDimension.FamilyLabel = centerHeight;

        Dimension innerRadiusDimension = CreateLinearDimension(
            document,
            elevation,
            [center.GetReference(), innerTop.GetReference()],
            new XYZ(endX + outerR * .45, 0, centerZ),
            new XYZ(endX + outerR * .45, 0, centerZ + innerR));
        innerRadiusDimension.FamilyLabel = innerRadius;

        Dimension outerRadiusDimension = CreateLinearDimension(
            document,
            elevation,
            [center.GetReference(), outerTop.GetReference()],
            new XYZ(endX + outerR * .75, 0, centerZ),
            new XYZ(endX + outerR * .75, 0, centerZ + outerR));
        outerRadiusDimension.FamilyLabel = outerRadius;

        Dimension startDimension = CreateLinearDimension(
            document,
            elevation,
            [originX.GetReference(), startPlane.GetReference()],
            new XYZ(0, 0, centerZ + outerR * 1.30),
            new XYZ(startX, 0, centerZ + outerR * 1.30));
        startDimension.FamilyLabel = start;

        Dimension endDimension = CreateLinearDimension(
            document,
            elevation,
            [originX.GetReference(), endPlane.GetReference()],
            new XYZ(0, 0, centerZ + outerR * 1.55),
            new XYZ(endX, 0, centerZ + outerR * 1.55));
        endDimension.FamilyLabel = end;

        var outline = new CurveArray();
        XYZ innerStart = new(startX, 0, centerZ + innerR);
        XYZ innerEnd = new(endX, 0, centerZ + innerR);
        XYZ outerEnd = new(endX, 0, centerZ + outerR);
        XYZ outerStart = new(startX, 0, centerZ + outerR);
        outline.Append(Line.CreateBound(innerStart, innerEnd));
        outline.Append(Line.CreateBound(innerEnd, outerEnd));
        outline.Append(Line.CreateBound(outerEnd, outerStart));
        outline.Append(Line.CreateBound(outerStart, innerStart));
        var profile = new CurveArrArray();
        profile.Append(outline);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero));
        Line axis = Line.CreateBound(
            new XYZ(startX, 0, centerZ),
            new XYZ(endX, 0, centerZ));
        Revolution revolution = document.FamilyCreate.NewRevolution(
            true,
            profile,
            sketch,
            axis,
            0,
            Math.PI * 2);
        SetFormMaterial(revolution, materialId);
        document.Regenerate();

        List<Line> profileLines = revolution.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        List<Line> verticalLines = profileLines
            .Where(line => Math.Abs(line.Direction.Z) > .99)
            .OrderBy(line => line.Evaluate(.5, true).X)
            .ToList();
        List<Line> horizontalLines = profileLines
            .Where(line => Math.Abs(line.Direction.X) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Z)
            .ToList();
        if (verticalLines.Count != 2 || horizontalLines.Count != 2)
            throw new InvalidOperationException(
                "The horizontal spigot revolution profile is incomplete.");

        document.FamilyCreate.NewAlignment(
            elevation,
            startPlane.GetReference(),
            verticalLines.First().Reference);
        document.FamilyCreate.NewAlignment(
            elevation,
            endPlane.GetReference(),
            verticalLines.Last().Reference);
        document.FamilyCreate.NewAlignment(
            elevation,
            innerTop.GetReference(),
            horizontalLines.First().Reference);
        document.FamilyCreate.NewAlignment(
            elevation,
            outerTop.GetReference(),
            horizontalLines.Last().Reference);
        document.FamilyCreate.NewAlignment(
            elevation,
            center.GetReference(),
            revolution.Axis.GeometryCurve.Reference);
        return revolution;
    }

    private static ReferencePlane CreateFrontHorizontalReferencePlane(
        Document document,
        View elevation,
        double elevationValue,
        double extent,
        string name)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            new XYZ(-extent, 0, elevationValue),
            new XYZ(extent, 0, elevationValue),
            XYZ.BasisY,
            elevation);
        plane.Name = name;
        return plane;
    }

    private static ReferencePlane CreateFrontVerticalReferencePlane(
        Document document,
        View elevation,
        double x,
        double extent,
        string name)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            new XYZ(x, 0, -extent),
            new XYZ(x, 0, extent),
            XYZ.BasisY,
            elevation);
        plane.Name = name;
        return plane;
    }

    private static Extrusion CreateNativeSquarePlate(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameter faceSize,
        FamilyParameter openingDiameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double currentFace = CurrentValue(manager, faceSize, Mm(198));
        double currentOpening = CurrentValue(manager, openingDiameter, Mm(120));
        double half = currentFace / 2;
        var outer = new CurveArray();
        XYZ[] points =
        [
            new(-half, -half, 0),
            new(half, -half, 0),
            new(half, half, 0),
            new(-half, half, 0)
        ];
        for (int index = 0; index < points.Length; index++)
            outer.Append(Line.CreateBound(points[index], points[(index + 1) % points.Length]));
        var inner = new CurveArray();
        double radius = currentOpening / 2;
        inner.Append(Arc.Create(
            XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        inner.Append(Arc.Create(
            XYZ.Zero, radius, Math.PI, Math.PI * 2, XYZ.BasisX, XYZ.BasisY));
        var profile = new CurveArrArray();
        profile.Append(outer);
        profile.Append(inner);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(2));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Line> lines = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Line>()
            .ToList();
        List<Line> vertical = lines
            .Where(line => Math.Abs(line.Direction.Y) > .99)
            .OrderBy(line => line.Evaluate(.5, true).X)
            .ToList();
        List<Line> horizontal = lines
            .Where(line => Math.Abs(line.Direction.X) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Y)
            .ToList();
        if (vertical.Count >= 2)
        {
            Dimension width = CreateLinearDimension(
                document,
                plan,
                [vertical.First().Reference, vertical.Last().Reference],
                new XYZ(-half, half * 1.18, 0),
                new XYZ(half, half * 1.18, 0));
            width.FamilyLabel = faceSize;
            EqualizeAboutCenter(
                document,
                plan,
                vertical.First().Reference,
                vertical.Last().Reference,
                XYZ.BasisX,
                half);
        }
        if (horizontal.Count >= 2)
        {
            Dimension depth = CreateLinearDimension(
                document,
                plan,
                [horizontal.First().Reference, horizontal.Last().Reference],
                new XYZ(half * 1.18, -half, 0),
                new XYZ(half * 1.18, half, 0));
            depth.FamilyLabel = faceSize;
            EqualizeAboutCenter(
                document,
                plan,
                horizontal.First().Reference,
                horizontal.Last().Reference,
                XYZ.BasisY,
                half);
        }
        Arc opening = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Arc>()
            .OrderByDescending(arc => arc.Radius)
            .First();
        Dimension openingDimension = document.FamilyCreate.NewDiameterDimension(
            plan,
            opening.Reference,
            new XYZ(radius * .65, radius * .65, 0));
        openingDimension.FamilyLabel = openingDiameter;
        return extrusion;
    }

    private static void EqualizeAboutCenter(
        Document document,
        View view,
        Reference first,
        Reference last,
        XYZ normal,
        double extent)
    {
        ReferencePlane? center = FindCenterReferencePlane(document, normal);
        if (center is null) return;
        XYZ start;
        XYZ end;
        if (normal.IsAlmostEqualTo(XYZ.BasisX))
        {
            start = new XYZ(-extent, -extent * 1.18, 0);
            end = new XYZ(extent, -extent * 1.18, 0);
        }
        else
        {
            start = new XYZ(-extent * 1.18, -extent, 0);
            end = new XYZ(-extent * 1.18, extent, 0);
        }
        Dimension eq = CreateLinearDimension(
            document,
            view,
            [first, center.GetReference(), last],
            start,
            end);
        eq.AreSegmentsEqual = true;
    }

    private static Extrusion CreateNativeAnnularExtrusion(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameter outerDiameter,
        FamilyParameter innerDiameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double outerD = CurrentValue(manager, outerDiameter, Mm(125));
        double innerD = CurrentValue(manager, innerDiameter, Mm(122));
        double outerR = Math.Max(outerD / 2, Mm(2));
        double innerR = Math.Clamp(innerD / 2, Mm(.5), outerR - Mm(.5));
        var outer = CircleProfile(outerR);
        var inner = CircleProfile(innerR);
        var profile = new CurveArrArray();
        profile.Append(outer);
        profile.Append(inner);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();

        List<Arc> arcs = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Arc>()
            .Where(arc => arc.Reference is not null)
            .OrderByDescending(arc => arc.Radius)
            .ToList();
        Dimension outerDimension = document.FamilyCreate.NewDiameterDimension(
            plan,
            arcs.First().Reference,
            new XYZ(outerR * 1.18, 0, 0));
        outerDimension.FamilyLabel = outerDiameter;
        Dimension innerDimension = document.FamilyCreate.NewDiameterDimension(
            plan,
            arcs.Last().Reference,
            new XYZ(innerR * .72, 0, 0));
        innerDimension.FamilyLabel = innerDiameter;
        return extrusion;
    }

    private static Extrusion CreateNativeCircularExtrusion(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameter diameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId materialId)
    {
        double currentD = CurrentValue(manager, diameter, Mm(20));
        double radius = Math.Max(currentD / 2, Mm(.5));
        var profile = new CurveArrArray();
        profile.Append(CircleProfile(radius));
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        SetFormMaterial(extrusion, materialId);
        document.Regenerate();
        Arc arc = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Arc>()
            .First(item => item.Reference is not null);
        Dimension dimension = document.FamilyCreate.NewDiameterDimension(
            plan,
            arc.Reference,
            new XYZ(radius * 1.2, 0, 0));
        dimension.FamilyLabel = diameter;
        return extrusion;
    }

    private static Extrusion CreateNativeBladeSector(
        Document document,
        FamilyManager manager,
        View plan,
        FamilyParameters parameters,
        ElementId materialId,
        double centerAngle,
        bool constrainRadius = true)
    {
        double outerRadius = CurrentValue(
            manager,
            parameters.BladeR,
            Mm(60));
        double innerRadius = Mm(10);
        const double halfAngle = Math.PI / 25;
        double startAngle = centerAngle - halfAngle;
        double endAngle = centerAngle + halfAngle;
        XYZ outerStart = PolarPoint(outerRadius, startAngle);
        XYZ outerEnd = PolarPoint(outerRadius, endAngle);
        XYZ innerStart = PolarPoint(innerRadius, startAngle);
        XYZ innerEnd = PolarPoint(innerRadius, endAngle);
        Arc outerArc = Arc.Create(
            XYZ.Zero,
            outerRadius,
            startAngle,
            endAngle,
            XYZ.BasisX,
            XYZ.BasisY);
        Curve innerArc = Arc.Create(
                XYZ.Zero,
                innerRadius,
                startAngle,
                endAngle,
                XYZ.BasisX,
                XYZ.BasisY)
            .CreateReversed();
        var sector = new CurveArray();
        sector.Append(outerArc);
        sector.Append(Line.CreateBound(outerEnd, innerEnd));
        sector.Append(innerArc);
        sector.Append(Line.CreateBound(innerStart, outerStart));
        var profile = new CurveArrArray();
        profile.Append(sector);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(6));
        AssociateExtrusionBounds(
            manager,
            extrusion,
            parameters.BladeZ0,
            parameters.BladeZ1);
        SetFormMaterial(extrusion, materialId);
        if (constrainRadius)
        {
            document.Regenerate();
            Arc sizedArc = extrusion.Sketch.Profile
                .Cast<CurveArray>()
                .SelectMany(item => item.Cast<Curve>())
                .OfType<Arc>()
                .OrderByDescending(item => item.Radius)
                .First();
            Dimension radius = document.FamilyCreate.NewRadialDimension(
                plan,
                sizedArc.Reference,
                PolarPoint(outerRadius * .72, centerAngle));
            radius.FamilyLabel = parameters.BladeR;
        }
        return extrusion;
    }

    private static XYZ PolarPoint(double radius, double angle) =>
        new(
            radius * Math.Cos(angle),
            radius * Math.Sin(angle),
            0);

    private static CurveArray CircleProfile(double radius)
    {
        var circle = new CurveArray();
        circle.Append(Arc.Create(
            XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        circle.Append(Arc.Create(
            XYZ.Zero, radius, Math.PI, Math.PI * 2, XYZ.BasisX, XYZ.BasisY));
        return circle;
    }

    private static CurveArray VerticalCircleProfile(
        double radius,
        double centerZ)
    {
        var circle = new CurveArray();
        XYZ center = new(0, 0, centerZ);
        circle.Append(Arc.Create(
            center, radius, 0, Math.PI, XYZ.BasisY, XYZ.BasisZ));
        circle.Append(Arc.Create(
            center, radius, Math.PI, Math.PI * 2, XYZ.BasisY, XYZ.BasisZ));
        return circle;
    }

    private static int CreateDuctConnector(
        Document document,
        FamilyManager manager,
        GeometryResult geometry,
        FamilyParameter physicalDiameter)
    {
        document.Regenerate();
        XYZ normal;
        double targetCoordinate;
        if (geometry.HorizontalConnector)
        {
            normal = XYZ.BasisX;
            targetCoordinate = CurrentValue(
                manager,
                EnsureLengthParameter(manager, "FT_LE_ZZ_SpigotX1"),
                Mm(150));
        }
        else
        {
            normal = XYZ.BasisZ;
            targetCoordinate = CurrentValue(
                manager,
                EnsureLengthParameter(manager, "FT_LE_ZZ_Height"),
                Mm(50));
        }
        Reference connectorFace = FindPlanarFace(
            geometry.ConnectorHost,
            targetCoordinate,
            normal);
        ConnectorElement connector = ConnectorElement.CreateDuctConnector(
            document,
            DuctSystemType.SupplyAir,
            ConnectorProfileType.Round,
            connectorFace);
        Parameter? diameter = connector.get_Parameter(
            BuiltInParameter.CONNECTOR_DIAMETER);
        if (diameter is null || diameter.IsReadOnly)
            throw new InvalidOperationException(
                "The TROX RFD connector does not expose an editable Diameter parameter.");
        manager.AssociateElementParameterToFamilyParameter(
            diameter,
            physicalDiameter);
        return 1;
    }

    private static Reference FindPlanarFace(
        Element element,
        double targetCoordinate,
        XYZ normal)
    {
        XYZ targetNormal = normal.Normalize();
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
            .Where(face =>
                face.FaceNormal.Normalize().DotProduct(targetNormal) > .99)
            .OrderBy(face => Math.Abs(
                face.Origin.DotProduct(targetNormal) - targetCoordinate))
            .FirstOrDefault();
        return best?.Reference
            ?? throw new InvalidOperationException(
                "Could not resolve the RFD spigot end face for the duct connector.");
    }

    private static void ValidateAllTypes(
        Document document,
        FamilyManager manager,
        IReadOnlyDictionary<int, FamilyType> familyTypes,
        int selectedSize,
        FamilyInstance nestedCore,
        FamilyParameter parentBladeD,
        GeometryResult connectorGeometry,
        FamilyParameter parentDuctDiameter,
        FamilyParameter parentConnectorCenter,
        FamilyParameter parentSpigotLength)
    {
        Parameter nestedBladeD = nestedCore.LookupParameter(
                "FT_LE_ZZ_BladeD")
            ?? throw new InvalidOperationException(
                "The nested core BladeD association was lost.");
        ConnectorElement connector = new FilteredElementCollector(document)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .Single();
        Parameter connectorDiameter = connector.get_Parameter(
                BuiltInParameter.CONNECTOR_DIAMETER)
            ?? throw new InvalidOperationException(
                "The TROX duct connector Diameter parameter was lost.");
        foreach ((int nominalSize, FamilyType type) in familyTypes)
        {
            manager.CurrentType = type;
            document.Regenerate();
            double expectedBladeD = type.AsDouble(parentBladeD)
                ?? throw new InvalidOperationException(
                    $"NS{nominalSize} has no parent BladeD value.");
            double actualBladeD = nestedBladeD.AsDouble();
            if (Math.Abs(actualBladeD - expectedBladeD) > 1e-7)
                throw new InvalidOperationException(
                    $"NS{nominalSize} did not drive the nested core BladeD geometry.");

            BoundingBoxXYZ coreBounds = nestedCore.get_BoundingBox(null)
                ?? throw new InvalidOperationException(
                    $"NS{nominalSize} has no nested core geometry bounds.");
            double coreWidth = Math.Max(
                coreBounds.Max.X - coreBounds.Min.X,
                coreBounds.Max.Y - coreBounds.Min.Y);
            if (coreWidth < expectedBladeD * .96
                || coreWidth > expectedBladeD * 1.04)
            {
                throw new InvalidOperationException(
                    $"NS{nominalSize} nested blade geometry is " +
                    $"{UnitUtils.ConvertFromInternalUnits(coreWidth, UnitTypeId.Millimeters):0.###} mm, " +
                    $"expected ØD2 " +
                    $"{UnitUtils.ConvertFromInternalUnits(expectedBladeD, UnitTypeId.Millimeters):0.###} mm.");
            }

            double expectedDuctD = type.AsDouble(parentDuctDiameter)
                ?? throw new InvalidOperationException(
                    $"NS{nominalSize} has no catalog duct diameter value.");
            if (Math.Abs(connectorDiameter.AsDouble() - expectedDuctD) > 1e-7)
                throw new InvalidOperationException(
                    $"NS{nominalSize} duct connector is not associated to catalog ØD.");

            BoundingBoxXYZ hostBounds =
                connectorGeometry.ConnectorHost.get_BoundingBox(null)
                ?? throw new InvalidOperationException(
                    $"NS{nominalSize} has no connector collar geometry bounds.");
            double hostDiameter = connectorGeometry.HorizontalConnector
                ? Math.Max(
                    hostBounds.Max.Y - hostBounds.Min.Y,
                    hostBounds.Max.Z - hostBounds.Min.Z)
                : Math.Max(
                    hostBounds.Max.X - hostBounds.Min.X,
                    hostBounds.Max.Y - hostBounds.Min.Y);
            double expectedSpigotOd = expectedDuctD + Mm(3);
            if (Math.Abs(hostDiameter - expectedSpigotOd) > Mm(.2))
                throw new InvalidOperationException(
                    $"NS{nominalSize} connector collar host does not surround catalog ØD correctly.");

            if (connectorGeometry.HorizontalConnector)
            {
                double expectedA = type.AsDouble(parentConnectorCenter)
                    ?? throw new InvalidOperationException(
                        $"NS{nominalSize} has no catalog connector centre A.");
                double actualA = (hostBounds.Min.Z + hostBounds.Max.Z) / 2;
                if (Math.Abs(actualA - expectedA) > Mm(.2))
                    throw new InvalidOperationException(
                        $"NS{nominalSize} connector centre does not match catalog A.");

                double expectedC = type.AsDouble(parentSpigotLength)
                    ?? throw new InvalidOperationException(
                        $"NS{nominalSize} has no catalog spigot length C.");
                double actualC = hostBounds.Max.X - hostBounds.Min.X;
                if (Math.Abs(actualC - expectedC) > Mm(.2))
                    throw new InvalidOperationException(
                        $"NS{nominalSize} connector collar length does not match catalog C.");
            }
        }
        manager.CurrentType = familyTypes[selectedSize];
        document.Regenerate();
    }

    private static void ValidateParentComposition(
        Document document,
        int expectedParentForms,
        int expectedNestedCores,
        int expectedConnectors)
    {
        int parentExtrusions = new FilteredElementCollector(document)
            .OfClass(typeof(Extrusion))
            .GetElementCount();
        int parentRevolutions = new FilteredElementCollector(document)
            .OfClass(typeof(Revolution))
            .GetElementCount();
        int parentForms = parentExtrusions + parentRevolutions;
        int nestedCores = new FilteredElementCollector(document)
            .OfClass(typeof(FamilyInstance))
            .GetElementCount();
        int connectors = new FilteredElementCollector(document)
            .OfClass(typeof(ConnectorElement))
            .GetElementCount();

        if (parentForms < expectedParentForms)
            throw new InvalidOperationException(
                "Parent Air Terminal geometry is incomplete: "
                + $"{parentForms}/{expectedParentForms} native forms were found.");
        if (nestedCores != expectedNestedCores)
            throw new InvalidOperationException(
                "Parent Air Terminal nested-core structure is incomplete: "
                + $"{nestedCores}/{expectedNestedCores} cores were found.");
        if (connectors != expectedConnectors)
            throw new InvalidOperationException(
                "Parent Air Terminal connector structure is incomplete: "
                + $"{connectors}/{expectedConnectors} connectors were found.");
    }

    private static void DeleteTemporaryNestedFolder(string path)
    {
        try
        {
            string name = Path.GetFileName(
                path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            if (!name.StartsWith(
                    ".familymep-tmp-trox-",
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // The generated parent already embeds the nested Families. A locked
            // temporary file must not invalidate an otherwise valid RFA.
        }
    }

    private static void AssociateExtrusionBounds(
        FamilyManager manager,
        Extrusion extrusion,
        FamilyParameter start,
        FamilyParameter end)
    {
        Parameter startParameter = extrusion.get_Parameter(
                BuiltInParameter.EXTRUSION_START_PARAM)
            ?? throw new InvalidOperationException(
                "Extrusion Start parameter is unavailable.");
        Parameter endParameter = extrusion.get_Parameter(
                BuiltInParameter.EXTRUSION_END_PARAM)
            ?? throw new InvalidOperationException(
                "Extrusion End parameter is unavailable.");
        manager.AssociateElementParameterToFamilyParameter(
            startParameter,
            start);
        manager.AssociateElementParameterToFamilyParameter(
            endParameter,
            end);
    }

    private static Dimension CreateLinearDimension(
        Document document,
        View view,
        IReadOnlyList<Reference> references,
        XYZ start,
        XYZ end)
    {
        var array = new ReferenceArray();
        foreach (Reference reference in references)
            array.Append(reference);
        return document.FamilyCreate.NewLinearDimension(
            view,
            Line.CreateBound(start, end),
            array);
    }

    private static ReferencePlane? FindCenterReferencePlane(
        Document document,
        XYZ normal) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(ReferencePlane))
            .Cast<ReferencePlane>()
            .Where(plane =>
            {
                try
                {
                    Plane geometry = plane.GetPlane();
                    return Math.Abs(
                               geometry.Normal.Normalize()
                                   .DotProduct(normal.Normalize())) > .99
                           && Math.Abs(
                               geometry.Origin.DotProduct(normal)) < 1e-6;
                }
                catch
                {
                    return false;
                }
            })
            .OrderBy(plane =>
                plane.Name.Contains("Center", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .FirstOrDefault();

    private static View FindFamilyView(
        Document document,
        XYZ direction) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view =>
                !view.IsTemplate
                && view.ViewType != ViewType.ThreeD
                && Math.Abs(
                    view.ViewDirection.Normalize()
                        .DotProduct(direction.Normalize())) > .99)
            .OrderBy(view =>
                view.Name.Equals("Ref. Level", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .FirstOrDefault()
        ?? throw new InvalidOperationException(
            "The Generic Model template does not contain a Ref. Level plan view.");

    private static FamilyParameter FixedLength(
        FamilyManager manager,
        string name,
        string formula)
    {
        FamilyParameter parameter = EnsureLengthParameter(manager, name);
        if (!string.Equals(
                parameter.Formula,
                formula,
                StringComparison.Ordinal))
            manager.SetFormula(parameter, formula);
        return parameter;
    }

    private static void ValidateControlledLengthParameters(
        FamilyManager manager,
        string familyRole,
        params FamilyParameter[] selectorParameters)
    {
        HashSet<string> selectors = selectorParameters
            .Select(parameter => parameter.Definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> editableDimensions = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter =>
                parameter.Definition.Name.StartsWith(
                    "FT_LE_ZZ_",
                    StringComparison.OrdinalIgnoreCase))
            .Where(parameter =>
                parameter.Definition.GetDataType().Equals(
                    SpecTypeId.Length))
            .Where(parameter =>
                !selectors.Contains(parameter.Definition.Name))
            .Where(parameter =>
                string.IsNullOrWhiteSpace(parameter.Formula))
            .Select(parameter => parameter.Definition.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (editableDimensions.Count == 0)
            return;

        throw new InvalidOperationException(
            $"{familyRole} contains editable dimension parameters: "
            + string.Join(", ", editableDimensions)
            + ". Variable dimensions must use size_lookup; fixed dimensions "
            + "must use a literal or derived formula.");
    }

    private static FamilyParameter EnsureLengthParameter(
        FamilyManager manager,
        string name,
        bool isInstance = false)
    {
        FamilyParameter? existing = manager.Parameters
            .Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
        return existing
            ?? manager.AddParameter(
                 name,
                 GroupTypeId.Geometry,
                 SpecTypeId.Length,
                 isInstance);
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

    private static void SetFormMaterial(
        GenericForm form,
        ElementId materialId)
    {
        Parameter? material = form.get_Parameter(
            BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
            material.Set(materialId);
    }

    private static void SetSubcategory(
        GenericForm form,
        Document document,
        string name)
    {
        Category? category = document.OwnerFamily?.FamilyCategory;
        if (category is null) return;
        Category? subcategory = category.SubCategories
            .Cast<Category>()
            .FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        subcategory ??= document.Settings.Categories
            .NewSubcategory(category, name);
        form.Subcategory = subcategory;
    }

    private static void SetAirTerminalCategory(Document document)
    {
        Category category = document.Settings.Categories.get_Item(
            BuiltInCategory.OST_DuctTerminal);
        document.OwnerFamily.FamilyCategory = category;
    }

    private static void SetMetricUnits(Document document)
    {
        Units units = document.GetUnits();
        units.SetFormatOptions(
            SpecTypeId.Length,
            new FormatOptions(UnitTypeId.Millimeters)
            {
                Accuracy = .1,
                UseDigitGrouping = false
            });
        document.SetUnits(units);
    }

    private static ElementId EnsureNeutralGray(Document document)
    {
        const string name = "FamilyMEP - Neutral Gray";
        Material? material = new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (material is null)
        {
            ElementId id = Material.Create(document, name);
            material = (Material)document.GetElement(id);
        }
        material.Color = new Autodesk.Revit.DB.Color(160, 160, 160);
        material.Transparency = 0;
        return material.Id;
    }

    private static double Mm(double value) =>
        UnitUtils.ConvertToInternalUnits(
            value,
            UnitTypeId.Millimeters);

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

    private sealed class SuppressIdenticalGeometryArrayWarning
        : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(
            FailuresAccessor failuresAccessor)
        {
            FailureDefinitionId target =
                BuiltInFailures.ArrayFailures.IdenticalGeometryCopiesInArray;
            foreach (FailureMessageAccessor failure
                     in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning
                    && failure.GetFailureDefinitionId() == target)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
