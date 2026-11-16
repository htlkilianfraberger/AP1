namespace AP1.Services;

// The venue, not the Grand Prix title, determines the outline and flag.
public static class CircuitArtwork
{
    public static string? CountryCode(string circuit) => circuit switch
    {
        "albert_park" => "au",
        "bahrain" => "bh",
        "shanghai" => "cn",
        "catalunya" => "es",
        "monaco" => "mc",
        "villeneuve" => "ca",
        "red_bull_ring" => "at",
        "silverstone" => "gb",
        "hungaroring" => "hu",
        "spa" => "be",
        "monza" => "it",
        "marina_bay" => "sg",
        "suzuka" => "jp",
        "americas" => "us",
        "rodriguez" => "mx",
        "interlagos" => "br",
        "yas_marina" => "ae",
        "imola" => "it",
        "sepang" => "my",
        "zandvoort" => "nl",
        "jeddah" => "sa",
        "miami" => "us",
        "losail" => "qa",
        "madring" => "es",
        "baku" => "az",
        "vegas" => "us",
        _ => null
    };
    public static string CircuitId(int openF1Key) => openF1Key switch
    {
        10 => "albert_park",
        63 => "bahrain",
        49 => "shanghai",
        15 => "catalunya",
        22 => "monaco",
        23 => "villeneuve",
        19 => "red_bull_ring",
        2 => "silverstone",
        4 => "hungaroring",
        7 => "spa",
        39 => "monza",
        61 => "marina_bay",
        46 => "suzuka",
        9 => "americas",
        65 => "rodriguez",
        14 => "interlagos",
        70 => "yas_marina",
        6 => "imola",
        12 => "sepang",
        55 => "zandvoort",
        149 => "jeddah",
        151 => "miami",
        150 => "losail",
        153 => "madring",
        144 => "baku",
        152 => "vegas",
        _ => ""
    };
    public static string? Map(string circuit) => CountryCode(circuit) is null ? null : $"/circuits/{circuit}.svg";
}
