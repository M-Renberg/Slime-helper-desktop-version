namespace SlimeHelper
{
    /// <summary>En kalender som är synlig i Google Kalender (huvudkalendern eller en delad kalender från ett annat konto).</summary>
    public sealed record CalendarRef(string Id, string Name, bool IsPrimary);

    /// <summary>En händelse från någon av kalendrarna, oberoende av Googles typer.</summary>
    public sealed record CalendarItem(CalendarRef Calendar, string Summary, DateTimeOffset Start, bool AllDay, string? ICalUid);

    /// <summary>
    /// Ren logik för att läsa flera kalendrar: vilka kalendrar som ska läsas, sammanslagning, dubbletter och visning.
    /// Ingen Google-kod här, så allt går att testa utan nätverk (se SlimeHelper.Checks).
    /// </summary>
    public static class CalendarMerge
    {
        /// <summary>Helgdagar, födelsedagar, veckonummer och väder: kalendrar som annars lätt skulle ta över "nästa händelse".</summary>
        public static bool IsNoiseCalendar(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!id.EndsWith("@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase)) return false;

            return id.Contains("#holiday", StringComparison.OrdinalIgnoreCase)
                || id.Contains("#contacts", StringComparison.OrdinalIgnoreCase)
                || id.Contains("#weeknum", StringComparison.OrdinalIgnoreCase)
                || id.Contains("#weather", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ska den här kalendern läsas? Det du ser (bockad) i Google Kalender läses, plus huvudkalendern alltid.
        /// Kalendrar där du bara ser "upptagen" kan inte listas, och dolda eller borttagna hoppas över.
        /// </summary>
        public static bool ShouldRead(string? id, bool? primary, bool? selected, bool? hidden, bool? deleted, string? accessRole, bool includeNoise = false)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (deleted == true || hidden == true) return false;
            if (string.Equals(accessRole, "freeBusyReader", StringComparison.OrdinalIgnoreCase)) return false;
            if (!includeNoise && IsNoiseCalendar(id)) return false;

            return primary == true || selected == true;
        }

        // Heldagshändelser hamnar sist på sin dag, så ett möte klockan 14 slår "Hemarbete" som "nästa" händelse
        private static DateTimeOffset SortKey(CalendarItem item)
        {
            return item.AllDay ? item.Start.AddDays(1).AddMinutes(-1) : item.Start;
        }

        /// <summary>
        /// Slår ihop händelser från flera kalendrar, tar bort dubbletter (samma inbjudan i två kalendrar, huvudkalenderns version vinner)
        /// och sorterar på tid. Återkommande händelser räknas per tillfälle, inte per serie.
        /// </summary>
        public static List<CalendarItem> MergeUpcoming(IEnumerable<CalendarItem> items)
        {
            var best = new Dictionary<string, CalendarItem>();

            foreach (var item in items)
            {
                string key = $"{item.ICalUid ?? item.Summary}|{item.Start.UtcDateTime.Ticks}|{(item.AllDay ? "d" : "t")}";

                if (!best.TryGetValue(key, out var existing) || (item.Calendar.IsPrimary && !existing.Calendar.IsPrimary))
                {
                    best[key] = item;
                }
            }

            return best.Values
                .OrderBy(SortKey)
                .ThenBy(i => i.Calendar.IsPrimary ? 0 : 1)
                .ThenBy(i => i.Summary, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// "Standup kl 09:00" idag, "Standup 2026-10-23 kl 09:00" en annan dag, "Semester 2026-10-23" för heldagshändelser.
        /// Händelser från andra kalendrar får kalenderns namn efter: "Retro kl 14:30 (Jobb)".
        /// </summary>
        public static string Format(CalendarItem item, DateTimeOffset now, bool showCalendarName)
        {
            string summary = string.IsNullOrWhiteSpace(item.Summary) ? "(no title)" : item.Summary.Trim();
            DateTimeOffset local = item.Start.ToLocalTime();

            string text;
            if (item.AllDay)
            {
                text = $"{summary} {local:yyyy-MM-dd}";
            }
            else if (local.Date == now.ToLocalTime().Date)
            {
                text = $"{summary} kl {local:HH:mm}";
            }
            else
            {
                text = $"{summary} {local:yyyy-MM-dd} kl {local:HH:mm}";
            }

            if (showCalendarName && !item.Calendar.IsPrimary && !string.IsNullOrWhiteSpace(item.Calendar.Name))
            {
                text += $" ({item.Calendar.Name})";
            }

            return text;
        }
    }
}

