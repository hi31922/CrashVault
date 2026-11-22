using System;
using System.Collections.Generic;
using System.IO;

namespace CrashVault
{
    internal sealed class VaultEvent
    {
        public long RowId, UtcTicks;
        public int EventId, WindowsLevel, Severity;
        public string Channel, Provider, Title, Explanation, Steps, Detail;
    }

    internal sealed class LogPosition
    {
        public long RecordId;
        public long UtcTicks;
    }

    internal sealed class VaultStore : IDisposable
    {
        private const string SummaryFields = "id, utc_ticks, channel, provider, event_id, severity, title";

        private readonly SqliteDatabase database;
        private SqliteStatement insertEvent;

        public VaultStore() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrashVault", "Events.sqlite3"))
        {
        }

        internal VaultStore(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            database = new SqliteDatabase(path);
            try
            {
                database.Execute("PRAGMA journal_mode=WAL");
                database.Execute(@"
                    CREATE TABLE IF NOT EXISTS events (
                        id INTEGER PRIMARY KEY,
                        channel TEXT NOT NULL,
                        record_number INTEGER NOT NULL,
                        utc_ticks INTEGER NOT NULL,
                        provider TEXT NOT NULL,
                        event_id INTEGER NOT NULL,
                        windows_level INTEGER NOT NULL,
                        severity INTEGER NOT NULL,
                        title TEXT NOT NULL,
                        explanation TEXT NOT NULL,
                        steps TEXT NOT NULL,
                        detail TEXT NOT NULL,
                        UNIQUE (channel, record_number, utc_ticks)
                    )");
                database.Execute(@"CREATE INDEX IF NOT EXISTS event_priority_time
                    ON events (severity DESC, utc_ticks DESC, id DESC)");
                database.Execute(@"
                    CREATE TABLE IF NOT EXISTS positions (
                        channel TEXT PRIMARY KEY,
                        record_number INTEGER NOT NULL,
                        utc_ticks INTEGER NOT NULL
                    )");
                database.Execute("CREATE TABLE IF NOT EXISTS status (id INTEGER PRIMARY KEY, message TEXT NOT NULL)");
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        public long GetRevision()
        {
            using (var query = database.Prepare("SELECT COALESCE(MAX(id),0) FROM events"))
            {
                query.Read();
                return query.Number(0);
            }
        }

        public string GetCollectorStatus()
        {
            using (var query = database.Prepare("SELECT message FROM status WHERE id=1"))
                return query.Read() ? query.Text(0) : "Reading Windows event logs…";
        }

        internal void SetCollectorStatus(string message)
        {
            using (var command = database.Prepare("INSERT OR REPLACE INTO status (id,message) VALUES (1,?)"))
            {
                command.Bind(1, message);
                command.Read();
            }
        }

        public List<VaultEvent> ReadEventSummaries(string search, int severity)
        {
            string term = (search ?? "").Trim();
            string sql = "SELECT " + SummaryFields + " FROM events WHERE 1";
            if (severity != 0)
                sql += " AND severity = ?";
            if (term.Length != 0)
            {
                sql += @" AND (title LIKE @search ESCAPE '\' OR provider LIKE @search ESCAPE '\'
                    OR channel LIKE @search ESCAPE '\' OR CAST(event_id AS TEXT) LIKE @search ESCAPE '\'
                    OR detail LIKE @search ESCAPE '\')";
            }
            sql += " ORDER BY severity DESC, utc_ticks DESC, id DESC";

            var entries = new List<VaultEvent>();
            using (var query = database.Prepare(sql))
            {
                int parameter = 1;
                if (severity != 0)
                    query.Bind(parameter++, severity);
                if (term.Length != 0)
                    query.Bind(parameter, "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
                while (query.Read())
                    entries.Add(ReadSummary(query));
            }
            return entries;
        }

        public VaultEvent ReadEvent(long rowId)
        {
            using (var query = database.Prepare("SELECT " + SummaryFields +
                ", windows_level, explanation, steps, detail FROM events WHERE id=?"))
            {
                query.Bind(1, rowId);
                if (!query.Read())
                    return null;

                VaultEvent entry = ReadSummary(query);
                entry.WindowsLevel = (int)query.Number(7);
                entry.Explanation = query.Text(8);
                entry.Steps = query.Text(9);
                entry.Detail = query.Text(10);
                return entry;
            }
        }

        private static VaultEvent ReadSummary(SqliteStatement query)
        {
            return new VaultEvent
            {
                RowId = query.Number(0),
                UtcTicks = query.Number(1),
                Channel = query.Text(2),
                Provider = query.Text(3),
                EventId = (int)query.Number(4),
                Severity = (int)query.Number(5),
                Title = query.Text(6)
            };
        }

        internal LogPosition ReadPosition(string channel)
        {
            using (var query = database.Prepare("SELECT record_number, utc_ticks FROM positions WHERE channel=?"))
            {
                query.Bind(1, channel);
                return query.Read() ? new LogPosition { RecordId = query.Number(0), UtcTicks = query.Number(1) } : null;
            }
        }

        internal void Append(string channel, List<KeyValuePair<long,VaultEvent>> entries, LogPosition position)
        {
            database.Execute("BEGIN IMMEDIATE");
            try
            {
                foreach (var item in entries)
                    Insert(channel, item.Key, item.Value);
                if (position != null)
                {
                    using (var cursor = database.Prepare(
                        "INSERT OR REPLACE INTO positions (channel, record_number, utc_ticks) VALUES (?, ?, ?)"))
                    {
                        cursor.Bind(1, channel);
                        cursor.Bind(2, position.RecordId);
                        cursor.Bind(3, position.UtcTicks);
                        cursor.Read();
                    }
                }
                database.Execute("COMMIT");
            }
            catch
            {
                database.Execute("ROLLBACK");
                throw;
            }
        }

        private void Insert(string channel, long recordNumber, VaultEvent entry)
        {
            if (insertEvent == null)
            {
                insertEvent = database.Prepare(@"
                    INSERT OR IGNORE INTO events
                        (channel,record_number,utc_ticks,provider,event_id,windows_level,severity,title,explanation,steps,detail)
                    VALUES (?,?,?,?,?,?,?,?,?,?,?)");
            }

            try
            {
                insertEvent.Bind(1, channel);
                insertEvent.Bind(2, recordNumber);
                insertEvent.Bind(3, entry.UtcTicks);
                insertEvent.Bind(4, entry.Provider);
                insertEvent.Bind(5, entry.EventId);
                insertEvent.Bind(6, entry.WindowsLevel);
                insertEvent.Bind(7, entry.Severity);
                insertEvent.Bind(8, entry.Title);
                insertEvent.Bind(9, entry.Explanation);
                insertEvent.Bind(10, entry.Steps);
                insertEvent.Bind(11, entry.Detail);
                insertEvent.Read();
            }
            finally
            {
                insertEvent.Reset();
            }
        }

        public void Dispose()
        {
            if (insertEvent != null)
                insertEvent.Dispose();
            database.Dispose();
        }
    }
}
