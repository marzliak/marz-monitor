# 05 · Sensores: fontes, modelo e regras

A coleta real do PC de referência está em `coleta-hardware/` (LHM 0.9.6 + WMI/PDH, 18/10/2026, 125.9 s, 355 entradas LHM). O mesmo conteúdo, normalizado, está em `prototype/data/sensores.json`:
```json
{ "family":"CpuGpu", "hardware":"Intel Core i9-14900K", "hwId":"/intelcpu/0",
  "id":"/intelcpu/0/temperature/0#CPU Package", "name":"CPU Package", "type":"Temperature",
  "unit":"degC", "state":"valores presentes"|"parcial"|"indisponível",
  "min":45, "mean":53.1, "max":61, "allZero":false,
  "role":"leitura"|"limite reportado, não valor atual"|"metadado/limite SPD; ..."|"contador acumulado; não taxa"|"estimativa reportada; ..." }
```

## 1. Descoberta dinâmica (requisito central)
- Na inicialização: `new Computer { IsCpuEnabled, IsGpuEnabled, IsMemoryEnabled, IsMotherboardEnabled, IsControllerEnabled, IsNetworkEnabled, IsStorageEnabled, IsPsuEnabled, IsBatteryEnabled = true }.Open()`.
- Percorrer **recursivamente** `Hardware` e `SubHardware`; cada `ISensor` vira um `SensorReading`. O ID é `sensor.Identifier.ToString()`.
- Assinar `Computer.HardwareAdded` e `HardwareRemoved`, e também refazer a varredura completa a cada 30 s (cobre dispositivos USB que o LHM não notifica). Sensores podem aparecer dentro de um hardware existente.
- **Nunca** filtrar por nome de modelo. Qualquer `SensorType` novo da biblioteca entra como `Unknown` com o `KindRaw` preservado e aparece no fim, rotulado pelo nome cru.
- **Tag NOVO**: o dispositivo cujo ID não está em `known-devices.json` recebe a tag nesta sessão. Ao fechar o Detalhes, o ID é gravado no arquivo. Na primeira execução, todos são gravados sem tag.

## 2. Categoria (família → rótulo)
| Família LHM (`HardwareType`) | Categoria |
|---|---|
| Cpu | CPU |
| GpuNvidia, GpuAmd, GpuIntel | GPU |
| Memory | Memória |
| Motherboard, SuperIO, EmbeddedController | Placa-mãe |
| Storage | Armazenamento |
| Network | Rede |
| Psu | Fonte |
| Cooler, EmbeddedController de terceiros | Controladora |
| Battery | Bateria |
| PowerMonitor | Energia |
| (outro) | nome cru do enum |

Ordem da lista: CPU, GPU, Memória, Placa-mãe, Armazenamento, Rede, Fonte, depois alfabética por categoria; dentro da categoria, por nome.

## 3. Nome de exibição do dispositivo
1. Usar `hardware.Name`, removendo o ID se ele estiver embutido (ex. "Network /Network/hw-0").
2. Se o resultado ficar vazio ou igual à família → "Adaptador de rede N" (Network), "Disco N" (Storage), "Memória N" (Memory) ou "{Categoria} N", numerado na ordem de ID.
3. Dois ou mais dispositivos com o mesmo nome → sufixo " · {último segmento do ID}" (ex. "KINGSTON SKC3000D2048G · hw-1").

## 4. Consolidação (família Memory)
A família **Memory** vira **um único dispositivo "Memória"** (ID `family:Memory`), reunindo Total Memory, Virtual Memory e cada módulo DIMM:
- Módulos = hardwares cujo nome contém `(#N)`; N é o **slot físico**. Ordenar por N e numerar em sequência: "Módulo 1", "Módulo 2", … (ex.: os slots 1 e 3 viram Módulo 1 e Módulo 2).
- Prefixo no nome do sensor: "Módulo 1 · Temperature", "Física · Memory Used" (Total Memory → **Física**), "Virtual · Memory Used".
- Meta do cabeçalho: "N módulos · módulo 1 no slot 1, módulo 2 no slot 3".
- A lista de famílias consolidadas é configurável no código (`Consolidate = ["Memory"]`), pensada para estender a outras.

## 5. Tipos (ordem de exibição e rótulo)
| # | SensorType | Rótulo pt-BR | Graficável? |
|---|---|---|---|
| 0 | Temperature | Temperatura | sim (barra/gauge 0–100) |
| 1 | Load | Carga | sim (barra/gauge 0–100) |
| 2 | Power | Potência | sim (sparkline) |
| 3 | Clock | Clock | sim (sparkline) |
| 4 | Voltage | Tensão | sim (sparkline) |
| 4b | Current | Corrente | sim (sparkline) |
| 5 | Fan | Ventoinhas | sim (barra) |
| 6 | Control | Controle de ventoinha | sim (barra) |
| 7 | Flow | Fluxo | sim (sparkline) |
| 8 | SmallData | Memória | não (texto) |
| 9 | Data | Dados | não (texto) |
| 10 | Throughput | Taxa | sim (sparkline, base zero) |
| 11 | Level | Nível | sim (barra) |
| 12 | Factor | Contadores | não |
| 13 | Timing | Tempos SPD | não |
| 99 | (desconhecido) | nome cru | pela unidade |

"Graficável" = papel Reading, qualidade ≠ Unavailable e unidade em {%, degC, RPM, W, MHz, V, A, B/s, L/h}.

## 6. Papel (Role) e tag
| Condição | Role | Tag exibida | Oculto por padrão? |
|---|---|---|---|
| Nome contém "Warning Temperature", "Critical Temperature", "Limit", "Max Temperature" (em Storage/Memory), "Resolution" | Limit | `limite` | sim (LIMITES) |
| Sensores SPD de Memory (Timing, tRCD, tRP, tRAS, tCL, "Thermal Sensor Low/High/Critical Limit") | Metadata | `SPD` | sim (LIMITES) |
| Data Read/Written, Host Reads/Writes, Power On Count, Power On Hours, Unsafe Shutdowns, Media Errors | Accumulated | `acumulado` | não |
| "Life", "Available Spare", "Percentage Used" de Storage | Estimate | `estimativa` | não |
| Nome contém "Distance to TjMax" | Reading | `margem` (não é temperatura; excluir do "principal") | não |
| Qualidade Partial (amostras faltando) | — | `parcial` | não |
| Qualidade Unavailable (Value sempre null) | — | `sem leitura`, valor "—" | não |

No protótipo, o papel vem pré-classificado em `sensores.json`. No app, aplique as regras acima e mantenha uma tabela de overrides por regex em `Normalize/Roles.cs`.

## 7. Qualidade (derivada em runtime)
- Janela de 10 amostras por sensor: todas null → Unavailable; alguma null → Partial; senão Ok.
- `allZero` = o valor foi 0 em todas as amostras da sessão (até a primeira amostra ≠ 0). Isso **não** é erro: só fica oculto por padrão.

## 8. Regras de qualidade da coleta real (obrigatórias)
Derivadas de `coleta-hardware/LEIA-ME.md`:
1. **355 entradas ≠ 355 sensores úteis**: incluem limites, resolução, SPD, acumulados e zerados. Daí os filtros.
2. **Disco no Resumo vem do PDH**, não das taxas LHM de Storage (que tiveram 9/10 valores e picos iniciais; o LHM 0.9.6 usa Stopwatch/TimeSpan com conversão suspeita). No Detalhes, as taxas LHM aparecem com o valor cru, sem tratamento especial.
3. **Espaço em disco**: use bytes nativos (`DriveInfo`), não `Total Space` do LHM (rotulado GiB, mas na prática é GB decimal).
4. **Warning/Critical Temperature** de SSD são limites, nunca temperaturas atuais.
5. "Temperature #2" da Kingston (77,85/65,85 °C) não tem componente identificado: mostrar com o nome cru. Não usar como temperatura principal (o "Composite" vem antes).
6. Chipset 88–89 °C (rótulo do Super-I/O): mostrar como está; **não** gerar alerta.
7. Water In/Out, T Sensor sem valor → `sem leitura`. Water Flow e CPU Optional Fan em zero → ocultos por "Zerados", sem nenhuma conclusão.
8. Fan #3/#4/#5 não indicam posição física: não renomear.
9. `Win32_VoltageProbe.CurrentReading` é null: não usar como fonte.
10. StorageReliability nativo traz zeros: não usar.
11. **Nunca** exibir "saudável", "OK" ou "falha" com base nesses dados.

## 9. Formatação de unidades
| Unidade | Exibição |
|---|---|
| degC | 1 casa + "°C" (no Resumo: inteiro + "°C") |
| % | inteiro; 1 casa se < 10 |
| B/s | < 1 KiB → "B/s" inteiro; < 1 MiB → "KB/s" inteiro; senão "MB/s" com 1 casa (base 1024) |
| MiB | < 1024 → "MB" inteiro; senão "GB" com 1 casa |
| GiB | < 1024 → "GB" com 1 casa; senão "TB" com 2 casas |
| MHz | < 1000 → "MHz" inteiro; senão "GHz" com 2 casas |
| V | 3 casas · A: 2 casas · W: 1 casa · RPM: inteiro · L/h: 1 casa |
| ns, 1, desconhecida | ≥ 100 → inteiro; senão até 2 casas sem zeros à direita; unidade crua |
| null/NaN | "—" e sem unidade |
Separador decimal: **ponto** (consistência com o protótipo e leitura técnica). Rede no Resumo: KB/s inteiro ou MB/s com 1 casa, base 1024.
