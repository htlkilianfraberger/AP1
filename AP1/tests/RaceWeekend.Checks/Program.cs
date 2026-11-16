using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AP1.Services;
using Microsoft.Extensions.Caching.Memory;

static void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

using var memory = new MemoryCache(new MemoryCacheOptions());
var fixture = new WeekendFixture();
var service = new RaceWeekendService(new HttpClient(fixture), new ApiResultCache(memory));
var races = await service.GetSeasonAsync(2025);
var race = races.Single();
Check(CircuitArtwork.CircuitId(12) == "sepang" && CircuitArtwork.CountryCode("sepang") == "my"
    && CircuitArtwork.Map("sepang") == "/circuits/sepang.svg" && RaceDisplay.TrackZone("sepang") == "Asia/Kuala_Lumpur",
    "The Bahrain GP at Sepang uses the Malaysian circuit, flag, map and timezone.");
Check(race.Sessions.Count == 5 && race.Sessions.Any(s => s.Name == "Sprint-Qualifying"), "Sprint weekend includes practice, sprint qualifying, sprint, qualifying and race.");
Check(race.TrackOffset == TimeSpan.FromHours(10) && race.CircuitImage is not null, "Track metadata matches the weekend by date.");
var raceSession = race.Sessions.Single(s => s.Key == "Race");
Check(RaceDisplay.SessionTime(raceSession).Contains("05:00") && RaceDisplay.SessionTime(raceSession, true, race.TrackOffset).Contains("14:00"), "Track-time switch converts UTC to Australia instead of Austrian time.");
Check(RaceDisplay.SessionTime(raceSession, true).Contains("UTC (Streckenzeit unbekannt)"), "Missing track offsets are labelled, never guessed.");
Check(RaceDisplay.Local(DateTimeOffset.Parse("2025-07-01T12:00:00Z")).Hour == 14
    && RaceDisplay.Local(DateTimeOffset.Parse("2025-01-01T12:00:00Z")).Hour == 13, "Austria conversion observes daylight saving time.");
Check(RaceDisplay.SessionTime(raceSession with { Start = DateTimeOffset.Parse("2023-03-31T04:00:00Z") }, true, null, "Australia/Melbourne").Contains("15:00")
    && RaceDisplay.SessionTime(raceSession with { Start = DateTimeOffset.Parse("2023-04-02T04:00:00Z") }, true, null, "Australia/Melbourne").Contains("14:00"),
    "Track time handles a daylight saving change within the race weekend.");
Check(!race.IsPast(DateTimeOffset.Parse("2025-03-16T05:00:00Z")) && race.IsPast(DateTimeOffset.Parse("2025-03-17T00:00:00Z")), "Calendar filter keeps an ongoing race and hides completed weekends.");
var vegas = race with { CircuitId = "vegas", Start = DateTimeOffset.Parse("2025-11-23T04:00:00Z") };
Check(RaceDisplay.RaceDate(vegas, true) == new DateOnly(2025, 11, 22)
    && RaceDisplay.RaceDate(vegas, false) == new DateOnly(2025, 11, 23), "Race card date follows the selected timezone across midnight.");
var unknown = raceSession with { Start = null };
Check(RaceDisplay.SessionTime(unknown with { Date = new DateOnly(1950, 5, 13) }).Contains("Uhrzeit nicht überliefert"), "Missing historical times are not shown as midnight.");
var requests = fixture.RequestCount;
Check(ReferenceEquals(races, await service.GetSeasonAsync(2025)) && fixture.RequestCount == requests, "Calendar and metadata are cached together.");

var history = await service.GetHistoryAsync("test");
Check(history.Wins[0].Count == 2 && history.Wins[0].Driver == "Ada Driver", "Wins include all paginated results.");
Check(history.Poles.Count == 2 && history.Poles.All(p => p.Count == 1), "Tied pole-start record holders remain available.");
Check(history.FastestLap?.Driver == "Bea Driver" && history.FastestLap.Time == "1:18.500" && history.FastestLap.Session == "Q2", "Fastest lap includes non-pole drivers and Q2 on a split qualifying page.");
Check(history.FastestRaceLap?.Time == "1:20.000", "Race lap record is separate from qualifying.");
var results = await service.GetResultsAsync(race);
Check(results.Select(r => r.Name).SequenceEqual(["Qualifying", "Sprint", "Rennen"]), "Past weekend loads all three classifications.");
Check(results.Last().Results[0].Status == "Disqualified" && results.Last().Results[0].PositionText == "D", "Disqualification is retained for classification rendering.");
requests = fixture.RequestCount;
Check((await service.GetResultsAsync(race with { Year = 2099, Round = 2, Sessions = [raceSession with { Start = DateTimeOffset.UtcNow.AddDays(30) }] })).Count == 0
    && fixture.RequestCount == requests, "Future races never request or invent results.");
fixture.BrokenPage = true;
try { await service.GetHistoryAsync("broken"); throw new Exception("Incomplete pagination was accepted"); }
catch (JsonException) { Console.WriteLine("PASS Incomplete pagination fails instead of publishing false records."); }

if (args.Contains("--live"))
{
    using var liveMemory = new MemoryCache(new MemoryCacheOptions());
    var live = new RaceWeekendService(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new ApiResultCache(liveMemory));
    var season = await live.GetSeasonAsync(2025);
    Check(season.Count >= 20 && season.All(r => r.Sessions.Any(s => s.Key == "Race")), "Live season has the full race schedule.");
    Check(season.Any(r => r.TrackOffset is not null && r.CircuitImage is not null), "Live OpenF1 metadata supplies images and track offsets.");
    var completed = await live.GetResultsAsync(season.First());
    Check(completed.Any(c => c.Name == "Rennen" && c.Results.Count >= 20), "Live completed Grand Prix classification loads.");
    var records = await live.GetHistoryAsync("monza");
    Check(records.Wins.Count > 0 && records.Poles.Count > 0 && records.FastestLap is not null && records.FastestRaceLap is not null, "Live circuit history loads through every page.");
    Console.WriteLine($"Monza: {records.Wins[0].Driver} ({records.Wins[0].Count} wins), {records.FastestLap?.Time}, race {records.FastestRaceLap?.Time}");
}

sealed class WeekendFixture : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public bool BrokenPage { get; set; }
    private static JsonObject Row(string name = "Ada", string q2 = "1:19.000") => new()
    {
        ["position"] = "1", ["positionText"] = "D", ["grid"] = "1", ["points"] = "0", ["status"] = "Disqualified",
        ["Driver"] = new JsonObject { ["driverId"] = name, ["givenName"] = name, ["familyName"] = "Driver" },
        ["Constructor"] = new JsonObject { ["name"] = "Test Team" },
        ["Q1"] = "1:21.000", ["Q2"] = q2, ["Q3"] = "1:22.000",
        ["FastestLap"] = new JsonObject { ["rank"] = "1", ["Time"] = new JsonObject { ["time"] = "1:20.000" } }
    };
    private static JsonObject Race(string field, params JsonObject[] results) => new()
    { ["season"] = "2025", ["round"] = "1", [field] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()) };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        RequestCount++;
        var path = request.RequestUri!.PathAndQuery;
        string body;
        if (request.RequestUri.Host == "api.openf1.org")
            body = """[{"date_start":"2025-03-14T00:00:00Z","date_end":"2025-03-16T23:59:00Z","meeting_name":"Test Grand Prix","gmt_offset":"10:00:00","circuit_image":"https://media.formula1.com/test.png"}]""";
        else if (path.StartsWith("/ergast/f1/2025.json"))
            body = """{"MRData":{"total":"1","RaceTable":{"Races":[{"season":"2025","round":"1","raceName":"Test Grand Prix","Circuit":{"circuitId":"test","circuitName":"Test Circuit","Location":{"locality":"Test","country":"Australia"}},"date":"2025-03-16","time":"04:00:00Z","FirstPractice":{"date":"2025-03-14","time":"01:00:00Z"},"SprintQualifying":{"date":"2025-03-14","time":"05:00:00Z"},"Sprint":{"date":"2025-03-15","time":"01:00:00Z"},"Qualifying":{"date":"2025-03-15","time":"05:00:00Z"}}]}}}""";
        else
        {
            var second = path.Contains("offset=1");
            var qualifying = path.Contains("qualifying");
            var field = qualifying ? "QualifyingResults" : path.Contains("sprint") ? "SprintResults" : "Results";
            var paginated = path.Contains("circuits") && (qualifying || path.Contains("results/1"));
            var rows = BrokenPage && path.Contains("broken") && second ? Array.Empty<JsonObject>()
                : new[] { Race(field, Row(second ? (qualifying ? "Bea" : "Ada") : "Ada", second ? "1:18.500" : "1:19.000")) };
            if (path.Contains("grid/1")) rows = [Race(field, Row("Ada"), Row("Bea"))];
            body = JsonSerializer.Serialize(new { MRData = new { total = paginated ? "2" : path.Contains("grid/1") ? "2" : "1", RaceTable = new { Races = rows } } });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
