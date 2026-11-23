#!/usr/bin/env bash
# Instala o HypeFrame para o usuário atual (sem root): app, ícone e atalho no GNOME.
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
APP_ID=io.github.danilogc4.HypeFrame
DEST="$HOME/.local/share/hypeframe"

install -Dm755 "$SRC/hypeframe.py" "$DEST/hypeframe.py"
install -Dm644 "$SRC/data/hypeframe.svg" "$DEST/hypeframe.svg"
install -Dm644 "$SRC/data/hypeframe.svg" "$HOME/.local/share/icons/hicolor/scalable/apps/$APP_ID.svg"
mkdir -p "$HOME/.local/bin" "$HOME/.local/share/applications"
ln -sf "$DEST/hypeframe.py" "$HOME/.local/bin/hypeframe"
sed "s|@EXEC@|$HOME/.local/bin/hypeframe|" "$SRC/data/$APP_ID.desktop.in" \
  > "$HOME/.local/share/applications/$APP_ID.desktop"
gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true
update-desktop-database "$HOME/.local/share/applications" >/dev/null 2>&1 || true
echo "HypeFrame instalado. Procure por \"HypeFrame\" no GNOME ou rode: hypeframe"
