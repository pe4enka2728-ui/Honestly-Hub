using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VpnZapretSwitch;

internal enum EpicLoginStatus { Unknown, Authenticated, Rejected, Failed }
internal sealed record EpicLoginObservation(EpicLoginStatus Status, string? AccountId = null)
{
    internal static EpicLoginObservation Unknown => new(EpicLoginStatus.Unknown);
    internal bool Matches(string? accountId) => Status == EpicLoginStatus.Authenticated && EpicProfileStore.ValidId(accountId) &&
        string.Equals(AccountId, accountId, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Status.ToString(); // Never put an account identifier in a diagnostic.
}

// Read only the current launcher log. Backups from other sessions cannot establish a login.
// No log lines, tokens, or error response payloads are returned to the UI.
internal sealed class EpicLoginLog(string path)
{
    private readonly object sync = new();
    private DateTime lastWrite;
    private long lastLength = -1;
    private DateTimeOffset lastStart;
    private EpicLoginObservation cached = EpicLoginObservation.Unknown;
    internal EpicLoginObservation Read(DateTimeOffset processStarted)
    {
        lock (sync)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return EpicLoginObservation.Unknown;
                if (lastStart == processStarted && lastLength == file.Length && lastWrite == file.LastWriteTimeUtc) return cached;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Bound allocation even if the launcher is left running for weeks. A lost old
                // event yields Unknown rather than assuming a remembered login is still valid.
                const int maximum = 4 * 1024 * 1024;
                if (stream.Length > maximum) stream.Position = stream.Length - maximum;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = new char[maximum]; int count = reader.ReadBlock(text, 0, text.Length);
                var result = Parse(new string(text, 0, count), processStarted);
                lastStart = processStarted; lastLength = file.Length; lastWrite = file.LastWriteTimeUtc; cached = result;
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return EpicLoginObservation.Unknown; }
        }
    }
    internal static EpicLoginObservation Parse(string text, DateTimeOffset processStarted)
    {
        var result = EpicLoginObservation.Unknown;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 32768 || line.Length < 25 || line[0] != '[') continue;
            int end = line.IndexOf(']');
            if (end < 0 || !DateTimeOffset.TryParseExact(line[1..end], "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) || time < processStarted) continue;
            if (Regex.IsMatch(line, @"\bOnLoginComplete\b", RegexOptions.CultureInvariant))
            {
                var status = Regex.Match(line, @"\bOnLoginComplete\b.*?\b(TRUE|FALSE)\b", RegexOptions.CultureInvariant);
                if (!status.Success) continue;
                if (status.Groups[1].Value == "FALSE")
                    result = new(line.Contains("errors.com.epicgames.account.auth_token.invalid_refresh_token", StringComparison.Ordinal)
                        ? EpicLoginStatus.Rejected : EpicLoginStatus.Failed);
                else
                {
                    var id = Regex.Match(line[(status.Index + status.Length)..], @"\b[0-9a-fA-F]{32}\b", RegexOptions.CultureInvariant);
                    result = id.Success ? new(EpicLoginStatus.Authenticated, id.Value) : EpicLoginObservation.Unknown;
                }
            }
            else if (line.Contains("OnLogoutComplete", StringComparison.Ordinal) ||
                line.Contains("LogOnlineIdentity:", StringComparison.Ordinal) && line.Contains("Logout", StringComparison.OrdinalIgnoreCase))
                result = EpicLoginObservation.Unknown;
        }
        return result;
    }
}
