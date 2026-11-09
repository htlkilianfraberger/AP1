# Constructor dashboard checks

Run from the solution directory with the application stopped:

```powershell
dotnet run --project tests/Constructors.Checks -- --webroot "$PWD/AP1/wwwroot"
```

Add `--live` to validate the current OpenF1 season, total wins/podiums and all
eleven team logos. Use `-p:OutputPath=obj/verification/` before `--` if the running
application locks the normal build output.

Fixture checks cover driver transfers, double podiums, disqualifications, missing
statistics, an empty season, an unpublished latest race and non-race API records.
The Razor render check uses the actual page render tree with supplied standings.
It checks logo binding, team colours and the requested statistics columns.
