using Sitecore.Data;

namespace Feature.Event.Calendar
{
    /// <summary>
    /// Template and field IDs for the Calendar Event / Calendar Event Occurrence templates.
    /// </summary>
    public static class CalendarTemplates
    {
        /// <summary>Calendar (parent of the events), e.g. "Shared Content/Calendar/Calendar Events".</summary>
        public static class CalendarItem
        {
            public static readonly ID TemplateId = new ID("{87B8E9AF-B49B-4BEB-820C-854588A2B817}");
        }

        /// <summary>
        /// Folder created under each Calendar Event that holds its occurrences:
        ///   Calendar Events / {Event} / Calendar Event Occurrence Folder / {Occurrence items}
        /// </summary>
        public static class CalendarEventOccurrenceFolder
        {
            public static readonly ID TemplateId = new ID("{23BACE65-B688-497D-9548-E4AF0B450F97}");
            public const string DefaultItemName = "Calendar Event Occurrence Folder";
        }

        /// <summary>/sitecore/templates/Feature/Event/Calendar/Content Types/Calendar Event</summary>
        public static class CalendarEvent
        {
            public static readonly ID TemplateId = new ID("{F72BD9A9-C224-4784-8124-909667D8B5AB}");

            public static class Fields
            {
                // Dates
                public static readonly ID StartDate = new ID("{82C3371C-4D85-4070-83F2-F49E00EF8CEF}");
                public static readonly ID EndDate = new ID("{73E4BDB8-54C4-411F-A183-8AE997C0B5FC}");

                // Event
                public static readonly ID EventId = new ID("{BC442045-C13C-4B85-9F5F-16B2C2CFE788}");
                public static readonly ID Url = new ID("{56053474-639E-4591-8CAB-CD5F9990A3F6}");
                public static readonly ID Duration = new ID("{CED556C2-8D45-4AE0-925E-25A5011910BE}");
                public static readonly ID IsActive = new ID("{BFAC7A5F-0A66-4431-89E7-FA8CAC84CEC1}");

                // Recurrence
                public static readonly ID IsRecurring = new ID("{5ECA79E9-E4DC-4EE2-8E32-EB62AD72F537}");
                public static readonly ID Frequency = new ID("{5FA18D21-CA09-469A-A998-683BCD7483B1}");     // Droplink
                public static readonly ID DayOfTheWeek = new ID("{B29952AA-CEC2-4387-A3ED-4CD0E2A2B947}");  // Checklist
                public static readonly ID Interval = new ID("{39FEFCD7-3B84-4D66-A439-A5DD25851976}");      // Droplink
                public static readonly ID Until = new ID("{E95E3E4F-DB9F-484A-9832-0B88CC789BC6}");
                public static readonly ID DayOfTheMonth = new ID("{C99CCDDA-8B74-44BF-A60E-5F09A9467B98}"); // Checklist

                /// <summary>
                /// Weeks of the Month (First, Second, Third, Forth, Last) – NOT on the template yet.
                /// Add a Checklist/Droplink field with source "Weeks of the Month" and put its ID here;
                /// until then the field is looked up by one of <see cref="WeekOfTheMonthNames"/>.
                /// </summary>
                public static readonly ID WeekOfTheMonth = null;
                public static readonly string[] WeekOfTheMonthNames =
                    { "WeekOfTheMonth", "WeeksOfTheMonth", "Week Of The Month", "Weeks Of The Month" };
            }

            /// <summary>Datasource folders of the lookup fields (for reference / validation).</summary>
            public static class Sources
            {
                public static readonly ID Frequency = new ID("{2851E1DA-8D9D-4606-A573-697593602AED}");
                public static readonly ID DayOfTheWeek = new ID("{AE90AE5B-F7ED-437C-8F12-ABFE250A8B02}");
                public static readonly ID Interval = new ID("{1403B74D-BCC3-48C7-8FD2-99A6A2E26EC2}");
                public static readonly ID DayOfTheMonth = new ID("{2AC2685F-75E1-4ED6-93D0-D047205E2B1B}");
            }
        }

        /// <summary>/sitecore/templates/Feature/Event/Calendar/Content Types/Calendar Event Occurrence</summary>
        public static class CalendarEventOccurrence
        {
            public static readonly ID TemplateId = new ID("{FF2439BF-191D-4A60-97DA-0C23918BCE2E}");

            public static class Fields
            {
                // Dates (shared, same IDs as on Calendar Event – from _Start Date / _End Date)
                public static readonly ID StartDate = new ID("{82C3371C-4D85-4070-83F2-F49E00EF8CEF}");
                public static readonly ID EndDate = new ID("{73E4BDB8-54C4-411F-A183-8AE997C0B5FC}");

                // Event
                public static readonly ID Uid = new ID("{AB2A83A8-A3CE-4901-8E42-88F288703B43}");
                public static readonly ID ExternalReferenceId = new ID("{47CD7915-AC4D-4027-A438-9ED3D1F77D30}");

                /// <summary>
                /// NOTE: second "End Date" field in the Event section (versioned). It duplicates the shared
                /// Dates/End Date by name, so item["End Date"] is ambiguous. The sync service writes both
                /// to keep them consistent – consider deleting this field from the template.
                /// </summary>
                public static readonly ID EventEndDate = new ID("{30F869A4-3A92-4C80-B978-CC32B3A77A1A}");
            }
        }
    }
}
