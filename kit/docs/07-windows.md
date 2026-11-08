# 07 · Integração com o Windows

## Janelas
| Propriedade | Popup | Detalhes |
|---|---|---|
| WindowStyle | None | None |
| AllowsTransparency | True | True |
| Background | Transparent (o fundo é uma Border com `bg` e alpha) | idem |
| ShowInTaskbar | False | **True** (aparece na barra e no Alt+Tab) |
| Topmost | True | False |
| ResizeMode | NoResize | CanResize (WindowChrome) |
| Ativa ao abrir | sim (`Activate()`) | sim |
| Some ao perder foco | se "Fechar ao perder foco" = on | não |
| WS_EX_TOOLWINDOW | sim (fica fora do Alt+Tab) | não |

## Posicionamento do popup
1. Obter o retângulo da taskbar: `SHAppBarMessage(ABM_GETTASKBARPOS)` → `uEdge` + `rc`.
2. Obter o monitor do ícone: `Shell_NotifyIconGetRect` (via H.NotifyIcon) → `MonitorFromPoint` → `GetMonitorInfo().rcWork`.
3. Converter px físicos para DIP com o DPI do monitor (`GetDpiForMonitor`).
4. Calcular, conforme a borda da taskbar:
   - Inferior: x = work.Right − 12 − W; y = work.Bottom − 12 − H
   - Superior: y = work.Top + 12
   - Direita: x = work.Right − 12 − W; y = work.Bottom − 12 − H
   - Esquerda: x = work.Left + 12
5. Recalcular ao trocar de tela (Resumo ↔ Configurações) mantendo a âncora no canto inferior direito (a altura muda).
- App manifest: `<dpiAwareness>PerMonitorV2</dpiAwareness>`.

## Acrílico / blur
- **Win11 22H2+**: `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE=38, DWMSBT_TRANSIENTWINDOW=3)` + `DwmExtendFrameIntoClientArea(-1)`, com o Background da janela transparente. Também `DWMWA_WINDOW_CORNER_PREFERENCE=33 → DWMWCP_DONOTROUND=1` (cantos retos são obrigatórios).
- **Win10**: `SetWindowCompositionAttribute` com `ACCENT_ENABLE_ACRYLICBLURBEHIND=4` e `GradientColor = 0x01000000` (a cor fica na nossa Border).
- Com opacidade 100%, desligar o blur (economia).
- "Efeitos de transparência" do Windows desligado → sem blur, só a cor com alpha.

## Tema do sistema
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (DWORD 0 = escuro). Ouvir `WM_SETTINGCHANGE` com lParam "ImmersiveColorSet" e reaplicar se o tema for "sistema".

## Autostart
- Não admin: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `MonitorTray = "<exe>" --tray`.
- Admin (recomendado para ter todos os sensores): Tarefa Agendada ONLOGON com RL HIGHEST (ver 02). Ao alternar a opção, criar/remover a tarefa e remover a chave Run correspondente.
- `--tray` inicia sem abrir o popup.

## Instância única
Um `Mutex` nomeado. A segunda instância envia `WM_COPYDATA`/pipe nomeado "show" para a primeira e sai.

## Hot-plug / energia
- `HardwareAdded/Removed` do LHM + `WM_DEVICECHANGE` → reconstruir a lista de dispositivos.
- `PowerModeChanged` (Resume) → `Computer.Reset()` e reabrir os provedores PDH (os contadores invalidam após hibernação).
- Troca de monitor/DPI (`WM_DPICHANGED`, `DisplaySettingsChanged`) → reposicionar o popup e validar a posição do Detalhes.

## Empacotamento
- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- O LHM extrai o driver `WinRing0`/`PawnIO` em runtime. Alguns antivírus sinalizam WinRing0: documentar no README do app e preferir LHM ≥ 0.9.5 (PawnIO).
- Ícone do app: o mesmo desenho do ícone da tray em .ico multi-resolução (16, 24, 32, 48, 256).
- Instalador opcional: Inno Setup ou MSIX (MSIX complica o admin/WinRing0; preferir Inno).
