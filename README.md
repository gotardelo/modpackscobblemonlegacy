# Cobblemon Legacy Launcher

Launcher/instalador em .NET/WPF para Minecraft 1.21.1 com Fabric e arquivos de modpack gerenciados por manifest.

## Rodar localmente

```powershell
.\run.ps1
```

O launcher abre uma interface grafica do Cobblemon Legacy. Depois que o jogador escolhe Microsoft ou nickname offline, o botao principal mostra `ATUALIZAR` quando falta algo no pack e `JOGAR` quando esta pronto.

Servidor:

```txt
enx-cirion-16.enx.host:10068
```

O launcher cria a pasta do jogo em `%APPDATA%\.cobblemonlegacy` e a configuracao em `%APPDATA%\CobblemonLegacyLauncher\launcher.settings.json`.

Ao atualizar/jogar, o launcher tambem:

- adiciona o Cobblemon Legacy automaticamente na lista de servidores do Multiplayer;
- aplica um preset inicial/adaptativo de performance mais leve em `options.txt`;
- ajusta RAM e downloads paralelos conforme o PC do jogador;
- valida e repara bibliotecas do Fabric antes de iniciar o jogo.
- verifica updates do proprio launcher na release publica do GitHub;
- carrega avisos/noticias de `news.json`;
- abre uma tela de diagnostico com sistema, Java, pack, Fabric e logs;
- gera pacote ZIP de suporte com logs e relatorio, e abre o Discord;
- oferece botao `Reparar` para reinstalar Fabric e revalidar o pack sem apagar mundos.
- aplica perfis de performance reais em `options.txt`: automatico, PC fraco, equilibrado e alto desempenho.
- oferece perfis de resourcepacks: completo, equilibrado e leve para PC fraco.
- cria backup local de `options.txt`, `servers.dat` e `config/` antes de reparos e ajustes automaticos.
- grava telemetria local em `%APPDATA%\CobblemonLegacyLauncher\telemetry.jsonl` para suporte.
- tem botao `Verificar` para checar integridade sem baixar nada.

## Links uteis

- Guia para players: [`docs/PLAYER_GUIDE.md`](docs/PLAYER_GUIDE.md)
- Checklist de testes: [`docs/QA_CHECKLIST.md`](docs/QA_CHECKLIST.md)
- Pagina de download: [`docs/download.html`](docs/download.html)
- Relatorio de seguranca: [`SECURITY_SCAN_REPORT.md`](SECURITY_SCAN_REPORT.md)

## Manifest do modpack

O launcher procura primeiro o manifest remoto:

```txt
https://raw.githubusercontent.com/gotardelo/cobblemonlegacy-downloads/main/manifest.json
```

Se nao conseguir baixar, ele usa o `manifest.json` local do projeto/executavel.

Os avisos do launcher usam:

```txt
https://raw.githubusercontent.com/gotardelo/cobblemonlegacy-downloads/main/news.json
```

Para preparar os arquivos do pack:

```txt
pack/mods/
pack/resourcepacks/
pack/config/
pack/shaderpacks/
```

Depois gere o manifest com hashes:

```powershell
.\tools\New-ModpackManifest.ps1 -Version "1.0.0" -ReleaseBaseUrl "https://github.com/gotardelo/cobblemonlegacy-downloads/releases/download/v1.0.0"
```

Antes de publicar qualquer troca de mods/resourcepacks, rode a validacao contra duplicados:

```powershell
.\tools\Test-PackIntegrity.ps1
```

Suba os arquivos dentro de `pack/mods`, `pack/resourcepacks`, `pack/config` e `pack/shaderpacks` como assets da GitHub Release. O manifest guarda o destino local completo, mas a URL da release usa o nome do arquivo.

## Publicar o launcher

```powershell
dotnet publish -c Release -r win-x64 -o publish\win-x64
```

Para gerar instalador `.exe`, abra `installer\CobblemonLegacy.iss` no Inno Setup e compile. O instalador final sai em `dist\CobblemonLegacyLauncherSetup.exe`.

Tambem existe um script de release local:

```powershell
.\tools\Publish-LauncherRelease.ps1 -Version "1.4.3"
```

## Login

O modo offline salva o nickname localmente. O modo Microsoft usa `CmlLib.Core.Auth.Microsoft` e abre o fluxo de autenticacao quando o jogador clica em Jogar.
