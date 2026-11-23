#!/usr/bin/env python3
"""HypeFrame: interface GNOME para o `fmp capture` (frame-motion-plus).

Monta a linha de comando do fmp a partir de controles, roda o fmp dentro da
distrobox, mostra a saída e guarda os últimos valores em
~/.config/hypeframe/config.json. Opcionalmente ajusta a TV (HDR/SDR, VRR, só a TV)
pelo Mutter antes de jogar e volta como estava ao parar.
"""
import json
import os
import shlex
import signal
import subprocess
import sys

import gi

# libadwaita não suporta GTK_THEME: com ela os estilos das linhas e grupos somem.
os.environ.pop("GTK_THEME", None)

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk, Pango  # noqa: E402

APP_ID = "io.github.danilogc4.HypeFrame"
VERSION = "0.1.0"
HERE = os.path.dirname(os.path.realpath(__file__))
CONFIG_DIR = os.path.join(GLib.get_user_config_dir(), "hypeframe")
CONFIG_FILE = os.path.join(CONFIG_DIR, "config.json")
LOG_MAX_LINES = 4000

# --------------------------------------------------------------------------
# Parâmetros
# --------------------------------------------------------------------------

MODES = [
    ("4k30-hdr", "4K 29,97 HDR10", 29.97),
    ("1440p60-hdr", "1440p 59,94 HDR10", 59.94),
    ("4k60-sdr", "4K 59,94 SDR", 59.94),
    ("1440p60-sdr", "1440p 59,94 SDR", 59.94),
    ("1080p60-sdr", "1080p 59,94 SDR", 59.94),
    ("1440p120-sdr", "1440p 120 SDR", 120.0),
    ("1080p120-sdr", "1080p 120 SDR", 120.0),
    ("auto", "Automático (maior modo 10 bits)", None),
    ("custom", "Personalizado (aba Captura)", None),
]

DEFAULTS = {
    # Início
    "mode": "4k30-hdr",
    "quality": "medium",
    "multiplier": 2,
    "output_hz_on": False,
    "output_hz": 119.88,
    "fullscreen": True,
    "clock": "auto",
    # Imagem
    "generator": "auto",
    "quality_switch": True,
    "scd": "off",
    "cuda_sync": "auto",
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
    # Tela e ritmo
    "lead_on": False,
    "lead": 5.5,
    "latch_on": False,
    "latch": 5.5,
    "late_generated": 4.0,
    "ceiling": True,
    "window": "1920x1080",
    "tv_apply": False,
    "tv_connector": "",
    "tv_color": "hdr",
    "tv_vrr": True,
    "tv_only": True,
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
        "HDR fiel",
        "TV em HDR (bt2100), HDR10 ligado, faixa limitada e BT.2020, como o PS5 manda",
        "weather-clear-symbolic",
        {
            "mode": "4k30-hdr", "hdr": "on", "range": "limited", "matrix": "bt2020",
            "clock": "auto", "tv_apply": True, "tv_color": "hdr", "tv_only": True,
            "lead_on": False, "latch_on": False,
        },
    ),
    (
        "latency",
        "Menor atraso",
        "TV em SDR com VRR e relógio VRR: 65 ms medidos, mas sem o HDR do PS5",
        "power-profile-performance-symbolic",
        {
            "mode": "4k30-hdr", "hdr": "follow", "range": "auto", "matrix": "auto",
            "clock": "vrr", "tv_apply": True, "tv_color": "sdr", "tv_vrr": True,
            "tv_only": True, "lead_on": False, "latch_on": False,
        },
    ),
]

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
    except (OSError, ValueError):
        pass
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


def game_state(state, conn, hdr, vrr, only):
    """O estado desejado para jogar, a partir do atual."""
    import copy
    want = copy.deepcopy(state)
    tv = want["monitors"][conn]
    base = tv["mode"] or tv["preferred"]
    base = base.replace("+vrr", "") if base else base
    mode = base + "+vrr" if vrr and base and base + "+vrr" in tv["modes"] else base
    tv["mode"] = mode
    tv["color"] = 1 if hdr else 0
    in_layout = any(conn in lm["monitors"] for lm in want["logical"])
    if only:
        old = next((lm for lm in want["logical"] if conn in lm["monitors"]), None)
        lm = dict(old) if old else {"scale": 1.0, "transform": 0}
        lm.update({"x": 0, "y": 0, "primary": True, "monitors": [conn]})
        want["logical"] = [lm]
    elif not in_layout:
        right = max((lm["x"] + 10000 for lm in want["logical"]), default=0)
        want["logical"].append({"x": right, "y": 0, "scale": 1.0, "transform": 0,
                                "primary": False, "monitors": [conn]})
    return want


def same_layout(a, b):
    def key(s):
        lms = sorted((lm["x"], lm["y"], round(lm["scale"], 3), lm["transform"], lm["primary"],
                      tuple(sorted(lm["monitors"]))) for lm in s["logical"])
        active = {c for lm in s["logical"] for c in lm["monitors"]}
        mons = sorted((c, s["monitors"][c]["mode"], s["monitors"][c]["color"]) for c in active)
        return lms, mons
    return key(a) == key(b)


def describe_tv(state, conn):
    m = state["monitors"].get(conn)
    if not m:
        return f"{conn}: desconectada"
    active = any(conn in lm["monitors"] for lm in state["logical"])
    if not active:
        return f"{conn}: desligada"
    color = {0: "SDR", 1: "HDR (bt2100)", 2: "SDR nativo"}.get(m["color"], str(m["color"]))
    others = sum(1 for lm in state["logical"] if conn not in lm["monitors"])
    extra = f", mais {others} monitor(es)" if others else ", única tela"
    return f"{conn}: {m['mode']}, {color}{extra}"


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
        self.kill_timers = []
        self.restore_args = None
        self.chip_box = None

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
        self.add_page("imagem", "Imagem e HDR", "color-select-symbolic", self.page_image())
        self.add_page("captura", "Captura", "camera-video-symbolic", self.page_capture())
        self.add_page("tela", "Tela e ritmo", "video-display-symbolic", self.page_display())
        self.add_page("audio", "Áudio", "audio-speakers-symbolic", self.page_audio())
        self.add_page("sistema", "Sistema", "emblem-system-symbolic", self.page_system())

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
        ess.add(self.combo("mode", "Modo de captura", [(m, l) for m, l, _f in MODES],
                           "O que a Elgato entrega; o fmp gera os quadros do meio"))
        ess.add(self.toggles("quality", "Qualidade do VFG",
                             [("low", "Baixa"), ("medium", "Média"), ("high", "Alta")],
                             "Média: ~2 ms por quadro em 4K. Q troca durante o jogo"))
        ess.add(self.spin("multiplier", "Multiplicador", 1, 8, 1, 0,
                          "Quadros na tela por quadro capturado"))
        ess.add(self.optional_spin("output_hz", "Taxa de saída (Hz)", 24, 360, 0.01, 2,
                                   "Ligada: substitui o multiplicador (o teto vira esta taxa)"))
        ess.add(self.switch("fullscreen", "Tela cheia",
                            "Abre no monitor onde está o mouse"))
        ess.add(self.toggles("clock", "Relógio da tela",
                             [("auto", "Auto"), ("fixed", "Fixo"), ("vrr", "VRR")],
                             "VRR só vale com a TV em VRR e taxa múltipla exata"))
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

    def page_image(self):
        page = Adw.PreferencesPage()
        gen = Adw.PreferencesGroup(title="Gerador de quadros")
        gen.add(self.combo("generator", "Gerador", [
            ("auto", "Automático (VFG se o SDK existir)"), ("vfg", "NVIDIA VFG"),
            ("blend", "Mistura simples"), ("repeat", "Repetir quadro"), ("none", "Nenhum (só reais)")]))
        gen.add(self.switch("quality_switch", "Carregar as duas qualidades",
                            "Permite trocar com Q sem pausa; custa ~1,5 GB de VRAM em 4K"))
        gen.add(self.toggles("scd", "Detecção de corte de cena",
                             [("off", "Desligada"), ("on", "Ligada")],
                             "Ligada, movimento rápido vira quadro real e cai para 30 fps"))
        gen.add(self.combo("cuda_sync", "Espera do CUDA", [
            ("auto", "Automático (bloqueante)"), ("blocking", "Bloqueante (menos CPU)"),
            ("spin", "Ativa (spin)"), ("yield", "Yield")]))
        page.add(gen)

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
            title="TV no GNOME",
            description="Antes de iniciar, o HypeFrame ajusta a TV pelo Mutter. Nos testes, outro monitor ligado deixou 17% dos quadros fora do ritmo")
        self.tv_now = Adw.ActionRow(title="Agora", subtitle="")
        self.tv_now.add_css_class("property")
        refresh = Gtk.Button(icon_name="view-refresh-symbolic", valign=Gtk.Align.CENTER,
                             tooltip_text="Ler de novo")
        refresh.add_css_class("flat")
        refresh.connect("clicked", lambda *_: self.reload_display())
        self.tv_now.add_suffix(refresh)
        tvg.add(self.tv_now)
        tvg.add(self.switch("tv_apply", "Ajustar a TV antes de jogar"))
        conns = [(c, f"{m['name']} ({c})") for c, m in self.display["monitors"].items()]
        if self.values["tv_connector"] and self.values["tv_connector"] not in dict(conns):
            conns.append((self.values["tv_connector"], self.values["tv_connector"]))
        tvg.add(self.combo("tv_connector", "TV", conns or [("", "Nenhum monitor encontrado")]))
        tvg.add(self.toggles("tv_color", "Saída da TV",
                             [("hdr", "HDR"), ("sdr", "SDR")],
                             "HDR: cores do PS5. SDR: o Mutter compõe liso, menor atraso"))
        tvg.add(self.switch("tv_vrr", "Taxa variável (VRR)"))
        tvg.add(self.switch("tv_only", "Só a TV", "Desliga os outros monitores enquanto joga"))
        tvg.add(self.switch("tv_restore", "Voltar como estava ao parar"))
        page.add(tvg)

        rit = Adw.PreferencesGroup(title="Ritmo de apresentação",
                                   description="Nos testes no GNOME, 5,5 ms fixos baixaram os quadros fora do ritmo de 17% para 1,5% com dois monitores")
        rit.add(self.optional_spin("lead", "Antecedência fixa (ms)", 0, 20, 0.5, 1,
                                   "Desligado: o fmp mede e adapta"))
        rit.add(self.optional_spin("latch", "Latch mínimo (ms)", 0, 20, 0.5, 1,
                                   "Desligado: 1 ms (padrão do fmp)"))
        rit.add(self.spin("late_generated", "Quadro gerado atrasado (ms)", 0, 16, 0.5, 1,
                          "VRR: mostra um gerado até este atraso em vez de pular"))
        rit.add(self.switch("ceiling", "Ritmo no teto do VRR",
                            "Pula um gerado quando a tela fica para trás no limite da taxa"))
        rit.add(self.entry("window", "Tamanho da janela (LxA)", size=True))
        page.add(rit)
        return page

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
        if "multiplier" in self.rows:
            self.rows["multiplier"].set_sensitive(not v["output_hz_on"])
        for k in ("tv_connector", "tv_color", "tv_vrr", "tv_only", "tv_restore"):
            if k in self.rows:
                self.rows[k].set_sensitive(v["tv_apply"])
        for k in ("peak", "maxcll_on", "maxfall", "range", "matrix"):
            if k in self.rows:
                self.rows[k].set_sensitive(v["hdr"] != "off" or k in ("range", "matrix"))
        if "quality" in self.rows:
            gen_vfg = v["generator"] in ("auto", "vfg")
            self.rows["quality"].set_sensitive(gen_vfg)
            self.rows["quality_switch"].set_sensitive(gen_vfg)
        if hasattr(self, "cmd_label"):
            self.cmd_label.set_text(shlex.join(self.build_argv("capture")))
        self.update_chips()
        if hasattr(self, "tv_now"):
            self.tv_now.set_subtitle(describe_tv(self.display, v["tv_connector"]))
        if hasattr(self, "banner"):
            self.update_banner()

    def update_chips(self):
        if self.chip_box is None:
            return
        while (c := self.chip_box.get_first_child()) is not None:
            self.chip_box.remove(c)
        v = self.values
        mode = next((m for m in MODES if m[0] == v["mode"]), None)
        chips = []
        if mode and mode[2]:
            src = float(v["capture_fps"]) if v["capture_fps"] and v["mode"] != "custom" else mode[2]
            chips.append((f"{mode[1]}", ""))
            out = v["output_hz"] if v["output_hz_on"] else src * v["multiplier"]
            chips.append((f"{fmt_num(src)} → {fmt_num(out)} fps", "violet"))
        else:
            chips.append((mode[1] if mode else v["mode"], ""))
            chips.append((f"até {fmt_num(v['output_hz'])} Hz" if v["output_hz_on"]
                          else f"×{v['multiplier']}", "violet"))
        gen = {"auto": "VFG", "vfg": "VFG", "blend": "Mistura", "repeat": "Repetição", "none": "Sem geração"}[v["generator"]]
        if gen == "VFG":
            gen += " " + {"low": "Baixa", "medium": "Média", "high": "Alta"}[v["quality"]]
        chips.append((gen, ""))
        if v["hdr"] != "off":
            chips.append((f"HDR {v['peak']} nits", "violet"))
        if v["tv_apply"]:
            chips.append((f"TV em {'HDR' if v['tv_color'] == 'hdr' else 'SDR'}"
                          + (" + VRR" if v["tv_vrr"] else ""), ""))
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
        others = len(self.display["logical"]) > 1
        if others and not (self.values["tv_apply"] and self.values["tv_only"]):
            self.banner.set_title("Outro monitor ligado: no GNOME o ritmo fica irregular")
            self.banner.set_button_label("Usar só a TV")
            self.banner.kind = "only"
            self.banner.set_revealed(True)
            return
        self.banner.set_revealed(False)

    def on_banner(self, _b):
        if getattr(self.banner, "kind", "") == "restore":
            self.restore_display(self.extra.get("pending_restore"))
        else:
            self.set_value("tv_apply", True)
            self.set_value("tv_only", True)
            self.refresh_all()
            self.schedule_save()
            self.toast("Ao iniciar, os outros monitores serão desligados")

    def reload_display(self):
        try:
            self.display = display_state()
        except GLib.Error as e:
            self.toast(f"Não li o estado dos monitores: {e.message}")
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
        if kind == "peak":
            return argv + ["hdr-pattern", "peak", "--fullscreen", "--duration-s", "20",
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
        if v["output_hz_on"]:
            a += ["--output-hz", fmt_num(v["output_hz"]).replace(",", ".")]
        else:
            a += ["--multiplier", str(v["multiplier"])]
        a.append("--quality-switch" if v["quality_switch"] else "--no-quality-switch")
        a += ["--scd", v["scd"]]
        if v["cuda_sync"] != "auto":
            a += ["--cuda-sync", v["cuda_sync"]]
        if v["fullscreen"]:
            a.append("--fullscreen")
        else:
            a += ["--window", v["window"]]
        a += ["--clock", v["clock"]]
        if v["lead_on"]:
            a += ["--present-lead-ms", fmt_num(v["lead"]).replace(",", ".")]
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

    # ---------------------------------------------------------------- execução

    def toggle_run(self):
        if self.proc or self.launch_timer:
            self.stop()
        else:
            self.start("capture")

    def run_tool(self, kind):
        if self.proc or self.launch_timer:
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

    def start(self, kind):
        if not self.check_ready(kind):
            return
        self.save_now()
        self.stopping = False
        if kind == "capture" and self.values["tv_apply"]:
            if not self.prepare_display():
                return
            if self.restore_args is not None:
                self.set_status("busy", "Ajustando a TV", "Esperando a TV ressincronizar")
                self.set_play(True)
                self.launch_timer = GLib.timeout_add(2500, self.after_display, kind)
                return
        self.launch(kind)

    def after_display(self, kind):
        self.launch_timer = 0
        try:
            self.display = display_state()
        except GLib.Error:
            pass
        self.log("Tela agora: " + describe_tv(self.display, self.values["tv_connector"]))
        want_hdr = self.values["tv_color"] == "hdr"
        tv = self.display["monitors"].get(self.values["tv_connector"])
        if tv and (tv["color"] == 1) != want_hdr:
            self.toast("A TV não ficou no modo de cor pedido (veja a saída)")
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
            self.alert("TV não encontrada", f"O conector {conn} não está conectado. Ligue a TV ou desative “Ajustar a TV”.")
            return False
        want = game_state(cur, conn, self.values["tv_color"] == "hdr", self.values["tv_vrr"],
                          self.values["tv_only"])
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
            self.alert("O GNOME recusou a configuração da TV", (r.stderr or r.stdout).strip()[:600])
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
            return
        *lines, self.partial = (self.partial + data).split(b"\n")
        for line in lines:
            self.on_line(line)
        stream.read_bytes_async(8192, GLib.PRIORITY_DEFAULT, None, self.on_chunk)

    def on_line(self, line):
        text = line.decode("utf-8", errors="replace").rstrip("\r")
        self.log(text)
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

    def finish_display(self):
        args, self.restore_args = self.restore_args, None
        if args:
            self.restore_display(args)
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
            if self.proc or self.launch_timer:
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
