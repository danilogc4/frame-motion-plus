# HypeFrame

Interface GNOME (GTK4 + libadwaita, Python) para o `fmp capture` deste repositório: captura do PS5
pela Elgato com geração de quadros NVIDIA VFG, no Bazzite/GNOME.

- Todos os parâmetros do `fmp capture` em abas (Início, Geração de quadros, Imagem e HDR, Captura, Telas, Áudio,
  Sistema), com explicação curta em cada controle.
- **Geração de quadros**, numa aba só:
  - quantos quadros na tela (60 ou 120 para um jogo a 30), a qualidade do VFG e a prioridade do ritmo
    ("Menor atraso", "Equilíbrio" ou "Mais liso", pelas opções `--lead-adapt` e `--lead-max-ms` do fmp);
  - cada opção diz quanto custa em ms;
  - um resumo mostra o atraso estimado da captura até a tela e de onde ele vem, pelas contas de
    `docs/gnome-mutter/LATENCIA.md`;
  - avisa antes de começar quando a qualidade não cabe e o fmp desligaria a geração.
- Depois de cada sessão, lê o resumo do fmp e mostra o atraso medido, quanto tempo a geração ficou ligada, os quadros
  fora do ritmo, a cópia para a GPU e a velocidade do PCIe. Guarda um histórico para comparar, em
  `~/.local/state/hypeframe/sessoes.json`. As medições do VFG e da cópia substituem as estimativas.
- Aba Sistema: a velocidade do PCIe da placa de vídeo e um botão que pede ao kernel a velocidade máxima da porta (pede
  senha). O controle é o resfriador `PCIe_Port_Link_Speed_*`; ver `LATENCIA.md`, seção 3.A.
- Perfis rápidos: "HDR fiel (4K)", "Menor atraso com HDR (1440p)" e "Menor atraso (SDR)", com os valores medidos em
  `docs/gnome-mutter/`.
- Roda o fmp dentro da distrobox (`fmp` por padrão) e mostra a saída ao vivo; Ctrl+Enter inicia e para.
- Avisa quando a placa de captura está em uso por outro programa e quando há outro monitor ligado.
- Tela do jogo: qualquer tela conectada; o jogo abre nela em tela cheia (`fmp --output @X,Y`), esteja o mouse onde estiver.
- Outras telas: uma chave para cada tela conectada, ligadas por padrão durante o jogo (para usar o OBS, por exemplo).
  Desligar alguma deixa o ritmo mais liso, mas religá-la ao parar já deixou o GNOME instável.
- Opcional: ajusta o HDR/SDR da tela do jogo pelo Mutter (`gdctl`) antes de jogar e volta como estava ao parar.
- Guarda os últimos valores, a aba aberta e o tamanho da janela em `~/.config/hypeframe/config.json`. As
  configurações antigas são convertidas sozinhas: o multiplicador vira 60 ou 120 fps, e a antecedência fica como era.

## Instalar

Precisa do fmp compilado (ver o README da raiz) e de uma distrobox com ele. No host:

```bash
./install.sh
```

Instala para o usuário atual, sem root: o app em `~/.local/share/hypeframe`, o comando `hypeframe` em
`~/.local/bin` e o atalho no menu do GNOME. Depois de mudar o código, rode `./install.sh` de novo.
`./uninstall.sh` remove tudo menos as configurações.

O caminho do binário do fmp, a distrobox e a pasta do VFX SDK (vista de dentro da distrobox) ficam na aba Sistema.
