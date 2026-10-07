"""Writes a small static website about the project: docs/index.html with its pictures in docs/img (GitHub Pages serves docs/).

    python tools/make_site.py

One page: what the four mods are, why the city is split the way it is, props or no props, the maps with every
level's name, how to get around, credits and where to find the author. Level names and sizes are read from
build/blocks.json, build/sections.json and build/pack, so run it again after a rebuild. No scripts, no external
files: the folder can be opened from disk or uploaded to any static host as it is.
"""
import html
import json
import os
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfg  # noqa: E402
WORK = cfg.WORK
SITE = os.path.join(cfg.REPO, "docs")
ART = os.path.join(WORK, "art")
AUTHOR = "petergowild"
DISCORD = "petergowild"                         # a user name, not a link
GITHUB = "https://github.com/peter-w07"       # change here if the account is named differently
REPO = "https://github.com/peter-w07/los-santos-reskate"     # the tools; change here if the repository is named differently
Image.MAX_IMAGE_PIXELS = None


def levels(src, pick):
    doc = json.load(open(os.path.join(cfg.LAYOUTS, src), encoding="utf-8"))
    return [(f"LS-{s['row']}{s['col']}", s["name"]) for s in doc["sections"] if not s.get("skip") and pick(s)]


def size_gb(name):
    p = os.path.join(WORK, "pack", name)
    if not os.path.isdir(p):
        return None
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(p) for f in fs) / 2 ** 30


def picture(src, dst, width):
    p = os.path.join(ART, src)
    if not os.path.isfile(p):
        return False
    im = Image.open(p).convert("RGB")
    if im.width > width:
        im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
    im.save(os.path.join(SITE, "img", dst), quality=86) if dst.endswith(".jpg") else im.save(os.path.join(SITE, "img", dst))
    return True


NUMBER = {1: "One", 2: "Two", 3: "Three", 4: "Four"}
MODS = [
    dict(id="LosSantos_Sections", word="Sections", colour="#5aaaff", size="800 m", props="With props",
         lv=levels("sections.json", lambda s: not s.get("special")), map="map_1_city_sections.png",
         line="The lightest way to play the city. Small levels load fast and keep the frame rate up; you switch level more often.",
         who="Lower-end PCs, and anyone who wants the best frame rate."),
    dict(id="LosSantos_City", word="City", colour="#78dc50", size="1.4 km", props="With props",
         lv=levels("city.json", lambda s: not s.get("special")), map="map_1_city_levels.png",
         line="The same streets in fewer, larger levels: each one is four of the small squares put together.",
         who="Most players. Long lines with far fewer loading screens."),
    dict(id="LosSantos_Big", word="Big", colour="#ff40c8", size="2 km", props="No props",
         lv=levels("city.json", lambda s: s.get("special") and s["row"] == "X"), map="map_3_big_levels.png",
         line="Two huge levels with the street clutter taken out: a clean canvas of buildings, plazas, ledges and stairs.",
         who="Building your own spots with the game's builder, planning trick lines, and cruising a long way."),
]


def main():
    os.makedirs(os.path.join(SITE, "img"), exist_ok=True)
    # the banner: the city itself, no labels (downtown and the coast, north up)
    import make_art
    make_art.Frame(make_art.OVERVIEW).crop(-2700, -1950, 1900, 350, (1800, 900)).save(os.path.join(SITE, "img", "overview.jpg"), quality=86)
    for m in MODS:
        m["has_map"] = picture(m["map"], m["id"] + "_map.jpg", 1500)
        m["has_icon"] = picture("icon_" + m["id"] + ".png", m["id"] + "_icon.png", 256)
        m["gb"] = size_gb(m["id"])
    sample = os.path.join(WORK, "map", "next_d5", "ui", "loading_screen.png")
    has_sample = os.path.isfile(sample)
    if has_sample:
        Image.open(sample).convert("RGB").resize((1600, 900), Image.LANCZOS).save(os.path.join(SITE, "img", "loading.jpg"), quality=86)

    e = html.escape
    cards, maps = [], []
    for m in MODS:
        gb = f"{m['gb']:.1f} GB" if m["gb"] else ""
        icon = f'<img class="icon" src="img/{m["id"]}_icon.png" alt="" width="128" height="128">' if m["has_icon"] else ""
        cards.append(f'''
      <article class="card" style="--c:{m["colour"]}">
        {icon}
        <div>
          <h3>{e(m["word"])}</h3>
          <p class="facts"><span>{len(m["lv"])} levels</span><span>{e(m["size"])}</span><span>{e(m["props"])}</span>{f"<span>{gb}</span>" if gb else ""}</p>
          <p>{e(m["line"])}</p>
          <p class="who"><b>For:</b> {e(m["who"])}</p>
          <p class="file"><code>{e(m["id"])}</code></p>
        </div>
      </article>''')
        rows = "".join(f"<li><b>{e(c)}</b> {e(n)}</li>" for c, n in m["lv"])
        pic = f'<a href="img/{m["id"]}_map.jpg"><img src="img/{m["id"]}_map.jpg" alt="Map of the {e(m["word"])} levels" loading="lazy"></a>' if m["has_map"] else ""
        maps.append(f'''
      <section class="map" style="--c:{m["colour"]}">
        <h3>{e(m["word"])} <small>{len(m["lv"])} levels, {e(m["size"])}, {e(m["props"].lower())}</small></h3>
        {pic}
        <details><summary>All {len(m["lv"])} level names</summary><ul class="names">{rows}</ul></details>
      </section>''')

    total = sum(len(m["lv"]) for m in MODS)
    page = f'''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Los Santos for ReSkate</title>
<meta name="description" content="The city of Los Santos as {total} levels for skate. with ReSkate, in {NUMBER[len(MODS)].lower()} mods: small sections, city levels and big levels.">
<style>
  :root {{ --bg:#10141a; --panel:#18202a; --line:#2a3644; --ink:#eef2f6; --soft:#aeb9c6; --mark:#ffc42e; }}
  * {{ box-sizing:border-box; }}
  html {{ scroll-behavior:smooth; }}
  body {{ margin:0; background:var(--bg); color:var(--ink); font:17px/1.6 "Segoe UI", system-ui, -apple-system, Roboto, Helvetica, Arial, sans-serif; }}
  a {{ color:var(--mark); }}
  img {{ max-width:100%; height:auto; display:block; }}
  .wrap {{ max-width:1080px; margin:0 auto; padding:0 20px; }}
  header {{ padding:56px 0 28px; }}
  header .eyebrow {{ letter-spacing:.2em; text-transform:uppercase; color:var(--soft); font-size:14px; margin:0; }}
  h1 {{ font-size:clamp(40px, 8vw, 84px); line-height:1; margin:.1em 0 .25em; letter-spacing:-.01em; text-wrap:balance; }}
  h1 span {{ color:var(--mark); }}
  header p.lead {{ font-size:20px; color:var(--soft); max-width:46em; margin:0; }}
  nav {{ display:flex; flex-wrap:wrap; gap:8px 18px; margin:22px 0 0; font-size:15px; }}
  nav a {{ color:var(--ink); text-decoration:none; border-bottom:2px solid var(--line); padding-bottom:2px; }}
  nav a:hover {{ border-color:var(--mark); }}
  .hero {{ border:1px solid var(--line); border-radius:10px; overflow:hidden; margin:28px 0 0; }}
  section.block {{ padding:44px 0 8px; }}
  h2 {{ font-size:30px; margin:0 0 .5em; text-wrap:balance; }}
  h3 {{ font-size:22px; margin:0 0 .3em; }}
  h3 small {{ font-size:15px; font-weight:400; color:var(--soft); margin-left:.4em; }}
  p {{ margin:0 0 1em; max-width:46em; }}
  .cards {{ display:grid; grid-template-columns:repeat(auto-fit, minmax(420px, 1fr)); gap:16px; }}
  .card {{ display:flex; gap:18px; background:var(--panel); border:1px solid var(--line); border-top:4px solid var(--c); border-radius:10px; padding:18px; }}
  .card .icon {{ border-radius:8px; flex:none; width:128px; height:128px; }}
  .card h3 {{ color:var(--c); }}
  .card p {{ margin:0 0 .5em; font-size:16px; }}
  .facts {{ display:flex; flex-wrap:wrap; gap:6px; }}
  .facts span {{ border:1px solid var(--line); border-radius:999px; padding:1px 10px; font-size:13.5px; color:var(--soft); white-space:nowrap; }}
  .who {{ color:var(--soft); }}
  code {{ font:14px Consolas, ui-monospace, monospace; color:var(--soft); overflow-wrap:anywhere; }}
  table {{ border-collapse:collapse; width:100%; margin:.4em 0 1.2em; font-size:16px; }}
  th, td {{ text-align:left; padding:9px 12px; border-bottom:1px solid var(--line); vertical-align:top; }}
  th {{ color:var(--soft); font-weight:600; font-size:14px; text-transform:uppercase; letter-spacing:.06em; }}
  .scroll {{ overflow-x:auto; }}
  .two {{ display:grid; grid-template-columns:repeat(auto-fit, minmax(300px, 1fr)); gap:16px; }}
  .two > div {{ background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:18px 20px; }}
  .two h3 {{ font-size:19px; }}
  .two p {{ font-size:16px; margin:0; }}
  .map {{ margin:0 0 34px; }}
  .map h3 {{ border-left:6px solid var(--c); padding-left:12px; }}
  .map img {{ border:1px solid var(--line); border-radius:10px; }}
  details {{ margin-top:10px; }}
  summary {{ cursor:pointer; color:var(--mark); }}
  .names {{ list-style:none; padding:0; margin:12px 0 0; display:grid; grid-template-columns:repeat(auto-fill, minmax(230px, 1fr)); gap:4px 18px; font-size:15px; }}
  .names b {{ color:var(--soft); font-weight:600; margin-right:.3em; }}
  .soon {{ margin-top:16px; background:linear-gradient(135deg, #2a2110, var(--panel) 60%); border:1px solid #6b5416; border-left:6px solid var(--mark); border-radius:10px; padding:18px 22px; }}
  .soon .tag {{ margin:0 0 .2em; color:var(--mark); font-size:13.5px; letter-spacing:.14em; text-transform:uppercase; font-weight:600; }}
  .soon p {{ margin:0; }}
  .button {{ display:inline-block; background:var(--mark); color:#161a20; font-weight:600; text-decoration:none; padding:9px 18px; border-radius:8px; }}
  .plain {{ margin:0 0 1.2em; padding-left:1.2em; max-width:46em; }}
  .plain li {{ margin:.25em 0; }}
  .steps {{ max-width:52em; padding-left:1.3em; }}
  .steps li {{ margin:0 0 1em; }}
  pre {{ background:#0b0e12; border:1px solid var(--line); border-radius:8px; padding:10px 14px; margin:.5em 0; overflow-x:auto; font:14.5px/1.5 Consolas, ui-monospace, monospace; color:#dfe6ee; }}
  #build h3 {{ margin-top:1.4em; }}
  .about {{ background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:22px 24px; }}
  .social {{ display:flex; flex-wrap:wrap; gap:10px; margin-top:14px; padding:0; list-style:none; }}
  .social li {{ border:1px solid var(--line); border-radius:8px; padding:8px 14px; }}
  .social span {{ color:var(--soft); font-size:14px; display:block; }}
  footer {{ color:var(--soft); font-size:14px; padding:36px 0 56px; }}
  @media (max-width:520px) {{ .card {{ flex-direction:column; }} .cards {{ grid-template-columns:1fr; }} }}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <p class="eyebrow">A map project by {e(AUTHOR)}</p>
    <h1>Los Santos <span>for ReSkate</span></h1>
    <p class="lead">The whole city, street for street at 1:1, as {total} levels you can skate in skate. with ReSkate: from the airport up to the Vinewood hills and from Del Perro Pier across to Mirror Park.</p>
    <nav>
      <a href="#mods">The mods</a><a href="#soon">Coming soon</a><a href="#why">Why it is split</a><a href="#props">Props or no props</a>
      <a href="#maps">Maps and level names</a><a href="#build">Build it yourself</a><a href="#around">Getting around</a><a href="#about">About</a>
    </nav>
    <div class="hero"><img src="img/overview.jpg" alt="Los Santos from above: downtown and the coast"></div>
  </header>

  <section class="block" id="mods">
    <h2>{NUMBER[len(MODS)]} mods, pick what suits you</h2>
    <p>The city comes as {NUMBER[len(MODS)].lower()} separate mods. They do not clash, so you can enable one, some or all of them.</p>
    <div class="cards">{"".join(cards)}
    </div>
    <div class="soon" id="soon">
      <p class="tag">Coming soon</p>
      <h3>One custom map with all the popular skate spots</h3>
      <p>A combined map of GTA that brings the best-known skate spots of the city together in one place, built for servers, so a whole session can skate the highlights without changing level.</p>
    </div>
  </section>

  <section class="block" id="why">
    <h2>Why the city is split up</h2>
    <p>All of Los Santos does not fit in one skate. level. The game has a fixed number of texture slots (20,480, and about 8,000 are used by the game itself) and a fixed amount of memory for meshes. A single 2&nbsp;km level of downtown with everything in it needed about 15,000 more textures and simply hung on loading.</p>
    <p>So the city is cut into levels that each stay inside those limits. Bigger levels mean fewer loading screens, but more held in memory at once and a longer view to draw; smaller levels are the other way round. That trade is the whole reason there is more than one version.</p>
    <div class="scroll"><table>
      <tr><th>Mod</th><th>Level size</th><th>Props</th><th>What you gain</th><th>What it costs</th></tr>
      <tr><td>Sections</td><td>800 m</td><td>Yes</td><td>Fastest loading, best frame rate</td><td>More level switches</td></tr>
      <tr><td>City</td><td>1.4 km</td><td>Yes</td><td>Four times the area per level</td><td>Heavier; in the four densest levels the least visible textures are plain colours</td></tr>
      <tr><td>Big</td><td>2 km</td><td>No</td><td>The longest lines without loading</td><td>No street furniture; the heaviest levels</td></tr>
    </table></div>
  </section>

  <section class="block" id="props">
    <h2>Props or no props</h2>
    <p>Props are the loose street-level objects of the city: lamp posts, benches, bins, bollards, fences, signs, planters, and most trees and plants.</p>
    <div class="two">
      <div><h3>With props: Sections, City</h3><p>The streets as they really are, with everything standing where it stands in the original. Made for realistic skating: the spots are the ones the map gives you.</p></div>
      <div><h3>Without props: Big</h3><p>A clean canvas. Every building, road, plaza, ledge and staircase is still there, but the clutter is gone, which is also what lets a 2&nbsp;km level fit. Made for dropping your own rails, ramps and objects with the game's builder, for planning trick lines, and for just messing around.</p></div>
    </div>
  </section>

  <section class="block" id="maps">
    <h2>Maps and level names</h2>
    <p>Every level is named by its place on a grid: a letter for the row (north to south), a number for the column (west to east), then the district. Click a map to see it full size.</p>
    {"".join(maps)}
  </section>

  <section class="block" id="around">
    <h2>Getting around</h2>
    <div class="two">
      <div><h3>Levels overlap</h3><p>Neighbouring levels share a 200&nbsp;m strip, so you can skate a little way into the next level before you need to switch.</p></div>
      <div><h3>Bus stops</h3><p>Each level has fast-travel stops for the districts inside it, and one at the edge towards every neighbour, named after it (for example "to LS-C3 (south)").</p></div>
      <div><h3>Loading screens and the pause map</h3><p>Each level's loading screen shows it from above with its name and where it lies in the city, and the pause menu has a map of the level.</p></div>
      <div><h3>Interiors</h3><p>The metro, tunnels, car parks and other interiors are in their real places, in every mod.</p></div>
    </div>
    {'<div class="hero"><img src="img/loading.jpg" alt="Example loading screen: Legion Square" loading="lazy"></div>' if has_sample else ''}
  </section>

  <section class="block" id="build">
    <h2>Build it yourself</h2>
    <p>The levels are made by a set of open tools that read <b>your own copy of GTA V</b> and write ReSkate levels. Nothing from the game is in the repository: you point the tools at your install and they build the levels on your PC. That also means you choose the area, the size and the quality.</p>
    <p><a class="button" href="{e(REPO)}">The tools on GitHub</a></p>

    <h3>What you need</h3>
    <ul class="plain">
      <li>GTA V installed (any PC version the tools can read), and skate. set up with <a href="https://github.com/Dingo-Shenanigans/ReSkate">ReSkate</a>.</li>
      <li>ReSkate Studio, the map compiler from the ReSkate project.</li>
      <li>The .NET 8 SDK and Python 3 (with Pillow and numpy), both free.</li>
      <li>Disk space: about 7 GB for the one-time preparation, plus 0.1 to 1 GB per level.</li>
    </ul>

    <h3>The steps</h3>
    <ol class="steps">
      <li><b>Set your folders.</b> Copy <code>config.example.json</code> to <code>config.json</code> and fill in where GTA V, skate., ReSkate Studio and your ReSkate Mods folder are, and a work folder with room to spare.
        <pre>python lossantos.py check</pre></li>
      <li><b>Prepare, once.</b> Builds the tools and reads the collision, interiors and water of the map from your game (a few minutes).
        <pre>python lossantos.py prepare</pre></li>
      <li><b>Build levels.</b> Either one of the ready-made layouts shown on this page, all of it or only the squares you want,
        <pre>python lossantos.py build sections
python lossantos.py build city --only B3,C3
python lossantos.py build big</pre>
        or a level of your own, anywhere on the map:
        <pre>python lossantos.py level Pier --region -2000,-1700,-1200,-700 --spawn -1630,-1010</pre></li>
      <li><b>Pack and install.</b> Merge a layout into one mod (shared data is stored once) and copy it into your Mods folder.
        <pre>python lossantos.py pack city
python lossantos.py install LosSantos_City</pre></li>
    </ol>

    <h3>Choosing where</h3>
    <p>A region is a box in the map's own coordinates, in metres: <code>--region west,south,east,north</code>. X grows to the east and Y to the north; Legion Square is at about (195, -934), the airport around (-1200, -2900), the Vinewood sign near (710, 1200). <code>--spawn x,y</code> is where you start; the tools put you on the nearest open road. The grid maps above are drawn in the same coordinates: the small squares are 600&nbsp;m, starting at x&nbsp;=&nbsp;-2500 in the west and y&nbsp;=&nbsp;1200 in the north.</p>

    <h3>Choosing size and quality</h3>
    <div class="scroll"><table>
      <tr><th>Option</th><th>What it does</th><th>Guide</th></tr>
      <tr><td><code>--region</code> size</td><td>How much of the map is in one level</td><td>800 m runs everywhere. 1.4 km is fine in most places and needs <code>--max-textures</code> downtown. 2 km needs <code>--no-props</code> as well.</td></tr>
      <tr><td><code>--no-props</code></td><td>Leaves out street furniture and most plants</td><td>A clean canvas, and a far lighter level.</td></tr>
      <tr><td><code>--no-interiors</code></td><td>Leaves out the metro, tunnels, car parks and shops</td><td>Saves textures where you do not need them.</td></tr>
      <tr><td><code>--textures 256</code></td><td>Largest texture side in pixels</td><td>256 is the default; 512 is sharper and about three times the size.</td></tr>
      <tr><td><code>--detail 1</code></td><td>Which of the game's model versions is used</td><td>0 is the best, 1 the default, 2 and 3 are lighter.</td></tr>
      <tr><td><code>--max-textures 5400</code></td><td>Keeps the most visible textures and turns the rest into flat colours</td><td>The game has a fixed number of texture slots; about 5,400 per level is safe. The tools tell you how many a level has.</td></tr>
      <tr><td><code>--far-view 0.03</code></td><td>How detailed the distant city is</td><td>0.03 is light; up to 0.5 looks better on open ground and costs memory.</td></tr>
    </table></div>
    <p>There is also <code>python lossantos.py model</code>, which writes any region, or the whole map, as an ordinary glTF 3D model for other uses.</p>
  </section>

  <section class="block" id="about">
    <h2>About</h2>
    <div class="about">
      <p>I'm {e(AUTHOR)}. I built this over several days because I really love skate. and GTA, and I wanted to see the two together.</p>
      <p>It was made partly with the help of Claude, Anthropic's AI assistant, which wrote the exporter and the build tools; the direction, the testing and the many rebuilds were mine.</p>
      <ul class="social">
        <li><span>Discord</span>{e(DISCORD)}</li>
        <li><span>GitHub</span><a href="{e(GITHUB)}">{e(GITHUB.replace("https://", ""))}</a></li>
        <li><span>The tools</span><a href="{e(REPO)}">{e(REPO.replace("https://github.com/", ""))}</a></li>
      </ul>
    </div>
  </section>

  <footer>
    <p>A fan project. Not affiliated with or endorsed by Rockstar Games, Take-Two Interactive, Electronic Arts or Full Circle. Grand Theft Auto and skate. are trademarks of their owners.</p>
  </footer>
</div>
</body>
</html>
'''
    open(os.path.join(SITE, "index.html"), "w", encoding="utf-8", newline="\n").write(page)
    print("wrote", os.path.join(SITE, "index.html"), f"{len(page) / 1024:.0f} KB,", total, "levels")


if __name__ == "__main__":
    main()
