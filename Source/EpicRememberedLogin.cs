using System.Security.Cryptography;
using System.Text;

namespace VpnZapretSwitch;

// This checks the local RememberMe envelope, not whether Epic still accepts its token.
// The current launcher also writes a 160-byte encrypted placeholder on the login screen.
// A non-empty INI, Enable=True, and the last AccountId do not establish a saved login.
internal static class EpicRememberedLogin
{
    internal static bool CanRestore(byte[]? ini)
    {
        if (ini is not { Length: > 0 and <= EpicProfileStore.MaxIni }) return false;
        byte[]? encoded = null;
        try
        {
            string text = Decode(ini); string? enable = null, data = null;
            bool section = false; int sections = 0, enables = 0, values = 0;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim().Equals("RememberMe", StringComparison.OrdinalIgnoreCase);
                    if (section) sections++;
                    continue;
                }
                if (!section) continue;
                int equals = line.IndexOf('='); if (equals < 0) continue;
                string key = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                if (key.Equals("Enable", StringComparison.OrdinalIgnoreCase)) { enable = value; enables++; }
                if (key.Equals("Data", StringComparison.OrdinalIgnoreCase)) { data = value; values++; }
            }
            if (sections != 1 || enables != 1 || values != 1 || !string.Equals(enable, "True", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(data)) return false;
            encoded = Convert.FromBase64String(data);
            // A conservative lower bound rejects the observed short, logged-out placeholder.
            // Do not infer server authorization or expiry from the size of this opaque envelope.
            return encoded.Length >= 256 && encoded.Length % 16 == 0;
        }
        catch (FormatException) { return false; }
        finally { if (encoded is not null) CryptographicOperations.ZeroMemory(encoded); }
    }

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
