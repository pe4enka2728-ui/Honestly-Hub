using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace VpnZapretSwitch;

internal sealed class ClientProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<JsonObject> Outbounds { get; set; } = [];
}
internal sealed class ClientLibrary
{
    public string SubscriptionUrl { get; set; } = "";
    public string SourceName { get; set; } = "Локальный импорт";
    public DateTimeOffset Updated { get; set; }
    public string SelectedId { get; set; } = "";
    public List<ClientProfile> Profiles { get; set; } = [];
    public int Skipped { get; set; }
    [JsonIgnore] public ClientProfile? Selected => Profiles.FirstOrDefault(p => p.Id == SelectedId);
}

internal static class ClientStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestlyTUN", "client");
    private static string FilePath => Path.Combine(Root, "profiles.dat");
    public static void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(Root);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Не определён пользователь Windows.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
    }
    public static ClientLibrary Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            byte[] bytes = ProtectedBytes.Transform(File.ReadAllBytes(FilePath), false);
            try { return JsonSerializer.Deserialize<ClientLibrary>(bytes) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException)
        { throw new IOException("Не удалось открыть сохранённую подписку этого пользователя Windows. Добавьте её заново."); }
    }
    public static void Save(ClientLibrary library)
    {
        EnsurePrivateDirectory();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(library);
        try
        {
            byte[] encrypted = ProtectedBytes.Transform(plain, true);
            string temporary = FilePath + ".new";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

internal static class ProtectedBytes
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    public static byte[] Transform(byte[] data, bool protect)
    {
        var source = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, source.Data, data.Length);
            Blob output;
            bool ok = protect ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
        finally
        {
            for (int i = 0; i < data.Length; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

internal static class SubscriptionParser
{
    private static readonly HashSet<string> ProxyProtocols = new(StringComparer.OrdinalIgnoreCase)
        { "vless", "vmess", "trojan", "shadowsocks" };
    public static ClientLibrary Parse(string body, string sourceName, string sourceUrl = "")
    {
        if (body.Length > 8_000_000) throw new InvalidOperationException("Подписка превышает 8 МБ.");
        body = body.Trim().TrimStart('\uFEFF').Trim();
        if (body.StartsWith('<')) throw new InvalidOperationException("Вместо подписки получена веб-страница. Вставьте полную персональную ссылку из кабинета провайдера.");
        if (!body.StartsWith('{') && !body.StartsWith('[') && !body.Contains("://", StringComparison.Ordinal))
        {
            try { body = DecodeBase64(body).Trim().TrimStart('\uFEFF').Trim(); }
            catch (FormatException) { throw new InvalidOperationException("Не распознан формат подписки. Поддерживаются ссылки VLESS, VMess, Trojan, Shadowsocks, Base64 и JSON Xray."); }
        }
        var library = new ClientLibrary { SourceName = Clean(sourceName), SubscriptionUrl = sourceUrl, Updated = DateTimeOffset.UtcNow };
        if (body.StartsWith('{') || body.StartsWith('['))
        {
            JsonNode node;
            try { node = JsonNode.Parse(body, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 48 })!; }
            catch (JsonException) { throw new InvalidOperationException("Сервер вернул некорректный JSON подписки."); }
            ReadNode(node);
        }
        else
        {
            foreach (string line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#')) continue;
                try { Add(FromLink(line)); }
                catch (Exception ex) when (ex is FormatException or UriFormatException or JsonException or InvalidOperationException or ArgumentException)
                { library.Skipped++; }
            }
        }
        library.Profiles = library.Profiles.DistinctBy(p => p.Id).Take(1000).ToList();
        if (library.Profiles.Count == 0) throw new InvalidOperationException("В подписке нет поддерживаемых серверов. Нужны VLESS/VMess/Trojan/Shadowsocks или JSON Xray. Проверьте формат экспорта у провайдера.");
        library.SelectedId = library.Profiles[0].Id;
        return library;

        void ReadNode(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) ReadNode(child); return; }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var link))
            { try { Add(FromLink(link)); } catch { library.Skipped++; } return; }
            if (node is not JsonObject item) { library.Skipped++; return; }
            foreach (string container in new[] { "configs", "profiles", "servers" })
                if (item[container] is JsonArray children) { ReadNode(children); return; }
            try
            {
                if (item["outbounds"] is JsonArray outbounds)
                {
                    var proxies = outbounds.OfType<JsonObject>().Where(o => ProxyProtocols.Contains(Text(o, "protocol"))).ToArray();
                    if (proxies.Length != 1) throw new InvalidOperationException("Неоднозначная конфигурация Xray.");
                    var profile = MakeProfile(Text(item, "remarks", Text(item, "name", Text(item, "ps", "Сервер"))), proxies[0]);
                    foreach (var dependency in outbounds.OfType<JsonObject>().Where(o => !ReferenceEquals(o, proxies[0])))
                    {
                        if (Text(dependency, "protocol") is not ("freedom" or "blackhole"))
                            throw new InvalidOperationException("Неподдерживаемая цепочка выходов.");
                        string tag = Text(dependency, "tag");
                        if (tag.Length == 0 || tag.StartsWith("honestly-", StringComparison.Ordinal)) throw new InvalidOperationException();
                        var copy = (JsonObject)dependency.DeepClone(); ValidateNoFiles(copy); profile.Outbounds.Add(copy);
                    }
                    Add(profile); return;
                }
                if (ProxyProtocols.Contains(Text(item, "protocol"))) { Add(MakeProfile(Text(item, "remarks", Text(item, "tag", "Сервер")), item)); return; }
                throw new InvalidOperationException();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FormatException)
            { library.Skipped++; }
        }
        void Add(ClientProfile profile)
        {
            if (library.Profiles.Count >= 1000) { library.Skipped++; return; }
            var tags = profile.Outbounds.Select(o => o["tag"]?.ToString() ?? "").ToArray();
            if (tags.Any(string.IsNullOrWhiteSpace) || tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
                throw new InvalidOperationException("Неоднозначные теги выходов.");
            profile.Id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile.Outbounds)));
            library.Profiles.Add(profile);
        }
    }

    private static ClientProfile MakeProfile(string name, JsonObject outbound)
    {
        var copy = (JsonObject)outbound.DeepClone();
        ValidateNoFiles(copy);
        if (!ProxyProtocols.Contains(Text(copy, "protocol"))) throw new InvalidOperationException("Протокол не поддерживается.");
        copy["protocol"] = Text(copy, "protocol").ToLowerInvariant();
        if (copy["settings"] is not JsonObject settings) throw new InvalidOperationException("Нет параметров сервера.");
        string protocol = Text(copy, "protocol");
        if (protocol == "vless" && settings["address"] is not null && settings["id"] is not null)
        {
            var user = new JsonObject { ["id"] = settings["id"]!.DeepClone(), ["encryption"] = Text(settings, "encryption", "none"), ["flow"] = Text(settings, "flow") };
            var server = new JsonObject { ["address"] = settings["address"]!.DeepClone(), ["port"] = settings["port"]?.DeepClone(), ["users"] = new JsonArray(user) };
            copy["settings"] = settings = new JsonObject { ["vnext"] = new JsonArray(server) };
        }
        string container = protocol is "vless" or "vmess" ? "vnext" : "servers";
        if (settings[container] is not JsonArray servers || servers.Count == 0) throw new InvalidOperationException("Не указан сервер подключения.");
        if (servers.OfType<JsonObject>().Count() != servers.Count) throw new InvalidOperationException("Некорректная запись сервера.");
        foreach (var server in servers.OfType<JsonObject>())
        {
            if (Text(server, "address").Length == 0 || !int.TryParse(Text(server, "port"), out int port) || port is < 1 or > 65535)
                throw new InvalidOperationException("Недопустимый адрес или порт сервера.");
            server["port"] = port;
            if (protocol is "vless" or "vmess")
            {
                if (server["users"] is not JsonArray users || users.Count == 0 || users.OfType<JsonObject>().Count() != users.Count || users.OfType<JsonObject>().Any(u => Text(u, "id").Length == 0))
                    throw new InvalidOperationException("Не указан идентификатор подключения.");
            }
            else if (Text(server, "password").Length == 0) throw new InvalidOperationException("Не указан пароль подключения.");
        }
        copy["tag"] = "vpn";
        copy.Remove("sendThrough");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString())));
        string transport = copy["streamSettings"] is JsonObject stream ? Text(stream, "network", "tcp") : "tcp";
        return new() { Id = id, Name = Clean(name), Description = Text(copy, "protocol").ToUpperInvariant() + " · " + transport.ToUpperInvariant(), Outbounds = [copy] };
    }
    private static void ValidateNoFiles(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj)
            {
                if (pair.Key.Equals("keyLog", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("masterKeyLog", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("reverse", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("script", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("certificates", StringComparison.OrdinalIgnoreCase) || pair.Key.EndsWith("File", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Файловые параметры внешней конфигурации не поддерживаются.");
                ValidateNoFiles(pair.Value);
            }
        else if (node is JsonArray array) foreach (var child in array) ValidateNoFiles(child);
    }
    private static ClientProfile FromLink(string text)
    {
        if (text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return Vmess(text[8..]);
        if (text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return Shadowsocks(text);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("vless" or "trojan") || uri.UserInfo.Length == 0)
            throw new FormatException("Неизвестный ключ.");
        var values = Query(uri.Query);
        string host = uri.Host.Trim('[', ']');
        int port = uri.Port > 0 ? uri.Port : 443;
        string user = Uri.UnescapeDataString(uri.UserInfo);
        var outbound = new JsonObject { ["protocol"] = uri.Scheme };
        if (uri.Scheme == "vless") outbound["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject {
            ["address"] = host, ["port"] = port, ["users"] = new JsonArray(new JsonObject { ["id"] = user,
                ["encryption"] = Get(values, "encryption", "none"), ["flow"] = Get(values, "flow") }) }) };
        else outbound["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = host, ["port"] = port, ["password"] = user }) };
        outbound["streamSettings"] = Stream(values, host, uri.Scheme == "trojan" ? "tls" : "none");
        string name = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : host;
        return MakeProfile(name, outbound);
    }
    private static JsonObject Stream(Dictionary<string, string> values, string host, string defaultSecurity)
    {
        string network = Get(values, "type", "tcp").ToLowerInvariant();
        if (network == "h2") network = "http";
        if (network is not ("tcp" or "raw" or "ws" or "grpc" or "httpupgrade" or "xhttp" or "splithttp"))
            throw new FormatException("Транспорт ключа не поддерживается.");
        string security = Get(values, "security", defaultSecurity).ToLowerInvariant();
        var stream = new JsonObject { ["network"] = network, ["security"] = security };
        if (security is "tls" or "reality")
        {
            var tls = new JsonObject { ["serverName"] = Get(values, "sni", host), ["fingerprint"] = Get(values, "fp", "chrome") };
            if (security == "reality") { tls["publicKey"] = Get(values, "pbk"); tls["shortId"] = Get(values, "sid"); tls["spiderX"] = Get(values, "spx", "/"); }
            else
            {
                tls["allowInsecure"] = Get(values, "allowInsecure") is "1" or "true";
                string alpn = Get(values, "alpn"); if (alpn.Length > 0) tls["alpn"] = new JsonArray(alpn.Split(',').Select(s => JsonValue.Create(s)).ToArray());
            }
            stream[security + "Settings"] = tls;
        }
        else if (security != "none") throw new FormatException("Защита транспорта не поддерживается.");
        string path = Get(values, "path", "/"), header = Get(values, "host", host);
        if (network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = path, ["headers"] = new JsonObject { ["Host"] = header } };
        if (network == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get(values, "serviceName"), ["multiMode"] = Get(values, "mode") == "multi" };
        if (network == "httpupgrade") stream["httpupgradeSettings"] = new JsonObject { ["host"] = header, ["path"] = path };
        if (network is "xhttp" or "splithttp")
        {
            var settings = new JsonObject { ["host"] = header, ["path"] = path, ["mode"] = Get(values, "mode", "auto") };
            if (Get(values, "extra") is { Length: > 0 } extra) { settings["extra"] = JsonNode.Parse(extra); ValidateNoFiles(settings); }
            stream["xhttpSettings"] = settings;
        }
        return stream;
    }
    private static ClientProfile Vmess(string text)
    {
        var item = JsonNode.Parse(DecodeBase64(text)) as JsonObject ?? throw new FormatException();
        string host = Text(item, "add"), id = Text(item, "id");
        if (host.Length == 0 || id.Length == 0 || !int.TryParse(Text(item, "port"), out int port) || port is < 1 or > 65535) throw new FormatException();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = Text(item, "net", "tcp"), ["security"] = Text(item, "tls") == "tls" ? "tls" : "none", ["host"] = Text(item, "host", host), ["path"] = Text(item, "path", "/"), ["sni"] = Text(item, "sni", host), ["fp"] = Text(item, "fp", "chrome"), ["serviceName"] = Text(item, "path") };
        int.TryParse(Text(item, "aid", "0"), out int aid);
        return MakeProfile(Text(item, "ps", host), new JsonObject { ["protocol"] = "vmess", ["settings"] = new JsonObject {
            ["vnext"] = new JsonArray(new JsonObject { ["address"] = host, ["port"] = port, ["users"] = new JsonArray(new JsonObject { ["id"] = id, ["alterId"] = aid, ["security"] = Text(item, "scy", "auto") }) }) }, ["streamSettings"] = Stream(values, host, "none") });
    }
    private static ClientProfile Shadowsocks(string text)
    {
        string content = text[5..];
        int fragment = content.IndexOf('#');
        string name = fragment >= 0 ? Uri.UnescapeDataString(content[(fragment + 1)..]) : "Shadowsocks";
        if (fragment >= 0) content = content[..fragment];
        if (content.Contains("plugin=", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Плагины Shadowsocks не поддерживаются.");
        content = content.Split('?', 2)[0];
        if (content.Contains('@')) content = content.TrimEnd('/');
        if (!content.Contains('@')) content = DecodeBase64(content);
        int at = content.LastIndexOf('@'); if (at < 1) throw new FormatException();
        string auth = Uri.UnescapeDataString(content[..at]);
        if (!auth.Contains(':')) auth = DecodeBase64(auth);
        int colon = auth.IndexOf(':'); if (colon < 1) throw new FormatException();
        if (!Uri.TryCreate("ss://" + content[(at + 1)..], UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535) throw new FormatException();
        return MakeProfile(name, new JsonObject { ["protocol"] = "shadowsocks", ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject {
            ["address"] = uri.Host.Trim('[', ']'), ["port"] = uri.Port, ["method"] = auth[..colon], ["password"] = auth[(colon + 1)..] }) } });
    }
    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0]);
            if (!values.TryAdd(key, parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "")) throw new FormatException();
        }
        return values;
    }
    private static string Get(Dictionary<string, string> values, string key, string fallback = "") => values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
    private static string Text(JsonObject item, string key, string fallback = "") => item[key]?.ToString() is { Length: > 0 } value ? value : fallback;
    private static string Clean(string value) => value.Contains("://", StringComparison.Ordinal) ? "Сервер" : new(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
    private static string DecodeBase64(string value)
    {
        value = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
    }
}

internal static class SubscriptionDownloader
{
    public static async Task<ClientLibrary> DownloadAsync(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Вставьте полную HTTPS-ссылку подписки.");
        if (uri.AbsolutePath == "/" && uri.Query.Length == 0)
            throw new InvalidOperationException("Указан только домен провайдера. Нужна полная персональная ссылка подписки из вашего кабинета.");
        string source = uri.Host;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HonestlyTUN/2.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.5");
        using (var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
        {
            string identity = machine?.GetValue("MachineGuid") as string ?? Environment.MachineName;
            string deviceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("HonestlyTUN:" + identity + ":" + WindowsIdentity.GetCurrent().User?.Value))).ToLowerInvariant();
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-hwid", deviceId);
        }
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-device-os", "Windows");
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-device-model", "Honestly TUN");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            for (int redirects = 0; redirects < 6; redirects++)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    uri = response.Headers.Location is { } target ? new Uri(uri, target) : throw new IOException();
                    if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidOperationException("Подписка перенаправляет на небезопасный адрес.");
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Сервер подписки вернул HTTP {(int)response.StatusCode}. Проверьте ссылку, срок подписки и привязку устройства у провайдера.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var data = new MemoryStream(); byte[] buffer = new byte[16384]; int length;
                while ((length = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (data.Length + length > 8_000_000) throw new InvalidOperationException("Подписка превышает 8 МБ.");
                    data.Write(buffer, 0, length);
                }
                return await Task.Run(() => SubscriptionParser.Parse(Encoding.UTF8.GetString(data.ToArray()), source, address.Trim()));
            }
            throw new InvalidOperationException("Слишком много перенаправлений подписки.");
        }
        catch (HttpRequestException) { throw new IOException("Не удалось загрузить подписку по HTTPS. Проверьте интернет и доступность провайдера."); }
        catch (OperationCanceledException) { throw new IOException("Истекло время загрузки подписки (30 секунд)."); }
    }
}
