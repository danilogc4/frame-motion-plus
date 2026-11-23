# HypeFrame

Interface GNOME (GTK4 + libadwaita, Python) para o `fmp capture` deste repositório: captura do PS5
pela Elgato com geração de quadros NVIDIA VFG, no Bazzite/GNOME.

- Todos os parâmetros do `fmp capture` em abas (Início, Imagem e HDR, Captura, Tela e ritmo, Áudio, Sistema),
  com explicação curta em cada controle.
- Perfis rápidos: "HDR fiel" e "Menor atraso" (valores medidos em `docs/gnome-mutter/PROPOSTA.md`).
- Roda o fmp dentro da distrobox (`fmp` por padrão) e mostra a saída ao vivo; Ctrl+Enter inicia e para.
- Avisa quando a placa de captura está em uso por outro programa e quando há outro monitor ligado.
- Tela do jogo: qualquer tela conectada; o jogo abre nela em tela cheia (`fmp --output @X,Y`), esteja o mouse onde estiver.
- Outras telas: uma chave para cada tela conectada, ligadas por padrão durante o jogo (para usar o OBS, por exemplo).
  Desligar alguma deixa o ritmo mais liso, mas religá-la ao parar já deixou o GNOME instável.
- Opcional: ajusta o HDR/SDR da tela do jogo pelo Mutter (`gdctl`) antes de jogar e volta como estava ao parar.
- Guarda os últimos valores, a aba aberta e o tamanho da janela em `~/.config/hypeframe/config.json`.

## Instalar

Precisa do fmp compilado (ver o README da raiz) e de uma distrobox com ele. No host:

```bash
./install.sh
```

Instala para o usuário atual, sem root: o app em `~/.local/share/hypeframe`, o comando `hypeframe` em
`~/.local/bin` e o atalho no menu do GNOME. Depois de mudar o código, rode `./install.sh` de novo.
`./uninstall.sh` remove tudo menos as configurações.

O caminho do binário do fmp, a distrobox e a pasta do VFX SDK (vista de dentro da distrobox) ficam na aba Sistema.
