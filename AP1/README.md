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

### Rennkalender und Renn-Details (2026-11-04)

- `/kalender` zeigt standardmäßig die kommenden Grand Prix. Über „Vergangene Rennen anzeigen“ wird die gesamte gewählte Saison sichtbar.
- Jede Rennkarte verlinkt auf `/kalender/{saison}/{runde}` und zeigt den veröffentlichten Zeitplan für Trainings, Qualifying, Sprint-Qualifying, Sprint und Rennen.
- Der Schalter „Streckenzeit statt österreichischer Zeit“ wechselt auf beiden Seiten zwischen Europe/Vienna und der Ortszeit der Strecke; Datum und Sommerzeit werden ebenfalls umgerechnet. Die Auswahl bleibt in den Detail- und Zurück-Links erhalten.
- Die Streckenzeitzonen sind nach Circuit-ID zugeordnet. Für neue Strecken dient der von OpenF1 veröffentlichte UTC-Versatz als Fallback. Ohne Ortszeitdaten wird ausdrücklich UTC mit einem Hinweis angezeigt. Unveröffentlichte Session-Zeiten werden nicht als Mitternacht ausgegeben.
- Die Saisonauswahl umfasst ausschließlich die von OpenF1 unterstützten Saisonen ab 2023. Archiv-Saisonen zeigen alle Rennen ohne Vergangenheitsfilter. Beim Export wird die ausgewählte Archiv-Saison als ICS ausgegeben.
- Vor 2023 stammen schematische Streckenlayouts aus [f1-circuits-svg](https://github.com/MisterSix2295/f1-circuits-svg/tree/f758ce80bb2f9af014eba35d975436381ce3cba9). Die MIT-Lizenz liegt im Asset-Ordner. Die Layoutauswahl berücksichtigt Saison und beim Sakhir-GP 2020 auch das Rennen. Flaggen zeigen das Austragungsland mit dem verwendeten heutigen Flaggensatz.
- Kommende Rennen bieten aufklappbare Streckenrekorde direkt im Kalender.
- Alle Strecken der Saisonen 2023–2026 haben lokale SVG-Streckenübersichten aus [F1 Circuits](https://github.com/bacinger/f1-circuits/tree/8094e957b8d6aa8b9f4510c93968556cccd30e02) und Landesflaggen aus [flag-icons 7.6.0](https://github.com/lipis/flag-icons). Die MIT-Lizenzen stehen bei den Assets. Die Übersichten zeigen den Streckenverlauf schematisch, nicht jede historische Layoutvariante.
- Flagge, Karte, Historie und Zeitzone richten sich nach dem Austragungsort: 2026 fährt der Bahrain Grand Prix in Sepang (Malaysia).
- Die Detailseite enthält eine Streckenkarte und die veröffentlichten Klassifikationen für Rennen, Qualifying und Sprint, einschließlich Startplatz, Status, Runden, Punkten und schnellster Rennrunde.
- Historische Streckenstatistiken zeigen schnellste erfasste Runde, schnellste Rennrunde sowie Ranglisten für Siege und Starts von P1. Gleichstände bleiben sichtbar.
- Kalender, Klassifikationen, Streckenstatistik, Streckenbilder und UTC-Versatz stammen aus [OpenF1](https://openf1.org/docs/). Die Datenabdeckung beginnt 2023. Die Daten werden serverseitig gecacht und mit begrenzter Anfragerate abgerufen. Ergebnisse und Statistik haben getrennte Lade- und Fehlerzustände.
- Datenabdeckung: Qualifying-Zeiten ab 1994 und schnellste Rennrunden ab 2004, soweit in der Quelle vorhanden. Die schnellste erfasste Runde umfasst Qualifying und Rennen, keine Trainings- oder Testrunden. Historische Layouts werden gemeinsam betrachtet. „Pole-Starts“ zählt Startplatz 1 im Hauptrennen und kann bei historischen Sprintformaten von der offiziellen Pole-Statistik abweichen. Diese Grenzen werden auch in der Oberfläche angegeben.
- Bestehender Kalenderexport unter `/rennkalender/export` bleibt verfügbar und verwendet OpenF1.

Prüfungen (ohne zusätzliche Testpakete):

```powershell
dotnet run --project ./tests/RaceWeekend.Checks -c Release
# Zusätzlich echte API-Daten prüfen:
dotnet run --project ./tests/RaceWeekend.Checks -c Release -- --live
```

### Historischer Template-Stand

- Navigation und Sidebar wurden entfernt.
- Die Template-Seiten `Counter` und `Weather` wurden entfernt.
- Die Startseite zeigt nur noch `AP1`.
- Der lokale HTTP-Start laeuft ueber Port `5277`.
