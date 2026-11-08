# Install, update and remove

## Install or update
1. Extract the ZIP `MarzMonitor-<version>-win-x64.zip`.
2. Double-click **INSTALAR.cmd** (open it normally, **without** "Run as administrator").
3. The window shows whether this is a new install or an update, and the options:
   - Start with Windows (elevated, no UAC at each logon)
   - Start hidden in the tray
   - Desktop shortcut
   - Open when finished
   - Replace GPU by MarzOps (only appears if it is installed with automatic startup or running)
4. Click **Install/Update** and confirm the UAC prompt.

The installer:
- checks the SHA256 of every file before anything else (and again in the elevated step);
- closes a running Marz Monitor through the `Local\MonitorTray.Quit.<SID>` signal: the app exits the normal way and returns the fans to automatic. It only treats an app that did not close as safe if `fan-status.json` (≤10 s) proves that no fan is in manual; otherwise it stops without touching anything;
- installs to `C:\Program Files\MonitorTray\<SID>\` (Administrators write; your account reads and executes);
- keeps the previous version in `_backups` (keeps 2) and rolls back to it if anything fails;
- registers two tasks for your account, in the interactive session, with highest privileges (never SYSTEM):
  - `MonitorTray-<SID>`: opens the app elevated on demand (shortcuts use this one);
  - `MonitorTray-Logon-<SID>`: starts at logon with `--tray` (the "Start with Windows" toggle turns this one on/off);
- creates Start menu shortcuts (and Desktop, if checked) and the entry in Settings › Apps.

## Silent install
`powershell -ExecutionPolicy Bypass -File Installer.ps1 -Silent [-NoDesktop] [-NoStartup] [-NoOpen] [-KeepGpuApp]` — same checks, closes the app safely, asks for UAC and prints the result to the console. By default it replaces GPU by MarzOps; `-KeepGpuApp` leaves it as it is.

## Remove
Settings › Apps › **Marz Monitor** › Uninstall (or run `Uninstall.ps1` in the install folder). Closes the app safely, removes tasks, folder and shortcuts. Your preferences stay in `%APPDATA%\MonitorTray`.

## Where things live
| What | Where |
|---|---|
| Program | `C:\Program Files\MonitorTray\<SID>\` |
| Preferences | `%APPDATA%\MonitorTray\settings.json` |
| Fan status (for the installer) | `%APPDATA%\MonitorTray\state\fan-status.json` |
| Motherboard fans (curve backup during FULL) | `%APPDATA%\MonitorTray\state\ac-user-curves.json` |
| Keyboard screens | `%APPDATA%\MonitorTray\keyboard.json` |
| Keyboard status (diagnostics) | `%APPDATA%\MonitorTray\state\keyboard-status.json` |
| Errors | `%APPDATA%\MonitorTray\logs\errors.log` |

## Armoury Crate
The motherboard fan modes are requested from the ASUS service (AsusFanControlService); Armoury Crate remains the owner of the curves. Before FULL, Marz saves your User curves and restores them when going back to CURVES, on quit and when opening the app (if the app closed midway). Without Armoury Crate, the MOTHERBOARD row does not appear.

## Coexistence with GPU by MarzOps
Marz Monitor replaces GPU by MarzOps (GPU fans and keyboard screen/wheel). With the **Replace** option checked, the installer closes GPU by MarzOps through its own quit signal (the fans go back to automatic) and **disables** the `CmpInf-OLED-Rodinha-<SID>` task. Nothing is deleted: to go back, re-enable the task in Task Scheduler.
On the first run, the keyboard screens are imported from the GPU by MarzOps profile.
If both run together: the fans are read-only in Marz Monitor (shared mutex) and both fight over the keyboard screen — avoid it.
