using System.Text.Json;

namespace VpnZapretSwitch;

public sealed class Settings
{
    public string IncyExe { get; set; } = @"C:\Program Files\INCY\incy.exe";
    public string ZapretFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "zapret");
    public string StrategyFile { get; set; } = "general (ALT).bat";
    public string ServiceName { get; set; } = "zapret";
    public int TimeoutSeconds { get; set; } = 45;
    public string ZapretMode { get; set; } = "bat";
    public string VpnMode { get; set; } = "embedded";
    public bool VerifyInternet { get; set; } = true;
    public string DnsProvider { get; set; } = "cloudflare";

    public string CoreExe => Path.Combine(Path.GetDirectoryName(IncyExe)!, "app", "resources", "bin", "xray.exe");
    public string WinwsExe => Path.Combine(ZapretFolder, "bin", "winws.exe");
    public string StrategyPath => Path.Combine(ZapretFolder, StrategyFile);
    public string ServiceScript => Path.Combine(ZapretFolder, "service.bat");

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))
          ?? throw new InvalidOperationException("settings.json пуст.")
        : new Settings();

    public void Save()
    {
        // Derived paths are deliberately excluded: there are no VPN credentials here.
        var data = new { IncyExe, ZapretFolder, StrategyFile, ServiceName, TimeoutSeconds, ZapretMode, VpnMode, VerifyInternet, DnsProvider };
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
