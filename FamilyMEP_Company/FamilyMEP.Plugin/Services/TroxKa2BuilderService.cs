#if REVIT2020 || REVIT2021 || REVIT2022
global using TroxParameterType = Autodesk.Revit.DB.ParameterType;
#else
global using TroxParameterType = Autodesk.Revit.DB.ForgeTypeId;
#endif
using System.Globalization;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Services;

/// <summary>
/// Catalog-parametric authoring for the TROX KA2-EU fire damper.
/// Dimension source: TROX KA2-EU Product Data 05/2025, dimension page.
/// </summary>
internal static class TroxKa2BuilderService
{
    private sealed record CatalogRow(
        int Key,
        int Width,
        int Height,
        int Length,
        int L1,
        int L2)
    {
        public string TypeName => $"B{Width}xH{Height}";
    }

    private sealed record Parameters(
        FamilyParameter BSelector,
        FamilyParameter HSelector,
        FamilyParameter B,
        FamilyParameter H,
        FamilyParameter L,
        FamilyParameter L1,
        FamilyParameter L2,
        FamilyParameter BodyW,
        FamilyParameter BodyH,
        FamilyParameter FlangeW,
        FamilyParameter FlangeH,
        FamilyParameter Wall,
        FamilyParameter FlangeT,
        FamilyParameter X0,
        FamilyParameter X1,
        FamilyParameter LeftFlangeX0,
        FamilyParameter LeftFlangeX1,
        FamilyParameter RightFlangeX0,
        FamilyParameter RightFlangeX1,
        FamilyParameter RibX0,
        FamilyParameter RibX1,
        FamilyParameter BladeW,
        FamilyParameter BladeH,
        FamilyParameter BladeX0,
        FamilyParameter BladeX1,
        FamilyParameter ActL,
        FamilyParameter ActW,
        FamilyParameter ActH,
        FamilyParameter ActX0,
        FamilyParameter ActX1,
        FamilyParameter ActY0,
        FamilyParameter ActY1,
        FamilyParameter ShaftD,
        FamilyParameter ShaftY0,
        FamilyParameter ShaftY1);

    private sealed record Geometry(
        GenericForm LeftConnectorHost,
        GenericForm RightConnectorHost,
        GenericForm Actuator,
        int FormCount);

    // Retained for the legacy nested-authoring helper. The active KA2 workflow
    // now creates the actuator natively in the parent Family.
    private sealed record NestedActuatorArtifact(ElementId FamilyId);

    public static FireDamperInspection Inspect()
    {
        IReadOnlyList<CatalogRow> rows = AllRows();
        ValidateCatalog(rows);
        return new FireDamperInspection
        {
            CategoryName = "Duct Accessories",
            ConnectorCount = 2,
            TypeCount = rows.Count,
            DefaultTypeName = "B500xH400"
        };
    }

    public static FireDamperBuilderResult Execute(
        UIApplication uiApplication,
        FireDamperBuilderRequest request)
    {
        IReadOnlyList<CatalogRow> rows = AllRows();
        ValidateCatalog(rows);
        string template = AppPaths.GenericModel2020Template;
        if (!File.Exists(template))
            throw new FileNotFoundException(
                "The Metric Generic Model 2020 template was not found.",
                template);
        Document project = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException(
                "Open an RVT project before creating TROX KA2-EU.");
        if (project.IsFamilyDocument)
            throw new InvalidOperationException(
                "Start Family Creator from an RVT project.");

        Directory.CreateDirectory(
            Path.GetDirectoryName(request.OutputPath)
            ?? AppPaths.GeneratedDuctAccessoryFolder);
        Document? familyDocument = null;
        string nestedFolder = Path.Combine(
            Path.GetDirectoryName(request.OutputPath)
                ?? AppPaths.GeneratedDuctAccessoryFolder,
            $".familymep-tmp-ka2-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(nestedFolder);
            familyDocument = uiApplication.Application.NewFamilyDocument(template)
                ?? throw new InvalidOperationException(
                    "Revit could not create the KA2-EU Family document.");
            string lookupCsv = WriteLookupCsv(request.OutputPath, rows);
            var result = new FireDamperBuilderResult
            {
                FamilyName = Path.GetFileNameWithoutExtension(request.OutputPath),
                ActiveTypeName = "B500xH400",
                OutputPath = request.OutputPath,
                CategoryName = "Duct Accessories",
                TypeCount = rows.Count
            };
            using (Transaction transaction = new(
                       familyDocument,
                       "FamilyMEP - Build TROX KA2-EU fire damper"))
            {
                transaction.Start();
                SetMetricUnits(familyDocument);
                SetDuctAccessoryCategory(familyDocument);
                FamilyManager manager = familyDocument.FamilyManager;
                CatalogRow selected = rows.Single(row =>
                    row.Width == 500 && row.Height == 400);
                EnsureBootstrapType(manager, selected.TypeName);
                Parameters parameters = CreateParameters(manager);
                manager.Set(parameters.BSelector, Mm(selected.Width));
                manager.Set(parameters.HSelector, Mm(selected.Height));
                ApplyFixedAndDerivedFormulas(manager, parameters);
                string tableName = ImportLookupTable(familyDocument, lookupCsv);
                ApplyLookupFormulas(manager, parameters, tableName);
                familyDocument.Regenerate();

                ElementId material = EnsureNeutralGray(familyDocument);
                Geometry geometry = BuildGeometry(
                    familyDocument,
                    manager,
                    parameters,
                    material);
                result.ConnectorCount = CreateConnectors(
                    familyDocument,
                    manager,
                    geometry,
                    parameters.B,
                    parameters.H);
                // Dimension labelling temporarily releases and restores some
                // formulas. Reapply the catalog formulas once all geometry and
                // connectors exist so the final flex starts from a clean state.
                ApplyLookupFormulas(manager, parameters, tableName);
                familyDocument.Regenerate();
                // Create catalog Types only after every reference, label,
                // formula, form and connector has been authored. Revit can
                // normalize existing Types to the active preview values while
                // new labelled geometry is being added.
                Dictionary<int, FamilyType> types =
                    CreateFamilyTypes(
                        manager,
                        rows,
                        selected,
                        parameters.BSelector,
                        parameters.HSelector);
                ValidateSelectorSeed(
                    types,
                    parameters.BSelector,
                    parameters.HSelector,
                    rows);
                ValidateControlledParameters(
                    manager,
                    parameters.BSelector,
                    parameters.HSelector);
                ValidateAllTypes(
                    familyDocument,
                    manager,
                    types,
                    rows,
                    parameters,
                    geometry);
                manager.CurrentType = types[selected.Key];
                familyDocument.Regenerate();
                transaction.Commit();

                result.AppliedChanges.Add(
                    $"Embedded lookup table: {tableName} ({rows.Count} official B x H Types)");
                result.AppliedChanges.Add(
                    "Official catalog: 14 listed B x H Types from B250xH250 to B1200xH500");
                result.AppliedChanges.Add(
                    "L/L1/L2, casing, blade and connector geometry are formula/lookup driven");
                result.AppliedChanges.Add(
                    "Two outward-facing bidirectional duct connectors are hosted on the end flanges");
                result.AppliedChanges.Add(
                    "Native actuator and shaft geometry are driven directly by parent lookup formulas");
                result.AppliedChanges.Add(
                    "Unhosted Generic Model template converted to Duct Accessories / Damper");
                result.AppliedChanges.Add(
                    "Neutral gray geometry; manufacturer identity fields left blank");
            }

            var save = new SaveAsOptions
            {
                OverwriteExistingFile = true,
                MaximumBackups = 1
            };
            familyDocument.SaveAs(request.OutputPath, save);
            if (request.LoadIntoProject)
                familyDocument.LoadFamily(
                    project,
                    new OverwriteFamilyLoadOptions());
            DeleteTemporaryNestedFolder(nestedFolder);
            familyDocument.Close(false);
            familyDocument = null;
            return result;
        }
        finally
        {
            if (familyDocument is not null)
            {
                try { familyDocument.Close(false); }
                catch { }
            }
            DeleteTemporaryNestedFolder(nestedFolder);
        }
    }

    private static IReadOnlyList<CatalogRow> AllRows()
    {
        // TROX KA2-EU Product Data 05/2025, "Dimensions and weight":
        // only the listed nominal B/H combinations are official Types.
        (int Width, int Height)[] officialSizes =
        [
            (250, 250),
            (300, 250),
            (400, 400),
            (500, 400),
            (600, 400),
            (700, 400),
            (500, 500),
            (600, 500),
            (700, 500),
            (800, 500),
            (900, 500),
            (1000, 500),
            (1100, 500),
            (1200, 500)
        ];
        var rows = new List<CatalogRow>();
        int key = 1;
        foreach ((int width, int height) in officialSizes)
        {
            int length = height <= 400 ? 580 : 680;
            int l1 = height <= 400 ? 275 : 285;
            rows.Add(new CatalogRow(
                key++,
                width,
                height,
                length,
                l1,
                length - l1));
        }
        return rows;
    }

    private static void ValidateCatalog(IReadOnlyList<CatalogRow> rows)
    {
        if (rows.Count != 14)
            throw new InvalidOperationException(
                $"KA2-EU catalog must contain 14 official sizes; found {rows.Count}.");
        if (rows.Select(row => row.Key).Distinct().Count() != rows.Count)
            throw new InvalidOperationException(
                "KA2-EU catalog selector keys are not unique.");
        foreach (CatalogRow row in rows)
        {
            if (row.Width < 250 || row.Width > 1200)
                throw new InvalidOperationException(
                    $"{row.TypeName}: width is outside the official range.");
            if (row.Height is not (250 or 400 or 500))
                throw new InvalidOperationException(
                    $"{row.TypeName}: height is outside the official range.");
            int expectedLength = row.Height <= 400 ? 580 : 680;
            int expectedL1 = row.Height <= 400 ? 275 : 285;
            if (row.Length != expectedLength
                || row.L1 != expectedL1
                || row.L1 + row.L2 != row.Length)
                throw new InvalidOperationException(
                    $"{row.TypeName}: L/L1/L2 does not match the catalog rule.");
        }
    }

    private static Dictionary<int, FamilyType> CreateFamilyTypes(
        FamilyManager manager,
        IReadOnlyList<CatalogRow> rows,
        CatalogRow selected,
        FamilyParameter bSelector,
        FamilyParameter hSelector)
    {
        var result = new Dictionary<int, FamilyType>();
        FamilyType current = manager.CurrentType
            ?? throw new InvalidOperationException(
                "The KA2 preview Type is unavailable.");
        if (!current.Name.Equals(
                selected.TypeName,
                StringComparison.OrdinalIgnoreCase))
        {
            manager.RenameCurrentType(selected.TypeName);
            current = manager.CurrentType;
        }
        manager.Set(bSelector, Mm(selected.Width));
        manager.Set(hSelector, Mm(selected.Height));
        result[selected.Key] = current;
        foreach (CatalogRow row in rows.Where(row => row.Key != selected.Key))
        {
            FamilyType created = manager.NewType(row.TypeName);
            manager.Set(bSelector, Mm(row.Width));
            manager.Set(hSelector, Mm(row.Height));
            result[row.Key] = created;
        }
        return result;
    }

    private static void EnsureBootstrapType(
        FamilyManager manager,
        string typeName)
    {
        if (manager.CurrentType is null)
            manager.NewType(typeName);
        else if (!manager.CurrentType.Name.Equals(
                     typeName,
                     StringComparison.OrdinalIgnoreCase))
            manager.RenameCurrentType(typeName);
    }

    private static Parameters CreateParameters(FamilyManager manager)
    {
        FamilyParameter Length(string name) =>
            EnsureParameter(manager, name, TroxLengthType);
        FamilyParameter bSelector = Length("FT_LE_ZZ_BSel");
        FamilyParameter hSelector = Length("FT_LE_ZZ_HSel");
        return new Parameters(
            bSelector,
            hSelector,
            Length("FT_LE_ZZ_B"),
            Length("FT_LE_ZZ_H"),
            Length("FT_LE_ZZ_L"),
            Length("FT_LE_ZZ_L1"),
            Length("FT_LE_ZZ_L2"),
            Length("FT_LE_ZZ_BodyW"),
            Length("FT_LE_ZZ_BodyH"),
            Length("FT_LE_ZZ_FlangeW"),
            Length("FT_LE_ZZ_FlangeH"),
            Length("FT_LE_ZZ_Wall"),
            Length("FT_LE_ZZ_FlangeT"),
            Length("FT_LE_ZZ_X0"),
            Length("FT_LE_ZZ_X1"),
            Length("FT_LE_ZZ_LFlangeX0"),
            Length("FT_LE_ZZ_LFlangeX1"),
            Length("FT_LE_ZZ_RFlangeX0"),
            Length("FT_LE_ZZ_RFlangeX1"),
            Length("FT_LE_ZZ_RibX0"),
            Length("FT_LE_ZZ_RibX1"),
            Length("FT_LE_ZZ_BladeW"),
            Length("FT_LE_ZZ_BladeH"),
            Length("FT_LE_ZZ_BladeX0"),
            Length("FT_LE_ZZ_BladeX1"),
            Length("FT_LE_ZZ_ActL"),
            Length("FT_LE_ZZ_ActW"),
            Length("FT_LE_ZZ_ActH"),
            Length("FT_LE_ZZ_ActX0"),
            Length("FT_LE_ZZ_ActX1"),
            Length("FT_LE_ZZ_ActY0"),
            Length("FT_LE_ZZ_ActY1"),
            Length("FT_LE_ZZ_ShaftD"),
            Length("FT_LE_ZZ_ShaftY0"),
            Length("FT_LE_ZZ_ShaftY1"));
    }

    private static void ValidateSelectorSeed(
        IReadOnlyDictionary<int, FamilyType> types,
        FamilyParameter bSelector,
        FamilyParameter hSelector,
        IReadOnlyList<CatalogRow> rows)
    {
        foreach (CatalogRow row in rows)
        {
            AssertMm(
                types[row.Key],
                bSelector,
                row.Width,
                row.TypeName,
                "B selector seed");
            AssertMm(
                types[row.Key],
                hSelector,
                row.Height,
                row.TypeName,
                "H selector seed");
        }
    }

    private static void ApplyFixedAndDerivedFormulas(
        FamilyManager manager,
        Parameters p)
    {
        SetFamilyFormula(manager, p.Wall, "20 mm");
        SetFamilyFormula(manager, p.FlangeT, "6 mm");
        SetFamilyFormula(manager, p.BodyW, "FT_LE_ZZ_B + 60 mm");
        SetFamilyFormula(manager, p.BodyH, "FT_LE_ZZ_H + 60 mm");
        SetFamilyFormula(manager, p.FlangeW, "FT_LE_ZZ_B + 105 mm");
        SetFamilyFormula(manager, p.FlangeH, "FT_LE_ZZ_H + 75 mm");
        SetFamilyFormula(manager, p.X0, "0 mm - FT_LE_ZZ_L1");
        SetFamilyFormula(manager, p.X1, "FT_LE_ZZ_L2");
        SetFamilyFormula(manager, p.LeftFlangeX0, "FT_LE_ZZ_X0 - FT_LE_ZZ_FlangeT");
        SetFamilyFormula(manager, p.LeftFlangeX1, "FT_LE_ZZ_X0");
        SetFamilyFormula(manager, p.RightFlangeX0, "FT_LE_ZZ_X1");
        SetFamilyFormula(manager, p.RightFlangeX1, "FT_LE_ZZ_X1 + FT_LE_ZZ_FlangeT");
        SetFamilyFormula(manager, p.RibX0, "FT_LE_ZZ_X0 + 195 mm");
        SetFamilyFormula(manager, p.RibX1, "FT_LE_ZZ_RibX0 + FT_LE_ZZ_FlangeT");
        SetFamilyFormula(manager, p.BladeW, "FT_LE_ZZ_B - 20 mm");
        SetFamilyFormula(manager, p.BladeH, "20 mm");
        SetFamilyFormula(manager, p.BladeX0, "FT_LE_ZZ_X0 + 35 mm");
        SetFamilyFormula(manager, p.BladeX1, "FT_LE_ZZ_X1 - 35 mm");
        SetFamilyFormula(manager, p.ActL, "195 mm");
        SetFamilyFormula(manager, p.ActW, "90 mm");
        SetFamilyFormula(manager, p.ActH, "80 mm");
        SetFamilyFormula(manager, p.ActX0, "0 mm - FT_LE_ZZ_ActL");
        SetFamilyFormula(manager, p.ActX1, "0 mm");
        SetFamilyFormula(manager, p.ActY0, "FT_LE_ZZ_FlangeW / 2 + 25 mm");
        SetFamilyFormula(manager, p.ActY1, "FT_LE_ZZ_ActY0 + FT_LE_ZZ_ActW");
        SetFamilyFormula(manager, p.ShaftD, "25 mm");
        SetFamilyFormula(manager, p.ShaftY0, "FT_LE_ZZ_BodyW / 2");
        SetFamilyFormula(manager, p.ShaftY1, "FT_LE_ZZ_ActY0");
    }

    private static string WriteLookupCsv(
        string outputPath,
        IReadOnlyList<CatalogRow> rows)
    {
        string folder = Path.GetDirectoryName(outputPath)
            ?? AppPaths.GeneratedDuctAccessoryFolder;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "DuctAccessory_TROX_KA2_EU_LTN.csv");
        var builder = new StringBuilder();
        builder.AppendLine(
            ",BSel##LENGTH##MILLIMETERS,HSelect##LENGTH##MILLIMETERS,"
            + "B##length##millimeters,H##length##millimeters,"
            + "L##length##millimeters,L1##length##millimeters,"
            + "L2##length##millimeters");
        foreach (CatalogRow row in rows)
        {
            builder.Append(
                $"{row.Width.ToString(CultureInfo.InvariantCulture)}"
                + "x"
                + $"{row.Height.ToString(CultureInfo.InvariantCulture)}");
            builder.Append(',');
            builder.Append(row.Width.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.Height.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.Width.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.Height.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(row.L1.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(row.L2.ToString(CultureInfo.InvariantCulture));
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
                "Revit could not create the KA2-EU lookup-table manager.");
        var error = new FamilySizeTableErrorInfo();
        if (!manager.ImportSizeTable(document, csvPath, error))
            throw new InvalidOperationException(
                "KA2-EU lookup-table import failed. "
                + $"Error: {error.FamilySizeTableErrorType}; "
                + $"row {error.InvalidRowIndex}, column {error.InvalidColumnIndex}, "
                + $"header '{error.InvalidHeaderText}'.");
        return Path.GetFileNameWithoutExtension(csvPath);
    }

    private static void ApplyLookupFormulas(
        FamilyManager manager,
        Parameters p,
        string table)
    {
        // B and H are the two explicit catalog keys for each Family Type.
        // Driving geometry directly from them avoids a stale compact numeric
        // selector while the remaining dimensions still come from the table.
        SetFamilyFormula(manager, p.B, "FT_LE_ZZ_BSel");
        SetFamilyFormula(manager, p.H, "FT_LE_ZZ_HSel");
        SetFamilyFormula(manager, p.L, Lookup(table, "L"));
        SetFamilyFormula(manager, p.L1, Lookup(table, "L1"));
        SetFamilyFormula(manager, p.L2, Lookup(table, "L2"));
    }

    private static string Lookup(string table, string column) =>
        $"size_lookup(\"{table}\", \"{column}\", 1 mm, "
        + "FT_LE_ZZ_BSel, FT_LE_ZZ_HSel)";

    private static NestedActuatorArtifact CreateNestedActuatorFamily(
        Document parentDocument,
        Application application,
        string template,
        string folder)
    {
        Document? nestedDocument = null;
        try
        {
            nestedDocument = application.NewFamilyDocument(template)
                ?? throw new InvalidOperationException(
                    "Could not create the nested KA2 actuator Family.");
            using Transaction transaction = new(
                nestedDocument,
                "FamilyMEP - Parametric nested KA2 actuator");
            transaction.Start();
            SetMetricUnits(nestedDocument);
            nestedDocument.OwnerFamily.FamilyCategory =
                nestedDocument.Settings.Categories.get_Item(
                    BuiltInCategory.OST_GenericModel);
            FamilyManager manager = nestedDocument.FamilyManager;
            if (manager.CurrentType is null)
                manager.NewType("KA2_Actuator");
            else
                manager.RenameCurrentType("KA2_Actuator");
            FamilyParameter heightInput = EnsureParameter(
                manager,
                "FT_LE_ZZ_HIn",
                TroxLengthType,
                isInstance: true);
            FamilyParameter height = EnsureParameter(
                manager,
                "FT_LE_ZZ_H",
                TroxLengthType,
                isInstance: true);
            SetFamilyFormula(manager, height, "FT_LE_ZZ_HIn");
            FamilyParameter actL = FixedLength(
                manager,
                "FT_LE_ZZ_ActL",
                "195 mm",
                isInstance: true);
            FamilyParameter actW = FixedLength(
                manager,
                "FT_LE_ZZ_ActW",
                "90 mm",
                isInstance: true);
            FamilyParameter actH = FixedLength(
                manager,
                "FT_LE_ZZ_ActH",
                "80 mm",
                isInstance: true);
            FamilyParameter flangeH = FixedLength(
                manager,
                "FT_LE_ZZ_FlangeH",
                "FT_LE_ZZ_H + 75 mm",
                isInstance: true);
            FamilyParameter actZ0 = FixedLength(
                manager,
                "FT_LE_ZZ_ActZ0",
                "FT_LE_ZZ_FlangeH / 2 + 25 mm",
                isInstance: true);
            FamilyParameter actZ1 = FixedLength(
                manager,
                "FT_LE_ZZ_ActZ1",
                "FT_LE_ZZ_ActZ0 + FT_LE_ZZ_ActH",
                isInstance: true);
            FamilyParameter shaftD = FixedLength(
                manager,
                "FT_LE_ZZ_ShaftD",
                "25 mm",
                isInstance: true);
            FamilyParameter shaftZ0 = FixedLength(
                manager,
                "FT_LE_ZZ_ShaftZ0",
                "FT_LE_ZZ_H / 2 + 30 mm",
                isInstance: true);
            FamilyParameter shaftZ1 = FixedLength(
                manager,
                "FT_LE_ZZ_ShaftZ1",
                "FT_LE_ZZ_ActZ0",
                isInstance: true);
            manager.Set(heightInput, Mm(400));
            nestedDocument.Regenerate();
            ElementId material = EnsureNeutralGray(nestedDocument);
            View plan = FindView(nestedDocument, XYZ.BasisZ);
            CreateCenteredBox(
                nestedDocument,
                manager,
                plan,
                actL,
                actW,
                actZ0,
                actZ1,
                material,
                "KA2 Actuator");
            CreateCenteredCircle(
                nestedDocument,
                manager,
                plan,
                shaftD,
                shaftZ0,
                shaftZ1,
                material,
                "KA2 Shaft");
            ValidateControlledParameters(manager, heightInput);
            nestedDocument.Regenerate();
            transaction.Commit();

            string path = Path.Combine(
                folder,
                "_TEMP_TROX_KA2_EU_ParametricActuator.rfa");
            nestedDocument.SaveAs(
                path,
                new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true,
                    MaximumBackups = 1
                });
            Family loaded = nestedDocument.LoadFamily(
                parentDocument,
                new OverwriteFamilyLoadOptions());
            return new NestedActuatorArtifact(loaded.Id);
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

    private static FamilyInstance PlaceNestedActuator(
        Document document,
        FamilyManager parentManager,
        NestedActuatorArtifact artifact,
        FamilyParameter parentHeight)
    {
        Family family = (Family)document.GetElement(artifact.FamilyId);
        FamilySymbol symbol = family.GetFamilySymbolIds()
            .Select(id => document.GetElement(id))
            .OfType<FamilySymbol>()
            .Single();
        if (!symbol.IsActive)
            symbol.Activate();
        FamilyInstance instance = document.FamilyCreate.NewFamilyInstance(
            XYZ.Zero,
            symbol,
            StructuralType.NonStructural);
        Parameter heightInput = instance.LookupParameter("FT_LE_ZZ_HIn")
            ?? throw new InvalidOperationException(
                "The nested KA2 actuator height input is unavailable.");
        if (!parentManager.CanElementParameterBeAssociated(heightInput))
            throw new InvalidOperationException(
                "The nested KA2 actuator height input cannot be associated.");
        parentManager.AssociateElementParameterToFamilyParameter(
            heightInput,
            parentHeight);
        document.Regenerate();
        return instance;
    }

    private static Geometry BuildGeometry(
        Document document,
        FamilyManager manager,
        Parameters p,
        ElementId material)
    {
        View front = FindView(document, XYZ.BasisX);
        View side = FindView(document, XYZ.BasisY);
        int count = 0;

        CreateRectangularRing(
            document, manager, front,
            p.BodyW, p.BodyH, p.B, p.H,
            p.X0, p.X1, material, "KA2 Casing");
        count++;
        Extrusion leftFlange = CreateRectangularRing(
            document, manager, front,
            p.FlangeW, p.FlangeH, p.B, p.H,
            p.LeftFlangeX0, p.LeftFlangeX1, material, "KA2 End Flange");
        count++;
        Extrusion rightFlange = CreateRectangularRing(
            document, manager, front,
            p.FlangeW, p.FlangeH, p.B, p.H,
            p.RightFlangeX0, p.RightFlangeX1, material, "KA2 End Flange");
        count++;

        CreateRectangularRing(
            document, manager, front,
            p.FlangeW, p.FlangeH, p.B, p.H,
            p.RibX0, p.RibX1, material, "KA2 Reinforcing Flange");
        count++;

        FamilyParameter mid0 = FixedLength(
            manager,
            "FT_LE_ZZ_MidX0",
            "0 mm - 4 mm");
        FamilyParameter mid1 = FixedLength(manager, "FT_LE_ZZ_MidX1", "4 mm");
        CreateRectangularRing(
            document, manager, front,
            p.FlangeW, p.FlangeH, p.B, p.H,
            mid0, mid1, material, "KA2 Stiffener");
        count++;

        CreateCenteredBox(
            document, manager, front,
            p.BladeW, p.BladeH,
            p.BladeX0, p.BladeX1, material, "KA2 Blade");
        count++;

        GenericForm actuator = CreateSideMountedBox(
            document, manager, front,
            p.ActW, p.ActH, p.ActY0,
            p.ActX0, p.ActX1, material, "KA2 Actuator");
        count++;
        CreateCenteredCircle(
            document, manager, side,
            p.ShaftD,
            p.ShaftY0, p.ShaftY1, material, "KA2 Shaft");
        count++;
        return new Geometry(leftFlange, rightFlange, actuator, count);
    }

    private static Extrusion CreateRectangularRing(
        Document document,
        FamilyManager manager,
        View view,
        FamilyParameter outerW,
        FamilyParameter outerH,
        FamilyParameter innerW,
        FamilyParameter innerH,
        FamilyParameter start,
        FamilyParameter end,
        ElementId material,
        string subcategory)
    {
        double ow = Value(manager, outerW, Mm(605));
        double oh = Value(manager, outerH, Mm(475));
        double iw = Math.Min(Value(manager, innerW, Mm(500)), ow - Mm(2));
        double ih = Math.Min(Value(manager, innerH, Mm(400)), oh - Mm(2));
        var profile = new CurveArrArray();
        profile.Append(RectangleOnYz(ow, oh));
        profile.Append(RectangleOnYz(iw, ih));
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateBounds(manager, extrusion, start, end);
        SetMaterial(extrusion, material);
        SetSubcategory(extrusion, document, subcategory);
        document.Regenerate();
        LabelRectangularProfile(
            document, manager, view, extrusion,
            outerW, outerH, innerW, innerH);
        return extrusion;
    }

    private static Extrusion CreateCenteredBox(
        Document document,
        FamilyManager manager,
        View view,
        FamilyParameter width,
        FamilyParameter height,
        FamilyParameter start,
        FamilyParameter end,
        ElementId material,
        string subcategory)
    {
        XYZ normal = view.ViewDirection.Normalize();
        double currentW = Value(manager, width, Mm(100));
        double currentH = Value(manager, height, Mm(100));
        CurveArray rectangle;
        if (Math.Abs(normal.DotProduct(XYZ.BasisX)) > .99)
            rectangle = RectangleOnYz(currentW, currentH);
        else
            rectangle = RectangleOnXy(currentW, currentH);
        var profile = new CurveArrArray();
        profile.Append(rectangle);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(normal, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateBounds(manager, extrusion, start, end);
        SetMaterial(extrusion, material);
        SetSubcategory(extrusion, document, subcategory);
        document.Regenerate();
        LabelSolidRectangle(
            document, view, extrusion, width, height, normal);
        return extrusion;
    }

    private static Extrusion CreateSideMountedBox(
        Document document,
        FamilyManager manager,
        View frontView,
        FamilyParameter depth,
        FamilyParameter height,
        FamilyParameter innerY,
        FamilyParameter startX,
        FamilyParameter endX,
        ElementId material,
        string subcategory)
    {
        double y0 = Value(manager, innerY, Mm(340));
        double currentDepth = Value(manager, depth, Mm(90));
        double currentHeight = Value(manager, height, Mm(80));
        double z = currentHeight / 2;
        XYZ[] points =
        [
            new(0, y0, -z),
            new(0, y0 + currentDepth, -z),
            new(0, y0 + currentDepth, z),
            new(0, y0, z)
        ];
        var profile = new CurveArrArray();
        profile.Append(ClosedLines(points));
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true,
            profile,
            sketch,
            Mm(10));
        AssociateBounds(manager, extrusion, startX, endX);
        SetMaterial(extrusion, material);
        SetSubcategory(extrusion, document, subcategory);
        document.Regenerate();

        List<Line> lines = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(loop => loop.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        List<Line> constantY = lines
            .Where(line => Math.Abs(line.Direction.Z) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Y)
            .ToList();
        List<Line> constantZ = lines
            .Where(line => Math.Abs(line.Direction.Y) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Z)
            .ToList();
        LabelDimension(
            document,
            frontView,
            constantY[0].Reference,
            constantY[1].Reference,
            new XYZ(0, y0, z * 1.4),
            new XYZ(0, y0 + currentDepth, z * 1.4),
            depth);
        LabelDimension(
            document,
            frontView,
            constantZ[0].Reference,
            constantZ[1].Reference,
            new XYZ(0, y0 + currentDepth * 1.25, -z),
            new XYZ(0, y0 + currentDepth * 1.25, z),
            height);
        ReferencePlane? centerY = FindCenterPlane(document, XYZ.BasisY);
        if (centerY is not null)
        {
            LabelDimension(
                document,
                frontView,
                centerY.GetReference(),
                constantY[0].Reference,
                new XYZ(0, 0, z * 1.75),
                new XYZ(0, y0, z * 1.75),
                innerY);
        }
        Equalize(
            document,
            frontView,
            constantZ[0].Reference,
            constantZ[1].Reference,
            XYZ.BasisZ,
            Mm(500));
        return extrusion;
    }

    private static Extrusion CreateCenteredCircle(
        Document document,
        FamilyManager manager,
        View view,
        FamilyParameter diameter,
        FamilyParameter start,
        FamilyParameter end,
        ElementId material,
        string subcategory)
    {
        XYZ normal = view.ViewDirection.Normalize();
        XYZ xAxis;
        XYZ yAxis;
        if (Math.Abs(normal.DotProduct(XYZ.BasisX)) > .99)
        {
            xAxis = XYZ.BasisY;
            yAxis = XYZ.BasisZ;
        }
        else if (Math.Abs(normal.DotProduct(XYZ.BasisY)) > .99)
        {
            xAxis = XYZ.BasisX;
            yAxis = -XYZ.BasisZ;
        }
        else
        {
            xAxis = XYZ.BasisX;
            yAxis = XYZ.BasisY;
        }
        double radius = Value(manager, diameter, Mm(25)) / 2;
        var loop = new CurveArray();
        loop.Append(Arc.Create(
            XYZ.Zero, radius, 0, Math.PI, xAxis, yAxis));
        loop.Append(Arc.Create(
            XYZ.Zero, radius, Math.PI, Math.PI * 2, xAxis, yAxis));
        var profile = new CurveArrArray();
        profile.Append(loop);
        SketchPlane sketch = SketchPlane.Create(
            document,
            Plane.CreateByNormalAndOrigin(normal, XYZ.Zero));
        Extrusion extrusion = document.FamilyCreate.NewExtrusion(
            true, profile, sketch, Mm(10));
        AssociateBounds(manager, extrusion, start, end);
        SetMaterial(extrusion, material);
        SetSubcategory(extrusion, document, subcategory);
        document.Regenerate();
        Arc arc = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(item => item.Cast<Curve>())
            .OfType<Arc>()
            .First(item => item.Reference is not null);
        Dimension dimension = document.FamilyCreate.NewDiameterDimension(
            view,
            arc.Reference,
            new XYZ(radius * 1.2, 0, 0));
        AssignDimensionLabel(document, dimension, diameter);
        return extrusion;
    }

    private static CurveArray RectangleOnYz(double width, double height)
    {
        double y = width / 2;
        double z = height / 2;
        XYZ[] points =
        [
            new(0, -y, -z),
            new(0, y, -z),
            new(0, y, z),
            new(0, -y, z)
        ];
        return ClosedLines(points);
    }

    private static CurveArray RectangleOnXy(double width, double depth)
    {
        double x = width / 2;
        double y = depth / 2;
        XYZ[] points =
        [
            new(-x, -y, 0),
            new(x, -y, 0),
            new(x, y, 0),
            new(-x, y, 0)
        ];
        return ClosedLines(points);
    }

    private static CurveArray ClosedLines(IReadOnlyList<XYZ> points)
    {
        var result = new CurveArray();
        for (int index = 0; index < points.Count; index++)
            result.Append(Line.CreateBound(
                points[index],
                points[(index + 1) % points.Count]));
        return result;
    }

    private static void LabelRectangularProfile(
        Document document,
        FamilyManager manager,
        View view,
        Extrusion extrusion,
        FamilyParameter outerW,
        FamilyParameter outerH,
        FamilyParameter innerW,
        FamilyParameter innerH)
    {
        List<Line> lines = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(loop => loop.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        List<Line> constantY = lines
            .Where(line => Math.Abs(line.Direction.Z) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Y)
            .ToList();
        List<Line> constantZ = lines
            .Where(line => Math.Abs(line.Direction.Y) > .99)
            .OrderBy(line => line.Evaluate(.5, true).Z)
            .ToList();
        if (constantY.Count != 4 || constantZ.Count != 4)
            throw new InvalidOperationException(
                "KA2 rectangular ring sketch references are incomplete.");
        double ow = Value(manager, outerW, Mm(605));
        double oh = Value(manager, outerH, Mm(475));
        LabelDimension(
            document, view,
            constantY[0].Reference, constantY[3].Reference,
            new XYZ(0, -ow / 2, oh * .62),
            new XYZ(0, ow / 2, oh * .62),
            outerW);
        LabelDimension(
            document, view,
            constantY[1].Reference, constantY[2].Reference,
            new XYZ(0, -ow / 3, oh * .48),
            new XYZ(0, ow / 3, oh * .48),
            innerW);
        LabelDimension(
            document, view,
            constantZ[0].Reference, constantZ[3].Reference,
            new XYZ(0, ow * .62, -oh / 2),
            new XYZ(0, ow * .62, oh / 2),
            outerH);
        LabelDimension(
            document, view,
            constantZ[1].Reference, constantZ[2].Reference,
            new XYZ(0, ow * .48, -oh / 3),
            new XYZ(0, ow * .48, oh / 3),
            innerH);
        Equalize(
            document, view,
            constantY[0].Reference, constantY[3].Reference,
            XYZ.BasisY, Math.Max(ow, oh));
        Equalize(
            document, view,
            constantY[1].Reference, constantY[2].Reference,
            XYZ.BasisY, Math.Max(ow, oh) * .86);
        Equalize(
            document, view,
            constantZ[0].Reference, constantZ[3].Reference,
            XYZ.BasisZ, Math.Max(ow, oh));
        Equalize(
            document, view,
            constantZ[1].Reference, constantZ[2].Reference,
            XYZ.BasisZ, Math.Max(ow, oh) * .86);
    }

    private static void LabelSolidRectangle(
        Document document,
        View view,
        Extrusion extrusion,
        FamilyParameter width,
        FamilyParameter height,
        XYZ normal)
    {
        List<Line> lines = extrusion.Sketch.Profile
            .Cast<CurveArray>()
            .SelectMany(loop => loop.Cast<Curve>())
            .OfType<Line>()
            .Where(line => line.Reference is not null)
            .ToList();
        if (Math.Abs(normal.DotProduct(XYZ.BasisX)) > .99)
        {
            List<Line> vertical = lines
                .Where(line => Math.Abs(line.Direction.Z) > .99)
                .OrderBy(line => line.Evaluate(.5, true).Y)
                .ToList();
            List<Line> horizontal = lines
                .Where(line => Math.Abs(line.Direction.Y) > .99)
                .OrderBy(line => line.Evaluate(.5, true).Z)
                .ToList();
            LabelDimension(
                document, view,
                vertical[0].Reference, vertical[1].Reference,
                new XYZ(0, -1, 1), new XYZ(0, 1, 1), width);
            LabelDimension(
                document, view,
                horizontal[0].Reference, horizontal[1].Reference,
                new XYZ(0, 1, -1), new XYZ(0, 1, 1), height);
            Equalize(
                document, view,
                vertical[0].Reference, vertical[1].Reference,
                XYZ.BasisY, Mm(500));
            Equalize(
                document, view,
                horizontal[0].Reference, horizontal[1].Reference,
                XYZ.BasisZ, Mm(500));
        }
        else
        {
            List<Line> vertical = lines
                .Where(line => Math.Abs(line.Direction.Y) > .99)
                .OrderBy(line => line.Evaluate(.5, true).X)
                .ToList();
            List<Line> horizontal = lines
                .Where(line => Math.Abs(line.Direction.X) > .99)
                .OrderBy(line => line.Evaluate(.5, true).Y)
                .ToList();
            LabelDimension(
                document, view,
                vertical[0].Reference, vertical[1].Reference,
                new XYZ(-1, 1, 0), new XYZ(1, 1, 0), width);
            LabelDimension(
                document, view,
                horizontal[0].Reference, horizontal[1].Reference,
                new XYZ(1, -1, 0), new XYZ(1, 1, 0), height);
            Equalize(
                document, view,
                vertical[0].Reference, vertical[1].Reference,
                XYZ.BasisX, Mm(500));
            Equalize(
                document, view,
                horizontal[0].Reference, horizontal[1].Reference,
                XYZ.BasisY, Mm(500));
        }
    }

    private static void LabelDimension(
        Document document,
        View view,
        Reference first,
        Reference last,
        XYZ start,
        XYZ end,
        FamilyParameter label)
    {
        var refs = new ReferenceArray();
        refs.Append(first);
        refs.Append(last);
        Dimension dimension = document.FamilyCreate.NewLinearDimension(
            view,
            Line.CreateBound(start, end),
            refs);
        AssignDimensionLabel(document, dimension, label);
    }

    private static void AssignDimensionLabel(
        Document document,
        Dimension dimension,
        FamilyParameter label)
    {
        FamilyManager manager = document.FamilyManager;
        string? formula = label.Formula;
        if (string.IsNullOrWhiteSpace(formula))
        {
            dimension.FamilyLabel = label;
            return;
        }

        manager.SetFormula(label, null);
        document.Regenerate();
        try
        {
            dimension.FamilyLabel = label;
            document.Regenerate();
        }
        finally
        {
            SetFamilyFormula(manager, label, formula);
            document.Regenerate();
        }
    }

    private static void Equalize(
        Document document,
        View view,
        Reference first,
        Reference last,
        XYZ normal,
        double extent)
    {
        ReferencePlane? center = FindCenterPlane(document, normal);
        if (center is null) return;
        XYZ start;
        XYZ end;
        if (normal.IsAlmostEqualTo(XYZ.BasisY))
        {
            start = new XYZ(0, -extent, -extent * .62);
            end = new XYZ(0, extent, -extent * .62);
        }
        else if (normal.IsAlmostEqualTo(XYZ.BasisZ))
        {
            start = new XYZ(0, -extent * .62, -extent);
            end = new XYZ(0, -extent * .62, extent);
        }
        else
        {
            start = new XYZ(-extent, -extent * .62, 0);
            end = new XYZ(extent, -extent * .62, 0);
        }
        var refs = new ReferenceArray();
        refs.Append(first);
        refs.Append(center.GetReference());
        refs.Append(last);
        Dimension dimension = document.FamilyCreate.NewLinearDimension(
            view,
            Line.CreateBound(start, end),
            refs);
        dimension.AreSegmentsEqual = true;
    }

    private static int CreateConnectors(
        Document document,
        FamilyManager manager,
        Geometry geometry,
        FamilyParameter width,
        FamilyParameter height)
    {
        document.Regenerate();
        Reference left = FindEndFace(geometry.LeftConnectorHost, -XYZ.BasisX);
        Reference right = FindEndFace(geometry.RightConnectorHost, XYZ.BasisX);
        ConnectorElement leftConnector = ConnectorElement.CreateDuctConnector(
            document,
            DuctSystemType.ExhaustAir,
            ConnectorProfileType.Rectangular,
            left);
        ConnectorElement rightConnector = ConnectorElement.CreateDuctConnector(
            document,
            DuctSystemType.ExhaustAir,
            ConnectorProfileType.Rectangular,
            right);
        leftConnector.SetLinkedConnectorElement(rightConnector);
        SetDuctFlowDirection(leftConnector, FlowDirectionType.Bidirectional);
        SetDuctFlowDirection(rightConnector, FlowDirectionType.Bidirectional);
        foreach (ConnectorElement connector in new[] { leftConnector, rightConnector })
        {
            Parameter connectorW = connector.get_Parameter(
                    BuiltInParameter.CONNECTOR_WIDTH)
                ?? throw new InvalidOperationException(
                    "KA2 connector Width parameter is unavailable.");
            Parameter connectorH = connector.get_Parameter(
                    BuiltInParameter.CONNECTOR_HEIGHT)
                ?? throw new InvalidOperationException(
                    "KA2 connector Height parameter is unavailable.");
            manager.AssociateElementParameterToFamilyParameter(connectorW, width);
            manager.AssociateElementParameterToFamilyParameter(connectorH, height);
        }
        return 2;
    }

    private static void SetDuctFlowDirection(
        ConnectorElement connector,
        FlowDirectionType direction)
    {
        Parameter? parameter = connector.get_Parameter(
            BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM);
        if (parameter is not null && !parameter.IsReadOnly)
            parameter.Set((int)direction);
    }

    private static Reference FindEndFace(Element element, XYZ direction)
    {
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        XYZ normal = direction.Normalize();
        PlanarFace? face = element.get_Geometry(options)
            .OfType<Solid>()
            .Where(solid => solid.Faces.Size > 0)
            .SelectMany(solid => solid.Faces.Cast<Face>())
            .OfType<PlanarFace>()
            .Where(candidate =>
                candidate.FaceNormal.Normalize().DotProduct(normal) > .99)
            .OrderByDescending(candidate =>
                candidate.Origin.DotProduct(normal))
            .FirstOrDefault();
        return face?.Reference
            ?? throw new InvalidOperationException(
                "KA2 connector host end face could not be resolved.");
    }

    private static void ValidateAllTypes(
        Document document,
        FamilyManager manager,
        IReadOnlyDictionary<int, FamilyType> types,
        IReadOnlyList<CatalogRow> rows,
        Parameters p,
        Geometry geometry)
    {
        List<ConnectorElement> connectors = new FilteredElementCollector(document)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .ToList();
        if (connectors.Count != 2)
            throw new InvalidOperationException(
                $"KA2-EU must contain 2 duct connectors; found {connectors.Count}.");
        foreach (ConnectorElement connector in connectors)
        {
            Parameter? flowDirection = connector.get_Parameter(
                BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM);
            if (flowDirection is not null
                && flowDirection.AsInteger() != (int)FlowDirectionType.Bidirectional)
                throw new InvalidOperationException(
                    "KA2-EU duct connectors must be bidirectional.");
        }
        // Flex the smallest size, both L/L1/L2 transitions, the preview size,
        // and the largest size. Catalog validation above covers all 14 official
        // B x H combinations; these representative Revit flexes catch unstable
        // references, formula associations and connector host geometry.
        HashSet<int> keysToFlex =
        [
            rows.First().Key,
            rows.Single(row => row.Width == 500 && row.Height == 400).Key,
            rows.Single(row => row.Width == 500 && row.Height == 500).Key,
            rows.Single(row => row.Width == 1200 && row.Height == 500).Key
        ];
        foreach (CatalogRow row in rows.Where(row => keysToFlex.Contains(row.Key)))
        {
            manager.CurrentType = types[row.Key];
            document.Regenerate();
            AssertMm(
                types[row.Key],
                p.BSelector,
                row.Width,
                row.TypeName,
                "B selector");
            AssertMm(
                types[row.Key],
                p.HSelector,
                row.Height,
                row.TypeName,
                "H selector");
            AssertMm(types[row.Key], p.B, row.Width, row.TypeName, "B");
            AssertMm(types[row.Key], p.H, row.Height, row.TypeName, "H");
            AssertMm(types[row.Key], p.L, row.Length, row.TypeName, "L");
            foreach (ConnectorElement connector in connectors)
            {
                Parameter connectorW = connector.get_Parameter(
                    BuiltInParameter.CONNECTOR_WIDTH)!;
                Parameter connectorH = connector.get_Parameter(
                    BuiltInParameter.CONNECTOR_HEIGHT)!;
                if (Math.Abs(connectorW.AsDouble() - Mm(row.Width)) > Mm(.1)
                    || Math.Abs(connectorH.AsDouble() - Mm(row.Height)) > Mm(.1))
                    throw new InvalidOperationException(
                        $"{row.TypeName}: connector B x H is not associated to the catalog.");
            }
            BoundingBoxXYZ leftBounds = geometry.LeftConnectorHost.get_BoundingBox(null)
                ?? throw new InvalidOperationException(
                    $"{row.TypeName}: left connector host has no geometry.");
            double hostW = leftBounds.Max.Y - leftBounds.Min.Y;
            double hostH = leftBounds.Max.Z - leftBounds.Min.Z;
            if (Math.Abs(hostW - Mm(row.Width + 105)) > Mm(.2)
                || Math.Abs(hostH - Mm(row.Height + 75)) > Mm(.2))
                throw new InvalidOperationException(
                    $"{row.TypeName}: flange connector host does not match catalog B+105/H+75.");
            BoundingBoxXYZ actuatorBounds = geometry.Actuator.get_BoundingBox(null)
                ?? throw new InvalidOperationException(
                    $"{row.TypeName}: actuator has no native geometry.");
            double expectedActuatorY0 = Mm((row.Width + 105) / 2.0 + 25);
            double expectedActuatorY1 = expectedActuatorY0 + Mm(90);
            if (Math.Abs(actuatorBounds.Min.X - Mm(-195)) > Mm(.2)
                || Math.Abs(actuatorBounds.Max.X) > Mm(.2)
                || Math.Abs(actuatorBounds.Min.Y - expectedActuatorY0) > Mm(.2)
                || Math.Abs(actuatorBounds.Max.Y - expectedActuatorY1) > Mm(.2)
                || Math.Abs(actuatorBounds.Min.Z - Mm(-40)) > Mm(.2)
                || Math.Abs(actuatorBounds.Max.Z - Mm(40)) > Mm(.2))
                throw new InvalidOperationException(
                    $"{row.TypeName}: side-mounted actuator position is not catalog driven.");
        }
    }

    private static void AssertMm(
        FamilyType type,
        FamilyParameter parameter,
        double expected,
        string typeName,
        string dimension)
    {
        double actual = type.AsDouble(parameter)
            ?? throw new InvalidOperationException(
                $"{typeName}: {dimension} has no value.");
        if (Math.Abs(actual - Mm(expected)) > Mm(.01))
            throw new InvalidOperationException(
                $"{typeName}: {dimension} is "
                + $"{UnitUtils.ConvertFromInternalUnits(actual, UnitTypeId.Millimeters):0.###} mm; "
                + $"expected catalog {expected} mm.");
    }

    private static void ValidateControlledParameters(
        FamilyManager manager,
        params FamilyParameter[] inputs)
    {
        HashSet<ElementId> inputIds = inputs
            .Select(parameter => parameter.Id)
            .ToHashSet();
        List<string> editable = manager.Parameters
            .Cast<FamilyParameter>()
            .Where(parameter =>
                parameter.Definition.Name.StartsWith(
                    "FT_LE_ZZ_",
                    StringComparison.OrdinalIgnoreCase))
            .Where(parameter =>
                !inputIds.Contains(parameter.Id)
                && string.IsNullOrWhiteSpace(parameter.Formula))
            .Select(parameter => parameter.Definition.Name)
            .ToList();
        if (editable.Count > 0)
            throw new InvalidOperationException(
                "KA2-EU contains editable controlled parameters: "
                + string.Join(", ", editable));
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
                    ".familymep-tmp-ka2-",
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // The parent RFA embeds the nested actuator. A locked temporary
            // document must not invalidate a successfully generated Family.
        }
    }

    private static void AssociateBounds(
        FamilyManager manager,
        Extrusion extrusion,
        FamilyParameter start,
        FamilyParameter end)
    {
        Parameter startParam = extrusion.get_Parameter(
                BuiltInParameter.EXTRUSION_START_PARAM)
            ?? throw new InvalidOperationException(
                "Extrusion Start parameter is unavailable.");
        Parameter endParam = extrusion.get_Parameter(
                BuiltInParameter.EXTRUSION_END_PARAM)
            ?? throw new InvalidOperationException(
                "Extrusion End parameter is unavailable.");
        manager.AssociateElementParameterToFamilyParameter(startParam, start);
        manager.AssociateElementParameterToFamilyParameter(endParam, end);
    }

    private static void HideForm(GenericForm form)
    {
        Parameter? visible = form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
        if (visible is not null && !visible.IsReadOnly)
            visible.Set(0);
    }

#if REVIT2020 || REVIT2021 || REVIT2022
    private static TroxParameterType TroxLengthType => ParameterType.Length;
#else
    private static TroxParameterType TroxLengthType => SpecTypeId.Length;
#endif

    private static FamilyParameter EnsureParameter(
        FamilyManager manager,
        string name,
        TroxParameterType dataType,
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
                #if REVIT2020 || REVIT2021 || REVIT2022
                BuiltInParameterGroup.PG_GEOMETRY,
#else
                GroupTypeId.Geometry,
#endif
                dataType,
                isInstance);
    }

    private static FamilyParameter FixedLength(
        FamilyManager manager,
        string name,
        string formula,
        bool isInstance = false)
    {
        FamilyParameter parameter = EnsureParameter(
            manager,
            name,
            TroxLengthType,
            isInstance);
        SetFamilyFormula(manager, parameter, formula);
        return parameter;
    }

    private static void SetFamilyFormula(
        FamilyManager manager,
        FamilyParameter parameter,
        string formula)
    {
        try
        {
            manager.SetFormula(parameter, formula);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"KA2 formula failed: {parameter.Definition.Name} = {formula}. "
                + exception.Message,
                exception);
        }
    }

    private static double Value(
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

    private static View FindView(Document document, XYZ direction) =>
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
            $"The Generic Model template has no view normal to {direction}.");

    private static ReferencePlane? FindCenterPlane(
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

    private static void SetDuctAccessoryCategory(Document document)
    {
        document.OwnerFamily.FamilyCategory =
            document.Settings.Categories.get_Item(
                BuiltInCategory.OST_DuctAccessory);
        Parameter? partType = document.OwnerFamily.get_Parameter(
            BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
        if (partType is not null && !partType.IsReadOnly)
            partType.Set((int)PartType.Damper);
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

    private static void SetMaterial(GenericForm form, ElementId materialId)
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
        Category familyCategory = document.OwnerFamily.FamilyCategory;
        Category? subcategory = familyCategory.SubCategories
            .Cast<Category>()
            .FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        subcategory ??= document.Settings.Categories.NewSubcategory(
            familyCategory,
            name);
        form.Subcategory = subcategory;
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
}
