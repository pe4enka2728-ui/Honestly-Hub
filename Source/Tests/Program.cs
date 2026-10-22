using VpnZapretSwitch;

int passed = ScrollMotionChecks.Run();
await Check("Запуск VPN после остановки Zapret", async () =>
{
    var fake = new Fake { Zapret = true };
    Assert(await Controller(fake).ToggleAsync(), "Expected VPN mode");
    Assert(fake.Vpn && !fake.Zapret, "Wrong final state");
    AssertCalls(fake, "validate", "vpn?", "zapret?", "stop-zapret", "connect");
});
await Check("Повторное нажатие возвращает Zapret", async () =>
{
    var fake = new Fake { Zapret = true };
    var controller = Controller(fake);
    await controller.ToggleAsync();
    Assert(!await controller.ToggleAsync(), "Expected Zapret mode");
    Assert(!fake.Vpn && fake.Zapret, "Wrong final state");
});
await Check("Состояние читается заново после перезапуска программы", async () =>
{
    var fake = new Fake { Vpn = true };
    Assert(!await Controller(fake).ToggleAsync(), "Expected Zapret mode");
    AssertCalls(fake, "validate", "vpn?", "disconnect", "start-zapret");
});
await Check("Ошибочная настройка ничего не останавливает", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "validate" };
    await Fails(() => Controller(fake).ToggleAsync(), "test:validate");
    AssertCalls(fake, "validate");
    Assert(fake.Zapret, "Zapret was stopped");
});
await Check("Ошибка подключения восстанавливает Zapret", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "connect" };
    await Fails(() => Controller(fake).ToggleAsync(), "Zapret восстановлен");
    Assert(!fake.Vpn && fake.Zapret, "Rollback failed");
    AssertCalls(fake, "validate", "vpn?", "zapret?", "stop-zapret", "connect", "disconnect", "start-zapret");
});
await Check("Ошибка отключения запрещает запуск Zapret", async () =>
{
    var fake = new Fake { Vpn = true, FailOn = "disconnect" };
    await Fails(() => Controller(fake).ToggleAsync(), "test:disconnect");
    Assert(!fake.Calls.Contains("start-zapret"), "Started Zapret with active VPN");
});
await Check("Неудачная очистка после timeout запрещает восстановление", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "connect,disconnect" };
    await Fails(() => Controller(fake).ToggleAsync(), "Автоматическое восстановление Zapret отменено");
    Assert(!fake.Calls.Contains("start-zapret"), "Unsafe rollback");
});
await Check("Ошибка остановки Zapret не включает VPN", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "stop-zapret" };
    await Fails(() => Controller(fake).ToggleAsync(), "Zapret восстановлен");
    Assert(!fake.Calls.Contains("connect") && !fake.Calls.Contains("disconnect"), "Touched VPN unnecessarily");
    Assert(fake.Zapret, "Zapret was not restored");
});
await Check("Изначально выключенный Zapret не включается при ошибке VPN", async () =>
{
    var fake = new Fake { FailOn = "connect" };
    await Fails(() => Controller(fake).ToggleAsync(), "Zapret до переключения не работал");
    Assert(!fake.Zapret, "Unexpected Zapret start");
});
await Check("Ошибка восстановления показывается пользователю", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "connect,start-zapret" };
    await Fails(() => Controller(fake).ToggleAsync(), "Не удалось восстановить Zapret");
});
await Check("Ошибка запуска Zapret не сообщает об успехе", async () =>
{
    var fake = new Fake { Vpn = true, FailOn = "start-zapret" };
    await Fails(() => Controller(fake).ToggleAsync(), "test:start-zapret");
    Assert(!fake.Vpn && !fake.Zapret, "Unexpected state");
});
await Check("Двойное нажатие не создаёт второе переключение", async () =>
{
    var fake = new Fake { Zapret = true, ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var controller = Controller(fake);
    Task<bool> first = controller.ToggleAsync();
    await Fails(() => controller.ToggleAsync(), "Переключение уже выполняется");
    fake.ConnectGate.SetResult(true);
    await first;
    Assert(fake.Calls.Count(x => x == "connect") == 1, "Multiple connections");
});
await Check("После ошибки кнопка снова доступна", async () =>
{
    var fake = new Fake { Zapret = true, FailOn = "connect" };
    var controller = Controller(fake);
    await Fails(() => controller.ToggleAsync(), "Zapret восстановлен");
    fake.FailOn = "";
    Assert(await controller.ToggleAsync(), "Gate was not released");
});
await Check("Возврат Zapret после самостоятельного завершения ядра VPN", async () =>
{
    var fake = new Fake();
    Assert(!await Controller(fake).ToggleAsync(returnToZapret: true), "Expected Zapret mode");
    Assert(fake.Zapret && !fake.Vpn, "Zapret was not restored");
});
await Check("Повторная попытка запуска Zapret после ошибки", async () =>
{
    var fake = new Fake { Vpn = true, FailOn = "start-zapret" };
    var controller = Controller(fake);
    await Fails(() => controller.ToggleAsync(), "test:start-zapret");
    fake.FailOn = "";
    await controller.ToggleAsync(returnToZapret: true);
    Assert(fake.Zapret && !fake.Vpn, "Retry connected VPN instead of starting Zapret");
});
await Check("Встроенное ядро INCY распознаётся без xray.exe", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, true, true, true, true));
    Assert(status.State == VpnState.Active && status.CoreRunning && status.ApplicationRunning, "Embedded core was missed");
    return Task.CompletedTask;
});
await Check("Открытое приложение отличается от подключённого VPN", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, true, false, false, false));
    Assert(status.State == VpnState.Disconnected && status.ApplicationRunning, "App state was lost");
    return Task.CompletedTask;
});
await Check("Задержка готовности TUN не считается завершённым подключением", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, true, true, true, false));
    Assert(status.State == VpnState.Starting && status.IsRunning, "TUN readiness was ignored");
    return Task.CompletedTask;
});
await Check("Системный прокси работает без TUN", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, true, true, false, false));
    Assert(status.State == VpnState.Active, "Proxy-only mode was missed");
    return Task.CompletedTask;
});
await Check("Ошибка чтения портов не означает выключенный VPN", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, false, true, false, true, true));
    Assert(status.State == VpnState.Unknown, "Failed observation was treated as disconnected");
    return Task.CompletedTask;
});
await Check("Неизвестная конфигурация не означает выключенный VPN", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, false, false, false, false));
    Assert(status.State == VpnState.Unknown, "Unknown config was treated as disconnected");
    return Task.CompletedTask;
});
await Check("Оставшийся адаптер без ядра не считается подключением", () =>
{
    var status = IncyStateResolver.Resolve(new(true, false, true, true, false, true, true));
    Assert(status.State == VpnState.Disconnected, "Orphan TUN was considered active");
    return Task.CompletedTask;
});
await Check("Внешнее ядро продолжает распознаваться", () =>
{
    var status = IncyStateResolver.Resolve(new(false, true, true, false, false, false, false));
    Assert(status.State == VpnState.Active, "Legacy process mode regressed");
    return Task.CompletedTask;
});
await Check("Закрытое приложение и остановленное ядро распознаются", () =>
{
    var status = IncyStateResolver.Resolve(new(false, false, false, false, false, false, false));
    Assert(status.State == VpnState.Disconnected && !status.ApplicationRunning, "Closed app not detected");
    return Task.CompletedTask;
});
await Check("Чужой процесс с таким же прокси-портом не считается INCY", () =>
{
    Assert(!IncyStateResolver.HasOwnedProxyListener([new(99, 10808, true)], new HashSet<int> { 42 }, new HashSet<int> { 10808 }), "Other proxy was included");
    return Task.CompletedTask;
});
await Check("Служебный API-порт не считается рабочим прокси", () =>
{
    Assert(!IncyStateResolver.HasOwnedProxyListener([new(42, 10813, true)], new HashSet<int> { 42 }, new HashSet<int> { 10808, 10809 }), "API-only listener was included");
    return Task.CompletedTask;
});
await Check("Локальный прокси-порт принадлежит нужному процессу", () =>
{
    Assert(IncyStateResolver.HasOwnedProxyListener([new(42, 10808, true)], new HashSet<int> { 42 }, new HashSet<int> { 10808 }), "Owned listener was missed");
    return Task.CompletedTask;
});
await Check("Номер стратегии берётся из реального меню, а не сортировки имён", () =>
{
    string menu = "Pick one of the options:\r\n1. general (ALT).bat\r\n12. general (ALT10).bat\r\n7. general.bat\r\nInput file index (number): ";
    Assert(ZapretConsoleProtocol.FindStrategyIndex(menu, "general.bat") == 7, "Hardcoded/assumed menu ordering");
    Assert(ZapretConsoleProtocol.FindStrategyIndex(menu, "general (ALT10).bat") == 12, "Multi-digit index failed");
    return Task.CompletedTask;
});
await Check("Отсутствующая или неоднозначная стратегия не вызывает произвольный пункт", () =>
{
    foreach (string menu in new[] { "1. general.bat\n", "1. general (ALT).bat\n2. general (ALT).bat\n" })
    {
        bool rejected = false;
        try { ZapretConsoleProtocol.FindStrategyIndex(menu, "general (ALT).bat"); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unsafe strategy fallback");
    }
    return Task.CompletedTask;
});
await Check("Управляющие последовательности ConPTY не сливают строки меню", () =>
{
    string text = ZapretConsoleProtocol.StripTerminal("\x1b[2J\x1b[H1. general.bat\x1b[2;1H2. general (ALT).bat\x1b[0m\r\nInput file index (number): ");
    Assert(ZapretConsoleProtocol.FindStrategyIndex(text, "general (ALT).bat") == 2, "ConPTY cursor positioning broke menu parsing");
    return Task.CompletedTask;
});
await Check("IPSet допускает IPv4/IPv6 и отвергает HTML и ошибочные маски", () =>
{
    Assert(ZapretConsoleProtocol.IsValidIpSet(["# list", "203.0.113.0/24", "2001:db8::/32"]), "Valid IPSet rejected");
    foreach (string[] invalid in new[] { new[] { "<html>502</html>" }, new[] { "203.0.113.0/99" }, Array.Empty<string>(), new[] { "# comments only" } })
        Assert(!ZapretConsoleProtocol.IsValidIpSet(invalid), "Invalid IPSet accepted");
    return Task.CompletedTask;
});
Console.WriteLine($"PASS: {passed} tests. No real network or processes were changed.");

SwitchController Controller(Fake fake) => new(fake, _ => { });
async Task Check(string name, Func<Task> action)
{
    await action();
    passed++;
    Console.WriteLine("PASS " + name);
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void AssertCalls(Fake fake, params string[] expected) =>
    Assert(fake.Calls.SequenceEqual(expected), string.Join(", ", fake.Calls));
static async Task Fails(Func<Task<bool>> action, string fragment)
{
    try { await action(); }
    catch (Exception ex)
    {
        Assert(ex.Message.Contains(fragment), ex.Message);
        return;
    }
    throw new Exception("Expected exception: " + fragment);
}

sealed class Fake : ISwitchPlatform
{
    public bool Vpn, Zapret;
    public string FailOn = "";
    public TaskCompletionSource<bool>? ConnectGate;
    public List<string> Calls = [];
    private void Step(string name)
    {
        Calls.Add(name);
        if (FailOn.Split(',').Contains(name)) throw new Exception("test:" + name);
    }
    public void Validate() => Step("validate");
    public bool IsVpnRunning() { Step("vpn?"); return Vpn; }
    public bool IsZapretRunning() { Step("zapret?"); return Zapret; }
    public Task StopZapretAsync() { Step("stop-zapret"); Zapret = false; return Task.CompletedTask; }
    public Task StartZapretAsync() { Step("start-zapret"); Zapret = true; return Task.CompletedTask; }
    public async Task ConnectVpnAsync()
    {
        // Simulate a late/partial launch even when connecting reports failure.
        Vpn = true;
        Step("connect");
        if (ConnectGate is not null) await ConnectGate.Task;
    }
    public Task DisconnectVpnAsync() { Step("disconnect"); Vpn = false; return Task.CompletedTask; }
}
