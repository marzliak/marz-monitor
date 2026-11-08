# 03 · Design system ("Industry")

Estética: **wireframe técnico / blueprint**. Cantos retos, linhas de 1 px, marcas de registro "+" nos cantos, tipografia condensada, números tabulares. A cor vem da **paleta escolhida** pelo usuário (duas cores: A = download/primária, B = upload/secundária).

Valores completos e legíveis por máquina em `spec/tokens.json`.

## Tipografia
| Uso | Fonte | Peso |
|---|---|---|
| Todos os números, rótulos, títulos, botões | **Barlow Condensed** | 400 / 500 / 600 |
| Nomes de sensores/dispositivos, campos de texto | **Barlow** | 400 / 500 |
- Empacote os TTF (Google Fonts, licença OFL) como `Resource` e referencie com `pack://application:,,,/Fonts/#Barlow Condensed`.
- **Números sempre tabulares**: `Typography.NumeralAlignment="Tabular"` no WPF. Valores não podem "pular" a cada tick.
- Rótulos de seção: CAIXA ALTA, letter-spacing 0.08–0.12 em (no WPF, ~`CharacterSpacing` não existe; use `TextBlock` com `FontStretch` normal e um espaçamento aplicado por um `Run` com glyphs, ou aceite o espaçamento padrão do Barlow Condensed, que já é legível; prioridade baixa).

## Temas (base neutra)
| Papel | Claro | Escuro |
|---|---|---|
| `bg` (fundo da janela) | #F2F2F3 | #18191B |
| `text` | #1D1F20 | #F5F5F8 |
| `muted` (rótulos, unidades) | #5D5D60 | #A3A3A8 |
| `line` (bordas/divisórias 1 px) | #1D1F20 @ 15% | #F5F5F8 @ 13% |
| `track` (trilho de barras/gauges) | #1D1F20 @ 9% | #F5F5F8 @ 9% |

## Paletas (A, B)
| id | Nome | A (↓, primária) | B (↑, secundária) |
|---|---|---|---|
| `eletrico` (padrão) | Elétrico | #2E7DFF | #00E5C3 |
| `neon` | Neon | #00C8FF | #FF4FD8 |
| `lima` | Lima | #2FE07A | #E8FF3A |
| `brasa` | Brasa | #FF5A1F | #FFC531 |
| `violeta` | Violeta | #8B5CFF | #FF5C93 |
| `aco` | Aço | #5980A6 | #94BCE3 |

## Papéis derivados (calcular em runtime a partir de A, B e tema)
| Papel | Escuro | Claro | Uso |
|---|---|---|---|
| `fill` / `dstroke` | A | A misturado 12% com preto (OKLCH) | barras, linha ↓, seleção de segmentado, chips ativos |
| `ustroke` | B | B misturado 30% com preto (OKLCH) | linha ↑, ênfase ≥85% |
| `dfill` | A @ 30% | A @ 24% | área ↓ / área dos cartões |
| `ufill` | B @ 26% | B @ 30% | área ↑ / área dos tiles |
| `hover` | A @ 14% | A @ 10% | hover de botões/linhas |
| `sel` | A @ 24% | A @ 18% | item selecionado na lista de dispositivos |

Escurecer em OKLCH: converta para OKLCH, multiplique L por (1 − fração) e mantenha C e h. Uma alternativa aceitável é o mix sRGB linear com preto. Os valores pré-calculados das 6 paletas estão em `spec/tokens.json`.

**Ênfase de carga alta**: barras e células com fração ≥ 0,85 da escala usam `ustroke` em vez de `fill`. Não existe vermelho de alerta (ver não-escopo).

## Fundo e transparência
- Fundo da janela = `bg` com alpha = opacidade (40–100%, padrão 90%).
- Atrás dele, **acrílico/blur** do sistema (Win11 `DWMSBT_TRANSIENTWINDOW`; Win10 `ACCENT_ENABLE_ACRYLICBLURBEHIND`). Ver `07-windows.md`.
- Sombra: `0 12px 32px rgba(0,0,0,.22)`. No WPF, use `DropShadowEffect` em uma Border interna com margem para a sombra caber (janela transparente), ou a sombra nativa do DWM no Win11.

## Moldura "blueprint" (obrigatória em popup e Detalhes)
- Borda de 1 px na cor `line`, **raio 0**.
- **4 marcas "+"** de 11×11 px centradas exatamente em cada vértice (deslocadas −6 px para fora), traço de 1 px na cor `text` @ 55%. Implementar como `Adorner` ou 4 `Path` posicionados num `Grid` com `ClipToBounds=False`.

## Ícones
**Lucide**, stroke 1.5, linecap/linejoin round. Tamanhos de 10 a 13 px. Exporte como `Geometry` (path data) em `Icons.xaml`:
`settings`, `x`, `chevron-left`, `chevron-right`, `arrow-down`, `arrow-up`, `external-link`, `search`, `maximize`, `minimize`.

## Interação
- Hover: fundo `hover` e ícone/texto em `text`.
- Foco de teclado: contorno de 2 px em `fill`, offset de 2 px. Nunca o foco tracejado padrão.
- Desabilitado: opacidade 45%.
- Transições: largura de barras e altura de células com 400 ms ease-out; troca de tema/paleta instantânea.
