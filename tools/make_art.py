"""Pictures for the Los Santos pack: the overview map with every level's box, and a loading screen per level.

    python tools/make_art.py overview                 -> build/art/los_santos_levels.png (+ a 2560 x 1440 version)
    python tools/make_art.py splits                   -> build/art/map_1_city_sections.png, map_2_landmark_levels.png, map_3_big_levels.png
    python tools/make_art.py loading <map folder> D5  -> <map folder>/ui/loading_screen.png (Studio's loading picture)

Both need the top-down picture of the whole city, build/next_test/overview/ui/map_image.png, made with
    gta-skate export --name overview --region -2700,-3800,1900,1800 --out build/next_test --map-height 6480 ...
build_sections.py calls loading() for every level after its export; a level's own ui/map_image.png is the sharp
picture of its area. Studio reads ui/loading_screen.png (2560 x 1440) and builds the game's loading screen from it.
"""
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfg  # noqa: E402
WORK = cfg.WORK
OVERVIEW = os.path.join(WORK, "next_test", "overview", "ui")
SEA = (28, 58, 84)
INK = (16, 20, 26)
GRID = (255, 255, 255)
MARK = (255, 196, 46)       # the level being loaded
LAND = (255, 120, 60)       # landmark levels
Image.MAX_IMAGE_PIXELS = None


def font(size, bold=True):
    for name in (("bahnschrift.ttf",) if bold else ("bahnschrift.ttf",)) + ("segoeuib.ttf", "arialbd.ttf", "arial.ttf"):
        path = os.path.join(r"C:\Windows\Fonts", name)
        if os.path.exists(path):
            f = ImageFont.truetype(path, size)
            try:
                f.set_variation_by_name("Bold" if bold else "SemiLight")
            except Exception:
                pass
            return f
    return ImageFont.load_default()


class Frame:
    """A top-down picture and the GTA rectangle it shows (ui/map_image.json: game z is -GTA y)."""

    def __init__(self, folder):
        j = json.load(open(os.path.join(folder, "map_image.json")))
        self.x0, self.x1, self.y0, self.y1 = j["min_x"], j["max_x"], -j["max_z"], -j["min_z"]
        self.im = Image.open(os.path.join(folder, "map_image.png")).convert("RGBA")
        self.w, self.h = self.im.size

    def px(self, x, y):
        return (x - self.x0) / (self.x1 - self.x0) * self.w, (self.y1 - y) / (self.y1 - self.y0) * self.h

    def crop(self, x0, y0, x1, y1, size):
        """The GTA rectangle as an RGB picture of the given size, sea colour where nothing was drawn."""
        a, b = self.px(x0, y1)
        c, d = self.px(x1, y0)
        part = self.im.crop((round(a), round(b), round(c), round(d))).resize(size, Image.LANCZOS)
        out = Image.new("RGB", size, SEA)
        out.paste(part, (0, 0), part)
        return out


SECTIONS_FILE = os.path.join(cfg.LAYOUTS, "sections.json")     # build_sections.py points this at the set it builds


def sections():
    doc = json.load(open(SECTIONS_FILE, encoding="utf-8"))
    return [s for s in doc["sections"] if not s.get("skip")], float(doc.get("margin", 100))


def code(s):
    return f"{s['row']}{s['col']}"


def extent(live):
    return (min(s["x0"] for s in live) - 100, min(s["y0"] for s in live) - 100,
            max(s["x1"] for s in live) + 100, max(s["y1"] for s in live) + 100)


def wrap(draw, text, fnt, width):
    lines, line = [], ""
    for word in text.split():
        t = (line + " " + word).strip()
        if draw.textlength(t, font=fnt) <= width or not line:
            line = t
        else:
            lines.append(line)
            line = word
    return lines + [line] if line else lines


def city_map(frame, live, scale, current=None, labels=True):
    """The city with every level's box. Grid levels: their 600 m core (the built level reaches 100 m further,
    so neighbours overlap). Landmark levels: the whole built box, in orange."""
    x0, y0, x1, y1 = extent(live)
    size = (round((x1 - x0) * scale), round((y1 - y0) * scale))
    im = frame.crop(x0, y0, x1, y1, size).convert("RGBA")
    if current is not None:
        im = Image.alpha_composite(im, Image.new("RGBA", size, (10, 14, 20, 120)))
    over = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(over)

    def box(s):
        return [(s["x0"] - x0) * scale, (y1 - s["y1"]) * scale, (s["x1"] - x0) * scale, (y1 - s["y0"]) * scale]

    lw = max(1, round(3 * scale))
    for s in live:
        if s.get("special"):
            continue
        b = box(s)
        d.rectangle(b, outline=GRID + (170,), width=lw)
        if labels:
            f1, f2 = font(max(10, round(96 * scale))), font(max(8, round(40 * scale)), False)
            d.text((b[0] + 14 * scale, b[1] + 8 * scale), code(s), font=f1, fill=(255, 255, 255, 235), stroke_width=max(1, round(4 * scale)), stroke_fill=INK + (255,))
            yy = b[1] + 112 * scale
            for line in wrap(d, s["name"], f2, b[2] - b[0] - 24 * scale):
                d.text((b[0] + 16 * scale, yy), line, font=f2, fill=(255, 255, 255, 235), stroke_width=max(1, round(3 * scale)), stroke_fill=INK + (255,))
                yy += 46 * scale
    for s in live:
        if not s.get("special"):
            continue
        b = box(s)
        d.rectangle(b, outline=LAND + (255,), width=max(2, round(6 * scale)))
        if labels:
            f2 = font(max(8, round(46 * scale)))
            text = f"{code(s)}  {s['name']}"
            tw = d.textlength(text, font=f2)
            d.rectangle([b[0], b[3] - 62 * scale, b[0] + tw + 28 * scale, b[3]], fill=LAND + (235,))
            d.text((b[0] + 14 * scale, b[3] - 56 * scale), text, font=f2, fill=INK + (255,))
    if current is not None:
        m = 0 if current.get("special") else 100
        b = [(current["x0"] - m - x0) * scale, (y1 - current["y1"] - m) * scale, (current["x1"] + m - x0) * scale, (y1 - current["y0"] + m) * scale]
        # the level itself at full brightness, inside a marked box
        a, bb, c, dd = [round(v) for v in b]
        bright = frame.crop(current["x0"] - m, current["y0"] - m, current["x1"] + m, current["y1"] + m, (max(1, c - a), max(1, dd - bb)))
        im.paste(bright, (a, bb))
        d.rectangle(b, outline=MARK + (255,), width=max(3, round(14 * scale)))
    return Image.alpha_composite(im, over).convert("RGB"), (x0, y0, x1, y1)


def overview():
    frame = Frame(OVERVIEW)
    live, _ = sections()
    scale = 0.75
    city, _ = city_map(frame, live, scale)
    head, foot = 230, 150
    out = Image.new("RGB", (city.width, city.height + head + foot), INK)
    out.paste(city, (0, head))
    d = ImageDraw.Draw(out)
    d.text((40, 30), "LOS SANTOS", font=font(120), fill=(255, 255, 255))
    grid = [s for s in live if not s.get("special")]
    marks = [s for s in live if s.get("special")]
    d.text((44, 160), f"{len(live)} levels for skate.  ·  {len(grid)} city sections (white, 800 m each, neighbours overlap by 200 m)  ·  {len(marks)} landmark levels (orange)",
           font=font(40, False), fill=(200, 208, 218))
    yb = head + city.height + 22
    d.text((44, yb), "Rows run A to H from north to south, columns 1 to 7 from west to east. Every section has a bus stop at the edge towards each neighbour, named after it.",
           font=font(34, False), fill=(200, 208, 218))
    d.text((44, yb + 52), "North is up. One grid square is 600 m.", font=font(34, False), fill=(200, 208, 218))
    art = os.path.join(WORK, "art")
    os.makedirs(art, exist_ok=True)
    out.save(os.path.join(art, "los_santos_levels.png"))
    # a 16:9 version (a desktop picture, or the loading picture of an overview level)
    wide = Image.new("RGB", (2560, 1440), INK)
    h = 1440 - 80
    small = out.resize((round(out.width * h / out.height), h), Image.LANCZOS)
    wide.paste(small, ((2560 - small.width) // 2, 40))
    wide.save(os.path.join(art, "los_santos_levels_2560.png"))
    print("wrote", os.path.join(art, "los_santos_levels.png"), out.size)


# the big no-props levels (build/run_big20.sh)
BIG = [("North 2 km  (no props)", -800, -1600, 1200, 400, (255, 64, 200)),
       ("South 2 km  (no props)", -1900, -3500, 100, -1500, (64, 224, 255))]
# nine border colours for the grid: a section and its eight neighbours never share one
NINE = [(255, 99, 71), (255, 200, 40), (120, 220, 80), (64, 200, 200), (90, 150, 255), (190, 120, 255),
        (255, 130, 190), (255, 160, 60), (170, 230, 150)]
SIX = [(255, 255, 255), (255, 240, 120), (160, 255, 220), (255, 190, 150), (200, 200, 255), (220, 255, 140)]


def dashed(d, box, colour, width, dash):
    x0, y0, x1, y1 = box
    for a, b, horizontal in ((x0, x1, True), (y0, y1, False)):
        t = a
        while t < b:
            e = min(t + dash, b)
            if horizontal:
                d.line([(t, y0), (e, y0)], fill=colour, width=width); d.line([(t, y1), (e, y1)], fill=colour, width=width)
            else:
                d.line([(x0, t), (x0, e)], fill=colour, width=width); d.line([(x1, t), (x1, e)], fill=colour, width=width)
            t += dash * 2


def splits(frame=None):
    """Three simple maps instead of one busy one: the city sections, the landmark levels, the big levels."""
    frame = frame or Frame(OVERVIEW)
    live, margin = sections()
    scale = 0.75
    x0, y0, x1, y1 = extent(live)
    x0 = min([x0] + [b[1] - 100 for b in BIG]); y0 = min([y0] + [b[2] - 100 for b in BIG])
    x1 = max([x1] + [b[3] + 100 for b in BIG]); y1 = max([y1] + [b[4] + 100 for b in BIG])
    size = (round((x1 - x0) * scale), round((y1 - y0) * scale))
    picture = frame.crop(x0, y0, x1, y1, size).convert("RGBA")
    grid = [s for s in live if not s.get("special")]
    marks = [s for s in live if s.get("special")]
    rows = sorted({s["row"] for s in grid})
    art = os.path.join(WORK, "art")
    os.makedirs(art, exist_ok=True)

    def box(ax0, ay0, ax1, ay1):
        return [(ax0 - x0) * scale, (y1 - ay1) * scale, (ax1 - x0) * scale, (y1 - ay0) * scale]

    def sheet(name, title, sub, notes, draw):
        base = Image.alpha_composite(picture, Image.new("RGBA", size, (8, 12, 18, 120)))
        fill = Image.new("RGBA", size, (0, 0, 0, 0))
        line = Image.new("RGBA", size, (0, 0, 0, 0))
        draw(ImageDraw.Draw(fill), ImageDraw.Draw(line))
        city = Image.alpha_composite(Image.alpha_composite(base, fill), line).convert("RGB")
        head, foot = 230, 60 + 56 * len(notes)
        out = Image.new("RGB", (city.width, city.height + head + foot), INK)
        out.paste(city, (0, head))
        d = ImageDraw.Draw(out)
        d.text((40, 30), title, font=font(104), fill=(255, 255, 255))
        d.text((44, 156), sub, font=font(40, False), fill=(200, 208, 218))
        for n, t in enumerate(notes):
            d.text((44, head + city.height + 28 + 56 * n), t, font=font(36, False), fill=(214, 220, 228))
        out.save(os.path.join(art, name))
        print("wrote", os.path.join(art, name), out.size)

    def tag(d, b, text, c, size_px, corner="bl"):
        f = font(size_px)
        tw = d.textlength(text, font=f)
        hh = round(size_px * 1.45)
        bx = b[0] if corner == "bl" else b[2] - tw - 36
        by = b[3] - hh if corner == "bl" else b[1]
        d.rectangle([bx, by, bx + tw + 36, by + hh], fill=c + (255,))
        d.text((bx + 18, by + round(size_px * 0.16)), text, font=f, fill=INK + (255,))

    # 1. the city sections: clean squares, no overlap drawn
    def draw_sections(df, d):
        for s in grid:
            c = NINE[(rows.index(s["row"]) % 3) * 3 + s["col"] % 3]
            b = box(s["x0"], s["y0"], s["x1"], s["y1"])
            df.rectangle(b, fill=c + (56,))
            d.rectangle([b[0] + 3, b[1] + 3, b[2] - 3, b[3] - 3], outline=c + (255,), width=6)
            k = 1.5 if s["x1"] - s["x0"] > 900 else 1.0
            d.text((b[0] + 24, b[1] + 18), code(s), font=font(round(84 * k)), fill=(255, 255, 255, 255), stroke_width=5, stroke_fill=INK + (255,))
            yy = b[1] + 116 * k
            for t in wrap(d, s["name"], font(round(32 * k), False), b[2] - b[0] - 48):
                d.text((b[0] + 26, yy), t, font=font(round(32 * k), False), fill=(255, 255, 255, 255), stroke_width=3, stroke_fill=INK + (255,))
                yy += 38 * k
    pitch = max(s["x1"] - s["x0"] for s in grid)
    big = pitch > 600
    sheet("map_1_city_levels.png" if big else "map_1_city_sections.png", "LOS SANTOS  ·  CITY LEVELS" if big else "LOS SANTOS  ·  CITY SECTIONS",
          f"{len(grid)} levels with props. Each square is one level.",
          [f"Each square is {pitch:.0f} m (the east column is narrower). The level itself reaches 100 m further on every side, so you can skate",
           f"a little way into the next square before you need to switch. Rows {rows[0]} to {rows[-1]} run north to south, columns 1 to {max(s['col'] for s in grid)} west to east."],
          draw_sections)
    if big:
        return      # the landmark and big-level maps belong to the full set (build/sections.json)

    # 2. the landmark levels
    def draw_marks(df, d):
        for n, s in enumerate(marks):
            c = SIX[n % len(SIX)]
            b = box(s["x0"], s["y0"], s["x1"], s["y1"])
            df.rectangle(b, fill=c + (70,))
            d.rectangle(b, outline=INK + (255,), width=14)
            d.rectangle([b[0] + 3, b[1] + 3, b[2] - 3, b[3] - 3], outline=c + (255,), width=8)
            tag(d, b, f"{code(s)}  {s['name']}", c, 44)
            w, h = s["x1"] - s["x0"], s["y1"] - s["y0"]
            d.text((b[0] + 22, b[1] + 16), f"{w:.0f} x {h:.0f} m", font=font(36, False), fill=(255, 255, 255, 255), stroke_width=3, stroke_fill=INK + (255,))
    sheet("map_2_landmark_levels.png", "LOS SANTOS  ·  LANDMARK LEVELS", f"{len(marks)} levels with props, each built around one place.",
          ["These lie on top of the city sections: the same streets, cut so the landmark sits in the middle of one level.",
           "The Diamond Casino level is larger (1000 x 800 m) so the casino and the racetrack are together."],
          draw_marks)

    # 3. the big levels
    def draw_big(df, d):
        for name, ax0, ay0, ax1, ay1, c in BIG:
            b = box(ax0, ay0, ax1, ay1)
            df.rectangle(b, fill=c + (70,))
            d.rectangle(b, outline=INK + (255,), width=18)
            d.rectangle([b[0] + 4, b[1] + 4, b[2] - 4, b[3] - 4], outline=c + (255,), width=10)
        # names after every box, so no box covers another level's name
        for n, (name, ax0, ay0, ax1, ay1, c) in enumerate(BIG):
            b = box(ax0, ay0, ax1, ay1)
            tag(d, b, name, c, 60, "tr" if n == 0 else "bl")
            d.text((b[0] + 28, b[1] + 22), f"{ax1 - ax0:.0f} x {ay1 - ay0:.0f} m", font=font(44, False), fill=(255, 255, 255, 255), stroke_width=3, stroke_fill=INK + (255,))
    sheet("map_3_big_levels.png", "LOS SANTOS  ·  BIG LEVELS", f"{len(BIG)} levels of 2 km, without props.",
          ["Street furniture and most plants are left out so a level this large fits the game's limits; buildings, roads and interiors are all there.",
           "The two overlap by 100 m at Strawberry / Chamberlain Hills."],
          draw_big)


def icons(frame=None):
    """A 256 px icon per mod: the city from above with that mod's level boxes, and its name."""
    global SECTIONS_FILE
    frame = frame or Frame(OVERVIEW)
    keep = SECTIONS_FILE
    art = os.path.join(WORK, "art")
    os.makedirs(art, exist_ok=True)
    X0, Y0, X1, Y1 = -2700, -3700, 1800, 1700        # the whole city, shown above the name band
    S, band = 1024, 250
    k = (S - band) / (Y1 - Y0)
    ox = round((S - (X1 - X0) * k) / 2)
    kinds = [("LosSantos_City", "city.json", "CITY", lambda s: not s.get("special"), (120, 220, 80)),
             ("LosSantos_Landmarks", "city.json", "LANDMARKS", lambda s: s.get("special") and s["row"] == "S", (255, 140, 60)),
             ("LosSantos_Big", "city.json", "BIG", lambda s: s.get("special") and s["row"] == "X", (255, 64, 200)),
             ("LosSantos_Sections", "sections.json", "SECTIONS", lambda s: not s.get("special"), (90, 170, 255))]
    for name, src, word, pick, colour in kinds:
        SECTIONS_FILE = os.path.join(cfg.LAYOUTS, src)
        live, margin = sections()
        im = Image.new("RGBA", (S, S), SEA + (255,))
        im.paste(frame.crop(X0, Y0, X1, Y1, (round((X1 - X0) * k), S - band)), (ox, 0))
        im = Image.alpha_composite(im, Image.new("RGBA", (S, S), (8, 12, 18, 96)))
        over = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        d = ImageDraw.Draw(over)
        for sct in live:
            if not pick(sct):
                continue
            b = [ox + (sct["x0"] - X0) * k, (Y1 - sct["y1"]) * k, ox + (sct["x1"] - X0) * k, (Y1 - sct["y0"]) * k]
            d.rectangle(b, fill=colour + (80,), outline=colour + (255,), width=5 if word in ("SECTIONS", "CITY") else 9)
        # name band
        d.rectangle([0, S - band, S, S], fill=INK + (255,))
        d.rectangle([0, S - band, S, S - band + 12], fill=colour + (255,))
        d.text((48, S - band + 30), "LOS SANTOS", font=font(60, False), fill=(214, 220, 228, 255))
        f = font(140)
        while d.textlength(word, font=f) > S - 96:
            f = font(f.size - 6)
        d.text((44, S - band + 96), word, font=f, fill=(255, 255, 255, 255))
        out = Image.alpha_composite(im, over).convert("RGB").resize((256, 256), Image.LANCZOS)
        out.save(os.path.join(art, f"icon_{name}.png"))
        print("wrote", os.path.join(art, f"icon_{name}.png"))
    SECTIONS_FILE = keep


def loading(map_dir, cell, frame=None):
    """ui/loading_screen.png for one level: its own top-down picture, its name, and where it lies in the city."""
    live, margin = sections()
    sec = next(s for s in live if code(s) == cell)
    frame = frame or Frame(OVERVIEW)
    W, H = 2560, 1440
    out = Image.new("RGB", (W, H), INK)
    m = 0 if sec.get("special") else margin
    bx0, by0, bx1, by1 = sec["x0"] - m, sec["y0"] - m, sec["x1"] + m, sec["y1"] + m
    # the level's own picture is sharper than the city one; fall back to the city picture
    own = os.path.join(map_dir, "ui")
    src = Frame(own) if os.path.exists(os.path.join(own, "map_image.png")) else frame
    # background: the level, enlarged and blurred, darkened
    side = max(bx1 - bx0, by1 - by0)
    bg = src.crop(bx0, by0, bx1, by1, (W, round(W * (by1 - by0) / (bx1 - bx0)))).filter(ImageFilter.GaussianBlur(28))
    bg = bg.crop((0, (bg.height - H) // 2, W, (bg.height - H) // 2 + H)) if bg.height >= H else bg.resize((W, H))
    out.paste(Image.blend(bg, Image.new("RGB", (W, H), INK), 0.62), (0, 0))
    d = ImageDraw.Draw(out)
    # left: the level
    ph = 1200
    pw = round(ph * (bx1 - bx0) / (by1 - by0))
    if pw > 1420:
        pw = 1420
        ph = round(pw * (by1 - by0) / (bx1 - bx0))
    pic = src.crop(bx0, by0, bx1, by1, (pw, ph))
    px, py = 110, (H - ph) // 2
    d.rectangle([px - 6, py - 6, px + pw + 5, py + ph + 5], fill=(255, 255, 255))
    out.paste(pic, (px, py))
    # right: name and the city
    tx = px + pw + 90
    tw = W - tx - 90
    d.text((tx, 120), "LOS SANTOS", font=font(54, False), fill=(200, 208, 218))
    y = 190
    f = font(118)
    for line in wrap(d, sec["name"].upper(), f, tw):
        d.text((tx, y), line, font=f, fill=(255, 255, 255))
        y += 124
    d.text((tx, y + 6), f"LS-{cell}" + ("   " + sec.get("kind", "landmark level") if sec.get("special") else ""), font=font(56), fill=MARK)
    y += 100
    # neighbours
    if not sec.get("special"):
        rows = sorted({s["row"] for s in live if not s.get("special")})
        by = {(s["row"], s["col"]): s for s in live if not s.get("special")}
        ri = rows.index(sec["row"])
        lines = []
        for word, dr, dc in (("North", -1, 0), ("East", 0, 1), ("South", 1, 0), ("West", 0, -1)):
            n = by.get((rows[ri + dr], sec["col"] + dc)) if 0 <= ri + dr < len(rows) else None
            if n:
                lines.append(f"{word}:  LS-{code(n)} {n['name']}")
        for line in lines:
            d.text((tx, y), line, font=font(38, False), fill=(214, 220, 228))
            y += 48
    y += 26
    # where it lies
    x0, y0, x1, y1 = extent(live)
    room_h = H - y - 90
    scale = min(tw / (x1 - x0), room_h / (y1 - y0))
    city, _ = city_map(frame, live, scale, current=sec, labels=False)
    d.rectangle([tx - 3, y - 3, tx + city.width + 2, y + city.height + 2], fill=(200, 208, 218))
    out.paste(city, (tx, y))
    os.makedirs(own, exist_ok=True)
    out.save(os.path.join(own, "loading_screen.png"))
    return out


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "overview":
        overview()
    elif len(sys.argv) > 1 and sys.argv[1] == "icons":
        icons()
    elif len(sys.argv) > 1 and sys.argv[1] == "splits":
        if len(sys.argv) > 2:           # another set of levels: python tools/make_art.py splits build/blocks.json
            SECTIONS_FILE = os.path.abspath(sys.argv[2])
        splits()
    elif len(sys.argv) > 3 and sys.argv[1] == "loading":
        loading(sys.argv[2], sys.argv[3].upper())
        print("wrote", os.path.join(sys.argv[2], "ui", "loading_screen.png"))
    else:
        print(__doc__)
