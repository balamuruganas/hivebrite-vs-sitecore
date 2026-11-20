using System.Diagnostics;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

// Visual Studio calls ToString() to show objects in DataTips / Locals / Watch.
// For Ical.Net types that runs the serializer, which needs other threads, so VS shows
// "To prevent an unsafe abort when evaluating the function 'RecurrencePattern.ToString' all
// threads were allowed to run" and skips breakpoints.
//
// These assembly-level attributes tell the debugger to display plain property values instead,
// so ToString() is never evaluated. They only affect the debugger, not runtime behaviour.
// To see the full RRULE string while debugging, use RecurrenceEngine.ToRRule(schedule).

[assembly: DebuggerDisplay(
    "RRULE {Frequency} interval={Interval} until={Until} byDay={ByDay.Count} byMonthDay={ByMonthDay.Count} byMonth={ByMonth.Count}",
    Target = typeof(RecurrencePattern))]

[assembly: DebuggerDisplay("{DayOfWeek} offset={Offset}", Target = typeof(WeekDay))]

[assembly: DebuggerDisplay("{Value} tz={TzId}", Target = typeof(CalDateTime))]

[assembly: DebuggerDisplay("{Period.StartTime.Value} – {Period.EndTime.Value}", Target = typeof(Occurrence))]

[assembly: DebuggerDisplay("{StartTime.Value} – {EndTime.Value}", Target = typeof(Period))]

[assembly: DebuggerDisplay("VEVENT {Uid} start={DtStart.Value} rules={RecurrenceRules.Count}", Target = typeof(CalendarEvent))]
