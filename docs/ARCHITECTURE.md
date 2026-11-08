# Arquitetura

```
src/
  MonitorTray.Core     net10.0, sem WPF: modelo, formatação, normalização (categoria, nome, consolidação de memória,
                       papéis/tags, sensor principal), histórico (RingBuffer, SensorTracker), view-model do Detalhes, Settings.
  MonitorTray.Sensors  LibreHardwareMonitor (LhmSource), PDH/rede/memória (SystemSummarySource), unidades (Volumes, WMI),
                       modo simulado, SensorService (thread própria, publica Snapshot imutável).
  MonitorTray.Gpu      Controle de fans NVIDIA portado do GPU by MarzOps (NVML/NVAPI + GpuSafety) e GpuFanService.
  MonitorTray.Keyboard (namespace MonitorTray.Oled) GameSense, escritor único do OLED, Raw Input da rodinha, compensação de mudo, KeyboardService (ciclo das telas).
                       Lógica pura (WheelState, MuteCorrelation, DeviceFilter, KeyboardConfig, OledComposer) fica em Core/Keyboard.
  MonitorTray.App      WPF montado em código: tema/paletas, controles desenhados, popup, Detalhes, Sobre, bandeja, plataforma.
tests/MonitorTray.Core.Tests   xUnit: formatação, escalas, regras de sensores do kit, settings, fans (fakes).
installer/             Installer.ps1 (não elevado), Elevated.ps1 (UAC), Uninstall.ps1.
tools/build-package.py Pacote versionado.
kit/                   Especificação original (docs, protótipo, screenshots, coleta de hardware).
```

## Fluxo de dados
- `SensorService` roda a cada `refreshMs`: Resumo barato sempre (PDH, memória, rede); LHM só com popup ou Detalhes aberto, numa thread do pool (tick atrasado é pulado, não enfileirado). Varredura completa a cada 30 s só com o Detalhes aberto; a primeira leitura depois do `Reset` é descartada.
- A UI recebe `Snapshot` via `Dispatcher.BeginInvoke` e atualiza os elementos existentes. O painel do dispositivo só é recriado quando muda a **estrutura** (seções/sensores/cartões), nunca por valor.
- `GpuFanService` lê a NVML a cada 1 s em outro timer, mantém o preset manual (reafirma, promove a 100% se a segurança exigir) e escreve `fan-status.json`.

## Decisões (desvios do kit, combinados com o usuário)
- .NET 10 em vez de 8 (fim de suporte). 
- Sem marcas "+" nos cantos. Popup não fecha ao perder o foco por padrão (pin). Números sem animação; barras e gráficos animados.
- Controle de fans incluído (fora do escopo v1 do kit).
- "STORAGE" no lugar de "DISCO"; L/E numa célula; unidades lógicas compactas.
- Sensor principal: `composite > core$ > hot spot` (o protótipo escolhia Hot Spot na GPU, contra o doc 06 §4).
- Interfaces de filtro de rede (WFP/QoS/WAN Miniport) são ignoradas; 0 W de package sem admin vira "—".
- Popup v2 (mock 01i): sem contorno claro; faixa de cards de temperatura (papéis de `TempCards`, escolhidos e ordenados nas Configurações, sensor ausente = card omitido); títulos GPU/PLACA-MÃE/WC sem temperaturas; FAN SPEED da GPU com o botão dos modos; cards de fan (placa e cooler) escolhidos nas Configurações; ↓↑ da rede lado a lado no canto inferior esquerdo; Configurações roláveis (máx. 760 px). Desligar fan da placa pelo card fica atrás de flag (ver fanxpert4-notes.md).
- Popup v2, rodada 2: ↓↑ da rede numa linha própria embaixo do gráfico (nada cobre a curva); cards de fan da placa e do cooler no mesmo grid de 5 colunas, % no cabeçalho e rpm embaixo; rpm só de conector conferido (`BoardFanMap`: chassis), CPU/bombas mostram o % e "—" (rpm nunca derivado do %). Alerta dos cards pelos limites das proteções (`TempLimits`: CPU 85, GPU 80, VRAM 95, líquido 50; demais documentados no código), perto = limite − 5 °C. Faixa fina no topo (`StatusStrip`) com proteções que agiram e modo game. Clique no card de temperatura abre o Detalhes no sensor (tooltip com mín./máx. da sessão, `TempSession`). Ícone da bandeja opcional com a maior temperatura e/ou cor de alerta (mantém o LHM ativo com o popup fechado). Modo game automático por executável (`AutoGame`, processos a cada 3 s; desliga só o que ligou).

## Elevação
LHM precisa de admin para CPU/placa-mãe/SPD. A cópia instalada, aberta sem elevação (atalho), relança pela tarefa `MonitorTray-<SID>` e sai, então sempre roda elevada sem UAC.
