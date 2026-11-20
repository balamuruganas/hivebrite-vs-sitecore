using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Feature.Event.Calendar.Models;
using Ical.Net;
using Sitecore;
using Sitecore.Data;
using Sitecore.Data.Fields;
using Sitecore.Data.Items;
using Sitecore.Data.Managers;
using Fields = Feature.Event.Calendar.CalendarTemplates.CalendarEvent.Fields;

namespace Feature.Event.Calendar.Services
{
    /// <summary>
    /// Reads a Calendar Event item into an <see cref="EventSchedule"/>.
    ///
    /// Lookup items are resolved from their Value/Code/Key/Text/Title/Name field, display name or
    /// item name (first that parses). Supported lookup folders:
    ///   Frequencies          : Daily | Weekly | Monthly | Yearly
    ///   Repeat Intervals     : 1 .. 20
    ///   Days of the Week     : Sunday .. Saturday   (also Mon / MO)
    ///   Week Days            : Monday .. Friday     (same parser – e.g. Daily + Mon–Fri = every weekday)
    ///   Days of the Month    : 1 .. 31 | Last
    ///   Weeks of the Month   : First | Second | Third | Forth/Fourth | Last
    /// </summary>
    public static class CalendarEventReader
    {
        public static bool IsCalendarEvent(Item item) =>
            item != null && InheritsFrom(item, CalendarTemplates.CalendarEvent.TemplateId);

        public static bool IsActive(Item item) =>
            item != null && MainUtil.GetBool(item[Fields.IsActive], false);

        /// <summary>
        /// Returns null when the item has no Start Date.
        ///
        ///  - Start Date : first occurrence (date + time) and start of the series.
        ///  - Until      : end of the series (inclusive; a date without time = whole day).
        ///  - Duration   : length of each occurrence (fallback: time of End Date - time of Start Date).
        ///  - The event recurs when "Is Recurring" is ticked. If no Frequency is selected it is
        ///    inferred: DayOfTheMonth ticked => Monthly, DayOfTheWeek ticked => Weekly.
        /// </summary>
        public static EventSchedule Read(Item item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var start = GetDate(item, Fields.StartDate);
            if (start == null) return null;

            var schedule = new EventSchedule
            {
                Start = start.Value,
                Until = GetDate(item, Fields.Until),
                // Droplink normally holds an item ID; the raw value is a fallback for plain text.
                Frequency = ParseFrequency(GetDroplinkItem(item, Fields.Frequency)) ?? ParseFrequency(item[Fields.Frequency]),
                Interval = ParseInterval(GetDroplinkItem(item, Fields.Interval)),
                DaysOfWeek = GetChecklistItems(item, Fields.DayOfTheWeek)
                    .Select(i => ParseDayOfWeek(i))
                    .Where(d => d.HasValue).Select(d => d.Value).Distinct().ToList(),
                DaysOfMonth = GetChecklistItems(item, Fields.DayOfTheMonth)
                    .Select(i => ParseDayOfMonth(i))
                    .Where(d => d.HasValue).Select(d => d.Value).Distinct().ToList(),
                WeeksOfMonth = GetLookupItems(FindField(item, Fields.WeekOfTheMonth, Fields.WeekOfTheMonthNames))
                    .Select(i => ParseWeekOfMonth(i))
                    .Where(w => w.HasValue).Select(w => w.Value).Distinct().ToList(),
            };

            var isRecurringTicked = MainUtil.GetBool(item[Fields.IsRecurring], false);

            if (schedule.Frequency == null && isRecurringTicked)
            {
                if (schedule.DaysOfMonth.Count > 0) schedule.Frequency = FrequencyType.Monthly;
                else if (schedule.DaysOfWeek.Count > 0) schedule.Frequency = FrequencyType.Weekly;
            }

            // Unticked "Is Recurring" = one-off event, whatever the other recurrence fields say.
            schedule.IsRecurring = isRecurringTicked && schedule.Frequency != null;

            if (isRecurringTicked && schedule.Frequency == null)
                Sitecore.Diagnostics.Log.Warn(
                    $"[Calendar] {item.Paths.FullPath}: 'Is Recurring' is ticked but Frequency could not be resolved " +
                    $"(raw value '{item[Fields.Frequency]}'). Treated as a one-off event.", typeof(CalendarEventReader));

            schedule.Duration = ResolveDuration(item, schedule, GetDate(item, Fields.EndDate));
            return schedule;
        }

        /// <summary>
        /// Duration field wins. Otherwise the End Date: for a recurring event only its time of day
        /// is used (End 11:30 - Start 10:00 = 1h30 per occurrence); for a one-off, End - Start.
        /// </summary>
        private static TimeSpan ResolveDuration(Item item, EventSchedule schedule, DateTime? end)
        {
            var parsed = ParseDuration(item[Fields.Duration]);
            if (parsed.HasValue) return parsed.Value;
            if (end == null) return TimeSpan.Zero;

            if (schedule.IsRecurring)
            {
                var timeOfDay = end.Value.TimeOfDay - schedule.Start.TimeOfDay;
                return timeOfDay > TimeSpan.Zero ? timeOfDay : TimeSpan.Zero;
            }

            return end.Value > schedule.Start ? end.Value - schedule.Start : TimeSpan.Zero;
        }

        /// <summary>Human-readable summary for logging / debugging.</summary>
        public static string Describe(EventSchedule s) =>
            s == null
                ? "no Start Date"
                : $"start={s.Start:yyyy-MM-dd HH:mm}, until={(s.Until.HasValue ? s.Until.Value.ToString("yyyy-MM-dd HH:mm") : "-")}, " +
                  $"duration={s.Duration}, recurring={s.IsRecurring}, freq={(s.Frequency?.ToString() ?? "-")}, " +
                  $"interval={s.Interval}, days=[{string.Join(",", s.DaysOfWeek)}], monthDays=[{string.Join(",", s.DaysOfMonth)}], " +
                  $"weeksOfMonth=[{string.Join(",", s.WeeksOfMonth)}]";

        #region Parsing

        /// <summary>Accepts "90" (minutes), "1:30", "01:30:00", "PT1H30M", "1h 30m", "2 hours".</summary>
        public static TimeSpan? ParseDuration(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim();

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                return minutes > 0 ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;

            if (value.Contains(":") &&
                TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
                return ts;

            if (value.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                try { return XmlConvert.ToTimeSpan(value.ToUpperInvariant()); }
                catch (FormatException) { /* fall through */ }
            }

            var match = Regex.Match(value,
                @"^(?:(?<d>\d+)\s*d(?:ays?)?)?\s*(?:(?<h>\d+(?:\.\d+)?)\s*h(?:(?:ou)?rs?)?)?\s*(?:(?<m>\d+)\s*m(?:in(?:ute)?s?)?)?$",
                RegexOptions.IgnoreCase);
            if (match.Success && match.Length > 0)
            {
                var result = TimeSpan.Zero;
                if (match.Groups["d"].Success) result += TimeSpan.FromDays(int.Parse(match.Groups["d"].Value));
                if (match.Groups["h"].Success) result += TimeSpan.FromHours(double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
                if (match.Groups["m"].Success) result += TimeSpan.FromMinutes(int.Parse(match.Groups["m"].Value));
                if (result > TimeSpan.Zero) return result;
            }

            return null;
        }

        public static FrequencyType? ParseFrequency(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToLowerInvariant();

            if (!char.IsDigit(t[0]) && Enum.TryParse(t, true, out FrequencyType exact) &&
                exact >= FrequencyType.Daily && exact <= FrequencyType.Yearly)
                return exact;
            if (t.Contains("year") || t.Contains("annual")) return FrequencyType.Yearly;
            if (t.Contains("month")) return FrequencyType.Monthly;
            if (t.Contains("week")) return FrequencyType.Weekly;
            if (t.Contains("day") || t.Contains("daily")) return FrequencyType.Daily;
            return null;
        }

        public static int ParseInterval(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 1;
            var match = Regex.Match(text, @"\d+");
            return match.Success && int.TryParse(match.Value, out var n) && n > 0 ? n : 1;
        }

        public static DayOfWeek? ParseDayOfWeek(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (t.Length < 2) return null;
            var prefix = t.Substring(0, Math.Min(3, t.Length));

            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                if (day.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return day;
            }
            return null;
        }

        public static int? ParseDayOfMonth(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (t.Equals("last", StringComparison.OrdinalIgnoreCase)) return -1;

            var match = Regex.Match(t, @"^(-?\d{1,2})(st|nd|rd|th)?$", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var day)) return null;
            return day != 0 && day >= -31 && day <= 31 ? day : (int?)null;
        }

        #endregion

        #region Sitecore field helpers

        /// <summary>Returns server-local time (Sitecore stores UTC and converts on read).</summary>
        public static DateTime? GetDate(Item item, ID fieldId)
        {
            var field = item.Fields[fieldId];
            if (field == null || string.IsNullOrEmpty(field.Value)) return null;
            var value = ((DateField)field).DateTime;
            return value == DateTime.MinValue ? (DateTime?)null : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }

        private static Item GetDroplinkItem(Item item, ID fieldId)
        {
            var field = item.Fields[fieldId];
            return field == null ? null : ((ReferenceField)field).TargetItem;
        }

        /// <summary>Field by ID if known, otherwise by the first matching name.</summary>
        private static Field FindField(Item item, ID fieldId, IEnumerable<string> names)
        {
            if (!ID.IsNullOrEmpty(fieldId) && item.Fields[fieldId] != null) return item.Fields[fieldId];
            return names?.Select(n => item.Fields[n]).FirstOrDefault(f => f != null);
        }

        /// <summary>Works for Checklist/Multilist (pipe-separated IDs) and Droplink (single ID).</summary>
        private static IEnumerable<Item> GetLookupItems(Field field)
        {
            if (field == null || string.IsNullOrEmpty(field.Value)) return Enumerable.Empty<Item>();
            return field.Value
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => ID.TryParse(v, out var id) ? field.Item.Database.GetItem(id) : null)
                .Where(i => i != null)
                .ToList();
        }

        private static IEnumerable<Item> GetChecklistItems(Item item, ID fieldId)
        {
            var field = item.Fields[fieldId];
            return field == null
                ? Enumerable.Empty<Item>()
                : ((MultilistField)field).GetItems() ?? Enumerable.Empty<Item>();
        }

        /// <summary>Field names tried on a lookup item before its display name / item name.</summary>
        public static readonly string[] LookupFieldNames = { "Value", "Code", "Key", "Text", "Title", "Name" };

        /// <summary>All non-empty texts a lookup item could carry, most specific first.</summary>
        private static IEnumerable<string> LookupTexts(Item lookup)
        {
            if (lookup == null) yield break;
            foreach (var name in LookupFieldNames)
            {
                var value = lookup.Fields[name]?.Value;
                if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
            }
            if (!string.IsNullOrWhiteSpace(lookup.DisplayName)) yield return lookup.DisplayName;
            yield return lookup.Name;
        }

        public static FrequencyType? ParseFrequency(Item lookup) =>
            LookupTexts(lookup).Select(t => ParseFrequency(t)).FirstOrDefault(f => f.HasValue);

        public static int ParseInterval(Item lookup)
        {
            foreach (var text in LookupTexts(lookup))
            {
                var match = Regex.Match(text, @"^\D*(\d{1,3})\D*$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > 0) return n;
            }
            return 1;
        }

        public static DayOfWeek? ParseDayOfWeek(Item lookup) =>
            LookupTexts(lookup).Select(t => ParseDayOfWeek(t)).FirstOrDefault(d => d.HasValue);

        /// <summary>First/1st/1 => 1 ... Fourth/Forth => 4, Fifth => 5, Last => -1.</summary>
        public static int? ParseWeekOfMonth(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToLowerInvariant();

            if (t.StartsWith("last")) return -1;
            if (t.StartsWith("first") || t.StartsWith("1")) return 1;
            if (t.StartsWith("second") || t.StartsWith("2")) return 2;
            if (t.StartsWith("third") || t.StartsWith("3")) return 3;
            if (t.StartsWith("fourth") || t.StartsWith("forth") || t.StartsWith("4")) return 4;
            if (t.StartsWith("fifth") || t.StartsWith("5")) return 5;
            return null;
        }

        public static int? ParseWeekOfMonth(Item lookup) =>
            LookupTexts(lookup).Select(t => ParseWeekOfMonth(t)).FirstOrDefault(w => w.HasValue);

        public static int? ParseDayOfMonth(Item lookup) =>
            LookupTexts(lookup).Select(t => ParseDayOfMonth(t)).FirstOrDefault(d => d.HasValue);

        private static bool InheritsFrom(Item item, ID templateId)
        {
            if (item.TemplateID == templateId) return true;
            var template = TemplateManager.GetTemplate(item);
            return template != null && template.InheritsFrom(templateId);
        }

        #endregion
    }
}
