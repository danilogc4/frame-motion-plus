#!/usr/bin/env bash
# Remove o HypeFrame instalado por install.sh. As configurações (~/.config/hypeframe) ficam.
set -euo pipefail
APP_ID=io.github.danilogc4.HypeFrame
rm -f "$HOME/.local/bin/hypeframe" "$HOME/.local/share/applications/$APP_ID.desktop" \
      "$HOME/.local/share/icons/hicolor/scalable/apps/$APP_ID.svg"
rm -rf "$HOME/.local/share/hypeframe"
update-desktop-database "$HOME/.local/share/applications" >/dev/null 2>&1 || true
echo "HypeFrame removido (configurações mantidas em ~/.config/hypeframe)."
