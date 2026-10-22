using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VpnZapretSwitch;

internal static class SpotifyApiChecks
{
    private const string Client = "0123456789abcdef0123456789abcdef";
    private const string DeviceJson = """{"devices":[{"id":"desktop /?&","name":"Test PC","type":"Computer","is_active":true,"is_restricted":false,"supports_volume":true,"volume_percent":30}]}""";
    private const string TokenJson = """{"access_token":"synthetic-access","token_type":"Bearer","expires_in":3600}""";
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(text) };
    private sealed class FakeHandler : HttpMessageHandler
    {
        internal readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> Steps = new();
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host is "accounts.spotify.com" or "api.spotify.com", "Unexpected API host");
            if (Steps.Count == 0) throw new Exception("Unexpected API request");
            return Steps.Dequeue()(request);
        }
        internal void Add(string json, HttpStatusCode code = HttpStatusCode.OK) => Steps.Enqueue(_ => Task.FromResult(Json(json, code)));
    }
    internal static async Task RunAsync()
    {
        Check(SpotifyOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "RFC 7636 PKCE vector");
        Check(SpotifyOAuth.NewVerifier().Length is >= 43 and <= 128, "PKCE verifier length");
        Check(SpotifyOAuth.ParseCallback("/callback/?code=ok&state=expected", "expected") == "ok", "Valid callback");
        Check(SpotifyOAuth.ParseCallback("/callback/?code=ok&state=wrong", "expected") is null, "Wrong state accepted");
        Check(SpotifyOAuth.ParseCallback("/callback/?code=ok&state=expected&code=duplicate", "expected") is null, "Duplicate callback accepted");
        Check(SpotifyOAuth.ParseCallback("/else/?code=ok&state=expected", "expected") is null, "Wrong callback path accepted");
        await ExpectAsync<InvalidOperationException>(() => Task.Run(() => SpotifyOAuth.ParseCallback("/callback/?error=access_denied&state=expected", "expected")));
        var uri = SpotifyOAuth.AuthorizationUri(Client, "verifier", "state");
        Check(uri.Host == "accounts.spotify.com" && uri.Query.Contains("code_challenge_method=S256") && !uri.Query.Contains("client_secret"), "Public client authorization");
        using (var data = JsonDocument.Parse("""{"devices":[{"id":"phone","type":"Smartphone","is_active":true,"supports_volume":true,"volume_percent":40},{"id":"null","type":"Computer","is_active":true,"supports_volume":true,"volume_percent":null},{"id":"locked","type":"Computer","is_active":true,"supports_volume":true,"is_restricted":true,"volume_percent":10}]}"""))
            Check(SpotifyApi.SelectTarget(SpotifyApi.ParseDevices(data.RootElement), "") is null, "Phone/null/restricted device selected");
        using (var data = JsonDocument.Parse(DeviceJson))
        {
            var devices = SpotifyApi.ParseDevices(data.RootElement);
            Check(SpotifyApi.SelectTarget(devices, "")?.Volume == 30, "Active computer volume");
            Check(SpotifyApi.SelectTarget([devices[0], devices[0] with { Id = "other" }], "") is null, "Ambiguous active computers");
            Check(SpotifyApi.SelectTarget(devices, "missing") is null, "Unavailable selection fell back");
        }
        Check(SpotifyApi.VolumePath(0, "desktop /?&").EndsWith("device_id=desktop%20%2F%3F%26"), "Device ID was not escaped");
        await ExpectAsync<ArgumentException>(() => Task.Run(() => SpotifyApi.VolumePath(101, "pc")));
        string fixture = Path.Combine(AppContext.BaseDirectory, "spotify-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SpotifyConnectionStore(Path.Combine(fixture, "spotify.dat"));
            Check(store.Load().RefreshToken == "", "Missing store not empty");
            store.Save(new(Client, "synthetic-refresh"));
            Check(store.Load().RefreshToken == "synthetic-refresh", "DPAPI round trip");
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)).Contains("synthetic-refresh"), "Plaintext refresh token stored");
            await ExpectAsync<IOException>(() => Task.Run(() => store.Save(new(Client, new string('x', 8193)))));
            Check(store.Load().RefreshToken == "synthetic-refresh", "Rejected save damaged credentials");
            var handler = new FakeHandler(); using var http = new HttpClient(handler); using var api = new SpotifyApi(store, http);
            handler.Steps.Enqueue(async request =>
            {
                string form = await request.Content!.ReadAsStringAsync();
                Check(form.Contains("grant_type=refresh_token") && !form.Contains("client_secret"), "Refresh grant");
                return Json(TokenJson.TrimEnd('}') + ",\"refresh_token\":\"rotated-refresh\"}");
            });
            handler.Add(DeviceJson); Check((await api.GetDevicesAsync(default)).Length == 1, "Device request");
            Check(store.Load().RefreshToken == "rotated-refresh", "Refresh rotation not saved");
            handler.Add(DeviceJson);
            handler.Steps.Enqueue(request =>
            {
                Check(request.Method == HttpMethod.Put && request.RequestUri!.AbsoluteUri.Contains("volume_percent=28&device_id=desktop%20%2F%3F%26"), "Volume command target");
                Check(request.Headers.Authorization?.Scheme == "Bearer", "Bearer header missing");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
            await api.SetVolumeAsync(28, "desktop /?&", default);
            handler.Add(DeviceJson); await ExpectAsync<InvalidOperationException>(() => api.SetVolumeAsync(28, "different", default));
            handler.Add("{}", HttpStatusCode.Unauthorized); handler.Add(TokenJson); handler.Add(DeviceJson);
            Check((await api.GetDevicesAsync(default)).Length == 1 && store.Load().RefreshToken == "rotated-refresh", "401 refresh retry / omitted refresh token");
            handler.Add("{}", HttpStatusCode.Forbidden); await ExpectAsync<InvalidOperationException>(() => api.GetDevicesAsync(default));
            handler.Steps.Enqueue(_ =>
            {
                var response = Json("{}", (HttpStatusCode)429); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); return Task.FromResult(response);
            });
            await ExpectAsync<SpotifyRateLimitException>(() => api.GetDevicesAsync(default)); int calls = handler.Calls;
            await ExpectAsync<SpotifyRateLimitException>(() => api.GetDevicesAsync(default)); Check(handler.Calls == calls, "429 ignored Retry-After");
            await api.DisconnectAsync(default); Check(!api.Connection.Linked && store.Load().RefreshToken == "", "Local unlink did not clear refresh token");
            Check(handler.Steps.Count == 0, "Unconsumed API expectations");

            // A synthetic browser callback exercises the real loopback listener, no browser/account involved.
            var authHandler = new FakeHandler(); using var authHttp = new HttpClient(authHandler); using var authApi = new SpotifyApi(store, authHttp);
            authHandler.Steps.Enqueue(async request =>
            {
                string form = await request.Content!.ReadAsStringAsync();
                Check(form.Contains("grant_type=authorization_code") && form.Contains("code=synthetic-code") && form.Contains("code_verifier=") && !form.Contains("client_secret"), "PKCE token exchange");
                return Json(TokenJson.TrimEnd('}') + ",\"refresh_token\":\"oauth-refresh\",\"scope\":\"" + SpotifyOAuth.Scopes + "\"}");
            });
            Task? callback = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await authApi.AuthorizeAsync(Client, address => callback = Task.Run(async () =>
            {
                string state = address.Query.TrimStart('?').Split('&').Single(item => item.StartsWith("state=")).Split('=', 2)[1];
                using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false });
                var rejected = await loopback.GetAsync(SpotifyOAuth.RedirectUri + "?code=ignored&state=wrong", timeout.Token);
                Check(rejected.StatusCode == HttpStatusCode.BadRequest, "Listener accepted wrong state");
                var accepted = await loopback.GetAsync(SpotifyOAuth.RedirectUri + "?code=synthetic-code&state=" + state, timeout.Token);
                Check(accepted.IsSuccessStatusCode, "Listener rejected valid callback");
                Check(!(await accepted.Content.ReadAsStringAsync()).Contains("synthetic-code"), "Code leaked in response page");
            }), timeout.Token);
            await callback!; Check(authApi.Connection.Linked && store.Load().RefreshToken == "oauth-refresh", "OAuth link persistence");
            using var canceled = new CancellationTokenSource();
            await ExpectAsync<OperationCanceledException>(() => SpotifyOAuth.ReceiveCodeAsync(Client, "verifier", _ => canceled.Cancel(), canceled.Token));
            await ExpectAsync<OperationCanceledException>(() => SpotifyOAuth.ReceiveCodeAsync(Client, "verifier", _ => canceled.Cancel(), canceled.Token));
            await authApi.DisconnectAsync(default);
            Check(Directory.GetFiles(fixture, "*.tmp").Length == 0, "Leftover token store temp file");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        Console.WriteLine($"PASS: {checks} Spotify checks: PKCE, loopback/state/cancel, DPAPI, devices, explicit volume target, refresh/401, 403, 429 and unlink. Synthetic data only.");
    }
}
