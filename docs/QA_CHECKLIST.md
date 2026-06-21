# Cobblemon Legacy - Checklist de testes

Use este checklist antes de mandar uma nova versao para os players.

## Instalador

- Instalar em Windows 10.
- Instalar em Windows 11.
- Instalar sem permissao de administrador.
- Abrir pelo atalho da area de trabalho.
- Desinstalar pelo Windows.
- Confirmar que o instalador nao apaga `.cobblemonlegacy`.

## Launcher

- Login offline com nickname novo.
- Fechar e abrir o launcher para confirmar que o nickname ficou salvo.
- Login Microsoft iniciado e cancelado sem erro fatal.
- Botao `Opcoes` abre e salva RAM/resolucao.
- Preset `PC fraco` salva corretamente.
- Preset `Equilibrado` salva corretamente.
- Preset `Alto desempenho` salva corretamente.
- Botao `Relatorio` gera arquivo, copia texto e abre Discord.
- Botao `Reparar` reinstala Fabric e revalida o pack sem apagar mundos.
- Botao `Atualizar` aparece quando existir release nova.

## Pack

- Primeira instalacao baixa Minecraft, Fabric, mods e resourcepacks.
- Segunda abertura mostra `Pronto para jogar`.
- Remover um mod manualmente e confirmar que o launcher baixa de novo.
- Corromper um arquivo pequeno do pack e confirmar que o launcher repara.
- Conferir se o servidor aparece no Multiplayer.

## Jogo

- Abrir com PC de 8 GB RAM.
- Abrir com PC de 16 GB RAM.
- Entrar no servidor `enx-cirion-16.enx.host:10068`.
- Fechar Minecraft e confirmar que o launcher volta/minimiza conforme opcao.

## Seguranca

- Rodar Microsoft Defender no instalador.
- Conferir SHA256 do instalador publicado.
- Publicar `SECURITY_SCAN_REPORT.md` na release.
- Testar download em navegador com SmartScreen.
