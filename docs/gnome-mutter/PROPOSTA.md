# frame-motion-plus no GNOME/Mutter: o que quebrou e o que mudar

Base: `adds39939/frame-motion-plus` commit `c65b806`. Branch `gnome-mutter` do fork `danilogc4/frame-motion-plus`, para uso pessoal.
Máquina: Bazzite, kernel 7.3.4, GNOME Shell 51.1 (Wayland), RTX 4080 Super, driver 620.70.06,
VFX SDK 1.3.0, Elgato 4K X em USB 10 Gbps, TV LG a 119,88 Hz com HDR (bt2100), segundo monitor
Dell 4K60 em retrato. VRR desligado na configuração de dois monitores. Medições de 22/11/2026
nos arquivos `.txt` desta pasta.

## Medições

| Captura 4k30-hdr, VFG Medium 2x, 30 s | Slots perdidos | Desvio dos intervalos | Captura → tela p50 / p99 |
|---|---|---|---|
| Janela (lead adaptativo padrão) | 626 / 3358 (19%) | 1,81 ms, máx 25,4 | 93,9 / 104,1 ms |
| Tela cheia (padrão) | 557 / 3358 (17%) | 1,68 ms, máx 17,0 | 90,3 / 97,7 ms |
| Tela cheia, `--latch-ms 5.5 --present-lead-ms 5.5` | **50 / 3358 (1,5%)** | **0,57 ms**, 0 fora da grade | **79,7 / 87,7 ms** |

- VFG: d p50 1,83 ms, p99 1,86 ms (bench: 4K Medium p99 4,0–4,6 ms; gate 1440p Medium 4,33 ms, PASS).
- `bench present --test latch --fullscreen`: latch do Mutter até **~5 ms** antes do primeiro pixel
  (FIFO 4,9; FIFO-latest-ready 5,4; mailbox 4,7). No KWin do autor o lead padrão era ~1,3 ms.
- `bench present --test cadence --fullscreen`: **267 de 1196 intervalos fora da grade** (p1 5,2 ms,
  p99 11,6 ms) num swapchain SDR B8G8R8A8 sobre a saída HDR. O próprio Mutter não segura 120 Hz liso.
- `bench jitter` a 119,88 Hz: p99 do atraso de acordar 78 µs sem RT e 50 µs com `--rt`. As threads
  **não** são o problema.
- Upload da captura para a GPU: p50 14,2 ms por quadro 4K P010 (~25 MB), com buffers USERPTR em memória
  pinned. O link PCIe estava em Gen1 x16 em repouso (máx Gen4).

## O que quebra no GNOME

1. **A janela abre onde está o mouse.** Não há opção de saída; o SDL3 cria a janela e o Mutter decide.
   Com dois monitores, abriu no Dell (60 Hz, retrato).
2. **O lead adaptativo sobe até quase o teto e fica.** `LeadController.OnLateContent` trata toda
   apresentação atrasada com hand-over cedo como "o latch está além disso" e pula para
   hand-over + 2 ms (5,5 → 9,8 → 14,1 ms). No Mutter parte desses atrasos são irregularidade do próprio
   compositor (ver o teste de cadência), não latch. O piso fica 10 min e a descida é de 0,25 ms a cada
   10 s, então o lead não volta: cada ms a mais é latência.
3. **Sem VRR na configuração de dois monitores.** O fmp mediu corretamente "fixed refresh" e caiu para a
   grade de 119,88 Hz com stride 2. Nessa grade qualquer atraso vira um quadro mostrado por 1 ou 3
   atualizações. Não testado ainda: só a TV ativa, com `ratemode variable` (a config salva do GNOME).
4. **HDR no Mutter provavelmente impede direct scanout.** A saída está em bt2100; um swapchain que
   não casa com o formato e o espaço de cor da saída é composto pelo shader de cor do Mutter, o que
   explica o latch de ~5 ms e a cadência irregular. Não verificado (falta testar com HDR desligado e com
   swapchain HDR10 no `bench present`).

## Ajustes propostos para o fork

1. **`--output <conector|nome>`** (e `display.output` no settings): listar com `SDL_GetDisplays`,
   criar a janela com `SDL_CreateWindowWithProperties` usando a posição
   `SDL_WINDOWPOS_CENTERED_DISPLAY(id)` e, em tela cheia, `SDL_SetWindowFullscreenMode` naquele display
   (no Wayland o SDL passa a saída em `xdg_toplevel.set_fullscreen`). Mostrar a saída escolhida em
   `capture --list`. Arquivos: `src/FrameMotionPlus.Vulkan/Windowing/SdlWindow.cs`, `SdlNative.cs`, CLI.
2. **Lead que não sobe por soluço do compositor** (`src/FrameMotionPlus.Pipeline/PresentAccounting.cs`,
   `LeadController`):
   - só subir quando houver N atrasos (ex.: 3) numa janela curta (ex.: 1 s), não no primeiro;
   - o salto "hand-over + 2 ms" só quando o atraso se repete no mesmo ponto; atraso isolado com
     hand-over bem antes do latch medido conta como "do display", como o fmp já faz para o VRR;
   - `--lead-max-ms` para limitar o teto (hoje ~2 períodos, 16,2 ms a 120 Hz);
   - piso por menos tempo (ex.: 60 s em vez de 10 min) e descida mais rápida.
3. **Medir o latch também em tela cheia e começar dele.** Hoje o lead base é 1,5 refresh ou o latch
   medido só para janela. Rodar a mesma varredura de `bench present --test latch` antes da captura em
   tela cheia e usar latch + margem (no Mutter, ~5,5 ms) como base.
4. **Perfil `--compositor mutter|kwin|auto`** (detectado por `XDG_CURRENT_DESKTOP`) com os valores
   medidos aqui: latch inicial 5,5 ms, critério de subida mais tolerante e aviso quando VRR está desligado
   na saída ("ative a taxa variável em Configurações > Monitores; com dois monitores o GNOME pode
   desligá-la").
5. **Swapchain que casa com a saída.** Escolher formato e espaço de cor iguais aos da saída do Mutter
   (HDR10 PQ quando a saída está em bt2100, também para conteúdo SDR) para dar chance de direct scanout;
   medir de novo latch e cadência.
6. **Upload de 14 ms:** investigar antes de mudar código. Hipótese: PCIe em economia (Gen1 em repouso).
   Testar com o GPU fixo em desempenho máximo e medir `capture gpu upload`. Se confirmar, documentar no
   README em vez de mexer no fmp.

## Próximos testes (antes de escrever código)

- Só a TV ativa (VRR ligado): `fmp bench present --test vrr --fullscreen` e a captura em tela cheia.
- HDR desligado na TV: `bench present --test cadence --fullscreen` para separar HDR do resto.
- Captura com `--latch-ms 5.5 --present-lead-ms 5.5` por 10 min para ver se o lead volta a subir.

## Licença

O repositório original não tem arquivo de licença. O fork existe para uso pessoal (os termos do GitHub
permitem fazer fork de repositório público); não há intenção de distribuir versões modificadas. Se alguma
mudança servir ao autor, o caminho é mandá-la como PR para `adds39939/frame-motion-plus`.

## Estado

Proposta apenas. Nenhum código foi alterado ainda.

## Rodada 2: só a TV ativa (VRR ligado, HDR ligado), 22/11/2026

Arquivos `vrr-hdr.txt` e `vrr-hdr2.txt`.

- **O segundo monitor era a maior causa da cadência irregular.** `bench present --test cadence --fullscreen`
  com só a TV: 4 de 1199 intervalos fora da grade (p99 8,41 ms), contra 267 de 1196 com o Dell de 60 Hz ligado.
- Latch do Mutter só com a TV: até ~6,2 ms (cobertura 6,4 ms).
- VRR do Mutter: `bench present --test vrr` deu "mixed": 465 de 779 pares seguiram os presents, 0 na grade.
  A sonda do `capture` viu 28–31 de 45 pares seguindo e mesmo assim escolheu fixed refresh.
- Captura padrão (fixed, só a TV): 77 de 3358 slots perdidos (2,3%), desvio 0,61 ms, 90 ms captura → tela.
  O lead foi de 12,5 para 15,6 ms.
- Com `--latch-ms 6.5 --present-lead-ms 6.5`: o lead subiu 4 vezes até 16,2 ms (6,5 → 10,6 → 11,6 → 16,0 → 16,2),
  85 perdidos e veredito FAIL (o verificador discordou do contador ao vivo em 1 slot). Confirma o item 2 da proposta:
  o controlador sobe por soluços e não volta.
- `--clock vrr` forçado: a TV caiu do HDMI no meio da captura (o PipeWire perdeu o sink HDMI e voltou) e o Mutter
  reaplicou a configuração salva de dois monitores sem VRR. Só 9 quadros mostrados; teste inválido. Pode ser a TV
  ressincronizando ao entrar e sair do VRR, ou o Mutter. Precisa repetir com a config só-TV salva (`gdctl set -P`
  ou pelas Configurações) para não voltar sozinha.

Ajustes que mudam com isso:
- Item 4 (perfil Mutter): avisar quando houver outra saída ativa com taxa diferente, e não só quando o VRR estiver
  desligado. Com dois monitores o Mutter perde o ritmo mesmo em tela cheia.
- Item 2 ganha prioridade: mesmo com o Mutter liso, o lead sobe até o teto e custa ~10 ms de latência.
- Novo: a sonda de VRR aceitar "seguindo na maioria" (ex.: ≥ 60% dos pares, 0 na grade) no Mutter, e tratar
  queda do HDMI durante VRR (pausar e refazer a sonda em vez de seguir apresentando para uma saída que sumiu).
- Teste com HDR desligado ainda não feito.

## Rodada 3: só a TV, config salva (VRR e HDR separados), 22/11/2026

Arquivos `vrr3.txt`, `sdr.txt`, `sdr-vrr.txt`. Captura 4k30-hdr, VFG Medium 2x, tela cheia, 30 s.

| Saída da TV | `--clock` | Perdidos | Desvio | Captura → tela p50 / p99 |
|---|---|---|---|---|
| HDR (bt2100), dois monitores | auto (fixed) | 557 (17%) | 1,68 ms | 90 / 98 ms |
| HDR, só a TV | auto (fixed) | 77 (2,3%) | 0,61 ms | 90 / 98 ms |
| HDR, só a TV | vrr | 1678 (todos atrasados) | 3,02 ms | 79 / 86 ms |
| **SDR, só a TV** | auto (fixed) | **9 (0,27%)** | **0,22 ms** | 77 / 77 ms |
| **SDR, só a TV** | **vrr** | **1** | 0,32 ms | **65 / 66 ms** |

- **O VRR do Mutter não seguiu a janela em nenhum caso** (`variable refresh lost ... 30.0 s`). Com a config salva
  a TV não caiu do HDMI; a queda da rodada 2 não se repetiu.
- Em HDR + `--clock vrr`, sem VRR de verdade, cada quadro sai com atraso irregular (desvio 3 ms): ruim.
- Em SDR + `--clock vrr` funciona por acaso: 59,94 quadros/s caem exatos na grade de 119,88 Hz, e esse modo
  entrega cada quadro na hora, sem antecedência. Por isso a menor latência (65 ms).
- Em SDR o Mutter compõe liso: cadência 0 de 1199 fora da grade e o lead não precisou subir (12,5 ms).
- **Custo do SDR:** o fmp continua mandando um swapchain HDR10 (PQ), mas a saída está em SDR, então o Mutter
  converte e o HDR do PS5 não chega à TV.

Conclusões para o fork:
- O pior inimigo no GNOME é o segundo monitor, depois a composição HDR do Mutter. O VFG em si sobra.
- Item 5 (swapchain que casa com a saída) vira o mais importante para HDR: com a saída em bt2100 o Mutter
  precisa recompor tudo. Testar swapchain HDR10 no `bench present` antes de escrever código.
- A sonda de VRR acertou ao dizer "fixed": o Mutter não dá VRR a essa janela. Vale descobrir o motivo
  (Mutter 51 só dá VRR com direct scanout? cursor por cima?) antes de mexer na sonda.
- Modo "grade exata": quando o refresh é múltiplo exato do ritmo de saída, apresentar como no `--clock vrr`
  (na hora, sem lead), mesmo sem VRR. Foi o melhor resultado medido.
