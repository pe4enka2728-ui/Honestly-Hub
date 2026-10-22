namespace VpnZapretSwitch;

internal enum EpicFriendStatus { Sent, AlreadyFriends, Pending, NotFound, Skipped, Failed, Uncertain, Cancelled }
internal sealed record EpicFriendAttempt(EpicFriendStatus Status, string Detail, bool StopBatch = false);
internal sealed record EpicFriendItem(string ProfileName, EpicFriendStatus Status, string Detail);
internal sealed record EpicFriendProgress(int Index, int Total, string ProfileName, string Detail);
internal sealed record EpicFriendBatchResult(EpicFriendItem[] Items, bool Restored, bool Cancelled, string HistoryNote="", string RestoreNote="")
{
    internal string Summary => "Отправлено: " + Items.Count(item => item.Status == EpicFriendStatus.Sent) +
        ". Уже друзья/заявка есть: " + Items.Count(item => item.Status is EpicFriendStatus.AlreadyFriends or EpicFriendStatus.Pending) +
        ". Остальные: " + Items.Count(item => item.Status is not (EpicFriendStatus.Sent or EpicFriendStatus.AlreadyFriends or EpicFriendStatus.Pending)) +
        ". " + (Restored ? "Исходный аккаунт и состояние лаунчера восстановлены." : RestoreNote.Length > 0 ? RestoreNote : "Не удалось вернуть исходное состояние. Нажмите «Восстановить Epic».") +
        (Cancelled ? " Обработка отменена." : "") + HistoryNote;
}
internal interface IEpicFriends
{
    Task CheckAvailableAsync(CancellationToken cancellation);
    Task<string> CheckSearchAsync(string launcher, CancellationToken cancellation);
    Task<string> CheckStatusAsync(string launcher, string nickname, CancellationToken cancellation);
    Task<EpicFriendAttempt> SendAsync(string launcher, string accountId, string nickname, CancellationToken cancellation);
}
internal static class EpicFriendNames
{
    internal static string Validate(string nickname)
    {
        nickname = nickname.Trim();
        if (nickname.Length is < 1 or > 64 || nickname.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new InvalidOperationException("Введите точный ник Epic: от 1 до 64 символов без переносов строк и скрытых знаков.");
        return nickname;
    }
    internal static bool Exact(string actual, string expected) => actual.Trim().Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
}
