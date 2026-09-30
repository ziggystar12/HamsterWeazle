# Image tools checks

Run from the repository root:

```powershell
dotnet run --project tests/ImageToolsSmoke/ImageToolsSmoke.csproj -c Release
dotnet run --project tests/DmkRoutingSmoke/DmkRoutingSmoke.csproj -c Release
dotnet build Mac/HamsterWeazle.csproj -c Debug -p:RuntimeIdentifier=
```

The Core checks cover track parsing, partial-cylinder grids, format confidence,
FAT12 image creation/import/extraction, corrupt filesystem rejection, reports,
cancellation and rescue across multiple captures. The test executable acts as a
fake GreaseWeazle tool; no hardware command is issued.

The Windows smoke suite also renders both themes with the track map shown and
hidden, checks the image browser, map preference persistence, drive routing,
DMK diagnostics and cancellation during Auto Read probes. Set
`HAMSTERWEAZLE_SMOKE_EVIDENCE_DIR` to choose its screenshot destination.

For an independently generated FAT12 fixture, set
`HAMSTERWEAZLE_FAT12_FIXTURE` to an image containing
`NESTED/DEEP/HELLO.TXT` with the ASCII payload
`Independent nested FAT12 fixture.` followed by a line feed. This adds checks
for nested-directory listing and extraction.

The suites leave generated images and reports in their printed evidence
directories. These checks and a Windows-hosted Mac compile do not qualify
physical drives, worn media or native macOS UI behavior.
