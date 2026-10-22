using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VpnZapretSwitch;

internal static class SpotifyOAuth
{
    internal const string RedirectUri = "http://127.0.0.1:43879/callback/";
    internal const string Scopes = "user-read-playback-state user-modify-playback-state";
    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static void ValidateClientId(string value)
    {
        if (value.Length != 32 || !value.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Введите публичный Client ID: 32 шестнадцатеричных символа. Client Secret не нужен.");
    }
    internal static Uri AuthorizationUri(string clientId, string verifier, string state)
    {
        ValidateClientId(clientId);
        return new("https://accounts.spotify.com/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId) +
            "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&scope=" + Uri.EscapeDataString(Scopes) +
            "&code_challenge_method=S256&code_challenge=" + Uri.EscapeDataString(Challenge(verifier)) +
            "&state=" + Uri.EscapeDataString(state));
    }
    internal static string? ParseCallback(string target, string state)
    {
        if (!target.StartsWith("/callback/?", StringComparison.Ordinal) || target.Contains('#') || target.Length > 8192) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string pair in target[(target.IndexOf('?') + 1)..].Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) return null;
            }
        }
        catch (UriFormatException) { return null; }
        if (!values.TryGetValue("state", out var actual) || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(actual))) return null;
        if (values.ContainsKey("error")) throw new InvalidOperationException("Авторизация отменена в Spotify.");
        return values.TryGetValue("code", out string? code) && code.Length is > 0 and <= 4096 && !code.Any(char.IsControl) ? code : null;
    }
    internal static async Task<string> ReceiveCodeAsync(string clientId, string verifier, Action<Uri> openBrowser, CancellationToken token)
    {
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 43879);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            try { listener.Start(4); }
            catch (SocketException) { throw new InvalidOperationException("Порт 43879 занят. Закройте другое окно привязки Spotify и повторите."); }
            openBrowser(AuthorizationUri(clientId, verifier, state));
            for (int attempt = 0; attempt < 32; attempt++)
            {
                using var connection = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                try
                {
                    string? first = await ReadLineAsync(reader, 8192, deadline.Token).ConfigureAwait(false);
                    if (first is null) continue;
                    var request = first.Split(' ');
                    int total = first.Length; string? host = null; bool headersEnded = false;
                    for (int line = 0; line < 64; line++)
                    {
                        string? header = await ReadLineAsync(reader, 8192, deadline.Token).ConfigureAwait(false);
                        if (header is null) throw new IOException("Неполный локальный запрос.");
                        if (header.Length == 0) { headersEnded = true; break; }
                        total += header.Length;
                        if (total > 16384) throw new IOException("Слишком длинный локальный запрос.");
                        if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        { if (host is not null) throw new IOException("Повтор заголовка Host."); host = header[5..].Trim(); }
                    }
                    if (!headersEnded || request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || host != "127.0.0.1:43879")
                    { await RespondAsync(stream, false, deadline.Token).ConfigureAwait(false); continue; }
                    string? code;
                    try { code = ParseCallback(request[1], state); }
                    catch (InvalidOperationException)
                    { await RespondAsync(stream, false, deadline.Token).ConfigureAwait(false); throw; }
                    await RespondAsync(stream, code is not null, deadline.Token).ConfigureAwait(false);
                    if (code is not null) return code;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) { }
            }
            throw new InvalidOperationException("Не удалось получить ответ Spotify. Повторите привязку.");
        }
        finally { listener.Stop(); }
    }
    private static async Task<string?> ReadLineAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder(); var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) > 0)
        {
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= limit) throw new IOException("Слишком длинный локальный запрос.");
            text.Append(one[0]);
        }
        return null;
    }
    private static async Task RespondAsync(Stream stream, bool accepted, CancellationToken token)
    {
        string message = accepted ? "Ответ Spotify получен. Вернитесь в Honestly TUN для завершения привязки." : "Ответ не принят. Вернитесь в Honestly TUN и повторите привязку при необходимости.";
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Honestly TUN · Spotify</title><body style=\"background:#111217;color:#f1f1f6;font:18px/1.6 system-ui;padding:48px\"><h1>Honestly TUN</h1><p>" + message + "</p>");
        string headers = (accepted ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 400 Bad Request\r\n") +
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
}
