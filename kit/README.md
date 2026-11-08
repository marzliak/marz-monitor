# Design spec (kit)

The original specification Marz Monitor was built from: product scope, architecture, design tokens, screen layouts, sensor normalization rules and algorithms.

| File | Content |
|---|---|
| `docs/01-product.md` | Product, audience, scope |
| `docs/02-architecture.md` | Stack, .NET solution, layers, threads |
| `docs/03-design-system.md` | Tokens, light/dark themes, 6 palettes, typography, "blueprint" frame |
| `docs/04-screens.md` | Popup, settings, Details window, tray icon, pixel by pixel |
| `docs/05-sensors.md` | Data sources, model, normalization, quality rules |
| `docs/06-algorithms.md` | Scales, charts, formatting, primary sensor, grids (pseudocode) |
| `docs/07-windows.md` | Tray, positioning, acrylic, admin, autostart, DPI, multi-monitor |
| `docs/08-acceptance-and-plan.md` | Plan and acceptance checklist |
| `docs/09-ui-text.md` | UI strings |
| `spec/*.json` | Tokens, settings schema, sensor model schema, strings |
| `prototype/data/sensors.json` | Sensor snapshot used by `--simulate`, `--preview` and the tests |

Some documents mention the original HTML prototype, the reference screenshots and the raw hardware survey. They are not part of this public repository; the app's own `--preview` renders every screen (see `screenshots/`).
