namespace VpnZapretSwitch;

internal sealed class EpicFriendHistoryPanel : AstraPanel
{
    private readonly Label title=Astra.Label("Добавлены со всех аккаунтов",12,style:FontStyle.Bold);
    private readonly Label hint=Astra.Label("Через 48 часов после полного обхода ник станет зелёным.",9,Astra.Muted);
    private readonly Label empty=Astra.Label("Здесь появятся ники с подтверждённым добавлением со всех сохранённых аккаунтов.",10,Astra.Muted);
    private readonly AstraCanvas body=new(){BackColor=Astra.Surface};
    private readonly AstraScrollHost scroll;
    private readonly Dictionary<string,EpicTrackedFriendCard> rows=[];
    private string[] accounts=[];
    private bool busy;
    internal event Action<string>? DeleteRequested;
    internal int PreferredHeight => 84+96*Math.Clamp(rows.Count,1,4);
    internal EpicFriendHistoryPanel()
    {
        Radius=12;Size=new(700,180);
        title.AutoSize=hint.AutoSize=empty.AutoSize=false;
        body.Controls.Add(empty);scroll=new(body,Astra.Surface,alwaysVisible:false){Dock=DockStyle.None};
        Controls.Add(title);Controls.Add(hint);Controls.Add(scroll);
        Resize+=(_,_)=>Arrange();body.Resize+=(_,_)=>ArrangeRows();Arrange();
    }
    internal void Update(EpicFriendHistory history,string[] currentAccounts,DateTimeOffset now)
    {
        accounts=currentAccounts;
        foreach(string key in rows.Keys.Except(history.Friends.Select(f=>f.Key)).ToArray())
        {var row=rows[key];body.Controls.Remove(row);row.Dispose();rows.Remove(key);}
        int index=0;
        foreach(var entry in history.Friends.OrderByDescending(f=>f.StartedAt))
        {
            if(!rows.TryGetValue(entry.Key,out var row))
            {row=new();string key=entry.Key;row.DeleteRequested+=()=>DeleteRequested?.Invoke(key);rows.Add(key,row);body.Controls.Add(row);}
            row.TabIndex=index++;row.Update(entry,accounts,now);row.SetBusy(busy);
        }
        hint.Text="Через 48 часов после полного обхода ник станет зелёным.";hint.ForeColor=Astra.Muted;
        empty.Text="Здесь появятся ники с подтверждённым добавлением со всех сохранённых аккаунтов.";
        empty.Visible=rows.Count==0;Arrange();
    }
    internal void SetError()
    {
        foreach(var row in rows.Values){body.Controls.Remove(row);row.Dispose();}rows.Clear();
        hint.Text="Не удалось прочитать сохранённый список ников.";hint.ForeColor=Astra.Red;
        empty.Text="Список не перезаписан. Проверьте доступ к хранилищу.";empty.Visible=true;Arrange();
    }
    internal void Tick(DateTimeOffset now)
    {foreach(var row in rows.Values) row.UpdateTime(accounts,now);}
    internal void SetBusy(bool value)
    {busy=value;foreach(var row in rows.Values) row.SetBusy(value);}
    private void Arrange()
    {
        if(scroll is null) return;
        int D(int n)=>(int)Math.Round(n*DeviceDpi/96d);
        title.SetBounds(D(16),D(12),Math.Max(1,Width-D(32)),D(26));
        hint.SetBounds(D(16),D(40),Math.Max(1,Width-D(32)),D(24));
        scroll.SetBounds(D(12),D(72),Math.Max(1,Width-D(24)),Math.Max(1,Height-D(84)));ArrangeRows();
    }
    private void ArrangeRows()
    {
        int D(int n)=>(int)Math.Round(n*DeviceDpi/96d);
        foreach(var row in rows.Values) row.SetBounds(0,D(row.TabIndex*96),Math.Max(1,body.ClientSize.Width),D(86));
        empty.SetBounds(D(6),D(8),Math.Max(1,body.ClientSize.Width-D(12)),D(64));
        body.MinimumSize=new(0,D(Math.Max(80,rows.Count*96)));
    }
}

internal sealed class EpicTrackedFriendCard : AstraPanel
{
    private readonly Label nickname=Astra.Label("",12,style:FontStyle.Bold);
    private readonly Label countdown=Astra.Label("",10);
    private readonly Label detail=Astra.Label("",9,Astra.Muted);
    private readonly AstraButton remove=new(){Text="Удалить",Kind=ButtonKind.Quiet};
    private EpicTrackedFriend? entry;
    internal event Action? DeleteRequested;
    internal EpicTrackedFriendCard()
    {
        Radius=10;Fill=Astra.Input;Size=new(650,86);
        nickname.AutoSize=countdown.AutoSize=detail.AutoSize=false;nickname.AutoEllipsis=true;
        Controls.Add(nickname);Controls.Add(countdown);Controls.Add(detail);Controls.Add(remove);
        remove.Click+=(_,_)=>DeleteRequested?.Invoke();Resize+=(_,_)=>Arrange();Arrange();
    }
    internal void Update(EpicTrackedFriend friend,string[] accounts,DateTimeOffset now)
    {entry=friend;nickname.Text=friend.Nickname;remove.AccessibleName="Удалить ник "+friend.Nickname+" из списка таймеров";UpdateTime(accounts,now);}
    internal void UpdateTime(string[] accounts,DateTimeOffset now)
    {
        if(entry is null) return;
        bool covered=entry.Covers(accounts),ready=entry.IsReady(accounts,now);
        Fill=ready?Astra.ActiveSurface:Astra.Input;nickname.ForeColor=ready?Astra.Green:Astra.Text;
        countdown.Text=accounts.Length==0 ? "Нет сохранённых аккаунтов" : !covered ? "Нужен полный обход: "+accounts.Count(id=>entry.AccountIds.Contains(id,StringComparer.OrdinalIgnoreCase))+" из "+accounts.Length+" аккаунтов" :
            ready?"48 часов прошли":"Осталось: "+entry.Countdown(now);
        countdown.ForeColor=ready?Astra.Green:covered?Astra.Accent:Astra.Amber;
        detail.Text="Аккаунтов: "+entry.AccountIds.Length+"  ·  Отсчёт с "+entry.StartedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        foreach(var label in new[]{nickname,countdown,detail}) label.BackColor=Fill;
        string accessible="Ник "+entry.Nickname+". "+countdown.Text;
        if(AccessibleName!=accessible) AccessibleName=accessible;
        Invalidate(invalidateChildren:true);
    }
    internal void SetBusy(bool value)=>remove.Enabled=!value;
    private void Arrange()
    {
        int D(int n)=>(int)Math.Round(n*DeviceDpi/96d);
        int width=Math.Max(1,Width-D(140));
        nickname.SetBounds(D(14),D(7),width,D(26));countdown.SetBounds(D(14),D(34),width,D(23));detail.SetBounds(D(14),D(59),width,D(20));
        remove.SetBounds(Math.Max(D(14),Width-D(114)),D(27),D(100),D(32));
    }
}
