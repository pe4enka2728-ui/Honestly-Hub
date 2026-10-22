using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VpnZapretSwitch;

internal sealed record SpotifyDevice(string Id, string Name, string Type, bool Active, bool Restricted, bool SupportsVolume, int? Volume)
{
    internal bool IsComputer => Type.Equals("computer", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name + (Active ? " · активно" : "");
}

public sealed record SpotifyQueueItem(
    string Id,
    string Name,
    string Artist,
    string Album,
    string Uri,
    string ImageUrl,
    int DurationMs
)
{
    public string DurationText => TimeSpan.FromMilliseconds(DurationMs).ToString(@"m\:ss");
}

public sealed record SpotifyQueue(
    SpotifyQueueItem? CurrentlyPlaying,
    IReadOnlyList<SpotifyQueueItem> Items
);

internal sealed class SpotifyRateLimitException(DateTimeOffset until) : InvalidOperationException("Spotify временно ограничил частоту запросов. Обновление возобновится автоматически.")
{ internal DateTimeOffset Until { get; } = until; }

internal sealed class SpotifyApi : IDisposable
{
    private readonly SpotifyConnectionStore store;
    private readonly HttpClient http;
    private readonly bool ownHttp;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private SpotifyCredentials credentials;
    private string accessToken = "";
    private DateTimeOffset expires, retryUntil;
    private bool needsAuthorization;
    private string note = "Привяжите Spotify в настройках музыки";
    private int disposed;
    internal SpotifyConnectionInfo Connection
    {
        get
        {
            var data = Volatile.Read(ref credentials);
            return new(data.ClientId, data.RefreshToken.Length > 0 && !Volatile.Read(ref needsAuthorization), data.DeviceId, Volatile.Read(ref note));
        }
    }
    internal SpotifyApi(SpotifyConnectionStore? store = null, HttpClient? http = null)
    {
        this.store = store ?? new();
        ownHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
        try { credentials = this.store.Load(); if (credentials.RefreshToken.Length > 0) note = "Spotify связан"; }
        catch { credentials = new(); note = "Сохранённую связь Spotify не удалось открыть. Привяжите Spotify заново."; }
    }
    internal async Task AuthorizeAsync(string clientId, Action<Uri> openBrowser, CancellationToken token)
    {
        clientId = clientId.Trim(); SpotifyOAuth.ValidateClientId(clientId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        string verifier = SpotifyOAuth.NewVerifier();
        string code;
        try { code = await SpotifyOAuth.ReceiveCodeAsync(clientId, verifier, openBrowser, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !stopping.IsCancellationRequested)
        { throw new InvalidOperationException("Время ожидания входа истекло. Нажмите «Привязать Spotify» снова."); }
        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            using var data = await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = SpotifyOAuth.RedirectUri,
                ["client_id"] = clientId, ["code_verifier"] = verifier
            }, deadline.Token).ConfigureAwait(false);
            var root = data.RootElement;
            string refresh = Text(root, "refresh_token");
            string scope = Text(root, "scope");
            if (refresh.Length is 0 or > 8192 || refresh.Any(char.IsControl) || SpotifyOAuth.Scopes.Split(' ').Except(scope.Split(' '), StringComparer.Ordinal).Any())
                throw new InvalidOperationException("Spotify не выдал необходимые права. Разрешите чтение состояния и управление воспроизведением при привязке.");
            var next = new SpotifyCredentials(clientId, refresh);
            ReadAccessToken(root, out string access, out DateTimeOffset expiry);
            deadline.Token.ThrowIfCancellationRequested();
            store.Save(next); Volatile.Write(ref credentials, next);
            accessToken = access; expires = expiry; needsAuthorization = false; retryUntil = default;
            Volatile.Write(ref note, "Spotify связан");
        }
        finally { gate.Release(); }
    }
    internal async Task DisconnectAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var next = new SpotifyCredentials(credentials.ClientId);
            store.Save(next); Volatile.Write(ref credentials, next); accessToken = ""; expires = default;
            needsAuthorization = false; Volatile.Write(ref note, "Привяжите Spotify в настройках музыки");
        }
        finally { gate.Release(); }
    }
    internal async Task<SpotifyDevice[]> GetDevicesAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await DevicesCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    internal async Task SelectDeviceAsync(string id, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (id.Length > 0)
            {
                var devices = await DevicesCoreAsync(linked.Token).ConfigureAwait(false);
                if (!devices.Any(device => device.Id == id && device.IsComputer && !device.Restricted && device.SupportsVolume))
                    throw new InvalidOperationException("Выберите доступный компьютер Spotify с управлением громкостью.");
            }
            var next = credentials with { DeviceId = id };
            store.Save(next); Volatile.Write(ref credentials, next);
        }
        finally { gate.Release(); }
    }
    internal async Task SetVolumeAsync(int percent, string expectedDeviceId, CancellationToken token)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var devices = await DevicesCoreAsync(linked.Token).ConfigureAwait(false);
            var target = SelectTarget(devices, credentials.DeviceId);
            if (target is null || target.Id != expectedDeviceId)
                throw new InvalidOperationException("Устройство Spotify изменилось. Дождитесь обновления регулятора и повторите.");
            using var response = await ApiRequestAsync(HttpMethod.Put, VolumePath(percent, target.Id), linked.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    internal async Task<SpotifyQueue?> GetQueueAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var response = await ApiRequestAsync(HttpMethod.Get, "me/player/queue", linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            using var data = await ReadJsonAsync(response, linked.Token).ConfigureAwait(false);
            var root = data.RootElement;
            SpotifyQueueItem? current = null;
            if (root.TryGetProperty("currently_playing", out var cur))
                current = ParseQueueItem(cur);

            var items = new List<SpotifyQueueItem>();
            if (root.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in q.EnumerateArray().Take(40))
                {
                    var item = ParseQueueItem(el);
                    if (item is not null) items.Add(item);
                }
            }
            return new SpotifyQueue(current, items);
        }
        catch { return null; }
        finally { gate.Release(); }
    }
    internal async Task<bool> PlayTrackAsync(string trackUri, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            string path = "me/player/play";
            string devId = credentials.DeviceId;
            if (devId.Length > 0)
                path += "?device_id=" + Uri.EscapeDataString(devId);

            var payload = JsonSerializer.Serialize(new { uris = new[] { trackUri } });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await ApiRequestWithBodyAsync(HttpMethod.Put, path, content, linked.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
        finally { gate.Release(); }
    }
    internal async Task<bool> SkipNextAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            string path = "me/player/next";
            string devId = credentials.DeviceId;
            if (devId.Length > 0)
                path += "?device_id=" + Uri.EscapeDataString(devId);

            using var response = await ApiRequestAsync(HttpMethod.Post, path, linked.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
        finally { gate.Release(); }
    }
    internal async Task<bool> SkipToQueueItemAsync(int indexInQueue, CancellationToken token = default)
    {
        if (indexInQueue < 0) return false;
        int skipCount = indexInQueue + 1;
        for (int i = 0; i < skipCount; i++)
        {
            bool ok = await SkipNextAsync(token).ConfigureAwait(false);
            if (!ok) return false;
            if (i < skipCount - 1)
                await Task.Delay(75, token).ConfigureAwait(false);
        }
        return true;
    }
    private async Task<HttpResponseMessage> ApiRequestWithBodyAsync(HttpMethod method, string path, HttpContent content, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CheckRateLimit();
            await EnsureAccessAsync(token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri("https://api.spotify.com/v1/" + path)) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            { response.Dispose(); accessToken = ""; expires = default; continue; }
            try
            {
                CheckResponse(response);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidOperationException("Привяжите Spotify заново.");
    }
    internal static SpotifyQueueItem? ParseQueueItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string id = Text(item, "id");
        string name = Text(item, "name");
        string uri = Text(item, "uri");
        if (string.IsNullOrWhiteSpace(name)) return null;

        string artist = "";
        if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(a => Text(a, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n));
            artist = string.Join(", ", names);
        }

        string album = "";
        string imageUrl = "";
        if (item.TryGetProperty("album", out var albumElem) && albumElem.ValueKind == JsonValueKind.Object)
        {
            album = Text(albumElem, "name");
            if (albumElem.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                var imgList = images.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList();
                if (imgList.Count > 0)
                {
                    var target = imgList.Count > 1 ? imgList[^1] : imgList[0];
                    imageUrl = Text(target, "url");
                    if (string.IsNullOrWhiteSpace(imageUrl))
                        imageUrl = Text(imgList[0], "url");
                }
            }
        }

        int durationMs = 0;
        if (item.TryGetProperty("duration_ms", out var dur) && dur.ValueKind == JsonValueKind.Number)
            dur.TryGetInt32(out durationMs);

        return new SpotifyQueueItem(id, name, artist, album, uri, imageUrl, durationMs);
    }
    internal static string VolumePath(int percent, string deviceId)
    {
        if (percent is < 0 or > 100 || string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 256 || deviceId.Any(char.IsControl))
            throw new ArgumentException("Некорректное устройство или громкость Spotify.");
        return "me/player/volume?volume_percent=" + percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&device_id=" + Uri.EscapeDataString(deviceId);
    }
    internal static SpotifyDevice? SelectTarget(IReadOnlyList<SpotifyDevice> devices, string selectedId)
    {
        var available = devices.Where(device => device.IsComputer && !device.Restricted && device.SupportsVolume && device.Volume is not null).ToArray();
        var matches = selectedId.Length > 0 ? available.Where(device => device.Id == selectedId).ToArray() : available.Where(device => device.Active).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static SpotifyDevice[] ParseDevices(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Take(100).Where(item => item.ValueKind == JsonValueKind.Object).Select(item =>
        {
            int? volume = item.TryGetProperty("volume_percent", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int value) && value is >= 0 and <= 100 ? value : null;
            string id = Text(item, "id"), name = Text(item, "name");
            return new SpotifyDevice(id, name.Length > 0 ? name : "Компьютер Spotify", Text(item, "type"), Flag(item, "is_active"),
                Flag(item, "is_restricted"), Flag(item, "supports_volume"), volume);
        }).Where(device => device.Id.Length is > 0 and <= 256 && !device.Id.Any(char.IsControl)).DistinctBy(device => device.Id).ToArray();
    }
    private async Task<SpotifyDevice[]> DevicesCoreAsync(CancellationToken token)
    {
        using var response = await ApiRequestAsync(HttpMethod.Get, "me/player/devices", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return [];
        using var data = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return ParseDevices(data.RootElement);
    }
    private async Task<HttpResponseMessage> ApiRequestAsync(HttpMethod method, string path, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CheckRateLimit();
            await EnsureAccessAsync(token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri("https://api.spotify.com/v1/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            { response.Dispose(); accessToken = ""; expires = default; continue; }
            try
            {
                CheckResponse(response);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidOperationException("Привяжите Spotify заново.");
    }
    private async Task EnsureAccessAsync(CancellationToken token)
    {
        if (!Connection.Linked) throw new InvalidOperationException(Connection.Note);
        if (accessToken.Length > 0 && expires > DateTimeOffset.UtcNow.AddSeconds(30)) return;
        using var response = await TokenRequestAsync(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.RefreshToken, ["client_id"] = credentials.ClientId }, token).ConfigureAwait(false);
        var root = response.RootElement;
        ReadAccessToken(root, out string access, out var expiry);
        string refresh = Text(root, "refresh_token");
        if (refresh.Length > 8192 || refresh.Any(char.IsControl)) throw new InvalidOperationException("Spotify вернул некорректный ответ авторизации.");
        if (refresh.Length > 0 && refresh != credentials.RefreshToken)
        {
            var next = credentials with { RefreshToken = refresh };
            store.Save(next); Volatile.Write(ref credentials, next);
        }
        accessToken = access; expires = expiry;
    }
    private async Task<JsonDocument> TokenRequestAsync(Dictionary<string, string> values, CancellationToken token)
    {
        CheckRateLimit();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token") { Content = new FormUrlEncodedContent(values) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            if (values["grant_type"] == "refresh_token") MarkNeedsAuthorization();
            throw new InvalidOperationException("Spotify не принял авторизацию. Проверьте Client ID, Redirect URI и повторите привязку.");
        }
        CheckResponse(response);
        return await ReadJsonAsync(response, token).ConfigureAwait(false);
    }
    private void CheckResponse(HttpResponseMessage response)
    {
        if ((int)response.StatusCode == 429)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
            delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : delay;
            retryUntil = DateTimeOffset.UtcNow.Add(delay);
            throw new SpotifyRateLimitException(retryUntil);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized) { MarkNeedsAuthorization(); throw new InvalidOperationException("Срок доступа Spotify истёк. Привяжите Spotify заново."); }
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Spotify отклонил доступ. Проверьте Premium, разрешения и список пользователей приложения в Spotify for Developers.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Устройство Spotify недоступно. Откройте настольный Spotify и включите трек.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Spotify временно недоступен (HTTP " + (int)response.StatusCode + ").");
    }
    private void MarkNeedsAuthorization() { needsAuthorization = true; Volatile.Write(ref note, "Привяжите Spotify заново: доступ истёк или был отозван."); }
    private void CheckRateLimit() { if (DateTimeOffset.UtcNow < retryUntil) throw new SpotifyRateLimitException(retryUntil); }
    private static void ReadAccessToken(JsonElement root, out string access, out DateTimeOffset expiry)
    {
        access = Text(root, "access_token");
        if (access.Length is 0 or > 8192 || access.Any(char.IsControl) || !Text(root, "token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("expires_in", out var seconds) || seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out int life) || life is < 1 or > 86400)
            throw new InvalidOperationException("Spotify вернул некорректный ответ авторизации.");
        expiry = DateTimeOffset.UtcNow.AddSeconds(life);
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 1024 * 1024) throw new InvalidOperationException("Слишком большой ответ Spotify.");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
    }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel(); if (ownHttp) http.Dispose();
    }
}
