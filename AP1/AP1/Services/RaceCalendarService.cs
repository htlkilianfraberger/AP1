using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace AP1.Services;

public sealed class RaceCalendarService(HttpClient httpClient)
{
    private const string OpenF1SessionsUrl = "https://api.openf1.org/v1/sessions";

    public async Task<byte[]> CreateCurrentSeasonCalendarAsync(CancellationToken cancellationToken = default)
    {
        var year = DateTime.UtcNow.Year;
        var sessions = await httpClient.GetFromJsonAsync<IReadOnlyList<OpenF1Session>>(
            $"{OpenF1SessionsUrl}?year={year}",
            cancellationToken);

        var calendarSessions = (sessions ?? [])
            .Where(session => !session.IsCancelled && session.DateStart is not null && IsRaceWeekendSession(session.SessionName))
            .OrderBy(session => session.DateStart)
            .ToList();

        var calendar = new StringBuilder();
        AppendLine(calendar, "BEGIN:VCALENDAR");
        AppendLine(calendar, "VERSION:2.0");
        AppendLine(calendar, "PRODID:-//AP1//Rennkalender//DE");
        AppendLine(calendar, "CALSCALE:GREGORIAN");
        AppendLine(calendar, "METHOD:PUBLISH");
        AppendLine(calendar, $"X-WR-CALNAME:Formel 1 Rennkalender {year}");

        foreach (var session in calendarSessions)
        {
            var start = session.DateStart!.Value.UtcDateTime;
            var end = session.DateEnd?.UtcDateTime ?? start.AddHours(GetFallbackDurationHours(session.SessionName));
            var sessionName = TranslateSessionName(session.SessionName);
            var title = $"Formel 1: {session.CountryName} Grand Prix - {sessionName}";
            var location = string.Join(", ", new[] { session.CircuitShortName, session.Location, session.CountryName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            AppendLine(calendar, "BEGIN:VEVENT");
            AppendLine(calendar, $"UID:ap1-f1-{year}-{session.SessionKey}@ap1");
            AppendLine(calendar, $"DTSTAMP:{FormatDate(DateTime.UtcNow)}");
            AppendLine(calendar, $"DTSTART:{FormatDate(start)}");
            AppendLine(calendar, $"DTEND:{FormatDate(end)}");
            AppendLine(calendar, $"SUMMARY:{Escape(title)}");
            AppendLine(calendar, $"LOCATION:{Escape(location)}");
            AppendLine(calendar, $"DESCRIPTION:{Escape("Exportiert mit AP1 ueber OpenF1.")}");
            AppendLine(calendar, "END:VEVENT");
        }

        AppendLine(calendar, "END:VCALENDAR");

        return Encoding.UTF8.GetBytes(calendar.ToString());
    }

    private static bool IsRaceWeekendSession(string sessionName) =>
        sessionName is "Practice 1" or "Practice 2" or "Practice 3" or "Qualifying" or "Sprint Qualifying" or "Sprint" or "Race";

    private static double GetFallbackDurationHours(string sessionName) =>
        sessionName.Equals("Race", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static string TranslateSessionName(string sessionName) =>
        sessionName switch
        {
            "Practice 1" => "Freies Training 1",
            "Practice 2" => "Freies Training 2",
            "Practice 3" => "Freies Training 3",
            "Qualifying" => "Qualifying",
            "Sprint Qualifying" => "Sprint Qualifying",
            "Sprint" => "Sprint",
            "Race" => "Rennen",
            _ => sessionName
        };

    private static string FormatDate(DateTime value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AppendLine(StringBuilder builder, string value) =>
        builder.Append(value).Append("\r\n");

    private sealed record OpenF1Session(
        [property: JsonPropertyName("country_name")] string CountryName,
        [property: JsonPropertyName("date_end")] DateTimeOffset? DateEnd,
        [property: JsonPropertyName("date_start")] DateTimeOffset? DateStart,
        [property: JsonPropertyName("is_cancelled")] bool IsCancelled,
        [property: JsonPropertyName("location")] string Location,
        [property: JsonPropertyName("session_key")] int SessionKey,
        [property: JsonPropertyName("session_name")] string SessionName,
        [property: JsonPropertyName("circuit_short_name")] string CircuitShortName);
}
