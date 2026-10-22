namespace VpnZapretSwitch;

public interface ISwitchPlatform
{
    void Validate();
    bool IsVpnRunning();
    bool IsZapretRunning();
    Task StopZapretAsync();
    Task StartZapretAsync();
    Task ConnectVpnAsync();
    Task DisconnectVpnAsync();
}

public sealed class SwitchController(ISwitchPlatform platform, Action<string> report)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> ToggleAsync(bool returnToZapret = false)
    {
        if (!await gate.WaitAsync(0))
            throw new InvalidOperationException("Переключение уже выполняется.");
        try
        {
            platform.Validate(); // Validate the restart configuration BEFORE stopping anything.
            if (returnToZapret || platform.IsVpnRunning())
            {
                report("Отключение VPN…");
                await platform.DisconnectVpnAsync();
                // A failed disconnect must never be followed by starting Zapret.
                report("Запуск Zapret…");
                await platform.StartZapretAsync();
                report("Zapret запущен, VPN остановлен.");
                return false;
            }

            bool restoreZapret = platform.IsZapretRunning();
            bool connectAttempted = false;
            try
            {
                report("Остановка Zapret…");
                await platform.StopZapretAsync();
                report("Подключение VPN…");
                connectAttempted = true;
                await platform.ConnectVpnAsync();
            }
            catch (Exception failure)
            {
                // A connect command may still be in progress after a timeout.
                // Explicitly cancel it and observe the core stopping before any rollback.
                try
                {
                    if (connectAttempted)
                    {
                        report("Ошибка переключения. Отключение VPN перед восстановлением…");
                        await platform.DisconnectVpnAsync();
                    }
                }
                catch (Exception disconnectFailure)
                {
                    throw new InvalidOperationException(
                        $"{failure.Message}\nНе удалось подтвердить отключение VPN: {disconnectFailure.Message}\n" +
                        "Автоматическое восстановление Zapret отменено. Проверьте состояние VPN.", failure);
                }
                if (restoreZapret)
                {
                    try
                    {
                        report("Восстановление Zapret…");
                        await platform.StartZapretAsync();
                    }
                    catch (Exception restoreFailure)
                    {
                        throw new InvalidOperationException(
                            $"{failure.Message}\nНе удалось восстановить Zapret: {restoreFailure.Message}", failure);
                    }
                }
                throw new InvalidOperationException(failure.Message +
                    (restoreZapret ? "\nZapret восстановлен." : "\nVPN отключён. Zapret до переключения не работал."), failure);
            }
            report("VPN подключён, Zapret остановлен.");
            return true;
        }
        finally { gate.Release(); }
    }
}
