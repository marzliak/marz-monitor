# 01 · Produto

## Visão
Um "DU Meter" moderno: discreto, sempre à mão na system tray, mostrando num relance o que importa (CPU, GPU, memória, disco, rede), e com uma janela de detalhes que expõe **todo** sensor que o hardware oferece, com gráficos vivos.

## Usuário
Entusiasta/power user no Windows, com hardware de alto desempenho (ex.: i9-14900K + RTX 3090 + 5 NVMe), que quer acompanhar temperaturas, potência e tráfego sem abrir um HWiNFO gigante.

## Superfícies
| Superfície | Quando | Tamanho |
|---|---|---|
| Ícone na tray | sempre | 16×16 (renderizado dinamicamente a cada tick) |
| Popup | clique esquerdo no ícone | 344 px de largura, altura pelo conteúdo (~232 px) |
| Popup › Configurações | engrenagem no popup | mesma janela, conteúdo trocado |
| Janela Detalhes | botão "Detalhes ↗" no popup ou item do menu | 960×640 padrão, redimensionável, arrastável, maximizável |

## Escopo (v1)
- Resumo: CPU % + °C + W; GPU % + °C + W; memória GB usado/total + %; disco leitura/escrita MB/s; rede ↓/↑ com gráfico espelhado autoescalável
- Detalhes: todos os dispositivos e sensores descobertos, agrupados por tipo, com cartões, gauges, sparklines, barras e grade por núcleo; busca; filtros "Zerados" e "Limites"
- Configurações: tema (claro/escuro/sistema), paleta (6), opacidade, atualização (0,5/1/2 s), histórico do gráfico de rede (30/60/120 s), escala da rede (separada/única), iniciar com o Windows, fechar ao perder foco
- Persistência de configurações e da posição/tamanho da janela Detalhes

## Fora de escopo (v1)
- Alertas, notificações, limiares, "saúde do sistema" (os dados não sustentam esse tipo de conclusão; ver `05-sensores.md`)
- Controle de ventoinhas/RGB
- Gravação de logs em disco/exportação CSV (candidato a v2)
- Overlay em jogos
- Outros SOs

## Princípios
1. **Glanceable**: o popup responde "está tudo bem?" em < 1 s de leitura.
2. **Adaptável**: zero suposições sobre o hardware.
3. **Honesto**: nunca fabrica, interpola ou rotula dados além do que o provedor informa.
4. **Leve**: invisível no Gerenciador de Tarefas.
