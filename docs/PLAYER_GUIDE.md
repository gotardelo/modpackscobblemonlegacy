# Cobblemon Legacy - Guia rapido para players

## Como instalar

1. Baixe `CobblemonLegacyLauncherSetup.exe` pela pagina oficial ou release do GitHub.
2. Execute o instalador.
3. Abra `Cobblemon Legacy`.
4. Escolha login Microsoft ou nickname offline.
5. Clique em `JOGAR`.

Na primeira vez o launcher baixa Minecraft 1.21.1, Fabric, mods e resourcepacks. Isso pode demorar.

O servidor ja aparece na tela Multiplayer automaticamente.

## Se o Windows avisar sobre seguranca

O launcher ainda nao tem assinatura digital. Alguns PCs podem mostrar SmartScreen ou aviso de reputacao porque o arquivo e novo e nao assinado.

Confira se o download veio do link oficial e compare o SHA256 publicado na release.

## Se o jogo nao abrir

1. Abra o launcher.
2. Clique em `Diagnostico` para conferir o estado do launcher.
3. Clique em `Gerar ZIP` ou use `Relatorio`.
4. Envie o arquivo `.zip` gerado no Discord.
5. Se quiser tentar resolver sozinho, clique em `Reparar`.

O botao `Verificar` checa se esta tudo certo sem baixar nada. O botao `Reparar` reinstala partes quebradas do Fabric e revalida o pack. Ele nao apaga mundos.

## PCs fracos

Abra `Opcoes` e clique em `PC fraco`.

Esse preset reduz resolucao inicial, RAM, ativa modo de compatibilidade, usa menos resourcepacks e aplica configuracoes mais leves dentro do Minecraft na proxima preparacao do jogo.

Se o PC for bom, use `Alto desempenho` e mantenha resourcepacks em `Completo`.

## Backups e privacidade

O launcher cria backups locais antes de reparos e ajustes automaticos em:

`%APPDATA%\.cobblemonlegacy\backups`

Ele tambem grava telemetria local para suporte em:

`%APPDATA%\CobblemonLegacyLauncher\telemetry.jsonl`

Esses arquivos nao sao enviados automaticamente. O envio so acontece se voce gerar o ZIP de suporte e mandar no Discord.

## Links

- Servidor: `enx-cirion-16.enx.host:10068`
- Discord: https://discord.gg/sETS2Fc7Ey
- Site: https://www.cobblemonlegacy.com.br
