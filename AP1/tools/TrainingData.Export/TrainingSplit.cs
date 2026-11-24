using System.Text;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace TrainingData.Export;

public static class TrainingSplit
{
    private static readonly string[] DriverFeatures =
    [
        "Season", "Round", "Location", "DriverNumber", "TeamKey",
        "PreviousStarts", "PreviousAverageFinish", "PointsBeforeRace",
        "ChampionshipPositionBeforeRace"
    ];

    private static readonly string[] TeamFeatures =
    [
        "Season", "Round", "Location", "TeamKey", "PreviousStarts",
        "PreviousAverageBestFinish", "PointsBeforeRace",
        "ChampionshipPositionBeforeRace"
    ];

    public static void Create(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var currentDriverRows = ReadYears(directory, "drivers", [2026], DriverFeatures, "FinishPosition");
        var rounds = currentDriverRows
            .Select(row => int.Parse(row.Features[1], CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();
        if (rounds.Length < 2)
            throw new InvalidDataException("At least two completed 2026 races are needed for a chronological split.");
        var trainingRound = rounds[Math.Clamp((int)Math.Ceiling(rounds.Length * 0.8) - 1, 0, rounds.Length - 2)];

        foreach (var (kind, features, target) in new[]
        {
            ("drivers", DriverFeatures, "FinishPosition"),
            ("teams", TeamFeatures, "BestFinishPosition")
        })
        {
            var train = ReadYears(directory, kind, [2023, 2024, 2025], features, target);
            var currentRows = kind == "drivers"
                ? currentDriverRows
                : ReadYears(directory, kind, [2026], features, target);
            var trainCurrent = currentRows.Where(row =>
                int.Parse(row.Features[1], CultureInfo.InvariantCulture) <= trainingRound).ToList();
            var test = currentRows.Where(row =>
                int.Parse(row.Features[1], CultureInfo.InvariantCulture) > trainingRound).ToList();
            if (trainCurrent.Count == 0 || test.Count == 0)
                throw new InvalidDataException($"No 2026 training or test rows for {kind}.");
            train.AddRange(trainCurrent);
            var splitDirectory = Path.Combine(directory, "splits", kind);
            Directory.CreateDirectory(splitDirectory);
            Write(Path.Combine(splitDirectory, "X_train.csv"), features, train.Select(r => r.Features));
            Write(Path.Combine(splitDirectory, "y_train.csv"), [target], train.Select(r => new[] { r.Target }));
            Write(Path.Combine(splitDirectory, "X_test.csv"), features, test.Select(r => r.Features));
            Write(Path.Combine(splitDirectory, "y_test.csv"), [target], test.Select(r => new[] { r.Target }));
            Console.WriteLine($"{kind}: {train.Count} training rows through 2026 round {trainingRound}, {test.Count} later test rows.");
        }
    }

    private static List<(string[] Features, string Target)> ReadYears(
        string directory, string kind, int[] years, string[] features, string target)
    {
        var rows = new List<(string[], string)>();
        foreach (var year in years)
        {
            var path = Path.Combine(directory, $"{kind}-{year}.csv");
            using var parser = new TextFieldParser(path, Encoding.UTF8)
            {
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(",");
            var header = parser.ReadFields() ?? throw new InvalidDataException($"Empty CSV: {path}");
            var columns = header.Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
            var featureIndices = features.Select(name => Column(columns, name, path)).ToArray();
            var targetIndex = Column(columns, target, path);
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? throw new InvalidDataException($"Invalid CSV: {path}");
                if (fields.Length != header.Length)
                    throw new InvalidDataException($"Unexpected column count in {path}");
                rows.Add((featureIndices.Select(index => fields[index]).ToArray(), fields[targetIndex]));
            }
        }
        return rows;
    }

    private static int Column(Dictionary<string, int> columns, string name, string path) =>
        columns.TryGetValue(name, out var index) ? index :
        throw new InvalidDataException($"Missing {name} in {path}");

    private static void Write(string path, string[] header, IEnumerable<string[]> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine(string.Join(',', header));
        foreach (var row in rows)
            writer.WriteLine(string.Join(',', row.Select(Escape)));
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value :
        $"\"{value.Replace("\"", "\"\"")}\"";
}
