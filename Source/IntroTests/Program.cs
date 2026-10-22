using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VpnZapretSwitch;

if (args.Length != 1) throw new ArgumentException("Pass the path to intro.frames.zip.");
await Task.Run(() =>
{
    using var archive = ZipFile.OpenRead(args[0]);
    using var stream = archive.GetEntry("manifest.json")!.Open();
    using var manifest = JsonDocument.Parse(stream);
    int index = 0, translucent = 0;
    foreach (var layout in manifest.RootElement.GetProperty("frames").EnumerateArray())
    {
        int width = layout.GetProperty("width").GetInt32(), height = layout.GetProperty("height").GetInt32();
        using var input = archive.GetEntry($"frames/{index:D3}.png")!.Open();
        if (input.CanSeek) throw new Exception("Regression fixture must use a nonseekable ZIP entry stream.");
        var bitmap = IntroFrameDecoder.Decode(input, width, height);
        if (bitmap.IsDownloading || !bitmap.IsFrozen || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            throw new Exception($"Frame {index} was not decoded synchronously.");
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4]; bgra.CopyPixels(pixels, width * 4, 0);
        bool transparent = false, visible = false;
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            transparent |= pixels[offset] == 0; visible |= pixels[offset] > 0;
            if (pixels[offset] is > 0 and < 255) translucent++;
        }
        if (!transparent || !visible) throw new Exception($"Frame {index} lost alpha or its image.");
        index++;
    }
    if (index != 102 || translucent == 0) throw new Exception("Incomplete animation.");
    using var entry = archive.GetEntry("frames/000.png")!.Open();
    try { IntroFrameDecoder.Decode(entry, 1, 1); throw new Exception("Incorrect dimensions accepted."); }
    catch (InvalidDataException) { }
    Console.WriteLine($"PASS: {index} ZIP frames fully decoded and frozen on worker thread; alpha preserved; incorrect dimensions rejected. No network operations.");
});
