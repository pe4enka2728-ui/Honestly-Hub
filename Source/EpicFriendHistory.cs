namespace VpnZapretSwitch;

internal sealed record EpicTrackedFriend(string Key, string Nickname, DateTimeOffset StartedAt, string[] AccountIds)
{
    internal DateTimeOffset ReadyAt => StartedAt.ToUniversalTime().AddHours(48);
    internal TimeSpan Remaining(DateTimeOffset now) => ReadyAt > now ? ReadyAt-now : TimeSpan.Zero;
    internal bool Covers(IReadOnlyList<string> accounts) => accounts.Count > 0 && accounts.All(id => AccountIds.Contains(id,StringComparer.OrdinalIgnoreCase));
    internal bool IsReady(IReadOnlyList<string> accounts,DateTimeOffset now) => Covers(accounts) && now >= ReadyAt;
    internal string Countdown(DateTimeOffset now)
    {
        long seconds=(long)Math.Ceiling(Remaining(now).TotalSeconds);
        return $"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}";
    }
}

internal sealed record EpicFriendHistory(int Version, EpicTrackedFriend[] Friends)
{
    internal const int MaxFriends=512;
    internal static EpicFriendHistory Empty => new(1,[]);
    internal static bool Complete(int expected,IReadOnlyList<EpicFriendItem> items) => expected>0 && items.Count==expected &&
        items.All(item => item.Status is EpicFriendStatus.Sent or EpicFriendStatus.Pending or EpicFriendStatus.AlreadyFriends);
    internal static void Validate(EpicFriendHistory history)
    {
        if(history is null || history.Version!=1 || history.Friends is null || history.Friends.Length>MaxFriends) throw new InvalidDataException("Повреждён список добавленных ников Epic.");
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var friend in history.Friends)
        {
            if(friend is null || !Guid.TryParseExact(friend.Key,"N",out _) || !keys.Add(friend.Key) || friend.Nickname is null ||
                EpicFriendNames.Validate(friend.Nickname)!=friend.Nickname || !names.Add(friend.Nickname) ||
                friend.StartedAt.UtcDateTime > DateTime.MaxValue.AddHours(-48) || friend.AccountIds is not {Length:>0 and <=EpicProfileStore.MaxProfiles} ||
                friend.AccountIds.Any(id=>!EpicProfileStore.ValidId(id)) || friend.AccountIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=friend.AccountIds.Length)
                throw new InvalidDataException("Повреждена запись добавленного ника Epic.");
        }
    }
    internal EpicFriendHistory Remember(string nickname,IReadOnlyList<string> accounts,DateTimeOffset now,bool newlySent)
    {
        Validate(this);nickname=EpicFriendNames.Validate(nickname);
        string[] ids=accounts.Select(id=>id.ToLowerInvariant()).Distinct().Order().ToArray();
        var previous=Friends.SingleOrDefault(f=>EpicFriendNames.Exact(f.Nickname,nickname));
        var entry=new EpicTrackedFriend(previous?.Key ?? Guid.NewGuid().ToString("N"),previous?.Nickname ?? nickname,
            previous is null || newlySent || !previous.Covers(ids) ? now.ToUniversalTime() : previous.StartedAt,ids);
        var result=this with {Friends=Friends.Where(f=>f.Key!=entry.Key).Append(entry).ToArray()};Validate(result);return result;
    }
    internal EpicFriendHistory Remove(string key)
    {
        Validate(this);
        if(!Guid.TryParseExact(key,"N",out _)) throw new InvalidOperationException("Неверная запись ника.");
        return this with {Friends=Friends.Where(f=>!f.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).ToArray()};
    }
}

internal sealed partial class EpicProfileStore
{
    internal string FriendHistoryPath => Path.Combine(Root,"friend-history.dat");
    internal EpicFriendHistory LoadFriendHistory()
    {
        if(!File.Exists(FriendHistoryPath)) return EpicFriendHistory.Empty;
        var history=ReadProtected<EpicFriendHistory>(FriendHistoryPath);EpicFriendHistory.Validate(history);return history;
    }
    internal void SaveFriendHistory(EpicFriendHistory history)
    { EpicFriendHistory.Validate(history);WriteProtected(FriendHistoryPath,history); }
}
