# Latência de entrada: onde está e como reduzir

Investigação de 23/11/2026. Mesma máquina dos outros relatórios desta pasta:
- Bazzite com GNOME 51.1 e RTX 4080 Super (driver aberto 620.70);
- Elgato 4K X em USB de 10 Gbps;
- TV LG a 119,88 Hz em HDR (bt2100), com o Dell 4K60 em retrato ligado;
- PS5 rodando um jogo a 30 fps (o detector de cadência do fmp vê cada quadro do jogo duas vezes no sinal de 59,94 Hz).

O fmp mede da **marca de tempo da captura** (o instante em que o driver da placa vê o começo do quadro) até o **primeiro pixel** que a placa de vídeo manda à TV. Fora dessa medição ficam três partes:
- o jogo e o PS5, do botão até o quadro sair pelo HDMI;
- a Elgato, do HDMI até o começo do quadro no USB;
- a TV, do sinal até a luz. Em 120 Hz, só o desenho até o meio da tela leva mais 4 ms.

Para medir essas três partes seria preciso um sensor de luz ou uma câmera rápida.

**Objetivo, definido pelo Danilo:** jogar jogos de 30 fps a 60 ou mais, **sempre com a geração de quadros ligada**. Opção que desliga a geração não entra na conta: está no fim da seção 4, como descartada.

## Resumo

- **Linha de base atual: 89,6 ms** (p99 98,8 ms) da captura até a TV. Configuração: o HypeFrame de 23/11 com o VFG em Média, o Dell ligado e a antecedência partindo de 5,5 ms.
  Arquivo: `latencia/2026-11-23_tv_4k30-hdr_vfg-media_dell-ligado.txt`.
- **O VFG em Alta não funciona em 4K30.** A Alta gastou 12,7 ms por quadro gerado (p99 17 ms). O limite para caber é T/M = 16,7 ms, então o fmp desliga a geração: só 89 quadros gerados em 30 s, contra 840 na Média, e 385 quadros fora do ritmo. Mesmo se coubesse, somaria cerca de 15 ms de espera pela geração. Em 4K30, use a Média (seção 3.H). **Em 23/11 o HypeFrame estava em Alta.**
- **Sem perder nada da imagem e com a geração ligada, dá para tirar uns 30 ms, de 89,6 para cerca de 60:**
  - PCIe preso em Gen1: −13 ms. **Causa achada:** a porta PCIe do processador está com teto em Gen1 (`Target Link Speed: 2.5GT/s`). O kernel tem um controle oficial para subir esse teto, e falta testar (seção 3.A);
  - controle de antecedência: −10 ms;
  - alinhamento à grade: −4 a −5 ms;
  - margens do escalonador: −1 a −2 ms.
- **Fora do PC**, o modo de jogo da TV pode valer tanto quanto tudo isso junto (seção 6). O driver da NVIDIA para Linux não manda o aviso de jogo (ALLM), então a TV fica no modo que estiver escolhido. Na LG, fora do modo jogo, isso dá cerca de 82 ms; no modo jogo, 6 a 11 ms.
- **O GNOME compõe a janela do fmp** (medido: sem envio direto à tela, nem em HDR). O VRR do GNOME esbarra num bug aberto com a NVIDIA. Dentro do GNOME, o que imita o VRR são a prioridade do ritmo (já no HypeFrame) e o modo "grade exata" (seção 3). Trocar de desktop fica fora do cenário (seção 7).
- **Com a geração ligada, o maior pedaço que sobra é a placa de captura:** 33,2 ms de USB e, escondida da medição, a "loteria" de 0 ou 16,7 ms do 4K30. Isso só sai capturando a 60 fps. A geração continua 30 → 60, porque o detector de cadência descarta as cópias de cada quadro do jogo (medido):
  - 1440p60 HDR: −25 ms medidos hoje (cerca de −17 com o PCIe já consertado), mais cerca de 8 ms, em média, da loteria;
  - 4K60 SDR: parecido, mas sem HDR na TV.
  Nenhuma placa PCIe com HDR de 10 bits tem suporte verificado no Linux (seção 6).
- **A espera da interpolação (16,7 ms) é o preço da geração** e fica. O quadro gerado já mostra metade do movimento novo 16,7 ms antes do quadro real. Por isso o primeiro sinal de uma reação aparece por volta de 73 ms (seção 3.H).
- **Feito em 23/11:**
  - o fmp ganhou `--lead-max-ms` e `--lead-adapt`, para segurar a antecedência;
  - o HypeFrame ganhou a aba "Geração de quadros", com o atraso estimado e de onde ele vem, avisos e o resultado medido de cada sessão num histórico;
  - na aba Sistema, ganhou o botão que libera o PCIe.
  
  Os testes com a TV ficam para quando as telas estiverem ligadas (seção 9).

## 1. De onde vêm os 89,6 ms

Componentes do modelo do fmp na rodada de base. Somam cerca de 90 ms; o fmp mediu 89,6 ms na mediana.

| Etapa | ms | O que é | Dá para reduzir? |
|---|---|---|---|
| USB (placa → PC) | 33,2 | A Elgato manda cada quadro espalhado pelo período inteiro (1/29,97 s). A marca de tempo é do começo e o quadro só fica completo no fim. A 60 fps, medido 16,6 ms | Só capturando a 60 fps (troca resolução ou HDR) |
| Cópia para a GPU | 14,2 | 25 MB por quadro a cerca de 1,75 GB/s. **O PCIe está preso em Gen1** (seção 3.A) | **Sim, sem custo: cerca de 1 ms em Gen4** |
| Cadência, conversão e folga | 2,7 | Detecção de quadro repetido (0,16), conversão para RGB (0,24) e a chegada planejada pelo p99, não pela mediana | Pouco (seção 3.E) |
| Espera da interpolação | 16,7 | O quadro gerado entre N e N+1 só existe depois que N+1 chega: (M−1)/M · T = T/2 | Não, com a geração ligada: é o preço da interpolação (seção 3.H) |
| Tempo de geração | 2,5 | p99 do VFG Média (2,0) + margem (0,5) | Não vale a pena (a Baixa ganha menos de 1 ms) |
| Alinhamento à grade | 4,8 | Em taxa fixa, o quadro real precisa cair num refresh: em média meio refresh (8,34 ms / 2) | Sim: VRR de verdade ou o modo "grade exata" (seção 3.C) |
| Antecedência | 16,2 | Quanto antes do refresh o quadro é entregue ao GNOME. **Começou em 5,5 e subiu sozinha** | **Sim, sem custo de imagem (seção 3.B)** |
| Arredondamento | 0,1 | | — |

## 2. Linha de base e como comparar

- `latencia/linha-de-base.csv` tem as 13 rodadas de 22 e 23/11, com condições, captura → tela p50/p99, cada componente, antecedência inicial e final, quadros perdidos e veredito.
- Os registros completos estão em `latencia/` (23/11) e nesta pasta (22/11).
- `latencia/extrair.py` tira as mesmas colunas de qualquer saída do fmp:

```bash
python3 docs/gnome-mutter/latencia/extrair.py saida-nova.txt >> docs/gnome-mutter/latencia/linha-de-base.csv
```

Para comparar com a base, repita as condições:
- jogo a 30 fps, TV em HDR e o Dell ligado;
- 4k30-hdr com VFG Média, `--present-lead-ms 5.5` e 30 s.

No HypeFrame, ligue "Parar depois de (s)" com 30 em Sistema, rode e use o botão de copiar a saída. O comando equivalente é:

```bash
distrobox enter -n fmp -T -- env FMP_VFX_SDK=/run/host/var/usrlocal/VideoFX \
  ~/Projetos/frame-motion-plus/artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp capture --no-settings \
  --mode 4k30-hdr --quality medium --multiplier 2 --quality-switch --scd off --fullscreen --output @2160,403 \
  --clock auto --present-lead-ms 5.5 --late-generated-ms 4 --ceiling-pacing on --hdr on --hdr-peak-nits 4000 \
  --range limited --matrix bt2020 --buffers 4 --audio off --duration-s 30
```

Compare principalmente:
- captura → tela p50 e p99;
- quadros perdidos, em % dos slots;
- a cópia para a GPU;
- a antecedência final;
- o alinhamento.

A latência varia pouco entre cenas, porque depende do ritmo e não do conteúdo. As perdas variam com o que mais está na tela, como o OBS no Dell.

## 3. Opções sem perder qualidade de imagem (da maior para a menor)

### A. PCIe preso em Gen1 → Gen4: cerca de −13 ms

- **Evidência (23/11):**
  - `current_link_speed` ficou em 2,5 GT/s (Gen1) em 260 de 260 amostras durante uma captura;
  - ficou em Gen1 também em 552 de 552 amostras durante o benchmark do VFG, com a GPU a 100%;
  - a placa suporta 16 GT/s (Gen4) e a porta PCIe do processador, 32 GT/s;
  - zero reenvios no link e nenhum erro de AER no kernel;
  - arquivos: `latencia/2026-11-23_pcie-*.csv`.
- **O normal:** a NVIDIA baixa o link para Gen1 em repouso e sobe sob carga. Aqui ele nunca sobe.
- **Causa achada (23/11, `latencia/2026-11-23_pcie-alvo-gen1.txt`):** a porta PCIe do processador (00:01.0) está com `Target Link Speed: 2.5GT/s`. É um teto na própria porta: a placa pode pedir Gen4 sob carga, mas a porta não passa de Gen1.
  - O SSD do sistema (Lexar NM620, na outra porta do processador, 00:06.0) também está em 2,5 GT/s, num link de 8 GT/s; o SSD do chipset (Kingston, SEC_DISK) está em 16 GT/s. O problema pega as duas portas do processador, não só a placa de vídeo.
  - Não foi o recuo automático do kernel ("retraining non-functional downstream link at 2.5GT/s"): nenhum registro neste boot. Não houve suspensão, e as portas nunca entraram em economia de energia. O thermald só usa resfriadores da CPU.
  - Para o driver da NVIDIA, o máximo do computador é Gen5 ("Host Max 5"), e o da placa, Gen4.
  - **Quem baixou o teto não está provado:** a BIOS (F19, de 09/2024) ou algo no Linux. Há relatos iguais com placas Gigabyte B760 e NVIDIA:
    - uma B760M DS3H AX DDR4 com RTX 3060 Ti ficava em 2,5 GT/s mesmo com "CPU PCIe Link Speed" em Gen4 na BIOS. Num pendrive com Ubuntu dava Gen4, e um kernel mais novo resolveu;
    - uma B760M Gaming X AX com Bazzite e RTX 5070 Ti (issue #1010 do driver aberto, ainda aberta), em que mudar a BIOS não resolveu.
- **O que se ganha:** a cópia de 25 MB cai de 14,2 ms para cerca de 1 ms. Também acelera o resto do sistema: exportações no Resolve, OBS, jogos no PC e o SSD do sistema, hoje limitado a cerca de 1 GB/s.
- **Como resolver** (cada passo pede a senha; é o teste nº 1 de amanhã, seção 9):
  1. **Pelo HypeFrame:** em Sistema, "Liberar a velocidade máxima do PCIe". Ele usa o controle oficial do kernel para a porta da placa de vídeo (o "controlador de banda" do PCIe, `PCIe_Port_Link_Speed_*`, em que o estado 0 é a velocidade máxima). O kernel escolhe a maior velocidade que os dois lados aceitam (Gen4 aqui) e retreina o link. O HypeFrame mostra o teto antes e depois. Pelo terminal, o mesmo é:

     ```bash
     for d in /sys/class/thermal/cooling_device*; do [ "$(cat $d/type)" = "PCIe_Port_Link_Speed_0000:00:01.0" ] && echo 0 | sudo tee $d/cur_state; done
     ```

     Feche o fmp e o OBS e salve o que estiver aberto: a imagem pode piscar.
  2. **Conferir:** o teto deve virar 16GT/s. Depois rode um jogo de 30 s: na "Última sessão", a cópia para a GPU deve cair de 14 para cerca de 1 ms, e o PCIe deve chegar a Gen4.

     ```bash
     sudo lspci -vv -s 00:01.0 | grep -E "LnkSta:|LnkCtl2"
     ```

  3. **Se der certo e o teto continuar em 16GT/s** (também depois de abrir e fechar o fmp), deixar fixo a cada boot com uma regra do udev. Isso fica para depois do teste, porque roda no boot, antes de dar para conferir. Se o teto voltar sozinho para 2,5, alguma coisa no Linux está baixando, e o próximo passo é descobrir em que momento.
  4. **Se o comando não subir o link:** na BIOS, conferir "CPU PCIe Link Speed" (o nome no modelo-irmão) e deixar em Gen4. Vale ver também se há BIOS mais nova que a F19.
  - Não mexa na porta do SSD do sistema (00:06.0) com o sistema ligado: se o retreino falhar, o disco some. Se a BIOS for a causa, o ajuste dela resolve os dois.
- **Trocas:** nenhuma na imagem. Um pouco mais de consumo em repouso, se o link deixar de baixar.
  Risco: se o slot não sustentar Gen4, aparecem reenvios (`nvidia-smi -q`, "Replays"). Aí se fixa em Gen3, que já dá cerca de 12 GB/s, ou cerca de 2 ms por quadro.

### B. Controle de antecedência que não sobe por soluço: −8 a −11 ms

- **Evidência:**
  - na base, a antecedência partiu de 5,5 ms e subiu sozinha: 6,5 → 7,5 → 10,3 → 13,2 → 16,0 → 16,2 ms;
  - em 22/11, com `--present-lead-ms 5.5`, ela foi até 14,1 ms, e a captura deu 79,7 ms, contra 90,3 ms da rodada sem a opção;
  - o GNOME precisa de cerca de 5 a 6 ms antes do refresh (o "latch" medido), e o resto é margem que o controlador acumula.
- **Feito em 23/11 (fmp e HypeFrame):**
  - `--lead-max-ms <ms>`: teto para a antecedência que se adapta. O limite de antes era fixo em cerca de 2 refreshes (16,2 ms a 119,88 Hz), e foi até onde ela subiu na base;
  - `--lead-adapt off`: antecedência fixa no valor de `--present-lead-ms`;
  - o resumo do fmp diz qual valeu ("at most 8.000 ms" ou "fixed: --lead-adapt off");
  - no HypeFrame, isso virou a escolha "Prioridade do ritmo": **Menor atraso** (fixa em 5,5 ms: −10,7 ms na conta), **Equilíbrio** (teto de 8 ms: −8,2 ms) e **Mais liso** (como era);
  - testes: os 720 testes do Core e os 114 do Pipeline que não precisam de hardware passaram, incluindo dois novos (o teto e as opções).
- **Ainda não medido:** quantos quadros a mais saem fora do ritmo com o teto. Fica para os testes de amanhã (seção 9).
- **O que ainda dá para mudar no fork** (item 2 da `PROPOSTA.md`):
  - subir só com vários atrasos numa janela curta, não no primeiro;
  - não saltar para "entrega + 2 ms" por um atraso isolado;
  - fazer o piso durar 60 s em vez de 10 min.
- **Trocas:** com o Dell ligado, o GNOME tem soluços. Uma antecedência menor pode deixar passar um pouco mais de quadros fora do ritmo. A meta é a menor antecedência com perdas abaixo de 2%.

### C. Alinhamento à grade (modo "grade exata" ou VRR): −4 a −5 ms

- **O problema:** em taxa fixa, o fmp atrasa cada quadro real até cair exatamente num refresh. Em média, isso é meio refresh: 4,1 a 6,7 ms medidos.
- **Evidência:** com a TV em SDR, o relógio "vrr" (entregar na hora e deixar a fila do Vulkan pôr no próximo refresh) deu **65,3 ms contra 77,1 ms** do relógio fixo. Ele funcionou porque 59,94 cai exato na grade de 119,88. A diferença inclui a antecedência (12,5 ms naquela rodada) e o alinhamento.
- **O que mudar:** quando a taxa da tela for múltipla exata da saída, apresentar como no relógio vrr, mesmo sem VRR. Antes de escrever código, falta testar em HDR com antecedência fixa: em 22/11, o relógio vrr em HDR ficou irregular.
- **Trocas:** se o GNOME atrasar uma entrega, aquele quadro fica um refresh a mais na tela.

### D. VRR de verdade (seção 7)

Com VRR, o fmp entrega cada quadro na hora. Isso elimina o alinhamento e quase toda a antecedência: sobram 2 a 4 ms da entrega ao primeiro pixel. Seria a soma de B e C. Hoje o VRR do GNOME não segue a janela do fmp (medido em 22/11).

### E. Margens do escalonador: −1 a −2 ms

- O fmp planeja a chegada dos quadros pelo p99 e soma 0,5 ms de margem.
- Baixar para o p95 tira cerca de 2 ms na base, porque a cópia oscila entre 14,2 e 16,6 ms.
- Com o PCIe em Gen4, essa oscilação some, e o ganho também.
- Ajustável sem código: um arquivo `--settings` com a seção `scheduler` (`GenerationTimePercentile`, `MarginMs`).
- **Trocas:** chegadas mais lentas que o percentil viram quadro real atrasado (um soluço).

### F. A "loteria" do 4K30 (escondida da medição)

- O jogo a 30 fps aparece duas vezes seguidas no sinal de 59,94 Hz.
- A Elgato, a 4K30, pega um quadro do sinal sim, outro não. Se cair na segunda cópia, a imagem já tem 16,7 ms quando recebe a marca de tempo, e o fmp não tem como saber.
- A fase muda a cada soluço do jogo, então o custo médio esperado é de cerca de 8 ms.
- A 4K30 não há como escolher a cópia. Capturar a 60 fps resolve, porque o detector de cadência pega sempre a primeira (seção 4).

### G. Coisas pequenas, sem custo

- Desligar a detecção de repetição: −0,16 ms, mas os quadros repetidos do jogo viram "novos" e a interpolação gagueja. Não vale.
- `--cuda-sync spin`: menos de 0,1 ms, gastando CPU.
- A thread de captura já acorda em menos de 0,1 ms (p99 de 78 µs medido em 21/11).

### H. Geração sempre ligada: o que ela custa e o que dá para escolher

- **A espera da interpolação fica.** O quadro gerado entre N e N+1 só pode ser feito depois que N+1 chega, e N+1 só entra na tela depois dele. Isso custa (M−1)/M · T: 16,7 ms em 30 → 60.
  - **O que você sente é um pouco menos:** o gerado entra 16,7 ms antes de N+1 e já mostra metade do movimento novo. O primeiro sinal de uma reação (o personagem começar a pular, por exemplo) aparece por volta de 73 ms na base; o quadro completo, em 89,6.
  - Só a extrapolação, que prevê o quadro seguinte só com os anteriores, tiraria essa espera. Mas o VFG só interpola, e prever erra justamente nas mudanças bruscas. Fica como ideia de pesquisa, não como opção.
- **Multiplicador:** 2 (30 → 60) é o de menor atraso. 4 (30 → 120) espera 3/4 · T = 25 ms, ou seja, +8,3 ms, em troca de mais fluidez. O VFG Média cabe nos dois (cerca de 2 ms por quadro gerado).
- **Qualidade do VFG:** Média em 4K. Além de não caber em 4K30 (p99 de 17 ms para um limite de 16,7), a Alta somaria cerca de 15 ms de espera pela geração se coubesse. Em 1440p ela deve caber, mas não foi medida.
- **O fmp desliga a geração quando a qualidade não cabe:** baixa o multiplicador até 1 (`LatencyModel.MaxMultiplierThatFits`) e tenta de novo de tempos em tempos. O `--quality-switch` só carrega a outra qualidade para a tecla Q, sem troca automática.
  - **No HypeFrame (feito em 23/11):** a aba "Geração de quadros" avisa antes de começar e oferece "Usar a Média e iniciar". Depois de cada sessão, mostra que parte do tempo a geração ficou ligada (a rodada com a Alta: 10,6%).
  - **Ainda possível no fork:** com as duas qualidades carregadas, trocar sozinho para a Média em vez de desligar a geração.

## 4. Opções com troca (resolução, HDR ou fluidez)

| Opção | Ganho | Troca | Evidência |
|---|---|---|---|
| **1440p60 HDR** (perfil "Menor atraso com HDR" no HypeFrame) | **−25 ms medidos hoje** (cerca de −17 com o PCIe em Gen4, porque a cópia já fica pequena), e mais cerca de 8 ms em média por acabar a loteria do item F | Imagem em 1440p ampliada para 4K na TV | Sem janela, 23/11: 49,9 ms contra 75,5 ms do 4K30 (USB 16,6 ms, cópia 6,4 ms), **com a geração ligada**: 300 gerados para 300 reais, e o detector de cadência descartou 368 cópias |
| 4K60 SDR | parecido com o 1440p60, e acaba a loteria | Sem HDR na TV. A conversão da Elgato é boa (vista nas gravações do OBS) | Sem janela: USB 16,5 ms, cópia 7,2 ms, com a geração ligada |
| 30 → 120 (multiplicador 4) | **+8,3 ms** (espera de 25 em vez de 16,7) | Mais fluido | Modelo do fmp |
| Saída SDR com relógio VRR | 65 ms medido (contra 77 em SDR fixo) | Sem HDR | 22/11 |
| Mostrar o gerado mais perto do quadro seguinte (t = 0,75) | cerca de −8 ms | Movimento irregular (intervalos de 25 e 8 ms). **Não recomendado** | Teoria |
| VFG Baixa | menos de 1 ms | Mais artefatos nos gerados. Não vale | — |
| Desligar as outras telas | Menos perdas. Na latência, só se permitir antecedência menor | Sem o OBS no Dell | 22/11 |
| PS5 a 120 Hz | **piora**: a 4K30 a placa pegaria um de 4 quadros repetidos (até 25 ms de quadro velho) | Deixe a saída do PS5 em 60 Hz enquanto captura em 4K30 | Teoria |

**Descartadas, porque desligam a geração** (o objetivo é jogar 30 fps a 60 ou mais): desligar a geração (−19 ms, mas fica em 30 fps), uma tecla para ligar e desligar durante o jogo e jogar pela saída de passagem da Elgato.

## 5. Já conferido e sem ganho

- **Perfil de energia:** `throughput-performance-bazzite`, CPU em "performance".
- **Kernel:** PREEMPT_DYNAMIC.
- **USB:** a Elgato está a 10 Gbps num controlador USB 3.2 de 20 Gbps.
- **Buffers de captura:** o mais novo sempre vence, então mais buffers não atrasam.

## 6. Fora do PC (TV, PS5, controle)

### TV LG: provavelmente o maior ganho de todos, e não aparece na medição do fmp

- **O driver da NVIDIA para Linux não manda ALLM nem o tipo de conteúdo "Jogo"** pelo HDMI. Há uma pergunta sem resposta no fórum da NVIDIA de agosto de 2026, com o driver 610. No Linux, só o amdgpu ganhou ALLM, no kernel 7.4.
- O EDID da TV anuncia ALLM, o tipo de conteúdo "Game" e VRR de 40 a 120 Hz (lido em 23/11). Mas, sem o aviso, a TV fica no modo de imagem que estiver escolhido.
- **Números do RTINGS para a LG C2** (lidos por trechos da busca):

  | Modo | Atraso |
  |---|---|
  | Jogo, 4K120 | 5,9 ms |
  | Jogo, 4K60 | 10,6 ms (o mesmo em HDR de 10 bits) |
  | Fora do modo jogo, 4K60 | 82,4 ms |

  A C3 e a C4 ficam parecidas: 13 ms a 60 Hz e 4,8 ms a 120 Hz.
- **O que fazer, sem custo de imagem relevante:**
  - Na entrada HDMI do PC, use o modo **Game Optimizer** (Otimizador de jogos) **ou** o ícone da entrada como **"PC"**. Segundo o RTINGS, com o ícone PC não é preciso o modo jogo para ter atraso baixo.
  - Confira qual está ativo hoje. Se a TV estiver num modo comum, você ganha dezenas de ms com isso sozinho.
- **"Boost" / "Prevent Input Delay":** só tira 2 a 3 ms, e só a 60 Hz. A 119,88 Hz não muda nada.
- **HGIG ou mapeamento de tons:** sem dado de atraso. Em modo jogo, o HDR não somou atraso na C2.
- **VRR ligado na TV:** não aumenta o atraso (RTINGS).

### PS5

- **Modo desempenho (60 fps) em vez de fidelidade (30 fps), quando o jogo tiver.**
  - O próprio jogo fica muito mais rápido. Digital Foundry, Ratchet & Clank com uma LG CX em modo jogo: 117,5 ms a 30 fps/60 Hz, 75 ms a 60 fps/60 Hz e 60,8 ms a 60 fps/120 Hz (lido por um espelho; confira no original).
  - Com o jogo a 60 fps, a geração vai de 60 para 120 (no HypeFrame: "Taxa do jogo" 60 e "120 fps"). Aí vale capturar a 60 fps (1440p60 HDR ou 4K60 SDR), e a espera da interpolação cai para 8,3 ms.
  - **Troca:** é escolha do jogo, e o modo desempenho costuma ter resolução ou efeitos menores.
- **HDR em "Ligado quando suportado"** (em vez de "Sempre ligado"): jogos em SDR saem em SDR, e aí cabe o 4K60 NV12 sem perder nada: USB de 16,6 ms e sem a loteria, com a geração ligada.
- **Saída de 120 Hz em "Automático":** num jogo de 30 fps o PS5 já manda 60 Hz. Não force 120 Hz com a captura em 4K30: a placa pegaria um de quatro quadros repetidos.
- **DualSense:** com cabo ou sem fio, a diferença no PS5 foi de 1 a 2 ms (teste de 2021). Não vale o cabo por latência.

### Placa de captura

- **A Elgato declara cerca de 35 ms** ("dois quadros a 60 fps") no Windows. Uma medição independente deu 36 ms até a prévia do OBS. Não achei medição no Linux.
- **O que a medição do fmp mostra:**
  - a placa espalha o envio de cada quadro pelo período inteiro: 33,17 ms a 30 fps e 16,57 ms a 60 fps;
  - o quadro N chega 0,2 ms **antes** de o quadro N+1 começar.

  Se o driver só fechasse o quadro ao ver o começo do seguinte, a entrega viria logo **depois**, com uns 12 ms perdidos no 4K30. Então a placa marca o fim do quadro, e os 33 ms são dela, não do driver.
- **A transferência de vídeo é em bloco (bulk).** Nenhum parâmetro do `uvcvideo` encurta a entrega. A economia de energia do USB não age durante a captura.
- **4K60 HDR é impossível nesta placa:** P010 4K60 precisa de cerca de 11,9 Gbit/s e o USB dela é de 10.
- **Trocar de placa, pesquisado em 23/11 (só Linux):** com a geração ligada, a placa é o maior pedaço que sobra (33 ms de USB e a loteria). Uma placa PCIe que capture 4K60 HDR tiraria os dois sem perder imagem. Mas hoje nenhuma serve:
  - a **Elgato 4K60 Pro MK.2 e a 4K Pro** só têm no Linux um driver da comunidade (`sc0710`). Ele entrega 4K60, mas o HDR sai só em 8 bits (BGR24 com PQ; o P010 não foi feito), o que dá degraus nos gradientes. Há rasgos na imagem a 4K60 sob carga, ainda em investigação, e a 4K Pro precisa carregar um firmware a cada boot a frio. O autor só testa a MK.2;
  - a **Magewell** tem driver oficial para Linux e um modo de baixa latência (mediu 32 → 17,7 ms a 1080p60), mas não confirmei 4K60 HDR de 10 bits, e o preço é de equipamento profissional.
- **Jogar pela saída de passagem da Elgato:** quem testou não viu atraso a mais. Mas assim você joga sem a geração de quadros, o que anula o propósito.

## 7. Compositor (GNOME) e alternativas

### Medido em 23/11: o GNOME compõe a janela do fmp (sem envio direto à tela)

- Com `WAYLAND_DEBUG=client`, todos os quadros apresentados na TV voltaram com as flags 7 (vsync, relógio de hardware, conclusão no hardware), **sem o 0x8 (zero-copy)**:
  - 345 de 345 com o swapchain HDR10;
  - 39 de 39 com o SDR.
- O WSI da NVIDIA descreve a cor certa pelo protocolo de cor do Wayland: BT.2020 + PQ e MaxCLL no HDR, sRGB no SDR. Ele usa `wp_fifo_v1` e `wp_commit_timing_v1`.
- Arquivo: `latencia/2026-11-23_envio-direto-wayland.txt`.
- **Consequência:** o GNOME pinta a janela a cada refresh. Daí vem o "latch" de cerca de 5 ms e a sensibilidade ao segundo monitor, já que todos os monitores são pintados na mesma thread.

**Por que pode não haver envio direto (pesquisa no código do Mutter 51.1):**
- O Mutter recusa quando precisaria converter a cor da janela para a da saída. O HDR10 padrão deve passar com o brilho HDR em 100%; aqui está em 100 (`gdctl`, "Backlight").
- Recusa formatos ou modifiers que o plano primário não aceita. Há uma issue aberta (#4687) com buffers da NVIDIA de layout comprimido sendo recusados.
- Recusa também quando há outra superfície visível por cima (cursor por software, notificação).

O motivo exato só aparece no log de depuração. Requer sair e entrar de novo na sessão:

```bash
mkdir -p ~/.config/environment.d && echo 'MUTTER_DEBUG=render,kms' > ~/.config/environment.d/90-mutter-debug.conf
```

Depois de entrar de novo, rode uma captura e veja o log:

```bash
journalctl --user -b --grep 'scanout|VRR|candidate'
```

- `Assigning scanout` indica envio direto.
- `No direct scanout candidate: …` dá o motivo.

Apague o arquivo depois, porque o log fica grande.

**Se o envio direto funcionar:** o latch deve cair (o KWin do autor do fmp mediu 1,3 ms), e o segundo monitor passa a pesar menos. É uma estimativa, porque o piso do Mutter continua acima do do KWin.

### VRR no GNOME: bloqueado por bug

- O Mutter 51 liga o VRR quando o modo da tela é "+vrr" **e** há uma janela em tela cheia "elegível". Elegível quer dizer cobrindo exatamente a tela, sem nada por cima e sem animação.
- **Bug aberto #4559** (NVIDIA): jogos Wayland nativos com vsync não sincronizam o VRR no GNOME, enquanto no KDE funciona. É o caso do fmp, e bate com os 30 s de "variable refresh lost" medidos em 22/11.
- **Bug aberto #5225:** com o modo "+vrr", o GNOME informa refresh = 0 ao app mesmo com o VRR inativo. A sonda de VRR do fmp não depende disso, mas convém lembrar.
- **Qualquer notificação, menu, aviso de volume ou janela modal desliga o VRR e o envio direto em todos os monitores** enquanto estiver na tela. Uma janela do outro monitor encostada na borda da TV também (#4815).
- Desligar e religar o VRR numa TV HDMI pode apagar a imagem por cerca de 1 s. Isso combina com a queda do HDMI de 22/11.

**Conclusão:** no GNOME 51 com NVIDIA, o VRR não é caminho por enquanto. Ficam as opções B (já no HypeFrame) e C da seção 3, que imitam o VRR em taxa fixa.

### Outras coisas do GNOME

- Não há ajuste do usuário para o latch: o `org.gnome.Mutter.DebugControl` não tem nada disso.
- `CLUTTER_PAINT=disable-triple-buffering` só age quando o GNOME não dá conta de compor a tempo.
- A prioridade da thread do KMS já é alta desde o GNOME 48.
- **Com dois monitores:**
  - deixe o Dell sem nada mudando na tela durante o jogo (por exemplo, a prévia do OBS desligada);
  - teste sem a rotação de 90°;
  - o GNOME 51.2 corrige o #5140, que desconecta clientes do `wp_commit_timing` ("Commit already has timestamp").

### Alternativas ao GNOME (fora do cenário)

O cenário é este: RTX 4080 Super, Linux com GNOME, sem Windows. As alternativas abaixo ficam só como referência e não entram na ordem sugerida (seção 8).

| Opção | Latência | Troca | Situação |
|---|---|---|---|
| **KDE Plasma (KWin 6.8)**, rebase para a imagem KDE do Bazzite | O KWin entrega cerca de 1 ms antes do refresh (1,3 ms medido pelo autor do fmp) e tem VRR "automático" para a janela em tela cheia, com HDR e envio direto. Estimativa: antecedência de 16,2 para cerca de 2 ms e fim do alinhamento, **cerca de −19 ms** sobre a base. Não soma com as opções B e C, que resolvem o mesmo | Outro desktop. O HypeFrame usa o Mutter para as telas e teria de usar o `kscreen-doctor`. O fmp foi feito no KWin | O KWin 6.7.0 derrubava apps HDR com o NVIDIA 610 (resolvido no 6.7.2 com o 610.43.03) |
| Game Mode (gamescope) do Bazzite | Envio direto e VRR | Exige a imagem "deck" e sessão de tela única (sem o OBS no Dell) | **HDR acima de 1440p120 pisca e corrompe na NVIDIA** (bug 5240452, relatado em 2026, com LG C1/C4). Não serve para 4K120 HDR hoje |
| gamescope dentro do GNOME | Soma o agendamento do gamescope ao do GNOME | — | Sem HDR no Mutter 51.1. Não serve |

## 8. Ordem sugerida

| # | O quê | Quem faz | Custo de imagem | Ganho esperado | Atraso esperado depois |
|---|---|---|---|---|---|
Sempre com a geração ligada, no GNOME:

| # | O quê | Quem faz | Custo de imagem | Ganho esperado | Atraso esperado depois |
|---|---|---|---|---|---|
| 1 | **TV: Game Optimizer ou ícone "PC"** na entrada do PC | Você, no menu da TV | Nenhum relevante | Até cerca de 70 ms, **fora** da medição do fmp, se a TV estiver hoje num modo comum | — |
| 2 | **VFG em Média** em 4K30. O HypeFrame avisa e troca com um clique | Você, no HypeFrame | Nenhum, porque a Alta não estava valendo | A geração volta a ficar ligada o tempo todo | 89,6 (base) |
| 3 | **PCIe em Gen4**: botão em Sistema, no HypeFrame | Você (pede senha) | Nenhum | −13 ms | cerca de 77 |
| 4 | **Prioridade do ritmo** "Equilíbrio" ou "Menor atraso" (o código já está feito) | Você, no HypeFrame | Nenhum; talvez mais soluços com o Dell | −8 a −11 ms | cerca de 66 a 69 |
| 5 | **Modo "grade exata"** no fork | Código | Nenhum | −4 a −5 ms | cerca de 62 |
| 6 | Percentil da chegada | Arquivo `--settings` | Nenhum | −1 a −2 ms (some com o PCIe em Gen4) | cerca de 61 |
| 7 | Diagnóstico do envio direto (`MUTTER_DEBUG`) | Você entra de novo na sessão; eu leio o log | Nenhum | Desconhecido (latch e segundo monitor) | — |
| 8 | **1440p60 HDR** (perfil "Menor atraso com HDR") para jogo em que o atraso importa | Você, no HypeFrame | 1440p ampliado | cerca de −17 ms medidos (com o PCIe já em Gen4), e mais cerca de 8 da loteria | cerca de 45 |
| 9 | Trocar sozinho da Alta para a Média, em vez de desligar a geração | Código | Nenhum | Garante a geração ligada | — |

Medir de novo depois de cada passo, sempre nas mesmas condições. O HypeFrame guarda cada sessão no histórico ("Última sessão" e "Sessões anteriores"). Para a tabela do repositório, use a saída copiada com `latencia/extrair.py` (seção 2).

## 9. Testes para fazer com a TV ligada

Nada disso pôde ser testado em 23/11 à noite: as telas estavam desligadas, e o OBS estava com a placa. Em todos, deixe "Parar depois de (s)" em 30 (aba Sistema) e o jogo a 30 fps numa cena parecida.

1. **PCIe.** Sistema → "Liberar a velocidade máxima do PCIe". Confira no aviso que o teto foi para 16 GT/s e rode uma sessão. Na "Última sessão", a cópia para a GPU deve cair de 14 para cerca de 1 ms, e o PCIe deve chegar a Gen4. Se der certo, eu preparo a regra que deixa isso fixo a cada boot.
2. **Qualidade.** Aceite "Usar a Média". Na "Última sessão", a geração deve ficar ligada perto de 100%.
3. **Prioridade do ritmo.** Uma sessão com cada uma: "Mais liso", "Equilíbrio" e "Menor atraso". No histórico, compare o atraso medido e o "fora do ritmo". Fique com a de menor atraso que não pareça soluçar no jogo.
4. **Perfil "Menor atraso com HDR (1440p)".** Veja se a imagem ampliada incomoda e quanto o atraso cai.
5. **120 fps.** Só se quiser mais fluidez: mostra quanto os +8 ms pesam na prática.
6. **TV.** No menu da LG, confira se a entrada do PC está no Game Optimizer ou com o ícone "PC".
7. **Opcional, envio direto do GNOME.** Crie o arquivo da seção 7 (`MUTTER_DEBUG`), saia e entre de novo, rode uma sessão e me avise. Eu leio o motivo no log. Depois, apague o arquivo.

O que foi testado sem as telas: o fmp novo compila, e os 834 testes que não precisam de hardware passam. O resumo de três rodadas reais é lido certo, e a conta do HypeFrame dá 89,8 ms para a base medida em 89,6. As páginas foram desenhadas fora da tela e conferidas, inclusive o aviso da Alta, uma sessão simulada indo para o histórico e as opções novas indo para o comando do fmp.

## Fontes

**Medições:** os arquivos em `latencia/` e nesta pasta, e o código do fmp:
- `Core/Scheduling/LatencyModel.cs` e `FrameScheduler.cs` (espera, alinhamento e margens);
- `Pipeline/PresentAccounting.cs` (`LeadController`);
- `Sources.V4l2/V4l2FrameSource.cs` (cópia de um bloco contíguo de memória fixa da CUDA).

**GNOME (código do Mutter 51.1 e issues):**
- https://gitlab.gnome.org/GNOME/mutter/-/blob/51.1/src/compositor/meta-compositor-view-native.c
- https://gitlab.gnome.org/GNOME/mutter/-/blob/51.1/clutter/clutter/clutter-frame-clock.c
- https://gitlab.gnome.org/GNOME/mutter/-/issues/4559 (VRR com NVIDIA em Wayland nativo)
- https://gitlab.gnome.org/GNOME/mutter/-/issues/4687 (envio direto recusado em HDR com NVIDIA)
- https://gitlab.gnome.org/GNOME/mutter/-/issues/5225 (refresh = 0 com o modo +vrr)
- https://gitlab.gnome.org/GNOME/mutter/-/issues/4815 (VRR cai com janela na borda vinda do outro monitor)
- https://gitlab.gnome.org/GNOME/mutter/-/issues/5140 (commit timing, corrigido no 51.2)

**KDE e gamescope:**
- https://invent.kde.org/plasma/kwin/-/blob/v6.8.3/src/backends/drm/drm_commit_thread.cpp
- https://planet.kde.org/xavers-blog-2026-06-24-display-next-hackfest-2026/
- https://bugs.kde.org/show_bug.cgi?id=521952
- https://forums.developer.nvidia.com/t/display-modes-above-2560x1440p-120hz-with-hdr-enabled-cause-flickering-corruption-within-gamescope-session/295314
- https://docs.bazzite.gg/General/FAQ/

**NVIDIA e PCIe:**
- https://forums.developer.nvidia.com/t/linux-enabling-persistent-pcie-gen-4-0-and-pcie-buswidth-16-for-l40s-gpu/338641
- https://forums.developer.nvidia.com/t/kernel-module-option-nvreg-registrydwords-for-powermizerenable-doesnt-work-on-530-41-03/247610
- https://forums.developer.nvidia.com/t/pinned-memory-throughput-significantly-lower-on-ubuntu-than-on-windows/352580
- https://discourse.ubuntu.com/t/rtx-5070-pcie-link-speed-stuck-at-gen-1-on-ubuntu-24-04-open-kernel-modules-580-x/77366
- https://docs.nvidia.com/deploy/nvidia-smi/index.html
- https://forums.developer.nvidia.com/t/allm-on-linux-and-content-type-drm-prop/381105 (sem ALLM no driver Linux)
- https://forum.zorin.com/t/pcie-link-speed-stuck-at-2-5gt-s-pcie-1-0-despite-gen4-settings-zorin-os-gigabyte-b760m/48774 (B760M DS3H AX DDR4 presa em 2,5 GT/s; um kernel mais novo resolveu)
- https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1010 (B760M Gaming X AX com Bazzite presa em Gen1; aberta)
- https://lkml.iu.edu/hypermail/linux/kernel/2501.1/10775.html (o recuo do kernel para 2,5 GT/s, `pcie_failed_link_retrain`, e a mensagem que ele deixa)
- https://lkml.iu.edu/hypermail/linux/kernel/2410.2/04672.html (o controlador de banda do PCIe e `pcie_set_target_speed`, que o resfriador `PCIe_Port_Link_Speed_*` usa)
- https://discussion.fedoraproject.org/t/nvidia-rtx-4060-mobile-pcie-link-stuck-at-gen1-2-5gt-s/183353 (outro caso em Gen1, sem solução)

**TV, placa, PS5 e controle:**
- https://www.rtings.com/tv/reviews/lg/c2-oled
- https://www.rtings.com/tv/reviews/lg/b3-oled/settings
- https://www.rtings.com/tv/tests/inputs/input-lag
- https://www.expertreviews.co.uk/technology/tvs-home-cinema/lg-c3-oled-review
- https://www.lg.com/us/support/help-library/lg-tv-how-to-enable-allm-automatic-low-latency-mode-game-optimizer-and-game-dashboard-CT10000030-20154860259552
- https://help.elgato.com/hc/en-us/articles/23479175821069-Elgato-Game-Capture-4K-X-Supported-Resolutions-and-Frame-Rates
- https://www.elgato.com/us/en/explorer/products/capture/what-is-the-difference-between-elgato-game-capture-4k-x-and-4k-pro/
- https://x.com/elgato/status/1753065868760871320 (cerca de 35 ms declarados)
- https://docs.kernel.org/userspace-api/media/v4l/buffer.html e o código do `uvcvideo` (`uvc_video.c`)
- https://magewell.com/developer/26/detail (captura PCIe em baixa latência)
- https://github.com/Nakildias/sc0710 (driver da comunidade para as Elgato PCIe: 4K60, HDR só em 8 bits)
- https://www.playstation.com/en-sg/support/hardware/ps5-4k-resolution-guide/
- https://www.gamepressure.com/newsroom/ps5-controller-latency-same-on-cable-and-wireless/zf2ec5
- https://gamepadla.com/sony-dualsense.html
