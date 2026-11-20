using System;
using System.Collections.Generic;
using System.Linq;
using Feature.Event.Calendar.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Serialization.DataTypes;

namespace Feature.Event.Calendar.Services
{
    /// <summary>
    /// Pure Ical.Net logic (no Sitecore dependency) – unit-testable.
    /// Written against Ical.Net 4.x (NuGet "Ical.Net" 4.3.x), which runs on .NET Framework 4.8.
    /// </summary>
    public static class RecurrenceEngine
    {
        /// <summary>Builds an RFC 5545 RRULE from the schedule.</summary>
        public static RecurrencePattern BuildPattern(EventSchedule schedule)
        {
            if (schedule?.Frequency == null)
                throw new ArgumentException("Schedule has no frequency.", nameof(schedule));

            var frequency = schedule.Frequency.Value;
            var pattern = new RecurrencePattern(frequency, Math.Max(1, schedule.Interval));

            var (days, monthDays, weeks) = GetEffectiveRules(schedule);

            if (weeks.Count > 0)
            {
                // "First Monday", "Last Friday", ... => BYDAY=1MO / -1FR
                // No day ticked => use the weekday of the Start Date.
                var weekDays = days.Count > 0 ? days : new List<DayOfWeek> { schedule.Start.DayOfWeek };
                pattern.ByDay = weeks.Distinct()
                    .SelectMany(w => weekDays.Distinct().Select(d => new WeekDay(d) { Offset = w }))
                    .ToList();
            }
            else if (days.Count > 0)
            {
                // BYDAY: filter for DAILY (e.g. Mon–Fri), expands for WEEKLY/MONTHLY/YEARLY.
                pattern.ByDay = days.Distinct().Select(d => new WeekDay(d)).ToList();
            }

            if (monthDays.Count > 0)
                pattern.ByMonthDay = monthDays.Distinct().OrderBy(d => d).ToList();

            // YEARLY + day/week rules would otherwise apply to every month (or the whole year):
            // pin it to the Start Date's month.
            if (frequency == FrequencyType.Yearly && (weeks.Count > 0 || monthDays.Count > 0 || days.Count > 0))
                pattern.ByMonth = new List<int> { schedule.Start.Month };

            if (schedule.Until.HasValue)
                pattern.Until = InclusiveUntil(schedule.Until.Value);

            return pattern;
        }

        /// <summary>Returns the RRULE string, e.g. "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE".</summary>
        public static string ToRRule(EventSchedule schedule) =>
            schedule?.IsRecurring == true && schedule.Frequency != null
                ? new RecurrencePatternSerializer().SerializeToString(BuildPattern(schedule))
                : null;

        public static CalendarEvent BuildCalendarEvent(EventSchedule schedule, string uid = null,
            string summary = null, string url = null)
        {
            var start = Floating(schedule.Start);
            var calendarEvent = new CalendarEvent
            {
                Uid = string.IsNullOrEmpty(uid) ? Guid.NewGuid().ToString("N") : uid,
                Summary = summary,
                DtStart = new CalDateTime(start),
                DtEnd = new CalDateTime(start + schedule.Duration),
            };

            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                calendarEvent.Url = uri;

            if (schedule.IsRecurring && schedule.Frequency != null)
                calendarEvent.RecurrenceRules.Add(BuildPattern(schedule));

            return calendarEvent;
        }

        /// <summary>
        /// Expands the schedule into concrete occurrences that start within [windowStart, windowEnd].
        /// </summary>
        public static IReadOnlyList<EventOccurrence> GetOccurrences(EventSchedule schedule,
            DateTime windowStart, DateTime windowEnd, int maxCount = 500)
        {
            if (schedule == null || windowEnd < windowStart)
                return Array.Empty<EventOccurrence>();

            // One-off event
            if (!schedule.IsRecurring || schedule.Frequency == null)
            {
                return schedule.Start >= windowStart && schedule.Start <= windowEnd
                    ? new[] { new EventOccurrence(schedule.Start, schedule.End) }
                    : Array.Empty<EventOccurrence>();
            }

            var calendarEvent = BuildCalendarEvent(schedule);

            // Ical.Net 4.x: GetOccurrences(DateTime, DateTime) -> HashSet<Occurrence> (unordered)
            return calendarEvent
                .GetOccurrences(Floating(windowStart), Floating(windowEnd))
                .Select(o => Floating(o.Period.StartTime.Value))
                .Where(s => s >= windowStart && s <= windowEnd)
                .Where(s => s != schedule.Start || StartMatchesRule(schedule))
                .Distinct()
                .OrderBy(s => s)
                .Take(maxCount)
                .Select(s => new EventOccurrence(s, s + schedule.Duration))
                .ToList();
        }

        /// <summary>
        /// RFC 5545 always counts DTSTART as the first occurrence, even when it doesn't match the
        /// rule (e.g. Start = Tuesday, rule = every Monday). Editors expect only matching days, so
        /// the start date is dropped in that case.
        /// </summary>
        private static bool StartMatchesRule(EventSchedule schedule)
        {
            var start = schedule.Start;
            var (days, monthDays, weeks) = GetEffectiveRules(schedule);

            // With weeks of the month and no ticked day, BuildPattern uses the Start Date's weekday.
            if (days.Count > 0 && !days.Contains(start.DayOfWeek))
                return false;

            if (weeks.Count > 0)
            {
                var daysInMonthW = DateTime.DaysInMonth(start.Year, start.Month);
                var nth = (start.Day - 1) / 7 + 1;                 // 1st..5th weekday of its kind
                var isLast = start.Day + 7 > daysInMonthW;
                if (!weeks.Any(w => w == nth || (w == -1 && isLast)))
                    return false;
            }

            if (monthDays.Count > 0)
            {
                var daysInMonth = DateTime.DaysInMonth(start.Year, start.Month);
                var matches = monthDays.Any(d =>
                    d > 0 ? d == start.Day : daysInMonth + d + 1 == start.Day);
                if (!matches) return false;
            }

            return true;
        }

        /// <summary>
        /// Picks the day/week fields that apply to the frequency. RFC 5545 ANDs BYDAY and
        /// BYMONTHDAY, so stray ticks in unrelated fields (e.g. Daily + Sun/Thu + "1st of month")
        /// would otherwise reduce the series to (almost) nothing.
        ///   Daily   : days of week (filter, e.g. Mon–Fri)
        ///   Weekly  : days of week
        ///   Monthly / Yearly : week of month (+ days of week), else days of month, else days of week
        /// </summary>
        private static (List<DayOfWeek> days, List<int> monthDays, List<int> weeks) GetEffectiveRules(EventSchedule schedule)
        {
            var days = (schedule.DaysOfWeek ?? new List<DayOfWeek>()).Distinct().ToList();
            var monthDays = (schedule.DaysOfMonth ?? new List<int>()).Distinct().ToList();
            var weeks = (schedule.WeeksOfMonth ?? new List<int>()).Distinct().ToList();

            switch (schedule.Frequency)
            {
                case FrequencyType.Daily:
                case FrequencyType.Weekly:
                    return (days, new List<int>(), new List<int>());

                case FrequencyType.Monthly:
                case FrequencyType.Yearly:
                    if (weeks.Count > 0) return (days, new List<int>(), weeks);
                    if (monthDays.Count > 0) return (new List<DayOfWeek>(), monthDays, new List<int>());
                    return (days, new List<int>(), new List<int>());

                default:
                    return (days, monthDays, weeks);
            }
        }

        /// <summary>Serializes the schedule to a full .ics document (handy for "Add to calendar").</summary>
        public static string ToIcs(EventSchedule schedule, string uid, string summary, string url = null)
        {
            var calendar = new Ical.Net.Calendar();
            calendar.Events.Add(BuildCalendarEvent(schedule, uid, summary, url));
            return new CalendarSerializer().SerializeToString(calendar);
        }

        // Date-only "Until" (midnight) means "including that whole day".
        private static DateTime InclusiveUntil(DateTime until) =>
            until.TimeOfDay == TimeSpan.Zero
                ? Floating(until.Date.AddDays(1).AddSeconds(-1))
                : Floating(until);

        // Unspecified kind => Ical.Net treats it as a floating (wall-clock) time, so no TZ shifting.
        private static DateTime Floating(DateTime value) =>
            DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }
}
