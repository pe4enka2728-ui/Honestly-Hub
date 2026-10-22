using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VpnZapretSwitch;

public sealed record PetPack(
    string Id,
    string FolderName,
    string Name,
    string Author,
    string DirectoryPath,
    string IdleImagePath,
    string WorkingImagePath,
    string ThinkingImagePath,
    string CelebrateImagePath,
    string SignatureImagePath,
    string SleepImagePath,
    int TotalPoses
);

public static class ThatPetService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly string[] SearchFolders =
    [
        Path.Combine(AppContext.BaseDirectory, "ThatPet"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThatPet"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "Source", "Assets", "ThatPet"),
        Path.Combine(AppContext.BaseDirectory, "..", "Source", "Assets", "ThatPet"),
        Path.Combine(Path.GetDirectoryName(typeof(ThatPetService).Assembly.Location) ?? "", "ThatPet"),
        Path.Combine(Path.GetDirectoryName(typeof(ThatPetService).Assembly.Location) ?? "", "..", "Source", "Assets", "ThatPet"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ThatPet")
    ];

    public static readonly Dictionary<string, string> LockedToUnlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon4"] = "argon",
        ["astra6"] = "astra",
        ["deepseek41"] = "deepseek",
        ["flash38"] = "flash",
        ["grok47"] = "grok",
        ["haiku55"] = "haiku",
        ["opus55"] = "opus",
        ["sol61"] = "sol",
        ["sonnet55"] = "sonnet"
    };

    public static readonly Dictionary<string, string> UnlockedToLocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = "argon4",
        ["astra"] = "astra6",
        ["deepseek"] = "deepseek41",
        ["flash"] = "flash38",
        ["grok"] = "grok47",
        ["haiku"] = "haiku55",
        ["opus"] = "opus55",
        ["sol"] = "sol61",
        ["sonnet"] = "sonnet55"
    };

    public static string AppFolder
    {
        get
        {
            foreach (var folder in SearchFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    string exe = Path.Combine(folder, "thatpet.exe");
                    if (File.Exists(exe)) return Path.GetFullPath(folder);
                }
            }
            return SearchFolders[0];
        }
    }

    public static string ExePath => Path.Combine(AppFolder, "thatpet.exe");
    public static string PacksFolder => Path.Combine(AppFolder, "packs");
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatPet");
    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static bool IsInstalled => File.Exists(ExePath);

    public static bool IsRunning => Process.GetProcessesByName("thatpet").Length > 0;

    public static int? RunningPid
    {
        get
        {
            try
            {
                var procs = Process.GetProcessesByName("thatpet");
                return procs.Length > 0 ? procs[0].Id : null;
            }
            catch { return null; }
        }
    }

    public static string NormalizeCharacterId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "opus";
        if (LockedToUnlocked.TryGetValue(id.Trim(), out var unlocked)) return unlocked;
        return id.Trim();
    }

    public static void EnsurePacksUnlocked()
    {
        if (!Directory.Exists(PacksFolder)) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(PacksFolder))
            {
                string packJson = Path.Combine(dir, "pack.json");
                if (!File.Exists(packJson)) continue;

                string rawJson;
                try { rawJson = File.ReadAllText(packJson, Encoding.UTF8); }
                catch { continue; }

                string currentId = "";
                try
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                        currentId = idProp.GetString() ?? "";
                }
                catch { }

                if (LockedToUnlocked.TryGetValue(currentId, out var newId))
                {
                    try
                    {
                        var patched = System.Text.RegularExpressions.Regex.Replace(
                            rawJson,
                            "\"id\"\\s*:\\s*\"[^\"]+\"",
                            $"\"id\": \"{newId}\"");
                        File.WriteAllText(packJson, patched, Utf8NoBom);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static List<PetPack> LoadPacks()
    {
        EnsurePacksUnlocked();

        var result = new List<PetPack>();
        if (!Directory.Exists(PacksFolder)) return result;

        foreach (var dir in Directory.GetDirectories(PacksFolder))
        {
            string packJson = Path.Combine(dir, "pack.json");
            if (!File.Exists(packJson)) continue;

            string folderName = Path.GetFileName(dir);
            string id = NormalizeCharacterId(folderName);
            string name = folderName;
            string author = "ThatLev";

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packJson, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp) && !string.IsNullOrEmpty(idProp.GetString()))
                    id = idProp.GetString()!;
                if (root.TryGetProperty("name", out var nameProp) && !string.IsNullOrEmpty(nameProp.GetString()))
                    name = nameProp.GetString()!;
                if (root.TryGetProperty("author", out var authorProp) && !string.IsNullOrEmpty(authorProp.GetString()))
                    author = authorProp.GetString()!;
            }
            catch { }

            string posesDir = Path.Combine(dir, "poses");
            string idle = Path.Combine(posesDir, "idle_stand.png");
            string working = Path.Combine(posesDir, "working_1.png");
            string thinking = Path.Combine(posesDir, "thinking_1.png");
            string celebrate = Path.Combine(posesDir, "celebrate_1.png");
            string signature = Path.Combine(posesDir, "idle_signature.png");
            string sleep = Path.Combine(posesDir, "sleep_sit.png");

            int totalPoses = 0;
            if (Directory.Exists(posesDir))
            {
                try
                {
                    var files = Directory.GetFiles(posesDir, "*.png");
                    totalPoses = files.Length;
                    if (!File.Exists(idle) && files.Length > 0) idle = files[0];
                    if (!File.Exists(working)) working = idle;
                    if (!File.Exists(thinking)) thinking = idle;
                    if (!File.Exists(celebrate)) celebrate = idle;
                    if (!File.Exists(signature)) signature = idle;
                    if (!File.Exists(sleep)) sleep = idle;
                }
                catch { }
            }

            result.Add(new PetPack(
                id,
                folderName,
                name,
                author,
                dir,
                idle,
                working,
                thinking,
                celebrate,
                signature,
                sleep,
                totalPoses
            ));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static JsonNode LoadSettingsNode()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonNode.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8)) ?? new JsonObject();
        }
        catch { }
        return new JsonObject();
    }

    public static void SaveSettingsNode(JsonNode node)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(SettingsPath, node.ToJsonString(options), Utf8NoBom);
        }
        catch { }
    }

    public static string GetActiveCharacter()
    {
        var node = LoadSettingsNode();
        if (node["characters"] is JsonArray arr && arr.Count > 0)
        {
            string raw = arr[0]?.GetValue<string>() ?? "opus";
            return NormalizeCharacterId(raw);
        }
        return "opus";
    }

    public static void SetActiveCharacter(string characterId)
    {
        EnsurePacksUnlocked();
        string norm = NormalizeCharacterId(characterId);

        var node = LoadSettingsNode();
        node["characters"] = new JsonArray { norm };
        node["onboarded"] = true;
        SaveSettingsNode(node);

        if (IsRunning) Restart();
    }

    public static double GetSize()
    {
        var node = LoadSettingsNode();
        if (node["size"] is JsonNode val && val.AsValue().TryGetValue<double>(out double s))
            return s;
        return 0.5;
    }

    public static void SetSize(double size)
    {
        var node = LoadSettingsNode();
        node["size"] = size;
        SaveSettingsNode(node);

        if (IsRunning) Restart();
    }

    public static bool GetBoolSetting(string key, bool defaultValue = true)
    {
        var node = LoadSettingsNode();
        if (node[key] is JsonNode val && val.AsValue().TryGetValue<bool>(out bool b))
            return b;
        return defaultValue;
    }

    public static void SetBoolSetting(string key, bool value)
    {
        var node = LoadSettingsNode();
        node[key] = value;
        SaveSettingsNode(node);

        if (IsRunning) Restart();
    }

    public static bool Start()
    {
        if (!IsInstalled) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                WorkingDirectory = AppFolder,
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("thatpet"))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
                catch { }
            }
        }
        catch { }
    }

    public static void Restart()
    {
        Stop();
        for (int i = 0; i < 15; i++)
        {
            if (Process.GetProcessesByName("thatpet").Length == 0) break;
            Thread.Sleep(50);
        }
        Start();
    }

    public static async Task<string> TriggerEventAsync(string eventName)
    {
        if (!IsInstalled) return "ThatPet не установлен";
        return await Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"hook --provider antigravity --event {eventName}",
                    WorkingDirectory = AppFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(2000);
                return "Событие отправлено питомцу";
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
        });
    }

    public static async Task<string> RunCommandAsync(string arguments)
    {
        if (!IsInstalled) return "ThatPet не установлен";
        return await Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = arguments,
                    WorkingDirectory = AppFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                string output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(3000);
                return output.Trim();
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
        });
    }
}
