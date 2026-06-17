# Cobblemon Legacy Launcher

Launcher/instalador em .NET para Minecraft 1.21.1 com Fabric e arquivos de modpack gerenciados por manifest.

## Rodar localmente

```powershell
.\run.ps1
.\run.ps1 install
.\run.ps1 play
.\run.ps1 paths
```

O launcher cria a pasta do jogo em `%APPDATA%\.cobblemonlegacy` e a configuracao em `%APPDATA%\CobblemonLegacyLauncher\launcher.settings.json`.

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

Suba os arquivos do `pack` para uma GitHub Release e suba o `manifest.json` no branch `main`.

## Publicar o launcher

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -o publish\win-x64
```

Para gerar instalador `.exe`, abra `installer\CobblemonLegacy.iss` no Inno Setup e compile. O instalador final sai em `dist\CobblemonLegacyLauncherSetup.exe`.

## Observacao sobre login

Esta primeira versao usa sessao offline do `CmlLib.Core`. Ela instala Minecraft/Fabric e abre o jogo, mas servidores online-mode vao exigir login Microsoft. O proximo passo natural e adicionar autenticacao Microsoft ao launcher.
