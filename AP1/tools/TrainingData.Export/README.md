# AP1 training data export

Run from the repository root:

```powershell
dotnet run --project AP1/tools/TrainingData.Export -- 2026 data/training
```

The tool exports one driver row per race entry and one team row per race with a classified team finish. It uses OpenF1 race sessions from 2023 onward and writes UTF-8 CSV files. No API key is required for historical data.

The CSV files live in the repository's `data/training` directory, so they can be committed and pushed. Export each season from 2023 through the current year into that directory. For the current year, only completed races with published data are included. Re-run the current year when new race results become available.

`SampleWeight` gives recent races more influence: it is `0.5 ^ (days since race / 365.25)` relative to the export time. Thus each year of age halves the weight, including within a season. When training a model, pass this column as sample weights; it is not a prediction feature. Regenerate all seasons together to use the same reference date.

Features describe only information available before each race: previous classified finishes and the championship's points/position at race start. Finish position, DNF and DSQ are targets. A zero driver finish position means no classified position; missing pre-race data stays empty. First race history is empty. Team keys are normalized using the app's TeamIdentity mapping. Races with unpublished or incomplete endpoint data are skipped and reported on stderr.

Split training and validation by season or chronological race order, not by random rows, to avoid future information leaking into the training set. The export prepares data; it does not train a model.
