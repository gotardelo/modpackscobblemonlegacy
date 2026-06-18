# Cobblemon Legacy Launcher

Launcher/instalador em .NET/WPF para Minecraft 1.21.1 com Fabric e arquivos de modpack gerenciados por manifest.

## Rodar localmente

```powershell
.\run.ps1
```

O launcher abre uma interface grafica do Cobblemon Legacy. O Minecraft so inicia depois que o jogador escolhe Microsoft ou nickname offline e clica no botao `JOGAR`.

Servidor:

```txt
enx-cirion-16.enx.host:10068
```

O launcher cria a pasta do jogo em `%APPDATA%\.cobblemonlegacy` e a configuracao em `%APPDATA%\CobblemonLegacyLauncher\launcher.settings.json`.

Ao jogar/atualizar, o launcher tambem:

- adiciona o Cobblemon Legacy automaticamente na lista de servidores do Multiplayer;
- aplica uma vez um preset inicial de performance mais leve em `options.txt`.

## Manifest do modpack

O launcher procura primeiro o manifest remoto:

```txt
https://raw.githubusercontent.com/gotardelo/modpackscobblemonlegacy/main/manifest.json
```

Se nao conseguir baixar, ele usa o `manifest.json` local do projeto/executavel.

Para preparar os arquivos do pack:

```txt
pack/mods/
pack/resourcepacks/
pack/config/
pack/shaderpacks/
```

Depois gere o manifest com hashes:

```powershell
.\tools\New-ModpackManifest.ps1 -Version "1.0.0" -ReleaseBaseUrl "https://github.com/gotardelo/modpackscobblemonlegacy/releases/download/v1.0.0"
```

Suba os arquivos dentro de `pack/mods`, `pack/resourcepacks`, `pack/config` e `pack/shaderpacks` como assets da GitHub Release. O manifest guarda o destino local completo, mas a URL da release usa o nome do arquivo.

## Publicar o launcher

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -o publish\win-x64
```

Para gerar instalador `.exe`, abra `installer\CobblemonLegacy.iss` no Inno Setup e compile. O instalador final sai em `dist\CobblemonLegacyLauncherSetup.exe`.

## Login

O modo offline salva o nickname localmente. O modo Microsoft usa `CmlLib.Core.Auth.Microsoft` e abre o fluxo de autenticacao quando o jogador clica em Jogar.
