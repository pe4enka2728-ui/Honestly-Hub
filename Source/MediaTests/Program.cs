using NAudio.Wave;
using System.IO;
using NAudio.CoreAudioApi;
using VpnZapretSwitch;

if (args.Contains("--live"))
{
    using var media = new SpotifyMedia(); using var audio = new AudioSpectrum();
    float peak = 0;
    for (int i = 0; i < 24; i++)
    { await Task.Delay(250); peak = Math.Max(peak, audio.ReadLevels().Max()); }
    var state = media.Snapshot;
    Console.WriteLine($"READ ONLY: Spotify session={state.Present}, playing={state.Playing}, coverBytes={state.Artwork?.Length ?? 0}, controls={state.Previous}/{state.TogglePause}/{state.Next}");
    Console.WriteLine($"Output audio peak={peak:F3}; {audio.Note}. No media commands sent; no audio saved.");
    Console.WriteLine("Media status: " + state.Note);
    try
    {
        var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        Console.WriteLine("OS media source IDs: " + string.Join(", ", manager.GetSessions().Select(item => item.SourceAppUserModelId)));
    }
    catch (Exception ex) { Console.WriteLine("OS media error: " + ex.GetType().Name + " " + ex.Message); }
    return;
}
if (args.Contains("--volume-read"))
{
    using var spotify = new SpotifyVolume();
    for (int i = 0; i < 40 && !spotify.Snapshot.Available; i++) await Task.Delay(500);
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var volume = spotify.Snapshot;
    Console.WriteLine($"Spotify internal available={volume.Available}; percent={volume.Percent}; muted={volume.Muted}; Windows master={device.AudioEndpointVolume.MasterVolumeLevelScalar:F4}; masterMuted={device.AudioEndpointVolume.Mute}");
    Console.WriteLine(volume.Note);
    return;
}
using (var spectrum = new AudioSpectrum(listen: false))
{
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    var silence = new byte[2048 * 2 * sizeof(float)];
    spectrum.Process(silence, silence.Length, format);
    if (spectrum.ReadLevels().Any(x => x != 0)) throw new Exception("Silence produced a wave.");
    var signal = new byte[silence.Length];
    for (int sample = 0; sample < 2048; sample++)
    {
        float value = (float)(.5 * Math.Sin(2 * Math.PI * 1000 * sample / 48000));
        for (int channel = 0; channel < 2; channel++) BitConverter.TryWriteBytes(signal.AsSpan((sample * 2 + channel) * 4, 4), value);
    }
    spectrum.Process(signal, signal.Length, format);
    var levels = spectrum.ReadLevels();
    int dominant = Array.IndexOf(levels, levels.Max());
    if (levels.Any(x => !float.IsFinite(x) || x < 0 || x > 1) || levels.Max() < .6 || dominant is < 9 or > 12)
        throw new Exception("1 kHz signal was not represented by the expected spectrum band.");
    foreach (var amplitude in new[] { .0001, .0000001 })
    {
        for (int sample = 0; sample < 2048; sample++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * sample / 48000));
            for (int channel = 0; channel < 2; channel++) BitConverter.TryWriteBytes(signal.AsSpan((sample * 2 + channel) * 4, 4), value);
        }
        spectrum.Process(signal, signal.Length, format);
        float peak = spectrum.ReadLevels().Max();
        if (amplitude == .0001 && peak < .2) throw new Exception("Quiet music was not visible.");
        if (amplitude < .0001 && peak != 0) throw new Exception("Very low noise generated a visible wave.");
        Console.WriteLine($"Spectrum amplitude={amplitude:G}; peak={peak:F3}");
    }
    spectrum.Process(silence, silence.Length, format);
    if (spectrum.ReadLevels().Any(x => x != 0)) throw new Exception("Silence did not clear the spectrum.");
    spectrum.Process(signal, signal.Length, format);
    await Task.Delay(300);
    if (spectrum.ReadLevels().Any(x => x != 0)) throw new Exception("Stopped input left stale audio bars.");
}
await SpotifyApiChecks.RunAsync();
string fixture = Path.Combine(AppContext.BaseDirectory, "app-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new AppShortcutStore(Path.Combine(fixture, "applications.json"));
    string first = Path.Combine(fixture, "A", "same.exe"), second = Path.Combine(fixture, "B", "same.exe");
    if (store.Load().Count != 0) throw new Exception("Missing app store did not return empty list.");
    store.Save([new(first, "First"), new(first.ToUpperInvariant(), "Duplicate"), new(second, "Second")]);
    var pinned = store.Load();
    if (pinned.Count != 2 || pinned[0].Name != "First") throw new Exception("Persistent path deduplication failed.");
    var windows = new[] { new AppWindow(10, 100, first, "one"), new AppWindow(10, 101, first, "two"), new AppWindow(20, 200, "", "Unknown app") };
    var catalog = AppCatalog.Merge(pinned, windows, new HashSet<string>([first.ToUpperInvariant()], StringComparer.OrdinalIgnoreCase));
    if (catalog.Length != 3 || !catalog[0].Running || catalog[1].Running || !catalog[2].Running || catalog[0].Window != 100)
        throw new Exception("App running/closed/window grouping failed, or equal basenames were mixed.");
    catalog = AppCatalog.Merge(pinned, [], new HashSet<string>([second], StringComparer.OrdinalIgnoreCase));
    if (catalog.Length != 2 || catalog[0].Running || !catalog[1].Running || catalog[1].Window != 0)
        throw new Exception("Pinned background-process status failed.");
    try { AppShortcutStore.Normalize(Path.Combine(fixture, "script.bat")); throw new Exception("Non-EXE path was accepted."); }
    catch (InvalidDataException) { }
    store.Save(pinned.Take(1));
    if (store.Load().Count != 1 || Directory.GetFiles(fixture, "*.tmp").Length != 0) throw new Exception("Atomic store update failed.");
}
finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
Console.WriteLine("PASS: quiet/silent/noise/stale audio, bounded 1 kHz levels, app persistence/deduplication, full-path identity, grouped windows, closed and background apps. No audio device opened or volume changed.");
