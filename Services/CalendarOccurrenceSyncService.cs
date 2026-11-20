using System;
using System.Collections.Generic;
using System.Linq;
using Feature.Event.Calendar.Models;
using Sitecore;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.SecurityModel;
using EventFields = Feature.Event.Calendar.CalendarTemplates.CalendarEvent.Fields;
using OccurrenceFields = Feature.Event.Calendar.CalendarTemplates.CalendarEventOccurrence.Fields;

namespace Feature.Event.Calendar.Services
{
    /// <summary>
    /// Materialises the occurrences of a Calendar Event as "Calendar Event Occurrence" items:
    ///
    ///   Calendar Events                              (Calendar)
    ///     └─ A Caregiver Support                     (Calendar Event)
    ///          └─ Calendar Event Occurrence Folder   (Calendar Event Occurrence Folder {23BACE65-...})
    ///               ├─ 2026-12-01 1000               (Calendar Event Occurrence)
    ///               └─ 2026-12-08 1000
    ///
    ///  - End Date of every occurrence = occurrence start + Duration (date AND time).
    ///  - When Duration is filled, the event's own End Date is updated to Start Date + Duration.
    ///  - Series range: Start Date .. Until (inclusive). Without Until the series is generated up
    ///    to HorizonDays from today (the daily agent keeps extending it).
    ///  - Idempotent: occurrences are matched by UID ({eventGuid}-{yyyyMMddTHHmmss}); occurrences
    ///    that no longer match the rule are removed.
    ///  - Inactive event (IsActive unchecked) => all occurrences are removed.
    /// </summary>
    public class CalendarOccurrenceSyncService
    {
        /// <summary>Used only when Until is empty.</summary>
        public int HorizonDays { get; set; } = 365;

        /// <summary>Safety cap per event (e.g. daily for ~3 years).</summary>
        public int MaxOccurrences { get; set; } = 1000;

        /// <summary>true = move removed occurrences to the Recycle Bin, false = delete permanently.</summary>
        public bool RecycleRemoved { get; set; } = true;

        /// <summary>Write Start Date + Duration back to the event's End Date field.</summary>
        public bool UpdateEventEndDate { get; set; } = true;

        /// <summary>
        /// true  = occurrences are only generated when the event's "IsActive" checkbox is ticked.
        /// false = IsActive is ignored (every event with a Start Date gets occurrences).
        /// </summary>
        public bool RequireIsActive { get; set; } = true;

        /// <summary>Generates occurrences without touching Sitecore (preview / API / tests).</summary>
        public IReadOnlyList<EventOccurrence> GetOccurrences(Item eventItem, DateTime? now = null)
        {
            var schedule = CalendarEventReader.Read(eventItem);
            if (schedule == null) return Array.Empty<EventOccurrence>();

            var (from, to) = GetWindow(schedule, now ?? DateTime.Now);
            return RecurrenceEngine.GetOccurrences(schedule, from, to, MaxOccurrences);
        }

        /// <param name="eventItem">Calendar Event item (master database).</param>
        /// <param name="occurrenceParent">
        /// Optional override of where occurrences live. Default: the event's child
        /// "Calendar Event Occurrence Folder" (created when needed).
        /// </param>
        public OccurrenceSyncResult Sync(Item eventItem, Item occurrenceParent = null, DateTime? now = null)
        {
            Assert.ArgumentNotNull(eventItem, nameof(eventItem));
            Assert.IsTrue(CalendarEventReader.IsCalendarEvent(eventItem),
                $"Item {eventItem.ID} is not a Calendar Event.");

            var result = new OccurrenceSyncResult();
            var schedule = CalendarEventReader.Read(eventItem);
            var current = now ?? DateTime.Now;

            using (new SecurityDisabler())
            {
                if (UpdateEventEndDate && schedule != null)
                    UpdateEndDateFromDuration(eventItem, schedule);

                var isActive = !RequireIsActive || CalendarEventReader.IsActive(eventItem);
                IReadOnlyList<EventOccurrence> desired = Array.Empty<EventOccurrence>();

                if (schedule != null && isActive)
                {
                    var (windowStart, windowEnd) = GetWindow(schedule, current);
                    desired = RecurrenceEngine.GetOccurrences(schedule, windowStart, windowEnd, MaxOccurrences);

                    Log.Info($"[Calendar] {eventItem.Paths.FullPath}: {CalendarEventReader.Describe(schedule)}, " +
                             $"rrule={RecurrenceEngine.ToRRule(schedule) ?? "-"}, " +
                             $"window={windowStart:yyyy-MM-dd HH:mm}..{windowEnd:yyyy-MM-dd HH:mm}, occurrences={desired.Count}", this);

                    if (desired.Count >= MaxOccurrences)
                        Log.Warn($"[Calendar] {eventItem.Paths.FullPath}: capped at {MaxOccurrences} occurrences.", this);

                    if (desired.Count == 0)
                        Log.Warn($"[Calendar] {eventItem.Paths.FullPath}: the recurrence rule produced no dates " +
                                 $"between {windowStart:yyyy-MM-dd HH:mm} and {windowEnd:yyyy-MM-dd HH:mm}. " +
                                 "Check Until (before Start Date?) and the ticked days.", this);
                }
                else
                {
                    Log.Warn($"[Calendar] {eventItem.Paths.FullPath}: no occurrences generated because " +
                             (schedule == null
                                 ? $"Start Date is empty (raw value '{eventItem[EventFields.StartDate]}')"
                                 : "the IsActive checkbox is not ticked") +
                             ". Existing occurrences will be removed.", this);
                }

                // Always ensure: {Event} / Calendar Event Occurrence Folder
                var parent = occurrenceParent ?? GetEventFolder(eventItem, create: true);

                var externalReference = !string.IsNullOrWhiteSpace(eventItem[EventFields.EventId])
                    ? eventItem[EventFields.EventId]
                    : eventItem.ID.ToString();

                var existing = GetExistingOccurrences(parent, eventItem.ID);
                var desiredUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var occurrence in desired)
                {
                    var uid = BuildUid(eventItem.ID, occurrence.Start);
                    desiredUids.Add(uid);

                    if (existing.TryGetValue(uid, out var item))
                    {
                        if (Apply(item, occurrence, uid, externalReference)) result.Updated++;
                        else result.Unchanged++;
                    }
                    else
                    {
                        item = parent.Add(BuildItemName(occurrence.Start),
                            new TemplateID(CalendarTemplates.CalendarEventOccurrence.TemplateId));
                        Apply(item, occurrence, uid, externalReference);
                        result.Created++;
                    }
                }

                // Remove occurrences that no longer belong to the series.
                foreach (var stale in existing.Where(kv => !desiredUids.Contains(kv.Key)).Select(kv => kv.Value))
                {
                    Remove(stale);
                    result.Removed++;
                }
            }

            Log.Info($"[Calendar] Synced occurrences for {eventItem.Paths.FullPath}: {result}", this);
            return result;
        }

        /// <summary>Removes all generated occurrences of the event (keeps the folder).</summary>
        public int RemoveAll(Item eventItem)
        {
            using (new SecurityDisabler())
            {
                var folder = GetEventFolder(eventItem, create: false);
                if (folder == null) return 0;

                var existing = GetExistingOccurrences(folder, eventItem.ID);
                foreach (var item in existing.Values) Remove(item);
                return existing.Count;
            }
        }

        /// <summary>
        /// Returns the event's child "Calendar Event Occurrence Folder": first child with the folder
        /// template {23BACE65-...}, otherwise a child with that name. Creates it when
        /// <paramref name="create"/> is true and none exists.
        /// </summary>
        public Item GetEventFolder(Item eventItem, bool create)
        {
            var folderTemplateId = CalendarTemplates.CalendarEventOccurrenceFolder.TemplateId;
            var folderName = CalendarTemplates.CalendarEventOccurrenceFolder.DefaultItemName;
            var children = eventItem.GetChildren().Cast<Item>().ToList();

            var folder = children.FirstOrDefault(c => c.TemplateID == folderTemplateId)
                      ?? children.FirstOrDefault(c => string.Equals(c.Name, folderName, StringComparison.OrdinalIgnoreCase));

            if (folder != null)
            {
                if (folder.TemplateID != folderTemplateId)
                    Log.Warn($"[Calendar] {folder.Paths.FullPath} has template {folder.TemplateID}, expected {folderTemplateId}. " +
                             "Using it anyway – check CalendarTemplates.CalendarEventOccurrenceFolder.TemplateId.", this);
                return folder;
            }

            if (!create) return null;

            var template = eventItem.Database.GetTemplate(folderTemplateId);
            if (template == null)
                throw new InvalidOperationException(
                    $"Template {folderTemplateId} (Calendar Event Occurrence Folder) not found in '{eventItem.Database.Name}'. " +
                    "Is this the template ID and not the ID of a content item?");

            folder = eventItem.Add(folderName, template);
            if (folder == null)
                throw new InvalidOperationException($"Could not create '{folderName}' under {eventItem.Paths.FullPath}.");

            Log.Info($"[Calendar] Created {folder.Paths.FullPath}", this);
            return folder;
        }

        private static bool IsEventFolder(Item parent, ID eventId) =>
            parent.ID == eventId ||
            (parent.ParentID == eventId &&
             parent.TemplateID == CalendarTemplates.CalendarEventOccurrenceFolder.TemplateId);

        public static string BuildUid(ID eventId, DateTime start) =>
            $"{eventId.Guid:N}-{start:yyyyMMdd'T'HHmmss}";

        /// <summary>
        /// If Duration is filled: End Date = Start Date + Duration (keeps the time of day).
        /// End Date is a shared field, so this writes once for all languages.
        /// </summary>
        private static void UpdateEndDateFromDuration(Item eventItem, EventSchedule schedule)
        {
            if (CalendarEventReader.ParseDuration(eventItem[EventFields.Duration]) == null) return;

            var endValue = ToSitecoreDate(schedule.End);
            if (eventItem[EventFields.EndDate] == endValue) return;

            using (new EditContext(eventItem, updateStatistics: false, silent: false))
                eventItem[EventFields.EndDate] = endValue;
        }

        /// <summary>Start Date .. Until (inclusive), or .. today + HorizonDays when Until is empty.</summary>
        private (DateTime from, DateTime to) GetWindow(EventSchedule schedule, DateTime now)
        {
            var from = schedule.Start;

            if (!schedule.IsRecurring)
                return (from, from);

            DateTime to;
            if (schedule.Until.HasValue)
            {
                to = schedule.Until.Value.TimeOfDay == TimeSpan.Zero
                    ? schedule.Until.Value.Date.AddDays(1).AddSeconds(-1)   // date only => whole day
                    : schedule.Until.Value;
            }
            else
            {
                var horizonBase = now.Date > schedule.Start.Date ? now.Date : schedule.Start.Date;
                to = horizonBase.AddDays(HorizonDays + 1).AddSeconds(-1);
            }

            if (to < from) to = from;
            return (from, to);
        }

        private Dictionary<string, Item> GetExistingOccurrences(Item parent, ID eventId)
        {
            var prefix = eventId.Guid.ToString("N") + "-";
            var map = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            var dedicatedFolder = IsEventFolder(parent, eventId);

            foreach (Item child in parent.GetChildren())
            {
                if (child.TemplateID != CalendarTemplates.CalendarEventOccurrence.TemplateId) continue;

                var uid = child[OccurrenceFields.Uid];

                // UID is versioned: when the event is saved in another language the child may have no
                // version (empty UID) in that language. Fall back to the shared Start Date so we match
                // the existing item instead of creating a duplicate (only in the event's own folder).
                if (string.IsNullOrEmpty(uid) && dedicatedFolder)
                {
                    var start = CalendarEventReader.GetDate(child, OccurrenceFields.StartDate);
                    if (start.HasValue) uid = BuildUid(eventId, start.Value);
                }

                if (string.IsNullOrEmpty(uid) || !uid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (map.ContainsKey(uid))
                {
                    // Duplicate (e.g. from a race) – drop the extra one.
                    Remove(child);
                    continue;
                }
                map[uid] = child;
            }
            return map;
        }

        /// <returns>true if any field changed.</returns>
        private static bool Apply(Item item, EventOccurrence occurrence, string uid, string externalReference)
        {
            var values = new Dictionary<ID, string>
            {
                [OccurrenceFields.StartDate] = ToSitecoreDate(occurrence.Start),
                [OccurrenceFields.EndDate] = ToSitecoreDate(occurrence.End),        // start + duration, with time
                [OccurrenceFields.EventEndDate] = ToSitecoreDate(occurrence.End),
                [OccurrenceFields.Uid] = uid,
                [OccurrenceFields.ExternalReferenceId] = externalReference,
            };

            var changed = values.Where(kv => item.Fields[kv.Key] != null && item[kv.Key] != kv.Value).ToList();
            if (changed.Count == 0) return false;

            using (new EditContext(item, updateStatistics: true, silent: false))
            {
                foreach (var kv in changed)
                    item[kv.Key] = kv.Value;
            }
            return true;
        }

        private void Remove(Item item)
        {
            if (RecycleRemoved) item.Recycle();
            else item.Delete();
        }

        /// <summary>Server-local wall-clock time -> Sitecore ISO UTC string ("yyyyMMddTHHmmssZ").</summary>
        private static string ToSitecoreDate(DateTime serverLocal) =>
            DateUtil.ToIsoDate(DateUtil.ToUniversalTime(DateTime.SpecifyKind(serverLocal, DateTimeKind.Local)));

        private static string BuildItemName(DateTime start) =>
            ItemUtil.ProposeValidItemName(start.ToString("yyyy-MM-dd HHmm"));
    }
}
