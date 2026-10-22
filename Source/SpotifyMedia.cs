using Windows.Media.Control;
using Windows.Storage.Streams;

namespace VpnZapretSwitch;

internal enum MusicCommand { Previous, TogglePause, Next }
internal sealed record MusicSnapshot(bool Present, bool Playing, string Title, string Artist,
    byte[]? Artwork, bool Previous, bool TogglePause, bool Next, string Note)
{
    internal static MusicSnapshot Empty(string note = "Откройте трек в Spotify") =>
        new(false, false, "Spotify", note, null, false, false, false, note);
}

// Only Spotify sessions are read/controlled. Browser and other player sessions
// never receive global media keys or commands intended for Spotify.
internal sealed class SpotifyMedia : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Task polling;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private MusicSnapshot snapshot = MusicSnapshot.Empty();
    private string artworkKey = "";
    private byte[]? artwork;
    private int disposed;
    internal MusicSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal SpotifyMedia() => polling = Task.Run(PollAsync);
    private async Task PollAsync()
    {
        var token = stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try { await RefreshAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { manager = null; Volatile.Write(ref snapshot, MusicSnapshot.Empty("Медиасеанс Spotify недоступен")); }
                finally { gate.Release(); }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync(CancellationToken token)
    {
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
        return manager.GetSessions()
            .Where(item => item.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .FirstOrDefault();
    }
    private async Task RefreshAsync(CancellationToken token)
    {
        var session = await FindSessionAsync(token).ConfigureAwait(false);
        if (session is null)
        {
            artworkKey = ""; artwork = null;
            Volatile.Write(ref snapshot, MusicSnapshot.Empty()); return;
        }
        var info = session.GetPlaybackInfo();
        var media = await session.TryGetMediaPropertiesAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
        string key = session.SourceAppUserModelId + "\n" + media.Title + "\n" + media.Artist + "\n" + media.AlbumTitle + "\n" + media.TrackNumber;
        if (key != artworkKey || artwork is null)
        {
            artworkKey = key; artwork = null;
            if (media.Thumbnail is not null)
            {
                try
                {
                    using var stream = await media.Thumbnail.OpenReadAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
                    if (stream.Size is > 0 and <= 4 * 1024 * 1024)
                    {
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        uint loaded = await reader.LoadAsync((uint)stream.Size).AsTask(token).ConfigureAwait(false);
                        if (loaded == stream.Size) { artwork = new byte[loaded]; reader.ReadBytes(artwork); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* A missing cover must not hide the track or controls. */ }
            }
        }
        token.ThrowIfCancellationRequested();
        var controls = info.Controls;
        bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Volatile.Write(ref snapshot, new(true, playing, string.IsNullOrWhiteSpace(media.Title) ? "Spotify" : media.Title,
            media.Artist, artwork, controls.IsPreviousEnabled,
            controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled), controls.IsNextEnabled, ""));
    }
    internal async Task<bool> SendAsync(MusicCommand command)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var token = stopping.Token;
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            var session = await FindSessionAsync(token).ConfigureAwait(false);
            if (session is null) return false;
            var info = session.GetPlaybackInfo(); var controls = info.Controls;
            bool accepted = command switch
            {
                MusicCommand.Previous when controls.IsPreviousEnabled => await session.TrySkipPreviousAsync().AsTask(token).ConfigureAwait(false),
                MusicCommand.Next when controls.IsNextEnabled => await session.TrySkipNextAsync().AsTask(token).ConfigureAwait(false),
                MusicCommand.TogglePause when controls.IsPlayPauseToggleEnabled => await session.TryTogglePlayPauseAsync().AsTask(token).ConfigureAwait(false),
                MusicCommand.TogglePause when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && controls.IsPauseEnabled => await session.TryPauseAsync().AsTask(token).ConfigureAwait(false),
                MusicCommand.TogglePause when controls.IsPlayEnabled => await session.TryPlayAsync().AsTask(token).ConfigureAwait(false),
                _ => false
            };
            await Task.Delay(180, token).ConfigureAwait(false);
            await RefreshAsync(token).ConfigureAwait(false);
            return accepted;
        }
        catch (OperationCanceledException) { return false; }
        catch { return false; }
        finally { if (entered) gate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        // An in-flight OS media request may still own the gate. No UI thread
        // waits for it, and cancellation prevents it publishing a later result.
        _ = polling.ContinueWith(completed => stopping.Dispose(), TaskScheduler.Default);
    }
}
