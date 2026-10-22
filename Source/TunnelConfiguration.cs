using System.Text.Json.Nodes;

namespace VpnZapretSwitch;

internal static class TunnelConfiguration
{
    public static JsonObject Build(string name, int socks, int api, string password, string secret, string proxyPath, string dnsProvider = "cloudflare") => new()
    {
        ["log"] = new JsonObject { ["disabled"] = true },
        ["dns"] = new JsonObject { ["servers"] = new JsonArray(VpnDns.Server(dnsProvider)), ["final"] = "vpn-dns", ["strategy"] = "ipv4_only" },
        ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = name,
            ["address"] = new JsonArray("172.29.245.1/30", "fdfe:dcba:9876::1/126"), ["mtu"] = 1400, ["auto_route"] = true, ["strict_route"] = true, ["dns_mode"] = "hijack", ["stack"] = "mixed" }),
        // An explicit loopback source disables automatic physical-interface binding
        // on the local SOCKS hop. The remote Xray socket is bound separately.
        ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "vpn", ["server"] = "127.0.0.1", ["server_port"] = socks, ["version"] = "5", ["username"] = "honestly", ["password"] = password, ["inet4_bind_address"] = "127.0.0.1" }, new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
        ["route"] = new JsonObject { ["auto_detect_interface"] = true, ["rules"] = new JsonArray(
            new JsonObject { ["process_path"] = new JsonArray(proxyPath), ["outbound"] = "direct" },
            new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }), ["final"] = "vpn" },
        ["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + api, ["secret"] = secret } }
    };

}
