using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MultiAiRobot.Feed
{
    // Windows ships the SQLite engine. All text crosses this boundary as UTF-16.
    internal sealed class Store : IDisposable
    {
        const string Engine = "winsqlite3.dll";
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int size, out IntPtr stmt, IntPtr tail);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] static extern int sqlite3_bind_text16(IntPtr s, int i, string text, int bytes, IntPtr destructor);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr s);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr s);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_changes(IntPtr db);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
        [DllImport(Engine, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr s);
        IntPtr db;

        public Store(string path)
        {
            Check(sqlite3_open16(path, out db));
            Check(sqlite3_busy_timeout(db, 5000));
            Run("CREATE TABLE IF NOT EXISTS tips (mode TEXT, id TEXT, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, bookmaker_id INTEGER, event_name TEXT, selection TEXT, odds REAL, edge_pct REAL, quote_time INTEGER, status TEXT NOT NULL, payload TEXT NOT NULL, event_json TEXT NOT NULL, PRIMARY KEY(mode,id))");
            Run("CREATE TABLE IF NOT EXISTS deliveries (mode TEXT, target TEXT, attempted_at TEXT NOT NULL, valuebet_id TEXT NOT NULL, PRIMARY KEY(mode,target))");
            Run("CREATE INDEX IF NOT EXISTS tips_recent ON tips(last_seen DESC)");
        }

        void Check(int code)
        {
            if (code != 0) throw new InvalidOperationException("SQLite " + code + ": " + Marshal.PtrToStringUni(sqlite3_errmsg16(db)));
        }

        public int Run(string sql, params string[] values) { return Execute(sql, values, null); }

        int Execute(string sql, string[] values, Action<string[]> read)
        {
            IntPtr stmt;
            Check(sqlite3_prepare16_v2(db, sql, -1, out stmt, IntPtr.Zero));
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    string text = values[i] ?? "";
                    Check(sqlite3_bind_text16(stmt, i + 1, text, checked(text.Length * 2), new IntPtr(-1)));
                }
                int code;
                while ((code = sqlite3_step(stmt)) == 100)
                {
                    if (read == null) continue;
                    string[] row = new string[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUni(sqlite3_column_text16(stmt, i)) ?? "";
                    read(row);
                }
                if (code != 101) Check(code);
                return sqlite3_changes(db);
            }
            finally { sqlite3_finalize(stmt); }
        }

        public void Save(Quote q, string now)
        {
            var args = new[] {q.Mode,q.Id,now,q.BookId,q.Tip.eventTitle,q.Tip.outcome,
                q.Tip.odds.ToString("R", CultureInfo.InvariantCulture),q.Tip.percent.ToString(CultureInfo.InvariantCulture),
                q.Version.ToString(CultureInfo.InvariantCulture),q.Status,q.Json,q.EventJson};
            Run("INSERT INTO tips(mode,id,first_seen,last_seen,bookmaker_id,event_name,selection,odds,edge_pct,quote_time,status,payload,event_json) VALUES(?1,?2,?3,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12) ON CONFLICT(mode,id) DO UPDATE SET last_seen=excluded.last_seen,bookmaker_id=excluded.bookmaker_id,event_name=excluded.event_name,selection=excluded.selection,odds=excluded.odds,edge_pct=excluded.edge_pct,quote_time=excluded.quote_time,status=excluded.status,payload=excluded.payload,event_json=excluded.event_json WHERE excluded.quote_time >= tips.quote_time", args);
        }

        public bool Claim(Quote q, string now)
        {
            // A stale response cannot overwrite or deliver an already newer version.
            if (!q.CanDeliver) return false;
            return Run("INSERT OR IGNORE INTO deliveries(mode,target,attempted_at,valuebet_id) SELECT ?1,?2,?3,?4 WHERE EXISTS(SELECT 1 FROM tips WHERE mode=?1 AND id=?4 AND quote_time=CAST(?5 AS INTEGER) AND status=?6)",
                q.Mode,q.Target,now,q.Id,q.Version.ToString(CultureInfo.InvariantCulture),q.Status) == 1;
        }

        public void History(Action<string[]> read)
        {
            Execute("SELECT last_seen,mode,id,event_name,selection,odds,edge_pct,status FROM tips ORDER BY last_seen DESC,id LIMIT 50", new string[0],read);
        }

        public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; } }
    }
}
