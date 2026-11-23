using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AP1.Services;

/// <summary>Formula 1 schedules, classifications and circuit statistics from OpenF1 (2023+).</summary>
public sealed class RaceWeekendService(HttpClient http, ApiResultCache cache)
{
    private const string Api = "https://api.openf1.org/v1/";
    private const int OpenF1FirstSeason = 2023;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<List<int>> GetSeasonsAsync() => Task.FromResult(
        Enumerable.Range(OpenF1FirstSeason, Math.Max(1, DateTime.UtcNow.Year - OpenF1FirstSeason + 1))
            .Reverse().ToList());

    public Task<IReadOnlyList<RaceWeekend>> GetSeasonAsync(int year, bool refresh = false)
    {
        if (year < OpenF1FirstSeason || year > DateTime.UtcNow.Year) throw new ArgumentOutOfRangeException(nameof(year));
        return Cached<IReadOnlyList<RaceWeekend>>($"weekends:{year}", ApiResultCache.LifetimeForSeason(year), refresh,
            token => ReadOpenF1SeasonAsync(year, token));
    }

    public Task<IReadOnlyList<WeekendClassification>> GetResultsAsync(RaceWeekend race, bool refresh = false) =>
        Cached<IReadOnlyList<WeekendClassification>>($"weekend-results:{race.Year}:{race.Round}",
            ApiResultCache.LifetimeForSeason(race.Year), refresh, async token =>
            {
                var tables = new List<WeekendClassification>();
                foreach (var (key, name) in new[] { ("Qualifying", "Qualifying"), ("Sprint", "Sprint"), ("Race", "Rennen") })
                {
                    var session = race.Sessions.FirstOrDefault(s => s.Key == key);
                    if (session?.SessionKey is not > 0 || session.HasStarted(DateTimeOffset.UtcNow) != true) continue;
                    var rows = await ReadOpenF1ResultsAsync(session, token);
                    if (rows.Count > 0) tables.Add(new(name, key == "Qualifying", rows));
                }
                return tables;
            });

    public Task<CircuitHistory> GetHistoryAsync(RaceWeekend race, bool refresh = false) =>
        Cached($"circuit-history:{race.CircuitKey}", TimeSpan.FromHours(24), refresh, async token =>
        {
            if (race.CircuitKey <= 0) return new CircuitHistory(null, null, [], [], null);
            var sessions = (await GetAsync<OpenF1CalendarSession>($"sessions?circuit_key={race.CircuitKey}", token))
                .Where(s => !s.IsCancelled && s.DateStart < DateTimeOffset.UtcNow
                    && s.SessionName is "Race" or "Qualifying").ToList();
            if (sessions.Count == 0) return new CircuitHistory(null, null, [], [], null);
            var query = SessionQuery(sessions.Select(s => s.SessionKey));
            var resultsTask = GetAsync<OpenF1Classification>($"session_result?{query}", token);
            var driversTask = GetAsync<OpenF1Driver>($"drivers?{query}", token);
            var lapsTask = GetAsync<OpenF1Lap>($"laps?{query}", token);
            await Task.WhenAll(resultsTask, driversTask, lapsTask);
            var results = await resultsTask;
            var drivers = await driversTask;
            var laps = await lapsTask;
            var sessionByKey = sessions.ToDictionary(s => s.SessionKey);
            var driverNames = drivers.GroupBy(d => d.DriverNumber).ToDictionary(g => g.Key,
                g => g.OrderByDescending(d => d.SessionKey).First().FullName ?? $"Fahrer #{g.Key}");
            var winners = Leaders(results.Where(r => r.Position == 1 && sessionByKey.GetValueOrDefault(r.SessionKey)?.SessionName == "Race"), sessionByKey, driverNames);
            var poles = Leaders(results.Where(r => r.Position == 1 && sessionByKey.GetValueOrDefault(r.SessionKey)?.SessionName == "Qualifying"), sessionByKey, driverNames);
            var circuitLaps = laps.Where(l => l.LapDuration is > 0 && sessionByKey.ContainsKey(l.SessionKey)).Select(l =>
                new CircuitLap(driverNames.GetValueOrDefault(l.DriverNumber, $"Fahrer #{l.DriverNumber}"),
                    sessionByKey[l.SessionKey].Year.ToString(), FormatLap(l.LapDuration!.Value), l.LapDuration.Value,
                    sessionByKey[l.SessionKey].SessionName == "Race" ? "Rennen" : "Qualifying")).ToList();
            var raceLaps = circuitLaps.Where(l => l.Session == "Rennen").ToList();
            return new CircuitHistory(circuitLaps.OrderBy(l => l.Seconds).FirstOrDefault(),
                raceLaps.OrderBy(l => l.Seconds).FirstOrDefault(), winners, poles,
                sessions.Where(s => s.SessionName == "Race").Select(s => s.Year).DefaultIfEmpty().Min() is var first && first > 0 ? first.ToString() : null);
        });

    public Task<IReadOnlyList<CircuitMeetingImage>> GetCircuitImagesAsync(int year, bool refresh = false) =>
        Cached<IReadOnlyList<CircuitMeetingImage>>($"circuit-images:{year}", TimeSpan.FromHours(24), refresh, async token =>
            await GetAsync<CircuitMeetingImage>($"meetings?year={year}", token));

    private async Task<IReadOnlyList<RaceWeekend>> ReadOpenF1SeasonAsync(int year, CancellationToken token)
    {
        var meetings = await GetAsync<OpenF1Meeting>($"meetings?year={year}", token);
        var sessions = await GetAsync<OpenF1CalendarSession>($"sessions?year={year}", token);
        var races = meetings.Where(m => !m.IsCancelled && !(m.MeetingName ?? "").Contains("Testing", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DateStart).Select((meeting, index) =>
            {
                var meetingSessions = sessions.Where(s => s.MeetingKey == meeting.MeetingKey)
                    .OrderBy(s => s.DateStart).Select(ParseOpenF1Session).ToList();
                var race = meetingSessions.FirstOrDefault(s => s.Key == "Race");
                var date = DateOnly.FromDateTime((race?.Start ?? meeting.DateStart).DateTime);
                var circuitId = CircuitArtwork.CircuitId(meeting.CircuitKey) ?? NormalizeCircuitId(meeting.CircuitShortName);
                return new RaceWeekend(year, index + 1, meeting.MeetingName ?? meeting.OfficialName ?? "Grand Prix", circuitId,
                    meeting.CircuitShortName ?? meeting.Location ?? "", meeting.Location ?? "", meeting.CountryName ?? "", date,
                    race?.Start, meetingSessions, ParseOffset(meeting.GmtOffset), meeting.CircuitImage, meeting.CircuitKey, meeting.MeetingKey);
            }).Where(r => r.Sessions.Any(s => s.Key == "Race")).ToList();
        return races;
    }

    private static WeekendSession ParseOpenF1Session(OpenF1CalendarSession session) =>
        new(session.SessionName switch { "Practice 1" => "FirstPractice", "Practice 2" => "SecondPractice", "Practice 3" => "ThirdPractice", "Sprint Qualifying" => "SprintQualifying", "Qualifying" => "Qualifying", "Sprint" => "Sprint", _ => session.SessionName ?? session.SessionType ?? "Session" },
            SessionLabel(session.SessionName ?? session.SessionType ?? "Session"), DateOnly.FromDateTime(session.DateStart.DateTime), session.DateStart, session.SessionKey);

    private async Task<IReadOnlyList<WeekendResult>> ReadOpenF1ResultsAsync(WeekendSession session, CancellationToken token)
    {
        var resultTask = GetAsync<OpenF1Classification>($"session_result?session_key={session.SessionKey}", token);
        var driverTask = GetAsync<OpenF1Driver>($"drivers?session_key={session.SessionKey}", token);
        var gridTask = session.Key is "Race" or "Sprint" ? GetAsync<OpenF1Grid>($"starting_grid?session_key={session.SessionKey}", token) : Task.FromResult(new List<OpenF1Grid>());
        var lapsTask = session.Key is "Race" or "Sprint" ? GetAsync<OpenF1Lap>($"laps?session_key={session.SessionKey}", token) : Task.FromResult(new List<OpenF1Lap>());
        await Task.WhenAll(resultTask, driverTask, gridTask, lapsTask);
        var drivers = (await driverTask).GroupBy(d => d.DriverNumber).ToDictionary(g => g.Key, g => g.Last());
        var grid = (await gridTask).ToDictionary(g => g.DriverNumber, g => g.Position);
        var fastestByDriver = (await lapsTask).Where(l => l.LapDuration is > 0).GroupBy(l => l.DriverNumber)
            .ToDictionary(g => g.Key, g => g.Min(l => l.LapDuration!.Value));
        var fastest = fastestByDriver.Count == 0 ? (double?)null : fastestByDriver.Values.Min();
        return (await resultTask).OrderBy(r => r.Position ?? int.MaxValue).Select(r =>
        {
            drivers.TryGetValue(r.DriverNumber, out var driver);
            var durations = Values(r.Duration);
            var gaps = Values(r.GapToLeader);
            var status = r.Dsq ? "Disqualifiziert" : r.Dns ? "Nicht gestartet" : r.Dnf ? "Ausgefallen" : "Beendet";
            var positionText = r.Dsq ? "D" : r.Dns ? "N" : r.Dnf ? "R" : r.Position?.ToString() ?? "";
            var time = session.Key == "Qualifying" ? "" : r.Position == 1 && durations.Count > 0
                ? FormatDuration(durations[0]) : gaps.Count > 0 ? FormatGap(gaps[0]) : "";
            fastestByDriver.TryGetValue(r.DriverNumber, out var driverFastest);
            return new WeekendResult(r.Position ?? 0, positionText, driver?.FullName ?? $"Fahrer #{r.DriverNumber}",
                driver?.TeamName ?? "", grid.GetValueOrDefault(r.DriverNumber).ToString(CultureInfo.InvariantCulture),
                r.NumberOfLaps?.ToString(CultureInfo.InvariantCulture) ?? "", "", time, status,
                durations.Count > 0 ? FormatLap(durations[0]) : "", durations.Count > 1 ? FormatLap(durations[1]) : "",
                durations.Count > 2 ? FormatLap(durations[2]) : "", driverFastest > 0 ? FormatLap(driverFastest) : "",
                fastest is not null && Math.Abs(driverFastest - fastest.Value) < 0.0005);
        }).ToList();
    }

    private static string NormalizeCircuitId(string? value) => string.IsNullOrWhiteSpace(value)
        ? "unknown" : value.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

    private static TimeSpan? ParseOffset(string? value) => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var offset)
        && offset >= TimeSpan.FromHours(-14) && offset <= TimeSpan.FromHours(14) ? offset : null;

    private Task<T> Cached<T>(string key, TimeSpan lifetime, bool refresh, Func<CancellationToken, Task<T>> load) where T : notnull =>
        refresh ? cache.RefreshAsync(key, lifetime, load) : cache.GetOrLoadAsync(key, lifetime, load);

    private async Task<List<T>> GetAsync<T>(string path, CancellationToken token)
    {
        try
        {
            using var document = await ReadAsync($"{Api}{path}", token);
            return JsonSerializer.Deserialize<List<T>>(document.RootElement.GetRawText(), JsonOptions) ?? [];
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Some optional OpenF1 datasets (for example historical starting grids)
            // are not published for every session. Treat that as unavailable data.
            return [];
        }
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
                await Task.Delay(350, token);
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

    private static IReadOnlyList<CircuitLeader> Leaders(IEnumerable<OpenF1Classification> rows,
        IReadOnlyDictionary<int, OpenF1CalendarSession> sessions, IReadOnlyDictionary<int, string> driverNames) => rows
        .GroupBy(r => r.DriverNumber).Select(g => new CircuitLeader(driverNames.GetValueOrDefault(g.Key, $"Fahrer #{g.Key}"),
            g.Count(), string.Join(", ", g.Select(r => sessions.GetValueOrDefault(r.SessionKey)?.Year ?? 0).Where(y => y > 0).Distinct().Order())))
        .OrderByDescending(r => r.Count).ThenBy(r => r.Driver).ToList();

    private static string SessionQuery(IEnumerable<int> keys) => string.Join("&", keys.Distinct().Select(k => $"session_key={k}"));
    private static string SessionLabel(string value) => value switch
    {
        "Practice 1" => "Freies Training 1", "Practice 2" => "Freies Training 2", "Practice 3" => "Freies Training 3",
        "Sprint Qualifying" => "Sprint-Qualifying", "Race" => "Rennen", _ => value
    };

    private static List<double> Values(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return [number];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToList();
        return [];
    }

    private static string FormatLap(double seconds) => string.Format(CultureInfo.InvariantCulture,
        "{0}:{1:00.000}", (int)(seconds / 60), seconds % 60);
    private static string FormatDuration(double seconds) => seconds >= 3600
        ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        : FormatLap(seconds);
    private static string FormatGap(double seconds) => seconds <= 0 ? "" : "+" + seconds.ToString("0.000", CultureInfo.InvariantCulture);
}

public sealed record RaceWeekend(int Year, int Round, string Name, string CircuitId, string CircuitName, string Location,
    string Country, DateOnly Date, DateTimeOffset? Start, IReadOnlyList<WeekendSession> Sessions,
    TimeSpan? TrackOffset = null, string? CircuitImage = null, int CircuitKey = 0, int MeetingKey = 0)
{
    public string Url => $"/kalender/{Year}/{Round}";
    public string? TrackZone => RaceDisplay.TrackZone(CircuitId);
    // Keep an ongoing Grand Prix visible. Unknown times stay visible throughout the announced day.
    public bool IsPast(DateTimeOffset now) => Start is { } start ? start.AddHours(3) < now : Date < DateOnly.FromDateTime(now.UtcDateTime);
}
public sealed record WeekendSession(string Key, string Name, DateOnly Date, DateTimeOffset? Start, int SessionKey = 0)
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

public sealed record OpenF1Meeting(
    [property: JsonPropertyName("meeting_key")] int MeetingKey,
    [property: JsonPropertyName("meeting_name")] string? MeetingName,
    [property: JsonPropertyName("meeting_official_name")] string? OfficialName,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("country_name")] string? CountryName,
    [property: JsonPropertyName("circuit_key")] int CircuitKey,
    [property: JsonPropertyName("circuit_short_name")] string? CircuitShortName,
    [property: JsonPropertyName("date_start")] DateTimeOffset DateStart,
    [property: JsonPropertyName("gmt_offset")] string? GmtOffset,
    [property: JsonPropertyName("circuit_image")] string? CircuitImage,
    [property: JsonPropertyName("is_cancelled")] bool IsCancelled = false);

public sealed record OpenF1CalendarSession(
    [property: JsonPropertyName("session_key")] int SessionKey,
    [property: JsonPropertyName("meeting_key")] int MeetingKey,
    [property: JsonPropertyName("session_name")] string? SessionName,
    [property: JsonPropertyName("session_type")] string? SessionType,
    [property: JsonPropertyName("date_start")] DateTimeOffset DateStart,
    [property: JsonPropertyName("year")] int Year = 0,
    [property: JsonPropertyName("is_cancelled")] bool IsCancelled = false);

public sealed record OpenF1Classification
{
    [JsonPropertyName("session_key")] public int SessionKey { get; init; }
    [JsonPropertyName("driver_number")] public int DriverNumber { get; init; }
    [JsonPropertyName("position")] public int? Position { get; init; }
    [JsonPropertyName("duration")] public JsonElement Duration { get; init; }
    [JsonPropertyName("gap_to_leader")] public JsonElement GapToLeader { get; init; }
    [JsonPropertyName("number_of_laps")] public int? NumberOfLaps { get; init; }
    [JsonPropertyName("dnf")] public bool Dnf { get; init; }
    [JsonPropertyName("dns")] public bool Dns { get; init; }
    [JsonPropertyName("dsq")] public bool Dsq { get; init; }
}

public sealed record OpenF1Grid
{
    [JsonPropertyName("driver_number")] public int DriverNumber { get; init; }
    [JsonPropertyName("position")] public int Position { get; init; }
}

public sealed record OpenF1Lap
{
    [JsonPropertyName("session_key")] public int SessionKey { get; init; }
    [JsonPropertyName("driver_number")] public int DriverNumber { get; init; }
    [JsonPropertyName("lap_duration")] public double? LapDuration { get; init; }
}

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
