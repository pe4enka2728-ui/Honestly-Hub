using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VpnZapretSwitch;

internal static class IncyCoreMetrics
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = TimeSpan.FromSeconds(3) };
    private static (string Signature, long Up, long Down, long Uptime, long Tick)? previous;
    internal sealed record Metrics(string Upload, string Elapsed, string Download, string Note);
    public static void Reset() => previous = null;

    public static async Task<Metrics> ReadAsync(Settings settings)
    {
        var endpoint = ReadEndpoint();
        var pids = WindowsPlatform.ExactProcesses(settings.IncyExe).ToHashSet();
        if (!NativeTcpListeners.Read().Any(l => l.Local && l.Port == endpoint.Port && pids.Contains(l.ProcessId)))
            throw new IOException("Локальный API статистики не принадлежит выбранному INCY.");
        var upTask = ReadCounters(endpoint.Port, endpoint.Tags, "uplink");
        var downTask = ReadCounters(endpoint.Port, endpoint.Tags, "downlink");
        var timeTask = Call(endpoint.Port, "GetSysStats", []);
        await Task.WhenAll(upTask, downTask, timeTask);
        long up = await upTask, down = await downTask;
        long uptime = Proto.Fields(await timeTask).FirstOrDefault(f => f.Number == 10)?.Value ?? 0;
        long now = Stopwatch.GetTimestamp();
        string signature = endpoint.Port + ":" + string.Join("|", endpoint.Tags);
        string upload = "—", download = "—";
        string note = "Статистика локального ядра INCY. Скорость усредняется между обновлениями.";
        if (previous is { } old && old.Signature == signature && uptime >= old.Uptime && up >= old.Up && down >= old.Down)
        {
            double seconds = (now - old.Tick) / (double)Stopwatch.Frequency;
            if (seconds is > 0.25 and < 15)
            { upload = Rate((up - old.Up) / seconds); download = Rate((down - old.Down) / seconds); }
        }
        else note += " Счётчики запущены или сброшены; ждём следующий замер.";
        previous = (signature, up, down, uptime, now);
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, uptime));
        string time = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        return new(upload, time, download, note);
    }

    private static string Rate(double bytes)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unit];
    }
    private static async Task<long> ReadCounters(int port, string[] tags, string direction)
    {
        long total = 0;
        foreach (string tag in tags)
        {
            // reset is omitted (protobuf default false). Never reset INCY's own counters.
            byte[] request = Proto.Text(1, $"outbound>>>{tag}>>>traffic>>>{direction}");
            var response = Proto.Fields(await Call(port, "GetStats", request));
            byte[] stat = response.FirstOrDefault(f => f.Number == 1)?.Bytes
                ?? throw new IOException("INCY не передал счётчик трафика.");
            total = checked(total + (Proto.Fields(stat).FirstOrDefault(f => f.Number == 2)?.Value ?? 0));
        }
        return total;
    }
    private static async Task<byte[]> Call(int port, string method, byte[] body)
    {
        byte[] frame = new byte[body.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), body.Length);
        body.CopyTo(frame, 5);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}/xray.app.stats.command.StatsService/{method}")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(frame) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (header[0] != 0 || length is < 0 or > 65536) throw new IOException("Неизвестный ответ API статистики INCY.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, timeout.Token);
        if (await stream.ReadAsync(new byte[1], timeout.Token) != 0) throw new IOException("Неожиданный поток статистики INCY.");
        if (response.TrailingHeaders.TryGetValues("grpc-status", out var status) && status.FirstOrDefault() != "0")
            throw new IOException("API INCY не предоставил запрошенную статистику.");
        return payload;
    }
    private static (int Port, string[] Tags) ReadEndpoint()
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "incy", "cache");
        string file = new[] { Path.Combine(cache, "xray_config.json"), Path.Combine(cache, "xray-config.json") }
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new IOException("Нет конфигурации локального API INCY.");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var api = root.GetProperty("api");
        if (!api.GetProperty("services").EnumerateArray().Any(s => s.GetString() == "StatsService"))
            throw new IOException("В конфигурации INCY отключена статистика ядра.");
        string apiTag = api.GetProperty("tag").GetString() ?? "api";
        var incoming = root.GetProperty("inbounds").EnumerateArray().FirstOrDefault(inbound =>
            inbound.TryGetProperty("protocol", out var protocol) && protocol.GetString() == "dokodemo-door"
            && inbound.TryGetProperty("listen", out var listen) && listen.GetString() == "127.0.0.1"
            && inbound.TryGetProperty("tag", out var tag)
            && root.GetProperty("routing").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("outboundTag", out var outbound) && outbound.GetString() == apiTag
                && rule.TryGetProperty("inboundTag", out var inputs) && inputs.EnumerateArray().Any(i => i.GetString() == tag.GetString())));
        if (incoming.ValueKind == JsonValueKind.Undefined) throw new IOException("Не найден локальный вход API INCY.");
        string[] tags = root.GetProperty("outbounds").EnumerateArray()
            .Where(o => o.TryGetProperty("protocol", out var p) && p.GetString() is not ("freedom" or "blackhole" or "dns" or "loopback"))
            .Select(o => o.GetProperty("tag").GetString()).Where(t => !string.IsNullOrWhiteSpace(t)).Cast<string>().ToArray();
        if (tags.Length == 0 || tags.Length > 16) throw new IOException("Не удалось однозначно определить счётчики прокси INCY.");
        return (incoming.GetProperty("port").GetInt32(), tags);
    }
}

internal static class Proto
{
    internal sealed record Field(int Number, long Value = 0, byte[]? Bytes = null);
    public static List<Field> Fields(byte[] bytes)
    {
        var fields = new List<Field>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            ulong key = Varint(bytes, ref offset);
            int number = checked((int)(key >> 3));
            switch (key & 7)
            {
                case 0: fields.Add(new(number, unchecked((long)Varint(bytes, ref offset)))); break;
                case 2:
                    int length = checked((int)Varint(bytes, ref offset));
                    if (length < 0 || length > bytes.Length - offset) throw new IOException("Обрезанный ответ статистики.");
                    fields.Add(new(number, Bytes: bytes.AsSpan(offset, length).ToArray())); offset += length; break;
                case 1: offset = checked(offset + 8); break;
                case 5: offset = checked(offset + 4); break;
                default: throw new IOException("Неподдерживаемый ответ статистики.");
            }
            if (offset > bytes.Length || number == 0) throw new IOException("Неверный ответ статистики.");
        }
        return fields;
    }
    private static ulong Varint(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63 && offset < data.Length; shift += 7)
        {
            byte part = data[offset++];
            if (shift == 63 && part > 1) throw new IOException("Слишком большой счётчик статистики.");
            value |= (ulong)(part & 127) << shift;
            if ((part & 128) == 0) return value;
        }
        throw new IOException("Обрезанный счётчик статистики.");
    }
    public static byte[] Text(int number, string text)
    {
        using var output = new MemoryStream();
        Write((ulong)((number << 3) | 2));
        byte[] value = Encoding.UTF8.GetBytes(text);
        Write((ulong)value.Length); output.Write(value); return output.ToArray();
        void Write(ulong integer)
        {
            while (integer >= 128) { output.WriteByte((byte)(integer | 128)); integer >>= 7; }
            output.WriteByte((byte)integer);
        }
    }
}
