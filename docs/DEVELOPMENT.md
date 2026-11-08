# Development

## Requirements
.NET SDK **10.0.400** (pinned in `global.json`, no roll-forward), Python 3 for packaging.

## Commands
```powershell
dotnet build src/MonitorTray.App -c Release -warnaserror
dotnet test tests/MonitorTray.Core.Tests -c Release
# offscreen screens to PNG (simulated data, no tray/hardware)
MonitorTray.exe --preview <folder> [--fake-new]
# one real read-only capture (LHM + PDH + fans), no UI
MonitorTray.exe --read-test <new.json>
# UI without hardware or admin
MonitorTray.exe --simulate [sensors.json]
```

## Version and release
1. Bump `<Version>` in `Directory.Build.props` (0.1.x) and record it in `CHANGELOG.md`.
2. `python tools/build-package.py` → `Downloads\MarzMonitor-<version>-win-x64.zip` (refuses to overwrite).
3. Commit + tag `v<version>`.

## Project rules
- No hardware model names in the code (test `NoHardcodedModelNamesInSrc`).
- Never interpolate gaps; null breaks the chart and keeps the previous bar.
- Never display "OK/healthy/failure".
- Tests and agents never write to the fans; only the user clicks.
- `.cs` files in CRLF.
