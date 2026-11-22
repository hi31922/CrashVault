using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Xml.Linq;

namespace CrashVault
{
    internal sealed class EventCollector
    {
        private const string StandardLevels = "(Level=1 or Level=2 or Level=3)";
        private const string RecentHistory = "TimeCreated[timediff(@SystemTime) <= 604800000]";
        private const int WriteBatchSize = 256;
        private const int DetailLimit = 32000;
        private readonly VaultStore store;

        public EventCollector(VaultStore store)
        {
            this.store = store;
        }

        public void Run()
        {
            string[] channels = { "System", "Application" };
            DateTime discoverAt = DateTime.MinValue;
            store.SetCollectorStatus("Reading Windows event logs…");

            while (true)
            {
                if (DateTime.UtcNow >= discoverAt)
                {
                    channels = FindChannels();
                    discoverAt = DateTime.UtcNow.AddMinutes(10);
                }

                int readable = 0;
                foreach (string channel in channels)
                {
                    try
                    {
                        CollectChannel(channel);
                        readable++;
                    }
                    catch (EventLogException)
                    {
                        // A channel can be restricted, cleared, or disabled during a scan.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (SecurityException)
                    {
                    }
                }

                string status = readable == 0 ? "Windows event logs are unavailable. Retrying…" :
                    String.Format(CultureInfo.CurrentCulture, "Watching {0} Windows logs · Checked {1:t}",
                        readable, DateTime.Now);
                store.SetCollectorStatus(status);
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
        }

        private static string[] FindChannels()
        {
            var channels = new List<string> { "System", "Application" };
            try
            {
                foreach (string name in EventLogSession.GlobalSession.GetLogNames())
                {
                    if (channels.Contains(name) || String.Equals(name, "Security", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        using (var settings = new EventLogConfiguration(name))
                        {
                            if (settings.IsEnabled &&
                                (settings.LogType == EventLogType.Administrative || settings.LogType == EventLogType.Operational))
                                channels.Add(name);
                        }
                    }
                    catch (EventLogException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (SecurityException)
                    {
                    }
                }
            }
            catch (EventLogException)
            {
                // Keep scanning the standard logs if discovery is temporarily unavailable.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (SecurityException)
            {
            }
            return channels.ToArray();
        }

        private void CollectChannel(string channel)
        {
            LogPosition newest = ReadPosition(channel, "*", true);
            if (newest == null)
                return;

            LogPosition saved = store.ReadPosition(channel);
            if (saved != null && newest.RecordId == saved.RecordId && newest.UtcTicks == saved.UtcTicks)
                return;

            string filter = newest.RecordId.ToString(CultureInfo.InvariantCulture);
            string range = "EventRecordID <= " + filter;
            if (saved == null)
            {
                range += " and " + RecentHistory;
            }
            else if (newest.RecordId > saved.RecordId)
            {
                string number = saved.RecordId.ToString(CultureInfo.InvariantCulture);
                LogPosition anchor = ReadPosition(channel, "*[System[EventRecordID=" + number + "]]", true);
                if (anchor != null && anchor.UtcTicks == saved.UtcTicks)
                {
                    range += " and EventRecordID > " + number;
                }
                else
                {
                    LogPosition oldest = ReadPosition(channel, "*", false);
                    if (oldest != null && oldest.RecordId > saved.RecordId)
                        range += " and EventRecordID > " + number;
                }
            }

            string filterLevels = StandardLevels;
            if (channel == "System")
            {
                filterLevels = "(" + StandardLevels +
                    " or (Provider[@Name='Display'] and EventID=4101)" +
                    " or (Provider[@Name='EventLog'] and EventID=6008)" +
                    " or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001))";
            }
            else if (channel == "Application")
            {
                filterLevels = "(" + StandardLevels + " or (Provider[@Name='Application Hang'] and EventID=1002))";
            }

            string query = "*[System[" + range + " and " + filterLevels + "]]";
            var batch = new List<KeyValuePair<long,VaultEvent>>(WriteBatchSize);
            using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, query)))
            {
                EventRecord next;
                while ((next = reader.ReadEvent()) != null)
                {
                    using (next)
                    {
                        if (!next.RecordId.HasValue)
                            continue;
                        VaultEvent entry = ConvertEvent(next, channel);
                        batch.Add(new KeyValuePair<long,VaultEvent>(next.RecordId.Value, entry));
                    }
                    if (batch.Count == WriteBatchSize)
                    {
                        store.Append(channel, batch, null);
                        batch.Clear();
                    }
                }
            }

            // A replay after shutdown is harmless. The archive's event identity is unique.
            store.Append(channel, batch, newest);
        }

        private static LogPosition ReadPosition(string channel, string filter, bool newestFirst)
        {
            var query = new EventLogQuery(channel, PathType.LogName, filter) { ReverseDirection = newestFirst };
            using (var reader = new EventLogReader(query))
            {
                reader.BatchSize = 1;
                using (var item = reader.ReadEvent())
                {
                    if (item == null || !item.RecordId.HasValue)
                        return null;
                    return new LogPosition { RecordId = item.RecordId.Value, UtcTicks = EventTime(item) };
                }
            }
        }

        private static VaultEvent ConvertEvent(EventRecord item, string channel)
        {
            var entry = new VaultEvent
            {
                UtcTicks = EventTime(item),
                Channel = channel,
                Provider = item.ProviderName ?? channel,
                EventId = item.Id,
                WindowsLevel = item.Level.GetValueOrDefault(),
                Detail = ReadDetail(item)
            };
            EventGuidance.Apply(entry);
            return entry;
        }

        private static long EventTime(EventRecord item)
        {
            return item.TimeCreated.HasValue ? item.TimeCreated.Value.ToUniversalTime().Ticks : 0;
        }

        private static string ReadDetail(EventRecord item)
        {
            string message = null;
            try
            {
                message = item.FormatDescription();
            }
            catch (EventLogException)
            {
                // Some providers supply data without an installed message resource.
            }

            if (String.IsNullOrWhiteSpace(message))
            {
                try
                {
                    var root = XDocument.Parse(item.ToXml()).Root;
                    var data = root.Elements().Where(element =>
                        element.Name.LocalName == "EventData" || element.Name.LocalName == "UserData");

                    IEnumerable<string> fields = data.SelectMany(element => element.Descendants())
                        .Where(element => !element.HasElements && !String.IsNullOrWhiteSpace(element.Value))
                        .Select(element =>
                        {
                            XAttribute name = element.Attribute("Name");
                            return (name == null ? element.Name.LocalName : name.Value) + ": " + element.Value;
                        });
                    message = String.Join(Environment.NewLine, fields);
                }
                catch (EventLogException)
                {
                }
                catch (System.Xml.XmlException)
                {
                }
            }

            if (String.IsNullOrWhiteSpace(message))
                return "Windows did not provide a description for this event.";

            message = message.Trim();
            return message.Length <= DetailLimit ? message : message.Substring(0, DetailLimit) + "\r\n[Details shortened]";
        }
    }
}
