# 03 · Design system ("Industry")

Aesthetic: **technical wireframe / blueprint**. Square corners, 1 px lines, "+" registration marks at the corners, condensed typography, tabular numbers. Color comes from the **palette chosen** by the user (two colors: A = download/primary, B = upload/secondary).

Complete, machine-readable values in `spec/tokens.json`.

## Typography
| Use | Font | Weight |
|---|---|---|
| All numbers, labels, titles, buttons | **Barlow Condensed** | 400 / 500 / 600 |
| Sensor/device names, text fields | **Barlow** | 400 / 500 |
- Bundle the TTFs (Google Fonts, OFL license) as `Resource` and reference them with `pack://application:,,,/Fonts/#Barlow Condensed`.
- **Numbers always tabular**: `Typography.NumeralAlignment="Tabular"` in WPF. Values must not "jump" on every tick.
- Section labels: UPPERCASE, letter-spacing 0.08–0.12 em (in WPF, ~`CharacterSpacing` does not exist; use a `TextBlock` with normal `FontStretch` and spacing applied through a `Run` with glyphs, or accept Barlow Condensed's default spacing, which is already legible; low priority).

## Themes (neutral base)
| Role | Light | Dark |
|---|---|---|
| `bg` (window background) | #F2F2F3 | #18191B |
| `text` | #1D1F20 | #F5F5F8 |
| `muted` (labels, units) | #5D5D60 | #A3A3A8 |
| `line` (1 px borders/dividers) | #1D1F20 @ 15% | #F5F5F8 @ 13% |
| `track` (bar/gauge track) | #1D1F20 @ 9% | #F5F5F8 @ 9% |

## Palettes (A, B)
| id | Name | A (↓, primary) | B (↑, secondary) |
|---|---|---|---|
| `electric` (default) | Electric | #2E7DFF | #00E5C3 |
| `neon` | Neon | #00C8FF | #FF4FD8 |
| `lime` | Lime | #2FE07A | #E8FF3A |
| `ember` | Ember | #FF5A1F | #FFC531 |
| `violet` | Violet | #8B5CFF | #FF5C93 |
| `steel` | Steel | #5980A6 | #94BCE3 |

## Derived roles (computed at runtime from A, B and the theme)
| Role | Dark | Light | Use |
|---|---|---|---|
| `fill` / `dstroke` | A | A mixed 12% with black (OKLCH) | bars, ↓ line, segmented selection, active chips |
| `ustroke` | B | B mixed 30% with black (OKLCH) | ↑ line, ≥85% emphasis |
| `dfill` | A @ 30% | A @ 24% | ↓ area / card area |
| `ufill` | B @ 26% | B @ 30% | ↑ area / tile area |
| `hover` | A @ 14% | A @ 10% | button/row hover |
| `sel` | A @ 24% | A @ 18% | selected item in the device list |

Darkening in OKLCH: convert to OKLCH, multiply L by (1 − fraction) and keep C and h. An acceptable alternative is a linear sRGB mix with black. The precomputed values for the 6 palettes are in `spec/tokens.json`.

**High-load emphasis**: bars and cells with a fraction ≥ 0.85 of the scale use `ustroke` instead of `fill`. There is no alert red (see non-scope).

## Background and transparency
- Window background = `bg` with alpha = opacity (40–100%, default 90%).
- Behind it, the system **acrylic/blur** (Win11 `DWMSBT_TRANSIENTWINDOW`; Win10 `ACCENT_ENABLE_ACRYLICBLURBEHIND`). See `07-windows.md`.
- Shadow: `0 12px 32px rgba(0,0,0,.22)`. In WPF, use a `DropShadowEffect` on an inner Border with a margin so the shadow fits (transparent window), or the native DWM shadow on Win11.

## "Blueprint" frame (mandatory on the popup and Details)
- 1 px border in the `line` color, **radius 0**.
- **4 "+" marks** of 11×11 px centered exactly on each vertex (offset −6 px outward), 1 px stroke in `text` @ 55%. Implement as an `Adorner` or 4 `Path`s positioned in a `Grid` with `ClipToBounds=False`.

## Icons
**Lucide**, stroke 1.5, round linecap/linejoin. Sizes from 10 to 13 px. Export as `Geometry` (path data) in `Icons.xaml`:
`settings`, `x`, `chevron-left`, `chevron-right`, `arrow-down`, `arrow-up`, `external-link`, `search`, `maximize`, `minimize`.

## Interaction
- Hover: `hover` background and icon/text in `text`.
- Keyboard focus: 2 px outline in `fill`, 2 px offset. Never the default dashed focus.
- Disabled: 45% opacity.
- Transitions: bar width and cell height with 400 ms ease-out; theme/palette changes are instant.
