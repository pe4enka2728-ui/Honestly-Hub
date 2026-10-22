using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;

namespace VpnZapretSwitch;

internal static class SpotifyImageCache
{
    private static readonly HttpClient http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly ConcurrentDictionary<string, Image?> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> loading = new(StringComparer.OrdinalIgnoreCase);

    internal static Image? GetImage(string? url, Action? onLoaded = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (cache.TryGetValue(url, out var image))
            return image;

        if (loading.TryAdd(url, true))
        {
            _ = Task.Run(async () =>
            {
                Image? loaded = null;
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (bytes.Length is > 0 and <= 5 * 1024 * 1024)
                        {
                            using var ms = new MemoryStream(bytes, writable: false);
                            using var original = Image.FromStream(ms);
                            int thumbSize = 80;
                            var thumb = new Bitmap(thumbSize, thumbSize);
                            using (var g = Graphics.FromImage(thumb))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(original, new Rectangle(0, 0, thumbSize, thumbSize));
                            }
                            loaded = thumb;
                        }
                    }
                }
                catch
                {
                    // Network or image decode failure: cached as null to avoid spamming
                }
                finally
                {
                    cache[url] = loaded;
                    loading.TryRemove(url, out _);
                    if (loaded is not null && onLoaded is not null)
                    {
                        try { onLoaded(); } catch { }
                    }
                }
            });
        }

        return null;
    }
}
