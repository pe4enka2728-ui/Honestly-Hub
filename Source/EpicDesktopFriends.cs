using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VpnZapretSwitch;

// Product runtime: all input is confined to a verified Epic window. No credentials are
// decrypted, no private Epic endpoints are called, and screenshots stay in memory.
internal sealed class EpicDesktopFriends : IEpicFriends
{
    public Task CheckAvailableAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (CreateEngine(navigation: true) is null)
            throw new InvalidOperationException("Не доступно распознавание Windows. Установите язык распознавания текста для Windows.");
        return Task.CompletedTask;
    }
    public async Task<string> CheckSearchAsync(string launcher, CancellationToken cancellation)
    {
        await CheckAvailableAsync(cancellation).ConfigureAwait(false);
        await OpenSearchAsync(launcher, null, cancellation).ConfigureAwait(false);
        return "Поиск друзей Epic открыт и распознан. Аккаунт не переключался, заявки не отправлялись.";
    }
    public async Task<string> CheckStatusAsync(string launcher, string nickname, CancellationToken cancellation)
    {
        nickname=EpicFriendNames.Validate(nickname);
        await CheckAvailableAsync(cancellation).ConfigureAwait(false);
        IntPtr window=await OpenSearchAsync(launcher,null,cancellation).ConfigureAwait(false);
        using var frame=Capture(window);
        var lines=await ReadSceneAsync(frame.Bitmap,GetDpiForWindow(window)/96d,cancellation).ConfigureAwait(false);
        var header=EpicFriendVision.AddHeader(lines,frame.Bitmap.Width) ?? throw new InvalidOperationException("Откройте поиск друзей Epic.");
        var target=FindSendTarget(frame.Bitmap,GetDpiForWindow(window)/96d,lines,header,nickname);
        if(target?.ExistingStatus is not null) return "У точного ника подтверждена существующая заявка или дружба. Повторное добавление не требуется. Ничего не отправлялось.";
        if(target is not null) return "Точный ник и кнопка добавления распознаны. Проверка ничего не отправляла.";
        throw new InvalidOperationException("Статус точного игрока не распознан. Найдите его вручную в поиске Epic и повторите проверку. Ник в Epic не вводился, заявка не отправлялась.");
    }
    public async Task<EpicFriendAttempt> SendAsync(string launcher, string accountId, string nickname, CancellationToken cancellation)
    {
        nickname = EpicFriendNames.Validate(nickname); bool clickedSend = false;
        try
        {
            IntPtr window = await OpenSearchAsync(launcher, accountId, cancellation).ConfigureAwait(false);
            EpicOcrLine? header = null;
            using (var frame = Capture(window))
            {
                var lines = await ReadSceneAsync(frame.Bitmap, GetDpiForWindow(window) / 96d, cancellation).ConfigureAwait(false);
                header = EpicFriendVision.AddHeader(lines, frame.Bitmap.Width) ?? throw new InvalidOperationException("Панель друзей изменилась.");
                var placeholder = lines.FirstOrDefault(l => l.Bounds.Top > header.Bounds.Bottom &&
                    l.Bounds.Top < header.Bounds.Bottom + header.Bounds.Height * 4 &&
                    (EpicFriendVision.Words(l.Text).Contains("поиск игроков") || EpicFriendVision.Words(l.Text).Contains("search players")));
                Point search = placeholder is null ? new(header.Bounds.Left + header.Bounds.Height * 4, header.Bounds.Bottom + header.Bounds.Height * 2) : EpicFriendVision.Center(placeholder.Bounds);
                Click(window, frame, search); await Task.Delay(150, cancellation).ConfigureAwait(false);
                ReplaceText(window, nickname);
            }
            EpicFriendSendTarget? target = null; bool openedProfile = false, foundName = false, rowAction = false;
            EpicOcrLine[] beforeSend = [];
            var searchDeadline = DateTime.UtcNow.AddSeconds(18);
            while (DateTime.UtcNow < searchDeadline)
            {
                await Task.Delay(650, cancellation).ConfigureAwait(false); VerifyAccount(accountId);
                using var frame = Capture(window); var lines = await ReadSceneAsync(frame.Bitmap, GetDpiForWindow(window) / 96d, cancellation).ConfigureAwait(false);
                header = EpicFriendVision.AddHeader(lines, frame.Bitmap.Width);
                if (header is null) throw new InvalidOperationException("Закрыта панель поиска Epic.");
                target = FindSendTarget(frame.Bitmap, GetDpiForWindow(window) / 96d, lines, header, nickname);
                if (target is not null) break;
                var match = EpicFriendVision.SearchResult(lines, header, nickname);
                foundName |= match is not null;
                if (match is not null && !openedProfile)
                { Click(window, frame, EpicFriendVision.Center(match.Bounds)); openedProfile = true; continue; }
            }
            if (target is null) return new(foundName ? EpicFriendStatus.Failed : EpicFriendStatus.NotFound, foundName ?
                "Точный ник найден, но кнопка добавления не распознана. Проверьте, не отправлена ли заявка ранее. Новое нажатие не выполнялось." :
                "Точный ник не найден или его карточка не распознана. Заявка не отправлена.");
            if (target.ExistingStatus is { } existing) return new(existing, existing == EpicFriendStatus.Pending ? "Заявка уже отправлена." : "Уже в друзьях.");
            // Verify the same target name and action in a second independent observation.
            await Task.Delay(500, cancellation).ConfigureAwait(false);
            using (var frame = Capture(window))
            {
                var lines = await ReadSceneAsync(frame.Bitmap, GetDpiForWindow(window) / 96d, cancellation).ConfigureAwait(false);
                header = EpicFriendVision.AddHeader(lines, frame.Bitmap.Width) ?? throw new InvalidOperationException("Панель друзей закрыта.");
                var confirmed = FindSendTarget(frame.Bitmap, GetDpiForWindow(window) / 96d, lines, header, nickname);
                if (confirmed?.ExistingStatus is { } status) return new(status, status == EpicFriendStatus.Pending ? "Заявка уже отправлена." : "Уже в друзьях.");
                if (confirmed is null || confirmed.InRow != target.InRow || !EpicFriendVision.SamePlace(confirmed.Name.Bounds, target.Name.Bounds) ||
                    !EpicFriendVision.SamePlace(confirmed.Action, target.Action))
                    return new(EpicFriendStatus.Failed, "Карточка игрока изменилась. Заявка не отправлена.", true);
                VerifyAccount(accountId); cancellation.ThrowIfCancellationRequested();
                beforeSend = lines; rowAction = confirmed.InRow;
                Click(window, frame, EpicFriendVision.Center(confirmed.Action), [confirmed.Name.Bounds, confirmed.Action], () => clickedSend = true);
            }
            // Once clicked, do not retry. Cancellation waits for this short acknowledgement
            // phase so an uncertain request is never mistaken for a safe retry.
            var acknowledgement = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < acknowledgement)
            {
                await Task.Delay(650).ConfigureAwait(false);
                VerifyAccount(accountId);
                using var frame = Capture(window); var lines = await ReadSceneAsync(frame.Bitmap, GetDpiForWindow(window) / 96d, CancellationToken.None).ConfigureAwait(false);
                header = EpicFriendVision.AddHeader(lines, frame.Bitmap.Width);
                var updated = header is null ? null : EpicFriendVision.Profile(lines, header, nickname);
                if (updated?.ExistingStatus is not null)
                    return new(EpicFriendStatus.Sent, "Заявка отправлена; состояние подтверждено в Epic.");
                if (rowAction && header is not null && EpicFriendVision.SearchResult(lines, header, nickname) is not null &&
                    (EpicFriendVision.RowStatus(lines, header, nickname) is not null ||
                     EpicFriendRowButton.HasPending(frame.Bitmap,GetDpiForWindow(window)/96d,EpicFriendVision.SearchResult(lines,header,nickname)) ||
                     EpicFriendVision.NewSentNotice(beforeSend, lines)))
                    return new(EpicFriendStatus.Sent, "Заявка отправлена кнопкой в строке игрока; состояние подтверждено в Epic.");
                if (lines.Any(l => EpicFriendVision.Words(l.Text).Contains("слишком много") || EpicFriendVision.Words(l.Text).Contains("too many")))
                    return new(EpicFriendStatus.Failed, "Epic ограничил отправку заявок. Обработка остановлена.", true);
            }
            return new(EpicFriendStatus.Uncertain, "Команда отправлена, но Epic не подтвердил результат. Повторная отправка отключена; проверьте заявку вручную.", true);
        }
        catch (OperationCanceledException) when (!clickedSend) { throw; }
        catch (Exception) when (clickedSend)
        { return new(EpicFriendStatus.Uncertain, "Не удалось прочитать результат отправки. Проверьте заявку в Epic; повторная отправка не выполнялась.", true); }
    }
    private static EpicFriendSendTarget? FindSendTarget(Bitmap bitmap, double scale, EpicOcrLine[] lines, EpicOcrLine header, string nickname)
    {
        var profile = EpicFriendVision.Profile(lines, header, nickname);
        if (profile is not null) return new(profile.Name, profile.Action.Bounds, profile.ExistingStatus, false);
        var name = EpicFriendVision.SearchResult(lines, header, nickname);
        if (name is null) return null;
        if (EpicFriendVision.RowStatus(lines, header, nickname) is { } existing) return new(name, Rectangle.Empty, existing, true);
        if (EpicFriendRowButton.HasPending(bitmap,scale,name)) return new(name,Rectangle.Empty,EpicFriendStatus.Pending,true);
        return EpicFriendRowButton.Find(bitmap, scale, name) is { } button ? new(name, button, null, true) : null;
    }
    private static async Task<IntPtr> OpenSearchAsync(string launcher, string? accountId, CancellationToken cancellation)
    {
        IntPtr window = await WaitWindowAsync(launcher, cancellation).ConfigureAwait(false);
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        SetForegroundWindow(window);
        await Task.Delay(700, cancellation).ConfigureAwait(false);
        var clock = Stopwatch.StartNew(); var navigation = new EpicSearchNavigation();
        while (clock.Elapsed < TimeSpan.FromSeconds(50))
        {
            cancellation.ThrowIfCancellationRequested();
            if (accountId is not null) VerifyAccount(accountId);
            using var frame = Capture(window); double scale = GetDpiForWindow(window) / 96d;
            var lines = await ReadSceneAsync(frame.Bitmap, scale, cancellation, navigation: true).ConfigureAwait(false);
            var header = EpicFriendVision.AddHeader(lines, frame.Bitmap.Width);
            var heading = EpicFriendVision.FriendsHeading(lines, frame.Bitmap.Width);
            var social = EpicFriendVision.SocialHeading(lines, frame.Bitmap.Width);
            var toolbar = header is null && heading is null ? EpicSocialToolbar.Find(frame.Bitmap, scale, social) : null;
            var storeSearch = lines.FirstOrDefault(l => l.Bounds.Top < frame.Bitmap.Height / 4 &&
                (EpicFriendVision.Words(l.Text).Contains("искать в магазине") || EpicFriendVision.Words(l.Text).Contains("search store") || EpicFriendVision.Words(l.Text).Contains("search the store")));
            bool mainVisible = storeSearch is not null || lines.Any(l => l.Bounds.Left < frame.Bitmap.Width / 4 &&
                EpicFriendVision.Words(l.Text) is "магазин" or "библиотека" or "store" or "library");
            bool login = lines.Any(l => EpicFriendVision.Words(l.Text) is "войти" or "sign in" or "sign in to epic games");
            var step = navigation.Next(header is not null, EpicFriendVision.SocialVisible(lines, frame.Bitmap.Width), heading is not null || toolbar is not null, mainVisible, login, clock.Elapsed);
            if (step == EpicSearchStep.Ready) return window;
            if (step == EpicSearchStep.LoginRequired) throw new InvalidOperationException("Epic требует ручного входа. Обновите сохранённый профиль.");
            if (step == EpicSearchStep.OpenAdd)
            {
                if (toolbar is not null) Click(window, frame, toolbar.Add, [toolbar.AddBounds, toolbar.SearchBounds]);
                else Click(window, frame, new Point(frame.Bitmap.Width - (int)Math.Round(118 * scale), EpicFriendVision.Center(heading!.Bounds).Y));
            }
            if (step == EpicSearchStep.OpenSocial)
                Click(window, frame, new Point(frame.Bitmap.Width - (int)Math.Round(70 * scale), storeSearch is null ? (int)Math.Round(80 * scale) : EpicFriendVision.Center(storeSearch.Bounds).Y));
            await Task.Delay(500, cancellation).ConfigureAwait(false);
        }
        string detail = navigation.SawFriends ? "Список друзей найден, но переход к поиску не подтвердился." :
            navigation.SawSocial ? "Панель общения открыта, но заголовок «Друзья» не распознан." : "Панель общения Epic не распознана.";
        throw new InvalidOperationException(detail + " Нажмите «Проверить поиск» во вкладке «Аккаунты». Заявки не отправлялись.");
    }
    private static void VerifyAccount(string expected)
    {
        if (!string.Equals(new EpicWindows().CurrentAccountId, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Epic изменил выбранный аккаунт. Отправка остановлена.");
    }
    private static async Task<IntPtr> WaitWindowAsync(string launcher, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew(); var readiness = new EpicWindowReadiness();
        Size lastSize = Size.Empty;
        var restored = new HashSet<IntPtr>();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellation.ThrowIfCancellationRequested();
            var processIds = new HashSet<uint>();
            using var own = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName("EpicGamesLauncher"))
                using (process)
                try
                {
                    process.Refresh();
                    if (!process.HasExited && process.SessionId == own.SessionId &&
                        EpicProcessImage.Read(process) is { } processPath &&
                        EpicWindows.LauncherFamily(processPath).Equals(EpicWindows.LauncherFamily(launcher), StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add((uint)process.Id);
                        var main = process.MainWindowHandle;
                        if (main != IntPtr.Zero && IsIconic(main) && restored.Add(main)) ShowWindowAsync(main, 9);
                    }
                }
                catch (InvalidOperationException) when(process.HasExited) { }
            var candidates = new List<EpicWindowCandidate>();
            // MainWindowHandle may still refer to a splash or browser popup. Enumerate
            // visible top-level windows of verified launcher processes instead.
            using (new PhysicalDpiScope())
            {
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint id);
                    if (processIds.Contains(id) && IsWindowVisible(window) && IsWindowEnabled(window) && !IsIconic(window) &&
                        GetClientRect(window, out var rect))
                        candidates.Add(new(window, new(rect.Right - rect.Left, rect.Bottom - rect.Top)));
                    return true;
                }, IntPtr.Zero);
            }
            if (candidates.Count > 0) lastSize = candidates.MaxBy(w => (long)w.ClientSize.Width*w.ClientSize.Height)!.ClientSize;
            var result = readiness.Observe(candidates,clock.Elapsed);
            if (result != IntPtr.Zero) return result;
            await Task.Delay(250, cancellation).ConfigureAwait(false);
        }
        throw new InvalidOperationException(lastSize.IsEmpty ? "Основное окно Epic не открылось за 30 секунд. Дождитесь его загрузки и повторите." : EpicCaptureGeometry.Problem(lastSize));
    }
    private sealed record Frame(Bitmap Bitmap, Rectangle ScreenBounds) : IDisposable
    { public void Dispose() => Bitmap.Dispose(); }
    private static Frame Capture(IntPtr window)
    {
        using var dpi = new PhysicalDpiScope();
        RequireForeground(window);
        if (!GetClientRect(window, out var rect)) throw new IOException("Не удалось прочитать размер окна Epic.");
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin)) throw new IOException("Не удалось определить положение окна Epic.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (!EpicCaptureGeometry.IsUsable(new(width,height)))
            throw new InvalidOperationException(EpicCaptureGeometry.Problem(new(width,height)));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, bitmap.Size);
            return new(bitmap, new(origin.X, origin.Y, width, height));
        }
        catch { bitmap.Dispose(); throw; }
    }
    internal static async Task<EpicOcrLine[]> ReadAsync(Bitmap bitmap, CancellationToken cancellation)
        => await ReadCoreAsync(bitmap, CreateEngine(navigation: false) ?? throw new InvalidOperationException("Windows OCR недоступен."), cancellation).ConfigureAwait(false);
    private static OcrEngine? CreateEngine(bool navigation)
    {
        if (navigation)
        {
            var russian = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            if (russian is not null && OcrEngine.TryCreateFromLanguage(russian) is { } ru) return ru;
        }
        return OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.AvailableRecognizerLanguages
            .Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine is not null);
    }
    // Navigation uses known labels. On a verified search page, enlarged complete lines
    // inside its results can also supply names; individual OCR words never supply names.
    internal static async Task<EpicOcrLine[]> ReadSceneAsync(Bitmap bitmap, double uiScale, CancellationToken cancellation, bool navigation = false)
    {
        var full = await ReadCoreAsync(bitmap, CreateEngine(navigation) ?? throw new InvalidOperationException("Windows OCR недоступен."), cancellation).ConfigureAwait(false);
        int width = Math.Min(bitmap.Width, (int)Math.Round(520 * uiScale));
        int left = bitmap.Width - width;
        double factor = Math.Min(2.5, OcrEngine.MaxImageDimension / (double)Math.Max(width, bitmap.Height));
        using var crop = new Bitmap(Math.Max(1, (int)(width * factor)), Math.Max(1, (int)(bitmap.Height * factor)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(crop))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, new Rectangle(Point.Empty, crop.Size), new Rectangle(left, 0, width, bitmap.Height), GraphicsUnit.Pixel);
        }
        var sidebar = await ReadCoreAsync(crop, CreateEngine(navigation: true) ?? throw new InvalidOperationException("Windows OCR недоступен."), cancellation).ConfigureAwait(false);
        Rectangle Map(Rectangle b) => Rectangle.FromLTRB(left + (int)Math.Round(b.Left / factor), (int)Math.Round(b.Top / factor), left + (int)Math.Round(b.Right / factor), (int)Math.Round(b.Bottom / factor));
        EpicOcrLine MapLine(EpicOcrLine l) => new(l.Text, Map(l.Bounds), l.WordBoxes?.Select(w => new EpicOcrWord(w.Text, Map(w.Bounds))).ToArray());
        var mapped = sidebar.Select(MapLine).ToArray();
        var combined = full.ToList();
        foreach (var label in EpicFriendVision.NavigationLabels(mapped))
            if (!combined.Any(l => EpicFriendNames.Exact(l.Text, label.Text) && EpicFriendVision.SamePlace(l.Bounds, label.Bounds))) combined.Add(label);
        if (EpicFriendVision.AddHeader(combined,bitmap.Width) is { } searchHeader)
        {
            var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en",StringComparison.OrdinalIgnoreCase));
            var latin = english is null ? CreateEngine(navigation:false) : OcrEngine.TryCreateFromLanguage(english);
            var resultLines = mapped.AsEnumerable();
            if(latin is not null) resultLines = resultLines.Concat((await ReadCoreAsync(crop,latin,cancellation).ConfigureAwait(false)).Select(MapLine));
            foreach(var line in resultLines.Where(l => EpicFriendVision.WholeSidebarResult(l,searchHeader,bitmap.Size,uiScale,left)))
                if(!combined.Any(old => EpicFriendNames.Exact(old.Text,line.Text) && EpicFriendVision.SamePlace(old.Bounds,line.Bounds))) combined.Add(line);
            await ConfirmNameRowsAsync(bitmap,combined,searchHeader,uiScale,cancellation).ConfigureAwait(false);
        }
        return combined.ToArray();
    }
    private static async Task ConfirmNameRowsAsync(Bitmap bitmap,List<EpicOcrLine> lines,EpicOcrLine header,double scale,CancellationToken cancellation)
    {
        var engine=CreateEngine(navigation:true) ?? throw new InvalidOperationException("Windows OCR недоступен.");
        foreach(var initialRegion in EpicFriendNameVision.Regions(lines,header,bitmap.Size,scale))
        {
            cancellation.ThrowIfCancellationRequested();
            var region=initialRegion.Bounds;
            var rowHint=new EpicOcrLine("",new(region.Left+(int)Math.Round(10*scale),initialRegion.RowBounds.Top,
                (int)Math.Round(5*scale),initialRegion.RowBounds.Height));
            // Only a verified check + Cancel pair may narrow the name column, so its
            // icons do not become letters in the OCR name after a request was sent.
            if(EpicFriendRowButton.HasPending(bitmap,scale,rowHint))
                region.Width=Math.Min(region.Width,bitmap.Width-(int)Math.Round(140*scale)-region.Left);
            if(region.Width<40*scale) continue;
            async Task<EpicOcrLine[]> Pass(int zoom,bool contrast)
            {
                using var crop=new Bitmap(initialRegion.Bounds.Width*zoom,region.Height*zoom,PixelFormat.Format32bppArgb);
                using(var graphics=Graphics.FromImage(crop))
                {
                    graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.Clear(bitmap.GetPixel(region.Left,region.Top));
                    graphics.DrawImage(bitmap,new Rectangle(0,0,region.Width*zoom,region.Height*zoom),region,GraphicsUnit.Pixel);
                }
                if(contrast) for(int y=0;y<crop.Height;y++) for(int x=0;x<crop.Width;x++)
                {
                    var color=crop.GetPixel(x,y);
                    crop.SetPixel(x,y,Math.Max(color.R,Math.Max(color.G,color.B))>=180?Color.Black:Color.White);
                }
                var observed=await ReadCoreAsync(crop,engine,cancellation).ConfigureAwait(false);
                Rectangle Map(Rectangle r)=>Rectangle.FromLTRB(region.Left+(int)Math.Floor(r.Left/(double)zoom),
                    region.Top+(int)Math.Floor(r.Top/(double)zoom),region.Left+(int)Math.Ceiling(r.Right/(double)zoom),region.Top+(int)Math.Ceiling(r.Bottom/(double)zoom));
                return observed.Select(l=>new EpicOcrLine(l.Text,Map(l.Bounds),l.WordBoxes?.Select(w=>new EpicOcrWord(w.Text,Map(w.Bounds))).ToArray())).ToArray();
            }
            var original=await Pass(3,false).ConfigureAwait(false);
            var contrast=await Pass(2,true).ConfigureAwait(false);
            var confirmed=EpicFriendNameVision.Consensus(original,contrast,region,scale);
            if(confirmed is not null)
                confirmed=confirmed with {Bounds=Rectangle.FromLTRB(confirmed.Bounds.Left,initialRegion.RowBounds.Top,confirmed.Bounds.Right,initialRegion.RowBounds.Bottom)};
            EpicFriendNameVision.ReplaceRow(lines,region,confirmed);
        }
    }
    internal static async Task<EpicOcrLine[]> ReadCoreAsync(Bitmap bitmap, OcrEngine engine, CancellationToken cancellation)
    {
        var ocrSize = EpicCaptureGeometry.OcrSize(bitmap.Size,(int)OcrEngine.MaxImageDimension);
        if (ocrSize != bitmap.Size)
        {
            using var scaled = new Bitmap(ocrSize.Width,ocrSize.Height,PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(bitmap,new Rectangle(Point.Empty,scaled.Size),new Rectangle(Point.Empty,bitmap.Size),GraphicsUnit.Pixel);
            }
            var lines=await ReadCoreAsync(scaled,engine,cancellation).ConfigureAwait(false);
            Rectangle Map(Rectangle b)=>EpicCaptureGeometry.RestoreOcrBounds(b,bitmap.Size,scaled.Size);
            return lines.Select(l=>new EpicOcrLine(l.Text,Map(l.Bounds),l.WordBoxes?.Select(w=>new EpicOcrWord(w.Text,Map(w.Bounds))).ToArray())).ToArray();
        }
        using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        { writer.WriteBytes(png.ToArray()); await writer.StoreAsync().AsTask(cancellation).ConfigureAwait(false); await writer.FlushAsync().AsTask(cancellation).ConfigureAwait(false); writer.DetachStream(); }
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation).ConfigureAwait(false);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellation).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(software).AsTask(cancellation).ConfigureAwait(false);
        return result.Lines.Where(l => l.Words.Count > 0).Select(line =>
        {
            double left = line.Words.Min(w => w.BoundingRect.Left), top = line.Words.Min(w => w.BoundingRect.Top);
            double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            return new EpicOcrLine(line.Text, Rectangle.FromLTRB((int)left, (int)top, (int)Math.Ceiling(right), (int)Math.Ceiling(bottom)),
                line.Words.Select(w => new EpicOcrWord(w.Text, Rectangle.FromLTRB((int)w.BoundingRect.Left, (int)w.BoundingRect.Top, (int)Math.Ceiling(w.BoundingRect.Right), (int)Math.Ceiling(w.BoundingRect.Bottom)))).ToArray());
        }).ToArray();
    }
    private static void RequireForeground(IntPtr window)
    {
        if (GetForegroundWindow() != window || !IsWindowEnabled(window))
            throw new InvalidOperationException("Окно Epic потеряло фокус или открыто другое окно. Обработка остановлена.");
    }
    private static void Click(IntPtr window, Frame frame, Point point, Rectangle[]? stableAreas = null, Action? beforeInput = null)
    {
        using var dpi = new PhysicalDpiScope();
        RequireForeground(window); var origin = new NativePoint();
        if(!ClientToScreen(window, ref origin)) throw new IOException("Не удалось определить положение окна Epic.");
        if (origin.X != frame.ScreenBounds.X || origin.Y != frame.ScreenBounds.Y || !GetClientRect(window, out var bounds) ||
            bounds.Right != frame.ScreenBounds.Width || bounds.Bottom != frame.ScreenBounds.Height || !new Rectangle(Point.Empty, frame.Bitmap.Size).Contains(point))
            throw new InvalidOperationException("Окно Epic переместилось. Повторите операцию.");
        var screen = new NativePoint { X = origin.X + point.X, Y = origin.Y + point.Y };
        if (GetAncestor(WindowFromPoint(screen), 2) != window) throw new InvalidOperationException("Окно Epic перекрыто другим приложением.");
        if (stableAreas is not null)
        {
            using var current = Capture(window);
            foreach (var area in stableAreas)
            {
                var region = Rectangle.Intersect(Rectangle.Inflate(area, 2, 2), new Rectangle(Point.Empty, frame.Bitmap.Size));
                for (int y = region.Top; y < region.Bottom; y += 2)
                    for (int x = region.Left; x < region.Right; x += 2)
                        if (frame.Bitmap.GetPixel(x, y) != current.Bitmap.GetPixel(x, y))
                            throw new InvalidOperationException("Карточка игрока изменилась перед отправкой. Заявка не отправлена.");
            }
        }
        RequireForeground(window);
        SetCursorPos(screen.X, screen.Y);
        beforeInput?.Invoke();
        try { Send([Mouse(2), Mouse(4)]); }
        finally { SendInput(1, [Mouse(4)], Marshal.SizeOf<Input>()); }
    }
    private static void ReplaceText(IntPtr window, string value)
    {
        RequireForeground(window);
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(code => (GetAsyncKeyState(code) & 0x8000) != 0))
            throw new InvalidOperationException("Отпустите Ctrl, Alt, Shift и Win перед обработкой Epic.");
        try { Send([Key(0x11, '\0', 0), Key(0x41, '\0', 0), Key(0x41, '\0', 2), Key(0x11, '\0', 2)]); }
        finally { SendInput(2, [Key(0x41, '\0', 2), Key(0x11, '\0', 2)], Marshal.SizeOf<Input>()); }
        foreach (char c in value) { RequireForeground(window); Send([Key(0, c, 4), Key(0, c, 6)]); }
    }
    private static Input Key(ushort code, char scan, uint flags) => new() { Type = 1, Data = new Union { Keyboard = new Keyboard { Code = code, Scan = scan, Flags = flags } } };
    private static Input Mouse(uint flags) => new() { Data = new Union { Mouse = new MouseData { Flags = flags } } };
    private static void Send(Input[] input)
    { if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) throw new IOException("Windows не передала ввод в Epic."); }
    // This scope must never span await: pooled continuation threads can have a
    // different DPI context. Capture and click use the same physical coordinates.
    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        internal PhysicalDpiScope()
        { if(previous==IntPtr.Zero) throw new IOException("Windows не позволила измерить окно Epic в физических пикселях."); }
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public MouseData Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Code, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    private delegate bool EnumWindowProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
