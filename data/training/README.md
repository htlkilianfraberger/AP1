# AP1 training datasets

These CSV files are generated from the public OpenF1 API using `AP1/tools/TrainingData.Export`.
They contain completed race data for 2023, 2024, 2025 and the published part of 2026
(last exported race: 2026-11-08). One driver row describes one race entry; one team row
describes a team's best classified result in a race.

Each row contains only pre-race features, the race result as target, and a `SampleWeight`
that increases for more recent races. The weight is recalculated when the files are
regenerated and should be passed to the ML algorithm as a sample weight, not a feature.
See the exporter README for the exact formula and regeneration command.
