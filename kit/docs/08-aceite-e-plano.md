# 08 · Plano de tarefas e critérios de aceite

## Fase 1: Esqueleto
- [ ] Solução com os 4 projetos (ver 02) compilando; manifesto PerMonitorV2
- [ ] Instância única
- [ ] Ícone na tray (estático), menu Abrir/Detalhes/Configurações/Sair
- [ ] Popup vazio de 344×232 com moldura blueprint, posicionado conforme 07 (testar a taskbar em baixo e em cima, 100% e 150% de DPI, 2 monitores)
- [ ] Fontes Barlow e Barlow Condensed embutidas

## Fase 2: Coleta
- [ ] `SimulatedProvider` lendo `prototype/data/sensores.json` (arg `--simulate`)
- [ ] `LhmProvider` com varredura recursiva, IDs estáveis e hot-plug
- [ ] `PdhProvider` (CPU, disco) e rede via `NetworkInterface` com Δt real
- [ ] `MemoryProvider` (GlobalMemoryStatusEx)
- [ ] `SensorService` em thread própria, pulando ticks atrasados; Snapshot imutável
- [ ] `RingBuffer<float?>(120)` por sensor
- [ ] Normalização: categoria, nome, consolidação de Memory, papel, qualidade, allZero
- [ ] Testes unitários: Nice, Formatter (todas as linhas da tabela 05 §9), Primary (casos de 06 §4), Namer (Network genérico, duplicados), Consolidator (slots 1 e 3 → módulos 1 e 2)

## Fase 3: Resumo
- [ ] Linha de métricas, disco, rede e gráfico espelhado (06 §1–2) idênticos a `screenshots/01` e `03`
- [ ] Ícone da tray dinâmico com barras ↓/↑ e tooltip, sem vazar HICON (handles GDI estáveis depois de 1 h)

## Fase 4: Configurações e tema
- [ ] Todas as linhas de 04 §B6, idênticas a `screenshots/02` e `04`
- [ ] Persistência atômica em `%APPDATA%\MonitorTray\settings.json`, validando contra o schema; arquivo corrompido → padrões
- [ ] ThemeService: troca de tema/paleta instantânea em todas as janelas abertas; tema "sistema" reage ao Windows
- [ ] Opacidade + acrílico (Win10 e Win11), cantos retos no Win11
- [ ] Autostart (Run ou Tarefa Agendada) e fechar ao perder foco

## Fase 5: Detalhes
- [ ] Janela própria, arrastável, redimensionável, maximizável, com posição/tamanho lembrados
- [ ] Lista de dispositivos com valor principal, sparkline e tag NOVO
- [ ] Cartões, gauges, seções com grade ou tiles exatamente como 04 §C, idênticos a `screenshots/10–26`
- [ ] Busca (sem acento), ZERADOS, LIMITES, rodapé com contagens
- [ ] Banner de admin quando não elevado
- [ ] Virtualização: `VirtualizingStackPanel` na lista; o painel só renderiza o dispositivo selecionado

## Fase 6: Polimento
- [ ] Resume da hibernação, troca de DPI/monitor e hot-plug de USB/NVMe testados
- [ ] Coleta reduzida com as janelas fechadas
- [ ] Publish single-file e ícone .ico
- [ ] README do app (requisitos, admin, aviso do driver)

## Checklist de aceite (todos obrigatórios)
### Visual
- [ ] Cantos retos e marcas "+" nos 4 cantos do popup e do Detalhes
- [ ] Todos os números em Barlow Condensed tabular, sem "pular" entre ticks
- [ ] Popup com 344 px de largura e gráfico de rede com 64 px de altura
- [ ] As 6 paletas trocam ícone da tray, quadrado do título, barras, gráficos, chips, segmentados e seleção
- [ ] Claro e escuro corretos (tabela 03)
### Dados
- [ ] Nenhum nome de modelo hard-coded (grep por "14900", "3090", "Kingston", "Corsair" e "NCT" no `src/` retorna vazio)
- [ ] No hardware de referência: CPU com grade por núcleo (Load, Clock, Temperature, Voltage com ≥ 9 itens)
- [ ] Memória aparece como 1 dispositivo com "2 módulos · módulo 1 no slot 1, módulo 2 no slot 3"
- [ ] O valor do cabeçalho da CPU é o CPU Package (nunca um Distance to TjMax)
- [ ] O cartão Carga da CPU usa o CPU Total
- [ ] Warning/Critical Temperature dos SSDs aparecem só com LIMITES ligado e com a tag `limite`
- [ ] Water In/Out/T Sensor com "—" e a tag `sem leitura`
- [ ] Lacunas aparecem como quebras na linha
- [ ] Nenhum texto de "saudável/OK/falha" em lugar nenhum
- [ ] Plugar um pendrive/SSD USB com SMART faz o dispositivo aparecer com NOVO em ≤ 30 s; ao remover, ele some
- [ ] `--simulate` com o JSON do kit + um sensor de tipo inventado ("Current" em uma "Psu") renderiza sem erro, com a categoria Fonte e o tipo Corrente
### Desempenho
- [ ] CPU do app < 1% (popup fechado), < 3% (Detalhes aberto, 1 s), em um i9-14900K
- [ ] RAM < 120 MB; sem crescimento depois de 1 h
- [ ] Abrir o popup em < 100 ms após o clique
