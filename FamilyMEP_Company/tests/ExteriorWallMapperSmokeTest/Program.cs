using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

string projectRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string pluginPath = Path.Combine(projectRoot, "plugin-output", "FamilyMEP.Plugin.dll");
if (!File.Exists(pluginPath)) throw new FileNotFoundException("Build the plugin first.", pluginPath);

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string local = Path.Combine(Path.GetDirectoryName(pluginPath)!, name.Name + ".dll");
    if (File.Exists(local)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(local);
    string revit = Path.Combine(@"C:\Program Files\Autodesk\Revit 2025", name.Name + ".dll");
    return File.Exists(revit) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(revit) : null;
};

Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
Type rowType = plugin.GetType("FamilyMEP.Plugin.ExteriorWallMapper.ExteriorWallRow", true)!;
Type batchType = plugin.GetType("FamilyMEP.Plugin.ExteriorWallMapper.ReplacementBatch", true)!;
Type linearPlanType = plugin.GetType("FamilyMEP.Plugin.ExteriorWallMapper.LinearBatchPlan", true)!;
Type exporterType = plugin.GetType("FamilyMEP.Plugin.ExteriorWallMapper.ExteriorWallExcelExporter", true)!;
object rows = Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType))!;
object batches = Activator.CreateInstance(typeof(List<>).MakeGenericType(batchType))!;
object linearPlans = Activator.CreateInstance(typeof(List<>).MakeGenericType(linearPlanType))!;
var warnings = new List<string> { "Smoke-test warning" };
string output = Path.Combine(Path.GetTempPath(), $"ExteriorWallMapper-smoke-{Guid.NewGuid():N}.xlsx");

MethodInfo export = exporterType.GetMethod("Export", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(exporterType.FullName, "Export");
export.Invoke(null, [output, rows, batches, linearPlans, warnings]);

using (ZipArchive workbook = ZipFile.OpenRead(output))
{
    string[] required =
    [
        "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/styles.xml",
        "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/worksheets/sheet3.xml",
        "xl/worksheets/sheet4.xml", "xl/worksheets/sheet5.xml", "xl/worksheets/sheet6.xml",
        "xl/worksheets/sheet7.xml", "xl/worksheets/sheet8.xml", "xl/worksheets/sheet9.xml",
        "xl/worksheets/sheet10.xml", "xl/worksheets/sheet11.xml"
    ];
    foreach (string entryName in required)
    {
        ZipArchiveEntry entry = workbook.GetEntry(entryName)
            ?? throw new InvalidDataException($"Missing XLSX entry: {entryName}");
        if (entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            using Stream stream = entry.Open();
            _ = XDocument.Load(stream);
        }
    }

    ZipArchiveEntry workbookEntry = workbook.GetEntry("xl/workbook.xml")!;
    using Stream workbookStream = workbookEntry.Open();
    XDocument workbookDocument = XDocument.Load(workbookStream);
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "Room Wall Inventory", StringComparison.Ordinal)))
        throw new InvalidDataException("The Room Wall Inventory worksheet is missing from workbook.xml.");
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "Grouped Wall Types", StringComparison.Ordinal)))
        throw new InvalidDataException("The Grouped Wall Types worksheet is missing from workbook.xml.");
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "Sun Exposed Rooms", StringComparison.Ordinal)))
        throw new InvalidDataException("The Sun Exposed Rooms worksheet is missing from workbook.xml.");
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "LINEAR Room Replace", StringComparison.Ordinal)))
        throw new InvalidDataException("The LINEAR Room Replace worksheet is missing from workbook.xml.");
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "LINEAR Replace Groups", StringComparison.Ordinal)))
        throw new InvalidDataException("The LINEAR Replace Groups worksheet is missing from workbook.xml.");
    if (!workbookDocument.Descendants().Any(element =>
            element.Name.LocalName == "sheet" &&
            string.Equals((string?)element.Attribute("name"), "Wall Assembly Layers", StringComparison.Ordinal)))
        throw new InvalidDataException("The Wall Assembly Layers worksheet is missing from workbook.xml.");

    ZipArchiveEntry inventoryEntry = workbook.GetEntry("xl/worksheets/sheet6.xml")!;
    using Stream inventoryStream = inventoryEntry.Open();
    XDocument inventoryDocument = XDocument.Load(inventoryStream);
    string[] requiredHeaders =
        ["Room Has EWA", "Room Detected Sides", "Wall Type", "Component / Material Name", "Material Class",
         "Proposed LINEAR Material / Component", "Shared Wall"];
    string[] cellTexts = inventoryDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in requiredHeaders)
        if (!cellTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"Room Wall Inventory header is missing: {header}");

    ZipArchiveEntry groupedEntry = workbook.GetEntry("xl/worksheets/sheet7.xml")!;
    using Stream groupedStream = groupedEntry.Open();
    XDocument groupedDocument = XDocument.Load(groupedStream);
    string[] groupedHeaders =
        ["Group ID", "Room Count", "Room Numbers", "Rooms Using This Wall Type", "Has Associated Opening",
         "Associated Opening Types", "Rooms With Associated Openings"];
    string[] groupedTexts = groupedDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in groupedHeaders)
        if (!groupedTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"Grouped Wall Types header is missing: {header}");

    ZipArchiveEntry sunRoomsEntry = workbook.GetEntry("xl/worksheets/sheet8.xml")!;
    using Stream sunRoomsStream = sunRoomsEntry.Open();
    XDocument sunRoomsDocument = XDocument.Load(sunRoomsStream);
    string[] sunRoomHeaders = ["Has Sun-Exposed EWA", "EWA Orientations", "EWA Thicknesses (mm)", "EWI Count", "ED Count"];
    string[] sunRoomTexts = sunRoomsDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in sunRoomHeaders)
        if (!sunRoomTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"Sun Exposed Rooms header is missing: {header}");

    ZipArchiveEntry replaceEntry = workbook.GetEntry("xl/worksheets/sheet9.xml")!;
    using Stream replaceStream = replaceEntry.Open();
    XDocument replaceDocument = XDocument.Load(replaceStream);
    string[] replaceHeaders =
        ["Group ID", "LINEAR Storey", "LINEAR Room Selection Name", "Room Number", "Room Name",
         "Has Associated Opening"];
    string[] replaceTexts = replaceDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in replaceHeaders)
        if (!replaceTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"LINEAR Room Replace header is missing: {header}");

    ZipArchiveEntry replaceGroupsEntry = workbook.GetEntry("xl/worksheets/sheet10.xml")!;
    using Stream replaceGroupsStream = replaceGroupsEntry.Open();
    XDocument replaceGroupsDocument = XDocument.Load(replaceGroupsStream);
    string[] replaceGroupHeaders =
        ["Group ID", "Component / Material Name", "Room Count", "LINEAR Storeys",
         "LINEAR Room Selection Names",
         "Rooms With Openings", "Rooms Without Openings", "Target LINEAR"];
    string[] replaceGroupTexts = replaceGroupsDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in replaceGroupHeaders)
        if (!replaceGroupTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"LINEAR Replace Groups header is missing: {header}");

    ZipArchiveEntry assemblyLayersEntry = workbook.GetEntry("xl/worksheets/sheet11.xml")!;
    using Stream assemblyLayersStream = assemblyLayersEntry.Open();
    XDocument assemblyLayersDocument = XDocument.Load(assemblyLayersStream);
    string[] assemblyLayerHeaders =
        ["Assembly Group", "Assembly Name", "Physical Thickness (mm)", "Layer Thickness Sum (mm)",
         "Overlap Removed (mm)", "Layer Order", "Layer / Material Name", "Room Numbers", "Room Names",
         "Source Element IDs", "Source Unique IDs", "IFC GUIDs", "Link Paths", "Geometry Keys",
         "Room Number [Wall Element IDs]", "Trace Status"];
    string[] assemblyLayerTexts = assemblyLayersDocument.Descendants()
        .Where(element => element.Name.LocalName == "t")
        .Select(element => element.Value)
        .ToArray();
    foreach (string header in assemblyLayerHeaders)
        if (!assemblyLayerTexts.Contains(header, StringComparer.Ordinal))
            throw new InvalidDataException($"Wall Assembly Layers header is missing: {header}");
    if (!assemblyLayersDocument.Descendants().Any(element =>
            element.Name.LocalName == "outlinePr" && (string?)element.Attribute("summaryBelow") == "0"))
        throw new InvalidDataException("Wall Assembly Layers does not enable Excel outline groups.");
}

File.Delete(output);
Console.WriteLine("Exterior Wall Mapper smoke test passed: XLSX package and XML parts are valid.");
