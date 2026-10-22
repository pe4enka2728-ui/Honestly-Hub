namespace VpnZapretSwitch;

public enum VpnState { Disconnected, Starting, Active, Unknown }

public sealed record IncyRuntimeStatus(bool ApplicationRunning, bool CoreRunning, VpnState State, string Detail)
{
    public bool IsRunning => State is VpnState.Active or VpnState.Starting;
}

public sealed record IncyFacts(bool ApplicationRunning, bool ExternalCoreRunning,
    bool PortsReadable, bool ConfigurationKnown, bool OwnedProxyListener,
    bool TunnelRequired, bool TunnelUp, string? Error = null);

public readonly record struct LocalListener(int ProcessId, int Port, bool Local);

public static class IncyStateResolver
{
    public static bool HasOwnedProxyListener(IEnumerable<LocalListener> listeners, ISet<int> processes, ISet<int> proxyPorts) =>
        listeners.Any(row => row.Local && processes.Contains(row.ProcessId) && proxyPorts.Contains(row.Port));

    public static IncyRuntimeStatus Resolve(IncyFacts facts)
    {
        bool core = facts.ExternalCoreRunning || facts.OwnedProxyListener;
        if (core)
        {
            if (facts.TunnelRequired && !facts.TunnelUp)
                return new(facts.ApplicationRunning, true, VpnState.Starting, "Ядро запущено · ожидается TUN-адаптер");
            return new(facts.ApplicationRunning, true, VpnState.Active,
                facts.TunnelRequired ? "Приложение открыто · TUN активен" : "Ядро INCY активно · локальный прокси");
        }
        if (!facts.ApplicationRunning)
            return new(false, false, VpnState.Disconnected, "Приложение закрыто");
        if (!facts.PortsReadable || !facts.ConfigurationKnown)
            return new(true, false, VpnState.Unknown,
                facts.Error ?? "Приложение открыто · VPN не удалось проверить");
        return new(true, false, VpnState.Disconnected, "Приложение открыто · VPN отключён");
    }
}

public sealed record ZapretRuntimeStatus(bool? Running, bool ServiceInstalled, string StateText, string Detail)
{
    public bool? ZapretService { get; init; }
    public bool? WinDivertService { get; init; }
    public bool? BypassProcess { get; init; }
}
public sealed record RuntimeStatus(IncyRuntimeStatus Incy, ZapretRuntimeStatus Zapret);
