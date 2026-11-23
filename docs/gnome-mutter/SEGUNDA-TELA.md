# Seletor de tela e segunda tela ligada: viabilidade

Pedido (22/11/2026): escolher em qual tela o jogo abre e poder deixar as outras telas ligadas enquanto o
HypeFrame roda, para ver o OBS gravando na segunda tela. Tem que valer para qualquer combinação de telas,
não só para a TV LG + Dell desta máquina. Este relatório vem antes de qualquer código.

## Resumo

| Item | Viável? | Esforço | Risco |
|---|---|---|---|
| Seletor de tela | **Sim, testado.** O SDL3 da distrobox põe a tela cheia na tela pedida. | Pequeno (fmp + HypeFrame) | Baixo |
| Outras telas ligadas | **Já funciona hoje** (desligar "Só a TV"). O custo é o ritmo dos quadros. | Pequeno (interface) | Médio, mas medido e contornável |
| OBS gravando o jogo enquanto o fmp roda | **Não do jeito que o OBS está configurado hoje** | De zero a grande, conforme o caminho | **Alto: é o maior problema** |

**O maior problema não é a segunda tela, é a placa de captura.** A Elgato entrega a imagem para um programa por
vez, e o seu OBS hoje lê a placa direto (`/dev/video1`, 4K60 NV12). Com o fmp rodando, o OBS fica sem a imagem do
PS5, com ou sem segunda tela. É preciso escolher o que o OBS vai gravar (seção 3) antes de decidir o resto.

## 1. Seletor de tela

### Como é hoje

O fmp cria uma janela comum, mostra um quadro e pede tela cheia "na tela em que a janela está"
(`SdlWindow.SetFullscreen`). No Wayland um programa não escolhe onde a janela nasce: o Mutter põe a janela onde
está o mouse. Foi por isso que o fmp abriu no Dell no primeiro teste.

### O que o SDL3 vê

SDL 3.4.20 (distrobox `fmp`), driver `wayland`:

| SDL id | Nome no SDL | Posição | Modo | Principal |
|---|---|---|---|---|
| 3 | `LG Electronics LG TV SSCR2` | 2160,403 | 3840x2160 a 119,880 Hz | sim |
| 4 | `Dell Inc. 32"` | 0,0 | 2160x3840 a 60,000 Hz | não |

O SDL usa fabricante e modelo como nome, não o conector (`HDMI-1`, `DP-1`). A posição de cada tela é a mesma que o
Mutter informa (`org.gnome.Mutter.DisplayConfig`), então o HypeFrame consegue casar as duas listas pelo nome ou pela
posição.

### Teste feito (22/11/2026)

Um script na distrobox (`SDL_CreateWindow`, um quadro preto, depois `SDL_SetWindowPosition` com
`SDL_WINDOWPOS_CENTERED_DISPLAY(id)` e `SDL_SetWindowFullscreen`) produziu:

```
janela nova caiu em: LG Electronics LG TV SSCR2
pedindo tela cheia em: Dell Inc. 32"
SetWindowPosition: False
SetWindowFullscreen: True
depois da tela cheia está em: Dell Inc. 32"
```

O `SetWindowPosition` devolve falso porque o Wayland não deixa a janela se posicionar sozinha. Mesmo assim, o SDL
guarda a tela pedida e a usa no pedido de tela cheia (`xdg_toplevel.set_fullscreen` com a saída escolhida). Essa é
a mudança de julho de 2025 no SDL: a saída pedida só vale se uma posição foi pedida antes da tela cheia. O Mutter
respeita a saída.

### O que mudar

- **fmp (fork):** opção `--output <nome|número>` em `play`, `capture` e `hdr-pattern`. Ela procura a tela pelo
  nome (parte dele basta, como "LG TV") ou pelo número da lista e chama `SDL_SetWindowPosition(
  SDL_WINDOWPOS_CENTERED_DISPLAY(id))` logo antes de `SetFullscreen(true)`. O `capture --list` (ou um `fmp displays`)
  passa a listar as telas. Arquivos: `SdlNative.cs` (+`SDL_GetDisplays`, `SDL_GetDisplayName`,
  `SDL_SetWindowPosition`), `SdlWindow.cs`, a CLI.
- **Cuidado:** a janela aparece por uma fração de segundo na tela do mouse antes de pular. Tudo que o fmp mede
  "antes de tocar" (taxa da tela, latch do compositor) tem que ser medido depois do pulo. O evento `DisplayChanged`
  já existe e serve para refazer a leitura.
- **Em janela (sem tela cheia) não dá para escolher.** O Wayland não permite. O seletor vale só para tela cheia,
  que é o uso normal.
- **HypeFrame:** um seletor "Tela do jogo" montado a partir do Mutter, com nome, conector, Hz e HDR/SDR de cada tela,
  para qualquer número de telas. Primeira opção: "Onde está o mouse (como hoje)". O valor salvo é o nome da tela,
  para continuar valendo se a ordem mudar.

Não há saída sem mexer no fmp. No Wayland não existe `wmctrl`, e mover a janela por fora dependeria de uma extensão
do GNOME.

## 2. Outras telas ligadas durante o jogo

### Como é hoje no HypeFrame

Isso já é possível: basta deixar "Ajustar a TV antes de jogar" desligado, ou ligado com "Só a TV" desligado. O
problema é a interface, que empurra para desligar as outras telas: o aviso no topo e os dois perfis rápidos ligam
"Só a TV". Para ficar genérico:

- trocar "TV" por "Tela do jogo" em toda a interface;
- trocar "Só a TV" por "Outras telas durante o jogo: manter ligadas / desligar";
- o aviso deixa de pedir para desligar e passa a sugerir a antecedência fixa (abaixo) quando há outra tela ligada;
- um terceiro perfil, "Com segunda tela", que mantém as outras telas e usa antecedência fixa.

### O custo medido (22/11/2026, captura 4k30-hdr, VFG Média 2x, 30 s, tela cheia na TV)

| Telas | Configuração | Slots perdidos | Captura → tela p50 |
|---|---|---|---|
| TV + Dell, TV em HDR | padrão | 557 (17%) | 90 ms |
| TV + Dell, TV em HDR | `--latch-ms 5.5 --present-lead-ms 5.5` | **50 (1,5%)** | **80 ms** |
| Só a TV, HDR | padrão | 77 (2,3%) | 90 ms |
| Só a TV, SDR | padrão | 9 (0,27%) | 77 ms |
| Só a TV, SDR | `--clock vrr` | 1 | 65 ms |

`bench present --test cadence --fullscreen`: 267 de 1196 intervalos fora da grade com o Dell ligado, contra 4 de 1199
só com a TV.

Com antecedência fixa, duas telas ficaram melhores que a configuração padrão com só a TV. A segunda tela pesa, mas
o maior estrago vem do controle de antecedência do fmp, que sobe a cada soluço e não desce (item 2 da
`PROPOSTA.md`). Duas telas não testadas: TV em SDR, e o OBS mostrando a prévia na segunda tela.

### Por que a segunda tela atrapalha (hipóteses, não verificadas)

- O Mutter tem um relógio de quadros por tela, mas pinta todas as telas na mesma thread. A TV a 119,88 Hz e o Dell a
  59,997 Hz não são múltiplos exatos, então os prazos de pintura das duas se cruzam de tempos em tempos. Quando se
  cruzam, o quadro da TV pode perder o prazo.
- O Dell está em retrato e é sempre composto com rotação, o que é trabalho a mais na GPU a cada repintura.
- No teste o Dell estava parado. **Com o OBS mostrando a prévia de 4K a 60 qps, o Dell repinta sem parar**, e o
  efeito pode ser maior que o medido. Precisa medir.
- Há relatos antigos de problemas do Mutter com várias telas na NVIDIA (mutter #3461, #2948; Launchpad 1970291,
  corrigido no Mutter 46.1). São de versões antigas e não descrevem exatamente este caso.

O Dell não oferece 59,94 Hz (só 59,997), então não dá para alinhar as duas taxas.

### O que fazer para ter a segunda tela ligada com pouco custo

1. Antecedência fixa de 5,5 a 6,5 ms (já medido: 1,5%). O certo a longo prazo é corrigir o controle de antecedência
   (item 2 da proposta).
2. No OBS, desativar a prévia durante a gravação, ou testar se a prévia ligada piora as perdas. Quem gasta é a
   prévia, não a janela do OBS aberta.
3. Não mexer em janelas da segunda tela durante o jogo. Cada animação do GNOME ali é repintura.
4. VRR não entra na conta por enquanto: o Mutter não deu VRR à janela do fmp nem com só a TV ligada.

## 3. O maior problema: a placa de captura com o OBS e o fmp ao mesmo tempo

### Os fatos

- **Um programa por vez.** Pela documentação do kernel, um driver V4L2 não deve copiar o mesmo fluxo para vários
  programas; isso fica a cargo de um intermediário em espaço de usuário. Na prática, o segundo programa recebe
  "Device or resource busy", como aconteceu nos testes com o OBS e com o mpv.
- **O OBS hoje** (cena `Untitled`): fonte `v4l2_input` em `/dev/video1`, 3840x2160 NV12 a 60 qps, faixa de cor 1.
  Grava em `obs_nvenc_hevc_tex` (NVENC HEVC), canvas 3840x2160 a 30 qps, NV12, Rec. 709. É uma gravação SDR.
- **O fmp** quer a mesma placa em 4K30 P010 (HDR).
- **A placa entrega um formato por vez.** Mesmo com um intermediário dividindo a imagem, OBS e fmp teriam de aceitar
  o mesmo formato. A Elgato 4K X só dá P010 em 4K30; o OBS hoje pede NV12 a 60.
- **O som não tem esse problema.** O áudio da Elgato passa pelo PipeWire, que aceita vários programas lendo a mesma
  fonte. O fmp e o OBS podem pegar o som juntos.
- Nesta máquina: OBS 33.0.3 (Flatpak); `v4l2loopback` 0.15.4 instalado e carregado (a câmera virtual do OBS); o
  PipeWire já publica a Elgato como fonte de vídeo (nó `Elgato 4K X (V4L2)`).

### Os caminhos

| Caminho | O que o OBS grava | Código | Prós | Contras |
|---|---|---|---|---|
| **A. OBS grava a tela da TV** (a fonte "Screen Capture (PipeWire)" já está na cena) | O que aparece na TV, com os quadros gerados | Nenhum | Funciona hoje | O Mutter copia cada quadro da TV para o OBS: mais trabalho justo na tela que precisa de ritmo (não medido). HDR pelo compartilhamento de tela do GNOME: incerto. Grava os quadros gerados, com artefatos, e não a fonte limpa. |
| **B. O fmp repassa a captura ao OBS** | A imagem limpa da placa | Grande (fork) | Um só dono da placa. O OBS recebe o mesmo quadro que o fmp, sem latência extra para o jogo. | O fmp publicaria cada quadro como fonte de vídeo no PipeWire. Os quadros já estão na memória da CPU antes de ir para a GPU, então é uma cópia de cerca de 25 MB por quadro (cerca de 750 MB/s). O OBS só grava com o fmp rodando, e o suporte a P010/HDR na fonte PipeWire do OBS é incerto. |
| **C. O OBS é dono da placa e o fmp lê a câmera virtual do OBS** | A imagem da placa | Médio (fmp lendo `v4l2loopback`) | O OBS fica como está | A câmera virtual é 8 bits: **perde o HDR**. Soma pelo menos um quadro de atraso ao jogo, e o fmp depende do OBS aberto. |
| **D. Segunda placa** (divisor HDMI, ou a saída de passagem da Elgato para outra placa) | A fonte limpa, em outra placa | Nenhum | Sem conflito de software, e cada um com o seu formato | Custo, outra porta USB rápida, e mais um aparelho no caminho do HDMI |

### Recomendação

Primeiro decida **o que o OBS deve gravar**:

- **O jogo limpo, como sai do PS5:** caminho D, se o custo couber; senão, B, que é o trabalho maior do fork.
- **O que aparece na TV, com geração de quadros:** caminho A. Antes de qualquer código, vale medir as perdas com o
  OBS capturando a tela da TV.

O caminho C só serve se o HDR não importar na hora de jogar.

## 4. Ordem sugerida

1. **Teste sem código (cerca de 15 min):** só a TV + Dell, HypeFrame com antecedência fixa de 5,5 ms, 60 s de
   captura em três situações: Dell parado; OBS aberto no Dell com prévia; OBS gravando a tela da TV (caminho A).
   Comparar slots perdidos e captura → tela.
2. **Seletor de tela e interface genérica** (pequeno): `--output` no fmp, "Tela do jogo" e "Outras telas: manter /
   desligar" no HypeFrame, perfil "Com segunda tela".
3. **Controle de antecedência** (item 2 da `PROPOSTA.md`): é o que mais pesa com duas telas.
4. **O caminho do OBS** escolhido na seção 3. Se for o B, é uma etapa própria e maior.

## 5. Decisão (22/11/2026): o OBS grava o jogo limpo

O Danilo escolheu gravar o jogo como sai do PS5, sem os quadros gerados. O caminho A sai da lista e o C também
(perde o HDR do jogo e soma atraso). A pesquisa seguinte trouxe três fatos que mudam os caminhos B e D:

- **No Linux, o OBS não captura HDR de placa.** A base de conhecimento do OBS diz que capturar HDR de dispositivo
  só funciona no Windows. O seu OBS grava a Elgato em NV12 (8 bits, Rec. 709) e vai continuar em SDR por qualquer
  caminho. Também não há confirmação de que a fonte "Video Capture Device (PipeWire)" aceite P010.
- **A Elgato 4K X tem saída HDMI de passagem**, com HDR10 e VRR, que funciona enquanto ela captura.
- **Quem estiver na saída de passagem decide o que o PS5 manda.** Em placas com passagem, o PS5 costuma ler o EDID
  do aparelho ligado na saída. Se esse aparelho não anunciar 4K HDR, o PS5 pode parar de mandar HDR também para o
  fmp. Não verificado na 4K X: é o primeiro teste antes de comprar qualquer coisa.

### A Elgato converte HDR para SDR sozinha (conferido em 22/11/2026)

O plugin de captura do OBS instalado aqui (`linux-v4l2.so`, OBS 33.0.3) só conhece formatos de 8 bits (NV12,
YUYV, UYVY, YU12) e não tem P010. Mesmo assim, com o PS5 em HDR, as gravações do OBS saem com cores normais. Medido
na gravação `2026-11-22 18-27-17.mkv` (HEVC Main, 8 bits, marcada como Rec. 709, faixa limitada):

| Instante | Y mín | Y médio | Y máx | Aparência |
|---|---|---|---|---|
| 15 min | 7–9 | 95 | 241–242 | normal |
| 60 min | 27–33 | 60 | 241 | normal (cena escura) |
| 110 min | 10–12 | 110 | 240–241 | normal |

O preto chega perto de 16 e o branco passa de 235, o uso normal da faixa SDR. Se o sinal HDR (PQ) tivesse vindo sem
conversão, o branco de referência ficaria perto de 145 e a imagem sairia lavada. Conclusão: **quando a captura é em 8
bits, a própria Elgato converte o HDR do PS5 para SDR**, e o resultado é bom. A questão das cores está resolvida. O
único problema que sobra é a placa atender um programa por vez.

O que isso muda nos caminhos abaixo:
- **D:** a segunda placa precisa fazer a mesma conversão HDR→SDR que a Elgato faz. Placa que não converte grava lavado.
- **E:** a vantagem de qualidade continua, porque é o único que guarda 10 bits e HDR, com a conversão para SDR feita
  no Resolve e sob seu controle. Mas não é mais o único jeito de ter cores boas: a conversão da Elgato já é boa.
  O E passa a se justificar mais por não precisar comprar nada e não brigar pela placa. Custo extra na edição: o
  arquivo HDR precisa de uma conversão para Rec. 709 no Resolve.

### Caminho A de volta à mesa: o OBS grava a saída do fmp

O Danilo propôs o caminho mais simples: o OBS grava a tela da TV (ou a janela do fmp) pela fonte "Captura de tela
(PipeWire)", e o fmp continua dono da placa. Não precisa de código. O que muda e o que falta saber:

- **Grava o que aparece na TV, não o jogo limpo:** entram os quadros gerados pelo VFG. A 30 qps no OBS, o canvas pega
  ora quadros reais, ora gerados, conforme a fase. Os artefatos do VFG Média são pequenos, mas existem.
- **Cores em HDR: incerto.** A janela do fmp é HDR10 (PQ) numa saída bt2100. Não achei documentação de como o
  Mutter 51 entrega isso ao compartilhamento de tela. No KDE houve gravações lavadas ou escuras com HDR ligado,
  depois corrigidas, e o suporte a gestão de cor HDR do GNOME ainda tem lacunas. Precisa testar.
- **Ritmo:** para cada quadro da TV, o Mutter faz uma cópia em 4K para o OBS. É trabalho a mais justo na saída cujo
  ritmo é apertado. Não medido.
- **Som:** usar o "Áudio do desktop" do OBS. O fmp já atrasa o som para casar com a imagem na TV, então ele chega
  sincronizado com a captura de tela. Capturar o som da placa direto sairia uns 90 ms adiantado.

**Teste proposto (5 min, sem código):** PS5 em HDR, só a TV com HDR, HypeFrame com o perfil "HDR fiel" (+ antecedência
fixa de 5,5 ms, se o Dell estiver ligado). No OBS, uma cena com "Captura de tela (PipeWire)" da TV e "Áudio do desktop".
Gravar 2 min. Depois conferir: (1) os quadros fora do ritmo no resumo do fmp, comparando com o mesmo teste sem o OBS
gravando; (2) as cores da gravação (Y mín/máx como acima, e os quadros a olho); (3) se a gravação sai lisa.

### Os caminhos que sobram

| Caminho | Como | O que o OBS recebe | Esforço | Riscos |
|---|---|---|---|---|
| **D. Segunda placa na saída de passagem** | PS5 → Elgato 4K X (fmp, HDR) → saída de passagem → segunda placa (OBS) | A fonte limpa, em SDR | Nenhum código, só compra | O EDID da segunda placa pode tirar o HDR ou os 4K do PS5. A segunda placa precisa converter HDR para SDR com cores certas (senão grava desbotado). É mais uma porta USB rápida. |
| **B. O fmp repassa a captura ao OBS** | O fmp continua dono da Elgato e publica cada quadro no PipeWire ou na câmera virtual (`v4l2loopback`) | A fonte limpa, mas o **fmp tem de converter HDR para SDR** antes, porque o OBS no Linux não usa P010 | Grande: publicação dos quadros, conversão HDR→SDR na GPU, cópia de volta para a CPU | O OBS só grava com o fmp rodando. Cada quadro passa a ter um trabalho a mais na GPU, no mesmo lugar em que o ritmo é apertado. |
| **E. O próprio fmp grava a captura** (novo) | O fmp grava os quadros capturados num arquivo (NVENC HEVC 10 bits, HDR10). O OBS fica só com webcam e cena, sem a Elgato. | Nada do jogo: o jogo vai para um arquivo à parte | Médio: o fmp já usa FFmpeg e NVENC está livre | Não tem a cena do OBS por cima do jogo: o jogo e a gravação do OBS se juntam na edição (Resolve). **É o único caminho que guarda o jogo em HDR.** |

### Recomendação

- **Se a gravação final é SDR e a cena do OBS importa** (webcam por cima do jogo, ao vivo): **D**. Antes de comprar,
  teste o que o PS5 passa a mandar com algo ligado na saída de passagem (até a própria TV serve de teste: o fmp tem
  de continuar recebendo 4K30 P010).
- **Se o jogo é editado depois de qualquer jeito** (como nos vídeos do canal, que passam pelo Resolve): **E**. Guarda o
  jogo limpo em HDR, não depende de hardware novo e não briga com o OBS pela placa. A webcam continua no OBS e os dois
  arquivos se alinham na edição.
- **B** fica por último: é o mais trabalhoso e entrega o mesmo SDR que o D.

## 6. Implementado (22/11/2026)

### Travamento do GNOME ao fechar o fmp: causa

As três sessões de jogo do dia pelo HypeFrame (11h36, 14h37 e 23h23) seguiram o mesmo padrão. Ao iniciar, o
HypeFrame desligava o Dell e passava a TV para VRR ("Só a TV" e "VRR" ligados pelo perfil), e o Mutter registrava
asserções de monitor lógico inexistente (`meta_monitor_manager_get_logical_monitor_from_number`,
`meta_display_get_monitor_geometry`). Ao fechar com Esc, o fmp saía limpo em 0,2 s (a placa liberada às
23:24:40,197; o processo morto às 23:24:40,421). No mesmo décimo de segundo, o HypeFrame religava o Dell e tirava a
TV do VRR, a ponto de derrubar até a sessão de captura de tela do OBS. O travamento vem dessa reconfiguração ao
fechar, não do fmp. As extensões ativas (Blur my Shell, Hot Edge, Magic Lamp) desenham por monitor e são suspeitas
comuns quando um monitor some e volta.

### O que mudou

- **fmp (fork): `--output <tela>`** em `play`, `capture`, `hdr-pattern` e `bench present`. Aceita o nome ou parte
  dele, o número da lista ou `@X,Y` (a posição no desktop). O resumo diz em que tela a tela cheia ficou. Testado: o
  Dell em 2160x3840 a 59,996 Hz e a TV em 3840x2160 a 119,879 Hz, cada um com a taxa da própria tela. Um nome
  inexistente lista as telas. O SDL só informa a nova tela de 0,3 a 1,8 s depois do pedido.
- **HypeFrame:**
  - "Tela do jogo": qualquer tela conectada, aberta pelo `--output @X,Y`. A posição é lida na hora, e o comando
    continua único mesmo com dois monitores iguais.
  - "Outras telas durante o jogo": uma chave por tela conectada, para qualquer número de telas. Por padrão ficam
    todas ligadas.
  - Os perfis não mexem mais nas outras telas.
  - O VRR vem desligado por padrão: o VRR do Mutter não seguiu a janela, e ligá-lo só força uma troca de modo.
  - A configuração antiga é migrada uma vez: outras telas ligadas, VRR desligado.
  - Com outras telas ligadas, o aviso sugere antecedência fixa de 5,5 ms.
  - Quando há telas a devolver, a devolução espera 1,5 s depois que o fmp sai.
  - Com a configuração atual do Danilo (HDR fiel, Dell ligado), o HypeFrame não reconfigura nenhuma tela, nem ao
    iniciar nem ao parar.

### Último quadro preso na TV depois do Esc (23/11/2026): segunda causa

Mesmo sem reconfigurar telas, depois do Esc o último quadro do jogo ficava na TV (o Alt+Tab não tirava; trocar de área
de trabalho tirava). O fmp saía limpo (`exec_died` 22 s depois do início, sem erro no GNOME). A diferença estava no
encerramento: o `hdr-pattern` minimiza e o `bench present` sai da tela cheia antes de destruir o swapchain, mas
`play`/`capture` destruíam swapchain e janela ainda em tela cheia. Correção no fmp: `SdlWindow.HideForTeardown` esconde a
janela no começo do `Teardown` (com as imagens ainda vivas), espera o compositor 150 ms e só então libera o resto. O
Esc, o fim do `--duration-s` e o Parar passam por esse caminho. Teste: captura 4k30-hdr de 6 s na TV com `--output`,
`teardown clean`, PASS, e o Danilo viu a TV voltar à área de trabalho sem quadro preso. Confirmado pelo Danilo no HypeFrame com Esc (23/11/2026).

### Cadência na TV com o Dell e o OBS abertos (22/11/2026, 23h40)

`bench present --test cadence --fullscreen`, 5 s, 600 intervalos: de 337 a 463 fora da grade, com e sem `--output`
(p1 de 5,1 a 5,9 ms, p50 de 8,4 ms, p99 de 10,8 a 11,6 ms). Nenhum chegou a dois refreshes, ou seja, não houve quadro
perdido. É pior que os 267 de 1196 do dia 21 (Dell aberto, sem o OBS), e o `--output` não muda nada. A antecedência
fixa continua sendo a proteção.

## Fontes

- SDL3, `SDL_SetWindowPosition`: https://wiki.libsdl.org/SDL3/SDL_SetWindowPosition
- SDL3 no Wayland: https://wiki.libsdl.org/SDL3/README-wayland
- SDL, "wayland: Fall-back to the compositor for fullscreen-desktop window placement" (2025):
  https://discourse.libsdl.org/t/sdl-wayland-fall-back-to-the-compositor-for-fullscreen-desktop-window-placement/60447
- Kernel, V4L2 "Opening and Closing Devices": https://kernel.org/doc/html/v6.1/userspace-api/media/v4l/open.html
- GNOME, câmeras compartilhadas pelo PipeWire: https://blogs.gnome.org/uraeus/?p=10037
- Mutter, várias telas na NVIDIA: https://gitlab.gnome.org/GNOME/mutter/-/issues/3461 ,
  https://gitlab.gnome.org/GNOME/mutter/-/issues/2948 , https://bugs.launchpad.net/bugs/1970291
- Mutter, relógio de quadros por tela: https://blogs.gnome.org/shell-dev/?p=1014
- OBS, placas com HDR (HDR de dispositivo só no Windows): https://obsproject.com/kb/video-capture-devices-with-hdr-support
- OBS, cores de alta precisão e HDR: https://github.com/obsproject/obs-studio/wiki/High-precision-color-spaces-(including-HDR)
- Fonte PipeWire do OBS e formatos aceitos (relato de terceiro): https://github.com/madhasoeki/CamCraft/pull/16
- Elgato 4K X, passagem HDMI 2.1 com HDR10 e VRR: https://help.elgato.com/hc/en-us/articles/360052398731-Elgato-and-HDMI-2-1 ,
  https://www.elgato.com/us/en/explorer/products/capture/what-is-the-difference-between-elgato-game-capture-4k-x-and-4k-pro/
- Medições: `PROPOSTA.md` e os `.txt` desta pasta.
