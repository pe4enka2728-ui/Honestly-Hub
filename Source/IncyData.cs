using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal sealed record IncyServer(string Id, string Name, string Description, string Subscription,
    int? Latency, bool Selected)
{
    public override string ToString() => Name;
}
internal sealed record IncyDisplayData(IReadOnlyList<IncyServer> Servers, string Upload, string Elapsed,
    string Download, string ServerName, string Note);

internal static class IncyDatabase
{
    // Read-only connection includes the live WAL. Never copy a live .db without its WAL,
    // and never write isActive: SQLDelight's in-process observers would not be notified.
    public static IReadOnlyList<IncyServer> ReadServers()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "incy", "incy.db");
        if (!File.Exists(file)) return [];
        int result = sqlite3_open_v2(file, out var db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY
        try
        {
            if (result != 0) throw new IOException("Не удалось прочитать базу серверов INCY.");
            sqlite3_busy_timeout(db, 350);
            const string sql = "SELECT s.id, s.name, COALESCE(s.customDescription,''), " +
                "COALESCE(NULLIF(p.customName,''),p.name,''), s.latency, s.isActive " +
                "FROM servers s LEFT JOIN subscriptions p ON p.id=s.subscriptionId " +
                "ORDER BY COALESCE(p.orderIndex,0), p.id, s.orderIndex, s.createdAt LIMIT 2000";
            if (sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero) != 0)
                throw new IOException("Формат базы серверов INCY изменился. Требуется обновление интеграции.");
            try
            {
                var servers = new List<IncyServer>();
                int step;
                while ((step = sqlite3_step(statement)) == 100)
                    servers.Add(new(Text(statement, 0), Text(statement, 1), Text(statement, 2), Text(statement, 3),
                        sqlite3_column_type(statement, 4) == 5 ? null : sqlite3_column_int(statement, 4),
                        sqlite3_column_int(statement, 5) != 0));
                if (step != 101) throw new IOException("База INCY занята. Повторите обновление списка.");
                return servers;
            }
            finally { sqlite3_finalize(statement); }
        }
        finally { if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    private static string Text(IntPtr statement, int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
}
