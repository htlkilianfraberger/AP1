using System.Net;
using AP1.Services;
using Microsoft.Extensions.Caching.Memory;

static void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

using var memory = new MemoryCache(new MemoryCacheOptions());
var fixture = new OpenF1Fixture();
var service = new RaceWeekendService(new HttpClient(fixture), new ApiResultCache(memory));

var seasons = await service.GetSeasonsAsync();
Check(seasons.Min() == 2023 && seasons.Max() == DateTime.UtcNow.Year, "Only OpenF1 seasons from 2023 are offered.");
var races = await service.GetSeasonAsync(2025);
var race = races.Single();
Check(race.CircuitKey == 10 && race.CircuitId == "albert_park", "OpenF1 circuit metadata maps to the local track identity.");
Check(race.Sessions.Count == 5 && race.Sessions.Any(s => s.Key == "SprintQualifying"), "Sprint weekend includes all OpenF1 sessions.");
Check(race.TrackOffset == TimeSpan.FromHours(11) && race.CircuitImage is not null, "OpenF1 supplies track offset and image.");
Check(race.Sessions.All(s => s.SessionKey > 0), "OpenF1 session keys are retained for classifications.");

var results = await service.GetResultsAsync(race);
Check(results.Select(r => r.Name).SequenceEqual(["Qualifying", "Sprint", "Rennen"]), "OpenF1 loads all published classifications.");
var raceResults = results.Single(r => r.Name == "Rennen").Results;
Check(raceResults[0].Driver == "Ada Driver" && raceResults[0].Grid == "2", "Driver and starting-grid data are combined.");
Check(raceResults[1].Status == "Ausgefallen" && raceResults[1].PositionText == "R", "OpenF1 DNF state is retained.");
Check(raceResults[0].IsFastestLap && raceResults[0].FastestLap == "1:20.000", "Fastest race lap is calculated from OpenF1 laps.");
var qualifying = results.Single(r => r.Name == "Qualifying").Results[0];
Check(qualifying.Q1 == "1:21.000" && qualifying.Q2 == "1:19.000" && qualifying.Q3 == "1:18.500", "Qualifying phases come from OpenF1 duration arrays.");

var history = await service.GetHistoryAsync(race);
Check(history.Wins[0].Driver == "Ada Driver" && history.Poles[0].Driver == "Ada Driver", "Circuit wins and poles come from OpenF1.");
Check(history.FastestLap?.Time == "1:18.500" && history.FastestRaceLap?.Time == "1:20.000", "Circuit records are calculated from OpenF1 laps.");
Check(history.FirstYear == "2025", "Circuit-history coverage starts with the first OpenF1 race in the fixture.");
var requests = fixture.RequestCount;
Check(ReferenceEquals(races, await service.GetSeasonAsync(2025)) && fixture.RequestCount == requests, "Calendar data is cached.");

if (args.Contains("--live"))
{
    using var liveMemory = new MemoryCache(new MemoryCacheOptions());
    var live = new RaceWeekendService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, new ApiResultCache(liveMemory));
    var season = await live.GetSeasonAsync(2025);
    Check(season.Count >= 20 && season.All(r => r.Sessions.Any(s => s.Key == "Race")), "Live OpenF1 season has a complete schedule.");
    var completed = await live.GetResultsAsync(season.First());
    Check(completed.Any(c => c.Name == "Rennen" && c.Results.Count >= 20), "Live OpenF1 race classification loads.");
    var records = await live.GetHistoryAsync(season.First(r => r.CircuitId == "monza"));
    Check(records.Wins.Count > 0 && records.Poles.Count > 0, "Live OpenF1 circuit history loads.");
}

sealed class OpenF1Fixture : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var path = request.RequestUri!.PathAndQuery;
        var body = path switch
        {
            var p when p.StartsWith("/v1/meetings") => """[{"meeting_key":500,"meeting_name":"Australian Grand Prix","meeting_official_name":"Australian Grand Prix","location":"Melbourne","country_name":"Australia","circuit_key":10,"circuit_short_name":"Melbourne","date_start":"2025-03-14T01:00:00Z","gmt_offset":"11:00:00","circuit_image":"https://media.formula1.com/test.png","is_cancelled":false}]""",
            var p when p.StartsWith("/v1/sessions?year=") => Sessions,
            var p when p.StartsWith("/v1/sessions?circuit_key=") => HistorySessions,
            var p when p.StartsWith("/v1/drivers") => Drivers,
            var p when p.StartsWith("/v1/starting_grid") => """[{"driver_number":1,"position":2},{"driver_number":2,"position":1}]""",
            var p when p.StartsWith("/v1/laps") => LapsFor(p),
            var p when p.StartsWith("/v1/session_result") => ResultsFor(p),
            _ => "[]"
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    private static string ResultsFor(string path)
    {
        if (path.Count(c => c == '=') > 1) return AllResults;
        if (path.Contains("1004")) return QualifyingResults;
        if (path.Contains("1003")) return SprintResults;
        return RaceResults;
    }

    private static string LapsFor(string path)
    {
        if (path.Count(c => c == '=') > 1) return Laps;
        if (path.Contains("1004")) return QualifyingLaps;
        if (path.Contains("1003")) return "[]";
        return RaceLaps;
    }

    private const string Sessions = """[{"session_key":1001,"meeting_key":500,"session_name":"Practice 1","session_type":"Practice","date_start":"2025-03-14T01:30:00Z","year":2025},{"session_key":1002,"meeting_key":500,"session_name":"Sprint Qualifying","session_type":"Sprint Qualifying","date_start":"2025-03-14T05:30:00Z","year":2025},{"session_key":1003,"meeting_key":500,"session_name":"Sprint","session_type":"Sprint","date_start":"2025-03-15T01:00:00Z","year":2025},{"session_key":1004,"meeting_key":500,"session_name":"Qualifying","session_type":"Qualifying","date_start":"2025-03-15T06:00:00Z","year":2025},{"session_key":1005,"meeting_key":500,"session_name":"Race","session_type":"Race","date_start":"2025-03-16T05:00:00Z","year":2025}]""";
    private const string HistorySessions = """[{"session_key":1004,"meeting_key":500,"session_name":"Qualifying","session_type":"Qualifying","date_start":"2025-03-15T06:00:00Z","year":2025,"is_cancelled":false},{"session_key":1005,"meeting_key":500,"session_name":"Race","session_type":"Race","date_start":"2025-03-16T05:00:00Z","year":2025,"is_cancelled":false}]""";
    private const string Drivers = """[{"session_key":1004,"driver_number":1,"full_name":"Ada Driver","team_name":"Test Team"},{"session_key":1004,"driver_number":2,"full_name":"Bea Driver","team_name":"Other Team"},{"session_key":1005,"driver_number":1,"full_name":"Ada Driver","team_name":"Test Team"},{"session_key":1005,"driver_number":2,"full_name":"Bea Driver","team_name":"Other Team"}]""";
    private const string QualifyingResults = """[{"session_key":1004,"driver_number":1,"position":1,"duration":[81.0,79.0,78.5],"gap_to_leader":[0,0,0],"number_of_laps":18,"dnf":false,"dns":false,"dsq":false},{"session_key":1004,"driver_number":2,"position":2,"duration":[81.2,79.5,null],"gap_to_leader":[0.2,0.5,null],"number_of_laps":14,"dnf":false,"dns":false,"dsq":false}]""";
    private const string SprintResults = """[{"session_key":1003,"driver_number":1,"position":1,"duration":1800.0,"gap_to_leader":0,"number_of_laps":20,"dnf":false,"dns":false,"dsq":false}]""";
    private const string RaceResults = """[{"session_key":1005,"driver_number":1,"position":1,"duration":5400.0,"gap_to_leader":0,"number_of_laps":58,"dnf":false,"dns":false,"dsq":false},{"session_key":1005,"driver_number":2,"position":2,"duration":null,"gap_to_leader":5.4,"number_of_laps":40,"dnf":true,"dns":false,"dsq":false}]""";
    private const string AllResults = """[{"session_key":1004,"driver_number":1,"position":1,"duration":[81.0,79.0,78.5],"gap_to_leader":[0,0,0],"number_of_laps":18,"dnf":false,"dns":false,"dsq":false},{"session_key":1005,"driver_number":1,"position":1,"duration":5400.0,"gap_to_leader":0,"number_of_laps":58,"dnf":false,"dns":false,"dsq":false}]""";
    private const string QualifyingLaps = """[{"session_key":1004,"driver_number":1,"lap_duration":78.5},{"session_key":1004,"driver_number":2,"lap_duration":79.5}]""";
    private const string RaceLaps = """[{"session_key":1005,"driver_number":1,"lap_duration":80.0},{"session_key":1005,"driver_number":2,"lap_duration":81.0}]""";
    private const string Laps = """[{"session_key":1004,"driver_number":1,"lap_duration":78.5},{"session_key":1004,"driver_number":2,"lap_duration":79.5},{"session_key":1005,"driver_number":1,"lap_duration":80.0},{"session_key":1005,"driver_number":2,"lap_duration":81.0}]""";
}
