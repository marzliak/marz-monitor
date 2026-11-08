# 04 · Telas (especificação detalhada)

Todas as medidas estão em DIP (px a 100%). A referência visual é `screenshots/` + `prototype/`. Fontes: **BC** = Barlow Condensed, **B** = Barlow.

---
## A. Ícone da tray (16×16, redesenhado a cada tick)
- Quadrado de contorno 1 px `#98989B` (0.5,0.5 → 15.5,15.5)
- Linha central horizontal em y=8, cor `#7A7A7D`
- Barra esquerda (x=3, largura 4) **subindo** do centro, com altura = round(min(1, down/escalaDown) × 6), cor = paleta A
- Barra direita (x=9, largura 4) **descendo** do centro, com altura = round(min(1, up/escalaUp) × 6), cor = paleta B
- Gerar como `Bitmap` 32bpp → `Icon.FromHandle(bmp.GetHicon())`; chamar `DestroyIcon` no handle anterior (para não vazar GDI). Para DPI 150/200%, renderizar a 24/32 px.
- Tooltip: `↓ 11.3 MB/s  ↑ 166 KB/s` (máx. 127 caracteres)
- Clique esquerdo: abre/fecha o popup. Clique direito: menu com **Abrir · Detalhes · Configurações · — · Sair**.

---
## B. Popup (344 px de largura)
Posição: canto inferior direito da área de trabalho do monitor que contém a tray; 12 px da borda direita e 12 px acima da taskbar (taskbar em outras bordas: ver 07). Moldura blueprint. Ver `screenshots/01-popup-resumo-escuro.png`.

### B1. Cabeçalho (padding 4/6/4/10, borda inferior `line`)
- Esquerda: quadrado de 6×6 dividido na diagonal (A em cima à esquerda, B embaixo à direita) + gap 6 + "MONITOR" (BC 600, 12 px, 0.08em, caixa alta).
- Direita (BC 11 px `muted`, gap 2):
  - rótulo do intervalo ("1s" / "500ms" / "2s"), margem direita 4
  - botão **DETALHES** (altura 22, padding 0 6, ícone external-link 12 + texto 11 px caixa alta); fundo `hover` quando a janela Detalhes está aberta
  - botão engrenagem 22×22 (fundo `hover` quando Configurações está ativo)
  - botão fechar 22×22 (esconde o popup)

### B2. Linha de métricas: grid de 3 colunas iguais, divisórias verticais e borda inferior
Cada célula tem padding 5/9/6 e gap vertical 2:
1. Linha de rótulo: à esquerda "CPU" / "GPU" / "MEM" (BC 11, 0.1em, `muted`); à direita "44°C" (CPU/GPU) ou "35%" (MEM), BC 11 `muted`, tabular.
2. Linha de valor:
   - CPU/GPU: **"16"** (BC 600, 22 px, line-height 1) + "%" (BC 12 `muted`) à esquerda; **"30"** (BC 500, 13) + " W" (11 `muted`) à direita.
   - MEM: **"11.3"** (22/600) + "/32 GB" (12 `muted`). O total vem do sistema (arredondado para GB inteiro).
3. Barra de 2 px de altura: trilho `track`, preenchimento `fill` com largura = %.

### B3. Linha do disco: grid `auto | 1fr | 1fr`, divisórias, borda inferior; padding das células 4/9
"DISCO" (BC 11, 0.1em, `muted`) | "L" (11 `muted`) + **"54.0"** (BC 600 16) + "MB/s" (11 `muted`) | "E" + valor + "MB/s". Sempre MB/s com 1 casa decimal.

### B4. Linha da rede: mesmo grid, sem borda inferior
"REDE" | ícone arrow-down 10 px em `dstroke` + valor + unidade | ícone arrow-up 10 px em `ustroke` + valor + unidade. Unidade automática: < 1024 KB/s → "KB/s" inteiro; senão "MB/s" com 1 casa.

### B5. Gráfico de rede espelhado (margem 0/9/8, altura 64, bordas superior e inferior `line`)
- Linha central em y=32 (`line`).
- **Download para cima**: área preenchida `dfill` + contorno 1.25 px `dstroke`.
- **Upload para baixo**: área `ufill` + contorno 1.25 px `ustroke`.
- Cada metade tem amplitude de 30 px. N pontos = histórico (30/60/120); o mais novo à direita, distribuídos uniformemente na largura.
- Escala automática por "nice number" (ver 06); modo Separada ou Única.
- Rótulos (BC 10 `muted`, tabular): canto superior direito "↓ 19.5 MB/s" e canto inferior direito "↑ 1000 KB/s".

### B6. Configurações (substitui B2–B5; ver `screenshots/02-popup-configuracoes-escuro.png`)
O cabeçalho mostra "<" (chevron-left 13, botão 20×20) + "CONFIGURAÇÕES". Cada linha: padding 6/10 (paleta 7/10), borda inferior `line`, rótulo à esquerda em BC 12, 0.08em, caixa alta, `muted`; controle à direita.

| Linha | Controle | Opções | Padrão |
|---|---|---|---|
| COR · {nome} | 6 quadrados de 20×20 divididos na diagonal (A/B), gap 7; o selecionado tem contorno de 1 px `text` com offset de 2 | ver 03 | Elétrico |
| TEMA | segmentado | Claro / Escuro / Sistema | Escuro |
| ATUALIZAÇÃO | segmentado | 0,5s / 1s / 2s | 1s |
| HISTÓRICO | segmentado | 30s / 60s / 120s | 60s |
| ESCALA REDE | segmentado | Separada / Única | Separada |
| OPACIDADE | slider de 40–100, passo 5, largura 130 + "90%" (largura 32, alinhado à direita) | | 90 |
| INICIAR COM O WINDOWS | checkbox de 15 px | | on |
| FECHAR AO PERDER FOCO | checkbox de 15 px | | on |

**Segmentado**: borda externa de 1 px `line`, raio 0, opções com padding 3/9, BC 12, divisórias de 1 px entre elas; a opção selecionada tem fundo `fill` e texto `bg`. Toda mudança é aplicada na hora e salva.

---
## C. Janela Detalhes (ver `screenshots/10` a `26`)
Janela própria (não um filho do popup), sem chrome nativo, com moldura blueprint, acrílico e mesma opacidade.
- Tamanho padrão **960×640**; mínimo 720×440; redimensionável pelas bordas (`WindowChrome.ResizeBorderThickness=6`).
- Posição padrão: centralizada no monitor da tray. A posição e o tamanho são lembrados entre sessões (validar se continuam visíveis em algum monitor).
- Arrastar pela barra de título (exceto sobre controles). Duplo clique ou botão → maximizar ocupando a área de trabalho com margem de 12.
- **Não** some ao perder foco. Não é topmost por padrão.
- Esc fecha. Ctrl+F foca a busca.

### C1. Barra de título (padding 6/6/6/12, borda inferior, gap 8)
Quadrado A/B de 7 px · "DETALHES DO SISTEMA" (BC 600, 13, 0.08em) · subtítulo flexível "12 dispositivos · 355 entradas" (BC 11 `muted`, ellipsis) · campo de busca (largura 220, altura 24, borda `line`, ícone search 12, placeholder "Filtrar sensores", B 12) · chip **ZERADOS** · chip **LIMITES** (altura 24, padding 0 8, borda `line`, BC 11 caixa alta; ativo = fundo `fill` e texto `bg`) · botão maximizar/restaurar 24 · botão fechar 24.

### C2. Corpo: grid `232px | 1fr`
**Lista de dispositivos (esquerda, rolagem própria, borda direita `line`)**. Cada item é um botão de largura total, padding 8/10/6/12, borda inferior, gap 3:
1. Linha 1: categoria (BC 10, 0.1em, caixa alta, `muted`, flex 1) · tag NOVO (BC 10, fundo B, texto #111) · valor principal (BC 600 13): temperatura principal "63 °C", ou a contagem de sensores quando não há temperatura.
2. Linha 2: nome do dispositivo (B 500 12, ellipsis).
3. Linha 3: sparkline de 18 px de altura dos últimos 40 ticks da temperatura principal (área `dfill` + linha 1.25 `dstroke`); omitida se não houver.
- Selecionado: fundo `sel`. Hover: `hover`. O primeiro da lista começa selecionado.

**Painel do dispositivo (direita, rolagem própria, padding 14/16/18)**
1. **Cabeçalho**: categoria (BC 11, 0.12em, `muted`) · nome (BC 600 **26**) · meta (BC 11 `muted`): "N sensores · {id}" ou, em dispositivo consolidado, "N sensores · 2 módulos · módulo 1 no slot 1, módulo 2 no slot 3".
2. **Cartões-resumo**: grid `repeat(auto-fill, minmax(200px, 1fr))`, gap 8, margem superior 12. **Um cartão por tipo de sensor que tenha leitura graficável** (na ordem de tipos da seção 05), usando o sensor principal do tipo (ver 06):
   - Borda de 1 px `line`, padding 8/10/6.
   - Topo: rótulo do tipo (BC 11, 0.1em, caixa alta, `muted`) · à direita o nome do sensor usado (BC 11 `muted`, ellipsis).
   - Valor: **BC 600 30** + unidade BC 13 `muted`. À direita, **gauge circular** de 38×38 (r=15, traço 5, trilho `track`, arco na cor do cartão começando às 12h) só para % e °C (escala 0–100).
   - **Gráfico de área** de 58 px de altura com os últimos 120 ticks; borda inferior `line`. Cartões de índice par usam `dstroke`/`dfill` e os de índice ímpar `ustroke`/`ufill` (alternância A/B).
   - Rodapé BC 10 `muted`: "mín X" à esquerda e "máx Y" à direita. Se mín == máx após formatação: só "estável X".
   - Não cria cartão se o sensor principal for constante e não for % ou °C.
3. **Seções por tipo** (uma para cada tipo que tenha sensores visíveis): título (BC 600 13, 0.1em, caixa alta) + meta à direita (BC 11 `muted`), margem superior 18, borda inferior `line`. O conteúdo é **um** destes:
   - **Grade de células**, se a seção tiver **≥ 9 sensores, todos graficáveis e da mesma unidade** (ex.: carga, clock e temperatura de cada núcleo P/E): grid `repeat(auto-fill, minmax(36px, 1fr))`, gap 3, células de 48 px de altura com fundo `track`, preenchimento de baixo para cima proporcional (cor `fill`, ou `ustroke` se ≥ 0,85) e o valor curto no topo (BC 600 10: inteiro; GHz com 1 casa para MHz; 2 casas para V). Tooltip "P-Core #1 · 54.0 °C". Meta: "24 · mín X · méd Y · máx Z".
   - **Tiles**, nos demais casos: grid `repeat(auto-fill, minmax(210px, 1fr))`, gap 6. Cada tile tem borda `line` e padding 6/8:
     - linha 1: nome (B 12, ellipsis, tooltip "nome · id") + tag opcional (BC 9, caixa alta, borda `line`, `muted`)
     - linha 2: valor (BC 600 17, largura mínima 74) + unidade (11 `muted`), seguido de **um** visual:
       - **barra** de 6 px (trilho `track`) para % / °C / RPM
       - **sparkline** de 24 px (área `ufill` + linha `ustroke`, últimos 60 ticks) para W, MHz, V, A, B/s, L/h que variam
       - **texto** da faixa "59.9–60.2 GB" (BC 11 `muted`, alinhado à direita) nos demais casos (acumulados, estimativas, limites, SPD, constantes); vazio se mín == máx após formatação
     - Meta da seção: a contagem.
4. Vazio por filtro: "Sem leituras visíveis com os filtros atuais."

### C3. Rodapé (padding 4/12, borda superior, BC 10 `muted`)
Esquerda: "259 sensores visíveis · 43 zerados ocultos · 53 limites/SPD ocultos" (omitir os termos zerados). Direita: "atualiza a cada 1s".

### C4. Comportamento da busca/filtros
- A busca (case-insensitive, sem acento) casa com nome do sensor, nome do dispositivo, tipo cru e tipo em pt-BR. A lista de dispositivos mostra só os que têm resultado; o painel, só os sensores que casam.
- **ZERADOS** desligado (padrão) oculta sensores cujo valor foi 0 em **todas** as amostras da sessão (`allZero`).
- **LIMITES** desligado (padrão) oculta os papéis Limit e Metadata.
- Os filtros não persistem entre sessões; a seleção do dispositivo persiste enquanto o app estiver aberto.
- Se o dispositivo selecionado sumir (hot-unplug), selecionar o primeiro.

### C5. Banner de privilégio (só quando não é admin)
Faixa no topo do painel direito: borda `line`, padding 6/10, B 12: "Algumas leituras (temperatura e potência da CPU, placa-mãe, SPD da RAM) exigem executar como administrador." + botão "Reiniciar como admin".
