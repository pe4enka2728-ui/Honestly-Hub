using System.Text;
using System.Text.Json;
using VpnZapretSwitch;

// Synthetic parser fixtures only. No real subscription, sockets, DPAPI store or processes.
const string key = "vless://00000000-0000-0000-0000-000000000001@example.org:443?security=reality&sni=example.net&fp=chrome&pbk=TEST-PUBLIC-KEY&sid=ab&type=tcp&flow=xtls-rprx-vision#Example";
int passed = 0;
Check("VLESS REALITY fields", () =>
{
    var library = Parse(key);
    var outbound = library.Selected!.Outbounds[0];
    Assert(library.Profiles.Count == 1 && library.Selected.Name == "Example");
    Assert(outbound["streamSettings"]!["realitySettings"]!["serverName"]!.ToString() == "example.net");
    Assert(outbound["settings"]!["vnext"]![0]!["users"]![0]!["flow"]!.ToString() == "xtls-rprx-vision");
});
Check("Base64 subscription", () =>
{
    string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(key + "\n" + "trojan://fake-pass@example.org:443?sni=example.net#Trojan"));
    Assert(Parse(body).Profiles.Count == 2);
});
Check("Duplicate keys collapse", () => Assert(Parse(key + "\n" + key).Profiles.Count == 1));
Check("Unsupported record is reported", () =>
{
    var library = Parse(key + "\nhysteria2://fake@example.org:443");
    Assert(library.Profiles.Count == 1 && library.Skipped == 1);
});
Check("HTML is not a subscription", () => Reject("<html>Login</html>"));
Check("Malformed JSON does not leak its contents", () =>
{
    try { Parse("{\"test-secret\":"); throw new Exception("JSON accepted"); }
    catch (InvalidOperationException ex) { Assert(!ex.Message.Contains("test-secret")); }
});
Check("Xray JSON array", () =>
{
    string body = "[{\"remarks\":\"Node A\",\"outbounds\":[{\"tag\":\"proxy\",\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"example.org\",\"port\":443,\"users\":[{\"id\":\"test-a\",\"encryption\":\"none\"}]}]}}]}]";
    Assert(Parse(body).Profiles.Single().Name == "Node A");
});
Check("Local file parameters in provider config are rejected", () =>
    Reject("{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{},\"streamSettings\":{\"tlsSettings\":{\"keyLog\":\"C:/example.txt\"}}}]}"));
Check("VMess base64 JSON", () =>
{
    string json = JsonSerializer.Serialize(new { v = "2", ps = "Example VMess", add = "example.org", port = "443", id = "00000000-0000-0000-0000-000000000002", aid = "0", net = "ws", host = "example.org", path = "/ws", tls = "tls" });
    Assert(Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))).Selected!.Description.Contains("WS"));
});
Check("Shadowsocks SIP002", () =>
{
    string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:fake-password"));
    Assert(Parse("ss://" + auth + "@example.org:8388#ExampleSS").Selected!.Outbounds[0]["protocol"]!.ToString() == "shadowsocks");
});
Check("UTF8 BOM and whitespace", () => Assert(Parse("\uFEFF\r\n" + key).Profiles.Count == 1));
Check("Same names preserve distinct server identities", () =>
{
    var data = Parse(key + "\n" + key.Replace("000000000001", "000000000002"));
    Assert(data.Profiles.Count == 2 && data.Profiles[0].Id != data.Profiles[1].Id);
});
Check("Loopback SOCKS hop overrides automatic interface binding", () =>
{
    var config = TunnelConfiguration.Build("TEST-TUN", 12001, 12002, "fake-password", "fake-secret", "C:/example/xray.exe");
    Assert(config["outbounds"]![0]!["inet4_bind_address"]!.ToString() == "127.0.0.1");
    Assert(config["inbounds"]![0]!["mtu"]!.GetValue<int>() == 1400);
});
Check("DNS port capture precedes the private network bypass", () =>
{
    var config = TunnelConfiguration.Build("TEST-TUN", 12001, 12002, "fake-password", "fake-secret", "C:/example/xray.exe");
    var rules = config["route"]!["rules"]!.AsArray();
    int dns = rules.ToList().FindIndex(r => r?["port"]?.ToString() == "53" && r?["action"]?.ToString() == "hijack-dns");
    int bypass = rules.ToList().FindIndex(r => r?["ip_is_private"]?.ToString() == "true");
    Assert(dns >= 0 && bypass > dns);
});
Check("Every selectable DNS uses TLS and the VPN detour", () =>
{
    foreach (string provider in VpnDns.Keys)
    {
        var dns = VpnDns.Server(provider);
        Assert(dns["detour"]!.ToString() == "vpn" && dns["server"]!.ToString() == VpnDns.Address(provider));
        Assert(dns["tls"]!["enabled"]!.GetValue<bool>() && dns["tls"]!["server_name"]!.ToString().Contains('.'));
    }
});
Check("DNS health response accepts a compressed IPv4 answer", () =>
{
    byte[] answer = [0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 1, (byte)'x', 0, 0, 1, 0, 1,
        0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 1];
    Assert(TunDnsProbe.ReadAddress(answer, [0x12, 0x34]).ToString() == "192.0.2.1");
    foreach (var bad in new[] { answer[..^1], answer.Select((b, i) => i == 0 ? (byte)0x22 : b).ToArray(), answer.Select((b, i) => i == 3 ? (byte)0x83 : b).ToArray() })
    {
        bool rejected = false;
        try { TunDnsProbe.ReadAddress(bad, [0x12, 0x34]); } catch (IOException) { rejected = true; }
        Assert(rejected);
    }
});
bool rootRejected = false;
try { await SubscriptionDownloader.DownloadAsync("https://get.s-protect.com"); }
catch (InvalidOperationException) { rootRejected = true; }
Assert(rootRejected); passed++;
Console.WriteLine($"PASS: {passed} parser/configuration checks. No real URLs, VPN engines or user storage were used.");

ClientLibrary Parse(string body) => SubscriptionParser.Parse(body, "Example");
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Reject(string body)
{
    bool rejected = false;
    try { Parse(body); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected);
}
static void Assert(bool value) { if (!value) throw new Exception("Parser check failed"); }
