using System.IO;
using System.Text.Json;

namespace VpnZapretSwitch;

internal sealed record AppShortcut(string Path, string Name);
internal sealed class AppShortcutStore(string? file = null)
{
    internal string FilePath { get; } = file ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestlyTUN", "applications.json");
    internal static string Normalize(string path)
    {
        string full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!System.IO.Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите исполняемый файл .exe.");
        return full;
    }
    internal List<AppShortcut> Load()
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 1024 * 1024) throw new InvalidDataException("Список приложений слишком большой.");
        var data = JsonSerializer.Deserialize<List<AppShortcut>>(File.ReadAllText(FilePath)) ?? [];
        return data.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => new AppShortcut(Normalize(item.Path), string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileNameWithoutExtension(item.Path) : item.Name))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(256).ToList();
    }
    internal void Save(IEnumerable<AppShortcut> apps)
    {
        var data = apps.Select(item => new AppShortcut(Normalize(item.Path), item.Name))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (data.Length > 256) throw new InvalidDataException("Можно закрепить не более 256 приложений.");
        string parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(parent);
        string temporary = System.IO.Path.Combine(parent, ".applications-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record AppWindow(int ProcessId, nint Handle, string Path, string Title);
internal sealed record AppEntry(string Key, string Path, string Name, bool Running, bool Pinned, nint Window, int ProcessId);
internal static class AppCatalog
{
    // Pure merge: full executable paths identify apps, never just process names.
    internal static AppEntry[] Merge(IReadOnlyList<AppShortcut> pinned, IReadOnlyList<AppWindow> windows, ISet<string> runningPaths)
    {
        var groups = windows.GroupBy(window => string.IsNullOrEmpty(window.Path) ? "pid:" + window.ProcessId : window.Path,
            StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<AppEntry>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in pinned)
        {
            string path = AppShortcutStore.Normalize(app.Path);
            if (!used.Add(path)) continue;
            groups.TryGetValue(path, out var window);
            result.Add(new(path, path, app.Name, runningPaths.Contains(path) || window is not null, true,
                window?.Handle ?? 0, window?.ProcessId ?? 0));
        }
        foreach (var pair in groups.OrderBy(item => item.Value.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!used.Add(pair.Key)) continue;
            var window = pair.Value;
            string name = string.IsNullOrEmpty(window.Path) ? window.Title : System.IO.Path.GetFileNameWithoutExtension(window.Path);
            result.Add(new(pair.Key, window.Path, name, true, false, window.Handle, window.ProcessId));
        }
        return result.ToArray();
    }
}
