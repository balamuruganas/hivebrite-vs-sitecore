using System;
using System.Collections.Generic;
using Feature.Event.Calendar.Services;
using Sitecore.Configuration;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.SecurityModel;

namespace Feature.Event.Calendar.Tasks
{
    /// <summary>
    /// Scheduled agent that rolls the occurrence window forward every day, so open-ended
    /// recurring events always have occurrences up to HorizonDays ahead.
    /// Point rootPath at your "Calendar Events" item to keep the tree walk small.
    /// </summary>
    public class RollCalendarOccurrencesAgent
    {
        private readonly string _database;
        private readonly string _rootPath;

        public RollCalendarOccurrencesAgent(string database, string rootPath)
        {
            _database = string.IsNullOrWhiteSpace(database) ? "master" : database;
            _rootPath = string.IsNullOrWhiteSpace(rootPath) ? "/sitecore/content" : rootPath;
        }

        public int HorizonDays { get; set; } = 365;

        public void Run()
        {
            var db = Factory.GetDatabase(_database, false);
            if (db == null) { Log.Warn($"[Calendar] Database '{_database}' not found.", this); return; }

            var service = new CalendarOccurrenceSyncService { HorizonDays = HorizonDays };
            int ok = 0, failed = 0;

            using (new SecurityDisabler())
            {
                var root = db.GetItem(_rootPath);
                if (root == null) { Log.Warn($"[Calendar] Root '{_rootPath}' not found.", this); return; }

                // Plain tree walk instead of Sitecore query: query results are capped by the
                // Query.MaxItems setting (100 by default), which would silently skip events.
                foreach (var item in FindEvents(root))
                {
                    try { service.Sync(item); ok++; }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Error($"[Calendar] Roll failed for {item.Paths.FullPath}", ex, this);
                    }
                }
            }

            Log.Info($"[Calendar] Rolled occurrences for {ok} event(s), {failed} failed.", this);
        }

        private static IEnumerable<Item> FindEvents(Item root)
        {
            var stack = new Stack<Item>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                // Generated occurrences – nothing to find in there.
                if (current.TemplateID == CalendarTemplates.CalendarEventOccurrenceFolder.TemplateId) continue;

                if (CalendarEventReader.IsCalendarEvent(current))
                {
                    if (current.Versions.Count > 0) yield return current;
                    continue; // below an event there is only its occurrence folder
                }

                if (!current.HasChildren) continue;
                foreach (Item child in current.GetChildren())
                    stack.Push(child);
            }
        }
    }
}
