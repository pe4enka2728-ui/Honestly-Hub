using System.Text.Json.Nodes;

namespace VpnZapretSwitch;

internal static class VpnDns
{
    public static readonly string[] Keys = ["cloudflare", "google", "quad9", "adguard"];
    public static readonly string[] Names = ["Cloudflare · 1.1.1.1", "Google · 8.8.8.8", "Quad9 · 9.9.9.9", "AdGuard · 94.140.14.14"];
    public static string Address(string key) => key switch
    {
        "cloudflare" => "1.1.1.1", "google" => "8.8.8.8", "quad9" => "9.9.9.9", "adguard" => "94.140.14.14",
        _ => throw new InvalidOperationException("Выберите DNS-сервер в настройках.")
    };
    public static JsonObject Server(string key) => new()
    {
        ["type"] = "https", ["tag"] = "vpn-dns", ["server"] = Address(key), ["path"] = "/dns-query", ["detour"] = "vpn",
        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = key switch
        { "cloudflare" => "cloudflare-dns.com", "google" => "dns.google", "quad9" => "dns.quad9.net", "adguard" => "dns.adguard-dns.com", _ => throw new InvalidOperationException("Неизвестный DNS.") } }
    };
}
