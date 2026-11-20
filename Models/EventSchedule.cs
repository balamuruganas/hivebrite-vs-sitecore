using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ical.Net;

namespace Feature.Event.Calendar.Models
{
    /// <summary>
    /// Sitecore-independent description of an event's schedule. Times are server-local wall-clock
    /// times (DateTimeKind.Unspecified), so recurrences keep the same clock time across DST changes.
    /// </summary>
    [DebuggerDisplay("{Start} +{Duration} recurring={IsRecurring} {Frequency} x{Interval} until={Until}")]
    public sealed class EventSchedule
    {
        public DateTime Start { get; set; }
        public TimeSpan Duration { get; set; }

        public bool IsRecurring { get; set; }
        public FrequencyType? Frequency { get; set; }
        public int Interval { get; set; } = 1;
        public IList<DayOfWeek> DaysOfWeek { get; set; } = new List<DayOfWeek>();

        /// <summary>1..31, or negative values counted from month end (-1 = last day).</summary>
        public IList<int> DaysOfMonth { get; set; } = new List<int>();

        /// <summary>
        /// Week of the month for Monthly/Yearly: 1..5 (First..Fifth), -1 = Last.
        /// Combined with DaysOfWeek: "First + Monday" => 1st Monday of the month.
        /// </summary>
        public IList<int> WeeksOfMonth { get; set; } = new List<int>();

        /// <summary>Inclusive end of the series (null = open ended).</summary>
        public DateTime? Until { get; set; }

        public DateTime End => Start + Duration;
    }

    [DebuggerDisplay("{Start} – {End}")]
    public sealed class EventOccurrence
    {
        public EventOccurrence(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }

        public DateTime Start { get; }
        public DateTime End { get; }

        public override string ToString() => $"{Start:yyyy-MM-dd HH:mm} – {End:yyyy-MM-dd HH:mm}";
    }

    public sealed class OccurrenceSyncResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Removed { get; set; }
        public int Unchanged { get; set; }

        public override string ToString() =>
            $"created={Created}, updated={Updated}, removed={Removed}, unchanged={Unchanged}";
    }
}
