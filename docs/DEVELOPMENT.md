# Desenvolvimento

## Requisitos
SDK .NET **10.0.400** (fixado em `global.json`, sem roll-forward), Python 3 para o empacotamento.

## Comandos
```powershell
dotnet build src/MonitorTray.App -c Release -warnaserror
dotnet test tests/MonitorTray.Core.Tests -c Release
# telas fora da tela em PNG (dados simulados, sem bandeja/hardware)
MonitorTray.exe --preview <pasta> [--fake-new]
# uma coleta real somente leitura (LHM + PDH + fans), sem UI
MonitorTray.exe --read-test <novo.json>
# UI sem hardware nem admin
MonitorTray.exe --simulate [sensores.json]
```

## Versão e release
1. Suba `<Version>` em `Directory.Build.props` (0.1.x) e registre no `CHANGELOG.md`.
2. `python tools/build-package.py` → `Downloads\MarzMonitor-<versão>-win-x64.zip` (recusa sobrescrever).
3. Commit + tag `v<versão>`.

## Regras do projeto
- Nada de nome de modelo de hardware no código (teste `NoHardcodedModelNamesInSrc`).
- Nunca interpolar lacunas; null quebra o gráfico e mantém a barra anterior.
- Nunca exibir "OK/saudável/falha".
- Testes e agentes nunca escrevem nas fans; só o usuário clica.
- Arquivos `.cs` em CRLF.
