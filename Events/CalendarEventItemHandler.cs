using System;
using Feature.Event.Calendar.Services;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;

namespace Feature.Event.Calendar.Events
{
    /// <summary>
    /// Regenerates occurrences when a Calendar Event is saved. Deleting an event needs no handler:
    /// its "Calendar Event Occurrence Folder" is a child item and is deleted with it.
    /// Wired up in App_Config/Include/Feature/Feature.Event.Calendar.config.
    /// </summary>
    public class CalendarEventItemHandler
    {
        [ThreadStatic] private static bool _running;

        public void OnItemSaved(object sender, EventArgs args)
        {
            var item = Sitecore.Events.Event.ExtractParameter(args, 0) as Item;
            if (!ShouldHandle(item)) return;

            Run(() => new CalendarOccurrenceSyncService().Sync(item), item);
        }

        private static bool ShouldHandle(Item item) =>
            !_running &&
            item != null &&
            string.Equals(item.Database?.Name, "master", StringComparison.OrdinalIgnoreCase) &&
            CalendarEventReader.IsCalendarEvent(item) &&
            !item.Paths.FullPath.StartsWith("/sitecore/templates", StringComparison.OrdinalIgnoreCase); // skip standard values

        private static void Run(Action action, Item item)
        {
            try
            {
                _running = true;
                action();
            }
            catch (Exception ex)
            {
                // Never block the editor's save because of occurrence generation.
                Log.Error($"[Calendar] Occurrence sync failed for {item.Paths.FullPath} ({item.ID})", ex, typeof(CalendarEventItemHandler));
            }
            finally
            {
                _running = false;
            }
        }
    }
}
