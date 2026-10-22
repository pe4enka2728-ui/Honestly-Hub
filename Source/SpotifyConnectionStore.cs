using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VpnZapretSwitch;

internal sealed record SpotifyCredentials(string ClientId = "", string RefreshToken = "", string DeviceId = "")
{ public override string ToString() => "Spotify credentials (protected)"; }
internal sealed record SpotifyConnectionInfo(string ClientId, bool Linked, string DeviceId, string Note);

internal sealed class SpotifyConnectionStore(string? path = null)
{
    internal string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonestlyTUN", "spotify.dat");
    internal SpotifyCredentials Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 65536) throw new IOException("Повреждены настройки Spotify.");
        byte[] plain = ProtectedBytes.Transform(File.ReadAllBytes(FilePath), false);
        try
        {
            var data = JsonSerializer.Deserialize<SpotifyCredentials>(plain) ?? throw new IOException("Повреждены настройки Spotify.");
            Validate(data);
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal void Save(SpotifyCredentials data)
    {
        Validate(data);
        string parent = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(parent);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data);
        string temporary = Path.Combine(parent, ".spotify-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, ProtectedBytes.Transform(plain, true));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    private static void Validate(SpotifyCredentials data)
    {
        if (data.RefreshToken is null || data.DeviceId is null || data.ClientId is null || data.RefreshToken.Length > 8192 || data.DeviceId.Length > 256 ||
            data.RefreshToken.Any(char.IsControl) || data.DeviceId.Any(char.IsControl) || (data.RefreshToken.Length > 0 && data.ClientId.Length == 0))
            throw new IOException("Повреждены настройки Spotify.");
        if (data.ClientId.Length > 0) SpotifyOAuth.ValidateClientId(data.ClientId);
    }
}
