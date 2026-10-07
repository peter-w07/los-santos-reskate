"""Four separate mods instead of one, so the owner can enable just the kind of level they want:

    LosSantos_City       16 city levels, 2 x 2 squares (1.4 km), props             build/blocks.json
    LosSantos_Landmarks   6 landmark levels, props                                 build/blocks.json (row S)
    LosSantos_Big         2 big 2 km levels, no props                              build/blocks.json (row X)
    LosSantos_Sections   50 small city sections (800 m), props: the lightest      build/sections.json

    python tools/make_packs.py [name ...]        -> build/pack/<name> (+ <name>.trainer, <name>.report.json)

Each is merged with tools/merge_pack.py (identical data stored once) and gets a manifest, a README and its map.
Level ids differ between the sets (lsc_* and ls_*), so all four can be installed together.
"""
import json
import os
import re
import shutil
import subprocess
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfg  # noqa: E402
WORK = cfg.WORK
PACKS = [
    dict(name="LosSantos_City", src="city.json", pick=lambda s: not s.get("special"), map="map_1_city_levels.png",
         title="Los Santos: city levels", what="{n} city levels of 1.4 km (2 x 2 squares), with props",
         notes=["Rows A to D run north to south, columns 1 to 4 west to east (see the map picture in this folder).",
                "Each level is a 1200 m square plus a 100 m rim, so neighbours overlap by 200 m. Bus stops named `to LS-..` stand at",
                "the edge towards each neighbour; the other stops are the districts inside the level.",
                "In the densest levels the least visible textures (well under 1 % of what is on screen) are flat colours."]),
    dict(name="LosSantos_Landmarks", src="city.json", pick=lambda s: s.get("special") and s["row"] == "S", map="map_2_landmark_levels.png",
         title="Los Santos: landmark levels", what="{n} landmark levels with props, each built around one place",
         notes=["The same streets as the city levels, cut so one place sits in the middle of a level."]),
    dict(name="LosSantos_Big", src="city.json", pick=lambda s: s.get("special") and s["row"] == "X", map="map_3_big_levels.png",
         title="Los Santos: big levels", what="{n} big levels of 2 km, without props",
         notes=["Street furniture and most plants are left out so a level this large fits the game's limits; buildings, roads",
                "and interiors are all there. The heaviest of the four mods."]),
    dict(name="LosSantos_Sections", src="sections.json", pick=lambda s: not s.get("special"), map="map_1_city_sections.png",
         title="Los Santos: small sections", what="{n} small city sections of 800 m, with props",
         notes=["The lightest way to play the city: small levels load fast and keep the frame rate up.",
                "Rows A to H run north to south, columns 1 to 7 west to east (see the map picture in this folder). Each section is",
                "a 600 m square plus a 100 m rim; bus stops named `to LS-..` stand at the edge towards each neighbour."]),
]

WHY = [
    "## Why there are four kinds of level",
    "",
    "All of Los Santos does not fit in one skate. level. The game has a fixed number of texture slots (20,480, with about",
    "8,000 already used by the game itself) and a fixed amount of memory for meshes; a single 2 km level of downtown with",
    "everything in it needed about 15,000 more textures and simply hung on loading. So the city is cut into levels that",
    "each stay inside those limits, in four flavours, and you install the ones that suit your PC and how you skate:",
    "",
    "| Mod | Level size | Props | For |",
    "|---|---|---|---|",
    "| LosSantos_Sections | 800 m | yes | the lightest: fastest loading and the best frame rate, at the cost of switching level more often |",
    "| LosSantos_City | 1.4 km | yes | the same streets in fewer, larger levels: four of the small squares in one |",
    "| LosSantos_Landmarks | 800 m to 1 km | yes | one famous place in the middle of a level, so a spot is never cut in half by a level edge |",
    "| LosSantos_Big | 2 km | no | the longest lines without a loading screen |",
    "",
    "Larger levels cost more: more textures and geometry in memory at once and a longer far view to draw. In the four",
    "densest city levels and in the big north level the least visible textures (well under 1 % of what is on screen)",
    "are plain colours; that is what lets them load at all.",
    "",
    "## Props or no props",
    "",
    "Props are the loose street-level objects of GTA V: lamp posts, benches, bins, bollards, fences, signs, planters and",
    "most trees and plants.",
    "",
    "- **With props** (Sections, City, Landmarks): the streets as they really are. Made for realistic skating: the spots",
    "  are the ones the map gives you, with everything standing where Rockstar put it.",
    "- **Without props** (Big): a clean canvas. Every building, road, plaza, ledge and staircase is still there, but the",
    "  clutter is gone, which is also what makes a 2 km level fit. Made for dropping your own rails, ramps and objects",
    "  with the game's builder, for planning trick lines, and for just messing around over a long distance.",
    ""
]


def build(p):
    doc = json.load(open(os.path.join(cfg.LAYOUTS, p["src"]), encoding="utf-8"))
    out = os.path.join(WORK, doc.get("out", "mods"))
    summary = json.load(open(os.path.join(out, "summary.json"), encoding="utf-8"))
    levels, missing = [], []
    for s in doc["sections"]:
        if s.get("skip") or not p["pick"](s):
            continue
        cell = f"{s['row']}{s['col']}"
        path = os.path.join(out, doc.get("mod", "LosSantos") + "_" + cell + "_" + re.sub(r"[^A-Za-z0-9]", "", s["name"].title())[:40])
        (levels if os.path.isfile(os.path.join(path, "layout.toc")) else missing).append((cell, s, path))
    if missing:
        print(f"{p['name']}: NOT BUILT, left out:", ", ".join(c for c, _, _ in missing), flush=True)
    if not levels:
        print(f"{p['name']}: nothing to merge", flush=True)
        return False
    pack = os.path.join(WORK, "pack", p["name"])
    rc = subprocess.run([sys.executable, os.path.join(cfg.TOOLS, "merge_pack.py"), "--name", p["name"], "--force"] + [x for _, _, x in levels]).returncode
    if rc != 0 or not os.path.isfile(os.path.join(pack, "layout.toc")):
        print(f"{p['name']}: MERGE FAILED (exit {rc})", flush=True)
        return False
    what = p["what"].format(n=len(levels))
    desc = f"Los Santos for ReSkate: {what}. Built from the owner's own GTA V with the Los Santos for ReSkate tools."
    json.dump({"name": p["name"], "author": cfg.AUTHOR or "unknown", "version_number": "1.0.0", "dependencies": [], "description": desc},
              open(os.path.join(pack, "manifest.json"), "w", encoding="utf-8", newline="\n"), indent=2)
    rows = []
    for cell, s, _ in levels:
        m = 0 if s.get("special") else float(s.get("margin", doc.get("margin", 100)))
        rows.append(f"| LS-{cell} | {s['name']} | {s['x1'] - s['x0'] + 2 * m:.0f} x {s['y1'] - s['y0'] + 2 * m:.0f} m | {summary.get(cell, {}).get('size_mb', '?')} MB |")
    readme = [f"# {p['title']}", "", desc, "",
              "The real GTA V map at 1:1, read from the owner's own game: buildings, roads, terrain, interiors, collision with",
              "the game's own surfaces, bus stops for fast travel, a pause-menu map and a loading picture per level.", ""] + p["notes"] + [""] + WHY + [
              "| Level | Area | Size | Before merging |", "|---|---|---|---|"] + rows + ["",
              "Built for one exact game version (the mod is stamped with it): rebuild after a game update.", "",
              "## Credits", "",
              "Built with Los Santos for ReSkate, the tools by petergowild (made over several days, partly with the help of",
              "Claude, Anthropic's AI assistant, for the love of skate. and GTA).", ""]
    open(os.path.join(pack, "README.md"), "w", encoding="utf-8", newline="\n").write("\n".join(readme))
    src = os.path.join(WORK, "art", p["map"])
    if os.path.isfile(src):
        shutil.copy2(src, os.path.join(pack, p["map"]))
    icon = os.path.join(WORK, "art", f"icon_{p['name']}.png")       # python tools/make_art.py icons
    if os.path.isfile(icon):
        shutil.copy2(icon, os.path.join(pack, "icon.png"))
    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(pack) for f in fs)
    print(f"PACK {p['name']}: {len(levels)} levels, {size / 2**30:.2f} GB", flush=True)
    return True


if __name__ == "__main__":
    want = set(sys.argv[1:])
    for p in PACKS:
        if not want or p["name"] in want:
            build(p)
