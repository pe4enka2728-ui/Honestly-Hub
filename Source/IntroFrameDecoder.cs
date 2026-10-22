using System.IO;
using System.Windows.Media.Imaging;

namespace VpnZapretSwitch;

internal static class IntroFrameDecoder
{
    internal static BitmapFrame Decode(Stream input, int width, int height)
    {
        // ZIP entry streams cannot seek. WPF treats those as asynchronous
        // downloads and can return a 1x1 placeholder even with OnLoad. A private
        // seekable buffer makes decoding complete before size checks/Freeze().
        using var png = new MemoryStream();
        input.CopyTo(png); png.Position = 0;
        var bitmap = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (bitmap.IsDownloading || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            throw new InvalidDataException($"Размер PNG-кадра {bitmap.PixelWidth}×{bitmap.PixelHeight}, ожидается {width}×{height}.");
        bitmap.Freeze();
        return bitmap;
    }
}
