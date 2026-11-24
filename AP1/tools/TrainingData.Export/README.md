# AP1 training data export

Run from the repository root:

```powershell
dotnet run --project AP1/tools/TrainingData.Export -- 2026 data/training
```

The tool exports one driver row per race entry and one team row per race with a classified team finish. It uses OpenF1 race sessions from 2023 onward and writes UTF-8 CSV files. No API key is required for historical data.

The CSV files live in the repository's `data/training` directory, so they can be committed and pushed. Export each season from 2023 through the current year into that directory. For the current year, only completed races with published data are included. Re-run the current year when new race results become available.

Features describe only information available before each race: previous classified finishes and the championship's points/position at race start. Finish position, DNF and DSQ are targets. A zero driver finish position means no classified position; missing pre-race data stays empty. First race history is empty. Team keys are normalized using the app's TeamIdentity mapping. Races with unpublished or incomplete endpoint data are skipped and reported on stderr.

Split training and validation by season or chronological race order, not by random rows, to avoid future information leaking into the training set. The export prepares data; it does not train a model.

Generate separate training and test files from the existing raw CSVs:

```powershell
dotnet run --project AP1/tools/TrainingData.Export -- split data/training
```

This keeps the raw CSVs and writes `X_train.csv`, `y_train.csv`, `X_test.csv`, and `y_test.csv` under `data/training/splits/drivers` and `data/training/splits/teams`. Training uses 2023-2025 plus the earliest 80% of completed 2026 races; the latest 20% of 2026 races form the test set. The cutoff is by whole race and is shared by drivers and teams. Rows in X and y are aligned by line number. The driver target is `FinishPosition` (0 means no classified position); the team target is `BestFinishPosition`. Names, dates, and session IDs are excluded from X. Categorical columns such as `Location` and `TeamKey` still need encoding for algorithms that require numeric input. Re-run the split after refreshing raw data.
