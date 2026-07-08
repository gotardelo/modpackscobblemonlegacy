# Cobblemon Legacy - Relatorio de seguranca

Data do teste: 2026-07-08
Launcher testado: v1.4.5

## Arquivos verificados

| Arquivo | Tamanho | SHA256 |
| --- | ---: | --- |
| `dist/CobblemonLegacyLauncherSetup.exe` | 57.026.960 bytes | `7C2C336478D6AE9D6D4A86773473C8CE3B11B3E31EC8F26EF9D2BD3C3FFBF552` |
| `publish/win-x64/CobblemonLegacy.exe` | 180.025.227 bytes | `6EE1C22FE3110E408FEC9CAD0CCF3E440AC10EBD69760BC38D80EF9D756A96B6` |

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
