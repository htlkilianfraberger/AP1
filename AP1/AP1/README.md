# AP1

AP1 ist ein Blazor-Webprojekt auf Basis von .NET 10.

## Voraussetzungen

- .NET SDK 10.0 oder neuer
- Rider, Visual Studio oder ein anderer Editor mit .NET-Unterstuetzung

## Projekt starten

Im Projektordner kann die App direkt ueber die Solution gebaut und gestartet werden:

```powershell
dotnet build .\AP1.sln
dotnet run --project .\AP1\AP1.csproj --launch-profile http
```

Die App ist danach unter folgender Adresse erreichbar:

```text
http://localhost:5277
```

## Mit Docker starten

Voraussetzung ist Docker Desktop (Windows/macOS) oder eine Docker Engine mit Docker Compose. Im Repository-Ordner mit der `docker-compose.yml` ausführen:

```powershell
docker compose up --build
```

Danach ist die App unter [http://localhost:8080](http://localhost:8080) erreichbar. Zum Beenden im selben Terminal `Ctrl+C` drücken. Der Container kann anschließend mit folgendem Befehl entfernt werden:

```powershell
docker compose down
```

Docker baut die Anwendung im SDK-Container und startet das veröffentlichte Ergebnis in einem ASP.NET-Runtime-Container. Lokale `bin`- und `obj`-Ordner werden nicht in das Image übernommen.

## Start in Rider

Fuer Rider ist eine feste Run-Konfiguration im Ordner `.run` hinterlegt. Waehle in Rider die Konfiguration `AP1` aus und starte sie.
## Struktur

```text
AP1/
├── AP1.sln
├── .run/
│   └── AP1.run.xml
└── AP1/
    ├── AP1.csproj
    ├── Program.cs
    ├── Components/
    │   ├── App.razor
    │   ├── Routes.razor
    │   ├── Layout/
    │   └── Pages/
    └── wwwroot/
```

## Aktueller Stand

- Navigation und Sidebar wurden entfernt.
- Die Template-Seiten `Counter` und `Weather` wurden entfernt.
- Die Startseite zeigt nur noch `AP1`.
- Der lokale HTTP-Start laeuft ueber Port `5277`.
