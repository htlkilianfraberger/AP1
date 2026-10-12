namespace AP1.Services;

public static class TeamIdentity
{
    public static string Key(string name) => name.Trim().ToLowerInvariant() switch
    {
        var n when n.Contains("red bull") => "redbullracing",
        var n when n.Contains("racing bulls") || n is "rb" || n.Contains("rb f1") || n.Contains("visa cash") => "racingbulls",
        var n when n.Contains("mercedes") => "mercedes",
        var n when n.Contains("ferrari") => "ferrari",
        var n when n.Contains("mclaren") => "mclaren",
        var n when n.Contains("aston") => "astonmartin",
        var n when n.Contains("alpine") => "alpine",
        var n when n.Contains("williams") => "williams",
        var n when n.Contains("haas") => "haasf1team",
        var n when n.Contains("audi") => "audi",
        var n when n.Contains("cadillac") => "cadillac",
        var n when n.Contains("sauber") => "kicksauber",
        var n when n.Contains("alfa romeo") => "alfaromeo",
        var n when n.Contains("alphatauri") || n.Contains("alpha tauri") => "alphatauri",
        var n => n
    };

    // Fallback only; normally colours come from the drivers API for the selected season.
    public static string Colour(string name) => Key(name) switch
    {
        "mercedes" => "#00D7B6", "ferrari" => "#ED1131", "mclaren" => "#F47600",
        "redbullracing" => "#4781D7", "racingbulls" => "#6C98FF", "alpine" => "#00A1E8",
        "haasf1team" => "#9C9FA2", "audi" => "#F50537", "williams" => "#1868DB",
        "astonmartin" => "#229971", "cadillac" => "#909090", "kicksauber" => "#52E252",
        "alfaromeo" => "#C92D4B", "alphatauri" => "#5E8FAA", _ => "#8a8f98"
    };

    public static string? Logo(string name) => Key(name) switch
    {
        "mercedes" or "ferrari" or "mclaren" or "redbullracing" or "racingbulls" or "alpine"
            or "haasf1team" or "audi" or "williams" or "astonmartin" or "cadillac"
            or "kicksauber" => $"team-logos/{Key(name)}.webp",
        "alfaromeo" or "alphatauri" => $"team-logos/{Key(name)}.png",
        _ => null
    };
}
