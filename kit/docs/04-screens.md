# 04 · Screens (detailed specification)

All measurements are in DIP (px at 100%). The visual reference is `screenshots/` + `prototype/`. Fonts: **BC** = Barlow Condensed, **B** = Barlow.

---
## A. Tray icon (16×16, redrawn on every tick)
- 1 px outline square `#98989B` (0.5,0.5 → 15.5,15.5)
- Horizontal center line at y=8, color `#7A7A7D`
- Left bar (x=3, width 4) **rising** from the center, with height = round(min(1, down/scaleDown) × 6), color = palette A
- Right bar (x=9, width 4) **descending** from the center, with height = round(min(1, up/scaleUp) × 6), color = palette B
- Generate as a 32bpp `Bitmap` → `Icon.FromHandle(bmp.GetHicon())`; call `DestroyIcon` on the previous handle (so GDI handles do not leak). For 150/200% DPI, render at 24/32 px.
- Tooltip: `↓ 11.3 MB/s  ↑ 166 KB/s` (max. 127 characters)
- Left click: opens/closes the popup. Right click: menu with **Open · Details · Settings · — · Quit**.

---
## B. Popup (344 px wide)
Position: bottom-right corner of the work area of the monitor that contains the tray; 12 px from the right edge and 12 px above the taskbar (taskbar on other edges: see 07). Blueprint frame. See `screenshots/01-popup-summary-dark.png`.

### B1. Header (padding 4/6/4/10, bottom border `line`)
- Left: 6×6 square split diagonally (A top-left, B bottom-right) + gap 6 + "MONITOR" (BC 600, 12 px, 0.08em, uppercase).
- Right (BC 11 px `muted`, gap 2):
  - interval label ("1s" / "500ms" / "2s"), right margin 4
  - **DETAILS** button (height 22, padding 0 6, external-link icon 12 + 11 px uppercase text); `hover` background when the Details window is open
  - 22×22 gear button (`hover` background when Settings is active)
  - 22×22 close button (hides the popup)

### B2. Metrics row: grid of 3 equal columns, vertical dividers and bottom border
Each cell has padding 5/9/6 and vertical gap 2:
1. Label line: on the left "CPU" / "GPU" / "MEM" (BC 11, 0.1em, `muted`); on the right "44°C" (CPU/GPU) or "35%" (MEM), BC 11 `muted`, tabular.
2. Value line:
   - CPU/GPU: **"16"** (BC 600, 22 px, line-height 1) + "%" (BC 12 `muted`) on the left; **"30"** (BC 500, 13) + " W" (11 `muted`) on the right.
   - MEM: **"11.3"** (22/600) + "/32 GB" (12 `muted`). The total comes from the system (rounded to a whole GB).
3. 2 px high bar: `track` track, `fill` fill with width = %.

### B3. Disk row: grid `auto | 1fr | 1fr`, dividers, bottom border; cell padding 4/9
"DISK" (BC 11, 0.1em, `muted`) | "R" (11 `muted`) + **"54.0"** (BC 600 16) + "MB/s" (11 `muted`) | "W" + value + "MB/s". Always MB/s with 1 decimal place.

### B4. Network row: same grid, no bottom border
"NETWORK" | arrow-down icon 10 px in `dstroke` + value + unit | arrow-up icon 10 px in `ustroke` + value + unit. Automatic unit: < 1024 KB/s → whole "KB/s"; otherwise "MB/s" with 1 decimal place.

### B5. Mirrored network chart (margin 0/9/8, height 64, top and bottom borders `line`)
- Center line at y=32 (`line`).
- **Download upward**: filled area `dfill` + 1.25 px outline `dstroke`.
- **Upload downward**: area `ufill` + 1.25 px outline `ustroke`.
- Each half has an amplitude of 30 px. N points = history (30/60/120); newest on the right, evenly distributed across the width.
- Automatic "nice number" scale (see 06); Separate or Shared mode.
- Labels (BC 10 `muted`, tabular): top-right corner "↓ 19.5 MB/s" and bottom-right corner "↑ 1000 KB/s".

### B6. Settings (replaces B2–B5; see `screenshots/02-popup-settings-dark.png`)
The header shows "<" (chevron-left 13, 20×20 button) + "SETTINGS". Each row: padding 6/10 (palette 7/10), bottom border `line`, label on the left in BC 12, 0.08em, uppercase, `muted`; control on the right.

| Row | Control | Options | Default |
|---|---|---|---|
| COLOR · {name} | 6 squares of 20×20 split diagonally (A/B), gap 7; the selected one has a 1 px `text` outline with an offset of 2 | see 03 | Electric |
| THEME | segmented | Light / Dark / System | Dark |
| REFRESH | segmented | 0.5s / 1s / 2s | 1s |
| HISTORY | segmented | 30s / 60s / 120s | 60s |
| NETWORK SCALE | segmented | Separate / Shared | Separate |
| OPACITY | slider 40–100, step 5, width 130 + "90%" (width 32, right-aligned) | | 90 |
| START WITH WINDOWS | 15 px checkbox | | on |
| CLOSE ON FOCUS LOSS | 15 px checkbox | | on |

**Segmented**: 1 px `line` outer border, radius 0, options with padding 3/9, BC 12, 1 px dividers between them; the selected option has a `fill` background and `bg` text. Every change is applied immediately and saved.

---
## C. Details window (see `screenshots/10` to `26`)
Its own window (not a child of the popup), without native chrome, with the blueprint frame, acrylic and the same opacity.
- Default size **960×640**; minimum 720×440; resizable from the edges (`WindowChrome.ResizeBorderThickness=6`).
- Default position: centered on the tray's monitor. Position and size are remembered between sessions (validate that they are still visible on some monitor).
- Drag by the title bar (except over controls). Double click or button → maximize, filling the work area with a margin of 12.
- Does **not** hide on focus loss. Not topmost by default.
- Esc closes. Ctrl+F focuses the search.

### C1. Title bar (padding 6/6/6/12, bottom border, gap 8)
7 px A/B square · "SYSTEM DETAILS" (BC 600, 13, 0.08em) · flexible subtitle "12 devices · 355 entries" (BC 11 `muted`, ellipsis) · search field (width 220, height 24, `line` border, search icon 12, placeholder "Filter sensors", B 12) · **ZEROS** chip · **LIMITS** chip (height 24, padding 0 8, `line` border, BC 11 uppercase; active = `fill` background and `bg` text) · maximize/restore button 24 · close button 24.

### C2. Body: grid `232px | 1fr`
**Device list (left, own scrolling, right border `line`)**. Each item is a full-width button, padding 8/10/6/12, bottom border, gap 3:
1. Line 1: category (BC 10, 0.1em, uppercase, `muted`, flex 1) · NEW tag (BC 10, B background, #111 text) · primary value (BC 600 13): primary temperature "63 °C", or the sensor count when there is no temperature.
2. Line 2: device name (B 500 12, ellipsis).
3. Line 3: 18 px high sparkline of the last 40 ticks of the primary temperature (area `dfill` + 1.25 line `dstroke`); omitted if there is none.
- Selected: `sel` background. Hover: `hover`. The first item in the list starts selected.

**Device panel (right, own scrolling, padding 14/16/18)**
1. **Header**: category (BC 11, 0.12em, `muted`) · name (BC 600 **26**) · meta (BC 11 `muted`): "N sensors · {id}" or, for a consolidated device, "N sensors · 2 modules · module 1 in slot 1, module 2 in slot 3".
2. **Summary cards**: grid `repeat(auto-fill, minmax(200px, 1fr))`, gap 8, top margin 12. **One card per sensor type that has a chartable reading** (in the type order of section 05), using the type's primary sensor (see 06):
   - 1 px `line` border, padding 8/10/6.
   - Top: type label (BC 11, 0.1em, uppercase, `muted`) · on the right the name of the sensor used (BC 11 `muted`, ellipsis).
   - Value: **BC 600 30** + unit BC 13 `muted`. On the right, a 38×38 **circular gauge** (r=15, stroke 5, `track` track, arc in the card's color starting at 12 o'clock) only for % and °C (scale 0–100).
   - 58 px high **area chart** with the last 120 ticks; bottom border `line`. Even-index cards use `dstroke`/`dfill` and odd-index ones `ustroke`/`ufill` (A/B alternation).
   - Footer BC 10 `muted`: "min X" on the left and "max Y" on the right. If min == max after formatting: only "stable X".
   - No card is created if the primary sensor is constant and is not % or °C.
3. **Sections per type** (one for each type that has visible sensors): title (BC 600 13, 0.1em, uppercase) + meta on the right (BC 11 `muted`), top margin 18, bottom border `line`. The content is **one** of these:
   - **Cell grid**, if the section has **≥ 9 sensors, all chartable and with the same unit** (e.g. load, clock and temperature of each P/E core): grid `repeat(auto-fill, minmax(36px, 1fr))`, gap 3, 48 px high cells with a `track` background, bottom-up proportional fill (color `fill`, or `ustroke` if ≥ 0.85) and the short value at the top (BC 600 10: integer; GHz with 1 decimal place for MHz; 2 decimal places for V). Tooltip "P-Core #1 · 54.0 °C". Meta: "24 · min X · avg Y · max Z".
   - **Tiles**, in all other cases: grid `repeat(auto-fill, minmax(210px, 1fr))`, gap 6. Each tile has a `line` border and padding 6/8:
     - line 1: name (B 12, ellipsis, tooltip "name · id") + optional tag (BC 9, uppercase, `line` border, `muted`)
     - line 2: value (BC 600 17, minimum width 74) + unit (11 `muted`), followed by **one** visual:
       - 6 px **bar** (`track` track) for % / °C / RPM
       - 24 px **sparkline** (area `ufill` + line `ustroke`, last 60 ticks) for W, MHz, V, A, B/s, L/h that vary
       - range **text** "59.9–60.2 GB" (BC 11 `muted`, right-aligned) in all other cases (accumulated, estimates, limits, SPD, constants); empty if min == max after formatting
     - Section meta: the count.
4. Empty due to filters: "No visible readings with the current filters."

### C3. Footer (padding 4/12, top border, BC 10 `muted`)
Left: "259 visible sensors · 43 hidden zeros · 53 hidden limits/SPD" (omit the terms whose count is zero). Right: "refreshes every 1s".

### C4. Search/filter behavior
- The search (case-insensitive, accent-insensitive) matches the sensor name, the device name, the raw type and the English type name. The device list shows only the devices that have results; the panel, only the matching sensors.
- **ZEROS** off (default) hides sensors whose value was 0 in **all** samples of the session (`allZero`).
- **LIMITS** off (default) hides the Limit and Metadata roles.
- The filters do not persist between sessions; the device selection persists while the app is open.
- If the selected device disappears (hot-unplug), select the first one.

### C5. Privilege banner (only when not admin)
Strip at the top of the right panel: `line` border, padding 6/10, B 12: "Some readings (CPU temperature and power, motherboard, RAM SPD) require running as administrator." + "Restart as admin" button.
