# Instalar, atualizar e remover

## Instalar ou atualizar
1. Extraia o ZIP `MarzMonitor-<versão>-win-x64.zip`.
2. Dê dois cliques em **INSTALAR.cmd** (abra normalmente, **sem** "Executar como administrador").
3. A janela mostra se é instalação nova ou atualização e as opções:
   - Iniciar com o Windows (elevado, sem UAC a cada logon)
   - Iniciar oculto na bandeja
   - Atalho na Área de Trabalho
   - Abrir ao concluir
   - Substituir o GPU by MarzOps (só aparece se ele estiver instalado com início automático ou aberto)
4. Clique em **Instalar/Atualizar** e confirme o UAC.

O instalador:
- confere o SHA256 de todos os arquivos antes de tudo (e de novo na etapa elevada);
- fecha o Marz Monitor aberto pelo sinal `Local\MonitorTray.Quit.<SID>`: o app sai do jeito normal e devolve as fans ao automático. Só considera seguro um app que não fechou se `fan-status.json` (≤10 s) provar que nenhuma fan está manual; senão para sem mexer em nada;
- instala em `C:\Program Files\MonitorTray\<SID>\` (Administradores gravam; sua conta lê e executa);
- guarda a versão anterior em `_backups` (mantém 2) e volta para ela se algo falhar;
- registra duas tarefas da sua conta, na sessão interativa, com privilégio mais alto (nunca SYSTEM):
  - `MonitorTray-<SID>`: abre o app elevado sob demanda (atalhos usam esta);
  - `MonitorTray-Logon-<SID>`: inicia no logon com `--tray` (o toggle "Iniciar com o Windows" liga/desliga esta);
- cria atalhos no menu Iniciar (e Área de Trabalho, se marcado) e a entrada em Configurações › Aplicativos.

## Instalação silenciosa
`powershell -ExecutionPolicy Bypass -File Installer.ps1 -Silent [-NoDesktop] [-NoStartup] [-NoOpen] [-KeepGpuApp]` — mesmas verificações, fecha o app com segurança, pede UAC e imprime o resultado no console. Por padrão substitui o GPU by MarzOps; `-KeepGpuApp` deixa ele como está.

## Remover
Configurações › Aplicativos › **Marz Monitor** › Desinstalar (ou rode `Uninstall.ps1` na pasta de instalação). Fecha o app com segurança, remove tarefas, pasta e atalhos. Suas preferências ficam em `%APPDATA%\MonitorTray`.

## Onde ficam as coisas
| O quê | Onde |
|---|---|
| Programa | `C:\Program Files\MonitorTray\<SID>\` |
| Preferências | `%APPDATA%\MonitorTray\settings.json` |
| Status das fans (para o instalador) | `%APPDATA%\MonitorTray\state\fan-status.json` |
| Fans da placa (backup das curvas durante FULL) | `%APPDATA%\MonitorTray\state\ac-user-curves.json` |
| Telas do teclado | `%APPDATA%\MonitorTray\keyboard.json` |
| Status do teclado (diagnóstico) | `%APPDATA%\MonitorTray\state\keyboard-status.json` |
| Erros | `%APPDATA%\MonitorTray\logs\errors.log` |

## Armoury Crate
Os modos das ventoinhas da placa são pedidos ao serviço da ASUS (AsusFanControlService); o Armoury Crate continua dono das curvas. Antes do FULL o Marz guarda suas curvas User e as devolve ao voltar para CURVAS, ao sair e ao abrir o app (se o app fechou no meio). Sem Armoury Crate, a linha MOTHERBOARD não aparece.

## Convivência com o GPU by MarzOps
O Marz Monitor substitui o GPU by MarzOps (fans da GPU e tela/rodinha do teclado). Com a opção **Substituir** marcada, o instalador fecha o GPU by MarzOps pelo sinal de saída dele (as fans voltam ao automático) e **desativa** a tarefa `CmpInf-OLED-Rodinha-<SID>`. Nada é apagado: para voltar, reative a tarefa no Agendador.
Na primeira execução, as telas do teclado são importadas do perfil do GPU by MarzOps.
Se os dois rodarem juntos: as fans ficam só leitura no Marz Monitor (mutex comum) e os dois disputam a tela do teclado — evite.
