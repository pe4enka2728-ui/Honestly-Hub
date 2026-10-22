using Microsoft.Win32;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VpnZapretSwitch;

// An INCY HTTP proxy left enabled after disconnect bypasses the browser's TUN route.
// Pause only a dead loopback proxy whose port is present in INCY's own inbound metadata.
// Never overwrite unrelated proxies, PAC settings, or changes made during the session.
internal sealed class IncyProxyPause : IDisposable
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly string server;
    private IncyProxyPause(string server) => this.server = server;
    public static IncyProxyPause? TryPause(Action<string>? report)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key?.GetValue("ProxyEnable") is not int enabled || enabled != 1) return null;
        string server = key.GetValue("ProxyServer") as string ?? "";
        var ports = IncyPorts();
        var endpoints = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (endpoints.Length == 0 || ports.Count == 0) return null;
        var listeners = NativeTcpListeners.Read().ToArray();
        foreach (string endpoint in endpoints)
        {
            string address = endpoint.Contains('=') ? endpoint[(endpoint.IndexOf('=') + 1)..] : endpoint;
            if (!Uri.TryCreate(address.Contains("://") ? address : "http://" + address, UriKind.Absolute, out var uri)
                || !uri.IsLoopback || !ports.Contains(uri.Port)
                || listeners.Any(l => l.Local && l.Port == uri.Port)) return null;
        }
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
        report?.Invoke("Приостановлен оставшийся системный прокси INCY. Браузер будет использовать TUN; после отключения настройка восстановится.");
        return new(server);
    }
    private static HashSet<int> IncyPorts()
    {
        var result = new HashSet<int>();
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "incy", "cache");
        foreach (string name in new[] { "xray_config.json", "xray-config.json" })
        {
            try
            {
                using var stream = new FileStream(Path.Combine(cache, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("inbounds", out var inbounds))
                    foreach (var inbound in inbounds.EnumerateArray())
                        if (inbound.TryGetProperty("protocol", out var protocol) && protocol.GetString() is "http" or "socks" or "mixed"
                            && inbound.TryGetProperty("port", out var port) && int.TryParse(port.ToString(), out int value)) result.Add(value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }
    public void Dispose()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key?.GetValue("ProxyEnable") is int enabled && enabled == 0 && Equals(key.GetValue("ProxyServer"), server))
        { key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord); Refresh(); }
    }
    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
    [DllImport("wininet.dll")] private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}
