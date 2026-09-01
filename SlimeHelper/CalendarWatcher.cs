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

        public async Task<bool> AddEventAsync(string summary, DateTime? startTime = null)
        {
            try
            {
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

                DateTime start = startTime ?? DateTime.Now.AddHours(1); // Standard: 1 timme fram i tiden om inget annat anges
                DateTime end = start.AddHours(1); // Standard: 1 timmes möteslängd

                var newEvent = new Event()
                {
                    Summary = summary,
                    Start = new EventDateTime()
                    {
                        DateTimeDateTimeOffset = start,
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
    }
}