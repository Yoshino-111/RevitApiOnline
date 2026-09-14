using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

/// <summary>
/// Catalog-parametric Geberit PRO_103025 90-degree Mapress bend.
/// The official 90-degree article range is embedded as a Revit lookup table;
/// every parent Type drives one native Sweep and two one-Type nested,
/// editable Revolution press ends.
/// </summary>
internal static class GeberitMapressBendBuilderService
{
    private sealed record CatalogRow(
        string Article,
        int DN,
        double D,
        double Arc,
        double L,
        double Z)
    {
        public string TypeName => $"DN{DN}";
        public double EndLength => L - Z;
    }

    private sealed record ParentParameters(
        FamilyParameter DN,
        FamilyParameter D,
        FamilyParameter L,
        FamilyParameter Z,
        FamilyParameter Arc,
        FamilyParameter EndL,
        FamilyParameter Wall,
        FamilyParameter BodyOD,
        FamilyParameter BoreD,
        FamilyParameter SocketOD,
        FamilyParameter RingOD,
        FamilyParameter BeadW,
        FamilyParameter ConnD);

    private sealed record NestedArtifact(
        string Path,
        string FamilyName,
        string TypeName);

    private sealed record PrototypeGeometry(
        Sweep Bend,
        FamilyInstance End1,
        FamilyInstance End2,
        ConnectorElement Connector1,
        ConnectorElement Connector2);

    private sealed record NestedRevolveReferences(
        ReferencePlane Start,
        ReferencePlane End,
        ReferencePlane Axis,
        ReferencePlane InnerRadius,
        ReferencePlane SocketRadius,
        ReferencePlane MouthRadius,
        ReferencePlane RingRadius);

    public static PipeFittingInspection Inspect()
    {
        IReadOnlyList<CatalogRow> rows = CatalogRows();
        ValidateCatalogRows(rows);
        CatalogRow row = rows.Single(item => item.DN == 20);
        return new PipeFittingInspection
        {
            CategoryName = "Pipe Fittings",
            PartTypeName = "Elbow",
            ConnectorCount = 2,
            TypeCount = rows.Count,
            DefaultTypeName = row.TypeName,
            NominalDiameterMm = row.DN,
            OutsideDiameterMm = row.D,
            LengthMm = row.L,
            ZMm = row.Z
        };
    }

    public static PipeFittingBuilderResult Execute(
        UIApplication uiApplication,
        PipeFittingBuilderRequest request)
    {
        IReadOnlyList<CatalogRow> rows = CatalogRows();
        ValidateCatalogRows(rows);
        CatalogRow row = rows.Single(item => item.DN == 20);
        if (!File.Exists(AppPaths.GeberitMapressBendCatalogPdf))
            throw new FileNotFoundException(
                "The official Geberit PRO_103025 catalog PDF was not found.",
                AppPaths.GeberitMapressBendCatalogPdf);
        if (!File.Exists(AppPaths.GenericModel2020Template))
            throw new FileNotFoundException(
                "The Metric Generic Model 2020 template was not found.",
                AppPaths.GenericModel2020Template);

        Document project = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException(
                "Open an RVT project before creating the Mapress bend.");
        if (project.IsFamilyDocument)
            throw new InvalidOperationException(
                "Start Family Creator from an RVT project.");

        Directory.CreateDirectory(
            Path.GetDirectoryName(request.OutputPath)
            ?? AppPaths.GeneratedPipeFittingFolder);
        string temporaryFolder = Path.Combine(
            AppPaths.GeneratedPipeFittingFolder,
            "_temp_mapress_catalog");
        Directory.CreateDirectory(temporaryFolder);
        NestedArtifact? nestedX = null;
        NestedArtifact? nestedZ = null;
        try
        {
            nestedX = BuildNestedEnd(
                uiApplication.Application,
                row,
                temporaryFolder,
                "_FM_Mapress_End_X",
                XYZ.BasisX);
            nestedZ = BuildNestedEnd(
                uiApplication.Application,
                row,
                temporaryFolder,
                "_FM_Mapress_End_Z",
                -XYZ.BasisZ);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Geberit catalog failed at nested Mapress-end construction: "
                + exception.Message,
                exception);
        }

        var result = new PipeFittingBuilderResult
        {
            FamilyName = Path.GetFileNameWithoutExtension(request.OutputPath),
            ActiveTypeName = row.TypeName,
            OutputPath = request.OutputPath,
            CategoryName = "Pipe Fittings",
            TypeCount = rows.Count
        };
        Document? familyDocument = null;
        try
        {
            familyDocument = uiApplication.Application.NewFamilyDocument(
                AppPaths.GenericModel2020Template);
            using (Transaction transaction = new(
                       familyDocument,
                       "FamilyMEP - Build Geberit Mapress bend catalog"))
            {
                transaction.Start();
                SetMetricUnits(familyDocument);
                SetPipeFittingCategory(familyDocument);
                FamilyManager manager = familyDocument.FamilyManager;
                EnsureBootstrapType(manager, row.TypeName);
                string lookupCsv = WriteLookupCsv(request.OutputPath, rows);
                string lookupTable = ImportLookupTable(
                    familyDocument,
                    lookupCsv);
                ParentParameters p = CreateParentParameters(
                    manager,
                    lookupTable);
                Dictionary<int, FamilyType> types = CreateFamilyTypes(
                    manager,
                    rows,
                    row,
                    p.DN);
                manager.CurrentType = types[row.DN];
                familyDocument.Regenerate();
                ElementId material = EnsureNeutralGray(familyDocument);
                try
                {
                    CreateReferenceSkeleton(familyDocument, manager, p, row);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Geberit catalog failed at reference-plane skeleton: "
                        + exception.Message,
                        exception);
                }
                PrototypeGeometry geometry;
                try
                {
                    geometry = BuildPrototypeGeometry(
                        familyDocument,
                        manager,
                        p,
                        row,
                        nestedX ?? throw new InvalidOperationException(
                            "The X-oriented Mapress nested family is unavailable."),
                        nestedZ ?? throw new InvalidOperationException(
                            "The Z-oriented Mapress nested family is unavailable."),
                        material,
                        result);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Geberit catalog failed at native elbow/nested/connectors: "
                        + exception.Message,
                        exception);
                }
                try
                {
                    ValidateCatalogFamily(
                        familyDocument,
                        manager,
                        p,
                        rows,
                        types,
                        row,
                        lookupTable,
                        geometry,
                        result);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Geberit catalog failed at automatic flex acceptance: "
                        + exception.Message,
                        exception);
                }
                transaction.Commit();
            }

            familyDocument.SaveAs(
                request.OutputPath,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    MaximumBackups = 1
                });
            if (request.LoadIntoProject)
                familyDocument.LoadFamily(
                    project,
                    new OverwriteFamilyLoadOptions());
            familyDocument.Close(false);
            familyDocument = null;

            result.AppliedChanges.Add(
                "10 official 90-degree Types: DN12-DN100 (articles 30102-31111)");
            result.AppliedChanges.Add(
                "Two orientation-specific nested Mapress ends avoid instance rotation; each has one parametric Type");
            result.AppliedChanges.Add(
                "Embedded Geberit lookup table drives d, L and Z; every Type is flex-tested before save");
            result.AppliedChanges.Add(
                "Editable native hollow Sweep path and nested ends are constrained to L/Z reference planes");
            result.AppliedChanges.Add(
                "Neutral gray geometry; manufacturer and identity fields remain blank");
            return result;
        }
        finally
        {
            if (familyDocument is not null)
            {
                try { familyDocument.Close(false); }
                catch { }
            }
            TryDeleteTemporary(nestedX?.Path ?? string.Empty);
            TryDeleteTemporary(nestedZ?.Path ?? string.Empty);
        }
    }

    private static IReadOnlyList<CatalogRow> CatalogRows() =>
    [
        new("30102", 12, 15, 90, 38, 18),
        new("30103", 15, 18, 90, 42, 22),
        new("30104", 20, 22, 90, 47, 26),
        new("30105", 25, 28, 90, 57, 34),
        new("31106", 32, 35, 90, 68, 42),
        new("31107", 40, 42, 90, 80, 50),
        new("31108", 50, 54, 90, 100, 65),
        new("31109", 65, 76.1, 90, 157, 104),
        new("31110", 80, 88.9, 90, 184, 124),
        new("31111", 100, 108, 90, 226, 151)
    ];

    private static void ValidateCatalogRows(IReadOnlyList<CatalogRow> rows)
    {
        if (rows.Count != 10
            || rows.Select(row => row.Article).Distinct().Count() != rows.Count
            || rows.Select(row => row.DN).Distinct().Count() != rows.Count)
            throw new InvalidOperationException(
                "Geberit PRO_103025 90-degree catalog must contain 10 unique articles and DN selectors.");
        foreach (CatalogRow row in rows)
        {
            if (Math.Abs(row.Arc - 90) > .001
                || row.D <= 0
                || row.L <= row.Z
                || row.Z <= row.D / 2
                || row.EndLength <= 0)
                throw new InvalidOperationException(
                    $"{row.TypeName}: invalid official d/L/Z relationship.");
        }
        CatalogRow dn20 = rows.Single(row => row.Article == "30104");
        if (dn20.DN != 20
            || Math.Abs(dn20.D - 22) > .001
            || Math.Abs(dn20.L - 47) > .001
            || Math.Abs(dn20.Z - 26) > .001)
            throw new InvalidOperationException(
                "The DN20 acceptance row no longer matches official article 30104.");
    }

    private static NestedArtifact BuildNestedEnd(
        Autodesk.Revit.ApplicationServices.Application application,
        CatalogRow row,
        string temporaryFolder,
        string familyName,
        XYZ axis)
    {
        const string typeName = "MapressEnd";
        string path = Path.Combine(temporaryFolder, familyName + ".rfa");
        Document? document = null;
        try
        {
            document = application.NewFamilyDocument(
                AppPaths.GenericModel2020Template);
            using (Transaction transaction = new(
                       document,
                       "FamilyMEP - Build one-Type Mapress nested end"))
            {
                transaction.Start();
                SetMetricUnits(document);
                DisableAlwaysVertical(document);
                FamilyManager manager = document.FamilyManager;
                EnsureBootstrapType(manager, typeName);
                FamilyParameter d = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_d", true);
                FamilyParameter position = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_Pos", true);
                FamilyParameter endL = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_EndL", true);
                FamilyParameter socketOD = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_SocketOD", true);
                FamilyParameter ringOD = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_RingOD", true);
                FamilyParameter beadW = EnsureLengthParameter(
                    manager, "FT_LE_ZZ_BeadW", true);
                FamilyParameter x0 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_X0",
                    "FT_LE_ZZ_Pos - FT_LE_ZZ_EndL", true);
                FamilyParameter rear1 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_RearX1",
                    "FT_LE_ZZ_X0 + FT_LE_ZZ_BeadW * 0.75", true);
                FamilyParameter rear2 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_RearX2",
                    "FT_LE_ZZ_X0 + FT_LE_ZZ_BeadW * 1.5", true);
                FamilyParameter bell0 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX0",
                    "FT_LE_ZZ_Pos - FT_LE_ZZ_EndL * 0.38", true);
                FamilyParameter bell1 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX1",
                    "FT_LE_ZZ_BellX0 + FT_LE_ZZ_BeadW * 0.65", true);
                FamilyParameter bell2 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX2",
                    "FT_LE_ZZ_BellX1 + FT_LE_ZZ_BeadW * 0.65", true);
                FamilyParameter bell3 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX3",
                    "FT_LE_ZZ_BellX2 + FT_LE_ZZ_BeadW * 0.65", true);
                FamilyParameter bell4 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX4",
                    "FT_LE_ZZ_BellX3 + FT_LE_ZZ_BeadW * 0.65", true);
                FamilyParameter bell5 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_BellX5",
                    "FT_LE_ZZ_BellX4 + FT_LE_ZZ_BeadW * 0.65", true);
                FamilyParameter x1 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_X1", "FT_LE_ZZ_Pos", true);
                FamilyParameter rearOD0 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_RearOD0",
                    "FT_LE_ZZ_SocketOD - 2 mm", true);
                FamilyParameter rearOD1 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_RearOD1",
                    "FT_LE_ZZ_SocketOD - 1 mm", true);
                FamilyParameter mouthOD1 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_MouthOD1",
                    "FT_LE_ZZ_SocketOD + 1 mm", true);
                FamilyParameter mouthOD2 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_MouthOD2",
                    "FT_LE_ZZ_SocketOD + 2 mm", true);
                FamilyParameter mouthOD3 = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_MouthOD3",
                    "FT_LE_ZZ_RingOD - 1 mm", true);
                FamilyParameter innerRadius = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_InnerR",
                    "FT_LE_ZZ_d / 2", true);
                FamilyParameter socketRadius = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_SocketR",
                    "FT_LE_ZZ_SocketOD / 2", true);
                FamilyParameter mouthRadius = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_MouthR",
                    "FT_LE_ZZ_MouthOD3 / 2", true);
                FamilyParameter ringRadius = EnsureFormulaLengthParameter(
                    manager, "FT_LE_ZZ_RingR",
                    "FT_LE_ZZ_RingOD / 2", true);
                manager.Set(d, Mm(row.D));
                manager.Set(position, Mm(row.L));
                manager.Set(endL, Mm(row.EndLength));
                manager.Set(socketOD, Mm(row.D + 6));
                manager.Set(ringOD, Mm(row.D + 10));
                manager.Set(beadW, Mm(2));
                document.Regenerate();

                ElementId material = EnsureNeutralGray(document);
                NestedRevolveReferences revolveReferences =
                    CreateNestedRevolveReferenceSkeleton(
                        document,
                        manager,
                        axis,
                        position,
                        endL,
                        innerRadius,
                        socketRadius,
                        mouthRadius,
                        ringRadius);
                Revolution revolution = CreateNativeMapressRevolution(
                    document,
                    manager,
                    axis,
                    material,
                    revolveReferences,
                    x0,
                    rear1,
                    rear2,
                    bell0,
                    bell1,
                    bell2,
                    bell3,
                    bell4,
                    bell5,
                    x1,
                    rearOD0,
                    rearOD1,
                    socketOD,
                    mouthOD1,
                    mouthOD2,
                    mouthOD3,
                    ringOD,
                    d);
                document.Regenerate();
                Parameter? alwaysVertical = document.OwnerFamily.get_Parameter(
                    BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
                if (alwaysVertical is not null && alwaysVertical.AsInteger() != 0)
                    throw new InvalidOperationException(
                        "The Mapress nested family is still marked Always vertical.");
                int nestedForms = new FilteredElementCollector(document)
                    .OfClass(typeof(Revolution))
                    .GetElementCount();
                if (nestedForms != 1)
                    throw new InvalidOperationException(
                        $"The Mapress end must contain one editable Revolution; found {nestedForms}.");
                if (revolution.Sketch is null)
                    throw new InvalidOperationException(
                        "The Mapress Revolution has no editable profile sketch.");
                if (new FilteredElementCollector(document)
                        .OfClass(typeof(Extrusion))
                        .GetElementCount() != 0)
                    throw new InvalidOperationException(
                        "The Mapress nested end still contains legacy Extrusions.");
                string[] requiredReferencePlanes =
                [
                    "RP_End_Start",
                    "RP_End_Face",
                    "RP_End_Origin",
                    "RP_Revolve_Axis",
                    "RP_Inner_R",
                    "RP_Socket_R",
                    "RP_Mouth_R",
                    "RP_Ring_R"
                ];
                ISet<string> referencePlaneNames =
                    new HashSet<string>(
                        new FilteredElementCollector(document)
                            .OfClass(typeof(ReferencePlane))
                            .Cast<ReferencePlane>()
                            .Select(referencePlane => referencePlane.Name),
                        StringComparer.OrdinalIgnoreCase);
                if (requiredReferencePlanes.Any(
                        name => !referencePlaneNames.Contains(name)))
                    throw new InvalidOperationException(
                        "The Mapress Revolution reference-plane skeleton is incomplete.");
                AssertMm(manager, socketOD, row.D + 6, "nested socket OD");
                AssertMm(manager, ringOD, row.D + 10, "nested press-ring OD");
                if (manager.Types.Cast<FamilyType>().Count() != 1)
                    throw new InvalidOperationException(
                        "The Mapress nested family must contain exactly one Type.");
                transaction.Commit();
            }
            document.SaveAs(
                path,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    MaximumBackups = 1
                });
            document.Close(false);
            document = null;
            return new NestedArtifact(path, familyName, typeName);
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

    private static ParentParameters CreateParentParameters(
        FamilyManager manager,
        string lookupTable)
    {
        FamilyParameter dn = EnsureLengthParameter(manager, "FT_LE_ZZ_DN");
        FamilyParameter d = EnsureLengthParameter(manager, "FT_LE_ZZ_d");
        FamilyParameter l = EnsureLengthParameter(manager, "FT_LE_ZZ_L");
        FamilyParameter z = EnsureLengthParameter(manager, "FT_LE_ZZ_Z");
        FamilyParameter arc = EnsureAngleParameter(manager, "FT_LE_ZZ_Arc");
        manager.SetFormula(d, Lookup(lookupTable, "d"));
        manager.SetFormula(l, Lookup(lookupTable, "L"));
        manager.SetFormula(z, Lookup(lookupTable, "Z"));
        manager.SetFormula(arc, "90°");
        FamilyParameter endL = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_EndL", "FT_LE_ZZ_L - FT_LE_ZZ_Z");
        FamilyParameter wall = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_Wall", "1.5 mm");
        FamilyParameter bodyOD = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_BodyOD", "FT_LE_ZZ_d + 2 * FT_LE_ZZ_Wall");
        FamilyParameter boreD = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_BoreD", "FT_LE_ZZ_d - 2 * FT_LE_ZZ_Wall");
        FamilyParameter socketOD = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_SocketOD", "FT_LE_ZZ_d + 6 mm");
        FamilyParameter ringOD = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_RingOD", "FT_LE_ZZ_d + 10 mm");
        FamilyParameter beadW = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_BeadW", "2 mm");
        FamilyParameter connD = EnsureFormulaLengthParameter(
            manager, "FT_LE_ZZ_ConnD", "FT_LE_ZZ_d");
        return new ParentParameters(
            dn, d, l, z, arc, endL, wall, bodyOD, boreD,
            socketOD, ringOD, beadW, connD);
    }

    private static string WriteLookupCsv(
        string outputPath,
        IReadOnlyList<CatalogRow> rows)
    {
        string folder = Path.GetDirectoryName(outputPath)
            ?? AppPaths.GeneratedPipeFittingFolder;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(
            folder,
            "PipeFitting_Geberit_Mapress_Bend90_LTN.csv");
        var builder = new StringBuilder();
        builder.AppendLine(
            ",DN##LENGTH##MILLIMETERS,"
            + "d##LENGTH##MILLIMETERS,"
            + "L##LENGTH##MILLIMETERS,"
            + "Z##LENGTH##MILLIMETERS");
        foreach (CatalogRow row in rows)
        {
            builder.Append(row.TypeName);
            builder.Append(',');
            builder.Append(row.DN.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.D.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.L.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(row.Z.ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string ImportLookupTable(Document document, string csvPath)
    {
        FamilySizeTableManager? manager =
            FamilySizeTableManager.GetFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
        if (manager is null || !manager.IsValidObject)
        {
            FamilySizeTableManager.CreateFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
            manager = FamilySizeTableManager.GetFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
        }
        if (manager is null || !manager.IsValidObject)
            throw new InvalidOperationException(
                "Revit could not create the Geberit lookup-table manager.");
        var error = new FamilySizeTableErrorInfo();
        if (!manager.ImportSizeTable(document, csvPath, error))
            throw new InvalidOperationException(
                "Geberit lookup-table import failed. "
                + $"Error: {error.FamilySizeTableErrorType}; "
                + $"row {error.InvalidRowIndex}, column {error.InvalidColumnIndex}, "
                + $"header '{error.InvalidHeaderText}'.");
        return Path.GetFileNameWithoutExtension(csvPath);
    }

    private static string Lookup(string table, string column) =>
        $"size_lookup(\"{table}\", \"{column}\", 1 mm, FT_LE_ZZ_DN)";

    private static Dictionary<int, FamilyType> CreateFamilyTypes(
        FamilyManager manager,
        IReadOnlyList<CatalogRow> rows,
        CatalogRow selected,
        FamilyParameter dn)
    {
        var result = new Dictionary<int, FamilyType>();
        FamilyType current = manager.CurrentType
            ?? throw new InvalidOperationException(
                "The Geberit preview Type is unavailable.");
        if (!current.Name.Equals(
                selected.TypeName,
                StringComparison.OrdinalIgnoreCase))
        {
            manager.RenameCurrentType(selected.TypeName);
            current = manager.CurrentType;
        }
        manager.Set(dn, Mm(selected.DN));
        result[selected.DN] = current;
        foreach (CatalogRow row in rows.Where(row => row.DN != selected.DN))
        {
            FamilyType created = manager.NewType(row.TypeName);
            manager.Set(dn, Mm(row.DN));
            result[row.DN] = created;
        }
        return result;
    }

    private static PrototypeGeometry BuildPrototypeGeometry(
        Document document,
        FamilyManager manager,
        ParentParameters p,
        CatalogRow row,
        NestedArtifact nestedX,
        NestedArtifact nestedZ,
        ElementId materialId,
        PipeFittingBuilderResult result)
    {
        double l = Mm(row.L);
        double z = Mm(row.Z);
        Sweep bend = CreateNativeHollowElbowSweep(
            document,
            manager,
            p,
            row,
            materialId);

        FamilySymbol symbolX = LoadSingleNestedSymbol(document, nestedX);
        FamilySymbol symbolZ = LoadSingleNestedSymbol(document, nestedZ);
        if (!symbolX.IsActive) symbolX.Activate();
        if (!symbolZ.IsActive) symbolZ.Activate();
        document.Regenerate();
        FamilyInstance end1 = document.FamilyCreate.NewFamilyInstance(
            XYZ.Zero,
            symbolX,
            StructuralType.NonStructural);
        FamilyInstance end2 = document.FamilyCreate.NewFamilyInstance(
            XYZ.Zero,
            symbolZ,
            StructuralType.NonStructural);
        AssociateNestedParameters(manager, end1, p);
        AssociateNestedParameters(manager, end2, p);
        document.Regenerate();

        Reference face1 = FindPlanarFace(bend, XYZ.BasisX, new XYZ(l, 0, 0));
        Reference face2 = FindPlanarFace(bend, -XYZ.BasisZ, new XYZ(0, 0, -l));
        ConnectorElement connector1 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            face1);
        ConnectorElement connector2 = ConnectorElement.CreatePipeConnector(
            document,
            PipeSystemType.Global,
            face2);
        AssociateConnectorDiameter(manager, connector1, p.ConnD);
        AssociateConnectorDiameter(manager, connector2, p.ConnD);
        connector1.SetLinkedConnectorElement(connector2);
        result.ConnectorCount = 2;
        return new PrototypeGeometry(
            bend, end1, end2, connector1, connector2);
    }

    private static ReferencePlane FindReferencePlane(
        Document document,
        string name) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(ReferencePlane))
            .Cast<ReferencePlane>()
            .FirstOrDefault(
                plane => plane.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Reference plane '{name}' was not found.");

    private static FamilySymbol LoadSingleNestedSymbol(
        Document document,
        NestedArtifact nested)
    {
        bool loaded = document.LoadFamily(
            nested.Path,
            new OverwriteFamilyLoadOptions(),
            out Family loadedFamily);
        if (!loaded && loadedFamily is null)
            throw new InvalidOperationException(
                $"The one-Type nested family '{nested.FamilyName}' could not be loaded.");
        IReadOnlyList<FamilySymbol> symbols = loadedFamily.GetFamilySymbolIds()
            .Select(id => document.GetElement(id))
            .OfType<FamilySymbol>()
            .ToList();
        if (symbols.Count != 1)
            throw new InvalidOperationException(
                $"Nested family '{nested.FamilyName}' must expose one Type; found {symbols.Count}.");
        return symbols[0];
    }

    private static CurveLoop CreateElbowPath(double l, double z)
    {
        XYZ p0 = new(l, 0, 0);
        XYZ p1 = new(z, 0, 0);
        // Quarter circle centred at (Z,0,-Z).  This makes the arc tangent to
        // the incoming -X leg at p1 and to the outgoing -Z leg at p2.
        XYZ pm = new(z - z / Math.Sqrt(2), 0, -z + z / Math.Sqrt(2));
        XYZ p2 = new(0, 0, -z);
        XYZ p3 = new(0, 0, -l);
        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Arc.Create(p1, p2, pm));
        loop.Append(Line.CreateBound(p2, p3));
        return loop;
    }

    private static CurveLoop CreateCircleLoop(
        XYZ center,
        XYZ normal,
        double radius)
    {
        (XYZ x, XYZ y) = ProfileAxes(normal);
        var loop = new CurveLoop();
        loop.Append(Arc.Create(center, radius, 0, Math.PI, x, y));
        loop.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, x, y));
        return loop;
    }

    private static void AssociateNestedParameters(
        FamilyManager manager,
        FamilyInstance instance,
        ParentParameters p)
    {
        IReadOnlyDictionary<string, FamilyParameter> mapping =
            new Dictionary<string, FamilyParameter>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["FT_LE_ZZ_d"] = p.D,
                ["FT_LE_ZZ_Pos"] = p.L,
                ["FT_LE_ZZ_EndL"] = p.EndL,
                ["FT_LE_ZZ_SocketOD"] = p.SocketOD,
                ["FT_LE_ZZ_RingOD"] = p.RingOD,
                ["FT_LE_ZZ_BeadW"] = p.BeadW
            };
        foreach ((string nestedName, FamilyParameter parentParameter) in mapping)
        {
            Parameter nestedParameter = instance.LookupParameter(nestedName)
                ?? throw new InvalidOperationException(
                    $"Nested Mapress parameter '{nestedName}' was not found.");
            if (!manager.CanElementParameterBeAssociated(nestedParameter))
                throw new InvalidOperationException(
                    $"Nested Mapress parameter '{nestedName}' cannot be associated.");
            manager.AssociateElementParameterToFamilyParameter(
                nestedParameter,
                parentParameter);
        }
    }

    private static void ValidateCatalogFamily(
        Document document,
        FamilyManager manager,
        ParentParameters p,
        IReadOnlyList<CatalogRow> rows,
        IReadOnlyDictionary<int, FamilyType> types,
        CatalogRow selected,
        string lookupTable,
        PrototypeGeometry geometry,
        PipeFittingBuilderResult result)
    {
        document.Regenerate();
        if (document.OwnerFamily.FamilyCategory.Id.Value
            != (long)BuiltInCategory.OST_PipeFitting)
            throw new InvalidOperationException(
                "Catalog Family category is not Pipe Fittings.");
        if (manager.Types.Cast<FamilyType>().Count() != rows.Count)
            throw new InvalidOperationException(
                $"Catalog Family must contain {rows.Count} parent Types.");
        FamilySizeTableManager? sizeTableManager =
            FamilySizeTableManager.GetFamilySizeTableManager(
                document,
                document.OwnerFamily.Id);
        if (sizeTableManager is null
            || !sizeTableManager.GetAllSizeTableNames().Contains(lookupTable))
            throw new InvalidOperationException(
                "The embedded Geberit lookup table is unavailable.");
        if (new FilteredElementCollector(document)
                .OfClass(typeof(FamilyInstance))
                .OfType<FamilyInstance>()
                .Count() != 2)
            throw new InvalidOperationException(
                "Catalog Family must contain exactly two nested Mapress ends.");
        if (new FilteredElementCollector(document)
                .OfClass(typeof(Sweep))
                .GetElementCount() != 1)
            throw new InvalidOperationException(
                "Catalog Family must contain exactly one editable native Sweep.");
        if (new FilteredElementCollector(document)
                .OfClass(typeof(FreeFormElement))
                .GetElementCount() != 0)
            throw new InvalidOperationException(
                "Catalog Family still contains a non-editable FreeFormElement.");
        List<ConnectorElement> connectors = new FilteredElementCollector(document)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .ToList();
        if (connectors.Count != 2)
            throw new InvalidOperationException(
                $"Catalog Family must contain 2 pipe connectors; found {connectors.Count}.");

        foreach (CatalogRow row in rows)
        {
            manager.CurrentType = types[row.DN];
            document.Regenerate();
            AssertMm(manager, p.DN, row.DN, $"{row.TypeName} DN selector");
            AssertMm(manager, p.D, row.D, $"{row.TypeName} d");
            AssertMm(manager, p.L, row.L, $"{row.TypeName} L");
            AssertMm(manager, p.Z, row.Z, $"{row.TypeName} Z");
            AssertMm(
                manager,
                p.EndL,
                row.EndLength,
                $"{row.TypeName} Mapress end length");
            AssertMm(
                manager,
                p.ConnD,
                row.D,
                $"{row.TypeName} connector diameter");
            foreach (ConnectorElement connector in connectors)
            {
                Parameter diameter = connector.get_Parameter(
                    BuiltInParameter.CONNECTOR_DIAMETER)
                    ?? throw new InvalidOperationException(
                        "Connector Diameter is unavailable.");
                double actual = UnitUtils.ConvertFromInternalUnits(
                    diameter.AsDouble(),
                    UnitTypeId.Millimeters);
                if (Math.Abs(actual - row.D) > .1)
                    throw new InvalidOperationException(
                        $"{row.TypeName}: connector diameter is {actual:0.###} mm; "
                        + $"expected catalog d{row.D:0.###}.");
            }
            ValidateSweepPathDatums(geometry.Bend, row);
            Parameter end1Position = geometry.End1.LookupParameter(
                "FT_LE_ZZ_Pos")
                ?? throw new InvalidOperationException(
                    $"{row.TypeName}: horizontal nested position parameter is missing.");
            Parameter end2Position = geometry.End2.LookupParameter(
                "FT_LE_ZZ_Pos")
                ?? throw new InvalidOperationException(
                    $"{row.TypeName}: vertical nested position parameter is missing.");
            if (Math.Abs(end1Position.AsDouble() - Mm(row.L)) > Mm(.2)
                || Math.Abs(end2Position.AsDouble() - Mm(row.L)) > Mm(.2))
                throw new InvalidOperationException(
                    $"{row.TypeName}: nested press-end geometry is not driven to catalog L.");
        }
        manager.CurrentType = types[selected.DN];
        document.Regenerate();
        result.ActiveTypeName = selected.TypeName;
        result.AcceptanceChecks.Add("Official articles 30102-31111 produce 10 DN12-DN100 Types");
        result.AcceptanceChecks.Add($"Embedded lookup table '{lookupTable}' drives d, L and Z");
        result.AcceptanceChecks.Add("All 10 Types flex and regenerate before save");
        result.AcceptanceChecks.Add("Nested Mapress end has one Type and five associated geometry inputs");
        result.AcceptanceChecks.Add("Each nested end is one editable Revolution locked to named reference planes");
        result.AcceptanceChecks.Add("Two orientation-specific nested ends stay at the origin; associated L drives their internal end-face datums");
        result.AcceptanceChecks.Add("The hollow 90-degree core is one editable native Sweep; no FreeForm geometry remains");
        result.AcceptanceChecks.Add("Two linked pipe connectors are face-hosted on orthogonal bend ends");
        result.AcceptanceChecks.Add("Both connector diameters equal full catalog d for every Type");
        result.AcceptanceChecks.Add("Sweep path and nested internal geometry reach both lookup-driven L endpoints without parent alignments");
        result.AcceptanceChecks.Add("Family regenerates before save without an exception");
    }

    private static void AssertMm(
        FamilyManager manager,
        FamilyParameter parameter,
        double expected,
        string label)
    {
        double actual = UnitUtils.ConvertFromInternalUnits(
            manager.CurrentType?.AsDouble(parameter) ?? 0,
            UnitTypeId.Millimeters);
        if (Math.Abs(actual - expected) > 0.1)
            throw new InvalidOperationException(
                $"{label} is {actual:0.###} mm; expected catalog {expected:0.###} mm.");
    }

    private static void ValidateSweepPathDatums(
        Sweep sweep,
        CatalogRow row)
    {
        IReadOnlyList<Line> lines = sweep.PathSketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .ToList();
        Line xLine = lines.FirstOrDefault(line =>
                Math.Abs(line.Direction.Normalize().DotProduct(
                    XYZ.BasisX)) > .99)
            ?? throw new InvalidOperationException(
                $"{row.TypeName}: native Sweep path has no horizontal leg.");
        Line zLine = lines.FirstOrDefault(line =>
                Math.Abs(line.Direction.Normalize().DotProduct(
                    XYZ.BasisZ)) > .99)
            ?? throw new InvalidOperationException(
                $"{row.TypeName}: native Sweep path has no vertical leg.");

        double xMaximum = Math.Max(
            xLine.GetEndPoint(0).X,
            xLine.GetEndPoint(1).X);
        double xMinimum = Math.Min(
            xLine.GetEndPoint(0).X,
            xLine.GetEndPoint(1).X);
        double zMaximum = Math.Max(
            zLine.GetEndPoint(0).Z,
            zLine.GetEndPoint(1).Z);
        double zMinimum = Math.Min(
            zLine.GetEndPoint(0).Z,
            zLine.GetEndPoint(1).Z);
        const double toleranceMm = .2;
        bool valid = Math.Abs(xMaximum - Mm(row.L)) <= Mm(toleranceMm)
            && Math.Abs(xMinimum - Mm(row.Z)) <= Mm(toleranceMm)
            && Math.Abs(zMaximum + Mm(row.Z)) <= Mm(toleranceMm)
            && Math.Abs(zMinimum + Mm(row.L)) <= Mm(toleranceMm);
        if (valid) return;

        double ToMm(double value) =>
            UnitUtils.ConvertFromInternalUnits(
                value,
                UnitTypeId.Millimeters);
        throw new InvalidOperationException(
            $"{row.TypeName}: native Sweep path does not match catalog datums. "
            + $"Actual X=[{ToMm(xMinimum):0.###}, {ToMm(xMaximum):0.###}] mm, "
            + $"Z=[{ToMm(zMinimum):0.###}, {ToMm(zMaximum):0.###}] mm; "
            + $"expected X=[{row.Z:0.###}, {row.L:0.###}] mm and "
            + $"Z=[-{row.L:0.###}, -{row.Z:0.###}] mm.");
    }

    private static void CreateReferenceSkeleton(
        Document document,
        FamilyManager manager,
        ParentParameters p,
        CatalogRow row)
    {
        View front = FindFamilyView(document, XYZ.BasisY);
        double l = Mm(row.L);
        double z = Mm(row.Z);
        ReferencePlane xL = CreateReferencePlane(
            document, front, "FT_X_L", new XYZ(l, 0, -l * .4),
            new XYZ(l, 0, l * .4));
        ReferencePlane xZ = CreateReferencePlane(
            document, front, "FT_X_Z", new XYZ(z, 0, -l * .4),
            new XYZ(z, 0, l * .4));
        ReferencePlane zL = CreateReferencePlane(
            document, front, "FT_Z_L", new XYZ(-l * .4, 0, -l),
            new XYZ(l * .4, 0, -l));
        ReferencePlane zZ = CreateReferencePlane(
            document, front, "FT_Z_Z", new XYZ(-l * .4, 0, -z),
            new XYZ(l * .4, 0, -z));
        // Use tool-owned datum references instead of guessing which default
        // template plane is Left/Right or Front/Back.  Revit can return an
        // incompatible default reference even when its geometric normal
        // looks correct, causing NewLinearDimension to reject the array.
        ReferencePlane centerX = CreateReferencePlane(
            document, front, "FT_X_0", new XYZ(0, 0, -l),
            new XYZ(0, 0, l));
        ReferencePlane centerZ = CreateReferencePlane(
            document, front, "FT_Z_0", new XYZ(-l, 0, 0),
            new XYZ(l, 0, 0));
        // Newly created datum references are not dimension-ready until the
        // Family document has regenerated.
        document.Regenerate();
        Dimension l1 = CreateLinearDimensionAtStage(
            "horizontal L",
            document, front, [centerX.GetReference(), xL.GetReference()],
            new XYZ(0, 0, Mm(35)), new XYZ(l, 0, Mm(35)));
        l1.FamilyLabel = p.L;
        Dimension l2 = CreateLinearDimensionAtStage(
            "vertical L",
            document, front, [centerZ.GetReference(), zL.GetReference()],
            new XYZ(-Mm(35), 0, 0), new XYZ(-Mm(35), 0, -l));
        l2.FamilyLabel = p.L;
        Dimension z1 = CreateLinearDimensionAtStage(
            "horizontal Z",
            document, front, [centerX.GetReference(), xZ.GetReference()],
            new XYZ(0, 0, Mm(28)), new XYZ(z, 0, Mm(28)));
        z1.FamilyLabel = p.Z;
        Dimension z2 = CreateLinearDimensionAtStage(
            "vertical Z",
            document, front, [centerZ.GetReference(), zZ.GetReference()],
            new XYZ(-Mm(28), 0, 0), new XYZ(-Mm(28), 0, -z));
        z2.FamilyLabel = p.Z;
    }

    private static ReferencePlane CreateReferencePlane(
        Document document,
        View view,
        string name,
        XYZ bubble,
        XYZ free)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            bubble,
            free,
            XYZ.BasisY,
            view);
        plane.Name = name;
        Parameter? isReference = plane.get_Parameter(
            BuiltInParameter.ELEM_IS_REFERENCE);
        if (isReference is not null && !isReference.IsReadOnly)
            isReference.Set(1); // Strong Reference
        return plane;
    }

    private static Dimension CreateLinearDimension(
        Document document,
        View view,
        IReadOnlyList<Reference> references,
        XYZ start,
        XYZ end)
    {
        var array = new ReferenceArray();
        foreach (Reference reference in references) array.Append(reference);
        return document.FamilyCreate.NewLinearDimension(
            view,
            Line.CreateBound(start, end),
            array);
    }

    private static Dimension CreateLinearDimensionAtStage(
        string stage,
        Document document,
        View view,
        IReadOnlyList<Reference> references,
        XYZ start,
        XYZ end)
    {
        try
        {
            return CreateLinearDimension(
                document,
                view,
                references,
                start,
                end);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"reference dimension '{stage}' failed: {exception.Message}",
                exception);
        }
    }

    private static Reference FindPlanarFace(
        Element element,
        XYZ normal,
        XYZ origin)
    {
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        PlanarFace? face = element.get_Geometry(options)
            .OfType<Solid>()
            .Where(solid => solid.Faces.Size > 0)
            .SelectMany(solid => solid.Faces.Cast<Face>())
            .OfType<PlanarFace>()
            .Where(candidate =>
                candidate.FaceNormal.Normalize().DotProduct(normal.Normalize()) > .99)
            .OrderBy(candidate => candidate.Origin.DistanceTo(origin))
            .FirstOrDefault();
        return face?.Reference
            ?? throw new InvalidOperationException(
                "A planar elbow end face could not be resolved for a pipe connector.");
    }

    private static void AssociateConnectorDiameter(
        FamilyManager manager,
        ConnectorElement connector,
        FamilyParameter diameter)
    {
        Parameter parameter = connector.get_Parameter(
            BuiltInParameter.CONNECTOR_DIAMETER)
            ?? throw new InvalidOperationException(
                "Pipe connector Diameter parameter is unavailable.");
        manager.AssociateElementParameterToFamilyParameter(parameter, diameter);
    }

    private static NestedRevolveReferences CreateNestedRevolveReferenceSkeleton(
        Document document,
        FamilyManager manager,
        XYZ axisDirection,
        FamilyParameter position,
        FamilyParameter endLength,
        FamilyParameter innerRadius,
        FamilyParameter socketRadius,
        FamilyParameter mouthRadius,
        FamilyParameter ringRadius)
    {
        XYZ axis = axisDirection.Normalize();
        XYZ radial = Math.Abs(axis.DotProduct(XYZ.BasisX)) > .99
            ? XYZ.BasisZ
            : XYZ.BasisX;
        XYZ viewDirection = axis.CrossProduct(radial).Normalize();
        View profileView = FindFamilyView(document, viewDirection);
        double endPosition = CurrentValue(manager, position, Mm(47));
        double length = CurrentValue(manager, endLength, Mm(21));
        double extent = Math.Max(Mm(38), Math.Max(length * 1.8, endPosition * 1.15));

        ReferencePlane start = CreateProfileReferencePlane(
            document, profileView, viewDirection, radial,
            axis * (endPosition - length), extent, "RP_End_Start");
        ReferencePlane end = CreateProfileReferencePlane(
            document, profileView, viewDirection, radial,
            axis * endPosition, extent, "RP_End_Face");
        ReferencePlane origin = CreateProfileReferencePlane(
            document, profileView, viewDirection, radial,
            XYZ.Zero, extent, "RP_End_Origin");
        ReferencePlane axisPlane = CreateProfileReferencePlane(
            document, profileView, viewDirection, axis,
            XYZ.Zero, extent, "RP_Revolve_Axis");
        ReferencePlane inner = CreateProfileReferencePlane(
            document, profileView, viewDirection, axis,
            radial * CurrentValue(manager, innerRadius, Mm(11)),
            extent, "RP_Inner_R");
        ReferencePlane socket = CreateProfileReferencePlane(
            document, profileView, viewDirection, axis,
            radial * CurrentValue(manager, socketRadius, Mm(14)),
            extent, "RP_Socket_R");
        ReferencePlane mouth = CreateProfileReferencePlane(
            document, profileView, viewDirection, axis,
            radial * CurrentValue(manager, mouthRadius, Mm(15.5)),
            extent, "RP_Mouth_R");
        ReferencePlane ring = CreateProfileReferencePlane(
            document, profileView, viewDirection, axis,
            radial * CurrentValue(manager, ringRadius, Mm(16)),
            extent, "RP_Ring_R");
        document.Regenerate();

        Dimension positionDimension = CreateLinearDimensionAtStage(
            "nested revolve position L",
            document,
            profileView,
            [origin.GetReference(), end.GetReference()],
            radial * extent * .88,
            axis * endPosition + radial * extent * .88);
        positionDimension.FamilyLabel = position;
        Dimension axial = CreateLinearDimensionAtStage(
            "nested revolve EndL",
            document,
            profileView,
            [start.GetReference(), end.GetReference()],
            axis * (endPosition - length) + radial * extent * .75,
            axis * endPosition + radial * extent * .75);
        axial.FamilyLabel = endLength;
        CreateRadiusReferenceDimension(
            document, profileView, axisPlane, inner,
            innerRadius, axis, radial, endPosition - length * .70, "nested inner radius");
        CreateRadiusReferenceDimension(
            document, profileView, axisPlane, socket,
            socketRadius, axis, radial, endPosition - length * .88, "nested socket radius");
        CreateRadiusReferenceDimension(
            document, profileView, axisPlane, mouth,
            mouthRadius, axis, radial, endPosition - length * 1.06, "nested mouth radius");
        CreateRadiusReferenceDimension(
            document, profileView, axisPlane, ring,
            ringRadius, axis, radial, endPosition - length * 1.24, "nested ring radius");
        document.Regenerate();
        return new NestedRevolveReferences(
            start, end, axisPlane, inner, socket, mouth, ring);
    }

    private static ReferencePlane CreateProfileReferencePlane(
        Document document,
        View view,
        XYZ cutVector,
        XYZ lineDirection,
        XYZ origin,
        double extent,
        string name)
    {
        ReferencePlane plane = document.FamilyCreate.NewReferencePlane(
            origin - lineDirection * extent,
            origin + lineDirection * extent,
            cutVector,
            view);
        plane.Name = name;
        Parameter? isReference = plane.get_Parameter(
            BuiltInParameter.ELEM_IS_REFERENCE);
        if (isReference is not null && !isReference.IsReadOnly)
            isReference.Set(1); // Strong Reference: exposed to the parent family.
        return plane;
    }

    private static void CreateRadiusReferenceDimension(
        Document document,
        View view,
        ReferencePlane axisPlane,
        ReferencePlane radiusPlane,
        FamilyParameter parameter,
        XYZ axis,
        XYZ radial,
        double axialOffset,
        string stage)
    {
        XYZ start = axis * axialOffset;
        XYZ end = start + radial * Mm(40);
        Dimension dimension = CreateLinearDimensionAtStage(
            stage,
            document,
            view,
            [axisPlane.GetReference(), radiusPlane.GetReference()],
            start,
            end);
        dimension.FamilyLabel = parameter;
    }

    private static Revolution CreateNativeMapressRevolution(
        Document document,
        FamilyManager manager,
        XYZ axisDirection,
        ElementId materialId,
        NestedRevolveReferences references,
        FamilyParameter x0,
        FamilyParameter rear1,
        FamilyParameter rear2,
        FamilyParameter bell0,
        FamilyParameter bell1,
        FamilyParameter bell2,
        FamilyParameter bell3,
        FamilyParameter bell4,
        FamilyParameter bell5,
        FamilyParameter x1,
        FamilyParameter rearOD0,
        FamilyParameter rearOD1,
        FamilyParameter socketOD,
        FamilyParameter mouthOD1,
        FamilyParameter mouthOD2,
        FamilyParameter mouthOD3,
        FamilyParameter ringOD,
        FamilyParameter innerDiameter)
    {
        XYZ axis = axisDirection.Normalize();
        XYZ radial = Math.Abs(axis.DotProduct(XYZ.BasisX)) > .99
            ? XYZ.BasisZ
            : XYZ.BasisX;
        XYZ normal = axis.CrossProduct(radial).Normalize();
        XYZ Point(FamilyParameter axial, FamilyParameter diameter) =>
            axis * SignedCurrentValue(manager, axial, 0)
            + radial * (CurrentValue(manager, diameter, Mm(22)) / 2);

        XYZ innerStart = Point(x0, innerDiameter);
        XYZ innerEnd = Point(x1, innerDiameter);
        List<XYZ> points =
        [
            innerStart,
            innerEnd,
            Point(x1, mouthOD3),
            Point(bell5, mouthOD3),
            Point(bell4, ringOD),
            Point(bell3, ringOD),
            Point(bell2, mouthOD2),
            Point(bell1, mouthOD1),
            Point(bell0, socketOD),
            Point(rear2, socketOD),
            Point(rear1, rearOD1),
            Point(x0, rearOD0)
        ];
        var outline = new CurveArray();
        for (int i = 0; i < points.Count; i++)
        {
            XYZ a = points[i];
            XYZ b = points[(i + 1) % points.Count];
            if (a.DistanceTo(b) < Mm(.05))
                throw new InvalidOperationException(
                    $"Mapress revolve profile segment {i + 1} is too short.");
            outline.Append(Line.CreateBound(a, b));
        }
        var profile = new CurveArrArray();
        profile.Append(outline);
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(normal, XYZ.Zero));
        double axial0 = SignedCurrentValue(manager, x0, Mm(26));
        double axial1 = SignedCurrentValue(manager, x1, Mm(47));
        double span = Math.Max(Math.Abs(axial1 - axial0), Mm(8));
        double axisStart = Math.Min(axial0, axial1) - span * .35;
        double axisEnd = Math.Max(axial0, axial1) + span * .35;
        Line revolveAxis = Line.CreateBound(
            axis * axisStart,
            axis * axisEnd);
        Revolution revolution = document.FamilyCreate.NewRevolution(
            true,
            profile,
            sketchPlane,
            revolveAxis,
            0,
            Math.PI * 2);
        SetFormMaterial(revolution, materialId);
        document.Regenerate();

        View profileView = FindFamilyView(document, normal);
        List<Line> lines = revolution.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.Start, true, SignedCurrentValue(manager, x0, -Mm(21)));
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.End, true, SignedCurrentValue(manager, x1, 0));
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.InnerRadius, false,
            CurrentValue(manager, innerDiameter, Mm(22)) / 2);
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.SocketRadius, false,
            CurrentValue(manager, socketOD, Mm(28)) / 2);
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.MouthRadius, false,
            CurrentValue(manager, mouthOD3, Mm(31)) / 2);
        AlignClosestProfileLine(
            document, profileView, lines, axis, radial,
            references.RingRadius, false,
            CurrentValue(manager, ringOD, Mm(32)) / 2);
        document.Regenerate();
        return revolution;
    }

    private static void AlignClosestProfileLine(
        Document document,
        View view,
        IReadOnlyList<Line> lines,
        XYZ axis,
        XYZ radial,
        ReferencePlane referencePlane,
        bool lineRunsRadially,
        double coordinate)
    {
        XYZ expectedDirection = lineRunsRadially ? radial : axis;
        Line? target = lines
            .Where(line =>
                Math.Abs(line.Direction.Normalize().DotProduct(
                    expectedDirection.Normalize())) > .99)
            .OrderBy(line =>
            {
                XYZ midpoint = line.Evaluate(.5, true);
                double actual = lineRunsRadially
                    ? midpoint.DotProduct(axis)
                    : midpoint.DotProduct(radial);
                return Math.Abs(actual - coordinate);
            })
            .FirstOrDefault();
        if (target?.Reference is null)
            throw new InvalidOperationException(
                $"A Revolution profile line could not be matched to '{referencePlane.Name}'.");
        document.FamilyCreate.NewAlignment(
            view,
            referencePlane.GetReference(),
            target.Reference);
    }

    private static Sweep CreateNativeHollowElbowSweep(
        Document document,
        FamilyManager manager,
        ParentParameters p,
        CatalogRow row,
        ElementId materialId)
    {
        double bodyRadius = CurrentValue(manager, p.BodyOD, Mm(row.D + 3)) / 2;
        double boreRadius = CurrentValue(manager, p.BoreD, Mm(row.D - 3)) / 2;
        CurveLoop pathLoop = CreateElbowPath(Mm(row.L), Mm(row.Z));
        var path = new CurveArray();
        foreach (Curve curve in pathLoop) path.Append(curve);
        SketchPlane pathPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero));

        XYZ px = XYZ.BasisX;
        XYZ py = XYZ.BasisY;
        var loops = new CurveArrArray();
        loops.Append(CreateCircleArray(bodyRadius, px, py));
        loops.Append(CreateCircleArray(boreRadius, px, py));
        SweepProfile profile =
            document.Application.Create.NewCurveLoopsProfile(loops);
        Sweep sweep = document.FamilyCreate.NewSweep(
            true,
            path,
            pathPlane,
            profile,
            0,
            ProfilePlaneLocation.Start);
        SetFormMaterial(sweep, materialId);
        document.Regenerate();

        View endView = FindFamilyView(document, XYZ.BasisX);
        List<Arc> arcs = sweep.ProfileSketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Arc>()
            .Where(arc => arc.Reference is not null)
            .OrderByDescending(arc => arc.Radius)
            .ToList();
        if (arcs.Count < 4)
            throw new InvalidOperationException(
                "The native hollow Sweep profile is incomplete.");
        Dimension outer = document.FamilyCreate.NewDiameterDimension(
            endView,
            arcs.First().Reference,
            new XYZ(Mm(row.L), 0, bodyRadius * 1.35));
        outer.FamilyLabel = p.BodyOD;
        Dimension inner = document.FamilyCreate.NewDiameterDimension(
            endView,
            arcs.Last().Reference,
            new XYZ(Mm(row.L), 0, boreRadius * .65));
        inner.FamilyLabel = p.BoreD;
        ConstrainSweepPathToCatalogDatums(
            document,
            sweep,
            p,
            row);
        document.Regenerate();
        return sweep;
    }

    private static void ConstrainSweepPathToCatalogDatums(
        Document document,
        Sweep sweep,
        ParentParameters p,
        CatalogRow row)
    {
        View front = FindFamilyView(document, XYZ.BasisY);
        List<Line> pathLines = sweep.PathSketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        Line xLine = pathLines.FirstOrDefault(
                line => Math.Abs(line.Direction.Normalize().DotProduct(
                    XYZ.BasisX)) > .99)
            ?? throw new InvalidOperationException(
                "The Sweep path has no horizontal catalog leg.");
        Line zLine = pathLines.FirstOrDefault(
                line => Math.Abs(line.Direction.Normalize().DotProduct(
                    XYZ.BasisZ)) > .99)
            ?? throw new InvalidOperationException(
                "The Sweep path has no vertical catalog leg.");

        Dimension xL = DimensionCurveEndpoint(
            document,
            front,
            FindReferencePlane(document, "FT_X_0"),
            xLine,
            endpoint => endpoint.X,
            useMaximum: true,
            new XYZ(0, 0, Mm(row.L + 18)),
            new XYZ(Mm(row.L), 0, Mm(row.L + 18)),
            "Sweep X=L");
        xL.FamilyLabel = p.L;
        Dimension xZ = DimensionCurveEndpoint(
            document,
            front,
            FindReferencePlane(document, "FT_X_0"),
            xLine,
            endpoint => endpoint.X,
            useMaximum: false,
            new XYZ(0, 0, Mm(row.L + 12)),
            new XYZ(Mm(row.Z), 0, Mm(row.L + 12)),
            "Sweep X=Z");
        xZ.FamilyLabel = p.Z;
        Dimension zL = DimensionCurveEndpoint(
            document,
            front,
            FindReferencePlane(document, "FT_Z_0"),
            zLine,
            endpoint => endpoint.Z,
            useMaximum: false,
            new XYZ(-Mm(row.L + 18), 0, 0),
            new XYZ(-Mm(row.L + 18), 0, -Mm(row.L)),
            "Sweep Z=-L");
        zL.FamilyLabel = p.L;
        Dimension zZ = DimensionCurveEndpoint(
            document,
            front,
            FindReferencePlane(document, "FT_Z_0"),
            zLine,
            endpoint => endpoint.Z,
            useMaximum: true,
            new XYZ(-Mm(row.L + 12), 0, 0),
            new XYZ(-Mm(row.L + 12), 0, -Mm(row.Z)),
            "Sweep Z=-Z");
        zZ.FamilyLabel = p.Z;
    }

    private static Dimension DimensionCurveEndpoint(
        Document document,
        View view,
        ReferencePlane plane,
        Curve curve,
        Func<XYZ, double> coordinate,
        bool useMaximum,
        XYZ dimensionStart,
        XYZ dimensionEnd,
        string label)
    {
        int endpoint = useMaximum
            ? (coordinate(curve.GetEndPoint(0))
               >= coordinate(curve.GetEndPoint(1)) ? 0 : 1)
            : (coordinate(curve.GetEndPoint(0))
               <= coordinate(curve.GetEndPoint(1)) ? 0 : 1);
        Reference reference = curve.GetEndPointReference(endpoint)
            ?? throw new InvalidOperationException(
                $"Sweep endpoint reference '{label}' is unavailable.");
        return CreateLinearDimensionAtStage(
            label,
            document,
            view,
            [plane.GetReference(), reference],
            dimensionStart,
            dimensionEnd);
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
        double outerRadius = CurrentValue(manager, outerDiameter, Mm(27)) / 2;
        double innerRadius = Math.Min(
            CurrentValue(manager, innerDiameter, Mm(22)) / 2,
            outerRadius - Mm(.5));
        (XYZ px, XYZ py) = ProfileAxes(axis);
        var profile = new CurveArrArray();
        profile.Append(CreateCircleArray(outerRadius, px, py));
        profile.Append(CreateCircleArray(innerRadius, px, py));
        SketchPlane sketchPlane = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(axis, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketchPlane,
            Mm(10));
        AssociateExtrusionBounds(manager, extrusion, start, end);
        Parameter? material = extrusion.get_Parameter(
            BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
            material.Set(materialId);
        document.Regenerate();
        List<Arc> arcs = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(array => array.Cast<Curve>())
            .OfType<Arc>()
            .Where(arc => arc.Reference is not null)
            .OrderByDescending(arc => arc.Radius)
            .ToList();
        Dimension outer = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            arcs.First().Reference,
            new XYZ(0, outerRadius * 1.35, 0));
        outer.FamilyLabel = outerDiameter;
        Dimension inner = document.FamilyCreate.NewDiameterDimension(
            dimensionView,
            arcs.Last().Reference,
            new XYZ(0, innerRadius * .65, 0));
        inner.FamilyLabel = innerDiameter;
        return extrusion;
    }

    private static CurveArray CreateCircleArray(
        double radius,
        XYZ x,
        XYZ y)
    {
        var circle = new CurveArray();
        circle.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, x, y));
        circle.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, x, y));
        return circle;
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
        manager.AssociateElementParameterToFamilyParameter(startParameter, start);
        manager.AssociateElementParameterToFamilyParameter(endParameter, end);
    }

    private static FamilyParameter EnsureLengthParameter(
        FamilyManager manager,
        string name,
        bool isInstance = false)
    {
        FamilyParameter? existing = manager.Parameters.Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return manager.AddParameter(
            name,
            BuiltInParameterGroup.PG_GEOMETRY,
            ParameterType.Length,
            isInstance);
#else
        return manager.AddParameter(
            name,
            GroupTypeId.Geometry,
            SpecTypeId.Length,
            isInstance);
#endif
    }

    private static FamilyParameter EnsureAngleParameter(
        FamilyManager manager,
        string name)
    {
        FamilyParameter? existing = manager.Parameters.Cast<FamilyParameter>()
            .FirstOrDefault(parameter =>
                parameter.Definition.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        return manager.AddParameter(
            name,
            BuiltInParameterGroup.PG_GEOMETRY,
            ParameterType.Angle,
            false);
#else
        return manager.AddParameter(
            name,
            GroupTypeId.Geometry,
            SpecTypeId.Angle,
            false);
#endif
    }

    private static FamilyParameter EnsureFormulaLengthParameter(
        FamilyManager manager,
        string name,
        string formula,
        bool isInstance = false)
    {
        FamilyParameter parameter = EnsureLengthParameter(
            manager,
            name,
            isInstance);
        if (!string.Equals(parameter.Formula, formula, StringComparison.Ordinal))
            manager.SetFormula(parameter, formula);
        return parameter;
    }

    private static void EnsureBootstrapType(
        FamilyManager manager,
        string typeName)
    {
        List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
        FamilyType? type = types.FirstOrDefault(item =>
            item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (type is null && types.Count == 0)
            type = manager.NewType(typeName);
        else if (type is null)
        {
            manager.CurrentType = types[0];
            manager.RenameCurrentType(typeName);
            type = manager.CurrentType;
        }
        FamilyType desired = manager.Types.Cast<FamilyType>()
            .Single(item =>
                item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        foreach (FamilyType extra in manager.Types.Cast<FamilyType>()
                     .Where(item => !item.Name.Equals(
                         typeName,
                         StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            manager.CurrentType = extra;
            manager.DeleteCurrentType();
        }
        manager.CurrentType = desired;
    }

    private static View FindFamilyView(Document document, XYZ direction)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => !view.IsTemplate && view.ViewType != ViewType.ThreeD)
            .FirstOrDefault(view =>
                Math.Abs(
                    view.ViewDirection.Normalize().DotProduct(
                        direction.Normalize())) > .99)
            ?? throw new InvalidOperationException(
                "The Generic Model template is missing a required orthographic view.");
    }

    private static ReferencePlane? FindCenterReferencePlane(
        Document document,
        XYZ normal)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(ReferencePlane))
            .Cast<ReferencePlane>()
            .FirstOrDefault(plane =>
            {
                try
                {
                    Plane geometry = plane.GetPlane();
                    return Math.Abs(
                               geometry.Normal.Normalize().DotProduct(
                                   normal.Normalize())) > .99
                           && Math.Abs(
                               geometry.Origin.DotProduct(normal)) < 1e-6;
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

    private static double SignedCurrentValue(
        FamilyManager manager,
        FamilyParameter parameter,
        double fallback)
    {
        try
        {
            return manager.CurrentType?.AsDouble(parameter) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SetFormMaterial(
        Element form,
        ElementId materialId)
    {
        Parameter? material = form.get_Parameter(
            BuiltInParameter.MATERIAL_ID_PARAM);
        if (material is not null && !material.IsReadOnly)
            material.Set(materialId);
    }

    private static (XYZ X, XYZ Y) ProfileAxes(XYZ normal)
    {
        if (normal.IsAlmostEqualTo(XYZ.BasisX))
            return (XYZ.BasisY, XYZ.BasisZ);
        if (normal.IsAlmostEqualTo(XYZ.BasisZ)
            || normal.IsAlmostEqualTo(-XYZ.BasisZ))
            return (XYZ.BasisX, XYZ.BasisY);
        throw new NotSupportedException(
            "Only X- and Z-normal circular profiles are supported.");
    }

    private static void SetPipeFittingCategory(Document document)
    {
        document.OwnerFamily.FamilyCategory =
            document.Settings.Categories.get_Item(
                BuiltInCategory.OST_PipeFitting);
        Parameter? partType = document.OwnerFamily.get_Parameter(
            BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
        if (partType is not null && !partType.IsReadOnly)
            partType.Set((int)PartType.Elbow);
    }

    private static void DisableAlwaysVertical(Document document)
    {
        Parameter? parameter = document.OwnerFamily.get_Parameter(
            BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
        if (parameter is null)
            return;
        if (parameter.IsReadOnly)
            throw new InvalidOperationException(
                "The Generic Model template does not allow Always vertical to be disabled.");
        parameter.Set(0);
        document.Regenerate();
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
        material.Color = new Autodesk.Revit.DB.Color(155, 160, 166);
        material.Transparency = 0;
        return material.Id;
    }

    private static double Mm(double value) =>
        UnitUtils.ConvertToInternalUnits(
            value,
            UnitTypeId.Millimeters);

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder)
                && Directory.Exists(folder)
                && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
            // The generated parent Family is valid even if Revit keeps a
            // temporary nested file handle alive until shutdown.
        }
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
