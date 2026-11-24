# AP1 training datasets

These CSV files are generated from the public OpenF1 API using `AP1/tools/TrainingData.Export`.
They contain completed race data for 2023, 2024, 2025 and the published part of 2026
(last exported race: 2026-11-08). One driver row describes one race entry; one team row
describes a team's best classified result in a race.

Each row contains pre-race features and the race result as target. See the
exporter README for the regeneration command.

The `splits` folder contains chronological train/test files for each entity.
See the exporter README for target definitions and how to regenerate them.
