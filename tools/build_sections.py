"""Builds the city as a grid of overlapping sections, each a separate ReSkate level, one after the other.

    python tools/build_sections.py build/sections.json [--only D4,D5] [--install] [--force]

sections.json:
  {"margin": 100,
   "sections": [{"row": "D", "col": 4, "name": "Legion Square", "x0": -100, "y0": -1200, "x1": 500, "y1": -600,
                 "spawn": "195,-934" (optional), "skip": false}]}
Rows are letters from north to south, columns numbers from west to east. Every section is exported in the same
coordinate frame with a margin around its core, so neighbours share a band of identical geometry and a position
means the same place in both. Level name "LS-D4 Legion Square" (the level lists sort by name), map id ls_d4,
mod folder LosSantos_D4_LegionSquare under build/mods. Each level gets bus stops at its centre and at the edge
towards every neighbour ("to LS-D5 (east)"), and the same points as Trainer spots (trainer.json).
--install copies finished mods into ReSkate's Mods folder (only while Skate is not running).
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfg  # noqa: E402

WORK, TOOLS = cfg.WORK, cfg.TOOLS
EXPORTER = cfg.EXPORTER + []
STUDIO, SKATE, MODS = cfg.STUDIO_CLI, cfg.SKATE_COMPILE, cfg.MODS
skate_running = cfg.skate_running
COMPILE_OPTIONS = ["--pause-map", "2d", "--no-gi", "--no-lods", "--vista-ratio", "0.03", "--vista-error", "6"]


def stat(log, key):
    m = re.search(rf"^{key}=(\d+)", log, re.M)
    return int(m.group(1)) if m else None


def label(sec):
    return f"LS-{sec['row']}{sec['col']}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("sections")
    ap.add_argument("--only", default="", help="comma separated cells, e.g. D4,D5")
    ap.add_argument("--install", action="store_true")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--no-special", action="store_true", help="leave out the landmark levels of the set")
    ap.add_argument("--kind", default="", choices=["", "grid", "landmarks", "big"], help="only the grid levels, the landmark levels (row S) or the big levels (row X)")
    ap.add_argument("--since", default="", help='rebuild every section packaged before this time, e.g. "2026-10-05 23:00"')
    ap.add_argument("--compile-options", default="")
    args = ap.parse_args()
    doc = json.load(open(args.sections, encoding="utf-8"))
    margin = float(doc.get("margin", 100))
    only = {s.strip().upper() for s in args.only.split(",") if s.strip()}
    extra = args.compile_options.split() if args.compile_options else COMPILE_OPTIONS
    os.makedirs(os.path.join(WORK, "logs"), exist_ok=True)
    # a set of levels may name its own ids, mod folders and output folder ("id", "mod", "out" in the json), so
    # two sets (the 800 m sections, the 1.4 km city levels) never share a level id or a folder
    idp, modp, outp = doc.get("id", "ls"), doc.get("mod", "LosSantos"), doc.get("out", "mods")
    budget = int(doc.get("max_textures", 0))      # more textures than this: the least visible become flat colours
    os.makedirs(os.path.join(WORK, outp), exist_ok=True)
    summary_path = os.path.join(WORK, outp, "summary.json")
    summary = json.load(open(summary_path, encoding="utf-8")) if os.path.exists(summary_path) else {}

    live = [s for s in doc["sections"] if not s.get("skip")]
    # landmark levels ("special") lie on top of the grid: they have no neighbours and their box is the region
    by_cell = {(s["row"], s["col"]): s for s in live if not s.get("special")}
    rows = sorted({s["row"] for s in live if not s.get("special")})
    kind = {"": lambda s: True, "grid": lambda s: not s.get("special"), "landmarks": lambda s: s.get("special") and s["row"] == "S",
            "big": lambda s: s.get("special") and s["row"] == "X"}[args.kind]
    todo = [s for s in live if (not only or f"{s['row']}{s['col']}" in only) and not (args.no_special and s.get("special")) and kind(s)]
    # nearest to Legion Square first, so the most used sections are ready first
    todo.sort(key=lambda s: (not s.get("special"), ((s["x0"] + s["x1"]) / 2 - 195) ** 2 + ((s["y0"] + s["y1"]) / 2 + 934) ** 2))
    for n, sec in enumerate(todo, 1):
        cell = f"{sec['row']}{sec['col']}"
        sid = sec.get("id") or f"{idp}_{cell.lower()}"
        display = sec.get("display") or f"{label(sec)} {sec['name']}"
        mod = sec.get("mod") or modp + "_" + cell + "_" + re.sub(r"[^A-Za-z0-9]", "", sec["name"].title())[:40]
        mod_dir = os.path.join(WORK, outp, mod)
        stamp = os.path.join(mod_dir, "manifest.json")
        fresh = os.path.isfile(stamp) and (not args.since or os.path.getmtime(stamp) >= time.mktime(time.strptime(args.since, "%Y-%m-%d %H:%M")))
        if fresh and not args.force:
            print(f"[{n}/{len(todo)}] {cell}: already built, skipped", flush=True)
        else:
            t0 = time.time()
            x0, y0, x1, y1 = sec["x0"], sec["y0"], sec["x1"], sec["y1"]
            m = float(sec.get("margin", margin))
            region = f"{x0 - m},{y0 - m},{x1 + m},{y1 + m}"
            cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
            spawn = sec.get("spawn") or f"{cx},{cy}"
            # bus stops: the centre, and the edge towards every neighbour, facing into the section
            # (yaw 0 faces south, 180 north, 90 east, -90 west); the exporter snaps each one to open road
            ri = rows.index(sec["row"]) if not sec.get("special") else -1
            def nb(dr, dc):
                r = ri + dr
                return by_cell.get((rows[r], sec["col"] + dc)) if ri >= 0 and 0 <= r < len(rows) else None
            stops = [f"{sec['name']}:{','.join(spawn.split(',')[:2])}"] + list(sec.get("extra_stops", []))
            for who, text, px, py, yaw in ((nb(-1, 0), "north", cx, y1 - 30, 0), (nb(1, 0), "south", cx, y0 + 30, 180),
                                           (nb(0, 1), "east", x1 - 30, cy, -90), (nb(0, -1), "west", x0 + 30, cy, 90)):
                if who is not None:
                    stops.append(f"to {label(who)} ({text}):{px},{py},{yaw}")
            cmd = EXPORTER + ["export", "--game", cfg.GTA, "--collision", cfg.EXPORT, "--out", os.path.join(WORK, "map"), "--name", sid, "--region", region, "--spawn", spawn, "--display", display, "--stops", ";".join(stops)]
            cmd += list(sec.get("export_options", []))       # e.g. ["--no-props"] for the big levels
            exp_log = os.path.join(WORK, "logs", f"export_{sid}.log")
            rc = cfg.run_heavy(cmd, exp_log)
            if rc != 0:
                print(f"[{n}/{len(todo)}] {cell}: EXPORT FAILED (see build/logs/export_{sid}.log)", flush=True)
                summary[cell] = {"name": display, "error": "export failed"}
                continue
            # the steps between export and compile (texture budget, loading picture, gate) use a CPU core or two:
            # they wait for the game to close as well, so nothing of the build runs while the user plays
            cfg.wait_for_game()
            map_dir = os.path.join(WORK, "map", sid)
            textures = json.load(open(os.path.join(map_dir, "stats.json"), encoding="utf-8"))["totals"]["textures"]
            budget = int(sec.get("max_textures", doc.get("max_textures", 0)))
            if budget and textures > budget:
                # the game has a fixed table of texture handles; a level over the budget faults or hangs on load
                tb = os.path.join(TOOLS, "texbudget")
                imp, tex, cut = map_dir + "_imp.json", map_dir + "_tex.json", map_dir + "_tb"
                shutil.rmtree(cut, ignore_errors=True)
                with open(exp_log, "a", encoding="utf-8") as fh:
                    ok = all(subprocess.run([sys.executable, os.path.join(tb, tool)] + a, stdout=fh, stderr=subprocess.STDOUT, cwd=tb).returncode == 0
                             for tool, a in (("importance.py", [map_dir, imp, cfg.EXPORT]), ("texinfo.py", [map_dir, tex]),
                                             ("repoint2.py", [map_dir, imp, tex, str(budget), cut])))
                if not ok or not os.path.isfile(os.path.join(cut, "map.json")):
                    print(f"[{n}/{len(todo)}] {cell}: TEXTURE BUDGET FAILED (see build/logs/export_{sid}.log)", flush=True)
                    summary[cell] = {"name": display, "error": "texture budget failed"}
                    continue
                mj = json.load(open(os.path.join(cut, "map.json"), encoding="utf-8"))
                mj["name"], mj["display_name"] = sid, display        # the tool names the map after its folder
                json.dump(mj, open(os.path.join(cut, "map.json"), "w", encoding="utf-8"))
                shutil.rmtree(map_dir, ignore_errors=True)
                os.rename(cut, map_dir)
                for f in (imp, tex):
                    os.remove(f)
                print(f"    {cell}: {textures} textures cut to {budget}", flush=True)
            # the loading picture: this level from above, its name and neighbours, and where it lies in the city
            try:
                import make_art
                make_art.SECTIONS_FILE = os.path.abspath(args.sections)
                make_art.loading(os.path.join(WORK, "map", sid), cell)
            except Exception as e:      # a level without the picture still builds (Studio makes a plain one)
                print(f"    no loading picture: {e}", flush=True)
            # gate: Studio cuts decals and blended surfaces at texture repeats; refuse a map that would explode
            gate = subprocess.run([sys.executable, os.path.join(TOOLS, "uv_gate.py"),
                                   os.path.join(WORK, "map", sid)], capture_output=True, text=True)
            with open(exp_log, "a", encoding="utf-8") as fh:
                fh.write(gate.stdout + gate.stderr)
            if gate.returncode != 0:
                print(f"[{n}/{len(todo)}] {cell}: TEXTURE-REPEAT GATE FAILED (see build/logs/export_{sid}.log)", flush=True)
                summary[cell] = {"name": display, "error": "texture-repeat gate failed"}
                continue
            stage = os.path.join(WORK, "stage", sid)
            shutil.rmtree(stage, ignore_errors=True)
            cmd = [STUDIO, "compile-map", SKATE, os.path.join(WORK, "map", sid), stage, "--mod-folder", mod] + list(sec.get("compile_options") or extra)
            log_path = os.path.join(WORK, "logs", f"compile_{sid}.log")
            rc = cfg.run_heavy(cmd, log_path, cwd=os.path.dirname(STUDIO))
            log = open(log_path, encoding="utf-8", errors="replace").read()
            patch = os.path.join(stage, "Patch")
            if rc != 0 or not os.path.isdir(patch):
                print(f"[{n}/{len(todo)}] {cell}: COMPILE FAILED rc={rc} (see build/logs/compile_{sid}.log)", flush=True)
                summary[cell] = {"name": display, "error": f"compile failed rc={rc}"}
                continue
            shutil.rmtree(mod_dir, ignore_errors=True)
            shutil.copytree(patch, mod_dir)
            with open(os.path.join(mod_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as fh:
                json.dump({"name": mod, "author": cfg.AUTHOR or "unknown", "version_number": "1.0.0", "dependencies": [],
                           "description": f"{display}: built from the owner's own GTA V with the Los Santos for ReSkate tools"}, fh, indent=2)
            # Trainer spots: where the exporter put the bus stops (game space is x, height, -y)
            spots = []
            for m in re.finditer(r"bus stop '(.+?)' at gta \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)", open(exp_log, encoding="utf-8", errors="replace").read()):
                spots.append({"name": m.group(1), "position": [float(m.group(2)), float(m.group(4)) + 0.5, -float(m.group(3))]})
            if spots:
                with open(os.path.join(mod_dir, "trainer.json"), "w", encoding="utf-8", newline="\n") as fh:
                    json.dump({"schema": 1, "note": f"{display}: neighbours are named on the edge spots", "spots": spots[:64]}, fh, indent=2)
            size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(mod_dir) for f in fs)
            summary[cell] = {"name": display, "mod": mod, "region": region, "size_mb": round(size / 1048576), "textures": min(textures, budget) if budget else textures,
                             "triangles": stat(log, "triangles"), "vista_triangles": stat(log, "vista-triangles"),
                             "resources": stat(log, "resources"), "ebx": stat(log, "ebx"), "seconds": round(time.time() - t0)}
            print(f"[{n}/{len(todo)}] {cell}: {display}: {summary[cell]['size_mb']} MB, {summary[cell]['triangles']} triangles, "
                  f"{summary[cell]['resources']} resources, {summary[cell]['seconds']} s", flush=True)
            shutil.rmtree(stage, ignore_errors=True)
            shutil.rmtree(os.path.join(WORK, "map", sid), ignore_errors=True)
            json.dump(summary, open(summary_path, "w", encoding="utf-8"), indent=1)
        if args.install and os.path.isdir(mod_dir):
            dst = os.path.join(MODS, mod)
            if skate_running():
                print("    not installed: Skate is running", flush=True)
            else:
                tmp = os.path.join(MODS, ".incoming_" + mod)
                shutil.rmtree(tmp, ignore_errors=True)
                shutil.copytree(mod_dir, tmp)
                shutil.rmtree(dst, ignore_errors=True)
                os.rename(tmp, dst)
                print(f"    installed into {dst}", flush=True)
    json.dump(summary, open(summary_path, "w", encoding="utf-8"), indent=1)
    print("done", flush=True)


if __name__ == "__main__":
    sys.exit(main())
