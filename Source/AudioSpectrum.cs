using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace VpnZapretSwitch;

// Output-device loopback only. Samples stay in memory, are reduced to twenty
// frequency levels, and are never recorded, uploaded or taken from a microphone.
internal sealed class AudioSpectrum : IDisposable
{
    internal const int Bands = 20;
    private const int Size = 2048;
    private readonly object gate = new();
    private readonly float[] samples = new float[Size];
    private readonly Complex[] fft = new Complex[Size];
    private readonly float[] levels = new float[Bands];
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private int position, disposed;
    private long lastPacket;
    private bool failed;
    private string note = "Звук устройства вывода Windows";
    internal string Note => Volatile.Read(ref note);
    internal AudioSpectrum(bool listen = true) => worker = listen ? Task.Run(RunAsync) : Task.CompletedTask;
    internal float[] ReadLevels()
    {
        lock (gate)
        {
            if (Stopwatch.GetElapsedTime(lastPacket).TotalMilliseconds > 240) return new float[Bands];
            return (float[])levels.Clone();
        }
    }
    private async Task RunAsync()
    {
        var token = stopping.Token;
        WasapiLoopbackCapture? capture = null;
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string deviceId = "";
            long nextDeviceCheck = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (capture is null || Stopwatch.GetTimestamp() >= nextDeviceCheck || Volatile.Read(ref failed))
                    {
                        using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (capture is null || current.ID != deviceId || Volatile.Read(ref failed))
                        {
                            ReleaseCapture(capture, device); capture = null; device = null;
                            device = enumerator.GetDevice(current.ID); deviceId = current.ID;
                            var next = new WasapiLoopbackCapture(device);
                            next.DataAvailable += (_, e) => Process(e.Buffer, e.BytesRecorded, next.WaveFormat);
                            next.RecordingStopped += (_, _) => Volatile.Write(ref failed, true);
                            Volatile.Write(ref failed, false); capture = next;
                            lock (gate) { position = 0; Array.Clear(levels); }
                            capture.StartRecording();
                            Volatile.Write(ref note, "Звук устройства вывода Windows");
                        }
                        nextDeviceCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 2;
                    }
                }
                catch
                {
                    Volatile.Write(ref note, "Визуализация звука временно недоступна");
                    Volatile.Write(ref failed, true);
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { Volatile.Write(ref note, "Визуализация звука недоступна"); }
        finally
        {
            ReleaseCapture(capture, device);
        }
    }
    private static void ReleaseCapture(WasapiLoopbackCapture? capture, MMDevice? device)
    {
        try { capture?.StopRecording(); } catch { }
        try { capture?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
    }
    internal void Process(byte[] data, int count, WaveFormat format)
    {
        int bytes = format.BitsPerSample / 8, channels = format.Channels;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (bytes is < 2 or > 4 || channels < 1 || format.BlockAlign < bytes * channels) return;
        lock (gate)
        {
            lastPacket = Stopwatch.GetTimestamp();
            for (int frame = 0; frame + format.BlockAlign <= count; frame += format.BlockAlign)
            {
                float mono = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = frame + channel * bytes;
                    float value = floating && bytes == 4 ? BitConverter.ToSingle(data, offset) : bytes switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768f,
                        3 => ((data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8) / 8388608f,
                        4 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) / 2147483648f,
                        _ => 0
                    };
                    if (float.IsFinite(value)) mono += value / channels;
                }
                samples[position++] = Math.Clamp(mono, -1, 1);
                if (position < Size) continue;
                position = 0; Transform(format.SampleRate);
            }
        }
    }
    private void Transform(int sampleRate)
    {
        for (int i = 0; i < Size; i++) { fft[i].X = samples[i] * (float)FastFourierTransform.HannWindow(i, Size); fft[i].Y = 0; }
        FastFourierTransform.FFT(true, 11, fft);
        double high = Math.Min(16000, sampleRate * .48);
        for (int band = 0; band < Bands; band++)
        {
            double lowFrequency = 45 * Math.Pow(high / 45, band / (double)Bands);
            double highFrequency = 45 * Math.Pow(high / 45, (band + 1d) / Bands);
            int lowBin = Math.Clamp((int)(lowFrequency * Size / sampleRate), 1, Size / 2 - 1);
            int highBin = Math.Clamp((int)Math.Ceiling(highFrequency * Size / sampleRate), lowBin + 1, Size / 2);
            double magnitude = 0;
            for (int bin = lowBin; bin < highBin; bin++) magnitude = Math.Max(magnitude, Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y) * 2);
            // Extend the quiet end by 26 dB and use a perceptual curve. Quiet
            // music stays visible, while exact silence and very low noise stay 0.
            double normalized = Math.Clamp((20 * Math.Log10(Math.Max(1e-10, magnitude)) + 94) / 76, 0, 1);
            levels[band] = (float)Math.Pow(normalized, .55);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        _ = worker.ContinueWith(completed => stopping.Dispose(), TaskScheduler.Default);
    }
}
