# 06 · Algorithms

Executable reference: the `Component` class in the `<script data-dc-script>` of `prototype/Monitor Tray.dc.html`. Below is the pseudocode/C# version. Cover everything with tests in `MonitorTray.Core.Tests`.

## 1. Nice number (network chart scale)
```csharp
static double Nice(double v) {
    v = Math.Max(v, 1);
    double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
    foreach (var m in new[] {1, 2, 5, 10}) if (m * p >= v) return m * p;
    return 10 * p;
}
// Separate (independent): scaleDown = Nice(max(down[visible])), scaleUp = Nice(max(up[visible]))
// Shared:                 both = Nice(max(maxDown, maxUp))
```
Tests: Nice(0)=1, Nice(1)=1, Nice(3)=5, Nice(7)=10, Nice(19.5)=20, Nice(1000)=1000, Nice(1001)=2000.

## 2. Mirrored chart (popup)
Width W, center C=32, amplitude H=30, N=history, step = W/(N−1).
```
pathDown = M0,C  L(i*step, C − (v_i/scaleDown)*H) …  L W,C Z
pathUp   = M0,C  L(i*step, C + (v_i/scaleUp)*H)   …  L W,C Z
```
Draw the area + the outline; the outline has constant thickness (in WPF, a `Path` with `Stretch=None` and coordinates in real px; recompute on `SizeChanged`).

## 3. Area chart / sparkline with gaps (Details)
```
chart(hist[], W, H, fixed?, zeroBase):
  vals = non-null; if empty → nothing
  if fixed (%/°C): lo,hi = 0,100
  else:  lo = zeroBase ? 0 : min(vals); hi = max(vals)
         pad = (hi−lo)*0.15 || |hi|*0.05 || 1
         if !zeroBase: lo −= pad;  hi += pad
  step = W/(len−1)
  for each contiguous run of non-null values (≥2 points):
     line: M p0 L p1 … ; area: M x0,H L p0 … L xn,H Z
  y = H − 1 − clamp01((v−lo)/(hi−lo)) * (H−2)
```
**Null breaks the line (gap). Never interpolate.** zeroBase = true for B/s.
Sizes: card 200×56 (history 120); tile 120×24 (last 60); device list 80×16 (last 40).

## 4. Primary sensor of a set
```
primary(list):
  pool = list without names ~ /distance|tjmax/i   (if that empties it, use list)
  for each regex in order: /^physical/i, /total/i, /package/i, /hot spot|composite/i, /(^|\s)core$/i, /core max/i
      if any matches → return the first match
  return the one with the highest current value
```
Used for: the device value/sparkline (among the non-zero reading Temperature sensors) and the sensor of each card (among the chartable sensors of that type).
Expected cases on the reference hardware: CPU Temp → "CPU Package"; CPU Load → "CPU Total"; GPU Temp → "GPU Core" (via `(^|\s)core$`); SSD → "Composite Temperature"; Memory Load → "Physical · Memory".

## 5. Fraction for bars/gauges/cells
```
frac(x): v = value; null → 0
  if unit ∈ {%, degC}: [0,100]
  else: [0, 1.1 × max(current values of the group)]
  clamp01((v−lo)/(hi−lo))
color = frac ≥ 0.85 ? ustroke : fill
gauge: dasharray = frac × 2π×15, 2π×15; starts at −90°
```

## 6. Grid × tiles decision
```
grid if: count ≥ 9 && all chartable && all units equal
```
Short cell value: MHz → GHz with 1 decimal; V → 2 decimals; others → integer.

## 7. Choosing the tile visual
```
g = chartable(x)
bar   = g && unit ∈ {%, degC, RPM}
spark = g && !bar && min≠max && !allZero
text  = otherwise  → range "min–max unit" (empty if equal after formatting)
```

## 8. Simulation (--simulate mode, identical to the prototype)
```
initial = mean
every tick: if mean null → null; if allZero → 0; if min==max → min
            else v = clamp(prev + (rand−0.5) × (max−min) × 0.3, min, max)
Summary (no LHM): random walk with 6% jumps; CPU 2–100, GPU 0–100,
  GPU W = 18 + gpu×2.6 ± 6, CPU W = 12 + cpu×1.1, CPU °C = 38 + cpu×0.4, GPU °C = 34 + gpu×0.45,
  mem 8–30 GB, network ↓ 0–14000 KB/s, ↑ 0–900 KB/s, disk with random spikes
Pre-fill 120 ticks at startup so the charts start out full.
```

## 9. Network: real rate
```
every tick: bytes = Σ adapters (Up, not Loopback/Tunnel) BytesReceived/BytesSent
rate = (bytes − bytesPrev) / Δt_real_seconds   (Stopwatch, not the nominal interval)
if rate < 0 (counter reset/new adapter) → 0 and resync
```

## 10. Refresh interval
Changing `refreshMs` restarts the timer immediately. The history stores samples (not seconds): "History 60s" at 0.5 s shows 60 samples (30 real seconds). This is the prototype's behavior and must be kept.
