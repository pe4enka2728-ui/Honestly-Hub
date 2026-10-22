using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VpnZapretSwitch;

internal sealed class IncyProbe(Settings settings, Func<string, int[]> findProcesses)
{
    public IncyRuntimeStatus Read()
    {
        int[] applications = findProcesses(settings.IncyExe);
        bool externalCore = findProcesses(settings.CoreExe).Length > 0;
        if (applications.Length == 0 && !externalCore)
            return IncyStateResolver.Resolve(new(false, false, true, true, false, false, false));
        var config = ReadConfiguration();
        bool ownedProxy = false, readable = true;
        string? error = null;
        try
        {
            var pids = applications.ToHashSet();
            ownedProxy = IncyStateResolver.HasOwnedProxyListener(NativeTcpListeners.Read(), pids, config.ProxyPorts);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            readable = false;
            error = "Приложение открыто · нет доступа к сетевому статусу";
        }
        bool tunnelRequired = !string.IsNullOrWhiteSpace(config.TunnelName);
        bool tunnelUp = tunnelRequired && NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            string.Equals(n.Name, config.TunnelName, StringComparison.OrdinalIgnoreCase)
            && n.OperationalStatus == OperationalStatus.Up);
        return IncyStateResolver.Resolve(new(applications.Length > 0, externalCore, readable,
            config.Known, ownedProxy, tunnelRequired, tunnelUp, error));
    }

    private static IncyConfiguration ReadConfiguration()
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "incy", "cache");
        // Read only inbound metadata. Outbound servers and VPN keys are never logged or copied.
        var candidates = new[] { Path.Combine(cache, "xray_config.json"), Path.Combine(cache, "xray-config.json") }
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);
        foreach (string path in candidates)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != JsonValueKind.Array) continue;
                var ports = new HashSet<int>();
                string? tunnel = null;
                foreach (var inbound in inbounds.EnumerateArray())
                {
                    string? protocol = inbound.TryGetProperty("protocol", out var property) ? property.GetString() : null;
                    if (protocol is "socks" or "http" or "mixed"
                        && inbound.TryGetProperty("port", out var portProperty)
                        && int.TryParse(portProperty.ToString(), out int port) && port is > 0 and <= 65535)
                        ports.Add(port);
                    if (protocol == "tun" && inbound.TryGetProperty("settings", out var tunSettings)
                        && tunSettings.TryGetProperty("name", out var name)) tunnel = name.GetString();
                }
                return new(ports.Count > 0, ports, tunnel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { /* Another INCY instance may be replacing the configuration; try the other known name. */ }
        }
        return new(false, [], null);
    }
    private sealed record IncyConfiguration(bool Known, HashSet<int> ProxyPorts, string? TunnelName);
}

internal static class NativeTcpListeners
{
    public static IEnumerable<LocalListener> Read() => ReadFamily(2, 24, 8, 20).Concat(ReadFamily(23, 56, 20, 52));

    private static List<LocalListener> ReadFamily(int family, int rowSize, int portOffset, int pidOffset)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (result is 50 or 232 or 10047) return []; // IPv6 may be unavailable on a machine.
        if (result != 0 && result != 122) throw new Win32Exception((int)result);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int allocated = Math.Max(size, 4);
            IntPtr buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                int count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (allocated - 4) / rowSize)
                    throw new InvalidOperationException("Некорректный размер таблицы локальных портов.");
                var rows = new List<LocalListener>(count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + i * rowSize;
                    uint rawPort = unchecked((uint)Marshal.ReadInt32(row, portOffset));
                    int port = (int)(((rawPort & 255) << 8) | ((rawPort >> 8) & 255));
                    int pid = Marshal.ReadInt32(row, pidOffset);
                    byte[] bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(row + (family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var address = new IPAddress(bytes);
                    bool local = IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
                    rows.Add(new(pid, port, local));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Таблица портов меняется. Повторите проверку.");
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool ordered, int family, int tableClass, uint reserved);
}
