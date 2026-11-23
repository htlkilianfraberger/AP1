using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AP1.Services;

/// <summary>
/// Loads historical OpenF1 race events and replays them against a simulated clock.
/// Historical OpenF1 data is public, so this service needs no API key.
/// </summary>
public sealed class RaceReplayService(HttpClient httpClient)
{
    private const int FirstSupportedYear = 2023;

    public async Task<IReadOnlyList<RaceReplaySession>> GetRaceSessionsAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        ValidateYear(year);
        var sessions = await GetAsync<List<RaceReplaySession>>(
            $"sessions?year={year}&session_name=Race",
            cancellationToken) ?? [];

        return sessions
            .Where(session => !session.IsCancelled && session.SessionName == "Race")
            .OrderBy(session => session.DateStart)
            .ToList();
    }

    public async Task<IReadOnlyList<RaceReplayFrame>> LoadFramesAsync(
        int sessionKey,
        CancellationToken cancellationToken = default)
    {
        if (sessionKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionKey));
        }

        var events = new List<RaceReplayEvent>();

        var positions = await GetAsync<List<OpenF1Position>>(
            $"position?session_key={sessionKey}", cancellationToken) ?? [];
        events.AddRange(positions.Select(position => new RaceReplayEvent(
            position.Date,
            "position",
            position.DriverNumber,
            position.Position,
            null,
            null,
            null,
            null,
            null,
            JsonSerializer.Serialize(position))));

        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        var intervals = await GetAsync<List<OpenF1Interval>>(
            $"intervals?session_key={sessionKey}", cancellationToken) ?? [];
        events.AddRange(intervals.Select(interval => new RaceReplayEvent(
            interval.Date,
            "interval",
            interval.DriverNumber,
            null,
            FormatValue(interval.Interval),
            FormatValue(interval.GapToLeader),
            null,
            null,
            null,
            JsonSerializer.Serialize(interval))));

        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        var raceControl = await GetAsync<List<OpenF1RaceControl>>(
            $"race_control?session_key={sessionKey}", cancellationToken) ?? [];
        events.AddRange(raceControl.Select(message => new RaceReplayEvent(
            message.Date,
            "race_control",
            message.DriverNumber,
            null,
            null,
            null,
            message.Category,
            message.Flag,
            message.Message,
            JsonSerializer.Serialize(message))));

        return events
            .OrderBy(item => item.Timestamp)
            .GroupBy(item => ToFrameTimestamp(item.Timestamp))
            .Select(group => new RaceReplayFrame(group.Key, group.ToList()))
            .OrderBy(frame => frame.Timestamp)
            .ToList();
    }

    public async Task<IReadOnlyList<RaceReplayDriver>> GetDriversAsync(
        int sessionKey,
        CancellationToken cancellationToken = default)
    {
        if (sessionKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionKey));
        }

        var drivers = await GetAsync<List<RaceReplayDriver>>(
            $"drivers?session_key={sessionKey}", cancellationToken) ?? [];

        return drivers
            .GroupBy(driver => driver.DriverNumber)
            .Select(group => group.First())
            .OrderBy(driver => driver.DriverNumber)
            .ToList();
    }

    public async IAsyncEnumerable<RaceReplayFrame> ReplayAsync(
        IReadOnlyList<RaceReplayFrame> frames,
        int startIndex,
        DateTimeOffset? previousTimestamp,
        double speedMultiplier,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (speedMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedMultiplier));
        }

        for (var index = Math.Max(0, startIndex); index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index];

            if (previousTimestamp is { } previous)
            {
                var delay = (frame.Timestamp - previous).TotalMilliseconds / speedMultiplier;
                if (delay > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
                }
            }

            yield return frame;
            previousTimestamp = frame.Timestamp;
        }
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await httpClient.GetAsync(relativeUrl, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 2)
            {
                var retryDelay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
    }

    private static void ValidateYear(int year)
    {
        if (year < FirstSupportedYear || year > DateTime.UtcNow.Year)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }
    }

    private static DateTimeOffset ToFrameTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
    }

    private static string? FormatValue(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private sealed record OpenF1Position(
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("driver_number")] int DriverNumber,
        [property: JsonPropertyName("position")] int Position);

    private sealed record OpenF1Interval(
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("driver_number")] int DriverNumber,
        [property: JsonPropertyName("interval")] JsonElement? Interval,
        [property: JsonPropertyName("gap_to_leader")] JsonElement? GapToLeader);

    private sealed record OpenF1RaceControl(
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("flag")] string? Flag,
        [property: JsonPropertyName("driver_number")] int? DriverNumber,
        [property: JsonPropertyName("message")] string? Message);
}

public sealed record RaceReplaySession(
    [property: JsonPropertyName("session_key")] int SessionKey,
    [property: JsonPropertyName("meeting_key")] int MeetingKey,
    [property: JsonPropertyName("session_name")] string SessionName,
    [property: JsonPropertyName("date_start")] DateTimeOffset DateStart,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("country_name")] string CountryName,
    [property: JsonPropertyName("circuit_short_name")] string CircuitShortName,
    [property: JsonPropertyName("is_cancelled")] bool IsCancelled);

public sealed record RaceReplayDriver(
    [property: JsonPropertyName("driver_number")] int DriverNumber,
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("name_acronym")] string? NameAcronym,
    [property: JsonPropertyName("team_name")] string? TeamName,
    [property: JsonPropertyName("team_colour")] string? TeamColour);

public sealed record RaceReplayFrame(DateTimeOffset Timestamp, IReadOnlyList<RaceReplayEvent> Events);

public sealed record RaceReplayEvent(
    DateTimeOffset Timestamp,
    string Kind,
    int? DriverNumber,
    int? Position,
    string? Interval,
    string? GapToLeader,
    string? Category,
    string? Flag,
    string? Message,
    string PayloadJson);
