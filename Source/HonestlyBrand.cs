using System.Reflection;

namespace VpnZapretSwitch;

internal static class HonestlyBrand
{
    public static Image Logo { get; } = LoadLogo();
    private static Image LoadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HonestlyTUN.logo.png")
            ?? throw new InvalidOperationException("Не найден логотип Honestly TUN.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
    public static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HonestlyTUN.icon.ico")
            ?? throw new InvalidOperationException("Не найдена иконка Honestly TUN.");
        return new Icon(stream);
    }
}
