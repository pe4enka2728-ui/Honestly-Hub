using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal static class CoreCatalog
{
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Runtime"));
    public static string Xray => Path.Combine(Root, "xray", "xray.exe");
    public static string Tunnel => Path.Combine(Root, "sing-box", "sing-box.exe");
    private static readonly Dictionary<string, (long Size, DateTime Write)> Verified = [];
    public static void Verify()
    {
        using var stream = typeof(CoreCatalog).Assembly.GetManifestResourceStream("HonestlyTUN.CoreHashes.json")!;
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        foreach (var pair in hashes)
        {
            string file = Path.Combine(Root, pair.Key);
            var info = new FileInfo(file);
            if (!info.Exists) throw new IOException("Не найдено комплектное VPN-ядро. Распакуйте архив целиком, сохранив папку Runtime рядом с App.");
            if (Verified.TryGetValue(file, out var previous) && previous == (info.Length, info.LastWriteTimeUtc)) continue;
            using var input = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Файлы VPN-ядра отличаются от этой сборки. Распакуйте официальный комплект Honestly TUN заново.");
            Verified[file] = (info.Length, info.LastWriteTimeUtc);
        }
    }
}

internal sealed class EmbeddedVpnHost : IDisposable
{
    public static EmbeddedVpnHost Shared { get; } = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClientLibrary library;
    private string libraryError = "";
    private PseudoConsoleSession? proxy, tunnel;
    private volatile bool starting;
    private bool connected;
    private int proxyPort, apiPort;
    private string socksPassword = "", apiSecret = "";
    private Stopwatch? connectionClock;
    private string? tunnelName;
    private IncyProxyPause? pausedProxy;
    public bool AnyProcess => proxy is { HasExited: false } || tunnel is { HasExited: false };
    public bool HasSession => AnyProcess || tunnelName is not null;
    public bool HasProfiles => library.Profiles.Count > 0;
    public string Summary => libraryError.Length > 0 ? libraryError : HasProfiles
        ? $"{library.SourceName} · {library.Profiles.Count} серверов · {library.Updated.ToLocalTime():dd.MM HH:mm}" : "Подписка ещё не добавлена";

    private EmbeddedVpnHost()
    {
        try { library = ClientStore.Load(); }
        catch (Exception ex) { library = new(); libraryError = ex.Message; }
    }

    public IncyRuntimeStatus ReadStatus()
    {
        bool alive = AnyProcess;
        if (starting) return new(true, alive, VpnState.Starting, "Встроенный VPN подключается…");
        if (alive && connected && proxy is { HasExited: false } && tunnel is { HasExited: false })
            return new(true, true, VpnState.Active, "Собственное подключение · TUN активен");
        if (alive) return new(true, true, VpnState.Starting, "Одно из ядер остановилось. Нажмите «Вернуться к Zapret» для очистки.");
        if (tunnelName is not null) return new(true, false, VpnState.Starting, "Требуется завершить очистку TUN. Нажмите «Вернуться к Zapret».");
        return new(false, false, VpnState.Disconnected, HasProfiles ? "Встроенный VPN отключён" : "Добавьте персональную ссылку подписки");
    }

    public async Task ImportAsync(string value, bool fromFile)
    {
        await gate.WaitAsync();
        try
        {
            if (AnyProcess || starting) throw new InvalidOperationException("Отключите встроенный VPN перед обновлением подписки.");
            ClientLibrary incoming;
            if (!fromFile && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) incoming = await SubscriptionDownloader.DownloadAsync(value);
            else incoming = await Task.Run(() => SubscriptionParser.Parse(value, "Локальный импорт"));
            if (incoming.Profiles.Any(p => p.Id == library.SelectedId)) incoming.SelectedId = library.SelectedId;
            else if (library.Selected is { } old)
            {
                var matching = incoming.Profiles.Where(p => p.Name == old.Name && p.Description == old.Description).ToArray();
                if (matching.Length == 1) incoming.SelectedId = matching[0].Id;
            }
            await Task.Run(() => ClientStore.Save(incoming));
            library = incoming; libraryError = "";
        }
        finally { gate.Release(); }
    }
    public async Task UpdateAsync()
    {
        string address = library.SubscriptionUrl;
        if (address.Length == 0) throw new InvalidOperationException("Для локального импорта нет URL обновления. Добавьте подписку или импортируйте файл заново.");
        await ImportAsync(address, false);
    }
    public int Skipped => library.Skipped;

    public async Task SelectAsync(string id, Settings settings, Action<string> report)
    {
        await gate.WaitAsync();
        try
        {
            if (!library.Profiles.Any(p => p.Id == id)) throw new InvalidOperationException("Сервер исчез после обновления подписки.");
            if (library.SelectedId == id) return;
            string old = library.SelectedId;
            bool restart = AnyProcess;
            if (restart) { report("Останавливаем встроенный VPN для смены сервера…"); await StopCoreAsync(); }
            library.SelectedId = id;
            try
            {
                if (restart) { report("Подключаем новый сервер…"); await StartCoreAsync(settings, report); }
                await Task.Run(() => ClientStore.Save(library));
            }
            catch (Exception failure)
            {
                await StopCoreAsync(); library.SelectedId = old;
                if (restart)
                {
                    try { report("Восстанавливаем предыдущий сервер…"); await StartCoreAsync(settings, report); }
                    catch { await StopCoreAsync(); throw new IOException(failure.Message + " Предыдущий сервер также не подключился; VPN отключён."); }
                }
                throw new IOException(failure.Message + (restart ? " Предыдущее подключение восстановлено." : " Предыдущий сервер сохранён."));
            }
        }
        finally { gate.Release(); }
    }
    public async Task ConnectAsync(Settings settings, Action<string>? report)
    {
        await gate.WaitAsync();
        try { await StartCoreAsync(settings, report); }
        catch { await StopCoreAsync(); throw; }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task StartCoreAsync(Settings settings, Action<string>? report)
    {
        if (HasSession) throw new InvalidOperationException("Сначала остановите предыдущее встроенное подключение.");
        var selected = library.Selected ?? throw new InvalidOperationException("Добавьте подписку и выберите сервер.");
        var external = new IncyProbe(settings, WindowsPlatform.ExactProcesses).Read();
        if (external.IsRunning || external.State == VpnState.Unknown)
            throw new InvalidOperationException("Отключите VPN в INCY перед запуском встроенного клиента.");
        CoreCatalog.Verify();
        ClientStore.EnsurePrivateDirectory();
        starting = true; connected = false;
        string configId = Guid.NewGuid().ToString("N");
        string proxyConfig = Path.Combine(ClientStore.Root, "core-" + configId + "-xray.json");
        string tunnelConfig = Path.Combine(ClientStore.Root, "core-" + configId + "-tun.json");
        try
        {
            foreach (string stale in Directory.EnumerateFiles(ClientStore.Root, "core-*.json")) File.Delete(stale);
            proxyPort = FreePort(); do { apiPort = FreePort(); } while (apiPort == proxyPort);
            socksPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            apiSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            tunnelName = "Honestly-" + configId[..8];
            var outbounds = new JsonArray(selected.Outbounds.Select(o => o.DeepClone()).ToArray());
            string physical = PhysicalInterface();
            foreach (var node in outbounds.OfType<JsonObject>())
            {
                var stream = node["streamSettings"] as JsonObject;
                if (stream is null) node["streamSettings"] = stream = new();
                var socket = stream["sockopt"] as JsonObject;
                if (socket is null) stream["sockopt"] = socket = new();
                socket["interface"] = physical;
                node.Remove("sendThrough");
                await ResolveEndpoints(node, settings.TimeoutSeconds);
            }
            var xray = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "none" },
                ["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "127.0.0.1", ["port"] = proxyPort, ["protocol"] = "socks", ["tag"] = "honestly-socks",
                    ["settings"] = new JsonObject { ["auth"] = "password", ["accounts"] = new JsonArray(new JsonObject { ["user"] = "honestly", ["pass"] = socksPassword }), ["udp"] = true, ["ip"] = "127.0.0.1" } }),
                ["outbounds"] = outbounds
            };
            await File.WriteAllTextAsync(proxyConfig, xray.ToJsonString());
            report?.Invoke("Запускаем собственное ядро подключения…");
            proxy = new(CoreCatalog.Xray, ["run", "-c", proxyConfig], Path.GetDirectoryName(CoreCatalog.Xray)!);
            await WaitCore(() => OwnsPort(proxy, proxyPort), proxy, settings.TimeoutSeconds, "Ядро подключения не открыло свой локальный порт. Проверьте формат сервера.");
            File.Delete(proxyConfig);
            if (settings.VerifyInternet)
            {
                report?.Invoke("Проверяем ответ сервера через VPN…");
                await VerifyProxyAsync();
            }
            var tun = BuildTunnelConfig(tunnelName, proxyPort, apiPort, socksPassword, apiSecret, CoreCatalog.Xray, settings.DnsProvider);
            await File.WriteAllTextAsync(tunnelConfig, tun.ToJsonString());
            report?.Invoke("Подключаем системный TUN…");
            tunnel = new(CoreCatalog.Tunnel, ["run", "-c", tunnelConfig], Path.GetDirectoryName(CoreCatalog.Tunnel)!);
            await WaitCore(() => OwnsPort(tunnel, apiPort) && NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == tunnelName && n.OperationalStatus == OperationalStatus.Up),
                tunnel, settings.TimeoutSeconds, "TUN не перешёл в рабочее состояние. Проверьте права администратора и отсутствие другого VPN.");
            using (await ReadApiAsync("version")) { }
            await Task.Delay(1200);
            if (proxy.HasExited || tunnel.HasExited) throw new IOException("VPN-ядро завершилось после запуска.");
            if (settings.VerifyInternet)
            {
                report?.Invoke("Проверяем DNS и HTTPS через готовый системный TUN…");
                await VerifyTunnelAsync(tunnelName);
            }
            pausedProxy = IncyProxyPause.TryPause(report);
            File.Delete(tunnelConfig);
            connectionClock = Stopwatch.StartNew(); connected = true;
        }
        finally
        {
            starting = false;
            if (File.Exists(proxyConfig)) File.Delete(proxyConfig);
            if (File.Exists(tunnelConfig)) File.Delete(tunnelConfig);
        }
    }

    internal static JsonObject BuildTunnelConfig(string name, int socks, int api, string password, string secret, string proxyPath, string dnsProvider = "cloudflare") => TunnelConfiguration.Build(name, socks, api, password, secret, proxyPath, dnsProvider);

    private static async Task VerifyTunnelAsync(string name)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellation) =>
            {
                var destination = await TunDnsProbe.ResolveAsync(context.DnsEndPoint.Host);
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name)
                    ?? throw new IOException("TUN-интерфейс исчез во время проверки.");
                if (GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out uint index) != 0
                    || nic.GetIPProperties().GetIPv4Properties()?.Index != index)
                    throw new IOException("Системный маршрут не проходит через TUN. Проверьте другие VPN и сетевые фильтры.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse("172.29.245.1"), 0));
                    await socket.ConnectAsync(destination, context.DnsEndPoint.Port, cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        }) { Timeout = TimeSpan.FromSeconds(18) };
        try
        {
            using var response = await client.GetAsync("https://www.gstatic.com/generate_204", HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.NoContent) throw new IOException("Неожиданный ответ HTTPS через TUN.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SocketException or IOException)
        {
            string phase = ex is OperationCanceledException ? "истекло время ожидания" : ex.GetBaseException() is IOException detail ? detail.Message : "нет ответа DNS/HTTPS";
            throw new IOException("Локальный прокси работает, но системный TUN не подтвердил DNS/HTTPS: " + phase
                + " Попробуйте другой DNS или сервер; проверьте другие VPN и фильтры. VPN будет отключён.");
        }
    }

    private async Task VerifyProxyAsync()
    {
        var webProxy = new WebProxy($"socks5://127.0.0.1:{proxyPort}") { Credentials = new NetworkCredential("honestly", socksPassword) };
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = webProxy, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(8), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            using var response = await client.GetAsync("https://www.gstatic.com/generate_204", HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.NoContent) throw new IOException("Сервер вернул неожиданный ответ проверки интернета.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { throw new IOException("Сервер не подтвердил доступ через VPN. Выберите другой сервер. Если это ограниченный сервер, отключите проверку интернета в настройках."); }
    }
    private static string PhysicalInterface()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))).ToArray();
        if (candidates.Length == 0) throw new IOException("Не найдено физическое подключение к интернету.");
        if (GetBestInterface(0x01010101, out uint index) == 0)
        {
            var best = candidates.FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == index);
            if (best is not null) return best.Name;
        }
        return candidates.OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)).First().Name;
    }
    private static async Task ResolveEndpoints(JsonObject outbound, int seconds)
    {
        if (outbound["settings"] is not JsonObject settings) return;
        foreach (string collection in new[] { "vnext", "servers" })
        {
            if (settings[collection] is not JsonArray servers) continue;
            foreach (var server in servers.OfType<JsonObject>()) await Resolve(server);
        }
        if (settings["address"] is not null) await Resolve(settings);
        async Task Resolve(JsonObject server)
        {
            string host = server["address"]?.ToString() ?? "";
            if (IPAddress.TryParse(host, out _)) return;
            if (host.Length == 0) throw new IOException("В ключе не указан адрес сервера.");
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(Math.Min(seconds, 10)));
                var address = addresses.OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetwork).FirstOrDefault()
                    ?? throw new IOException("Не найден адрес VPN-сервера.");
                server["address"] = address.ToString();
                if (outbound["streamSettings"] is JsonObject stream)
                    foreach (string name in new[] { "tlsSettings", "realitySettings" })
                        if (stream[name] is JsonObject tls && string.IsNullOrWhiteSpace(tls["serverName"]?.ToString())) tls["serverName"] = host;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException) { throw new IOException("Не удалось определить IP VPN-сервера. Проверьте интернет и подписку."); }
        }
    }
    private static bool OwnsPort(PseudoConsoleSession? session, int port) => session is { HasExited: false }
        && NativeTcpListeners.Read().Any(l => l.ProcessId == session.ProcessId && l.Port == port && l.Local);
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); } }
    private static async Task WaitCore(Func<bool> ready, PseudoConsoleSession process, int seconds, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (process.HasExited || watch.Elapsed >= TimeSpan.FromSeconds(seconds)) throw new IOException(error);
            await Task.Delay(200);
        }
    }
    private async Task<JsonDocument> ReadApiAsync(string path)
    {
        if (!OwnsPort(tunnel, apiPort)) throw new IOException("Статистика собственного TUN недоступна.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{apiPort}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);
        string? line = await reader.ReadLineAsync(cancellation.Token);
        if (line is null || line.Length > 65536) throw new IOException("Неверный ответ статистики TUN.");
        return JsonDocument.Parse(line);
    }
    public async Task<IncyDisplayData> ReadDisplayAsync()
    {
        var current = library;
        var rows = current.Profiles.Select(p => new IncyServer(p.Id, p.Name, p.Description, current.SourceName, null, p.Id == current.SelectedId)).ToArray();
        string selected = current.Selected?.Name ?? "—";
        if (!connected || ReadStatus().State != VpnState.Active) return new(rows, "—", "—", "—", selected, libraryError);
        string up = "—", down = "—", note = "Статистика собственного VPN. Локальная сеть подключается напрямую.";
        try
        {
            using var data = await ReadApiAsync("traffic");
            up = Rate(data.RootElement.GetProperty("up").GetDouble()); down = Rate(data.RootElement.GetProperty("down").GetDouble());
        }
        catch { note = "Статистика встроенного VPN недоступна: ядро ещё обновляет состояние или завершилось."; }
        TimeSpan elapsed = connectionClock?.Elapsed ?? TimeSpan.Zero;
        string time = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        return new(rows, up, time, down, selected, note);
    }
    private static string Rate(double bytes)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"]; int index = 0;
        while (bytes >= 1024 && index < 3) { bytes /= 1024; index++; }
        return Math.Max(0, bytes).ToString("0.00", CultureInfo.InvariantCulture) + " " + units[index];
    }
    private async Task StopCoreAsync()
    {
        connected = false; starting = false; connectionClock = null;
        await Stop(tunnel); tunnel = null;
        await Stop(proxy); proxy = null;
        socksPassword = ""; apiSecret = "";
        if (tunnelName is { } name)
        {
            var wait = Stopwatch.StartNew();
            while (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == name && n.OperationalStatus == OperationalStatus.Up)
                && wait.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(200);
            if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == name && n.OperationalStatus == OperationalStatus.Up))
                throw new IOException("TUN ещё активен после остановки. Zapret не будет запущен до очистки интерфейса.");
        }
        tunnelName = null;
        pausedProxy?.Dispose(); pausedProxy = null;
        static async Task Stop(PseudoConsoleSession? process)
        {
            if (process is null) return;
            try
            {
                if (!process.HasExited)
                {
                    try { await process.SendAsync("\u0003"); } catch (IOException) { }
                    var wait = Stopwatch.StartNew();
                    while (!process.HasExited && wait.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(100);
                }
            }
            finally { process.Dispose(); }
        }
    }
    public void Dispose()
    {
        try { tunnel?.Dispose(); }
        finally { tunnel = null; proxy?.Dispose(); proxy = null; connected = false; pausedProxy?.Dispose(); pausedProxy = null; }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
}
