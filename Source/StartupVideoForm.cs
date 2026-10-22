using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace VpnZapretSwitch;

// A per-pixel transparent owned window: Windows composes the live WinForms
// interface, the dimming scrim and the animation with independent opacities.
internal sealed class StartupVideoForm : W.Window, IDisposable
{
    internal static string AnimationPath => Path.Combine(AppContext.BaseDirectory, "Media", "intro.frames.zip");
    private readonly Form owner;
    private readonly C.Canvas stage = new() { IsHitTestVisible = false };
    private readonly C.Image image = new() { Stretch = M.Stretch.Fill, IsHitTestVisible = false };
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch clock = new();
    private IntroSession? session;
    private HwndSource? source;
    private IntroManifest? manifest;
    private bool closed, disposed;
    private int frameIndex = -1;
    private string failure = "";

    private StartupVideoForm(Form owner)
    {
        this.owner = owner;
        Title = "Honestly TUN — вступление";
        WindowStyle = W.WindowStyle.None; ResizeMode = W.ResizeMode.NoResize;
        AllowsTransparency = true; Background = M.Brushes.Transparent;
        ShowInTaskbar = false; WindowStartupLocation = W.WindowStartupLocation.Manual;
        Width = Math.Max(1, owner.ClientSize.Width * 96d / owner.DeviceDpi);
        Height = Math.Max(1, owner.ClientSize.Height * 96d / owner.DeviceDpi);
        var root = new C.Grid { Background = new M.SolidColorBrush(M.Color.FromArgb(166, 0, 0, 0)), ClipToBounds = true };
        M.RenderOptions.SetBitmapScalingMode(stage, M.BitmapScalingMode.HighQuality);
        stage.Children.Add(image);
        var animation = new C.Viewbox
        {
            Child = stage, Stretch = M.Stretch.Uniform, Margin = new W.Thickness(24, 48, 24, 48),
            HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        root.Children.Add(animation);
        Content = root;
        W.Automation.AutomationProperties.SetName(root, "Вступление Honestly TUN. Нажмите в любом месте или Esc для пропуска.");
        // Consume the whole press/release sequence on this modal layer. Closing
        // on mouse-down could send mouse-up to a VPN button underneath it.
        root.PreviewMouseDown += (_, e) => e.Handled = true;
        root.PreviewMouseUp += (_, e) => { e.Handled = true; Finish(); };
        root.TouchUp += (_, e) => { e.Handled = true; Finish(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Finish(); }
        };
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
            AlignToOwner();
        };
        ContentRendered += async (_, _) =>
        {
            if (session is not null || closed) return;
            AlignToOwner(); Focus();
            session = new IntroSession(AnimationPath);
            try
            {
                manifest = await session.Ready;
                if (closed) return;
                stage.Width = manifest.Width; stage.Height = manifest.Height;
                ShowAvailableFrame(0);
                clock.Start(); timer.Start();
            }
            catch (OperationCanceledException)
            {
                if (!closed) { failure = "Вступление пропущено: загрузка анимации заняла слишком много времени."; Finish(); }
            }
            catch (Exception ex)
            {
                if (!closed) { failure = "Вступление пропущено: " + DescribeError(ex); Finish(); }
            }
        };
        timer.Tick += (_, _) =>
        {
            if (manifest is null || closed) return;
            if (session?.Error is { } error) { failure = "Вступление прервано: " + DescribeError(error); Finish(); return; }
            int next = (int)(clock.Elapsed.TotalSeconds * manifest.FramesPerSecond);
            if (next >= manifest.FrameCount) { Finish(); return; }
            ShowAvailableFrame(next);
        };
        Closed += (_, _) => { closed = true; Release(); };
        owner.LocationChanged += OwnerBoundsChanged;
        owner.SizeChanged += OwnerBoundsChanged;
        owner.DpiChanged += OwnerDpiChanged;
        owner.FormClosed += OwnerClosed;
    }

    private void ShowAvailableFrame(int desired)
    {
        if (session is null) return;
        // Only a few decoded frames are buffered; slow rendering drops overdue
        // frames instead of changing the 3.4-second duration or growing memory.
        while (frameIndex < desired && session.Frames.TryRead(out var frame))
        {
            image.Source = frame.Image;
            image.Width = frame.Layout.Width; image.Height = frame.Layout.Height;
            C.Canvas.SetLeft(image, frame.Layout.X); C.Canvas.SetTop(image, frame.Layout.Y);
            frameIndex = frame.Index;
        }
    }
    private void OwnerBoundsChanged(object? sender, EventArgs e) => AlignToOwner();
    private void OwnerDpiChanged(object? sender, DpiChangedEventArgs e) => AlignToOwner();
    private void OwnerClosed(object? sender, FormClosedEventArgs e) => Finish();
    private void AlignToOwner()
    {
        if (closed || owner.IsDisposed || owner.WindowState == FormWindowState.Minimized) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var bounds = owner.RectangleToScreen(owner.ClientRectangle);
        // Set the HWND in physical pixels; WPF handles its own DIP conversion.
        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x02E0 or 0x007E)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignToOwner));
        return IntPtr.Zero;
    }
    private void Finish() { if (!closed) Close(); }
    internal static string ShowIntro(Form owner)
    {
        if (!File.Exists(AnimationPath)) return "Вступление пропущено: Media/intro.frames.zip не найден.";
        try
        {
            owner.Update();
            using var intro = new StartupVideoForm(owner);
            new WindowInteropHelper(intro) { Owner = owner.Handle }.EnsureHandle();
            intro.ShowDialog();
            return intro.failure;
        }
        catch (Exception ex) { return "Вступление пропущено: " + DescribeError(ex); }
    }
    private static string DescribeError(Exception ex)
    {
        var cause = ex.GetBaseException();
        string text = cause.Message.Replace('\r', ' ').Replace('\n', ' ');
        return cause.GetType().Name + ": " + (text.Length > 350 ? text[..350] + "…" : text);
    }
    private void Release()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); session?.Dispose(); session = null; image.Source = null;
        owner.LocationChanged -= OwnerBoundsChanged; owner.SizeChanged -= OwnerBoundsChanged;
        owner.DpiChanged -= OwnerDpiChanged; owner.FormClosed -= OwnerClosed;
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
    }
    public void Dispose() { if (!closed && IsVisible) Close(); Release(); }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private sealed class IntroSession : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(8));
        private readonly TaskCompletionSource<IntroManifest> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<IntroFrame> channel = Channel.CreateBounded<IntroFrame>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Task producer;
        private int disposed;
        private Exception? error;
        internal Task<IntroManifest> Ready => ready.Task;
        internal ChannelReader<IntroFrame> Frames => channel.Reader;
        internal Exception? Error => Volatile.Read(ref error);
        internal IntroSession(string path) => producer = Task.Run(() => ProduceAsync(path));
        private async Task ProduceAsync(string path)
        {
            var token = cancellation.Token;
            try
            {
                using var file = File.OpenRead(path);
                if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                var metadata = archive.GetEntry("manifest.json") ?? throw new InvalidDataException();
                if (metadata.Length > 256 * 1024) throw new InvalidDataException();
                IntroManifest manifest;
                using (var stream = metadata.Open())
                    manifest = JsonSerializer.Deserialize<IntroManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
                if (manifest.Version != 1 || manifest.Width is < 1 or > 1920 || manifest.Height is < 1 or > 1080 ||
                    manifest.FramesPerSecond is < 1 or > 60 || manifest.FrameCount is < 1 or > 300 ||
                    manifest.Frames?.Count != manifest.FrameCount || manifest.FrameCount / (double)manifest.FramesPerSecond > 10)
                    throw new InvalidDataException();
                for (int index = 0; index < manifest.FrameCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var layout = manifest.Frames[index];
                    if (layout.X < 0 || layout.Y < 0 || layout.Width < 1 || layout.Height < 1 ||
                        (long)layout.X + layout.Width > manifest.Width || (long)layout.Y + layout.Height > manifest.Height)
                        throw new InvalidDataException();
                    var entry = archive.GetEntry($"frames/{index:D3}.png") ?? throw new InvalidDataException();
                    if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                    BitmapFrame bitmap;
                    using (var stream = entry.Open())
                        bitmap = IntroFrameDecoder.Decode(stream, layout.Width, layout.Height);
                    await channel.Writer.WriteAsync(new IntroFrame(index, bitmap, layout), token).ConfigureAwait(false);
                    if (index == 0)
                    {
                        cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                        ready.TrySetResult(manifest);
                    }
                }
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(token); channel.Writer.TryComplete(); }
            catch (Exception ex) { Volatile.Write(ref error, ex); ready.TrySetException(ex); channel.Writer.TryComplete(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            _ = producer.ContinueWith(completed =>
            {
                while (channel.Reader.TryRead(out _)) { }
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }
    }
    private sealed record IntroFrame(int Index, BitmapSource Image, FrameLayout Layout);
    private sealed class IntroManifest
    {
        public IntroManifest() { }
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FramesPerSecond { get; set; }
        public int FrameCount { get; set; }
        public List<FrameLayout>? Frames { get; set; }
    }
    private sealed class FrameLayout
    {
        public FrameLayout() { }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
