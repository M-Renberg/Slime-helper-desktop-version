using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;

namespace SlimeHelper
{
    public class CalendarWatcher
    {
        private static readonly string[] Scopes = [CalendarService.Scope.Calendar];
        private const string ApplicationName = "Slime Helper";

        /// <summary>Tar med helgdags- och födelsedagskalendrar (av som standard, annars tar de lätt över "nästa händelse").</summary>
        public bool IncludeHolidaysAndBirthdays { get; set; } = false;

        /// <summary>
        /// Resolves credentials.json next to the executable (AppContext.BaseDirectory) first so Windows
        /// autostart (where CWD is C:\Windows\System32) still finds it, falling back to current directory.
        /// </summary>
        public static string ResolveCredentialsPath()
        {
            string baseDirCred = Path.Combine(AppContext.BaseDirectory, "credentials.json");
            if (File.Exists(baseDirCred)) return baseDirCred;
            return Path.GetFullPath("credentials.json");
        }

        public static bool HasCredentialsFile() => File.Exists(ResolveCredentialsPath());

        // Samma inloggning som förut (credentials.json + token.json), men på ett ställe
        private static async Task<CalendarService> CreateServiceAsync()
        {
            UserCredential credential;
            string credPath = ResolveCredentialsPath();

            using (var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read))
            {
                // Sparar tokens lokalt i AppData så du slipper logga in varje gång
                string tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "token.json");

                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(tokenPath, true));
            }

            return new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
        }

        public async Task<string?> GetNextEventAsync()
        {
            try
            {
                var service = await CreateServiceAsync();
                var now = DateTimeOffset.Now;

                // Alla kalendrar du ser i Google Kalender (huvudkalendern + delade kalendrar från andra konton), inte bara "primary"
                List<CalendarRef> calendars = await GetVisibleCalendarsAsync(service);
                if (calendars.Count == 0)
                {
                    calendars.Add(new CalendarRef("primary", "primary", true)); // aldrig sämre än förut
                }

                // Alla kalendrar hämtas samtidigt; en som misslyckas stoppar inte de andra
                var perCalendar = await Task.WhenAll(calendars.Select(c => FetchUpcomingAsync(service, c, now)));

                var next = CalendarMerge.MergeUpcoming(perCalendar.SelectMany(x => x)).FirstOrDefault();
                if (next != null)
                {
                    return CalendarMerge.Format(next, now, showCalendarName: calendars.Count > 1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Calendar Error: {ex.Message}");
            }

            return null;
        }

        public async Task<string?> GetUpcomingEventsSummaryAsync(int days = 14, int maxEvents = 15)
        {
            if (!HasCredentialsFile()) return null;

            try
            {
                var service = await CreateServiceAsync();
                var now = DateTimeOffset.Now;
                var horizon = now.AddDays(days);

                List<CalendarRef> calendars = await GetVisibleCalendarsAsync(service);
                if (calendars.Count == 0)
                {
                    calendars.Add(new CalendarRef("primary", "primary", true));
                }

                var perCalendar = await Task.WhenAll(calendars.Select(c => FetchUpcomingAsync(service, c, now, 15)));
                var upcoming = CalendarMerge.MergeUpcoming(perCalendar.SelectMany(x => x))
                    .Where(e => e.Start <= horizon)
                    .Take(maxEvents)
                    .ToList();

                if (upcoming.Count == 0)
                {
                    return $"No upcoming Google Calendar events in the next {days} days.";
                }

                var sb = new System.Text.StringBuilder();
                foreach (var item in upcoming)
                {
                    string when = item.AllDay
                        ? item.Start.ToString("yyyy-MM-dd (ddd) 'All day'")
                        : item.Start.ToString("yyyy-MM-dd (ddd) HH:mm");
                    string cal = calendars.Count > 1 && !string.IsNullOrWhiteSpace(item.Calendar.Name)
                        ? $" [{item.Calendar.Name}]"
                        : "";
                    sb.AppendLine($"- {when}: {item.Summary}{cal}");
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Calendar Summary Error: {ex.Message}");
                return null;
            }
        }

        private async Task<List<CalendarRef>> GetVisibleCalendarsAsync(CalendarService service)
        {
            var result = new List<CalendarRef>();

            try
            {
                string? pageToken = null;
                do
                {
                    CalendarListResource.ListRequest request = service.CalendarList.List();
                    request.PageToken = pageToken;
                    CalendarList page = await request.ExecuteAsync();

                    var entries = page.Items;
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                        {
                            string id = entry.Id ?? "";
                            if (!CalendarMerge.ShouldRead(id, entry.Primary, entry.Selected, entry.Hidden, entry.Deleted, entry.AccessRole, IncludeHolidaysAndBirthdays))
                                continue;

                            string name = id;
                            if (!string.IsNullOrWhiteSpace(entry.SummaryOverride)) name = entry.SummaryOverride;
                            else if (!string.IsNullOrWhiteSpace(entry.Summary)) name = entry.Summary;

                            result.Add(new CalendarRef(id, name, entry.Primary == true));
                        }
                    }

                    pageToken = page.NextPageToken;
                } while (!string.IsNullOrEmpty(pageToken));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not list calendars: {ex.Message}");
            }

            return result;
        }

        private static async Task<List<CalendarItem>> FetchUpcomingAsync(CalendarService service, CalendarRef calendar, DateTimeOffset now, int maxResults = 5)
        {
            var items = new List<CalendarItem>();

            try
            {
                // Händelser från och med nu (samma sökning som förut, men per kalender och med några fler träffar att slå ihop)
                EventsResource.ListRequest request = service.Events.List(calendar.Id);
                request.TimeMinDateTimeOffset = now;
                request.ShowDeleted = false;
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.MaxResults = maxResults;

                Events events = await request.ExecuteAsync();
                if (events.Items != null)
                {
                    foreach (var e in events.Items)
                    {
                        var item = ToItem(calendar, e);
                        if (item != null) items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Calendar '{calendar.Name}' could not be read: {ex.Message}");
            }

            return items;
        }

        private static CalendarItem? ToItem(CalendarRef calendar, Event e)
        {
            if (e.Status == "cancelled") return null;

            // En händelse du själv har tackat nej till ska inte visas som "nästa"
            if (e.Attendees != null && e.Attendees.Any(a => a.Self == true && a.ResponseStatus == "declined")) return null;

            DateTimeOffset start;
            bool allDay;

            if (e.Start?.DateTimeDateTimeOffset is DateTimeOffset timed)
            {
                start = timed;
                allDay = false;
            }
            else if (DateTime.TryParse(e.Start?.Date, out DateTime day))
            {
                start = new DateTimeOffset(DateTime.SpecifyKind(day.Date, DateTimeKind.Local));
                allDay = true;
            }
            else
            {
                return null;
            }

            string summary = string.IsNullOrWhiteSpace(e.Summary) ? "(no title)" : e.Summary.Trim();
            return new CalendarItem(calendar, summary, start, allDay, e.ICalUID);
        }

        public async Task<(bool Success, string Message)> AddStructuredEventAsync(
            string title,
            DateTimeOffset start,
            int durationMinutes = 60,
            string? description = null)
        {
            if (string.IsNullOrWhiteSpace(title))
                return (false, "Event title cannot be empty.");

            if (!HasCredentialsFile())
                return (false, "Google Calendar credentials.json not found.");

            try
            {
                var service = await CreateServiceAsync();
                int clampedDuration = Math.Clamp(durationMinutes, 5, 24 * 60);
                DateTimeOffset end = start.AddMinutes(clampedDuration);

                var newEvent = new Event
                {
                    Summary = title.Trim(),
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    Start = new EventDateTime { DateTimeDateTimeOffset = start },
                    End = new EventDateTime { DateTimeDateTimeOffset = end }
                };

                await service.Events.Insert(newEvent, "primary").ExecuteAsync();
                return (true, $"Scheduled \"{title.Trim()}\" on {start:yyyy-MM-dd HH:mm} ({clampedDuration} min).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding structured event: {ex.Message}");
                return (false, $"Failed to add Google Calendar event: {ex.Message}");
            }
        }

        public async Task<bool> AddEventAsync(string input)
        {
            try
            {
                // 1. Tolka datum och tid ur strängen
                (string summary, DateTime startTime) = ParseEventInput(input);

                UserCredential credential;
                string credPath = ResolveCredentialsPath();

                using (var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read))
                {
                    string tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlimeHelper", "token.json");

                    credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        Scopes,
                        "user",
                        CancellationToken.None,
                        new FileDataStore(tokenPath, true));
                }

                var service = new CalendarService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = ApplicationName,
                });

                DateTime end = startTime.AddHours(1); // Standard: 1 timmes möteslängd

                var newEvent = new Event()
                {
                    Summary = summary,
                    Start = new EventDateTime()
                    {
                        DateTimeDateTimeOffset = startTime,
                    },
                    End = new EventDateTime()
                    {
                        DateTimeDateTimeOffset = end,
                    }
                };

                EventsResource.InsertRequest request = service.Events.Insert(newEvent, "primary");
                await request.ExecuteAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding event: {ex.Message}");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "slime_cal_error.txt"), ex.ToString());
                return false;
            }
        }

        private (string summary, DateTime startTime) ParseEventInput(string input)
        {
            DateTime targetDate = DateTime.Now.AddHours(1); // Standard: 1 timme fram om inget anges
            string summary = input;

            // 1. Leta efter klockslag (t.ex. "kl 14:30", "kl 14", "at 14:30", "14:30", "14.30")
            var timeMatch = System.Text.RegularExpressions.Regex.Match(input, @"(?:kl\.?|klockan|at)?\s*(\d{1,2})[:\.](\d{2})|\b(\d{1,2})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (timeMatch.Success)
            {
                int hours = 0;
                int minutes = 0;

                if (!string.IsNullOrEmpty(timeMatch.Groups[1].Value))
                {
                    hours = int.Parse(timeMatch.Groups[1].Value);
                    minutes = int.Parse(timeMatch.Groups[2].Value);
                }
                else if (!string.IsNullOrEmpty(timeMatch.Groups[3].Value))
                {
                    hours = int.Parse(timeMatch.Groups[3].Value);
                    if (input.Contains("kl") || input.Contains("klockan") || input.Contains("at") || (hours > 7 && hours < 23))
                    {
                        minutes = 0;
                    }
                }

                if (hours >= 0 && hours < 24 && minutes >= 0 && minutes < 60)
                {
                    targetDate = new DateTime(targetDate.Year, targetDate.Month, targetDate.Day, hours, minutes, 0);
                }

                // Rensa bort klockslaget ur titeln/sammanfattningen
                summary = timeMatch.Value.Length > 0 ? input.Replace(timeMatch.Value, "").Trim() : summary;
            }

            // 2. Leta efter relativa dagar eller veckodagar (Svenska & Engelska)
            string lowerInput = input.ToLower();
            if (lowerInput.Contains("imorgon") || lowerInput.Contains("tomorrow"))
            {
                targetDate = targetDate.AddDays(1);
                summary = summary.Replace("imorgon", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("tomorrow", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (lowerInput.Contains("i övermorgon") || lowerInput.Contains("övermorgon") || lowerInput.Contains("day after tomorrow"))
            {
                targetDate = targetDate.AddDays(2);
                summary = summary.Replace("i övermorgon", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("övermorgon", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("day after tomorrow", "", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var daysMap = new Dictionary<string, DayOfWeek>
        {
            { "måndag", DayOfWeek.Monday }, { "monday", DayOfWeek.Monday },
            { "tisdag", DayOfWeek.Tuesday }, { "tuesday", DayOfWeek.Tuesday },
            { "onsdag", DayOfWeek.Wednesday }, { "wednesday", DayOfWeek.Wednesday },
            { "torsdag", DayOfWeek.Thursday }, { "thursday", DayOfWeek.Thursday },
            { "fredag", DayOfWeek.Friday }, { "friday", DayOfWeek.Friday },
            { "lördag", DayOfWeek.Saturday }, { "saturday", DayOfWeek.Saturday },
            { "söndag", DayOfWeek.Sunday }, { "sunday", DayOfWeek.Sunday }
        };

                foreach (var pair in daysMap)
                {
                    if (lowerInput.Contains(pair.Key))
                    {
                        int daysToAdd = ((int)pair.Value - (int)DateTime.Now.DayOfWeek + 7) % 7;
                        if (daysToAdd == 0) daysToAdd = 7; // Nästa vecka om det är samma dag
                        targetDate = DateTime.Now.Date.AddDays(daysToAdd).AddHours(targetDate.Hour).AddMinutes(targetDate.Minute);

                        summary = System.Text.RegularExpressions.Regex.Replace(summary, pair.Key, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        break;
                    }
                }
            }

            // Städa upp extra ord (både svenska och engelska) från titeln
            summary = summary.Replace(" på ", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace(" i ", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace(" kl ", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace(" klockan ", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace(" at ", " ", StringComparison.OrdinalIgnoreCase)
                             .Replace(" on ", " ", StringComparison.OrdinalIgnoreCase)
                             .Trim(' ', '"', '-');

            if (string.IsNullOrWhiteSpace(summary)) summary = "Slime Meeting";

            return (summary, targetDate);
        }
    }
}