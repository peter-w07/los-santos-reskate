#!/usr/bin/env python3
"""Los Santos for ReSkate: build levels for skate. (with ReSkate) from your own copy of GTA V.

    python lossantos.py check                         are the folders in config.json right, are the tools there
    python lossantos.py prepare                       once: build the tools, read collision / interiors / water from the game
    python lossantos.py build sections|city|big [--only D5,E5] [--force]
    python lossantos.py level NAME --region W,S,E,N [--spawn X,Y] [quality options]     a level of your own
    python lossantos.py pack sections|city|big                                           many levels -> one mod
    python lossantos.py install NAME                  copy a pack or a level into ReSkate's Mods folder
    python lossantos.py model NAME --region W,S,E,N [--textures 512] [--detail 1]        an ordinary glTF 3D model
    python lossantos.py art                           the overview maps and mod icons

Nothing from the game is stored in this repository: every command reads the GTA V install named in config.json
(read only) and writes under the "work" folder. See README.md.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "tools"))

LAYOUT = {            # name -> (layout file, kind, pack name)
    "sections": ("sections.json", "grid", "LosSantos_Sections"),
    "city": ("city.json", "grid", "LosSantos_City"),
    "big": ("city.json", "big", "LosSantos_Big"),
}
OVERVIEW_REGION = "-2700,-3800,1900,1800"       # the city: the picture the loading screens and maps are cut from


def run(cmd, **kw):
    print("  >", " ".join(str(c) for c in cmd), flush=True)
    return subprocess.run(cmd, **kw).returncode


def cmd_check(args):
    import cfg
    ok = True

    def line(good, text, hint=""):
        nonlocal ok
        ok = ok and good
        print(("  ok    " if good else "  MISSING ") + text + ("" if good or not hint else "   -> " + hint))

    line(os.path.isfile(os.path.join(cfg.GTA, "GTA5.exe")), f"GTA V in {cfg.GTA}", 'set "gta" in config.json')
    line(os.path.isfile(os.path.join(cfg.SKATE, "Skate.exe")), f"skate. in {cfg.SKATE}", 'set "skate" to the folder with Skate.exe that ReSkate launches')
    line(os.path.isfile(cfg.STUDIO_CLI), f"ReSkate Studio in {cfg.STUDIO}", 'set "studio" to the folder with reskate_cli.exe')
    line(bool(cfg.MODS) and os.path.isdir(cfg.MODS), f"Mods folder {cfg.MODS}", 'set "mods" (only needed for install)')
    line(shutil.which("dotnet") is not None, ".NET SDK (dotnet)", "install the .NET 8 SDK")
    try:
        import numpy  # noqa: F401
        import PIL  # noqa: F401
        line(True, "Python packages numpy and Pillow")
    except ImportError:
        line(False, "Python packages numpy and Pillow", "pip install numpy pillow")
    os.makedirs(cfg.WORK, exist_ok=True)
    free = shutil.disk_usage(cfg.WORK).free / 2 ** 30
    line(free > 20, f"work folder {cfg.WORK} ({free:.0f} GB free)", "the preparation needs about 7 GB, each level 0.1 to 1 GB")
    print("  " + ("ready" if os.path.isfile(cfg.EXPORTER[1]) and os.path.isfile(os.path.join(cfg.EXPORT, "collision", "index.json"))
                  else "not prepared yet: run  python lossantos.py prepare"))
    return 0 if ok else 1


def cmd_prepare(args):
    import cfg
    for tool in ("gta-skate", "gta-export"):
        if run(["dotnet", "build", "-c", "Release", os.path.join(cfg.TOOLS, tool)]) != 0:
            return 1
    os.makedirs(cfg.work("logs"), exist_ok=True)
    gx = ["dotnet", os.path.join(cfg.TOOLS, "gta-export", "bin", "Release", "net8.0", "gta-export.dll")]
    if args.force or not os.path.isfile(os.path.join(cfg.EXPORT, "collision", "index.json")):
        print("reading collision, materials and water from the game (a few minutes, about 6 GB)", flush=True)
        if cfg.run_heavy(gx + ["all", "--game", cfg.GTA, "--out", cfg.EXPORT] + (["--force"] if args.force else []), cfg.work("logs", "prepare_collision.log")) != 0:
            print("  failed: see", cfg.work("logs", "prepare_collision.log"))
            return 1
    if args.force or not os.path.isfile(os.path.join(cfg.EXPORT, "interiors", "index.json")):
        print("reading the interiors (metro, tunnels, car parks, shops)", flush=True)
        if cfg.run_heavy(gx + ["interiors", "--game", cfg.GTA, "--out", cfg.EXPORT] + (["--force"] if args.force else []), cfg.work("logs", "prepare_interiors.log")) != 0:
            print("  failed: see", cfg.work("logs", "prepare_interiors.log"))
            return 1
    if args.force or not os.path.isfile(cfg.work("next_test", "overview", "ui", "map_image.png")):
        print("drawing the city from above (for the loading pictures and maps)", flush=True)
        cfg.run_heavy(cfg.EXPORTER + ["export", "--game", cfg.GTA, "--collision", cfg.EXPORT, "--out", cfg.work("next_test"), "--name", "overview",
                                      "--region", OVERVIEW_REGION, "--map-height", "6480", "--tex-cap", "64", "--min-size", "1.5",
                                      "--no-interiors", "--no-prop-collision"], cfg.work("logs", "prepare_overview.log"))
        for junk in ("meshes", "textures"):                 # only the picture is kept
            shutil.rmtree(cfg.work("next_test", "overview", junk), ignore_errors=True)
    print("prepared")
    return 0


def cmd_build(args):
    import cfg
    file, kind, _ = LAYOUT[args.layout]
    cmd = [sys.executable, os.path.join(cfg.TOOLS, "build_sections.py"), os.path.join(cfg.LAYOUTS, file), "--kind", kind]
    if args.only:
        cmd += ["--only", args.only]
    if args.force:
        cmd += ["--force"]
    return run(cmd)


def cmd_level(args):
    import cfg
    try:
        x0, y0, x1, y1 = [float(v) for v in args.region.split(",")]
    except ValueError:
        sys.exit("--region is west,south,east,north in metres, for example --region -100,-1200,500,-600")
    if not (x1 > x0 and y1 > y0):
        sys.exit("--region: east must be greater than west and north greater than south")
    safe = re.sub(r"[^A-Za-z0-9]", "", args.name.title())[:40] or "Level"
    sid = "lsx_" + safe.lower()
    export = ["--tex-cap", str(args.textures), "--level", str(args.detail), "--tree-level", str(args.detail)]
    export += ["--no-props"] if args.no_props else []
    export += ["--no-interiors"] if args.no_interiors else []
    sec = dict(row="U", col=1, name=args.name, x0=x0, y0=y0, x1=x1, y1=y1, skip=False, special=True, margin=0, kind="custom level",
               id=sid, display=args.name, mod="LosSantos_" + safe, export_options=export,
               compile_options=["--pause-map", "2d", "--no-gi", "--no-lods", "--vista-ratio", str(args.far_view), "--vista-error", str(round(0.18 / args.far_view, 2))])
    if args.spawn:
        sec["spawn"] = args.spawn
    if args.max_textures:
        sec["max_textures"] = args.max_textures
    os.makedirs(cfg.work("custom"), exist_ok=True)
    layout = cfg.work("custom", safe + ".json")
    json.dump({"margin": 0, "id": "lsx", "mod": "LosSantos", "out": "custom", "sections": [sec]}, open(layout, "w", encoding="utf-8"), indent=1)
    side = max(x1 - x0, y1 - y0)
    if side > 1500 and not args.no_props:
        print(f"note: {side:.0f} m across with props is heavy; 2 km levels are known to need --no-props and --max-textures 5500")
    elif side > 1000 and not args.max_textures:
        print(f"note: {side:.0f} m across; if the export reports more than about 5,400 textures, add --max-textures 5400")
    rc = run([sys.executable, os.path.join(cfg.TOOLS, "build_sections.py"), layout, "--force"])
    if rc == 0:
        print(f'the level is in {cfg.work("custom", "LosSantos_" + safe)};  python lossantos.py install LosSantos_{safe}')
    return rc


def cmd_pack(args):
    import cfg
    return run([sys.executable, os.path.join(cfg.TOOLS, "make_packs.py"), LAYOUT[args.layout][2]])


def cmd_install(args):
    import cfg
    if not cfg.MODS or not os.path.isdir(cfg.MODS):
        sys.exit('config.json: set "mods" to ReSkate\'s Mods folder')
    src = next((p for p in (cfg.work("pack", args.name), cfg.work("custom", args.name), cfg.work("levels", args.name), cfg.work("mods", args.name))
                if os.path.isfile(os.path.join(p, "layout.toc"))), None)
    if not src:
        sys.exit(f"{args.name}: not found under {cfg.WORK} (pack, custom, levels, mods)")
    if cfg.skate_running():
        sys.exit("skate. is running: close it first (the Mods folder is not touched while the game runs)")
    tmp, dst = os.path.join(cfg.MODS, ".incoming_" + args.name), os.path.join(cfg.MODS, args.name)
    shutil.rmtree(tmp, ignore_errors=True)       # a folder starting with a dot is never scanned as a mod
    shutil.copytree(src, tmp)
    shutil.rmtree(dst, ignore_errors=True)
    os.rename(tmp, dst)
    trainer = src + ".trainer"                   # a pack's Trainer spots: one dot-folder per level (many small folders)
    if args.trainer and os.path.isdir(trainer):
        for d in os.listdir(trainer):
            shutil.rmtree(os.path.join(cfg.MODS, d), ignore_errors=True)
            shutil.copytree(os.path.join(trainer, d), os.path.join(cfg.MODS, d))
    print("installed", dst)
    return 0


def cmd_model(args):
    import cfg
    os.makedirs(cfg.work("logs"), exist_ok=True)
    log = cfg.work("logs", f"model_{args.name}.log")
    rc = cfg.run_heavy(cfg.EXPORTER + ["model", "--game", cfg.GTA, "--collision", cfg.EXPORT, "--out", cfg.work("model"), "--name", args.name,
                                       "--region", args.region, "--tex-cap", str(args.textures), "--level", str(args.detail), "--tree-level", str(args.detail)]
                       + (["--no-props"] if args.no_props else []) + (["--no-interiors"] if args.no_interiors else []), log)
    print(("the model is in " + cfg.work("model", args.name)) if rc == 0 else "failed: see " + log)
    return rc


def cmd_art(args):
    import cfg
    rc = 0
    for extra in ([], [os.path.join(cfg.LAYOUTS, "city.json")]):
        rc |= run([sys.executable, os.path.join(cfg.TOOLS, "make_art.py"), "splits"] + extra)
    rc |= run([sys.executable, os.path.join(cfg.TOOLS, "make_art.py"), "icons"])
    return rc


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("check").set_defaults(fn=cmd_check)
    p = sub.add_parser("prepare"); p.add_argument("--force", action="store_true"); p.set_defaults(fn=cmd_prepare)
    p = sub.add_parser("build"); p.add_argument("layout", choices=sorted(LAYOUT)); p.add_argument("--only", default="", help="comma separated level codes, e.g. D5,E5")
    p.add_argument("--force", action="store_true", help="rebuild levels that are already built"); p.set_defaults(fn=cmd_build)

    def quality(p, textures):
        p.add_argument("--textures", type=int, default=textures, help="largest texture side in pixels")
        p.add_argument("--detail", type=int, default=1, choices=[0, 1, 2, 3], help="which model version of the game: 0 best ... 3 lightest")
        p.add_argument("--no-props", action="store_true", help="leave out street furniture and most plants")
        p.add_argument("--no-interiors", action="store_true", help="leave out the metro, tunnels, car parks and shops")

    p = sub.add_parser("level"); p.add_argument("name"); p.add_argument("--region", required=True, help="west,south,east,north in metres")
    p.add_argument("--spawn", default="", help="x,y where you start (put on the nearest open road)")
    quality(p, 256)
    p.add_argument("--max-textures", type=int, default=0, help="keep the most visible textures, flat colours for the rest (about 5400 is safe)")
    p.add_argument("--far-view", type=float, default=0.03, help="detail of the distant city: 0.03 light ... 0.5 best")
    p.set_defaults(fn=cmd_level)
    p = sub.add_parser("pack"); p.add_argument("layout", choices=sorted(LAYOUT)); p.set_defaults(fn=cmd_pack)
    p = sub.add_parser("install"); p.add_argument("name")
    p.add_argument("--trainer", action="store_true", help="also copy the Trainer spot folders of a pack (one small folder per level)")
    p.set_defaults(fn=cmd_install)
    p = sub.add_parser("model"); p.add_argument("name"); p.add_argument("--region", required=True); quality(p, 512); p.set_defaults(fn=cmd_model)
    sub.add_parser("art").set_defaults(fn=cmd_art)
    args = ap.parse_args()
    sys.exit(args.fn(args))


if __name__ == "__main__":
    main()
