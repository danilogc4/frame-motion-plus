#!/usr/bin/env python3
"""HypeFrame: interface GNOME para o `fmp capture` (frame-motion-plus).

Monta a linha de comando do fmp a partir de controles, roda o fmp dentro da
distrobox, mostra a saída e guarda os últimos valores em
~/.config/hypeframe/config.json. Opcionalmente ajusta a TV (HDR/SDR, VRR, só a TV)
pelo Mutter antes de jogar e volta como estava ao parar.
"""
import json
import os
import re
import shlex
import signal
import subprocess
import sys
import time

import gi

# libadwaita não suporta GTK_THEME: com ela os estilos das linhas e grupos somem.
os.environ.pop("GTK_THEME", None)

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk, Pango  # noqa: E402

APP_ID = "io.github.danilogc4.HypeFrame"
VERSION = "0.2.0"
HERE = os.path.dirname(os.path.realpath(__file__))
CONFIG_DIR = os.path.join(GLib.get_user_config_dir(), "hypeframe")
CONFIG_FILE = os.path.join(CONFIG_DIR, "config.json")
STATE_DIR = os.path.join(GLib.get_user_state_dir(), "hypeframe")
HISTORY_FILE = os.path.join(STATE_DIR, "sessoes.json")
HISTORY_MAX = 60
LOG_MAX_LINES = 4000

# --------------------------------------------------------------------------
# Parâmetros
# --------------------------------------------------------------------------

MODES = [
    # (id do fmp, rótulo, taxa da captura, (largura, altura), formato)
    ("4k30-hdr", "4K 29,97 HDR10", 29.97, (3840, 2160), "p010"),
    ("1440p60-hdr", "1440p 59,94 HDR10", 59.94, (2560, 1440), "p010"),
    ("4k60-sdr", "4K 59,94 SDR", 59.94, (3840, 2160), "nv12"),
    ("1440p60-sdr", "1440p 59,94 SDR", 59.94, (2560, 1440), "nv12"),
    ("1080p60-sdr", "1080p 59,94 SDR", 59.94, (1920, 1080), "nv12"),
    ("1440p120-sdr", "1440p 120 SDR", 120.0, (2560, 1440), "nv12"),
    ("1080p120-sdr", "1080p 120 SDR", 120.0, (1920, 1080), "nv12"),
    ("auto", "Automático (maior modo 10 bits)", None, None, None),
    ("custom", "Personalizado (aba Captura)", None, None, None),
]

QUALITY_NAMES = {"low": "Baixa", "medium": "Média", "high": "Alta"}
QUALITY_FROM_FMP = {"Low": "low", "Medium": "medium", "High": "high"}

# Taxa do jogo (o fmp detecta sozinho; aqui serve para escolher o multiplicador e fazer as contas).
GAME_RATES = {"30": 29.97, "60": 59.94}

# Tempo do VFG por quadro gerado (p99, ms), medido numa RTX 4080 Super em 23/11/2026. Cada sessão troca estes
# números pelos medidos nesta máquina (histórico em ~/.local/state/hypeframe).
VFG_P99_DEFAULT = {
    2160: {"medium": 2.03, "high": 17.0},
    1440: {"medium": 1.77},
}

# Prioridade do ritmo: o que fazer quando o GNOME não pega um quadro a tempo (a "antecedência" do fmp).
LEAD_POLICIES = [
    ("low", "Menor atraso",
     "A antecedência fica fixa. Quando o GNOME atrasa, aquele quadro aparece um refresh depois (um soluço)"),
    ("balanced", "Equilíbrio",
     "A antecedência sobe só até o teto (8 ms por padrão). Menos atraso que “Mais liso”, alguns soluços a mais"),
    ("smooth", "Mais liso",
     "A antecedência sobe o quanto precisar, até 2 refreshes (16 ms a 120 Hz). Menos soluços, mais atraso"),
]

# Quanto cada parte custa na conta do atraso, para a barra (cores da interface).
PART_COLORS = {
    "usb": (0.0, 0.72, 0.83), "copy": (1.0, 0.54, 0.24), "misc": (0.35, 0.38, 0.49),
    "hold": (0.55, 0.36, 1.0), "gen": (0.71, 0.55, 1.0), "align": (0.18, 0.9, 0.62),
    "lead": (1.0, 0.79, 0.30),
}

DEFAULTS = {
    # Início
    "mode": "4k30-hdr",
    "fullscreen": True,
    # Geração de quadros
    "game_fps": "30",  # taxa do jogo: "30" ou "60"
    "fg_target": "60",  # quadros por segundo na tela: "60", "120" ou "custom" (multiplicador/taxa à mão)
    "quality": "medium",
    "multiplier": 2,
    "output_hz_on": False,
    "output_hz": 119.88,
    "quality_switch": True,
    "scd": "off",
    "lead_policy": "smooth",  # low (fixa), balanced (com teto), smooth (sobe o quanto precisar)
    "lead_max": 8.0,
    "generator": "auto",
    "clock": "auto",
    "cuda_sync": "auto",
    # Imagem
    "hdr": "on",
    "peak": 4000,
    "maxcll_on": False,
    "maxcll": 4000,
    "maxfall": 0,
    "range": "limited",
    "matrix": "bt2020",
    # Captura
    "device": "",
    "capture_fps": "",
    "custom_size": "3840x2160",
    "custom_fps": "",
    "custom_format": "",
    "buffers": 4,
    "buffer_mode": "auto",
    "cadence": True,
    # Telas e ritmo
    "lead_on": False,
    "lead": 5.5,
    "latch_on": False,
    "latch": 5.5,
    "late_generated": 4.0,
    "ceiling": True,
    "window": "1920x1080",
    "tv_apply": False,
    "tv_connector": "",  # a tela do jogo (conector no Mutter: HDMI-1, DP-2...)
    "game_output": True,  # abrir o jogo na tela do jogo (--output); falso: onde está o mouse
    "tv_color": "hdr",
    "tv_vrr": False,
    "others_off": [],  # conectores das outras telas a desligar durante o jogo
    "tv_restore": True,
    # Áudio
    "audio": "auto",
    "sink": "",
    "source": "",
    "audio_offset": 0,
    # Sistema
    "box": "fmp",
    "fmp_path": "~/Projetos/frame-motion-plus/artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp",
    "sdk": "/run/host/var/usrlocal/VideoFX",
    "duration_on": False,
    "duration": 60,
    "warmup": 2.0,
    "csv": "",
    "extra": "",
}

PROFILES = [
    (
        "hdr",
        "HDR fiel (4K)",
        "4K30 em HDR10 e 30 → 60 com o VFG Média; TV em HDR (bt2100), faixa limitada e BT.2020, como o PS5 manda",
        "weather-clear-symbolic",
        {
            "mode": "4k30-hdr", "game_fps": "30", "fg_target": "60", "quality": "medium", "generator": "auto",
            "hdr": "on", "range": "limited", "matrix": "bt2020",
            "clock": "auto", "tv_apply": True, "tv_color": "hdr", "tv_vrr": False,
            "latch_on": False,
        },
    ),
    (
        "hdr-fast",
        "Menor atraso com HDR (1440p)",
        "1440p60 HDR10: a placa entrega cada quadro na metade do tempo e acaba a “loteria” do 4K30 "
        "(−25 ms medidos em 23/11). A geração continua 30 → 60; a imagem fica em 1440p ampliada",
        "starred-symbolic",
        {
            "mode": "1440p60-hdr", "game_fps": "30", "fg_target": "60", "quality": "medium", "generator": "auto",
            "hdr": "on", "range": "limited", "matrix": "bt2020",
            "clock": "auto", "tv_apply": True, "tv_color": "hdr", "tv_vrr": False,
            "latch_on": False,
        },
    ),
    (
        "latency",
        "Menor atraso (SDR)",
        "Tela do jogo em SDR e relógio VRR: 65 ms medidos em 22/11 só com ela ligada, mas sem o HDR do PS5",
        "power-profile-performance-symbolic",
        {
            "mode": "4k30-hdr", "game_fps": "30", "fg_target": "60", "quality": "medium", "generator": "auto",
            "hdr": "follow", "range": "auto", "matrix": "auto",
            "clock": "vrr", "tv_apply": True, "tv_color": "sdr", "tv_vrr": False,
            "latch_on": False,
        },
    ),
]
# Os perfis não mexem nas outras telas nem na prioridade do ritmo: são escolhas à parte.

CONFIG_VERSION = 3
RESTORE_DELAY_MS = 1500

EXIT_MEANING = {
    0: "O fmp terminou normalmente.",
    1: "O fmp terminou com falha numa checagem (veja a saída).",
    2: "Parâmetro inválido ou modo que a placa não oferece.",
    69: "Faltou GPU, placa de captura, tela ou o VFX SDK.",
    70: "Erro interno do fmp.",
}

TRANSFORMS = {0: "normal", 1: "90", 2: "180", 3: "270", 4: "flipped",
              5: "flipped-90", 6: "flipped-180", 7: "flipped-270"}
COLOR_MODES = {0: "default", 1: "bt2100", 2: "sdr-native"}
RGB_RANGES = {1: "auto", 2: "full", 3: "limited"}

CSS = """
:root {
  --accent-bg-color: #7b5cff;
  --accent-fg-color: #ffffff;
  --accent-color: #a996ff;
}
window.hypeframe { background-color: #0c1124; color: #e6ecff; }
window.hypeframe .sidebar-pane { background-color: #080c1a; }
window.hypeframe headerbar { background-color: transparent; box-shadow: none; }
window.hypeframe list.boxed-list, window.hypeframe .card {
  background-color: #121936;
  box-shadow: 0 0 0 1px rgba(122,247,255,0.09), 0 2px 10px rgba(0,0,0,0.25);
}
window.hypeframe list.boxed-list > row { background-color: transparent; }
window.hypeframe list.boxed-list > row:hover { background-color: rgba(122,247,255,0.04); }
window.hypeframe list.navigation-sidebar > row:selected { background-color: rgba(123,92,255,0.30); }
window.hypeframe list.navigation-sidebar > row:selected image { color: #7af7ff; }
window.hypeframe switch:checked { background-color: #7b5cff; }
window.hypeframe toggle-group { background-color: rgba(255,255,255,0.06); }
window.hypeframe toggle-group > toggle:checked {
  background-image: linear-gradient(90deg, #00b8d4, #7b5cff);
  color: #ffffff;
}
window.hypeframe banner > revealer > widget { background-color: rgba(255,170,60,0.18); }
window.hypeframe popover > contents { background-color: #151c3c; }
window.hypeframe popover > arrow { background-color: #151c3c; }
window.hypeframe row.expander list.nested { background-color: rgba(0,0,0,0.18); }
.hero {
  background-image: linear-gradient(135deg, rgba(0,229,255,0.16), rgba(139,92,255,0.24));
  border: 1px solid rgba(122,247,255,0.18);
  border-radius: 26px;
  padding: 26px 28px;
}
.hero-title { font-size: 32px; font-weight: 800; letter-spacing: -0.5px; color: #eefaff; }
.hero-sub { color: rgba(223,231,255,0.72); font-size: 14px; }
.chip {
  background-color: rgba(122,247,255,0.12);
  color: #9ef3ff;
  border-radius: 999px;
  padding: 4px 12px;
  font-weight: 700;
  font-size: 12px;
}
.chip.violet { background-color: rgba(181,140,255,0.16); color: #cdb8ff; }
.play {
  background-image: linear-gradient(90deg, #00cfe8, #8b5cff);
  color: white;
  font-weight: 800;
  font-size: 17px;
  border-radius: 999px;
  min-height: 52px;
  padding: 0 34px;
  box-shadow: 0 8px 26px rgba(139,92,255,0.45);
}
.play:hover { filter: brightness(1.1); }
.play.running {
  background-image: linear-gradient(90deg, #ff4d6d, #ff8a3d);
  box-shadow: 0 8px 26px rgba(255,77,109,0.40);
}
.play.small { font-size: 15px; min-height: 44px; padding: 0 18px; box-shadow: none; }
.brand { font-weight: 800; font-size: 17px; }
.status-card {
  background-color: rgba(255,255,255,0.04);
  border-radius: 16px;
  padding: 12px 14px;
}
.dot { min-width: 10px; min-height: 10px; border-radius: 999px; background-color: #59607d; }
.dot.running { background-color: #2ee59d; box-shadow: 0 0 10px #2ee59d; }
.dot.busy { background-color: #ffc94d; box-shadow: 0 0 10px #ffc94d; }
.dot.error { background-color: #ff4d6d; box-shadow: 0 0 10px #ff4d6d; }
.logbox { background-color: #060a16; border-radius: 14px; border: 1px solid rgba(122,247,255,0.10); }
textview.log, textview.log text { background-color: transparent; color: #cfe1ff; }
textview.log { font-family: monospace; font-size: 11px; padding: 10px; }
.cmd { font-family: monospace; font-size: 11px; color: #9ef3ff; }
.profile-icon { color: #7af7ff; }
.fg-rate { font-size: 34px; font-weight: 800; letter-spacing: -0.5px; color: #eefaff; }
.fg-total { font-size: 24px; font-weight: 800; color: #9ef3ff; }
.fg-caption { color: rgba(223,231,255,0.72); font-size: 13px; }
.fg-note { color: rgba(223,231,255,0.60); font-size: 12px; }
.measure { font-size: 22px; font-weight: 800; color: #eefaff; }
.warn { color: #ffb347; }
.bad { color: #ff6b81; }
.good { color: #2ee59d; }
.legend { font-size: 12px; color: rgba(223,231,255,0.85); }
.legend-dot { min-width: 10px; min-height: 10px; border-radius: 3px; }
.legend-dot.usb { background-color: #00b8d4; }
.legend-dot.copy { background-color: #ff8a3d; }
.legend-dot.misc { background-color: #59607d; }
.legend-dot.hold { background-color: #8b5cff; }
.legend-dot.gen { background-color: #b58cff; }
.legend-dot.align { background-color: #2ee59d; }
.legend-dot.lead { background-color: #ffc94d; }
"""


def load_config():
    values = dict(DEFAULTS)
    extra = {}
    try:
        with open(CONFIG_FILE, encoding="utf-8") as f:
            data = json.load(f)
        for k, v in data.get("values", {}).items():
            if k in DEFAULTS and type(v) is type(DEFAULTS[k]) or (
                k in DEFAULTS and isinstance(DEFAULTS[k], float) and isinstance(v, int)
            ):
                values[k] = v
        extra = {k: v for k, v in data.items() if k != "values"}
        if extra.get("version", 1) < 2:
            # Versão 1 desligava as outras telas ("Só a TV") e ligava o VRR ao jogar. Religar o Dell ao parar
            # deixava o GNOME instável (22/11/2026); o VRR do Mutter nem seguia a janela. Agora: tudo ligado, sem VRR.
            values["others_off"] = []
            values["tv_vrr"] = False
            extra["migrated"] = True
        if extra.get("version", 1) < 3:
            # Versão 3: a geração de quadros ganhou aba própria. O multiplicador vira "quadros na tela" (60 ou 120
            # para um jogo a 30), e a antecedência continua como era ("Mais liso": sobe o quanto precisar).
            if values["output_hz_on"]:
                values["fg_target"] = "custom"
            else:
                values["fg_target"] = {2: "60", 4: "120"}.get(values["multiplier"], "custom")
            values["game_fps"] = "30"
            values["lead_policy"] = "smooth"
            extra["migrated3"] = True
    except (OSError, ValueError):
        pass
    extra["version"] = CONFIG_VERSION
    values["others_off"] = [c for c in values["others_off"] if isinstance(c, str)]
    return values, extra


def save_config(values, extra):
    os.makedirs(CONFIG_DIR, exist_ok=True)
    data = dict(extra)
    data["values"] = values
    tmp = CONFIG_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    os.replace(tmp, CONFIG_FILE)


def fmt_num(x):
    s = f"{x:.2f}".rstrip("0").rstrip(".")
    return s.replace(".", ",")


def valid_size(s):
    parts = s.lower().split("x")
    return len(parts) == 2 and all(p.isdigit() and int(p) > 0 for p in parts)


def fmt_ms(x):
    """Milissegundos com uma casa (ou nenhuma, de 100 para cima), vírgula decimal."""
    if x is None:
        return "?"
    return f"{x:.0f}" if abs(x) >= 100 else f"{x:.1f}".replace(".", ",")


# --------------------------------------------------------------------------
# Geração de quadros: o modo, o multiplicador e a conta do atraso
# --------------------------------------------------------------------------


def mode_spec(v):
    """(taxa da captura, (largura, altura), formato) do modo escolhido; None onde não se sabe."""
    if v["mode"] == "custom":
        size = tuple(int(p) for p in v["custom_size"].lower().split("x")) if valid_size(v["custom_size"]) else None
        fps = float(v["custom_fps"]) if v["custom_fps"] else None
        return fps, size, v["custom_format"] or "p010"
    if v["mode"] == "auto":
        return 29.97, (3840, 2160), "p010"  # na Elgato 4K X o maior modo de 10 bits é 4K30 P010
    for mid, _label, fps, size, fmt in MODES:
        if mid == v["mode"]:
            if fps and v["capture_fps"]:
                fps = float(v["capture_fps"])
            return fps, size, fmt
    return None, None, None


def frame_bytes(size, fmt):
    w, h = size
    return w * h * {"p010": 3.0, "nv12": 1.5, "yuyv": 2.0}.get(fmt or "p010", 3.0)


def res_key(height):
    """A classe de resolução das medições do VFG: 2160, 1440 ou 1080."""
    return 2160 if height >= 2000 else 1440 if height >= 1300 else 1080


def res_label(height):
    key = res_key(height)
    return "4K" if key == 2160 else f"{key}p"


def fg_multiplier(v, capture_fps=None):
    """O multiplicador que o fmp recebe (teto de quadros na tela por quadro do jogo)."""
    if v["fg_target"] == "custom":
        if v["output_hz_on"]:
            # --output-hz: o teto é a taxa de saída sobre a taxa da captura (não a do jogo).
            return max(1, min(8, round(v["output_hz"] / round(capture_fps or 29.97))))
        return v["multiplier"]
    game = round(GAME_RATES.get(v["game_fps"], 29.97))
    return max(1, min(8, round(int(v["fg_target"]) / game)))


def screen_hz(state, conn):
    """A taxa atual de uma tela pelo nome do modo no Mutter (3840x2160@119.880)."""
    m = (state or {}).get("monitors", {}).get(conn) or {}
    match = re.search(r"@([0-9.]+)", m.get("mode") or "")
    return float(match.group(1)) if match else None


def vfg_p99(measures, height, quality):
    """O tempo (p99, ms) de um quadro gerado: o medido nesta máquina, senão o da RTX 4080 Super, senão None."""
    key = res_key(height)
    got = measures.get("vfg", {}).get(str(key), {}).get(quality)
    return got if got is not None else VFG_P99_DEFAULT.get(key, {}).get(quality)


def lead_estimate(v, hz, last):
    """(antecedência usada na conta, texto) para a prioridade do ritmo escolhida."""
    period = 1000.0 / hz
    top = max(1.0, 2 * period - 0.5)  # o limite do fmp: cerca de 2 refreshes
    start = v["lead"]
    policy = v["lead_policy"]
    if policy == "low":
        return start, f"fixa em {fmt_ms(start)} ms"
    if policy == "balanced":
        cap = min(max(v["lead_max"], start), top)
        return cap, f"de {fmt_ms(start)} até {fmt_ms(cap)} ms"
    measured = None
    if last and last.get("lead_policy", "smooth") == "smooth" and last.get("lead_end") and not last.get("lead_fixed"):
        measured = min(last["lead_end"], top)
    return (measured if measured is not None else top), f"de {fmt_ms(start)} até {fmt_ms(top)} ms"


def estimate(v, hz, measures):
    """Atraso da captura até o primeiro pixel, pelas contas do fmp (docs/gnome-mutter/LATENCIA.md, seção 1).

    Com a base de 23/11/2026 (4K30 HDR, VFG Média, PCIe em Gen1, antecedência que subiu a 16,2 ms) dá 89,8 ms;
    o fmp mediu 89,6. measures: "ms_per_mb" (cópia medida), "vfg" (p99 por resolução e qualidade), "last".
    hz: a taxa da tela do jogo (None quando não se sabe: a conta usa 60 e não avisa sobre ela).
    """
    hz_known = hz is not None
    hz = hz or 60.0
    cap_fps, size, fmt = mode_spec(v)
    cap_fps = cap_fps or 29.97
    size = size or (3840, 2160)
    parts, warns, notes = [], [], []
    parts.append(("Placa de captura (USB)", 1000.0 / cap_fps, "usb"))
    mb = frame_bytes(size, fmt) / 1e6
    per_mb = measures.get("ms_per_mb")
    parts.append(("Cópia para a GPU", mb * per_mb if per_mb else mb / 25.0, "copy"))
    parts.append(("Conversão e folga", 2.7, "misc"))
    game = min(GAME_RATES.get(v["game_fps"], 29.97), cap_fps)
    t_ms = 1000.0 / game
    mult = fg_multiplier(v, cap_fps)
    wants = v["generator"] != "none" and mult >= 2
    vfg = v["generator"] in ("auto", "vfg")
    d = vfg_p99(measures, size[1], v["quality"]) if vfg else 0.3
    fits = True
    if wants:
        budget = t_ms / mult
        if d is not None and d > budget:
            fits = False
            warns.append(f"Com a {QUALITY_NAMES[v['quality']]} em {res_label(size[1])}, cada quadro gerado leva cerca de "
                         f"{fmt_ms(d)} ms, e o limite é {fmt_ms(budget)} ms: o fmp desliga a geração")
        else:
            parts.append(("Espera da interpolação", (mult - 1) / mult * t_ms, "hold"))
            parts.append(("Geração do quadro", (d if d is not None else 2.0) + 0.5, "gen"))
    if hz_known and mult * game > hz * 1.01 and wants:
        warns.append(f"A tela do jogo está a {fmt_num(hz)} Hz: não cabem {fmt_num(round(mult * game))} quadros por segundo")
    if v["clock"] == "vrr":
        lead, lead_txt = 2.0, "relógio VRR: entrega na hora"
    else:
        parts.append(("Encaixe no refresh", 500.0 / hz, "align"))
        lead, lead_txt = lead_estimate(v, hz, measures.get("last"))
    parts.append(("Antecedência", lead, "lead"))
    if cap_fps < 40 and game > 25 and GAME_RATES.get(v["game_fps"], 29.97) < 40:
        notes.append("Fora da conta: em 4K30 a placa pega uma de duas cópias de cada quadro do jogo; se for a segunda, "
                     "a imagem chega 16,7 ms mais velha (8 ms em média)")
    notes.append("Fora da conta: a TV (no modo jogo, cerca de 6 ms a 120 Hz) e o próprio jogo")
    return {"parts": parts, "total": sum(p[1] for p in parts), "mult": mult, "gen": wants and fits,
            "wants": wants, "fits": fits, "d": d, "t": t_ms, "game": game, "lead_txt": lead_txt,
            "warns": warns, "notes": notes, "copy_measured": per_mb is not None, "height": size[1]}


# --------------------------------------------------------------------------
# Resultado de cada sessão (o resumo do fmp) e o histórico
# --------------------------------------------------------------------------


def parse_summary(text):
    """Os números do resumo do `fmp capture` (os mesmos de docs/gnome-mutter/latencia/extrair.py)."""
    def num(pattern):
        m = re.search(pattern, text, re.M)
        return float(m.group(1)) if m else None

    r = {}
    m = re.search(r"latency\s+a real frame is shown ([0-9.]+) ms after its capture timestamp \(p99 ([0-9.]+)\)", text)
    if not m:
        return None
    r["latency"], r["latency_p99"] = float(m.group(1)), float(m.group(2))
    m = re.search(r"negotiated\s+(\d+)x(\d+)\s+(\S+)\s+@\s+([0-9.]+) fps.*?(\d+) B per frame", text)
    if m:
        r["size"] = f"{m.group(1)}x{m.group(2)}"
        r["height"] = int(m.group(2))
        r["format"] = m.group(3).lower()
        r["capture_fps"] = float(m.group(4))
        r["frame_bytes"] = int(m.group(5))
    m = re.search(r"slots\s+(\d+) measured: (\d+) real, (\d+) generated, (\d+) repeated", text)
    if m:
        r["slots"], r["real"], r["generated"], r["repeated"] = (int(m.group(i)) for i in range(1, 5))
    r["missed"] = num(r"^\s*missed\s+(\d+) slot")
    r["upload"] = num(r"capture gpu upload p50 ([0-9.]+) ms")
    m = re.search(r"present lead ([0-9.]+) ms at the end(.*)$", text, re.M)
    if m:
        r["lead_end"] = float(m.group(1))
        p = re.search(r"peak ([0-9.]+) ms", m.group(2))
        r["lead_peak"] = float(p.group(1)) if p else r["lead_end"]
        r["lead_fixed"] = "lead-adapt off" in m.group(2)
    m = re.search(r"generation GPU time p50 ([0-9.]+) ms, p99 ([0-9.]+) ms", text)
    if m:
        r["d_p50"], r["d_p99"] = float(m.group(1)), float(m.group(2))
    r["by_quality"] = {}
    for m in re.finditer(r"^\s*at (Low|Medium|High)\s+real frames shown ([0-9.]+) ms.*?d p50 ([0-9.]+) ms, "
                         r"p99 ([0-9.]+) ms over (\d+)", text, re.M):
        r["by_quality"][QUALITY_FROM_FMP[m.group(1)]] = {
            "latency": float(m.group(2)), "p50": float(m.group(3)), "p99": float(m.group(4)), "n": int(m.group(5))}
    interval = num(r"unique-frame intervals p50 ([0-9.]+)")
    if interval:
        r["game_fps"] = 1000.0 / interval
    m = re.search(r"display ([0-9.]+) Hz.*?multiplier cap (\d+), effective (\d+)", text)
    if m:
        r["display_hz"], r["m_cap"], r["m_eff"] = float(m.group(1)), int(m.group(2)), int(m.group(3))
    verdicts = re.findall(r"^\s*verdict\s+(PASS|FAIL)\b", text, re.M)
    r["verdict"] = verdicts[-1] if verdicts else None
    return r


def fg_share(r):
    """A parte dos quadros gerados que o multiplicador pedia (1,0: a geração ficou ligada o tempo todo)."""
    want = r.get("real", 0) * max(1, (r.get("m_cap") or 2) - 1)
    return min(1.0, r.get("generated", 0) / want) if want else None


def load_history():
    try:
        with open(HISTORY_FILE, encoding="utf-8") as f:
            data = json.load(f)
        return [s for s in data if isinstance(s, dict) and "latency" in s]
    except (OSError, ValueError, TypeError):
        return []


def save_history(hist):
    os.makedirs(STATE_DIR, exist_ok=True)
    tmp = HISTORY_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(hist[-HISTORY_MAX:], f, indent=1, ensure_ascii=False)
    os.replace(tmp, HISTORY_FILE)


def measures_from(hist):
    """O que o histórico ensina para as contas: o VFG por resolução e qualidade e a cópia por MB."""
    out = {"vfg": {}, "ms_per_mb": None, "last": hist[-1] if hist else None}
    for s in hist:
        h = s.get("height")
        for q, st in (s.get("by_quality") or {}).items():
            if h and st.get("n", 0) >= 30:
                out["vfg"].setdefault(str(res_key(h)), {})[q] = st["p99"]
        if s.get("upload") and s.get("frame_bytes"):
            out["ms_per_mb"] = s["upload"] / (s["frame_bytes"] / 1e6)
    return out


# --------------------------------------------------------------------------
# PCIe da placa de vídeo (leitura sem root; a correção pede senha pelo pkexec)
# --------------------------------------------------------------------------

PCI_DIR = "/sys/bus/pci/devices"
BDF_RE = re.compile(r"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$")
PCIE_GEN = {2.5: 1, 5.0: 2, 8.0: 3, 16.0: 4, 32.0: 5, 64.0: 6}


def read_text(path):
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return ""


def gts(text):
    m = re.match(r"([0-9.]+)\s*GT/s", text or "")
    return float(m.group(1)) if m else None


def gen_label(speed):
    if not speed:
        return "?"
    n = PCIE_GEN.get(speed)
    return f"{fmt_num(speed)} GT/s (Gen{n})" if n else f"{fmt_num(speed)} GT/s"


def gpu_link():
    """A placa de vídeo (NVIDIA de preferência), a porta PCIe acima dela, o resfriador de PCIe do kernel dessa porta
    (bwctrl: o estado 0 é a velocidade máxima) e as velocidades do link."""
    best = None
    try:
        names = os.listdir(PCI_DIR)
    except OSError:
        return None
    for bdf in names:
        if not read_text(f"{PCI_DIR}/{bdf}/class").startswith("0x03"):
            continue
        width = read_text(f"{PCI_DIR}/{bdf}/max_link_width")
        key = (read_text(f"{PCI_DIR}/{bdf}/vendor") == "0x10de", int(width) if width.isdigit() else 0)
        if best is None or key > best[0]:
            best = (key, bdf)
    if not best:
        return None
    bdf = best[1]
    port = os.path.basename(os.path.dirname(os.path.realpath(f"{PCI_DIR}/{bdf}")))
    if not BDF_RE.match(port):
        port = None
    cdev = None
    if port:
        base = "/sys/class/thermal"
        try:
            for n in sorted(os.listdir(base)):
                if re.fullmatch(r"cooling_device\d+", n) and read_text(f"{base}/{n}/type") == f"PCIe_Port_Link_Speed_{port}":
                    cdev = f"{base}/{n}"
        except OSError:
            pass
    return {
        "gpu": bdf, "port": port, "cdev": cdev,
        "now": gts(read_text(f"{PCI_DIR}/{bdf}/current_link_speed")),
        "max": gts(read_text(f"{PCI_DIR}/{bdf}/max_link_speed")),
        "port_max": gts(read_text(f"{PCI_DIR}/{port}/max_link_speed")) if port else None,
        "width": read_text(f"{PCI_DIR}/{bdf}/current_link_width"),
    }


def gpu_name(bdf):
    try:
        r = subprocess.run(["lspci", "-s", bdf], capture_output=True, text=True, timeout=5)
        line = r.stdout.strip().splitlines()[0]
        return line.split(": ", 1)[1].split(" (rev")[0] if ": " in line else line
    except (OSError, IndexError, subprocess.SubprocessError):
        return bdf


# --------------------------------------------------------------------------
# Sistema: dispositivos, áudio, monitores, processos
# --------------------------------------------------------------------------


def capture_devices():
    out = []
    base = "/sys/class/video4linux"
    try:
        names = sorted(os.listdir(base), key=lambda n: int(n[5:]) if n[5:].isdigit() else 999)
    except OSError:
        return out
    for n in names:
        try:
            with open(f"{base}/{n}/name") as f:
                label = f.read().strip()
            with open(f"{base}/{n}/index") as f:
                index = f.read().strip()
        except OSError:
            continue
        if index != "0":
            continue
        if label.count(":") == 1 and label.split(":")[0].strip() == label.split(":")[1].strip():
            label = label.split(":")[0].strip()
        out.append((f"/dev/{n}", f"{label} ({n})"))
    return out


def pactl_nodes(kind):
    try:
        r = subprocess.run(["pactl", "-f", "json", "list", kind], capture_output=True,
                           text=True, timeout=5)
        items = json.loads(r.stdout or "[]")
    except (OSError, ValueError, subprocess.SubprocessError):
        return []
    out = []
    for it in items:
        name = it.get("name", "")
        if not name or name.endswith(".monitor"):
            continue
        out.append((name, it.get("description") or name))
    return out


def display_state():
    """Estado do Mutter: monitores (modo atual, cor) e monitores lógicos."""
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    res = bus.call_sync(
        "org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
        "org.gnome.Mutter.DisplayConfig", "GetCurrentState", None, None,
        Gio.DBusCallFlags.NONE, 3000, None)
    _serial, monitors, logical, props = res.unpack()
    mons = {}
    for (conn, _vendor, product, _ser), modes, mprops in monitors:
        current = preferred = None
        ids = []
        for mid, w, h, rate, _ps, _sc, moprops in modes:
            ids.append(mid)
            if moprops.get("is-current"):
                current = mid
            if moprops.get("is-preferred"):
                preferred = mid
        mons[conn] = {
            "name": mprops.get("display-name") or product,
            "mode": current,
            "preferred": preferred or (ids[0] if ids else None),
            "modes": ids,
            "color": mprops.get("color-mode", 0),
            "colors": list(mprops.get("supported-color-modes", [])),
            "rgb": mprops.get("rgb-range", 0),
            "builtin": bool(mprops.get("is-builtin", False)),
        }
    logicals = []
    for x, y, scale, transform, primary, mlist, _p in logical:
        logicals.append({
            "x": x, "y": y, "scale": scale, "transform": transform,
            "primary": bool(primary), "monitors": [m[0] for m in mlist],
        })
    return {"monitors": mons, "logical": logicals,
            "layout": {1: "logical", 2: "physical"}.get(props.get("layout-mode", 1), "logical")}


def gdctl_args(state):
    args = ["gdctl", "set", "-P", "--layout-mode", state["layout"]]
    for lm in state["logical"]:
        args += ["-L", "--x", str(lm["x"]), "--y", str(lm["y"]),
                 "--scale", repr(float(lm["scale"])),
                 "--transform", TRANSFORMS.get(lm["transform"], "normal")]
        if lm["primary"]:
            args.append("--primary")
        for conn in lm["monitors"]:
            m = state["monitors"][conn]
            args += ["-M", conn]
            if m["mode"]:
                args += ["--mode", m["mode"]]
            if m["color"] in COLOR_MODES:
                args += ["--color-mode", COLOR_MODES[m["color"]]]
            if m["rgb"] in RGB_RANGES:
                args += ["--rgb-range", RGB_RANGES[m["rgb"]]]
    return args


def game_state(state, conn, apply_color, hdr, vrr, off):
    """O estado desejado para jogar, a partir do atual.

    apply_color: mexer no modo de cor/VRR da tela do jogo. off: conectores das outras telas a desligar.
    A tela do jogo nunca é desligada; se ela estiver fora do desktop, entra à direita.
    """
    import copy
    want = copy.deepcopy(state)
    tv = want["monitors"][conn]
    if apply_color:
        base = tv["mode"] or tv["preferred"]
        base = base.replace("+vrr", "") if base else base
        mode = base + "+vrr" if vrr and base and base + "+vrr" in tv["modes"] else base
        tv["mode"] = mode
        tv["color"] = 1 if hdr else 0
    off = {c for c in off if c != conn}
    if off:
        kept = []
        for lm in want["logical"]:
            mons = [c for c in lm["monitors"] if c not in off]
            if mons:
                kept.append(dict(lm, monitors=mons))
        want["logical"] = kept
    if not any(conn in lm["monitors"] for lm in want["logical"]):
        right = max((lm["x"] + 10000 for lm in want["logical"]), default=0)
        want["logical"].append({"x": right, "y": 0, "scale": 1.0, "transform": 0,
                                "primary": False, "monitors": [conn]})
    if want["logical"] and not any(lm["primary"] for lm in want["logical"]):
        for lm in want["logical"]:
            lm["primary"] = conn in lm["monitors"]
    if off and want["logical"]:
        # O Mutter quer o desktop começando em 0,0: desloca o que sobrou. Telas que ficarem sem encostar
        # umas nas outras (3+ telas, desligando a do meio) o GNOME recusa, e o HypeFrame mostra o erro.
        dx = min(lm["x"] for lm in want["logical"])
        dy = min(lm["y"] for lm in want["logical"])
        for lm in want["logical"]:
            lm["x"] -= dx
            lm["y"] -= dy
    return want


def same_layout(a, b):
    def key(s):
        lms = sorted((lm["x"], lm["y"], round(lm["scale"], 3), lm["transform"], lm["primary"],
                      tuple(sorted(lm["monitors"]))) for lm in s["logical"])
        active = {c for lm in s["logical"] for c in lm["monitors"]}
        mons = sorted((c, s["monitors"][c]["mode"], s["monitors"][c]["color"]) for c in active)
        return lms, mons
    return key(a) == key(b)


def screen_label(conn, m):
    """Nome de uma tela para o usuário: o nome do Mutter e o conector."""
    return f"{m['name']} ({conn})" if m.get("name") else conn


def active_connectors(state):
    return [c for lm in state["logical"] for c in lm["monitors"]]


def logical_position(state, conn):
    """Posição da tela no desktop (a mesma que o SDL usa), para o --output @X,Y."""
    for lm in state["logical"]:
        if conn in lm["monitors"]:
            return lm["x"], lm["y"]
    return None


def describe_tv(state, conn):
    m = state["monitors"].get(conn)
    if not m:
        return f"{conn or 'Tela do jogo'}: desconectada"
    if conn not in active_connectors(state):
        return f"{screen_label(conn, m)}: desligada"
    color = {0: "SDR", 1: "HDR (bt2100)", 2: "SDR nativo"}.get(m["color"], str(m["color"]))
    others = sum(1 for lm in state["logical"] if conn not in lm["monitors"])
    extra = f", mais {others} tela(s) ligada(s)" if others else ", única tela ligada"
    return f"{m['mode']}, {color}{extra}"


def holders(paths):
    """Processos do usuário que têm algum destes dispositivos aberto."""
    found = {}
    me = os.getpid()
    for pid in os.listdir("/proc"):
        if not pid.isdigit() or int(pid) == me:
            continue
        fd_dir = f"/proc/{pid}/fd"
        try:
            for fd in os.listdir(fd_dir):
                try:
                    if os.readlink(f"{fd_dir}/{fd}") in paths:
                        with open(f"/proc/{pid}/comm") as f:
                            found[int(pid)] = f.read().strip()
                        break
                except OSError:
                    continue
        except OSError:
            continue
    return found


def fmp_pids(fmp_path):
    out = []
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                argv = f.read().split(b"\0")
        except OSError:
            continue
        if argv and argv[0].decode(errors="replace") == fmp_path:
            out.append(int(pid))
    return out


# --------------------------------------------------------------------------
# Janela
# --------------------------------------------------------------------------


class HypeFrameWindow(Adw.ApplicationWindow):
    def __init__(self, app):
        super().__init__(application=app, title="HypeFrame")
        self.add_css_class("hypeframe")
        self.values, self.extra = load_config()
        self.setters = {}
        self.rows = {}
        self.save_id = 0
        self.proc = None
        self.proc_kind = None
        self.stopping = False
        self.launch_timer = 0
        self.restore_timer = 0
        self.kill_timers = []
        self.restore_args = None
        self.chip_box = None
        self.radio_rows = {}
        self.run_lines = []
        self.run_eof = self.run_exited = False
        self.run_config = None
        self.pcie_timer = 0
        self.pcie_seen = []
        self.history = load_history()
        self.measures = measures_from(self.history)
        self.link = gpu_link()
        self.link_name = gpu_name(self.link["gpu"]) if self.link else ""
        self.est = None

        self.devices = capture_devices()
        self.sinks = pactl_nodes("sinks")
        self.sources = pactl_nodes("sources")
        try:
            self.display = display_state()
        except GLib.Error:
            self.display = {"monitors": {}, "logical": [], "layout": "logical"}
        if not self.values["tv_connector"] or self.values["tv_connector"] not in self.display["monitors"]:
            ext = [c for c, m in self.display["monitors"].items() if not m["builtin"]]
            hdmi = [c for c in ext if c.startswith("HDMI")]
            if hdmi or ext:
                self.values["tv_connector"] = (hdmi or ext)[0]

        w, h = self.extra.get("size", [1100, 820])
        self.set_default_size(w, h)
        self.set_icon_name(APP_ID)

        self.toasts = Adw.ToastOverlay()
        self.set_content(self.toasts)
        self.split = Adw.OverlaySplitView(min_sidebar_width=230, max_sidebar_width=280)
        self.toasts.set_child(self.split)

        bp = Adw.Breakpoint.new(Adw.BreakpointCondition.parse("max-width: 760sp"))
        bp.add_setter(self.split, "collapsed", True)
        self.add_breakpoint(bp)

        self.stack = Adw.ViewStack(vexpand=True)
        self.pages = []
        self.build_pages()
        self.build_sidebar()
        self.build_content()
        self.refresh_all()
        self.update_banner()

        page = self.extra.get("page", "inicio")
        for i, (name, *_r) in enumerate(self.pages):
            if name == page:
                self.nav.select_row(self.nav.get_row_at_index(i))

        self.set_status("idle", "Parado", "")
        self.connect("close-request", self.on_close_request)
        if self.extra.pop("migrated", False):
            self.toast("Agora as outras telas ficam ligadas no jogo e o VRR fica desligado (ajuste em Telas)")
            self.schedule_save()
        if self.extra.pop("migrated3", False):
            self.toast("Nova aba “Geração de quadros”: fluidez, qualidade, ritmo e o atraso de cada sessão")
            self.schedule_save()
        self.install_shortcuts()

    # ---------------------------------------------------------------- layout

    def build_sidebar(self):
        tv = Adw.ToolbarView()
        hb = Adw.HeaderBar(show_end_title_buttons=False)
        brand = Gtk.Box(spacing=8)
        brand.append(self.app_image(24))
        lab = Gtk.Label(label="HypeFrame")
        lab.add_css_class("brand")
        brand.append(lab)
        hb.set_title_widget(brand)
        tv.add_top_bar(hb)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        self.nav = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE)
        self.nav.add_css_class("navigation-sidebar")
        for name, title, icon, _w in self.pages:
            row = Gtk.ListBoxRow()
            rb = Gtk.Box(spacing=12, margin_top=8, margin_bottom=8, margin_start=6)
            rb.append(Gtk.Image.new_from_icon_name(icon))
            rb.append(Gtk.Label(label=title, xalign=0))
            row.set_child(rb)
            row.page_name = name
            self.nav.append(row)
        self.nav.connect("row-selected", self.on_nav)
        sw = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        sw.set_child(self.nav)
        box.append(sw)

        status = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4,
                         margin_start=12, margin_end=12)
        status.add_css_class("status-card")
        top = Gtk.Box(spacing=8)
        self.side_dot = Gtk.Box(valign=Gtk.Align.CENTER)
        self.side_dot.add_css_class("dot")
        top.append(self.side_dot)
        self.side_status = Gtk.Label(label="Parado", xalign=0, hexpand=True)
        self.side_status.add_css_class("heading")
        top.append(self.side_status)
        status.append(top)
        self.side_detail = Gtk.Label(xalign=0, wrap=True)
        self.side_detail.add_css_class("dim-label")
        self.side_detail.add_css_class("caption")
        status.append(self.side_detail)
        box.append(status)

        self.side_play = Gtk.Button(label="Iniciar", margin_start=12, margin_end=12, margin_bottom=14)
        self.side_play.add_css_class("play")
        self.side_play.add_css_class("small")
        self.side_play.connect("clicked", lambda *_: self.toggle_run())
        box.append(self.side_play)
        tv.set_content(box)
        self.split.set_sidebar(tv)

    def build_content(self):
        tv = Adw.ToolbarView()
        hb = Adw.HeaderBar(show_start_title_buttons=True)
        self.title = Adw.WindowTitle(title="Início")
        hb.set_title_widget(self.title)

        show = Gtk.ToggleButton(icon_name="sidebar-show-symbolic", tooltip_text="Menu")
        show.bind_property("active", self.split, "show-sidebar",
                           GObject_flags())
        self.split.bind_property("collapsed", show, "visible", GObject_flags(sync=True))
        hb.pack_start(show)

        menu = Gio.Menu()
        menu.append("Restaurar padrões", "win.defaults")
        menu.append("Abrir pasta das configurações", "win.config-dir")
        menu.append("Sobre o HypeFrame", "win.about")
        mb = Gtk.MenuButton(icon_name="open-menu-symbolic", menu_model=menu, tooltip_text="Menu")
        hb.pack_end(mb)
        self.add_action_cb("defaults", self.on_defaults)
        self.add_action_cb("config-dir", lambda *_: Gio.AppInfo.launch_default_for_uri(
            GLib.filename_to_uri(CONFIG_DIR if os.path.isdir(CONFIG_DIR) else GLib.get_user_config_dir()), None))
        self.add_action_cb("about", self.on_about)
        tv.add_top_bar(hb)

        self.banner = Adw.Banner()
        self.banner.connect("button-clicked", self.on_banner)
        tv.add_top_bar(self.banner)

        tv.set_content(self.stack)
        self.split.set_content(tv)

    def add_action_cb(self, name, cb):
        a = Gio.SimpleAction.new(name, None)
        a.connect("activate", cb)
        self.add_action(a)

    def install_shortcuts(self):
        ctl = Gtk.ShortcutController(scope=Gtk.ShortcutScope.GLOBAL)
        ctl.add_shortcut(Gtk.Shortcut.new(
            Gtk.ShortcutTrigger.parse_string("<Control>Return|F5"),
            Gtk.CallbackAction.new(lambda *_: (self.toggle_run(), True)[1])))
        ctl.add_shortcut(Gtk.Shortcut.new(
            Gtk.ShortcutTrigger.parse_string("<Control>q"),
            Gtk.CallbackAction.new(lambda *_: (self.close(), True)[1])))
        self.add_controller(ctl)

    def app_image(self, size):
        for p in (os.path.join(HERE, "hypeframe.svg"), os.path.join(HERE, "data", "hypeframe.svg")):
            if os.path.exists(p):
                img = Gtk.Image.new_from_file(p)
                break
        else:
            img = Gtk.Image.new_from_icon_name(APP_ID)
        img.set_pixel_size(size)
        return img

    def on_nav(self, _lb, row):
        if not row:
            return
        self.stack.set_visible_child_name(row.page_name)
        for name, title, *_r in self.pages:
            if name == row.page_name:
                self.title.set_title(title)
        self.extra["page"] = row.page_name
        self.schedule_save()
        if self.split.get_collapsed():
            self.split.set_show_sidebar(False)

    def add_page(self, name, title, icon, widget):
        self.pages.append((name, title, icon, widget))
        self.stack.add_named(widget, name)

    # ---------------------------------------------------------------- páginas

    def build_pages(self):
        self.add_page("inicio", "Início", "media-playback-start-symbolic", self.page_home())
        self.add_page("geracao", "Geração de quadros", "media-seek-forward-symbolic", self.page_generation())
        self.add_page("imagem", "Imagem e HDR", "color-select-symbolic", self.page_image())
        self.add_page("captura", "Captura", "camera-video-symbolic", self.page_capture())
        self.add_page("tela", "Telas", "video-display-symbolic", self.page_display())
        self.add_page("audio", "Áudio", "audio-speakers-symbolic", self.page_audio())
        self.add_page("sistema", "Sistema", "emblem-system-symbolic", self.page_system())

    def go_page(self, name):
        for i, (n, *_r) in enumerate(self.pages):
            if n == name:
                self.nav.select_row(self.nav.get_row_at_index(i))

    def page_home(self):
        sw = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.NEVER)
        clamp = Adw.Clamp(maximum_size=820, margin_top=24, margin_bottom=32,
                          margin_start=16, margin_end=16)
        sw.set_child(clamp)
        col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=24)
        clamp.set_child(col)

        hero = Gtk.Box(spacing=22)
        hero.add_css_class("hero")
        hero.append(self.app_image(104))
        txt = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, hexpand=True,
                      valign=Gtk.Align.CENTER)
        t = Gtk.Label(label="HypeFrame", xalign=0)
        t.add_css_class("hero-title")
        txt.append(t)
        s = Gtk.Label(label="Geração de quadros ao vivo para a captura do PS5, com NVIDIA VFG",
                      xalign=0, wrap=True)
        s.add_css_class("hero-sub")
        txt.append(s)
        self.chip_box = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=6,
                                    column_spacing=6, row_spacing=6, margin_top=6)
        txt.append(self.chip_box)
        self.hero_play = Gtk.Button(label="Iniciar", halign=Gtk.Align.START, margin_top=10)
        self.hero_play.add_css_class("play")
        self.hero_play.connect("clicked", lambda *_: self.toggle_run())
        txt.append(self.hero_play)
        hero.append(txt)
        col.append(hero)

        prof = Adw.PreferencesGroup(title="Perfis rápidos",
                                    description="Ajustam vários controles de uma vez; o resto fica como está")
        for pid, title, sub, icon, _vals in PROFILES:
            row = Adw.ActionRow(title=title, subtitle=sub, activatable=True)
            img = Gtk.Image.new_from_icon_name(icon)
            img.add_css_class("profile-icon")
            row.add_prefix(img)
            row.add_suffix(Gtk.Image.new_from_icon_name("go-next-symbolic"))
            row.connect("activated", lambda _r, p=pid: self.apply_profile(p))
            prof.add(row)
        col.append(prof)

        ess = Adw.PreferencesGroup(title="Essencial")
        ess.add(self.combo("mode", "Modo de captura", [(m[0], m[1]) for m in MODES],
                           "O que a Elgato entrega; o fmp gera os quadros do meio"))
        self.home_fg = Adw.ActionRow(title="Geração de quadros", activatable=True)
        img = Gtk.Image.new_from_icon_name("media-seek-forward-symbolic")
        img.add_css_class("profile-icon")
        self.home_fg.add_prefix(img)
        self.home_fg.add_suffix(Gtk.Image.new_from_icon_name("go-next-symbolic"))
        self.home_fg.connect("activated", lambda *_: self.go_page("geracao"))
        ess.add(self.home_fg)
        self.home_last = Adw.ActionRow(title="Última sessão", activatable=True)
        img = Gtk.Image.new_from_icon_name("document-open-recent-symbolic")
        img.add_css_class("profile-icon")
        self.home_last.add_prefix(img)
        self.home_last.add_suffix(Gtk.Image.new_from_icon_name("go-next-symbolic"))
        self.home_last.connect("activated", lambda *_: self.go_page("geracao"))
        ess.add(self.home_last)
        ess.add(self.switch("fullscreen", "Tela cheia",
                            "Abre na tela do jogo escolhida em Telas"))
        col.append(ess)

        logg = Adw.PreferencesGroup(title="Saída do fmp")
        tools = Gtk.Box(spacing=6)
        copy = Gtk.Button(icon_name="edit-copy-symbolic", tooltip_text="Copiar a saída")
        copy.add_css_class("flat")
        copy.connect("clicked", self.on_copy_log)
        clear = Gtk.Button(icon_name="edit-clear-all-symbolic", tooltip_text="Limpar")
        clear.add_css_class("flat")
        clear.connect("clicked", lambda *_: self.logbuf.set_text(""))
        tools.append(copy)
        tools.append(clear)
        logg.set_header_suffix(tools)
        self.logbuf = Gtk.TextBuffer()
        self.logview = Gtk.TextView(buffer=self.logbuf, editable=False, cursor_visible=False,
                                    wrap_mode=Gtk.WrapMode.WORD_CHAR, monospace=True)
        self.logview.add_css_class("log")
        lsw = Gtk.ScrolledWindow(min_content_height=260, max_content_height=420,
                                 propagate_natural_height=True)
        lsw.set_child(self.logview)
        frame = Gtk.Box()
        frame.add_css_class("logbox")
        lsw.set_hexpand(True)
        frame.append(lsw)
        logg.add(frame)
        col.append(logg)
        self.log("Pronto. Ajuste os controles e clique em Iniciar (Ctrl+Enter).")
        return sw

    def radios(self, key, options, group):
        """Uma linha por opção, com botão de rádio: o título e a explicação de cada escolha ficam à vista."""
        rows = {}
        first = None
        for value, title, subtitle in options:
            row = Adw.ActionRow(title=title, subtitle=subtitle or "", activatable=True)
            chk = Gtk.CheckButton(valign=Gtk.Align.CENTER)
            if first is None:
                first = chk
            else:
                chk.set_group(first)
            row.add_prefix(chk)
            row.set_activatable_widget(chk)
            chk.set_active(self.values[key] == value)
            chk.connect("toggled", lambda c, val=value: c.get_active() and self.changed(key, val))
            group.add(row)
            rows[value] = (row, chk)
        if self.values[key] not in rows:
            self.values[key] = options[0][0]
            rows[options[0][0]][1].set_active(True)

        def setv(val):
            if val in rows:
                rows[val][1].set_active(True)
        self.setters[key] = setv
        self.radio_rows[key] = rows
        return rows

    def page_generation(self):
        page = Adw.PreferencesPage()

        # Resumo: o que sai na tela, o atraso estimado e de onde ele vem, e o último medido.
        top = Adw.PreferencesGroup()
        hero = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        hero.add_css_class("hero")
        head = Gtk.Box(spacing=18)
        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, hexpand=True)
        self.fg_rate = Gtk.Label(xalign=0)
        self.fg_rate.add_css_class("fg-rate")
        left.append(self.fg_rate)
        self.fg_sub = Gtk.Label(xalign=0, wrap=True)
        self.fg_sub.add_css_class("fg-caption")
        left.append(self.fg_sub)
        head.append(left)
        right = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, valign=Gtk.Align.CENTER)
        cap = Gtk.Label(label="Atraso estimado", xalign=1)
        cap.add_css_class("fg-caption")
        right.append(cap)
        self.fg_total = Gtk.Label(xalign=1)
        self.fg_total.add_css_class("fg-total")
        right.append(self.fg_total)
        self.fg_total_sub = Gtk.Label(xalign=1)
        self.fg_total_sub.add_css_class("fg-note")
        right.append(self.fg_total_sub)
        head.append(right)
        hero.append(head)
        self.fg_bar = Gtk.DrawingArea(content_height=16, hexpand=True)
        self.fg_bar.set_draw_func(self.draw_bar)
        hero.append(self.fg_bar)
        self.fg_legend = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=4,
                                     min_children_per_line=2, column_spacing=14, row_spacing=4,
                                     homogeneous=False)
        hero.append(self.fg_legend)
        self.fg_warn = Gtk.Label(xalign=0, wrap=True)
        self.fg_warn.add_css_class("bad")
        hero.append(self.fg_warn)
        self.fg_last = Gtk.Label(xalign=0, wrap=True)
        self.fg_last.add_css_class("fg-caption")
        hero.append(self.fg_last)
        self.fg_notes = Gtk.Label(xalign=0, wrap=True)
        self.fg_notes.add_css_class("fg-note")
        hero.append(self.fg_notes)
        top.add(hero)
        page.add(top)

        game = Adw.PreferencesGroup(title="Seu jogo")
        game.add(self.toggles("game_fps", "Taxa do jogo (fps)", [("30", "30"), ("60", "60")],
                              "O fmp detecta sozinho; isto escolhe quantos quadros gerar"))
        keys = Adw.ActionRow(title="Durante o jogo",
                             subtitle="Q troca a qualidade · H troca SDR e HDR10 · A volta a seguir o console · Esc sai")
        keys.add_css_class("property")
        game.add(keys)
        page.add(game)

        rate = Adw.PreferencesGroup(
            title="Quadros na tela",
            description="Cada quadro gerado fica entre dois do jogo. Para criá-lo, o fmp espera o quadro seguinte "
                        "chegar: é a espera da interpolação, o preço fixo da geração")
        self.radios("fg_target", [
            ("60", "60 fps · menor atraso", ""),
            ("120", "120 fps · mais fluido", ""),
            ("custom", "Personalizado", "Multiplicador ou taxa de saída escolhidos à mão"),
        ], rate)
        rate.add(self.spin("multiplier", "Multiplicador", 1, 8, 1, 0,
                           "Teto de quadros na tela por quadro do jogo"))
        rate.add(self.optional_spin("output_hz", "Taxa de saída (Hz)", 24, 360, 0.01, 2,
                                    "Ligada: substitui o multiplicador (o teto vira esta taxa sobre a da captura)"))
        page.add(rate)

        self.quality_group = Adw.PreferencesGroup(
            title="Qualidade dos quadros gerados",
            description="O tempo de cada quadro gerado entra no atraso. Se passar do limite, o fmp desliga a geração")
        self.radios("quality", [
            ("medium", "Média · recomendada", ""),
            ("high", "Alta · menos artefatos", ""),
            ("low", "Baixa · mais leve", ""),
        ], self.quality_group)
        self.quality_group.add(self.switch("quality_switch", "Trocar a qualidade com Q durante o jogo",
                                           "Carrega as duas qualidades no início (+1,5 GB de memória de vídeo em 4K)"))
        page.add(self.quality_group)

        rhythm = Adw.PreferencesGroup(
            title="Prioridade do ritmo",
            description="O GNOME precisa de cada quadro uns 5 ms antes do refresh. Quando um chega tarde, o fmp passa "
                        "a entregar mais cedo (a antecedência), e todo quadro espera mais")
        self.radios("lead_policy", LEAD_POLICIES, rhythm)
        rhythm.add(self.spin("lead_max", "Teto do Equilíbrio (ms)", 1, 20, 0.5, 1,
                             "Até onde a antecedência pode subir em “Equilíbrio”"))
        rhythm.add(self.optional_spin("lead", "Antecedência inicial (ms)", 0, 20, 0.5, 1,
                                      "Desligado: o fmp mede o GNOME antes de começar (“Menor atraso” usa este valor)"))
        page.add(rhythm)

        cuts = Adw.PreferencesGroup(title="Cortes de cena")
        cuts.add(self.toggles("scd", "Cena que muda de repente",
                              [("off", "Interpolar"), ("on", "Detectar")],
                              "Detectar confunde movimento rápido com corte"))
        page.add(cuts)

        adv = Adw.PreferencesGroup()
        exp = Adw.ExpanderRow(title="Opções avançadas",
                              subtitle="Gerador, relógio da tela, latch, VRR e CUDA")
        exp.add_row(self.combo("generator", "Gerador", [
            ("auto", "Automático (VFG se o SDK existir)"), ("vfg", "NVIDIA VFG"),
            ("blend", "Mistura simples"), ("repeat", "Repetir quadro"), ("none", "Nenhum (só reais, sem geração)")]))
        exp.add_row(self.toggles("clock", "Relógio da tela",
                                 [("auto", "Auto"), ("fixed", "Fixo"), ("vrr", "VRR")],
                                 "VRR: entrega na hora. Sem VRR de verdade na tela, só vale com taxa múltipla exata"))
        exp.add_row(self.optional_spin("latch", "Latch mínimo (ms)", 0, 20, 0.5, 1,
                                       "Desligado: 1 ms (padrão do fmp)"))
        exp.add_row(self.spin("late_generated", "Quadro gerado atrasado (ms)", 0, 8, 0.5, 1,
                              "VRR: mostra um gerado até este atraso em vez de pular"))
        exp.add_row(self.switch("ceiling", "Ritmo no teto do VRR",
                                "Pula um gerado quando a tela fica para trás no limite da taxa"))
        exp.add_row(self.combo("cuda_sync", "Espera do CUDA", [
            ("auto", "Automático (bloqueante)"), ("blocking", "Bloqueante (menos CPU)"),
            ("spin", "Ativa (spin)"), ("yield", "Yield")]))
        adv.add(exp)
        page.add(adv)

        self.last_group = Adw.PreferencesGroup(
            title="Última sessão",
            description="Medido pelo fmp ao parar (Esc ou Parar). Compare antes e depois de mudar uma opção")
        self.last_rows = {}
        for k, title in (("latency", "Atraso medido"), ("gen", "Geração de quadros"), ("missed", "Fora do ritmo"),
                         ("copy", "Cópia para a GPU"), ("lead", "Antecedência"), ("game", "Jogo detectado")):
            row = Adw.ActionRow(title=title)
            row.add_css_class("property")
            self.last_group.add(row)
            self.last_rows[k] = row
        self.hist_exp = Adw.ExpanderRow(title="Sessões anteriores")
        self.hist_rows = []
        self.last_group.add(self.hist_exp)
        clear = Gtk.Button(icon_name="edit-clear-all-symbolic", tooltip_text="Limpar o histórico",
                           valign=Gtk.Align.CENTER)
        clear.add_css_class("flat")
        clear.connect("clicked", self.on_clear_history)
        self.last_group.set_header_suffix(clear)
        page.add(self.last_group)
        self.show_history()
        return page

    def page_image(self):
        page = Adw.PreferencesPage()
        hdr = Adw.PreferencesGroup(title="HDR e cor",
                                   description="A Elgato não manda metadados HDR; o fmp manda estes para a TV")
        hdr.add(self.combo("hdr", "HDR", [
            ("on", "Ligado (HDR10)"), ("follow", "Seguir o console"), ("off", "Desligado (SDR)"),
            ("auto", "Como a placa informa"), ("10bit", "Ligado em toda captura 10 bits")],
            "H troca SDR/HDR10 durante o jogo; A volta a seguir"))
        hdr.add(self.spin("peak", "Pico (nits)", 100, 10000, 100, 0,
                          "Ponha o pico usado na calibração HDR do PS5. Alto (3000–4000): a TV comprime os claros"))
        hdr.add(self.optional_spin("maxcll", "MaxCLL próprio", 100, 10000, 100, 0,
                                   "Desligado: igual ao pico"))
        hdr.add(self.spin("maxfall", "MaxFALL (nits)", 0, 10000, 50, 0, "0: não informar"))
        hdr.add(self.toggles("range", "Faixa YCbCr",
                             [("auto", "Auto"), ("limited", "Limitada"), ("full", "Completa")],
                             "O PS5 manda limitada"))
        hdr.add(self.toggles("matrix", "Matriz",
                             [("auto", "Auto"), ("bt2020", "BT.2020"), ("bt709", "BT.709"), ("bt601", "BT.601")]))
        b = Adw.ButtonRow(title="Mostrar o padrão de pico na TV (20 s)",
                          start_icon_name="display-brightness-symbolic")
        b.connect("activated", lambda *_: self.run_tool("peak"))
        hdr.add(b)
        page.add(hdr)
        return page

    def page_capture(self):
        page = Adw.PreferencesPage()
        src = Adw.PreferencesGroup(title="Placa de captura")
        devs = [("", "Automático (a primeira com modo 10 bits)")] + self.devices
        src.add(self.combo("device", "Dispositivo", devs))
        src.add(self.combo("capture_fps", "Taxa pedida à placa", [
            ("", "A do modo (29,97 ou 59,94 do PS5)"), ("29.97", "29,97"), ("30", "30"),
            ("59.94", "59,94"), ("60", "60")],
            "30 ou 60 só para fontes a 60,000 Hz (PC)"))
        lst = Adw.ButtonRow(title="Listar dispositivos e formatos", start_icon_name="view-list-symbolic")
        lst.connect("activated", lambda *_: self.run_tool("list"))
        src.add(lst)
        page.add(src)

        self.custom_group = Adw.PreferencesGroup(
            title="Modo personalizado",
            description="Vale quando o modo de captura é “Personalizado”")
        self.custom_group.add(self.entry("custom_size", "Tamanho (LxA)", size=True))
        self.custom_group.add(self.combo("custom_fps", "Taxa", [
            ("", "A maior que a placa oferece"), ("29.97", "29,97"), ("30", "30"),
            ("59.94", "59,94"), ("60", "60"), ("120", "120")]))
        self.custom_group.add(self.combo("custom_format", "Formato", [
            ("", "P010 primeiro"), ("p010", "P010 (10 bits)"), ("nv12", "NV12 (8 bits)"),
            ("yuyv", "YUYV")]))
        page.add(self.custom_group)

        buf = Adw.PreferencesGroup(title="Buffers e quadros repetidos")
        buf.add(self.spin("buffers", "Buffers de captura", 2, 16, 1, 0))
        buf.add(self.combo("buffer_mode", "Modo dos buffers", [
            ("auto", "Automático"), ("userptr", "USERPTR (memória pinned)"),
            ("userptr-registered", "USERPTR registrado"), ("mmap", "MMAP")]))
        buf.add(self.switch("cadence", "Detectar quadros repetidos",
                            "Desligado: todo quadro capturado conta como novo"))
        page.add(buf)
        return page

    def page_display(self):
        page = Adw.PreferencesPage()
        tvg = Adw.PreferencesGroup(
            title="Tela do jogo",
            description="Onde o jogo abre em tela cheia. Vale para qualquer monitor ou TV ligado ao computador")
        self.tv_now = Adw.ActionRow(title="Agora", subtitle="")
        self.tv_now.add_css_class("property")
        refresh = Gtk.Button(icon_name="view-refresh-symbolic", valign=Gtk.Align.CENTER,
                             tooltip_text="Ler as telas de novo")
        refresh.add_css_class("flat")
        refresh.connect("clicked", lambda *_: self.reload_display())
        self.tv_now.add_suffix(refresh)
        tvg.add(self.tv_now)
        conns = [(c, screen_label(c, m)) for c, m in self.display["monitors"].items()]
        if self.values["tv_connector"] and self.values["tv_connector"] not in dict(conns):
            conns.append((self.values["tv_connector"], self.values["tv_connector"]))
        tvg.add(self.combo("tv_connector", "Tela", conns or [("", "Nenhuma tela encontrada")]))
        tvg.add(self.switch("game_output", "Abrir o jogo nesta tela",
                            "Desligado: abre onde está o mouse (o GNOME decide)"))
        tvg.add(self.switch("tv_apply", "Ajustar o modo de cor antes de jogar",
                            "Muda o HDR/SDR desta tela pelo Mutter e volta ao parar"))
        tvg.add(self.toggles("tv_color", "Modo de cor",
                             [("hdr", "HDR"), ("sdr", "SDR")],
                             "HDR: cores do PS5. SDR: o Mutter compõe mais liso, menor atraso"))
        tvg.add(self.switch("tv_vrr", "Taxa variável (VRR)",
                            "O Mutter 51 não deu VRR à janela do fmp nos testes; ligar só muda o modo da tela"))
        page.add(tvg)

        self.others_group = Adw.PreferencesGroup(
            title="Outras telas durante o jogo",
            description="Ligadas, você usa outros programas nelas (OBS, chat). Desligar deixa o ritmo mais liso, "
                        "mas desligar e religar telas já deixou o GNOME instável ao parar (22/11/2026)")
        self.others_rows = []
        page.add(self.others_group)
        self.restore_group = Adw.PreferencesGroup()
        self.restore_group.add(self.switch("tv_restore", "Voltar as telas como estavam ao parar"))
        page.add(self.restore_group)
        self.build_others()

        win = Adw.PreferencesGroup(title="Janela",
                                   description="O ritmo dos quadros (antecedência, latch, VRR) fica em Geração de quadros")
        win.add(self.entry("window", "Tamanho da janela fora da tela cheia (LxA)", size=True))
        page.add(win)
        return page

    def build_others(self):
        """Uma linha por tela conectada, menos a do jogo: ligada ou desligada durante o jogo."""
        for row in self.others_rows:
            self.others_group.remove(row)
        self.others_rows = []
        game = self.values["tv_connector"]
        others = [(c, m) for c, m in self.display["monitors"].items() if c != game]
        if not others:
            row = Adw.ActionRow(title="Nenhuma outra tela conectada")
            row.add_css_class("dim-label")
            self.others_group.add(row)
            self.others_rows.append(row)
            return
        active = active_connectors(self.display)
        for conn, m in others:
            sub = m["mode"] if conn in active and m["mode"] else "desligada agora"
            row = Adw.SwitchRow(title=screen_label(conn, m), subtitle=f"{sub} · ligada durante o jogo")
            row.set_active(conn not in self.values["others_off"])
            row.connect("notify::active", lambda r, _p, c=conn: self.set_other(c, r.get_active()))
            self.others_group.add(row)
            self.others_rows.append(row)

    def set_other(self, conn, keep_on):
        off = [c for c in self.values["others_off"] if c != conn]
        if not keep_on:
            off.append(conn)
        self.changed("others_off", off)

    def plan_off(self):
        """As outras telas que serão desligadas de fato (conectadas e ligadas agora)."""
        active = active_connectors(self.display)
        game = self.values["tv_connector"]
        return [c for c in self.values["others_off"] if c != game and c in active]

    def others_on_during_game(self):
        game = self.values["tv_connector"]
        off = set(self.plan_off())
        return [c for c in active_connectors(self.display) if c != game and c not in off]

    def output_arg(self):
        """--output para o fmp: a posição da tela do jogo no desktop (única mesmo com dois monitores iguais)."""
        v = self.values
        if not v["game_output"] or not v["tv_connector"]:
            return None
        pos = logical_position(self.display, v["tv_connector"])
        if pos is None:
            m = self.display["monitors"].get(v["tv_connector"])
            return m["name"] if m and m.get("name") else None
        return f"@{pos[0]},{pos[1]}"

    def page_audio(self):
        page = Adw.PreferencesPage()
        g = Adw.PreferencesGroup(title="Som da placa",
                                 description="O som é atrasado para casar com a imagem, medido ao vivo")
        g.add(self.toggles("audio", "Áudio", [("auto", "Auto"), ("on", "Ligado"), ("off", "Desligado")]))
        sinks = [("", "Saída padrão do sistema")] + self.sinks
        if self.values["sink"] and self.values["sink"] not in dict(sinks):
            sinks.append((self.values["sink"], self.values["sink"]))
        g.add(self.combo("sink", "Saída", sinks))
        srcs = [("", "O da placa de captura (automático)")] + self.sources
        if self.values["source"] and self.values["source"] not in dict(srcs):
            srcs.append((self.values["source"], self.values["source"]))
        g.add(self.combo("source", "Entrada", srcs))
        g.add(self.spin("audio_offset", "Ajuste fino (ms)", -200, 500, 5, 0,
                        "Positivo atrasa o som (processamento da TV, da placa)"))
        page.add(g)
        return page

    def page_system(self):
        page = Adw.PreferencesPage()
        g = Adw.PreferencesGroup(title="Onde está o fmp")
        g.add(self.entry("box", "Distrobox"))
        g.add(self.entry("fmp_path", "Binário do fmp"))
        g.add(self.entry("sdk", "VFX SDK (visto de dentro da distrobox)"))
        page.add(g)

        pg = Adw.PreferencesGroup(
            title="PCIe da placa de vídeo",
            description="Cada quadro capturado vai da memória para a placa de vídeo pelo PCIe. Em Gen1 isso leva cerca "
                        "de 14 ms em 4K HDR; em Gen4, cerca de 1 ms")
        self.pcie_card = Adw.ActionRow(title="Placa")
        self.pcie_card.add_css_class("property")
        pg.add(self.pcie_card)
        self.pcie_now = Adw.ActionRow(title="Link agora")
        self.pcie_now.add_css_class("property")
        ref = Gtk.Button(icon_name="view-refresh-symbolic", valign=Gtk.Align.CENTER, tooltip_text="Ler de novo")
        ref.add_css_class("flat")
        ref.connect("clicked", lambda *_: self.reload_link())
        self.pcie_now.add_suffix(ref)
        pg.add(self.pcie_now)
        self.pcie_game = Adw.ActionRow(title="No último jogo")
        self.pcie_game.add_css_class("property")
        pg.add(self.pcie_game)
        self.pcie_fix = Adw.ButtonRow(title="Liberar a velocidade máxima do PCIe (pede senha)",
                                      start_icon_name="emblem-synchronizing-symbolic")
        self.pcie_fix.connect("activated", self.on_pcie_fix)
        pg.add(self.pcie_fix)
        page.add(pg)

        d = Adw.PreferencesGroup(title="Diagnóstico")
        d.add(self.optional_spin("duration", "Parar depois de (s)", 5, 3600, 5, 0,
                                 "Desligado: roda até Esc ou Parar"))
        d.add(self.spin("warmup", "Aquecimento fora das estatísticas (s)", 0, 60, 0.5, 1))
        d.add(self.entry("csv", "Gravar telemetria CSV em (vazio: não grava)"))
        d.add(self.entry("extra", "Opções extras do fmp"))
        page.add(d)

        c = Adw.PreferencesGroup(title="Comando que será executado")
        copy = Gtk.Button(icon_name="edit-copy-symbolic", tooltip_text="Copiar")
        copy.add_css_class("flat")
        copy.connect("clicked", lambda *_: self.copy_text(self.cmd_label.get_text(), "Comando copiado"))
        c.set_header_suffix(copy)
        self.cmd_label = Gtk.Label(xalign=0, wrap=True, selectable=True,
                                   wrap_mode=Pango.WrapMode.WORD_CHAR, margin_top=10, margin_bottom=10,
                                   margin_start=12, margin_end=12)
        self.cmd_label.add_css_class("cmd")
        box = Gtk.Box()
        box.add_css_class("card")
        box.append(self.cmd_label)
        c.add(box)
        page.add(c)
        return page

    # ---------------------------------------------------------------- controles

    def changed(self, key, value):
        if self.values.get(key) == value:
            return
        self.values[key] = value
        if key == "tv_connector" and hasattr(self, "others_group"):
            self.build_others()
        self.refresh_all()
        self.schedule_save()

    def combo(self, key, title, options, subtitle=None):
        options = list(options)
        row = Adw.ComboRow(title=title)
        if subtitle:
            row.set_subtitle(subtitle)
        row.set_model(Gtk.StringList.new([lab for _v, lab in options]))
        vals = [v for v, _l in options]

        def setv(v):
            row.set_selected(vals.index(v) if v in vals else 0)
        setv(self.values[key])
        if self.values[key] not in vals:
            self.values[key] = vals[0]
        row.connect("notify::selected", lambda r, _p: self.changed(key, vals[r.get_selected()]))
        self.setters[key] = setv
        self.rows[key] = row
        return row

    def toggles(self, key, title, options, subtitle=None):
        row = Adw.ActionRow(title=title)
        if subtitle:
            row.set_subtitle(subtitle)
        tg = Adw.ToggleGroup(valign=Gtk.Align.CENTER)
        tg.add_css_class("round")
        for v, lab in options:
            tg.add(Adw.Toggle(name=v, label=lab))
        names = [v for v, _l in options]
        if self.values[key] not in names:
            self.values[key] = names[0]
        tg.set_active_name(self.values[key])
        tg.connect("notify::active-name", lambda g, _p: g.get_active_name() and self.changed(key, g.get_active_name()))
        row.add_suffix(tg)
        self.setters[key] = lambda v: tg.set_active_name(v)
        self.rows[key] = row
        return row

    def spin(self, key, title, lo, hi, step, digits, subtitle=None):
        row = Adw.SpinRow.new_with_range(lo, hi, step)
        row.set_title(title)
        row.set_digits(digits)
        if subtitle:
            row.set_subtitle(subtitle)
        row.set_value(self.values[key])
        is_float = isinstance(DEFAULTS[key], float)
        row.connect("notify::value", lambda r, _p: self.changed(
            key, round(r.get_value(), digits) if is_float else int(r.get_value())))
        self.setters[key] = lambda v: row.set_value(v)
        self.rows[key] = row
        return row

    def optional_spin(self, key, title, lo, hi, step, digits, subtitle=None):
        on_key = key + "_on"
        exp = Adw.ExpanderRow(title=title, show_enable_switch=True)
        if subtitle:
            exp.set_subtitle(subtitle)
        exp.set_enable_expansion(self.values[on_key])
        exp.set_expanded(self.values[on_key])
        exp.connect("notify::enable-expansion", lambda e, _p: self.changed(on_key, e.get_enable_expansion()))
        inner = self.spin(key, "Valor", lo, hi, step, digits)
        exp.add_row(inner)
        self.setters[on_key] = lambda v: (exp.set_enable_expansion(v), exp.set_expanded(v))
        self.rows[on_key] = exp
        return exp

    def switch(self, key, title, subtitle=None):
        row = Adw.SwitchRow(title=title)
        if subtitle:
            row.set_subtitle(subtitle)
        row.set_active(self.values[key])
        row.connect("notify::active", lambda r, _p: self.changed(key, r.get_active()))
        self.setters[key] = lambda v: row.set_active(v)
        self.rows[key] = row
        return row

    def entry(self, key, title, size=False):
        row = Adw.EntryRow(title=title)
        row.set_text(self.values[key])

        def on_change(r):
            t = r.get_text().strip()
            if size:
                if valid_size(t):
                    r.remove_css_class("error")
                else:
                    r.add_css_class("error")
                    return
            self.changed(key, t)
        row.connect("changed", on_change)
        self.setters[key] = lambda v: row.set_text(v)
        self.rows[key] = row
        return row

    def set_value(self, key, value):
        if key in self.setters:
            self.setters[key](value)
        self.values[key] = value

    # ---------------------------------------------------------------- estado

    def refresh_all(self):
        v = self.values
        custom = v["mode"] == "custom"
        if hasattr(self, "custom_group"):
            self.custom_group.set_sensitive(custom)
        if "capture_fps" in self.rows:
            self.rows["capture_fps"].set_sensitive(not custom and v["mode"] != "auto")
        if "window" in self.rows:
            self.rows["window"].set_sensitive(not v["fullscreen"])
        custom_rate = v["fg_target"] == "custom"
        if "multiplier" in self.rows:
            self.rows["multiplier"].set_sensitive(custom_rate and not v["output_hz_on"])
        if "output_hz_on" in self.rows:
            self.rows["output_hz_on"].set_sensitive(custom_rate)
        if "lead_max" in self.rows:
            self.rows["lead_max"].set_sensitive(v["lead_policy"] == "balanced")
        if hasattr(self, "quality_group"):
            self.quality_group.set_sensitive(v["generator"] in ("auto", "vfg"))
        for k in ("tv_color", "tv_vrr"):
            if k in self.rows:
                self.rows[k].set_sensitive(v["tv_apply"])
        if "tv_restore" in self.rows:
            self.rows["tv_restore"].set_sensitive(v["tv_apply"] or bool(v["others_off"]))
        for k in ("peak", "maxcll_on", "maxfall", "range", "matrix"):
            if k in self.rows:
                self.rows[k].set_sensitive(v["hdr"] != "off" or k in ("range", "matrix"))
        self.update_generation()
        if hasattr(self, "cmd_label"):
            self.cmd_label.set_text(shlex.join(self.build_argv("capture")))
        self.update_chips()
        self.update_pcie()
        if hasattr(self, "tv_now"):
            self.tv_now.set_subtitle(describe_tv(self.display, v["tv_connector"]))
        if hasattr(self, "banner"):
            self.update_banner()

    # ---------------------------------------------------------------- geração de quadros

    def update_generation(self):
        """Recalcula a conta do atraso e reescreve o resumo, as explicações de cada opção e a linha do Início."""
        v = self.values
        hz = screen_hz(self.display, v["tv_connector"])
        est = estimate(v, hz, self.measures)
        self.est = est
        if not hasattr(self, "fg_rate"):
            return
        game = est["game"]
        gen_name = {"auto": "VFG", "vfg": "VFG", "blend": "Mistura", "repeat": "Repetição",
                    "none": "sem gerador"}[v["generator"]]
        if gen_name == "VFG":
            gen_name += " " + QUALITY_NAMES[v["quality"]]
        policy = {p: t for p, t, _s in LEAD_POLICIES}[v["lead_policy"]]
        if est["gen"]:
            n = est["mult"] - 1
            rate = f"{round(game)} → {round(game * est['mult'])} fps"
            what = f"{n} quadro{'s' if n > 1 else ''} gerado{'s' if n > 1 else ''} por quadro do jogo"
        else:
            rate = f"{round(game)} fps, sem geração"
            what = "a geração não vai ligar" if est["wants"] else "nada a gerar com esta escolha"
        self.fg_rate.set_label(rate)
        self.fg_sub.set_label(f"{what} · {gen_name} · ritmo: {policy}")
        self.fg_total.set_label(f"≈ {fmt_ms(est['total'])} ms")
        self.fg_total_sub.set_label("da captura à tela" + ("" if est["copy_measured"] else " (cópia estimada)"))
        self.fg_bar.queue_draw()
        while (c := self.fg_legend.get_first_child()) is not None:
            self.fg_legend.remove(c)
        for label, ms, kind in est["parts"]:
            item = Gtk.Box(spacing=6)
            dot = Gtk.Box(valign=Gtk.Align.CENTER)
            dot.add_css_class("legend-dot")
            dot.add_css_class(kind)
            item.append(dot)
            lab = Gtk.Label(label=f"{label} {fmt_ms(ms)}")
            lab.add_css_class("legend")
            item.append(lab)
            self.fg_legend.append(item)
        self.fg_warn.set_label("\n".join(est["warns"]))
        self.fg_warn.set_visible(bool(est["warns"]))
        self.fg_notes.set_label("\n".join(est["notes"]))
        last = self.history[-1] if self.history else None
        self.fg_last.set_label(self.session_line(last, long=True) if last else
                               "Ainda sem sessão medida: rode e pare com Esc ou Parar para ver o atraso real")

        # As explicações de cada escolha, com o custo em ms.
        t_ms = est["t"]
        g = round(GAME_RATES.get(v["game_fps"], 29.97))
        for target, (row, _chk) in self.radio_rows.get("fg_target", {}).items():
            if target == "custom":
                continue
            m = max(1, round(int(target) / g))
            if m < 2:
                sub = f"O jogo já está a {g}: sem quadros gerados"
            else:
                hold = (m - 1) / m * t_ms
                sub = (f"{m - 1} gerado{'s' if m > 2 else ''} por quadro do jogo · espera de {fmt_ms(hold)} ms")
                if target == "120" and g == 30:
                    sub += f" (+{fmt_ms(hold - t_ms / 2)} ms em relação a 60)"
                if hz and int(target) > hz * 1.01:
                    sub += f" · a tela do jogo está a {fmt_num(hz)} Hz"
            row.set_subtitle(sub)
        budget = t_ms / est["mult"] if est["mult"] >= 2 else t_ms / 2
        res = res_key(est["height"])
        rlab = res_label(est["height"])
        for q, (row, _chk) in self.radio_rows.get("quality", {}).items():
            d = vfg_p99(self.measures, est["height"], q)
            local = self.measures.get("vfg", {}).get(str(res), {}).get(q) is not None
            if d is None:
                sub = (f"Não medida em {rlab}. Mais artefatos; deve ganhar menos de 1 ms" if q == "low"
                       else f"Não medida em {rlab} ainda")
            else:
                src = "medido aqui" if local else "medido numa RTX 4080 Super"
                sub = f"≈ {fmt_ms(d)} ms por quadro gerado em {rlab} ({src})"
                if d > budget:
                    sub += f" · passa do limite de {fmt_ms(budget)} ms: a geração desliga"
                elif q == "high":
                    sub += f" · cabe, mas soma {fmt_ms(d - (vfg_p99(self.measures, est['height'], 'medium') or 2.0))} ms ao atraso"
            row.set_subtitle(sub)
        base = lead_estimate(dict(v, lead_policy="smooth"), hz or 60.0, self.measures.get("last"))[0]
        for p, (row, _chk) in self.radio_rows.get("lead_policy", {}).items():
            lead, txt = lead_estimate(dict(v, lead_policy=p), hz or 60.0, self.measures.get("last"))
            desc = {k: s for k, _t, s in LEAD_POLICIES}[p]
            delta = f" · ≈ −{fmt_ms(base - lead)} ms em relação a “Mais liso”" if p != "smooth" and lead < base else ""
            row.set_subtitle(f"Antecedência {txt}{delta}. {desc}")
        if "game_fps" in self.rows:
            seen = last.get("game_fps") if last else None
            self.rows["game_fps"].set_subtitle("O fmp detecta sozinho; isto escolhe quantos quadros gerar"
                                               + (f". Último jogo: {fmt_num(seen)} fps" if seen else ""))
        if hasattr(self, "home_fg"):
            self.home_fg.set_subtitle(f"{rate} · {gen_name} · ritmo: {policy} · ≈ {fmt_ms(est['total'])} ms estimados")
            self.home_last.set_subtitle(self.session_line(last) if last else "Nenhuma ainda")

    def draw_bar(self, _area, cr, width, height):
        est = self.est
        if not est or width <= 0 or not est["parts"]:
            return
        total = est["total"] or 1.0
        x = 0.0
        n = len(est["parts"])
        for i, (_label, ms, kind) in enumerate(est["parts"]):
            w = width * ms / total
            cr.set_source_rgb(*PART_COLORS[kind])
            cr.rectangle(x, 0, max(1.0, w - (2 if i < n - 1 else 0)), height)
            cr.fill()
            x += w

    def session_line(self, s, long=False):
        """Uma linha com o principal de uma sessão medida."""
        try:
            when = time.strftime("%d/%m %H:%M", time.strptime(s.get("date", ""), "%Y-%m-%dT%H:%M:%S"))
        except ValueError:
            when = s.get("date", "?")
        parts = [f"{fmt_ms(s['latency'])} ms medidos" + (f" (p99 {fmt_ms(s.get('latency_p99'))})" if long else "")]
        share = fg_share(s)
        if share is not None:
            parts.append(f"geração {round(share * 100)}%")
        if s.get("missed") is not None and s.get("slots"):
            parts.append(f"{fmt_num(round(100 * s['missed'] / s['slots'], 1))}% fora do ritmo")
        head = f"Última sessão ({when}): " if long else f"{when} · "
        return head + " · ".join(parts)

    def show_history(self):
        """A última sessão em detalhe e as anteriores numa lista, para comparar."""
        if not hasattr(self, "last_rows"):
            return
        rows = self.last_rows
        last = self.history[-1] if self.history else None
        if not last:
            for row in rows.values():
                row.set_subtitle("—")
            rows["latency"].set_subtitle("Nenhuma sessão medida ainda: rode e pare com Esc ou Parar")
        else:
            prev = self.history[-2] if len(self.history) > 1 else None
            delta = ""
            if prev:
                d = last["latency"] - prev["latency"]
                delta = f" · {'+' if d >= 0 else '−'}{fmt_ms(abs(d))} ms em relação à anterior"
            rows["latency"].set_subtitle(f"{fmt_ms(last['latency'])} ms da captura à tela (p99 "
                                         f"{fmt_ms(last.get('latency_p99'))}){delta}")
            share = fg_share(last)
            if share is None:
                rows["gen"].set_subtitle("—")
            else:
                txt = f"ligada {round(share * 100)}% do tempo ({last.get('generated', 0)} gerados, {last.get('real', 0)} reais)"
                if share < 0.8:
                    txt += ": a qualidade não coube ou o multiplicador caiu"
                rows["gen"].set_subtitle(txt)
            if last.get("missed") is not None and last.get("slots"):
                rows["missed"].set_subtitle(f"{fmt_num(round(100 * last['missed'] / last['slots'], 1))}% dos refreshes "
                                            f"({int(last['missed'])} de {last['slots']})")
            else:
                rows["missed"].set_subtitle("—")
            up = last.get("upload")
            pc = last.get("pcie_max")
            copy = f"{fmt_ms(up)} ms por quadro" if up else "—"
            if pc:
                copy += f" · PCIe chegou a {gen_label(pc)}"
                if pc <= 2.5 and (last.get("pcie_cap") or 0) > 2.5:
                    copy += " (preso: veja Sistema)"
            rows["copy"].set_subtitle(copy)
            if last.get("lead_end") is not None:
                lt = f"terminou em {fmt_ms(last['lead_end'])} ms"
                if last.get("lead_fixed"):
                    lt += " (fixa)"
                elif last.get("lead_peak") and last["lead_peak"] > last["lead_end"] + 0.05:
                    lt += f", pico de {fmt_ms(last['lead_peak'])} ms"
                rows["lead"].set_subtitle(lt)
            else:
                rows["lead"].set_subtitle("—")
            rows["game"].set_subtitle(f"{fmt_num(last['game_fps'])} fps" if last.get("game_fps") else "—")
        for row in self.hist_rows:
            self.hist_exp.remove(row)
        self.hist_rows = []
        older = list(reversed(self.history[:-1]))[:15]
        self.hist_exp.set_subtitle(f"{len(self.history)} no histórico" if self.history else "Nenhuma")
        for s in older:
            conf = [s.get("mode", "?"), QUALITY_NAMES.get(s.get("quality"), "?"),
                    f"{s.get('fg_target', '?')} fps" if s.get("fg_target") not in (None, "custom") else "taxa à mão",
                    "ritmo " + {p: t for p, t, _s in LEAD_POLICIES}.get(s.get("lead_policy"), "?")]
            row = Adw.ActionRow(title=self.session_line(s), subtitle=" · ".join(conf))
            self.hist_exp.add_row(row)
            self.hist_rows.append(row)

    def on_clear_history(self, *_):
        d = Adw.AlertDialog(heading="Limpar o histórico de sessões?",
                            body="As medições guardadas somem. As contas voltam aos números da RTX 4080 Super.")
        d.add_response("cancel", "Cancelar")
        d.add_response("ok", "Limpar")
        d.set_response_appearance("ok", Adw.ResponseAppearance.DESTRUCTIVE)

        def resp(_d, r):
            if r == "ok":
                self.history = []
                try:
                    save_history(self.history)
                except OSError as e:
                    self.toast(f"Não consegui limpar: {e}")
                self.measures = measures_from(self.history)
                self.show_history()
                self.refresh_all()
        d.connect("response", resp)
        d.present(self)

    def session_config(self):
        """O que foi escolhido para a sessão, para o histórico."""
        v = self.values
        cap_fps, _s, _f = mode_spec(v)
        return {
            "date": time.strftime("%Y-%m-%dT%H:%M:%S"), "mode": v["mode"], "quality": v["quality"],
            "fg_target": v["fg_target"], "game_fps_choice": v["game_fps"], "mult": fg_multiplier(v, cap_fps),
            "generator": v["generator"], "lead_policy": v["lead_policy"], "lead_on": v["lead_on"],
            "lead": v["lead"], "lead_max": v["lead_max"], "clock": v["clock"], "tv_color": v["tv_color"],
            "tv_apply": v["tv_apply"], "others_on": len(self.others_on_during_game()),
            "estimate": round(self.est["total"], 1) if self.est else None,
        }

    def sample_pcie(self):
        if not self.link or not self.proc:
            self.pcie_timer = 0
            return False
        s = gts(read_text(f"{PCI_DIR}/{self.link['gpu']}/current_link_speed"))
        if s:
            self.pcie_seen.append(s)
        return True

    def maybe_collect(self):
        """Quando o fmp saiu e a saída acabou: lê o resumo, guarda no histórico e mostra."""
        if self.run_config is None or not (self.run_eof and self.run_exited):
            return
        cfg, self.run_config = self.run_config, None
        res = parse_summary("\n".join(self.run_lines))
        self.run_lines = []
        if not res:
            return
        res.update(cfg)
        if self.pcie_seen:
            res["pcie_max"] = max(self.pcie_seen)
            res["pcie_samples"] = len(self.pcie_seen)
        if self.link:
            res["pcie_cap"] = self.link["max"]
        self.history.append(res)
        try:
            save_history(self.history)
        except OSError as e:
            self.toast(f"Não consegui guardar a sessão: {e}")
        self.measures = measures_from(self.history)
        self.show_history()
        self.refresh_all()
        share = fg_share(res)
        msg = f"Atraso medido: {fmt_ms(res['latency'])} ms"
        if share is not None and share < 0.8:
            msg += f"; a geração ficou ligada só {round(share * 100)}% do tempo"
        self.toast(msg)

    # ---------------------------------------------------------------- PCIe

    def reload_link(self):
        self.link = gpu_link()
        if self.link and not self.link_name:
            self.link_name = gpu_name(self.link["gpu"])
        self.update_pcie()

    def update_pcie(self):
        if not hasattr(self, "pcie_card"):
            return
        link = self.link
        if not link:
            self.pcie_card.set_subtitle("Nenhuma placa de vídeo PCIe encontrada")
            self.pcie_now.set_subtitle("—")
            self.pcie_game.set_subtitle("—")
            self.pcie_fix.set_sensitive(False)
            return
        self.pcie_card.set_subtitle(f"{self.link_name} ({link['gpu']}, porta {link['port'] or '?'})")
        self.pcie_now.set_subtitle(f"{gen_label(link['now'])} de até {gen_label(link['max'])}, x{link['width']}. "
                                   "Parada, a NVIDIA baixa para Gen1 sozinha: o que conta é durante o jogo")
        last = next((s for s in reversed(self.history) if s.get("pcie_max")), None)
        if last:
            stuck = last["pcie_max"] <= 2.5 and (last.get("pcie_cap") or 0) > 2.5
            txt = ("ficou em Gen1 o tempo todo" if stuck else f"chegou a {gen_label(last['pcie_max'])}")
            if last.get("upload"):
                txt += f"; a cópia levou {fmt_ms(last['upload'])} ms por quadro"
            if stuck:
                txt += ". O teto da porta deve estar em Gen1 (veja LATENCIA.md, seção 3.A)"
            self.pcie_game.set_subtitle(txt)
        else:
            self.pcie_game.set_subtitle("Ainda sem medição durante um jogo")
        fixed = getattr(self, "pcie_fix_result", None)
        self.pcie_fix.set_sensitive(bool(link.get("cdev")) and not self.proc)
        if fixed:
            self.pcie_fix.set_title(f"Liberar a velocidade máxima do PCIe (pede senha) · {fixed}")
        if not link.get("cdev"):
            self.pcie_fix.set_title("Liberar a velocidade do PCIe: o kernel não tem o controle desta porta")

    def on_pcie_fix(self, *_):
        link = self.link or {}
        cdev, port = link.get("cdev"), link.get("port")
        if not (cdev and port and re.fullmatch(r"/sys/class/thermal/cooling_device\d+", cdev) and BDF_RE.match(port)):
            self.toast("O kernel não oferece o controle de velocidade desta porta")
            return
        d = Adw.AlertDialog(
            heading="Liberar a velocidade do PCIe?",
            body=("O HypeFrame pede ao kernel a velocidade máxima da porta da placa de vídeo (o controle oficial "
                  f"“{os.path.basename(cdev)}”, estado 0). O kernel retreina o link, então a imagem pode piscar: salve o "
                  "que estiver aberto e feche o fmp e o OBS.\n\nVale até reiniciar o PC. Depois, rode um jogo e veja a "
                  "cópia para a GPU na Última sessão."))
        d.add_response("cancel", "Cancelar")
        d.add_response("ok", "Liberar (pede senha)")
        d.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)
        d.set_close_response("cancel")

        def resp(_d, r):
            if r != "ok":
                return
            show = f"lspci -vv -s {port} | grep -E 'LnkCtl2|LnkSta:' | tr -s '[:space:]' ' '"
            script = f"echo antes: $({show}); echo 0 > {cdev}/cur_state; sleep 1; echo depois: $({show})"
            try:
                proc = Gio.Subprocess.new(["pkexec", "/bin/sh", "-c", script],
                                          Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_MERGE)
            except GLib.Error as e:
                self.alert("Não consegui pedir a senha", e.message)
                return
            self.log("[HypeFrame] $ pkexec /bin/sh -c " + shlex.quote(script))
            proc.communicate_utf8_async(None, None, self.on_pcie_fixed)
        d.connect("response", resp)
        d.present(self)

    def on_pcie_fixed(self, proc, res):
        try:
            _ok, out, _err = proc.communicate_utf8_finish(res)
        except GLib.Error as e:
            out = e.message
        out = out or ""
        self.log(out.strip())
        code = proc.get_exit_status() if proc.get_if_exited() else -1
        if code in (126, 127):
            self.toast("Cancelado: nada mudou")
            return
        before = re.search(r"antes:.*?Target Link Speed: ([0-9.]+)GT/s", out)
        after = re.search(r"depois:.*?Target Link Speed: ([0-9.]+)GT/s", out)
        if after:
            b = f"{fmt_num(float(before.group(1)))} → " if before else ""
            self.pcie_fix_result = f"teto da porta: {b}{fmt_num(float(after.group(1)))} GT/s"
            self.toast(f"PCIe: {self.pcie_fix_result}. Rode um jogo para medir a cópia")
        else:
            self.pcie_fix_result = "sem confirmação (veja a saída do fmp)"
            self.toast("Não consegui confirmar o teto da porta (veja a saída)")
        self.reload_link()

    def update_chips(self):
        if self.chip_box is None:
            return
        while (c := self.chip_box.get_first_child()) is not None:
            self.chip_box.remove(c)
        v = self.values
        mode = next((m for m in MODES if m[0] == v["mode"]), None)
        chips = [(mode[1] if mode else v["mode"], "")]
        est = self.est
        if est and est["gen"]:
            chips.append((f"{round(est['game'])} → {round(est['game'] * est['mult'])} fps", "violet"))
        elif est:
            chips.append(("sem geração" if not est["wants"] else "geração não cabe", "violet"))
        gen = {"auto": "VFG", "vfg": "VFG", "blend": "Mistura", "repeat": "Repetição", "none": "Sem geração"}[v["generator"]]
        if gen == "VFG":
            gen += " " + QUALITY_NAMES[v["quality"]]
        chips.append((gen, ""))
        if est:
            chips.append((f"≈ {fmt_ms(est['total'])} ms", "violet"))
        if v["hdr"] != "off":
            chips.append((f"HDR {v['peak']} nits", "violet"))
        if v["tv_apply"]:
            chips.append((f"Tela em {'HDR' if v['tv_color'] == 'hdr' else 'SDR'}"
                          + (" + VRR" if v["tv_vrr"] else ""), ""))
        on = len(self.others_on_during_game())
        off = len(self.plan_off())
        if on or off:
            chips.append((f"{on} outra(s) tela(s) ligada(s)" if not off else
                          f"{off} tela(s) desligada(s) no jogo", ""))
        chips.append(("Tela cheia" if v["fullscreen"] else "Janela", "violet"))
        for text, cls in chips:
            lab = Gtk.Label(label=text)
            lab.add_css_class("chip")
            if cls:
                lab.add_css_class(cls)
            self.chip_box.append(lab)

    def update_banner(self):
        pending = self.extra.get("pending_restore")
        if pending and not self.proc:
            self.banner.set_title("A tela ficou como no último jogo")
            self.banner.set_button_label("Voltar como estava")
            self.banner.kind = "restore"
            self.banner.set_revealed(True)
            return
        est = self.est
        if est and est["wants"] and not est["fits"] and self.values["quality"] != "medium":
            medium = vfg_p99(self.measures, est["height"], "medium")
            if medium is None or medium <= est["t"] / est["mult"]:
                self.banner.set_title(f"A qualidade {QUALITY_NAMES[self.values['quality']]} não cabe aqui: "
                                      "o fmp desligaria a geração de quadros")
                self.banner.set_button_label("Usar a Média")
                self.banner.kind = "quality"
                self.banner.set_revealed(True)
                return
        if self.others_on_during_game() and not self.values["lead_on"] and self.values["lead_policy"] != "low":
            self.banner.set_title("Outras telas ligadas: no GNOME o ritmo fica irregular sem antecedência inicial fixa")
            self.banner.set_button_label("Usar 5,5 ms")
            self.banner.kind = "lead"
            self.banner.set_revealed(True)
            return
        last = self.history[-1] if self.history else None
        if (last and not self.proc and last.get("pcie_max") and last["pcie_max"] <= 2.5
                and (last.get("pcie_cap") or 0) > 2.5):
            up = f": a cópia levou {fmt_ms(last['upload'])} ms por quadro" if last.get("upload") else ""
            self.banner.set_title(f"No último jogo o PCIe ficou em Gen1{up}")
            self.banner.set_button_label("Ver")
            self.banner.kind = "pcie"
            self.banner.set_revealed(True)
            return
        self.banner.set_revealed(False)

    def on_banner(self, _b):
        kind = getattr(self.banner, "kind", "")
        if kind == "restore":
            self.restore_display(self.extra.get("pending_restore"))
        elif kind == "quality":
            self.set_value("quality", "medium")
            self.refresh_all()
            self.schedule_save()
            self.toast("Qualidade Média: a geração fica ligada")
        elif kind == "pcie":
            self.go_page("sistema")
        else:
            self.set_value("lead", 5.5)
            self.set_value("lead_on", True)
            self.refresh_all()
            self.schedule_save()
            self.toast("Antecedência inicial de 5,5 ms (com dois monitores: de 17% para 1,5% de quadros fora do ritmo)")

    def reload_display(self):
        try:
            self.display = display_state()
        except GLib.Error as e:
            self.toast(f"Não li o estado dos monitores: {e.message}")
        if hasattr(self, "others_group"):
            self.build_others()
        self.refresh_all()

    def apply_profile(self, pid):
        for p, title, _s, _i, vals in PROFILES:
            if p == pid:
                for k, v in vals.items():
                    self.set_value(k, v)
                self.refresh_all()
                self.schedule_save()
                self.toast(f"Perfil “{title}” aplicado")

    def schedule_save(self):
        if self.save_id:
            GLib.source_remove(self.save_id)
        self.save_id = GLib.timeout_add(400, self.save_now)

    def save_now(self):
        self.save_id = 0
        try:
            save_config(self.values, self.extra)
        except OSError as e:
            self.toast(f"Não consegui salvar: {e}")
        return False

    def on_defaults(self, *_):
        d = Adw.AlertDialog(heading="Restaurar os padrões?",
                            body="Todos os controles voltam aos valores iniciais do HypeFrame.")
        d.add_response("cancel", "Cancelar")
        d.add_response("ok", "Restaurar")
        d.set_response_appearance("ok", Adw.ResponseAppearance.DESTRUCTIVE)

        def resp(_d, r):
            if r == "ok":
                keep_tv = self.values["tv_connector"]
                for k, v in DEFAULTS.items():
                    self.set_value(k, v)
                self.set_value("tv_connector", keep_tv)
                self.refresh_all()
                self.schedule_save()
                self.toast("Padrões restaurados")
        d.connect("response", resp)
        d.present(self)

    def on_about(self, *_):
        a = Adw.AboutDialog(
            application_name="HypeFrame", application_icon=APP_ID, version=VERSION,
            comments="Interface para o fmp (frame-motion-plus): captura do PS5 com geração de quadros NVIDIA VFG no GNOME.",
            website="https://github.com/adds39939/frame-motion-plus")
        a.present(self)

    # ---------------------------------------------------------------- comando

    def fmp_path(self):
        return os.path.expanduser(self.values["fmp_path"])

    def build_argv(self, kind):
        v = self.values
        argv = ["distrobox", "enter", "-n", v["box"] or "fmp", "-T", "--", "env"]
        if v["sdk"]:
            argv.append(f"FMP_VFX_SDK={v['sdk']}")
        argv.append(self.fmp_path())
        if kind == "list":
            return argv + ["capture", "--list"]
        out = self.output_arg()
        if kind == "peak":
            return argv + ["hdr-pattern", "peak", "--fullscreen", "--duration-s", "20"] + (
                ["--output", out] if out else []) + [
                           "--hdr-peak-nits", str(v["peak"])] + (
                ["--hdr-max-cll", str(v["maxcll"])] if v["maxcll_on"] else [])

        a = ["capture"]
        if v["device"]:
            a.append(v["device"])
        a.append("--no-settings")
        if v["mode"] == "custom":
            if valid_size(v["custom_size"]):
                a += ["--size", v["custom_size"]]
            if v["custom_fps"]:
                a += ["--fps", v["custom_fps"]]
            if v["custom_format"]:
                a += ["--format", v["custom_format"]]
        elif v["mode"] != "auto":
            a += ["--mode", v["mode"]]
            if v["capture_fps"]:
                a += ["--capture-fps", v["capture_fps"]]
        if v["generator"] != "auto":
            a += ["--generator", v["generator"]]
        a += ["--quality", v["quality"]]
        if v["fg_target"] == "custom" and v["output_hz_on"]:
            a += ["--output-hz", fmt_num(v["output_hz"]).replace(",", ".")]
        else:
            a += ["--multiplier", str(fg_multiplier(v, mode_spec(v)[0]))]
        a.append("--quality-switch" if v["quality_switch"] else "--no-quality-switch")
        a += ["--scd", v["scd"]]
        if v["cuda_sync"] != "auto":
            a += ["--cuda-sync", v["cuda_sync"]]
        if v["fullscreen"]:
            a.append("--fullscreen")
            if out:
                a += ["--output", out]
        else:
            a += ["--window", v["window"]]
        a += ["--clock", v["clock"]]
        a += self.lead_args()
        if v["latch_on"]:
            a += ["--latch-ms", fmt_num(v["latch"]).replace(",", ".")]
        a += ["--late-generated-ms", fmt_num(v["late_generated"]).replace(",", ".")]
        a += ["--ceiling-pacing", "on" if v["ceiling"] else "off"]
        a += ["--hdr", v["hdr"], "--hdr-peak-nits", str(v["peak"])]
        if v["maxcll_on"]:
            a += ["--hdr-max-cll", str(v["maxcll"])]
        if v["maxfall"]:
            a += ["--hdr-max-fall", str(v["maxfall"])]
        if v["range"] != "auto":
            a += ["--range", v["range"]]
        if v["matrix"] != "auto":
            a += ["--matrix", v["matrix"]]
        if not v["cadence"]:
            a.append("--no-cadence")
        if v["buffer_mode"] != "auto":
            a += ["--buffer-mode", v["buffer_mode"]]
        a += ["--buffers", str(v["buffers"])]
        a += ["--audio", v["audio"]]
        if v["sink"]:
            a += ["--audio-sink", v["sink"]]
        if v["source"]:
            a += ["--audio-source", v["source"]]
        if v["audio_offset"]:
            a += ["--audio-offset-ms", str(v["audio_offset"])]
        if v["duration_on"]:
            a += ["--duration-s", str(v["duration"])]
        if v["warmup"] != DEFAULTS["warmup"]:
            a += ["--warmup-s", fmt_num(v["warmup"]).replace(",", ".")]
        if v["csv"]:
            a += ["--csv", os.path.expanduser(v["csv"])]
        if v["extra"]:
            try:
                a += shlex.split(v["extra"])
            except ValueError:
                pass
        return argv + a

    def lead_args(self):
        """A prioridade do ritmo em opções do fmp: antecedência fixa, com teto, ou livre para subir."""
        v = self.values

        def ms(x):
            return fmt_num(x).replace(",", ".")
        start = v["lead"] if v["lead_on"] else None
        if v["lead_policy"] == "low":
            # Fixa precisa de um valor: sem ele o fmp ficaria em 1,5 refresh (12,5 ms a 120 Hz).
            return ["--present-lead-ms", ms(v["lead"]), "--lead-adapt", "off"]
        out = ["--present-lead-ms", ms(start)] if start is not None else []
        if v["lead_policy"] == "balanced":
            out += ["--lead-max-ms", ms(max(v["lead_max"], start or 0.0))]
        return out

    # ---------------------------------------------------------------- execução

    def toggle_run(self):
        if self.restore_timer:
            self.toast("Esperando as telas voltarem")
            return
        if self.proc or self.launch_timer:
            self.stop()
        else:
            self.start("capture")

    def run_tool(self, kind):
        if self.proc or self.launch_timer or self.restore_timer:
            self.toast("Pare o fmp antes")
            return
        self.start(kind)

    def check_ready(self, kind):
        path = self.fmp_path()
        if not os.path.exists(path):
            self.alert("fmp não encontrado",
                       f"Não achei o binário em\n{path}\n\nConfira em Sistema.")
            return False
        if kind == "peak":
            return True
        if self.values["device"]:
            devs = [self.values["device"]]
        else:
            devs = [d for d, lab in self.devices if "Elgato" in lab] or [d for d, _l in self.devices]
        held = holders(set(devs))
        if held:
            names = ", ".join(sorted(set(held.values())))
            self.alert("A placa de captura está em uso",
                       f"Quem está usando: {names}.\n\nFeche esse programa (por exemplo o OBS) e tente de novo: a placa atende um programa por vez.")
            return False
        return True

    def start(self, kind, confirmed=False):
        if not self.check_ready(kind):
            return
        est = self.est
        if kind == "capture" and not confirmed and est and est["wants"] and not est["fits"]:
            self.ask_quality(kind)
            return
        self.save_now()
        self.stopping = False
        if kind == "capture" and (self.values["tv_apply"] or self.plan_off()):
            if not self.prepare_display():
                return
            if self.restore_args is not None:
                self.set_status("busy", "Ajustando as telas", "Esperando as telas ressincronizarem")
                self.set_play(True)
                self.launch_timer = GLib.timeout_add(2500, self.after_display, kind)
                return
        self.launch(kind)

    def ask_quality(self, kind):
        """A qualidade escolhida não cabe no tempo de um quadro gerado: o fmp desligaria a geração."""
        q = self.values["quality"]
        warn = next((w for w in self.est["warns"] if "limite" in w), "")
        d = Adw.AlertDialog(
            heading="A geração de quadros vai desligar",
            body=f"{warn}.\n\nA Média gera cada quadro em cerca de 2 ms em 4K nesta placa e mantém a geração ligada.")
        d.add_response("cancel", "Cancelar")
        d.add_response("keep", f"Iniciar com a {QUALITY_NAMES[q]}")
        d.add_response("medium", "Usar a Média e iniciar")
        d.set_response_appearance("medium", Adw.ResponseAppearance.SUGGESTED)
        d.set_default_response("medium")
        d.set_close_response("cancel")

        def resp(_d, r):
            if r == "medium":
                self.set_value("quality", "medium")
                self.refresh_all()
                self.schedule_save()
                self.start(kind, confirmed=True)
            elif r == "keep":
                self.start(kind, confirmed=True)
        d.connect("response", resp)
        d.present(self)

    def after_display(self, kind):
        self.launch_timer = 0
        try:
            self.display = display_state()
        except GLib.Error:
            pass
        self.log("Tela do jogo agora: " + describe_tv(self.display, self.values["tv_connector"]))
        want_hdr = self.values["tv_color"] == "hdr"
        tv = self.display["monitors"].get(self.values["tv_connector"])
        if self.values["tv_apply"] and tv and (tv["color"] == 1) != want_hdr:
            self.toast("A tela do jogo não ficou no modo de cor pedido (veja a saída)")
        self.refresh_all()
        self.launch(kind)
        return False

    def prepare_display(self):
        self.restore_args = None
        try:
            cur = display_state()
        except GLib.Error as e:
            self.alert("Não consegui ler os monitores", e.message)
            return False
        conn = self.values["tv_connector"]
        if conn not in cur["monitors"]:
            self.alert("Tela do jogo não encontrada",
                       f"A tela {conn} não está conectada. Ligue-a ou escolha outra em Telas.")
            return False
        self.display = cur
        want = game_state(cur, conn, self.values["tv_apply"], self.values["tv_color"] == "hdr",
                          self.values["tv_vrr"], self.plan_off())
        if same_layout(cur, want):
            self.log("Tela já está como pedido: " + describe_tv(cur, conn))
            return True
        args = gdctl_args(want)
        self.log("$ " + shlex.join(args))
        try:
            r = subprocess.run(args, capture_output=True, text=True, timeout=20)
        except (OSError, subprocess.SubprocessError) as e:
            self.alert("gdctl falhou", str(e))
            return False
        if r.returncode != 0:
            self.log((r.stdout + r.stderr).strip())
            self.alert("O GNOME recusou a configuração das telas", (r.stderr or r.stdout).strip()[:600])
            return False
        if self.values["tv_restore"]:
            self.restore_args = gdctl_args(cur)
            self.extra["pending_restore"] = self.restore_args
        else:
            self.restore_args = []
        self.save_now()
        return True

    def restore_display(self, args):
        if not args:
            return
        self.log("Voltando a tela: $ " + shlex.join(args))
        try:
            r = subprocess.run(args, capture_output=True, text=True, timeout=20)
            ok = r.returncode == 0
            if not ok:
                self.log((r.stdout + r.stderr).strip())
        except (OSError, subprocess.SubprocessError) as e:
            ok = False
            self.log(str(e))
        if ok:
            self.extra.pop("pending_restore", None)
            self.toast("Tela de volta como estava")
        else:
            self.toast("Não consegui voltar a tela (veja a saída)")
        self.save_now()
        GLib.timeout_add(2500, lambda: (self.reload_display(), False)[1])

    def launch(self, kind):
        argv = self.build_argv(kind)
        self.log("")
        self.log("$ " + shlex.join(argv))
        try:
            self.proc = Gio.Subprocess.new(
                argv, Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_MERGE)
        except GLib.Error as e:
            self.alert("Não consegui iniciar", e.message)
            self.finish_display()
            self.set_play(False)
            return
        self.proc_kind = kind
        self.saw_busy = False
        self.partial = b""
        self.run_lines = []
        self.run_eof = self.run_exited = False
        self.run_config = self.session_config() if kind == "capture" else None
        self.pcie_seen = []
        if kind == "capture" and self.link and not self.pcie_timer:
            self.pcie_timer = GLib.timeout_add(1000, self.sample_pcie)
        # Leitura em blocos: no PyGObject, read_line devolve b"" tanto para linha vazia quanto
        # para o fim do stream, e o laço não acabaria nunca.
        stream = self.proc.get_stdout_pipe()
        stream.read_bytes_async(8192, GLib.PRIORITY_DEFAULT, None, self.on_chunk)
        self.proc.wait_async(None, self.on_exit)
        label = {"capture": "Rodando", "list": "Listando dispositivos", "peak": "Padrão de pico na TV"}.get(kind, "Rodando")
        detail = {"capture": "Esc na janela ou Parar encerra. H: SDR/HDR, Q: qualidade",
                  "list": "", "peak": "Veja até onde o quadrado de dentro aparece"}.get(kind, "")
        self.set_status("running", label, detail)
        self.set_play(True)

    def on_chunk(self, stream, res):
        try:
            data = stream.read_bytes_finish(res).get_data()
        except GLib.Error:
            data = b""
        if not data:
            if self.partial:
                self.on_line(self.partial)
                self.partial = b""
            self.run_eof = True
            self.maybe_collect()
            return
        *lines, self.partial = (self.partial + data).split(b"\n")
        for line in lines:
            self.on_line(line)
        stream.read_bytes_async(8192, GLib.PRIORITY_DEFAULT, None, self.on_chunk)

    def on_line(self, line):
        text = line.decode("utf-8", errors="replace").rstrip("\r")
        self.log(text)
        if self.run_config is not None and len(self.run_lines) < 20000:
            self.run_lines.append(text)
        if "busy" in text.lower():
            self.saw_busy = True

    def on_exit(self, proc, res):
        try:
            proc.wait_finish(res)
        except GLib.Error:
            pass
        for t in self.kill_timers:
            GLib.source_remove(t)
        self.kill_timers = []
        if proc.get_if_exited():
            code = proc.get_exit_status()
            msg = EXIT_MEANING.get(code, f"O fmp saiu com código {code}.")
        else:
            code = -proc.get_term_sig() if proc.get_if_signaled() else -1
            msg = "O fmp foi encerrado."
        if self.stopping and code in (0, 1, 130, -2, -15, 143):
            msg = "Parado." if code in (0, 130, -2, -15, 143) else msg
        self.log(f"[HypeFrame] {msg} (código {code})")
        kind = self.proc_kind
        self.proc = None
        self.proc_kind = None
        ok = code == 0 or (self.stopping and code in (130, -2, -15, 143))
        if self.saw_busy and not ok:
            msg = "A placa estava ocupada por outro programa"
        self.set_status("idle" if ok else "error", "Parado" if ok else "Terminou com erro",
                        "" if ok else msg)
        self.set_play(False)
        if not ok or kind == "capture":
            self.toast(msg)
        if kind == "capture":
            self.finish_display()
        if self.pcie_timer:
            GLib.source_remove(self.pcie_timer)
            self.pcie_timer = 0
        self.run_exited = True
        self.maybe_collect()

    def finish_display(self):
        args, self.restore_args = self.restore_args, None
        if args:
            # Folga antes de devolver as telas: o fmp acabou de sair e a janela em tela cheia some nesse instante.
            # Reconfigurar as telas no mesmo décimo de segundo deixou o GNOME instável (22/11/2026).
            before = self.last_status
            self.set_status("busy", "Voltando as telas", "")

            def later():
                self.restore_timer = 0
                self.restore_display(args)
                self.set_status(*before)
                return False
            self.restore_timer = GLib.timeout_add(RESTORE_DELAY_MS, later)
        self.update_banner()

    def stop(self):
        if self.launch_timer:
            GLib.source_remove(self.launch_timer)
            self.launch_timer = 0
            self.set_status("idle", "Parado", "")
            self.set_play(False)
            self.finish_display()
            return
        if not self.proc:
            return
        self.stopping = True
        self.set_status("busy", "Parando", "Esperando o resumo do fmp")
        pids = fmp_pids(self.fmp_path())
        if pids:
            for p in pids:
                try:
                    os.kill(p, signal.SIGINT)
                except OSError:
                    pass
        else:
            self.proc.send_signal(signal.SIGINT)

        def term():
            for p in fmp_pids(self.fmp_path()):
                try:
                    os.kill(p, signal.SIGTERM)
                except OSError:
                    pass
            return False

        def kill():
            if self.proc:
                self.proc.force_exit()
            return False
        self.kill_timers = [GLib.timeout_add_seconds(8, term), GLib.timeout_add_seconds(15, kill)]

    # ---------------------------------------------------------------- UI util

    def set_play(self, running):
        for b in (self.hero_play, self.side_play):
            b.set_label("Parar" if running else "Iniciar")
            if running:
                b.add_css_class("running")
            else:
                b.remove_css_class("running")

    def set_status(self, kind, title, detail):
        self.last_status = (kind, title, detail)
        for c in ("running", "busy", "error"):
            self.side_dot.remove_css_class(c)
        if kind != "idle":
            self.side_dot.add_css_class(kind)
        self.side_status.set_label(title)
        self.side_detail.set_label(detail or "")
        self.side_detail.set_visible(bool(detail))

    def log(self, text):
        buf = self.logbuf
        end = buf.get_end_iter()
        buf.insert(end, text + "\n")
        lines = buf.get_line_count()
        if lines > LOG_MAX_LINES:
            start = buf.get_start_iter()
            cut = buf.get_iter_at_line(lines - LOG_MAX_LINES)
            buf.delete(start, cut[1] if isinstance(cut, tuple) else cut)
        mark = buf.create_mark(None, buf.get_end_iter(), False)
        self.logview.scroll_mark_onscreen(mark)
        buf.delete_mark(mark)

    def on_copy_log(self, *_):
        b = self.logbuf
        self.copy_text(b.get_text(b.get_start_iter(), b.get_end_iter(), False), "Saída copiada")

    def copy_text(self, text, msg):
        Gdk.Display.get_default().get_clipboard().set_content(
            Gdk.ContentProvider.new_for_value(text))
        self.toast(msg)

    def toast(self, msg):
        self.toasts.add_toast(Adw.Toast(title=msg, timeout=4))

    def alert(self, heading, body):
        d = Adw.AlertDialog(heading=heading, body=body)
        d.add_response("ok", "Entendi")
        d.present(self)

    def on_close_request(self, *_):
        if self.restore_timer and not self.proc:
            # Fechando durante a folga: devolve as telas já, senão o tempo some com a janela.
            GLib.source_remove(self.restore_timer)
            self.restore_timer = 0
            self.restore_display(self.extra.get("pending_restore"))
        w, h = self.get_default_size()
        self.extra["size"] = [w, h]
        self.save_now()
        if self.proc or self.launch_timer:
            d = Adw.AlertDialog(heading="O fmp está rodando",
                                body="Parar o fmp e fechar o HypeFrame?")
            d.add_response("cancel", "Continuar jogando")
            d.add_response("quit", "Parar e fechar")
            d.set_response_appearance("quit", Adw.ResponseAppearance.DESTRUCTIVE)

            def resp(_d, r):
                if r == "quit":
                    self.stop()
                    self.quit_when_done()
            d.connect("response", resp)
            d.present(self)
            return True
        return False

    def quit_when_done(self):
        def check():
            if self.proc or self.launch_timer or self.restore_timer:
                return True
            self.destroy()
            return False
        GLib.timeout_add(200, check)


def GObject_flags(sync=False):
    from gi.repository import GObject
    return GObject.BindingFlags.SYNC_CREATE if sync else GObject.BindingFlags.BIDIRECTIONAL


class HypeFrameApp(Adw.Application):
    def __init__(self):
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.DEFAULT_FLAGS)

    def do_startup(self):
        Adw.Application.do_startup(self)
        Adw.StyleManager.get_default().set_color_scheme(Adw.ColorScheme.FORCE_DARK)
        prov = Gtk.CssProvider()
        prov.load_from_string(CSS)
        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(), prov, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

    def do_activate(self):
        win = self.get_active_window() or HypeFrameWindow(self)
        win.present()


if __name__ == "__main__":
    sys.exit(HypeFrameApp().run(sys.argv))
