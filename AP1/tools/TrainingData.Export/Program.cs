using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AP1.Services;
using TrainingData.Export;

if (args.Length == 2 && args[0] == "split")
{
    TrainingSplit.Create(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length < 2 || !int.TryParse(args[0], out var year) || year < 2023 || year > DateTime.UtcNow.Year)
{
    Console.Error.WriteLine("Usage: dotnet run --project AP1/tools/TrainingData.Export -- <season> <output-directory> | split <data-directory>");
    return 1;
}
var output = Path.GetFullPath(args[1]);
using var client = new HttpClient { BaseAddress = new Uri("https://api.openf1.org/v1/"), Timeout = TimeSpan.FromSeconds(30) };
async Task<List<T>> Fetch<T>(string path)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        await Task.Delay(400);
        try
        {
            using var response = await client.GetAsync(path);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 2)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1)));
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<T>>() ?? [];
        }
        catch (Exception ex) when (attempt < 2 && ex is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
        }
    }
    throw new HttpRequestException($"Could not load {path}");
}
var sessions = await Fetch<OpenF1Session>($"sessions?year={year}&session_type=Race");
var races = sessions.Where(s => s.SessionName == "Race" && !s.IsCancelled && s.DateEnd < DateTimeOffset.UtcNow)
    .OrderBy(s => s.DateStart).ToList();
var snapshots = new List<RaceSnapshot>();
foreach (var race in races)
{
    var key = race.SessionKey;
    var drivers = await Fetch<OpenF1Driver>($"drivers?session_key={key}");
    var results = await Fetch<OpenF1RaceResult>($"session_result?session_key={key}");
    var driverStandings = await Fetch<TrainingDriverChampionship>($"championship_drivers?session_key={key}");
    var teamStandings = await Fetch<TrainingTeamChampionship>($"championship_teams?session_key={key}");
    if (drivers.Count == 0 || results.Count == 0 || driverStandings.Count == 0 || teamStandings.Count == 0)
    {
        Console.Error.WriteLine($"Skipping {key}: incomplete published data.");
        continue;
    }
    snapshots.Add(new RaceSnapshot(race, drivers, results, driverStandings, teamStandings));
}
var rows = TrainingRows.Build(snapshots);
Directory.CreateDirectory(output);
await WriteCsv(Path.Combine(output, $"drivers-{year}.csv"), rows.Drivers);
await WriteCsv(Path.Combine(output, $"teams-{year}.csv"), rows.Teams);
Console.WriteLine($"Exported {rows.Drivers.Count} driver rows and {rows.Teams.Count} team rows from {snapshots.Count} races.");
return 0;

static async Task WriteCsv<T>(string path, IReadOnlyList<T> rows)
{
    await using var stream = File.Create(path);
    await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
    var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
    await writer.WriteLineAsync(string.Join(',', properties.Select(p => p.Name)));
    foreach (var row in rows)
        await writer.WriteLineAsync(string.Join(',', properties.Select(p => Escape(Convert.ToString(p.GetValue(row), CultureInfo.InvariantCulture)))));
}

static string Escape(string? value)
{
    value ??= "";
    return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
