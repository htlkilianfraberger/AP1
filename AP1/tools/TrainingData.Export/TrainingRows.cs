using AP1.Services;

namespace TrainingData.Export;

public sealed record RaceSnapshot(
    OpenF1Session Session,
    IReadOnlyList<OpenF1Driver> Drivers,
    IReadOnlyList<OpenF1RaceResult> Results,
    IReadOnlyList<TrainingDriverChampionship> DriverStandings,
    IReadOnlyList<TrainingTeamChampionship> TeamStandings);

public sealed record TrainingDriverChampionship(
    [property: System.Text.Json.Serialization.JsonPropertyName("driver_number")] int DriverNumber,
    [property: System.Text.Json.Serialization.JsonPropertyName("points_start")] decimal? PointsStart,
    [property: System.Text.Json.Serialization.JsonPropertyName("position_start")] int? PositionStart);

public sealed record TrainingTeamChampionship(
    [property: System.Text.Json.Serialization.JsonPropertyName("team_name")] string TeamName,
    [property: System.Text.Json.Serialization.JsonPropertyName("points_start")] decimal? PointsStart,
    [property: System.Text.Json.Serialization.JsonPropertyName("position_start")] int? PositionStart);

public sealed record DriverTrainingRow(
    int Season, int Round, int SessionKey, string RaceDate, string Location,
    int DriverNumber, string DriverName, string TeamKey,
    int PreviousStarts, double? PreviousAverageFinish,
    decimal? PointsBeforeRace, int? ChampionshipPositionBeforeRace,
    int FinishPosition, bool Dnf, bool Dsq);

public sealed record TeamTrainingRow(
    int Season, int Round, int SessionKey, string RaceDate, string Location,
    string TeamKey, string TeamName, int PreviousStarts,
    double? PreviousAverageBestFinish, decimal? PointsBeforeRace,
    int? ChampionshipPositionBeforeRace, int BestFinishPosition);

public static class TrainingRows
{
    public static (List<DriverTrainingRow> Drivers, List<TeamTrainingRow> Teams) Build(
        IEnumerable<RaceSnapshot> snapshots)
    {
        var driverRows = new List<DriverTrainingRow>();
        var teamRows = new List<TeamTrainingRow>();
        foreach (var season in snapshots.GroupBy(s => s.Session.Year))
        {
            var driverHistory = new Dictionary<int, List<int>>();
            var teamHistory = new Dictionary<string, List<int>>();
            var round = 0;
            foreach (var race in season.OrderBy(s => s.Session.DateStart))
            {
                round++;
                var results = race.Results.GroupBy(r => r.DriverNumber).ToDictionary(g => g.Key, g => g.First());
                var drivers = race.Drivers.Where(d => d.DriverNumber > 0 && !string.IsNullOrWhiteSpace(d.TeamName))
                    .GroupBy(d => d.DriverNumber).ToDictionary(g => g.Key, g => g.First());
                var date = race.Session.DateStart.ToString("yyyy-MM-dd");
                foreach (var (number, driver) in drivers)
                {
                    if (!results.TryGetValue(number, out var result) || result.Dns) continue;
                    var prior = driverHistory.GetValueOrDefault(number);
                    var standing = race.DriverStandings.FirstOrDefault(s => s.DriverNumber == number);
                    driverRows.Add(new DriverTrainingRow(
                        season.Key, round, race.Session.SessionKey, date, race.Session.Location,
                        number, driver.FullName ?? $"#{number}", TeamIdentity.Key(driver.TeamName!),
                        prior?.Count ?? 0, prior?.Count > 0 ? prior.Average() : null,
                        standing?.PointsStart, standing?.PositionStart,
                        result.Position ?? 0, result.Dnf, result.Dsq));
                }
                foreach (var group in drivers.Values.GroupBy(d => TeamIdentity.Key(d.TeamName!)))
                {
                    var finishes = group.Select(d => results.GetValueOrDefault(d.DriverNumber))
                        .Where(r => r is not null && !r.Dns && !r.Dsq && r.Position is > 0)
                        .Select(r => r!.Position!.Value).ToList();
                    if (finishes.Count == 0) continue;
                    var prior = teamHistory.GetValueOrDefault(group.Key);
                    var standing = race.TeamStandings.FirstOrDefault(s => TeamIdentity.Key(s.TeamName) == group.Key);
                    teamRows.Add(new TeamTrainingRow(
                        season.Key, round, race.Session.SessionKey, date, race.Session.Location,
                        group.Key, group.First().TeamName!, prior?.Count ?? 0,
                        prior?.Count > 0 ? prior.Average() : null,
                        standing?.PointsStart, standing?.PositionStart, finishes.Min()));
                }
                // Update history only after both row types have been built for this race.
                foreach (var row in driverRows.Where(r => r.Season == season.Key && r.SessionKey == race.Session.SessionKey && r.FinishPosition > 0 && !r.Dsq))
                {
                    if (!driverHistory.TryGetValue(row.DriverNumber, out var history)) driverHistory[row.DriverNumber] = history = [];
                    history.Add(row.FinishPosition);
                }
                foreach (var row in teamRows.Where(r => r.Season == season.Key && r.SessionKey == race.Session.SessionKey))
                {
                    if (!teamHistory.TryGetValue(row.TeamKey, out var history)) teamHistory[row.TeamKey] = history = [];
                    history.Add(row.BestFinishPosition);
                }
            }
        }
        return (driverRows, teamRows);
    }
}
