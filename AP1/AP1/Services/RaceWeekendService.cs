using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AP1.Services;

/// <summary>Published season schedules and classifications from Jolpica, circuit images from OpenF1.</summary>
public sealed class RaceWeekendService(HttpClient http, ApiResultCache cache)
{
    private const string Api = "https://api.jolpi.ca/ergast/f1/";
    private static readonly SemaphoreSlim RequestGate = new(1, 1);

    public Task<List<int>> GetSeasonsAsync() =>
        cache.GetOrLoadAsync("race-seasons", TimeSpan.FromHours(24), async token =>
        {
            var years = new List<int>();
            var offset = 0;
            while (true)
            {
                using var doc = await ReadAsync($"{Api}seasons.json?limit=100&offset={offset}", token);
                var data = doc.RootElement.GetProperty("MRData");
                var page = data.GetProperty("SeasonTable").GetProperty("Seasons");
                years.AddRange(page.EnumerateArray().Select(s => Number(s, "season")));
                offset += page.GetArrayLength();
                if (offset >= Number(data, "total")) break;
                if (page.GetArrayLength() == 0) throw new JsonException("Incomplete season index.");
            }
            return years.Where(year => year > 0).Distinct().OrderDescending().ToList();
        });

    public Task<IReadOnlyList<RaceWeekend>> GetSeasonAsync(int year, bool refresh = false)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        return Cached<IReadOnlyList<RaceWeekend>>($"weekends:{year}", ApiResultCache.LifetimeForSeason(year), refresh, async token =>
        {
            var races = (await ReadRacesAsync($"{year}.json", null, token)).Select(ParseWeekend).OrderBy(r => r.Date).ToList();
            if (year < 2023) return races; // OpenF1 meeting metadata starts in 2023.
            IReadOnlyList<CircuitMeetingImage> meetings = [];
            try { meetings = await GetCircuitImagesAsync(year, refresh); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { /* Schedule remains usable without optional track metadata. */ }
            return races.Select(r =>
            {
                var meeting = meetings.FirstOrDefault(m => r.Date >= DateOnly.FromDateTime(m.Start.DateTime)
                    && r.Date <= DateOnly.FromDateTime(m.End.DateTime) && !(m.Name ?? "").Contains("Testing", StringComparison.OrdinalIgnoreCase)
                    && (CircuitArtwork.CircuitId(m.CircuitKey) is not { Length: > 0 } id || id == r.CircuitId));
                var offset = TimeSpan.TryParse(meeting?.GmtOffset, CultureInfo.InvariantCulture, out var parsed)
                    && parsed >= TimeSpan.FromHours(-14) && parsed <= TimeSpan.FromHours(14) ? (TimeSpan?)parsed : null;
                return r with { TrackOffset = offset, CircuitImage = meeting?.Image };
            }).ToList();
        });
    }

    public Task<IReadOnlyList<WeekendClassification>> GetResultsAsync(RaceWeekend race, bool refresh = false) =>
        Cached<IReadOnlyList<WeekendClassification>>($"weekend-results:{race.Year}:{race.Round}",
            ApiResultCache.LifetimeForSeason(race.Year), refresh, async token =>
            {
                var tables = new List<WeekendClassification>();
                foreach (var (key, path, name) in new[] {
                    ("Qualifying", "qualifying", "Qualifying"), ("Sprint", "sprint", "Sprint"), ("Race", "results", "Rennen") })
                {
                    var session = race.Sessions.FirstOrDefault(s => s.Key == key);
                    // Older schedules can omit a session even when its classification exists.
                    var archiveLookup = session is null && race.Year < DateTime.UtcNow.Year
                        && ((key == "Qualifying" && race.Year >= 1994) || (key == "Sprint" && race.Year >= 2021));
                    if (!archiveLookup && session?.HasStarted(DateTimeOffset.UtcNow) != true) continue;
                    var field = path == "qualifying" ? "QualifyingResults" : path == "sprint" ? "SprintResults" : "Results";
                    var rows = await ReadRacesAsync($"{race.Year}/{race.Round}/{path}.json", field, token);
                    if (archiveLookup && rows.Count == 0) continue;
                    tables.Add(new(name, path == "qualifying", rows.SelectMany(r => Items(r, field)).Select(ParseResult)
                        .OrderBy(r => r.Position).ToList()));
                }
                return tables;
            });

    public Task<CircuitHistory> GetHistoryAsync(string circuitId, bool refresh = false) =>
        Cached($"circuit-history:{circuitId}", TimeSpan.FromHours(24), refresh, async token =>
        {
            var path = $"circuits/{Uri.EscapeDataString(circuitId)}";
            var winners = await ReadRacesAsync($"{path}/results/1.json", "Results", token);
            var poles = await ReadRacesAsync($"{path}/grid/1/results.json", "Results", token);
            var fastest = await ReadRacesAsync($"{path}/fastest/1/results.json", "Results", token);
            // Q1/Q2 can be faster than Q3, including for a driver who did not take pole.
            var qualifying = await ReadRacesAsync($"{path}/qualifying.json", "QualifyingResults", token);
            var raceLaps = fastest.SelectMany(r => Items(r, "Results").Select(result =>
                Lap(result, Text(r, "season"), Text(result, "FastestLap", "Time", "time"), "Rennen")))
                .OfType<CircuitLap>().ToList();
            var qualifyingLaps = qualifying.SelectMany(r => Items(r, "QualifyingResults").SelectMany(result =>
                new[] { "Q1", "Q2", "Q3" }.Select(q => Lap(result, Text(r, "season"), Text(result, q), q))))
                .OfType<CircuitLap>();
            return new CircuitHistory(
                qualifyingLaps.Concat(raceLaps).OrderBy(l => l.Seconds).FirstOrDefault(),
                raceLaps.OrderBy(l => l.Seconds).FirstOrDefault(), Leaders(winners), Leaders(poles),
                winners.Select(r => Text(r, "season")).Distinct().Order().FirstOrDefault());
        });

    public Task<IReadOnlyList<CircuitMeetingImage>> GetCircuitImagesAsync(int year, bool refresh = false) =>
        Cached<IReadOnlyList<CircuitMeetingImage>>($"circuit-images:{year}", TimeSpan.FromHours(24), refresh, async token =>
            await http.GetFromJsonAsync<List<CircuitMeetingImage>>($"https://api.openf1.org/v1/meetings?year={year}", token) ?? []);

    private Task<T> Cached<T>(string key, TimeSpan lifetime, bool refresh, Func<CancellationToken, Task<T>> load) where T : notnull =>
        refresh ? cache.RefreshAsync(key, lifetime, load) : cache.GetOrLoadAsync(key, lifetime, load);

    private async Task<List<JsonElement>> ReadRacesAsync(string path, string? resultsField, CancellationToken token)
    {
        var races = new List<JsonElement>();
        var offset = 0;
        while (true)
        {
            using var document = await ReadAsync($"{Api}{path}?limit=100&offset={offset}", token);
            var data = document.RootElement.GetProperty("MRData");
            var page = Items(data.GetProperty("RaceTable"), "Races").ToList();
            races.AddRange(page.Select(r => r.Clone()));
            var count = resultsField is null ? page.Count : page.Sum(r => Items(r, resultsField).Count());
            var total = Number(data, "total");
            offset += count;
            if (offset >= total) break;
            if (count == 0) throw new JsonException("Incomplete paginated race data.");
        }
        return races;
    }

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await RequestGate.WaitAsync(token);
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("AP1-F1Dashboard/1.0");
                response = await http.SendAsync(request, token);
                await Task.Delay(275, token);
            }
            finally { RequestGate.Release(); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    if (delay > TimeSpan.FromSeconds(15)) response.EnsureSuccessStatusCode();
                    await Task.Delay(delay, token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            }
        }
    }

    private static RaceWeekend ParseWeekend(JsonElement r)
    {
        var sessions = new List<WeekendSession>();
        foreach (var (key, name) in new[] { ("FirstPractice", "Freies Training 1"), ("SecondPractice", "Freies Training 2"),
            ("ThirdPractice", "Freies Training 3"), ("SprintQualifying", "Sprint-Qualifying"),
            ("SprintShootout", "Sprint-Qualifying"), ("Sprint", "Sprint"), ("Qualifying", "Qualifying"), ("Race", "Rennen") })
        {
            var source = key == "Race" ? r : r.TryGetProperty(key, out var value) ? value : default;
            var date = Text(source, "date");
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            var time = Text(source, "time");
            DateTimeOffset? start = !string.IsNullOrEmpty(time) && DateTimeOffset.TryParse($"{date}T{time}", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            sessions.Add(new(key, name, day, start));
        }
        var race = sessions.Single(s => s.Key == "Race");
        return new(Number(r, "season"), Number(r, "round"), Text(r, "raceName"), Text(r, "Circuit", "circuitId"),
            Text(r, "Circuit", "circuitName"), Text(r, "Circuit", "Location", "locality"), Text(r, "Circuit", "Location", "country"),
            race.Date, race.Start, sessions.OrderBy(s => s.Start ?? new DateTimeOffset(s.Date.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero)).ToList());
    }

    private static WeekendResult ParseResult(JsonElement r) => new(
        Number(r, "position"), Text(r, "positionText"), Driver(r), Text(r, "Constructor", "name"), Text(r, "grid"),
        Text(r, "laps"), Text(r, "points"), Text(r, "Time", "time"), Text(r, "status"),
        Text(r, "Q1"), Text(r, "Q2"), Text(r, "Q3"), Text(r, "FastestLap", "Time", "time"), Text(r, "FastestLap", "rank") == "1");

    private static IReadOnlyList<CircuitLeader> Leaders(List<JsonElement> races) => races
        .SelectMany(r => Items(r, "Results").Select(result => new { Id = Text(result, "Driver", "driverId"), Name = Driver(result), Year = Text(r, "season") }))
        .GroupBy(r => r.Id).Select(g => new CircuitLeader(g.First().Name, g.Count(), string.Join(", ", g.Select(r => r.Year).Distinct().Order())))
        .OrderByDescending(r => r.Count).ThenBy(r => r.Driver).ToList();

    private static CircuitLap? Lap(JsonElement result, string year, string time, string session)
    {
        var parts = time.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var seconds) || minutes * 60 + seconds <= 0) return null;
        return new(Driver(result), year, time, minutes * 60 + seconds, session);
    }

    private static string Driver(JsonElement r) => $"{Text(r, "Driver", "givenName")} {Text(r, "Driver", "familyName")}".Trim();
    private static int Number(JsonElement r, string field) => int.TryParse(Text(r, field), out var n) ? n : 0;
    private static IEnumerable<JsonElement> Items(JsonElement r, string field) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(field, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string Text(JsonElement r, params string[] path)
    {
        foreach (var part in path)
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(part, out r)) return "";
        return r.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : r.ToString();
    }
}

public sealed record RaceWeekend(int Year, int Round, string Name, string CircuitId, string CircuitName, string Location,
    string Country, DateOnly Date, DateTimeOffset? Start, IReadOnlyList<WeekendSession> Sessions,
    TimeSpan? TrackOffset = null, string? CircuitImage = null)
{
    public string Url => $"/kalender/{Year}/{Round}";
    public string? TrackZone => RaceDisplay.TrackZone(CircuitId);
    // Keep an ongoing Grand Prix visible. Unknown times stay visible throughout the announced day.
    public bool IsPast(DateTimeOffset now) => Start is { } start ? start.AddHours(3) < now : Date < DateOnly.FromDateTime(now.UtcDateTime);
}
public sealed record WeekendSession(string Key, string Name, DateOnly Date, DateTimeOffset? Start)
{
    public bool HasStarted(DateTimeOffset now) => Start is { } start ? start <= now : Date < DateOnly.FromDateTime(now.UtcDateTime);
}
public sealed record WeekendClassification(string Name, bool IsQualifying, IReadOnlyList<WeekendResult> Results);
public sealed record WeekendResult(int Position, string PositionText, string Driver, string Team, string Grid, string Laps,
    string Points, string Time, string Status, string Q1, string Q2, string Q3, string FastestLap, bool IsFastestLap);
public sealed record CircuitLap(string Driver, string Year, string Time, double Seconds, string Session);
public sealed record CircuitLeader(string Driver, int Count, string Years);
public sealed record CircuitHistory(CircuitLap? FastestLap, CircuitLap? FastestRaceLap, IReadOnlyList<CircuitLeader> Wins,
    IReadOnlyList<CircuitLeader> Poles, string? FirstYear);
public sealed record CircuitMeetingImage(
    [property: JsonPropertyName("date_start")] DateTimeOffset Start,
    [property: JsonPropertyName("date_end")] DateTimeOffset End,
    [property: JsonPropertyName("circuit_image")] string? Image,
    [property: JsonPropertyName("meeting_name")] string? Name,
    [property: JsonPropertyName("gmt_offset")] string? GmtOffset,
    [property: JsonPropertyName("circuit_key")] int CircuitKey = 0);

public static class RaceDisplay
{
    private static readonly IReadOnlyDictionary<string, string> TrackZones = new Dictionary<string, string>
    {
        ["adelaide"] = "Australia/Adelaide", ["ain-diab"] = "Africa/Casablanca", ["aintree"] = "Europe/London",
        ["anderstorp"] = "Europe/Stockholm", ["avus"] = "Europe/Berlin", ["boavista"] = "Europe/Lisbon",
        ["brands_hatch"] = "Europe/London", ["bremgarten"] = "Europe/Zurich", ["buddh"] = "Asia/Kolkata",
        ["charade"] = "Europe/Paris", ["dallas"] = "America/Chicago", ["detroit"] = "America/Detroit",
        ["dijon"] = "Europe/Paris", ["donington"] = "Europe/London", ["essarts"] = "Europe/Paris",
        ["estoril"] = "Europe/Lisbon", ["fuji"] = "Asia/Tokyo", ["galvez"] = "America/Argentina/Buenos_Aires",
        ["george"] = "Africa/Johannesburg", ["hockenheimring"] = "Europe/Berlin", ["indianapolis"] = "America/Indiana/Indianapolis",
        ["jacarepagua"] = "America/Sao_Paulo", ["jarama"] = "Europe/Madrid", ["jerez"] = "Europe/Madrid",
        ["kyalami"] = "Africa/Johannesburg", ["lemans"] = "Europe/Paris", ["long_beach"] = "America/Los_Angeles",
        ["magny_cours"] = "Europe/Paris", ["monsanto"] = "Europe/Lisbon", ["montjuic"] = "Europe/Madrid",
        ["mosport"] = "America/Toronto", ["nivelles"] = "Europe/Brussels", ["okayama"] = "Asia/Tokyo",
        ["pedralbes"] = "Europe/Madrid", ["pescara"] = "Europe/Rome", ["phoenix"] = "America/Phoenix",
        ["reims"] = "Europe/Paris", ["ricard"] = "Europe/Paris", ["riverside"] = "America/Los_Angeles",
        ["sebring"] = "America/New_York", ["sochi"] = "Europe/Moscow", ["tremblant"] = "America/Toronto",
        ["valencia"] = "Europe/Madrid", ["watkins_glen"] = "America/New_York", ["yeongam"] = "Asia/Seoul",
        ["zeltweg"] = "Europe/Vienna", ["zolder"] = "Europe/Brussels",
        ["sepang"] = "Asia/Kuala_Lumpur",
        ["albert_park"] = "Australia/Melbourne", ["shanghai"] = "Asia/Shanghai", ["suzuka"] = "Asia/Tokyo",
        ["bahrain"] = "Asia/Bahrain", ["jeddah"] = "Asia/Riyadh", ["miami"] = "America/New_York",
        ["villeneuve"] = "America/Toronto", ["imola"] = "Europe/Rome", ["monaco"] = "Europe/Monaco",
        ["catalunya"] = "Europe/Madrid", ["madring"] = "Europe/Madrid", ["madrid"] = "Europe/Madrid",
        ["red_bull_ring"] = "Europe/Vienna", ["silverstone"] = "Europe/London", ["hungaroring"] = "Europe/Budapest",
        ["spa"] = "Europe/Brussels", ["zandvoort"] = "Europe/Amsterdam", ["monza"] = "Europe/Rome",
        ["baku"] = "Asia/Baku", ["marina_bay"] = "Asia/Singapore", ["americas"] = "America/Chicago",
        ["rodriguez"] = "America/Mexico_City", ["interlagos"] = "America/Sao_Paulo",
        ["vegas"] = "America/Los_Angeles", ["las_vegas"] = "America/Los_Angeles", ["losail"] = "Asia/Qatar",
        ["yas_marina"] = "Asia/Dubai", ["portimao"] = "Europe/Lisbon", ["istanbul"] = "Europe/Istanbul",
        ["paul_ricard"] = "Europe/Paris", ["nurburgring"] = "Europe/Berlin", ["mugello"] = "Europe/Rome"
    };
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-AT");
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    public static string? TrackZone(string circuitId) => TrackZones.GetValueOrDefault(circuitId);
    public static DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);
    private static DateTimeOffset InZone(DateTimeOffset time, bool trackTime, TimeSpan? trackOffset, string? trackZone) =>
        trackTime ? trackZone is not null ? TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById(trackZone))
            : time.ToOffset(trackOffset ?? TimeSpan.Zero) : Local(time);
    public static DateOnly RaceDate(RaceWeekend race, bool trackTime) => race.Start is { } time
        ? DateOnly.FromDateTime(InZone(time, trackTime, race.TrackOffset, race.TrackZone).DateTime) : race.Date;
    public static string SessionTime(WeekendSession session, bool trackTime = false, TimeSpan? trackOffset = null, string? trackZone = null) => session.Start is { } time
        ? InZone(time, trackTime, trackOffset, trackZone).ToString("ddd, dd.MM. · HH:mm", Culture)
            + (trackTime && trackOffset is null && trackZone is null ? " UTC (Streckenzeit unbekannt)" : "")
        : $"{session.Date:dd.MM.yyyy} · {(session.Date < DateOnly.FromDateTime(DateTime.UtcNow) ? "Uhrzeit nicht überliefert" : "Uhrzeit noch offen")}";
    public static string OffsetLabel(TimeSpan? offset) => offset is { } value
        ? $"UTC{(value < TimeSpan.Zero ? "−" : "+")}{value.Duration():hh\\:mm}" : "UTC · Streckenzeit unbekannt";
    public static ApiLoadError Error(Exception exception) => exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
        ? new("Zu viele Anfragen", "Die Datenquelle begrenzt gerade die Anfragen. Bitte versuche es in Kürze erneut.")
        : new("Daten nicht verfügbar", "Die Renndaten konnten nicht geladen werden. Bitte versuche es erneut.");
}
