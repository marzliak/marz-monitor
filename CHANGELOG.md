# Changelog

Formato: [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/). Versionamento: série **0.1.x** (cada pacote entregue sobe o último número).

## [0.1.32] - 2026-11-07
### Corrigido
- Fan embutido do RYUJIN preso em 100%: o modo game o punha no preset FULL e, quando o game desligava sozinho (GPU/placa mudadas por fora), o cooler não voltava ao que era antes. Agora qualquer saída do modo game devolve cada parte do cooler que ainda está como o game deixou.
- Ao abrir, um fan embutido em FULL fora do modo game volta ao STD (uma curva própria do usuário é mantida).
### Alterado
- CURVAS / SILENT / STD / TURBO / FULL do WC RYUJIN II valem também para o fan embutido (antes só bomba e radiador); CURVAS devolve a config própria do embutido, se houver.

## [0.1.31] - 2026-11-07 (teste, branch popup-v2)
### Adicionado
- **Quick card** (botão direito num card de fan do popup): CURVA / MANUAL / OFF, slider de % (grava ao soltar) e atalhos 30/50/75/100, link "Editar curva…" que abre o Controle térmico no fan.
- **MANUAL por fan da placa**: curva plana no % pedido até 80 °C e 100% a partir de 85 °C, gravada no chip pelo mesmo caminho do OFF/FULL (curva User guardada; volta ao sair do Marz). Mínimo 20%. Card com contorno destacado. FULL e modo game tiram do MANUAL.
- **MANUAL no RYUJIN** pelo quick card: bomba (mín. 30%), radiador (3 fans juntos) e fan embutido em % fixo.

## [0.1.30] - 2026-11-07 (teste, branch popup-v2)
### Adicionado
- Ícone da bandeja: escolha de quais temperaturas mostrar (TEMPERATURA NO ÍCONE; nenhuma = a mais quente). Com mais de uma, alternam a cada 2/3/5/10 s (TROCAR A CADA). O tooltip do ícone diz qual está aparecendo. A cor de alerta continua considerando todos os cards.

## [0.1.29] - 2026-11-07 (teste, branch popup-v2)
### Alterado
- Ícone da bandeja: novo tamanho PP (58% da altura), padrão.
- Controle térmico: "tela n/m no LCD" só aparece na aba do LCD.
### Adicionado
- Controle térmico (RYUJIN): botão RESTAURAR volta a curva do atuador à padrão (pontos do STD); só grava ao Aplicar.

## [0.1.28] - 2026-11-07 (teste, branch popup-v2)
### Corrigido
- Contorno claro em volta do popup: a janela tinha 6 px transparentes fora da moldura, onde o acrílico aparecia como borda. Margem zerada.
### Alterado
- Curva do RYUJIN segue a **média de ~5 s** da temperatura da fonte (antes: leitura instantânea a cada 1 s; a CPU Package oscila 10–20 °C em picos). A linha AGORA e o "% agora" do Controle térmico usam a mesma média.
- Controle térmico: proteções recolhidas (título com ícone de informação; clique abre a lista, tooltip mostra as regras); sem "dono:"/"driver".
- Auto Tuning do radiador: faixa sobre o gráfico com etapa (1/11), barra de progresso, tempo restante e CANCELAR; popup mostra "auto tuning n/11".

## [0.1.27] - 2026-11-07 (teste, branch popup-v2)
### Adicionado
- Nova janela **Controle térmico**: faixa com todos os fans, abas GPU/PLACA-MÃE/RYUJIN II, gráfico da curva com fonte explícita, linha AGORA, curva anterior, edição de pontos no RYUJIN (até 8) e na placa em CURVAS (4 pontos do chip), Aplicar/Descartar; LCD do cooler em aba própria.
### Corrigido
- Contorno claro em volta do popup no app real: a moldura sem contorno ainda desenhava uma borda de 1 px da cor do fundo, e em DPI fracionário a emenda saía com alpha menor (linha clara sobre o blur). Agora só o fundo; a região da janela segue o retângulo real da moldura e a borda do DWM (Windows 11) fica sem cor.
- CURVAS do WC no popup abria o controle térmico: agora aplica a config própria do cooler de antes do preset (guardada em `ryujin-curves.json`, sobrevive ao reinício) ou, sem uma, as curvas padrão (pontos do STD como curva própria).
- Card de fan da placa com "DESLIGAR FAN DA PLACA" desligado: só leitura (sem mãozinha nem dica). Ligado, um clique bloqueado pela proteção mostra "não desligou" com o motivo; desligar a opção religa os fans desligados.
### Alterado
- Sem as tags driver / AC / Marz nos títulos.
- Placa-mãe: todos os modos visíveis por padrão (escolha segue nas Configurações).
- GPU: rpm ao lado do % de cada fan quando > 0.
- Ícone da bandeja com temperatura: número menor por padrão e opção TAMANHO DA FONTE (P/M/G).

## [0.1.26] - 2026-11-07 (teste, branch popup-v2)
### Alterado
- **Popup redesenhado** no mesmo tamanho: sem contorno claro; faixa de cards de temperatura (escolha e ordem nas Configurações; cor de alerta pelos limites das proteções); GPU com FAN SPEED compacto e fan/A/% à direita; placa e WC com CURVAS/SILENT/STD/TURBO/FULL e cards de fan de tamanho único (escolha nas Configurações); rpm só quando medido; rede com ↓/↑ numa linha própria.
### Adicionado
- Faixa de avisos no topo (modo game, fan religado por proteção, proteções da GPU/cooler).
- Ícone da bandeja com a maior temperatura e/ou cor de alerta (opcional).
- Modo game automático por lista de executáveis.
- Clique num card de temperatura abre o Detalhes no sensor; tooltip com mín./máx. da sessão.
- Tag de quem controla cada seção (driver / AC / Marz).
- Desligar fan da placa pelo card (atrás da opção "DESLIGAR FAN DA PLACA", desligada por padrão; curva de proteção 0% < 80 °C / 100% ≥ 85 °C; religa por temperatura, FULL e modo game; não persiste).

## [0.1.25] - 2026-11-02
### Adicionado
- **Water cooler ROG RYUJIN II** (USB HID, sem o Armoury Crate): leitura de água, bomba, fan embutido e 3 fans do radiador; controle de bomba/radiador/fan embutido com presets SILENT/STD/TURBO/FULL, curva ou % fixo, fonte da curva configurável e Auto Tuning do radiador. Proteções: bomba ≥ 30 %, água ≥ 50 °C põe tudo em 100 %, sem leitura da fonte usa 70 %, ao fechar deixa ≥ 60 %, para e avisa se outro programa escrever no cooler.
- **Tela do LCD do cooler**: telas de 1 a 4 informações com qualquer sensor, rótulo e unidade, alternando por tempo (desligado por padrão).
- Janela **Controle térmico** (botão termômetro no cabeçalho e item na bandeja): cards de temperaturas e rotações (cooler, placa, GPU), modos da placa, curvas % × rpm.
- Popup: linha **WC RYUJIN II** (presets + água + rpm de bomba/radiador/fan embutido).
- **Modo game configurável** nas Configurações (GPU, placa com todas as fans, bomba, radiador, fan embutido; % da GPU e do cooler); padrão tudo a 100 %. Persiste entre reinícios. `MonitorTray.exe --game` alterna.
### Alterado
- Popup: GPU, MOTHERBOARD e WC no mesmo padrão; modos da placa e do WC na linha do título; rede sem a linha de números (valor atual em destaque no gráfico).

## [0.1.24] - 2026-11-02
### Corrigido
- Ícone da bandeja não ficava visível mesmo marcado: o Guid do ícone era o mesmo do app instalado e dos builds de teste, e o Windows perdia a escolha. Agora o Guid é por caminho do exe e, na primeira vez, o ícone já entra visível na barra.

## [0.1.23] - 2026-11-02
### Alterado
- STORAGE: leitura e escrita por unidade identificadas como `R` e `W` (continuam em branco quando paradas).

## [0.1.22] - 2026-11-02
### Alterado
- Instalador, README e INSTALL atualizados: fans da placa via Armoury Crate, modo game, onde fica o backup das curvas.

## [0.1.21] - 2026-11-02
### Adicionado
- Botão **modo game** (controle) no cabeçalho, ao lado do 1s: todas as ventoinhas a 100% (GPU + placa via Armoury Crate FULL); clique de novo volta ao automático/curvas.
- MOTHERBOARD: temperaturas da placa (mobo, chipset, VRM) na linha dos modos; chipset ≥ 85 °C em destaque.

## [0.1.20] - 2026-11-02
### Alterado
- STORAGE: L/E na mesma linha do % (em branco quando parado) e micro gráfico de leitura/escrita em cada unidade.
- Rede: rótulos do gráfico mostram "atual | escala" em cima (download) e embaixo (upload).

## [0.1.19] - 2026-11-02
### Alterado
- STORAGE: rótulo pequeno na mesma linha das unidades; cada unidade mostra %, leitura e escrita (L/E por unidade, contador LogicalDisk). Linha do L/E total removida (total no tooltip do STORAGE).

## [0.1.18] - 2026-11-02
### Alterado
- MOTHERBOARD: RPM em células com o nome de cada ventoinha (Exaustor, Front top, Front down, Rear), legíveis; nomes em `settings.json` (`acFanNames`).

## [0.1.17] - 2026-11-02
### Alterado
- Linha PLACA renomeada para MOTHERBOARD; RPM das ventoinhas dos modos ao lado dos botões (compacto; nomes no tooltip).

## [0.1.16] - 2026-11-02
### Corrigido
- Linha PLACA: o botão clicado acende na hora (com "aplicando…" enquanto o Armoury Crate trabalha); se falhar, volta ao modo anterior. FULL aplica todas as ventoinhas juntas (~1 s em vez de ~6 s).

## [0.1.15] - 2026-11-02
### Adicionado
- **Modos das ventoinhas da placa pelo Armoury Crate** (COM do AsusFanControlService; o Marz não escreve no chip): linha **PLACA** no popup com CURVAS (seu perfil User), SILENT, STD, TURBO e FULL (100%).
- Configurações › **MODOS DA PLACA** (quais botões aparecem) e **VENTOINHAS NOS MODOS** (quais saídas entram; padrão Chassis 1–4).
- Segurança: antes do FULL as curvas User são guardadas em `state\ac-user-curves.json` e devolvidas (com conferência) ao voltar para CURVAS, ao sair e ao abrir o app se ficou pendente. O perfil de inicialização do Armoury Crate não é alterado.
- `--ac-test <log> "<ventoinha>"`: teste de bancada (admin).

## [0.1.14] - 2026-11-01
### Alterado
- Memória: GC de estação sem coleta concorrente, `ConserveMemory=7`, `RetainVM=false`, sem TieredPGO.
- Só na bandeja (popup, Detalhes e editor do teclado fechados): após 10 s compacta o heap e devolve as páginas ao Windows; repete a cada 5 min.

## [0.1.13] - 2026-11-01
### Alterado
- Configurações › **HISTÓRICO** do gráfico de rede: 1m, 5m, 10m, 30m, 1h, 2h (máx. 2 h). Guarda 1 valor por segundo (pico dos ticks); janelas longas mostram até 120 pontos com o **pico** de cada intervalo (2 h = 1 ponto por minuto), então picos curtos não somem. Valores antigos (30/120 s) voltam para 1m. Tooltip do gráfico mostra a janela.

## [0.1.12] - 2026-11-01
### Adicionado
- Configurações › **UNIDADE REDE**: MB/s (bytes, base 1024) ou **Mbps** (bits, base 1000, como speedtest/operadora). Vale para popup, escala do gráfico (teto redondo em bits), tooltip da bandeja, Detalhes e os valores calculados de rede do teclado. Salvo em `settings.json` (`netUnit`).
### Corrigido
- Teste de mutex das fans derrubava o executor de testes quando o app instalado estava aberto (contagem de testes variava).

## [0.1.11] - 2026-11-01
Primeira versão que substitui por completo o GPU by MarzOps.
### Adicionado
- Instalador: opção **Substituir o GPU by MarzOps** (aparece só quando ele está instalado/aberto): fecha pelo sinal de saída dele e desativa a tarefa de logon (reversível). Silencioso: padrão ligado, `-KeepGpuApp` para manter.
- Instalador/LEIA-ME: descrição com teclado e rede; tabela com `keyboard.json` e `keyboard-status.json`.

## [0.1.10] - 2026-11-01
### Alterado
- Detalhes: **Rede numa aba só**. Dentro dela, cartões e seções por tipo de tráfego (Total, Rede local, Internet) e depois uma seção por placa. Cada cartão escolhe Download/Upload (▾, salvo).

## [0.1.9] - 2026-11-01
### Corrigido
- Saída do app não registra mais `ObjectDisposedException` (snapshots enfileirados tentavam atualizar o ícone já descartado).

## [0.1.8] - 2026-11-01
### Adicionado
- Detalhes › Rede: dispositivo **Tráfego de rede** com download/upload **total**, **rede local** e **internet** (classificação por endereço: privados, link-local, CGNAT/Tailscale, multicast e IPv6 ULA contam como local). Fonte: ETW do kernel (TCP/UDP, somente leitura, requer admin). No cartão Taxa, o seletor ▾ escolhe qual mostrar; a escolha fica salva.

## [0.1.7] - 2026-11-01
### Adicionado
- **Teclado (OLED do SteelSeries Apex)** portado do GPU by MarzOps: até 20 telas de 2 linhas × 20 colunas enviadas pelo SteelSeries GG (GameSense), rotação por tempo, rodinha (1 clique = próxima tela, 2 cliques = fixar/soltar; Raw Input só no Consumer Control do Apex), compensação de mudo do clique da rodinha.
- Janela **Telas do teclado**: criar, duplicar, remover e reordenar telas; tempo na tela; ícone GG; por linha: sensor (calculados genéricos ou qualquer sensor do LHM), prefixo, sufixo, casas decimais; prévia ao vivo do que está no teclado. Salva em `%APPDATA%\MonitorTray\keyboard.json`.
- Linha TECLADO no popup (tela atual, fixar, próxima, editar) e item no menu da bandeja.
- Na primeira execução, as telas são importadas do perfil do GPU by MarzOps (`profile-rodinha.json`).
- Ao sair, o OLED é liberado (o GG volta à tela dele) e a rodinha é solta.

## [0.1.6] - 2026-11-01
### Alterado
- Novo nome: **Marz Monitor** (popup, Sobre, tooltip, atalhos, Aplicativos instalados, pacote `MarzMonitor-<versão>-win-x64.zip`). Identificadores internos (MonitorTray.exe, pasta, tarefas, sinais, %APPDATA%\MonitorTray) mantidos para atualizar por cima sem perder nada; atalhos antigos são removidos.

## [0.1.5] - 2026-11-01
### Adicionado
- Tooltip da bandeja com CPU (% e °C), memória, GPU (% e °C, preset de fan quando manual), disco (L/E MB/s) e rede. Funciona com o popup fechado (GPU via NVML).

## [0.1.4] - 2026-11-01
### Alterado
- Ícone da bandeja: linha de pulso fixa (branca na barra escura, escura na clara, troca com o tema do Windows). A velocidade da rede continua no tooltip.

## [0.1.3] - 2026-11-01
### Corrigido
- Instalador: a ACL era aplicada arquivo a arquivo e deixava o exe ilegível (falha "null-valued expression" na verificação de versão). Agora só a pasta recebe a ACL e os arquivos herdam. Erros da etapa elevada informam a linha.

## [0.1.2] - 2026-11-01
### Adicionado
- Instalação silenciosa: `Installer.ps1 -Silent [-NoDesktop] [-NoStartup] [-NoOpen]` (sem janelas; mesmo fluxo seguro e UAC).

## [0.1.1] - 2026-11-01
### Adicionado
- Cartões do Detalhes com seletor (▾): Média, Máximo ou qualquer sensor do tipo — ex.: Clock da CPU por núcleo P/E ou média de todos. A lista vem do hardware; a escolha fica salva em `settings.json` (`cardPicks`).

## [0.1.0] - 2026-11-01
### Adicionado
- Popup na bandeja: CPU/GPU/MEM, STORAGE (L/E + uso por unidade com hardware no tooltip), rede com gráfico espelhado, linha da GPU (nome curto, temperaturas core/hot/vram, ícone da fan com modo) e FAN SPEED (A/30/50/75/100).
- Janela Detalhes com descoberta dinâmica de sensores (LibreHardwareMonitor), cartões, grade por núcleo, tiles, busca e filtros.
- Controle de fans portado do GPU by MarzOps com todas as proteções; presets também no Detalhes.
- Gráficos com rolagem contínua e curva monotônica; números trocam direto; nada cai a zero entre leituras.
- Configurações: tema, 6 paletas, atualização (clique no rótulo do cabeçalho), histórico, opacidade, iniciar com o Windows, iniciar oculto, fechar esconde na bandeja, pin.
- Popup e Detalhes arrastáveis.
- Janela Sobre (versão, build, sistema, créditos, copiar informações).
- Instalador gráfico: verifica SHA256, fecha a instância aberta pelo sinal de saída (fans ao automático), UAC, backup/rollback, tarefas elevadas da conta, atalhos, entrada em Aplicativos instalados e desinstalador.
