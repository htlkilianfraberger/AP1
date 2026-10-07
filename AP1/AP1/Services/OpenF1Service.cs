using System.Text.Json.Serialization;

namespace AP1.Services;

public sealed class OpenF1Service(HttpClient httpClient)
{
    private const int FirstSupportedSeason = 2023;

    public async Task<DriverChampionshipStandingResult> GetCurrentDriverChampionshipAsync(int year, CancellationToken cancellationToken = default)
    {
        if (year < FirstSupportedSeason || year > DateTime.UtcNow.Year) throw new ArgumentOutOfRangeException(nameof(year));
        {
            var raceSessions = await httpClient.GetFromJsonAsync<List<OpenF1Session>>(
                $"sessions?year={year}&session_type=Race",
                cancellationToken) ?? [];

            var completed = raceSessions.Where(s => !s.IsCancelled && s.SessionName == "Race" && s.DateEnd < DateTimeOffset.UtcNow).OrderBy(s => s.DateStart).ToList();
            foreach (var latestRaceSession in completed.AsEnumerable().Reverse())
            {
                var championship = await httpClient.GetFromJsonAsync<List<OpenF1DriverChampionship>>($"championship_drivers?session_key={latestRaceSession.SessionKey}", cancellationToken) ?? [];
                if (championship.Count == 0) continue;

                var drivers = await httpClient.GetFromJsonAsync<List<OpenF1Driver>>(
                    $"drivers?session_key={latestRaceSession.SessionKey}",
                    cancellationToken) ?? [];

                var driverDetailsByNumber = drivers
                    .GroupBy(driver => driver.DriverNumber)
                    .ToDictionary(group => group.Key, group => group.First());

                // Substitute drivers can retain championship points without entering the latest race.
                foreach (var previous in completed.Where(s => s.DateStart < latestRaceSession.DateStart).Reverse())
                {
                    if (championship.All(d => driverDetailsByNumber.ContainsKey(d.DriverNumber))) break;
                    await Task.Delay(350, cancellationToken);
                    var previousDrivers = await httpClient.GetFromJsonAsync<List<OpenF1Driver>>($"drivers?session_key={previous.SessionKey}", cancellationToken) ?? [];
                    foreach (var driver in previousDrivers) driverDetailsByNumber.TryAdd(driver.DriverNumber, driver);
                }

                var history = new Dictionary<int, List<OpenF1RaceResult>>();
                var races = completed.Where(s => s.DateStart <= latestRaceSession.DateStart).ToList();
                var statisticsAvailable = true;
                foreach (var race in races)
                {
                    try
                    {
                        // Pace requests to stay below OpenF1's public rate limit.
                        await Task.Delay(350, cancellationToken);
                        var results = await httpClient.GetFromJsonAsync<List<OpenF1RaceResult>>($"session_result?session_key={race.SessionKey}", cancellationToken) ?? [];
                        history[race.SessionKey] = results;
                        if (results.Count == 0) statisticsAvailable = false;
                    }
                    catch (HttpRequestException) { statisticsAvailable = false; }
                }
                var standings = championship
                    .OrderBy(driver => driver.PositionCurrent)
                    .Select(driver =>
                    {
                        driverDetailsByNumber.TryGetValue(driver.DriverNumber, out var driverDetails);

                        return new DriverChampionshipStanding(
                            driver.PositionCurrent,
                            driver.DriverNumber,
                            driverDetails?.FullName ?? $"Fahrer #{driver.DriverNumber}",
                            driverDetails?.NameAcronym ?? driver.DriverNumber.ToString(),
                            driverDetails?.TeamName ?? "Unbekannt",
                            NormalizeTeamColour(driverDetails?.TeamColour),
                            driver.PointsCurrent,
                            driver.PointsCurrent - driver.PointsStart,
                            driver.PositionStart - driver.PositionCurrent,
                            driverDetails?.HeadshotUrl,
                            statisticsAvailable ? history.Values.SelectMany(x => x).Count(x => x.DriverNumber == driver.DriverNumber && x.Position == 1 && !x.Dsq) : null,
                            statisticsAvailable ? history.Values.SelectMany(x => x).Count(x => x.DriverNumber == driver.DriverNumber && x.Position is >= 1 and <= 3 && !x.Dsq) : null,
                            races.TakeLast(5).Select(r => history.GetValueOrDefault(r.SessionKey)?.FirstOrDefault(x => x.DriverNumber == driver.DriverNumber)?.Label ?? "–").ToList());
                    })
                    .ToList();

                return new DriverChampionshipStandingResult(latestRaceSession, standings, races.Count,
                    raceSessions.Count(s => !s.IsCancelled && s.SessionName == "Race"),
                    raceSessions.Where(s => !s.IsCancelled && s.SessionName == "Race" && s.DateStart > DateTimeOffset.UtcNow).OrderBy(s => s.DateStart).FirstOrDefault(), statisticsAvailable);
            }
        }

        return new DriverChampionshipStandingResult(null, []);
    }

    private static string NormalizeTeamColour(string? teamColour)
    {
        if (string.IsNullOrWhiteSpace(teamColour))
        {
            return "#8a8f98";
        }

        var trimmed = teamColour.Trim().TrimStart('#');
        return trimmed.Length == 6 && trimmed.All(Uri.IsHexDigit) ? $"#{trimmed}" : "#8a8f98";
    }
}

public sealed record DriverChampionshipStandingResult(
    OpenF1Session? Session,
    IReadOnlyList<DriverChampionshipStanding> Standings,
    int CompletedRaces = 0, int TotalRaces = 0, OpenF1Session? NextRace = null, bool StatisticsAvailable = false);

public sealed record DriverChampionshipStanding(
    int Position,
    int DriverNumber,
    string DriverName,
    string Acronym,
    string TeamName,
    string TeamColour,
    decimal Points,
    decimal PointsGained,
    int PositionsGained,
    string? HeadshotUrl, int? Wins, int? Podiums, IReadOnlyList<string> RecentResults);

public sealed record OpenF1RaceResult
{
    [JsonPropertyName("driver_number")] public int DriverNumber { get; init; }
    [JsonPropertyName("position")] public int? Position { get; init; }
    [JsonPropertyName("dnf")] public bool Dnf { get; init; }
    [JsonPropertyName("dns")] public bool Dns { get; init; }
    [JsonPropertyName("dsq")] public bool Dsq { get; init; }
    public string Label => Dsq ? "DSQ" : Dns ? "DNS" : Dnf ? "DNF" : Position?.ToString() ?? "–";
}

public sealed record OpenF1DriverChampionship
{
    [JsonPropertyName("driver_number")]
    public int DriverNumber { get; init; }

    [JsonPropertyName("points_current")]
    public decimal PointsCurrent { get; init; }

    [JsonPropertyName("points_start")]
    public decimal PointsStart { get; init; }

    [JsonPropertyName("position_current")]
    public int PositionCurrent { get; init; }

    [JsonPropertyName("position_start")]
    public int PositionStart { get; init; }
}

public sealed record OpenF1Driver
{
    [JsonPropertyName("headshot_url")]
    public string? HeadshotUrl { get; init; }
    [JsonPropertyName("driver_number")]
    public int DriverNumber { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    [JsonPropertyName("name_acronym")]
    public string? NameAcronym { get; init; }

    [JsonPropertyName("team_colour")]
    public string? TeamColour { get; init; }

    [JsonPropertyName("team_name")]
    public string? TeamName { get; init; }
}

public sealed record OpenF1Session
{
    [JsonPropertyName("date_end")]
    public DateTimeOffset? DateEnd { get; init; }
    [JsonPropertyName("session_key")]
    public int SessionKey { get; init; }

    [JsonPropertyName("meeting_key")]
    public int MeetingKey { get; init; }

    [JsonPropertyName("session_name")]
    public string SessionName { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("country_name")]
    public string CountryName { get; init; } = string.Empty;

    [JsonPropertyName("date_start")]
    public DateTimeOffset DateStart { get; init; }

    [JsonPropertyName("year")]
    public int Year { get; init; }

    [JsonPropertyName("is_cancelled")]
    public bool IsCancelled { get; init; }
}
