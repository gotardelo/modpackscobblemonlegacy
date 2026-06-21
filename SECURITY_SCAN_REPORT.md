# Cobblemon Legacy - Relatorio de seguranca

Data do teste: 2026-06-20  
Launcher testado: v1.1.0

## Arquivos verificados

| Arquivo | Tamanho | SHA256 |
| --- | ---: | --- |
| `dist/CobblemonLegacyLauncherSetup.exe` | 57.006.555 bytes | `42DE002D41FFB0DF2842C860C743A89FBDB55C7F7D6D38E0E4EE8E1D157DB645` |
| `publish/win-x64/CobblemonLegacy.exe` | 179.935.115 bytes | `71EEECE1B411DB1FEF373E1726B58023E1798E0AB2E70A1A91057F258550D8A9` |

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
