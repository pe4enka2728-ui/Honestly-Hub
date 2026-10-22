using System.Net;
using System.Text.RegularExpressions;

namespace VpnZapretSwitch;

internal static class ZapretConsoleProtocol
{
    public static int FindStrategyIndex(string output, string filename)
    {
        var matches = Regex.Matches(output, @"(?m)^\s*(\d+)\.\s+([^\r\n]+\.bat)\s*$")
            .Where(m => string.Equals(m.Groups[2].Value.Trim(), filename, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !int.TryParse(matches[0].Groups[1].Value, out int index) || index < 1)
            throw new InvalidOperationException("В меню service.bat не найден однозначный пункт «" + filename + "». Установка остановлена.");
        return index;
    }

    public static bool IsValidIpSet(IEnumerable<string> lines)
    {
        int count = 0;
        foreach (string source in lines)
        {
            string value = source.Trim().TrimStart('\uFEFF');
            if (value.Length == 0 || value.StartsWith('#')) continue;
            string[] parts = value.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)) return false;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out int prefix) || prefix < 0
                || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))) return false;
            count++;
        }
        return count > 0;
    }

    public static string StripTerminal(string value)
    {
        value = Regex.Replace(value, @"\x1B\[\d*(?:;\d*)?[Hf]|\x1B\[\d*[BEd]", "\n");
        return Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07]*(?:\x07|\x1B\\)", "").Replace("\b", "");
    }
}
