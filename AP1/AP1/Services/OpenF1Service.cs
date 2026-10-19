using System.Text.Json.Serialization;

namespace AP1.Services;

public sealed class OpenF1Service(HttpClient httpClient, ApiResultCache? cache = null)
{
    private const int FirstSupportedSeason = 2023;

    public Task<DriverChampionshipStandingResult> GetCurrentDriverChampionshipAsync(int year, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (year < FirstSupportedSeason || year > DateTime.UtcNow.Year) throw new ArgumentOutOfRangeException(nameof(year));
        if (cache is not null && forceRefresh)
            return cache.RefreshAsync($"driver-championship:{year}", ApiResultCache.LifetimeForSeason(year),
                token => LoadDriverChampionshipAsync(year, token), cancellationToken);
        return cache is null
            ? LoadDriverChampionshipAsync(year, cancellationToken)
            : cache.GetOrLoadAsync($"driver-championship:{year}", ApiResultCache.LifetimeForSeason(year),
                token => LoadDriverChampionshipAsync(year, token), cancellationToken);
    }

    private async Task<DriverChampionshipStandingResult> LoadDriverChampionshipAsync(int year, CancellationToken cancellationToken)
    {
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

    public Task<TeamChampionshipStandingResult> GetCurrentTeamChampionshipAsync(int year, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (year < FirstSupportedSeason || year > DateTime.UtcNow.Year) throw new ArgumentOutOfRangeException(nameof(year));
        if (cache is not null && forceRefresh)
            return cache.RefreshAsync($"team-championship:{year}", ApiResultCache.LifetimeForSeason(year),
                token => LoadTeamChampionshipAsync(year, token), cancellationToken);
        return cache is null
            ? LoadTeamChampionshipAsync(year, cancellationToken)
            : cache.GetOrLoadAsync($"team-championship:{year}", ApiResultCache.LifetimeForSeason(year),
                token => LoadTeamChampionshipAsync(year, token), cancellationToken);
    }

    private async Task<TeamChampionshipStandingResult> LoadTeamChampionshipAsync(int year, CancellationToken cancellationToken)
    {
        var sessions = await httpClient.GetFromJsonAsync<List<OpenF1Session>>($"sessions?year={year}&session_type=Race", cancellationToken) ?? [];
        var completed = sessions.Where(s => !s.IsCancelled && s.SessionName == "Race" && s.DateEnd < DateTimeOffset.UtcNow).OrderBy(s => s.DateStart).ToList();
        foreach (var race in completed.AsEnumerable().Reverse())
        {
            await Task.Delay(350, cancellationToken);
            var teams = await httpClient.GetFromJsonAsync<List<OpenF1TeamChampionship>>($"championship_teams?session_key={race.SessionKey}", cancellationToken) ?? [];
            if (teams.Count == 0) continue;
            var races = completed.Where(s => s.DateStart <= race.DateStart).ToList();
            var range = $"session_key%3E={races.Min(s => s.SessionKey)}&session_key%3C={races.Max(s => s.SessionKey)}";
            // Fetch the season in two batches; use session keys to exclude practice, qualifying and sprints.
            var drivers = await GetTeamDataAsync<OpenF1Driver>($"drivers?{range}", cancellationToken);
            var results = await GetTeamDataAsync<OpenF1RaceResult>($"session_result?{range}", cancellationToken, races.Select(r => r.SessionKey).ToHashSet());
            var driversByRace = drivers.GroupBy(d => d.SessionKey).ToDictionary(g => g.Key, g => g.ToList());
            var resultsByRace = results.GroupBy(r => r.SessionKey).ToDictionary(g => g.Key, g => g.ToList());
            var statsAvailable = races.All(r => resultsByRace.TryGetValue(r.SessionKey, out var finishers)
                && finishers.Count > 0 && driversByRace.TryGetValue(r.SessionKey, out var entrants)
                && finishers.All(f => entrants.Any(d => d.DriverNumber == f.DriverNumber && !string.IsNullOrWhiteSpace(d.TeamName))));

            var standings = teams.OrderBy(t => t.PositionCurrent).Select(team =>
            {
                var key = TeamIdentity.Key(team.TeamName);
                var latestDrivers = driversByRace.GetValueOrDefault(race.SessionKey, [])
                    .Where(d => TeamIdentity.Key(d.TeamName ?? "") == key).ToList();
                var colour = latestDrivers.Select(d => d.TeamColour).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                var teamResults = races.Select(r =>
                {
                    var numbers = driversByRace.GetValueOrDefault(r.SessionKey, [])
                        .Where(d => TeamIdentity.Key(d.TeamName ?? "") == key).Select(d => d.DriverNumber).ToHashSet();
                    return resultsByRace.GetValueOrDefault(r.SessionKey, []).Where(f => numbers.Contains(f.DriverNumber)).ToList();
                }).ToList();
                var allResults = teamResults.SelectMany(r => r).ToList();
                var recent = races.TakeLast(5).Zip(teamResults.TakeLast(5), (r, finishes) =>
                {
                    // Show the team's best classified finish; the tooltip includes both cars.
                    var best = finishes.Where(f => !f.Dsq && !f.Dns && !f.Dnf && f.Position > 0).OrderBy(f => f.Position).FirstOrDefault();
                    var label = best?.Label ?? (finishes.Count == 0 ? "–" : finishes.Any(f => f.Dnf) ? "DNF" : finishes.Any(f => f.Dsq) ? "DSQ" : "DNS");
                    var detail = finishes.Count == 0 ? "Ergebnisse nicht verfügbar" : string.Join(" · ", finishes.OrderBy(f => f.DriverNumber).Select(f => $"#{f.DriverNumber}: {f.Label}"));
                    return new TeamRecentResult(label, $"{r.Location}: {detail}");
                }).ToList();
                return new TeamChampionshipStanding(team.PositionCurrent, team.TeamName,
                    string.IsNullOrWhiteSpace(colour) ? TeamIdentity.Colour(team.TeamName) : NormalizeTeamColour(colour),
                    team.PointsCurrent, team.PointsCurrent - team.PointsStart, team.PositionStart - team.PositionCurrent,
                    statsAvailable ? allResults.Count(f => f.Position == 1 && !f.Dsq) : null,
                    statsAvailable ? allResults.Count(f => f.Position is >= 1 and <= 3 && !f.Dsq) : null,
                    recent, string.Join(" / ", latestDrivers.Select(d => d.NameAcronym ?? d.FullName).Distinct()));
            }).ToList();
            return new TeamChampionshipStandingResult(race, standings, races.Count,
                sessions.Count(s => !s.IsCancelled && s.SessionName == "Race"),
                sessions.Where(s => !s.IsCancelled && s.SessionName == "Race" && s.DateStart > DateTimeOffset.UtcNow).OrderBy(s => s.DateStart).FirstOrDefault(), statsAvailable);
        }
        return new TeamChampionshipStandingResult(null, []);
    }

    private async Task<List<T>> GetTeamDataAsync<T>(string path, CancellationToken cancellationToken, HashSet<int>? raceKeys = null)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await Task.Delay(400, cancellationToken);
                using var response = await httpClient.GetAsync(path, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (raceKeys is null) return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken) ?? [];
                using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                return document.RootElement.EnumerateArray()
                    .Where(row => raceKeys.Contains(row.GetProperty("session_key").GetInt32()))
                    .Select(row => System.Text.Json.JsonSerializer.Deserialize<T>(row, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!)
                    .ToList();
            }
        }
        catch (HttpRequestException) { return []; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
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
    [JsonPropertyName("session_key")] public int SessionKey { get; init; }
    [JsonPropertyName("driver_number")] public int DriverNumber { get; init; }
    [JsonPropertyName("position")] public int? Position { get; init; }
    [JsonPropertyName("dnf")] public bool Dnf { get; init; }
    [JsonPropertyName("dns")] public bool Dns { get; init; }
    [JsonPropertyName("dsq")] public bool Dsq { get; init; }
    public string Label => Dsq ? "DSQ" : Dns ? "DNS" : Dnf ? "DNF" : Position?.ToString() ?? "–";
}

public sealed record TeamChampionshipStandingResult(OpenF1Session? Session, IReadOnlyList<TeamChampionshipStanding> Standings, int CompletedRaces = 0, int TotalRaces = 0, OpenF1Session? NextRace = null, bool StatisticsAvailable = false);
public sealed record TeamChampionshipStanding(int Position, string TeamName, string TeamColour, decimal Points, decimal PointsGained, int PositionsGained, int? Wins, int? Podiums, IReadOnlyList<TeamRecentResult> RecentResults, string Drivers);
public sealed record TeamRecentResult(string Label, string Description);
public sealed record OpenF1TeamChampionship
{
    [JsonPropertyName("team_name")] public string TeamName { get; init; } = string.Empty;
    [JsonPropertyName("points_current")] public decimal PointsCurrent { get; init; }
    [JsonPropertyName("points_start")] public decimal PointsStart { get; init; }
    [JsonPropertyName("position_current")] public int PositionCurrent { get; init; }
    [JsonPropertyName("position_start")] public int PositionStart { get; init; }
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
    [JsonPropertyName("session_key")] public int SessionKey { get; init; }
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
