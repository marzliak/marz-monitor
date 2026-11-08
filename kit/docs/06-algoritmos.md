# 06 · Algoritmos

Referência executável: a classe `Component` no `<script data-dc-script>` de `prototype/Monitor Tray.dc.html`. Abaixo, a versão em pseudocódigo/C#. Cubra tudo com testes em `MonitorTray.Core.Tests`.

## 1. Nice number (escala do gráfico de rede)
```csharp
static double Nice(double v) {
    v = Math.Max(v, 1);
    double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
    foreach (var m in new[] {1, 2, 5, 10}) if (m * p >= v) return m * p;
    return 10 * p;
}
// Separada: scaleDown = Nice(max(down[visíveis])), scaleUp = Nice(max(up[visíveis]))
// Única:    ambos = Nice(max(maxDown, maxUp))
```
Testes: Nice(0)=1, Nice(1)=1, Nice(3)=5, Nice(7)=10, Nice(19.5)=20, Nice(1000)=1000, Nice(1001)=2000.

## 2. Gráfico espelhado (popup)
Largura W, centro C=32, amplitude H=30, N=histórico, step = W/(N−1).
```
pathDown = M0,C  L(i*step, C − (v_i/scaleDown)*H) …  L W,C Z
pathUp   = M0,C  L(i*step, C + (v_i/scaleUp)*H)   …  L W,C Z
```
Desenhar a área + o contorno; contorno com espessura constante (no WPF, `Path` com `Stretch=None` e coordenadas em px reais; recalcular no `SizeChanged`).

## 3. Gráfico de área / sparkline com lacunas (Detalhes)
```
chart(hist[], W, H, fixed?, zeroBase):
  vals = não-nulos; se vazio → nada
  se fixed (%/°C): lo,hi = 0,100
  senão: lo = zeroBase ? 0 : min(vals); hi = max(vals)
         pad = (hi−lo)*0.15 || |hi|*0.05 || 1
         se !zeroBase: lo −= pad;  hi += pad
  step = W/(len−1)
  para cada trecho contíguo de valores não-nulos (≥2 pontos):
     linha: M p0 L p1 … ; área: M x0,H L p0 … L xn,H Z
  y = H − 1 − clamp01((v−lo)/(hi−lo)) * (H−2)
```
**Null quebra a linha (lacuna). Nunca interpolar.** zeroBase = true para B/s.
Tamanhos: cartão 200×56 (histórico 120); tile 120×24 (últimos 60); lista de dispositivos 80×16 (últimos 40).

## 4. Sensor principal de um conjunto
```
primary(list):
  pool = list sem nomes ~ /distance|tjmax/i   (se esvaziar, usa list)
  para cada regex na ordem: /^física/i, /total/i, /package/i, /hot spot|composite/i, /(^|\s)core$/i, /core max/i
      se algum casa → retorna o primeiro que casa
  retorna o de maior valor atual
```
Usado em: valor/sparkline do dispositivo (entre as Temperature de leitura não-zeradas) e sensor de cada cartão (entre os graficáveis do tipo).
Casos esperados no hardware de referência: CPU Temp → "CPU Package"; CPU Load → "CPU Total"; GPU Temp → "GPU Core" (via `(^|\s)core$`); SSD → "Composite Temperature"; Memória Carga → "Física · Memory".

## 5. Fração para barras/gauges/células
```
frac(x): v = valor; null → 0
  se unidade ∈ {%, degC}: [0,100]
  senão: [0, 1.1 × max(valores atuais do grupo)]
  clamp01((v−lo)/(hi−lo))
cor = frac ≥ 0.85 ? ustroke : fill
gauge: dasharray = frac × 2π×15, 2π×15; começa em −90°
```

## 6. Decisão grade × tiles
```
grid se: count ≥ 9 && todos graficáveis && todas as unidades iguais
```
Valor curto da célula: MHz → GHz com 1 casa; V → 2 casas; demais → inteiro.

## 7. Escolha do visual do tile
```
g = graficável(x)
bar   = g && unidade ∈ {%, degC, RPM}
spark = g && !bar && min≠max && !allZero
text  = senão  → faixa "min–max unidade" (vazia se igual após formatar)
```

## 8. Simulação (modo --simulate, idêntica ao protótipo)
```
inicial = mean
a cada tick: se mean null → null; se allZero → 0; se min==max → min
            senão v = clamp(prev + (rand−0.5) × (max−min) × 0.3, min, max)
Resumo (sem LHM): passeio aleatório com saltos de 6%; CPU 2–100, GPU 0–100,
  GPU W = 18 + gpu×2.6 ± 6, CPU W = 12 + cpu×1.1, CPU °C = 38 + cpu×0.4, GPU °C = 34 + gpu×0.45,
  mem 8–30 GB, rede ↓ 0–14000 KB/s, ↑ 0–900 KB/s, disco com picos aleatórios
Pré-preencher 120 ticks no início para os gráficos já nascerem cheios.
```

## 9. Rede: taxa real
```
a cada tick: bytes = Σ adaptadores (Up, não Loopback/Tunnel) BytesReceived/BytesSent
rate = (bytes − bytesPrev) / Δt_real_segundos   (Stopwatch, não o intervalo nominal)
se rate < 0 (reset do contador/adaptador novo) → 0 e ressincroniza
```

## 10. Intervalo de atualização
Mudar `refreshMs` reinicia o timer imediatamente. O histórico guarda amostras (não segundos): "Histórico 60s" a 0,5 s mostra 60 amostras (30 s reais). Esse é o comportamento do protótipo e deve ser mantido.
