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

        public async Task<string?> GetNextEventAsync()
        {
            try
            {
                UserCredential credential;
                string credPath = "credentials.json";

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

                // Skapa kalendertjänsten
                var service = new CalendarService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = ApplicationName,
                });

                // Hämta händelser från primära kalendern från och med nu
                EventsResource.ListRequest request = service.Events.List("primary");
                request.TimeMinDateTimeOffset = DateTime.Now;
                request.ShowDeleted = false;
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.MaxResults = 1;

                Events events = await request.ExecuteAsync();
                var items = events.Items;

                if (items != null && items.Count > 0)
                {
                    var nextEvent = items[0];
                    string when = nextEvent.Start.DateTimeDateTimeOffset?.ToString("HH:mm") ?? nextEvent.Start.Date;
                    return $"{nextEvent.Summary} kl {when}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Calendar Error: {ex.Message}");
            }

            return null;
        }

        public async Task<bool> AddEventAsync(string input)
        {
            try
            {
                // 1. Tolka datum och tid ur strängen
                (string summary, DateTime startTime) = ParseEventInput(input);

                UserCredential credential;
                string credPath = "credentials.json";

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