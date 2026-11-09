using System.Net;
using System.Reflection;
using System.Text.Json;
using AP1.Components.Pages;
using AP1.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS {message}");
}

var fixture = new Fixture();
var service = new OpenF1Service(new HttpClient(fixture) { BaseAddress = new Uri("https://example.test/") });
var result = await service.GetCurrentTeamChampionshipAsync(2023);
var mercedes = result.Standings.Single(t => t.TeamName == "Mercedes");
var ferrari = result.Standings.Single(t => t.TeamName == "Ferrari");
Check(result.CompletedRaces == 2, "Stand matches the last race with championship data, not the newer unpublished race.");
Check(mercedes.TeamColour == "#00D7B6", "Team colour comes from the drivers response.");
Check(mercedes.Wins == 1 && ferrari.Wins == 1, "A driver transfer credits each victory to the team at that race.");
Check(mercedes.Podiums == 3 && ferrari.Podiums == 1, "Double podiums count twice; disqualified cars do not count.");
Check(mercedes.RecentResults.Select(r => r.Label).SequenceEqual(["1", "2"]), "Recent team finishes are chronological and use the best classified car.");
Check(ferrari.RecentResults[0].Label == "DSQ", "Disqualification is preserved in recent results.");
Check(result.NextRace?.Location == "Future", "Next race is populated.");

fixture.MissingResults = true;
var incomplete = await service.GetCurrentTeamChampionshipAsync(2023);
Check(!incomplete.StatisticsAvailable && incomplete.Standings.All(t => t.Wins is null && t.Podiums is null),
    "Missing data displays unavailable statistics, not misleading zeros.");
fixture.EmptySeason = true;
Check((await service.GetCurrentTeamChampionshipAsync(2023)).Standings.Count == 0, "Empty season returns an empty state.");

if (args.Contains("--live"))
{
    var liveService = new OpenF1Service(new HttpClient { BaseAddress = new Uri("https://api.openf1.org/v1/"), Timeout = TimeSpan.FromSeconds(30) });
    result = await liveService.GetCurrentTeamChampionshipAsync(DateTime.UtcNow.Year);
    Check(result.Standings.Count > 0 && result.StatisticsAvailable, "Live standings and complete Grand Prix statistics load.");
    Check(result.Standings.All(t => t.TeamColour != "#8a8f98"), "Every current team has its colour.");
    Check(result.Standings.Sum(t => t.Wins) == result.CompletedRaces, "Live season has exactly one team victory per completed Grand Prix.");
    Check(result.Standings.Sum(t => t.Podiums) == result.CompletedRaces * 3, "Live season has three podium places per completed Grand Prix.");
    foreach (var team in result.Standings) Console.WriteLine($"{team.TeamName}: {team.Points} points, {team.Wins} wins, {team.Podiums} podiums, {team.TeamColour}");
}

var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../AP1/wwwroot"));
// Pass the project web root explicitly when using a non-default output path.
var rootArg = Array.IndexOf(args, "--webroot");
if (rootArg >= 0) webRoot = args[rootArg + 1];
foreach (var team in result.Standings)
{
    var logo = TeamIdentity.Logo(team.TeamName);
    Check(logo is not null && File.Exists(Path.Combine(webRoot, logo)), $"Local logo exists for {team.TeamName}.");
}

var services = new ServiceCollection().AddLogging().AddSingleton(service).BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
SeededConstructors.Result = result;
var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
    var page = await renderer.RenderComponentAsync<PreviewRoot>();
    return page.ToHtmlString();
});
Check(html.Contains("Siege") && html.Contains("Podien") && html.Contains("Letzte 5 Rennen"), "Rendered Razor page includes all statistics columns.");
Check(!html.Contains("formula1.svg") && !html.Contains("TeamName=\"team.TeamName\""), "Rendered page never uses the old generic logo or literal binding.");
foreach (var team in result.Standings)
    Check(html.Contains(TeamIdentity.Logo(team.TeamName)!) && html.Contains(team.TeamColour), $"Rendered page has the right logo and colour for {team.TeamName}.");
var htmlArg = Array.IndexOf(args, "--html");
if (htmlArg >= 0)
    await File.WriteAllTextAsync(args[htmlArg + 1], $"<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"http://localhost:5277/app.css\"><base href=\"http://localhost:5277/\"></head><body><main style=\"padding:24px\">{html}</main></body></html>");

public class SeededConstructors : Constructors
{
    public static TeamChampionshipStandingResult Result { get; set; } = default!;
    public RenderFragment Content => builder => base.BuildRenderTree(builder);
    public SeededConstructors()
    {
        typeof(Constructors).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, Result);
        typeof(Constructors).GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, false);
    }
}

public class PreviewRoot : ComponentBase
{
    private readonly SeededConstructors _page = new();
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        => builder.AddContent(0, _page.Content);
}

public sealed class Fixture : HttpMessageHandler
{
    public bool MissingResults { get; set; }
    public bool EmptySeason { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        object data = path switch
        {
            var p when p.StartsWith("/sessions") => EmptySeason ? Array.Empty<object>() : new object[] {
                new { session_key=10, session_name="Race", date_start="2023-03-01T12:00:00Z", date_end="2023-03-01T14:00:00Z", location="First" },
                new { session_key=20, session_name="Race", date_start="2023-03-08T12:00:00Z", date_end="2023-03-08T14:00:00Z", location="Second" },
                new { session_key=30, session_name="Race", date_start="2023-03-15T12:00:00Z", date_end="2023-03-15T14:00:00Z", location="Unpublished" },
                new { session_key=40, session_name="Race", date_start="2099-03-01T12:00:00Z", date_end="2099-03-01T14:00:00Z", location="Future" } },
            var p when p.Contains("championship_teams?session_key=30") => Array.Empty<object>(),
            var p when p.StartsWith("/championship_teams") => new object[] {
                new {team_name="Mercedes", position_current=1, position_start=1, points_current=58, points_start=43},
                new {team_name="Ferrari", position_current=2, position_start=2, points_current=25, points_start=0} },
            var p when p.StartsWith("/drivers") => new object[] {
                new {session_key=10, driver_number=1, team_name="Mercedes", team_colour="00D7B6", name_acronym="ONE"},
                new {session_key=10, driver_number=2, team_name="Mercedes", team_colour="00D7B6", name_acronym="TWO"},
                new {session_key=10, driver_number=3, team_name="Ferrari", team_colour="ED1131", name_acronym="THR"},
                new {session_key=20, driver_number=1, team_name="Ferrari", team_colour="ED1131", name_acronym="ONE"},
                new {session_key=20, driver_number=2, team_name="Mercedes", team_colour="00D7B6", name_acronym="TWO"} },
            var p when p.StartsWith("/session_result") => MissingResults ? Array.Empty<object>() : new object[] {
                new {session_key=15, driver_number=3, position="RT", dsq=false},
                new {session_key=10, driver_number=1, position=1, dsq=false},
                new {session_key=10, driver_number=2, position=2, dsq=false},
                new {session_key=10, driver_number=3, position=3, dsq=true},
                new {session_key=20, driver_number=1, position=1, dsq=false},
                new {session_key=20, driver_number=2, position=2, dsq=false} },
            _ => throw new Exception($"Unexpected request: {path}")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") });
    }
}
