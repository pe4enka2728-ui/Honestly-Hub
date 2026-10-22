using System.Drawing;

namespace VpnZapretSwitch;

internal sealed record EpicOcrWord(string Text, Rectangle Bounds);
internal sealed record EpicOcrLine(string Text, Rectangle Bounds, EpicOcrWord[]? WordBoxes = null);
internal sealed record EpicFriendView(EpicOcrLine Name, EpicOcrLine Action, EpicFriendStatus? ExistingStatus);
internal static class EpicFriendVision
{
    internal static string Words(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();
    internal static bool IsAddHeader(string text) => Words(text) is "добавить друзей" or "add friends" or "add friend";
    internal static bool IsAddAction(string text) => Words(text) is "добавить в друзья" or "add friend" or "send friend request";
    internal static EpicFriendStatus? Existing(string text) => Words(text) switch
    {
        "друзья" or "вы друзья" or "в друзьях" or "friends" or "already friends" or "remove friend" or "удалить из друзей" => EpicFriendStatus.AlreadyFriends,
        "запрос отправлен" or "заявка отправлена" or "отменить запрос" or "отменить заявку" or "request sent" or "friend request sent" or "cancel request" or "cancel friend request" => EpicFriendStatus.Pending,
        _ => null
    };
    internal static EpicOcrLine? AddHeader(IReadOnlyList<EpicOcrLine> lines, int width) =>
        UniqueNavigation(NavigationLabels(lines).Where(l => l.Bounds.Left > width * .60 && IsAddHeader(l.Text)));
    internal static EpicOcrLine? FriendsHeading(IReadOnlyList<EpicOcrLine> lines, int width) =>
        UniqueNavigation(NavigationLabels(lines).Where(l => l.Bounds.Left > width * .60 && Words(l.Text) is "друзья" or "friends"));
    internal static bool SocialVisible(IReadOnlyList<EpicOcrLine> lines, int width) =>
        NavigationLabels(lines).Any(l => l.Bounds.Left > width * .60 && Words(l.Text) is "панель общения" or "social panel" or "social");
    internal static EpicOcrLine? SocialHeading(IReadOnlyList<EpicOcrLine> lines, int width) =>
        UniqueNavigation(NavigationLabels(lines).Where(l => l.Bounds.Left > width * .60 && Words(l.Text) is "панель общения" or "social panel" or "social"));
    internal static IEnumerable<EpicOcrLine> NavigationLabels(IEnumerable<EpicOcrLine> lines)
    {
        foreach (var line in lines)
        {
            if (IsNavigationLabel(line.Text)) { yield return line; continue; }
            var words = line.WordBoxes ?? line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => new EpicOcrWord(w, line.Bounds)).ToArray();
            for (int start = 0; start < words.Length; start++)
                for (int length = 1; length <= 3 && start + length <= words.Length; length++)
                {
                    string label = string.Join(" ", words.Skip(start).Take(length).Select(w => w.Text));
                    if (!IsNavigationLabel(label) || words.Where((_, index) => index < start || index >= start + length).Any(w => !Decoration(w.Text))) continue;
                    var boxes = words.Skip(start).Take(length).Select(w => w.Bounds).ToArray();
                    yield return new(label, Rectangle.FromLTRB(boxes.Min(b => b.Left), boxes.Min(b => b.Top), boxes.Max(b => b.Right), boxes.Max(b => b.Bottom)));
                }
        }
    }
    private static bool IsNavigationLabel(string value) => IsAddHeader(value) || Words(value) is "друзья" or "friends" or "панель общения" or "social panel" or "social";
    private static bool Decoration(string value) => value.All(c => !char.IsLetter(c)) || value.Length == 1 && value[0] < 128;
    internal static bool SamePlace(Rectangle a, Rectangle b) => a.IntersectsWith(b) &&
        Math.Abs(Center(a).Y - Center(b).Y) < Math.Max(a.Height, b.Height) &&
        Math.Abs(Center(a).X - Center(b).X) < Math.Max(a.Width, b.Width);
    private static EpicOcrLine? UniqueNavigation(IEnumerable<EpicOcrLine> source)
    {
        var found = new List<EpicOcrLine>();
        foreach (var item in source)
            if (!found.Any(old => Words(old.Text) == Words(item.Text) && SamePlace(old.Bounds, item.Bounds))) found.Add(item);
        return found.Count == 1 ? found[0] : null;
    }
    internal static EpicOcrLine? SearchResult(IReadOnlyList<EpicOcrLine> lines, EpicOcrLine header, string nickname)
    {
        int bottom = EpicFriendNameVision.ResultTop(lines,header);
        return Unique(lines.Where(l => l.Bounds.Left >= header.Bounds.Left - header.Bounds.Height * 2 &&
            l.Bounds.Top > bottom && EpicFriendNames.Exact(l.Text, nickname)));
    }
    internal static bool WholeSidebarResult(EpicOcrLine line, EpicOcrLine header, Size frame, double scale, int cropLeft) =>
        line.Bounds.Left > cropLeft + 5*scale && line.Bounds.Left >= header.Bounds.Left - header.Bounds.Height*2 &&
        line.Bounds.Right < frame.Width - 100*scale && line.Bounds.Top > header.Bounds.Bottom + header.Bounds.Height*5 &&
        line.Bounds.Bottom < frame.Height - 5*scale;
    internal static EpicFriendView? Profile(IReadOnlyList<EpicOcrLine> lines, EpicOcrLine header, string nickname)
    {
        int panelLeft = header.Bounds.Left - header.Bounds.Height * 2;
        var candidates = new List<EpicFriendView>();
        foreach (var action in lines.Where(l => l.Bounds.Right < panelLeft && (IsAddAction(l.Text) || Existing(l.Text) is not null)))
        {
            var names = lines.Where(l => l.Bounds.Top < action.Bounds.Top &&
                action.Bounds.Top - l.Bounds.Top < Math.Max(400, action.Bounds.Height * 22) &&
                Math.Abs(Center(l.Bounds).X - Center(action.Bounds).X) < Math.Max(45, action.Bounds.Height * 3) &&
                EpicFriendNames.Exact(l.Text, nickname)).OrderByDescending(l => l.Bounds.Height).ToArray();
            if (names.Length == 0) continue;
            var largest = names.Where(l => l.Bounds.Height == names[0].Bounds.Height).ToArray();
            if (largest.Length == 1) candidates.Add(new(largest[0], action, Existing(action.Text)));
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
    internal static EpicFriendStatus? RowStatus(IReadOnlyList<EpicOcrLine> lines, EpicOcrLine header, string nickname)
    {
        var name = SearchResult(lines, header, nickname);
        if (name is null) return null;
        var statuses = lines.Where(l => !ReferenceEquals(l,name) && l.Bounds.Left >= name.Bounds.Left &&
            l.Bounds.Top >= name.Bounds.Top - 2 && l.Bounds.Bottom <= name.Bounds.Bottom + name.Bounds.Height * 1.5 &&
            Existing(l.Text) is not null).Select(l => Existing(l.Text)!.Value).Distinct().ToArray();
        return statuses.Length == 1 ? statuses[0] : null;
    }
    internal static bool IsSentNotice(string text) => Words(text) is "заявка отправлена" or "запрос отправлен" or
        "запрос на добавление в друзья отправлен" or "friend request sent";
    internal static bool NewSentNotice(IReadOnlyList<EpicOcrLine> before, IReadOnlyList<EpicOcrLine> after) =>
        after.Any(l => IsSentNotice(l.Text) && !before.Any(old => Words(old.Text) == Words(l.Text)));
    internal static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static EpicOcrLine? Unique(IEnumerable<EpicOcrLine> source)
    {
        var found=new List<EpicOcrLine>();
        foreach(var line in source)
            if(!found.Any(old=>EpicFriendNames.Exact(old.Text,line.Text) && SamePlace(old.Bounds,line.Bounds))) found.Add(line);
        return found.Count==1?found[0]:null;
    }
}

internal enum EpicSearchStep { Wait, OpenSocial, OpenAdd, Ready, LoginRequired }
internal sealed class EpicSearchNavigation
{
    private int socialAttempts, addAttempts;
    private TimeSpan lastSocial = TimeSpan.FromSeconds(-10), lastAdd = TimeSpan.FromSeconds(-10);
    internal bool SawSocial { get; private set; }
    internal bool SawFriends { get; private set; }
    internal int SocialAttempts => socialAttempts;
    internal int AddAttempts => addAttempts;
    internal EpicSearchStep Next(bool searchVisible, bool socialVisible, bool friendsVisible, bool mainVisible, bool loginVisible, TimeSpan elapsed)
    {
        SawSocial |= socialVisible || friendsVisible || searchVisible; SawFriends |= friendsVisible;
        if (searchVisible) return EpicSearchStep.Ready;
        if (loginVisible && !socialVisible && !friendsVisible) return EpicSearchStep.LoginRequired;
        if (friendsVisible)
        {
            if (addAttempts < 3 && elapsed - lastAdd >= TimeSpan.FromSeconds(3))
            { lastAdd = elapsed; addAttempts++; return EpicSearchStep.OpenAdd; }
            return EpicSearchStep.Wait;
        }
        // During the async transition, the panel can be blank while the dimmed store
        // remains readable. Never toggle its avatar again after requesting Add friends.
        if (addAttempts > 0 || socialVisible) return EpicSearchStep.Wait;
        if (mainVisible && socialAttempts < 3 && elapsed - lastSocial >= TimeSpan.FromSeconds(5))
        { lastSocial = elapsed; socialAttempts++; return EpicSearchStep.OpenSocial; }
        return EpicSearchStep.Wait;
    }
}
