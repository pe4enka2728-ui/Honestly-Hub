namespace VpnZapretSwitch;

internal sealed record SpotifyVolumeSnapshot(bool Available, int Percent, bool Muted, string Note, string DeviceId = "");

// Volume uses Spotify Web API exclusively. This service has no window/input
// APIs and never launches, restores or activates the Spotify application.
internal sealed class SpotifyVolume : IDisposable
{
    private sealed record Request(int Percent, string DeviceId, DateTimeOffset When);
    private readonly SpotifyApi api;
    private readonly bool ownsApi;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private SpotifyVolumeSnapshot snapshot = new(false, 0, false, "Привяжите Spotify в настройках музыки");
    private Request? requested;
    private long lastReadTicks;
    private int disposed;
    internal string Diagnostic = "Spotify Web API only; no window control";
    internal SpotifyVolumeSnapshot Snapshot
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            if (current.Available && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastReadTicks) > TimeSpan.FromSeconds(15).Ticks)
                return current with { Available = false, Note = "Нет свежего ответа Spotify" };
            var pending = Volatile.Read(ref requested);
            return pending is not null && current.Available && DateTimeOffset.UtcNow - pending.When < TimeSpan.FromSeconds(2)
                ? current with { Percent = pending.Percent, Muted = pending.Percent == 0 } : current;
        }
    }
    internal SpotifyVolume(SpotifyApi? api = null)
    {
        ownsApi = api is null; this.api = api ?? new SpotifyApi(); worker = Task.Run(RunAsync);
    }
    internal void SetVolume(int percent)
    {
        var current = Snapshot;
        if (current.Available && api.Connection.Linked && Volatile.Read(ref disposed) == 0)
            Volatile.Write(ref requested, new(Math.Clamp(percent, 0, 100), current.DeviceId, DateTimeOffset.UtcNow));
    }
    private async Task RunAsync()
    {
        var token = stopping.Token; DateTimeOffset next = default;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var change = Volatile.Read(ref requested);
                if (!api.Connection.Linked)
                {
                    Publish(new(false, 0, false, api.Connection.Note));
                    if (change is not null) Interlocked.CompareExchange(ref requested, null, change);
                    await Task.Delay(250, token).ConfigureAwait(false); continue;
                }
                if (change is null && DateTimeOffset.UtcNow < next)
                { await Task.Delay(100, token).ConfigureAwait(false); continue; }
                try
                {
                    if (change is not null && DateTimeOffset.UtcNow - change.When < TimeSpan.FromSeconds(8))
                    {
                        await api.SetVolumeAsync(change.Percent, change.DeviceId, token).ConfigureAwait(false);
                        await Task.Delay(350, token).ConfigureAwait(false);
                    }
                    var devices = await api.GetDevicesAsync(token).ConfigureAwait(false);
                    var target = SpotifyApi.SelectTarget(devices, api.Connection.DeviceId);
                    if (target is null)
                        Publish(new(false, 0, false, api.Connection.DeviceId.Length > 0 ? "Выбранный компьютер Spotify недоступен. Обновите устройства в настройках музыки." : "Включите трек на компьютере Spotify или выберите компьютер в настройках музыки."));
                    else
                        Publish(new(true, target.Volume!.Value, target.Volume == 0, "Громкость Spotify · " + target.Name, target.Id));
                    next = DateTimeOffset.UtcNow.AddSeconds(2.5);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (SpotifyRateLimitException ex) { Publish(new(false, 0, false, ex.Message)); next = ex.Until; }
                catch (Exception ex)
                {
                    Publish(new(false, 0, false, ex is System.Net.Http.HttpRequestException or TaskCanceledException ? "Spotify не отвечает. Проверяем соединение…" : ex.Message));
                    next = DateTimeOffset.UtcNow.AddSeconds(5);
                }
                finally { if (change is not null) Interlocked.CompareExchange(ref requested, null, change); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Publish(SpotifyVolumeSnapshot value)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Interlocked.Exchange(ref lastReadTicks, DateTime.UtcNow.Ticks); Volatile.Write(ref snapshot, value);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        _ = worker.ContinueWith(completed => { if (ownsApi) api.Dispose(); stopping.Dispose(); }, TaskScheduler.Default);
    }
}
