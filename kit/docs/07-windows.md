# 07 · Windows integration

## Windows
| Property | Popup | Details |
|---|---|---|
| WindowStyle | None | None |
| AllowsTransparency | True | True |
| Background | Transparent (the background is a Border with `bg` and alpha) | same |
| ShowInTaskbar | False | **True** (shows in the taskbar and in Alt+Tab) |
| Topmost | True | False |
| ResizeMode | NoResize | CanResize (WindowChrome) |
| Activated on open | yes (`Activate()`) | yes |
| Hides on focus loss | if "Close on focus loss" = on | no |
| WS_EX_TOOLWINDOW | yes (stays out of Alt+Tab) | no |

## Popup positioning
1. Get the taskbar rectangle: `SHAppBarMessage(ABM_GETTASKBARPOS)` → `uEdge` + `rc`.
2. Get the icon's monitor: `Shell_NotifyIconGetRect` (via H.NotifyIcon) → `MonitorFromPoint` → `GetMonitorInfo().rcWork`.
3. Convert physical px to DIP using the monitor DPI (`GetDpiForMonitor`).
4. Compute, according to the taskbar edge:
   - Bottom: x = work.Right − 12 − W; y = work.Bottom − 12 − H
   - Top: y = work.Top + 12
   - Right: x = work.Right − 12 − W; y = work.Bottom − 12 − H
   - Left: x = work.Left + 12
5. Recompute when switching screens (Summary ↔ Settings), keeping the anchor at the bottom-right corner (the height changes).
- App manifest: `<dpiAwareness>PerMonitorV2</dpiAwareness>`.

## Acrylic / blur
- **Win11 22H2+**: `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE=38, DWMSBT_TRANSIENTWINDOW=3)` + `DwmExtendFrameIntoClientArea(-1)`, with a transparent window Background. Also `DWMWA_WINDOW_CORNER_PREFERENCE=33 → DWMWCP_DONOTROUND=1` (square corners are mandatory).
- **Win10**: `SetWindowCompositionAttribute` with `ACCENT_ENABLE_ACRYLICBLURBEHIND=4` and `GradientColor = 0x01000000` (the color lives in our Border).
- At 100% opacity, turn the blur off (saves resources).
- Windows "Transparency effects" turned off → no blur, only the color with alpha.

## System theme
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (DWORD 0 = dark). Listen for `WM_SETTINGCHANGE` with lParam "ImmersiveColorSet" and reapply if the theme is "system".

## Autostart
- Non-admin: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `MonitorTray = "<exe>" --tray`.
- Admin (recommended to get all sensors): ONLOGON Scheduled Task with RL HIGHEST (see 02). When toggling the option, create/remove the task and remove the corresponding Run key.
- `--tray` starts without opening the popup.

## Single instance
A named `Mutex`. The second instance sends "show" via `WM_COPYDATA`/named pipe to the first one and exits.

## Hot-plug / power
- LHM `HardwareAdded/Removed` + `WM_DEVICECHANGE` → rebuild the device list.
- `PowerModeChanged` (Resume) → `Computer.Reset()` and reopen the PDH providers (the counters become invalid after hibernation).
- Monitor/DPI change (`WM_DPICHANGED`, `DisplaySettingsChanged`) → reposition the popup and validate the Details position.

## Packaging
- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- LHM extracts the `WinRing0`/`PawnIO` driver at runtime. Some antivirus products flag WinRing0: document it in the app README and prefer LHM ≥ 0.9.5 (PawnIO).
- App icon: the same drawing as the tray icon, as a multi-resolution .ico (16, 24, 32, 48, 256).
- Optional installer: Inno Setup or MSIX (MSIX complicates admin/WinRing0; prefer Inno).
