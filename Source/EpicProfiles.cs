using System.Security.Cryptography;
using System.Text.Json;

namespace VpnZapretSwitch;

internal sealed record EpicLocalState(byte[]? EditorIni, byte[]? LegacyIni, string? AccountId)
{
    internal byte[]? ActiveIni => EditorIni ?? LegacyIni;
    internal string Variant => EditorIni is not null ? "WindowsEditor" : "Windows";
    public override string ToString() => "Epic local state (protected)";
    internal bool SameAs(EpicLocalState other) => AccountId == other.AccountId && Equal(EditorIni, other.EditorIni) && Equal(LegacyIni, other.LegacyIni);
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
internal sealed record EpicProfile(string Key, string AccountId, string Name, DateTimeOffset SavedAt, string Variant, byte[] Ini)
{
    internal bool HasRememberedLogin => EpicRememberedLogin.CanRestore(Ini);
    public override string ToString() => Name;
}
internal sealed record EpicLibrary(int Version, string LauncherPath, bool AutoStart, EpicProfile[] Profiles)
{
    internal static EpicLibrary Empty => new(1, "", true, []);
    public override string ToString() => "Epic library (protected)";
}
internal sealed record EpicRecovery(int Version, EpicLocalState State, string LauncherPath)
{ public override string ToString() => "Epic recovery (protected)"; }

internal sealed partial class EpicProfileStore
{
    internal const int MaxIni = 2 * 1024 * 1024, MaxData = 32 * 1024 * 1024, MaxProfiles = 128;
    internal string Root { get; }
    internal string LibraryPath => Path.Combine(Root, "profiles.dat");
    internal string RecoveryPath => Path.Combine(Root, "recovery.dat");
    internal bool NeedsRecovery => File.Exists(RecoveryPath);
    internal EpicProfileStore(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestlyTUN", "epic"));
    internal IDisposable Lock()
    {
        Directory.CreateDirectory(Root);
        try { return new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Профили Epic уже изменяются в другом окне Honestly TUN. Дождитесь завершения."); }
    }
    internal EpicLibrary Load()
    {
        if (!File.Exists(LibraryPath)) return EpicLibrary.Empty;
        var library = ReadProtected<EpicLibrary>(LibraryPath); Validate(library); return library;
    }
    internal void Save(EpicLibrary library) { Validate(library); WriteProtected(LibraryPath, library); }
    internal void SaveRecovery(EpicRecovery recovery)
    {
        ValidateState(recovery.State); WriteProtected(RecoveryPath, recovery);
    }
    internal EpicRecovery ReadRecovery()
    {
        var recovery = ReadProtected<EpicRecovery>(RecoveryPath);
        if (recovery.Version != 1 || recovery.State is null || recovery.LauncherPath is null) throw new InvalidDataException("Неверный файл восстановления Epic.");
        ValidateState(recovery.State); return recovery;
    }
    internal void ClearRecovery() { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); }
    internal static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new InvalidDataException("Имя профиля: от 1 до 64 символов без переносов строк.");
        return name;
    }
    internal static bool ValidId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);
    internal static void ValidateState(EpicLocalState state)
    {
        if (state.EditorIni?.Length > MaxIni || state.LegacyIni?.Length > MaxIni || state.AccountId?.Length > 256)
            throw new InvalidDataException("Локальные данные Epic слишком велики или имеют неверный формат.");
    }
    internal static void Validate(EpicLibrary library)
    {
        if (library is null || library.Version != 1 || library.Profiles is null || library.Profiles.Length > MaxProfiles || library.LauncherPath is null || library.LauncherPath.Length > 2048)
            throw new InvalidDataException("Повреждено хранилище профилей Epic.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in library.Profiles)
        {
            if (profile is null || !Guid.TryParseExact(profile.Key, "N", out _) || !keys.Add(profile.Key) || !ValidId(profile.AccountId) ||
                !ids.Add(profile.AccountId) || profile.Ini is not { Length: > 0 and <= MaxIni } || profile.Variant is not ("WindowsEditor" or "Windows"))
                throw new InvalidDataException("Повреждён профиль Epic.");
            if (profile.Name is null || CleanName(profile.Name) != profile.Name) throw new InvalidDataException("Неверное имя профиля Epic.");
        }
    }
    internal static EpicProfile FromState(EpicLocalState state, string name, string? key = null)
    {
        ValidateState(state);
        if (!ValidId(state.AccountId) || !EpicRememberedLogin.CanRestore(state.ActiveIni))
            throw new InvalidOperationException("Epic не сохранил полноценные данные входа. Войдите в лаунчер с опцией «Запомнить меня», дождитесь открытия библиотеки и нажмите «Сохранить текущий». Прежний профиль не заменён.");
        return new(key ?? Guid.NewGuid().ToString("N"), state.AccountId!.ToLowerInvariant(), CleanName(name), DateTimeOffset.UtcNow, state.Variant, state.ActiveIni!.ToArray());
    }
    internal static EpicLibrary Upsert(EpicLibrary library, EpicProfile profile)
    {
        var existing = library.Profiles.FirstOrDefault(item => item.AccountId.Equals(profile.AccountId, StringComparison.OrdinalIgnoreCase));
        if (existing?.HasRememberedLogin == true && !profile.HasRememberedLogin)
            throw new InvalidOperationException("Неполные данные входа из копии не могут заменить сохранённый профиль Epic. Рабочий профиль сохранён.");
        var next = library.Profiles.Where(item => item.AccountId != existing?.AccountId).Append(profile with { Key = existing?.Key ?? profile.Key }).ToArray();
        var result = library with { Profiles = next }; Validate(result); return result;
    }
    internal static EpicLocalState Target(EpicLocalState original, EpicProfile? profile)
    {
        if (profile is null) return new(null, null, "");
        bool editor = original.EditorIni is not null || original.LegacyIni is null && profile.Variant == "WindowsEditor";
        return new(editor ? profile.Ini.ToArray() : null, editor ? null : profile.Ini.ToArray(), profile.AccountId);
    }
    internal void Export(string destination)
    {
        Validate(Load());
        if (!File.Exists(LibraryPath)) throw new InvalidOperationException("Сначала сохраните профиль Epic.");
        if (Path.GetFullPath(destination).Equals(LibraryPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите отдельный файл резервной копии.");
        AtomicWrite(destination, File.ReadAllBytes(LibraryPath));
    }
    internal EpicLibrary ReadBackup(string path) { var library = ReadProtected<EpicLibrary>(path); Validate(library); return library; }
    internal static EpicProfile[] ReadTcNo(string folder)
    {
        folder = Path.GetFullPath(folder);
        string idsPath = Path.Combine(folder, "ids.json");
        var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadBounded(idsPath, 1024 * 1024))
            ?? throw new InvalidDataException("Не найден список аккаунтов TcNo (ids.json).");
        if (ids.Count is < 1 or > MaxProfiles) throw new InvalidDataException("Недопустимое число профилей TcNo.");
        var profiles = new List<EpicProfile>();
        const string registryKey = "REG:HKCU\\Software\\Epic Games\\Unreal Engine\\Identifiers:AccountId";
        foreach (var pair in ids)
        {
            string name = CleanName(pair.Value ?? "");
            if (!ValidId(pair.Key) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                throw new InvalidDataException("Неверное имя или ID профиля TcNo. Импорт не выполнен.");
            string profilePath = Path.GetFullPath(Path.Combine(folder, name));
            if (!profilePath.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь профиля TcNo выходит за выбранную папку.");
            var registry = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadBounded(Path.Combine(profilePath, "reg.json"), 1024 * 1024));
            string? accountId = registry?.FirstOrDefault(entry => entry.Key.Equals(registryKey, StringComparison.OrdinalIgnoreCase)).Value;
            if (!ValidId(accountId) || !pair.Key.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AccountId в профиле TcNo не совпадает со списком. Импорт не выполнен.");
            byte[] ini = ReadBounded(Path.Combine(profilePath, "GameUserSettings.ini"), MaxIni);
            profiles.Add(FromState(new(ini, null, accountId), name));
        }
        return profiles.ToArray();
    }
    private static T ReadProtected<T>(string path)
    {
        byte[] plain;
        try { plain = ProtectedBytes.Transform(ReadBounded(path, MaxData + 65536), false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or CryptographicException)
        { throw new InvalidDataException("Не удалось открыть данные Epic. Нужен тот же пользователь Windows; файл также может быть повреждён."); }
        try
        {
            if (plain.Length > MaxData) throw new InvalidDataException("Хранилище Epic превышает 32 МБ.");
            return JsonSerializer.Deserialize<T>(plain, new JsonSerializerOptions { MaxDepth = 16 }) ?? throw new InvalidDataException("Пустое хранилище Epic.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void WriteProtected<T>(string path, T data)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data);
        try
        {
            if (plain.Length > MaxData) throw new InvalidDataException("Хранилище Epic превышает 32 МБ. Удалите ненужные профили.");
            AtomicWrite(path, ProtectedBytes.Transform(plain, true));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximum) throw new InvalidDataException("Файл Epic превышает допустимый размер.");
        using var data = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (data.Length + count > maximum) throw new InvalidDataException("Файл Epic превышает допустимый размер.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
    internal static void AtomicWrite(string path, byte[] data)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".honestly-epic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { stream.Write(data); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
