using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VpnZapretSwitch;

internal static class CountryFlag
{
    // Bundled Twemoji flags: Segoe UI on Windows often displays regional-indicator
    // emoji as two letters. No system emoji font or online image request is required.
    private static readonly (string Code, string[] Names)[] Countries =
    [
        ("NL", ["нидерланд", "netherlands", "holland"]), ("DE", ["германи", "germany", "deutschland"]),
        ("EE", ["эстони", "estonia"]), ("RU", ["росси", "russia"]), ("FI", ["финлян", "finland"]),
        ("FR", ["франц", "france"]), ("GB", ["великобрит", "британи", "англи", "united kingdom", "england", "britain"]),
        ("US", ["сша", "америк", "united states", "usa"]), ("PL", ["польш", "poland"]),
        ("UA", ["украин", "ukraine"]), ("LT", ["литва", "литвы", "lithuania"]), ("LV", ["латви", "latvia"]),
        ("SE", ["швеци", "sweden"]), ("NO", ["норвег", "norway"]), ("DK", ["дани", "denmark"]),
        ("CH", ["швейцар", "switzerland"]), ("AT", ["австри", "austria"]), ("IT", ["итали", "italy"]),
        ("ES", ["испани", "spain"]), ("BE", ["бельги", "belgium"]), ("RO", ["румыни", "romania"]),
        ("BG", ["болгари", "bulgaria"]), ("CZ", ["чехи", "czech"]), ("HU", ["венгри", "hungary"]),
        ("IE", ["ирланди", "ireland"]), ("JP", ["япони", "japan"]), ("TR", ["турци", "turkey", "türkiye"]),
        ("EU", ["европ", "europe", "gemini"])
    ];
    public static string DisplayName(string name) => string.Concat(name.EnumerateRunes().Where(r => r.Value is < 0x1F1E6 or > 0x1F1FF).Select(r => r.ToString())).Trim();
    private static string Code(string name)
    {
        var runes = name.EnumerateRunes().ToArray();
        for (int i = 0; i + 1 < runes.Length; i++)
            if (runes[i].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[i + 1].Value is >= 0x1F1E6 and <= 0x1F1FF)
                return "" + (char)('A' + runes[i].Value - 0x1F1E6) + (char)('A' + runes[i + 1].Value - 0x1F1E6);
        foreach (var country in Countries)
            if (country.Names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))
                || Regex.IsMatch(name, @"(?<![\p{L}])" + country.Code + @"(?![\p{L}])", RegexOptions.IgnoreCase)) return country.Code;
        foreach (var region in Regions)
            if (region.Names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return region.Code;
        var prefix = Regex.Match(name, @"^\W*([A-Za-z]{2})(?:[\s_\-:/|]|$)");
        return prefix.Success ? prefix.Groups[1].Value.ToUpperInvariant() : "";
    }
    private static readonly (string Code, string[] Names)[] Regions = ReadRegions();
    private static readonly Dictionary<string, Bitmap?> Images = new(StringComparer.Ordinal);
    private static (string, string[])[] ReadRegions()
    {
        var regions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (!regions.TryGetValue(region.TwoLetterISORegionName, out var names)) regions[region.TwoLetterISORegionName] = names = new(StringComparer.OrdinalIgnoreCase);
                foreach (string name in new[] { region.EnglishName, region.NativeName, region.DisplayName }) if (name.Length >= 4) names.Add(name);
            }
            catch (ArgumentException) { }
        }
        return regions.Select(r => (r.Key, r.Value.OrderByDescending(n => n.Length).ToArray())).OrderByDescending(r => r.Item2.Max(n => n.Length)).ToArray();
    }
    public static void Draw(Graphics g, Rectangle bounds, string name)
    {
        string code = Code(name);
        if (!Images.TryGetValue(code, out var image))
        {
            using var stream = typeof(CountryFlag).Assembly.GetManifestResourceStream("HonestlyTUN.flags." + code + ".png");
            using var original = stream is null ? null : Image.FromStream(stream);
            Images[code] = image = original is null ? null : new Bitmap(original);
        }
        if (image is not null) { g.DrawImage(image, bounds); return; }
        // No location in the name: do not invent the server's country.
        var r = new Rectangle(bounds.X + bounds.Width / 5, bounds.Y + bounds.Height / 5, bounds.Width * 3 / 5, bounds.Height * 3 / 5);
        using var pen = new Pen(Astra.Muted, Math.Max(1, bounds.Width / 28f));
        g.DrawEllipse(pen, r);
        g.DrawEllipse(pen, r.X + r.Width / 3, r.Y, Math.Max(1, r.Width / 3), r.Height);
        g.DrawLine(pen, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
    }
}