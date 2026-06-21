# Cobblemon Legacy - Relatorio de seguranca

Data do teste: 2026-06-20  
Launcher testado: v1.2.0

## Arquivos verificados

| Arquivo | Tamanho | SHA256 |
| --- | ---: | --- |
| `dist/CobblemonLegacyLauncherSetup.exe` | 57.004.251 bytes | `8D588BB996479249976FBFC81C801CE1611D87ECE3B58E96F153F45E52D465B5` |
| `publish/win-x64/CobblemonLegacy.exe` | 179.959.691 bytes | `4D862FB969802B8104091ABA85E400D9B0312FFD9E6D7928BFC21B266C7F45A7` |

## Resultados

- Microsoft Defender, scan customizado no instalador: `found no threats`.
- Microsoft Defender, scan customizado no executavel do launcher: `found no threats`.
- `dotnet build -c Release`: compilacao concluida com sucesso.
- `dotnet list package --vulnerable --include-transitive`: nenhum pacote vulneravel encontrado nas fontes atuais do NuGet.
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
