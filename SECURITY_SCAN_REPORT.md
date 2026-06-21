# Cobblemon Legacy - Relatorio de seguranca

Data do teste: 2026-06-21
Launcher testado: v1.3.0

## Arquivos verificados

| Arquivo | Tamanho | SHA256 |
| --- | ---: | --- |
| `dist/CobblemonLegacyLauncherSetup.exe` | 57.013.956 bytes | `BE0F84CEA7C47AD19147803FCE31230FEC09B7BA7AF65C3AFEEBDC4EFA46421F` |
| `publish/win-x64/CobblemonLegacy.exe` | 179.971.979 bytes | `E87A29CEF25C4FD6AF908751D29A6C8C96FC436F6EDFEA04EC8043D24FE19F0B` |

## Resultados

- Microsoft Defender, scan customizado no instalador: `found no threats`.
- Microsoft Defender, scan customizado no executavel do launcher: `found no threats`.
- `dotnet build -c Release`: compilacao concluida com sucesso.
- `dotnet publish -c Release -r win-x64`: publicacao concluida com sucesso.
- `dotnet list package --vulnerable --include-transitive`: nenhum pacote vulneravel encontrado nas fontes atuais do NuGet.
- Inno Setup 6.7.3: instalador gerado com sucesso e `SetupLogging=yes`.
- Instalador em modo silencioso: instalou em pasta temporaria com exit code `0`.
- Desinstalador em modo silencioso: removeu a instalacao temporaria com exit code `0`.
- `.\run.ps1 configure-profile`: execucao concluida com exit code `0`.
- Assinatura digital: instalador e executavel estao `NotSigned`.

## Observacao importante

O launcher nao foi assinado digitalmente com certificado de code signing. Por isso, alguns PCs podem exibir SmartScreen, bloqueio de navegador ou alerta de antivirus/reputacao mesmo quando a varredura nao encontra ameacas.

Para reduzir falsos positivos em jogadores, os proximos passos recomendados sao:

- Comprar/usar um certificado de code signing e assinar `CobblemonLegacy.exe` e `CobblemonLegacyLauncherSetup.exe`.
- Publicar sempre o SHA256 do instalador junto com o link de download.
- Enviar o instalador para analise no Microsoft Security Intelligence quando houver falso positivo.
- Fazer uma verificacao multi-engine no VirusTotal antes de divulgar uma versao nova.
