using System.Net;
using System.Text.Json;

namespace AP1.Services;

public sealed record ApiLoadError(string Title, string Message)
{
    public static ApiLoadError FromException(Exception exception, string resource) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            new("Zu viele Anfragen", "OpenF1 begrenzt gerade die Anfragen. Warte kurz und versuche es dann erneut."),
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } =>
            new("Datenquelle nicht erreichbar", "OpenF1 ist vorübergehend nicht verfügbar. Bitte versuche es später erneut."),
        HttpRequestException =>
            new("Verbindung fehlgeschlagen", $"Die {resource} konnten nicht von OpenF1 geladen werden. Prüfe deine Verbindung und versuche es erneut."),
        TaskCanceledException =>
            new("Zeitüberschreitung", $"Das Laden der {resource} dauert zu lange. Bitte versuche es erneut."),
        JsonException =>
            new("Daten konnten nicht gelesen werden", "OpenF1 hat unerwartete Daten geliefert. Bitte versuche es später erneut."),
        _ =>
            new("Laden fehlgeschlagen", $"Die {resource} konnten nicht geladen werden. Bitte versuche es erneut.")
    };
}
