# 09 · UI text (English)

The machine-readable file is `spec/strings.en.json`. Upper case is applied by the style, not by the text.

Number formatting follows English conventions: the decimal separator is a **point** ("0.5s", "77.85 °C"), never a comma. Units follow the rules in 05 §9.

| Key | Text |
|---|---|
| popup.title | Monitor |
| popup.details | Details |
| popup.settings.title | Settings |
| popup.cpu / gpu / mem | CPU / GPU / MEM |
| popup.disk / net | DISK / NETWORK |
| popup.disk.read / write | R / W |
| tooltip.close / back / settings / details | Close / Back / Settings / Open details |
| settings.color | Color · {name} |
| settings.theme | Theme: Light · Dark · System (ids `light` · `dark` · `system`) |
| settings.refresh | Refresh: 0.5s · 1s · 2s |
| settings.history | History: 30s · 60s · 120s |
| settings.netscale | Network scale: Separate · Shared (ids `independent` · `shared`) |
| settings.opacity | Opacity |
| settings.autostart | Start with Windows |
| settings.autohide | Close on focus loss |
| palettes | Electric · Neon · Lime · Ember · Violet · Steel (ids `electric` · `neon` · `lime` · `ember` · `violet` · `steel`) |
| details.title | System details |
| details.sub | {n} devices · {m} entries |
| details.loading | Loading sensors… |
| details.search | Filter sensors |
| details.chip.zero | Zeros (tooltip: Show always-zero sensors) |
| details.chip.limits | Limits (tooltip: Show limits and SPD metadata) |
| details.max / restore | Maximize / Restore |
| details.meta | {n} sensors · {id} |
| details.meta.modules | {n} modules · module {i} in slot {s}, … |
| details.min / max / avg / stable | min {v} / max {v} / avg {v} / stable {v} |
| details.empty.filter | No visible readings with the current filters. |
| details.empty.search | No sensor matches the filter. |
| details.footer | {n} sensors visible · {z} zeros hidden · {l} limits/SPD hidden |
| details.refresh | refreshes every {t} |
| details.new | NEW |
| details.admin | Some readings (CPU temperature and power, motherboard, RAM SPD) require running as administrator. |
| details.admin.btn | Restart as admin |
| tag.* | no reading · partial · margin · accumulated · estimate · limit · SPD |
| cat.* | CPU · GPU · Memory · Motherboard · Storage · Network · PSU · Controller · Battery · Power |
| type.* | Temperature · Load · Power · Clock · Voltage · Current · Fans · Fan control · Flow · Memory · Data · Throughput · Level · Counters · SPD timings |
| dev.fallback | Network adapter {n} · Disk {n} · Memory {n} |
| mem.src | Physical · Virtual · Module {n} |
| tray.menu | Open · Details · Settings · Quit |
| tray.tooltip | ↓ {down}  ↑ {up} |

## Terms used by the shipping app

Keep these English terms consistent with the app's own UI (thermal control, popup tabs and Details): Settings · Details · Thermal control · GAME MODE · CURVES · SILENT · STD · TURBO · FULL · FIXED · MANUAL · OFF · MOTHERBOARD · WC RYUJIN II · PUMP · RADIATOR · EMB. · LIQ · QUIT · About · network · STORAGE · FAN SPEED.
