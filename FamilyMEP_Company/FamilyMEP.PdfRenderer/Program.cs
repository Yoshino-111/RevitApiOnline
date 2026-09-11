using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

try
{
    if (args.Length >= 3 && args[0].Equals("extract", StringComparison.OrdinalIgnoreCase))
    {
        string source = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        PdfVectorScene scene = PdfVectorExtractor.ExtractFirstPage(source);
        if (Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase))
            await PdfVectorExtractor.WriteAsync(scene, output);
        else
            PdfVectorExtractor.WriteBinary(scene, output);
        Console.WriteLine($"pages={scene.PageCount}");
        Console.WriteLine($"paths={scene.Paths.Count}");
        Console.WriteLine($"segments={scene.Segments.Count}");
        return 0;
    }

    int offset = args.Length > 0 && args[0].Equals("render", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    if (args.Length < offset + 2)
    {
        Console.Error.WriteLine("Usage: FamilyMEP.PdfRenderer [render] <source.pdf> <output.png> [width]");
        Console.Error.WriteLine("   or: FamilyMEP.PdfRenderer extract <source.pdf> <output.json>");
        return 2;
    }

    string renderSource = Path.GetFullPath(args[offset]);
    string renderOutput = Path.GetFullPath(args[offset + 1]);
    uint targetWidth = args.Length > offset + 2 && uint.TryParse(args[offset + 2], out uint parsedWidth)
        ? Math.Clamp(parsedWidth, 800u, 5000u)
        : 2200u;

    StorageFile file = await StorageFile.GetFileFromPathAsync(renderSource);
    PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
    if (document.PageCount == 0)
        throw new InvalidDataException("The PDF does not contain any pages.");

    using PdfPage page = document.GetPage(0);
    uint targetHeight = Math.Max(
        1,
        (uint)Math.Round(targetWidth * page.Size.Height / Math.Max(1.0, page.Size.Width)));
    using var stream = new InMemoryRandomAccessStream();
    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
    {
        DestinationWidth = targetWidth,
        DestinationHeight = targetHeight
    });
    stream.Seek(0);
    using var reader = new DataReader(stream.GetInputStreamAt(0));
    uint length = await reader.LoadAsync((uint)stream.Size);
    byte[] bytes = new byte[length];
    reader.ReadBytes(bytes);

    Directory.CreateDirectory(Path.GetDirectoryName(renderOutput)!);
    await File.WriteAllBytesAsync(renderOutput, bytes);
    Console.WriteLine($"pages={document.PageCount}");
    Console.WriteLine($"width={targetWidth}");
    Console.WriteLine($"height={targetHeight}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.ToString());
    return 1;
}
