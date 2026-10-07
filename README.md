# Los Santos for ReSkate

Tools that turn **your own copy of GTA V** into levels for skate. running with
[ReSkate](https://github.com/Dingo-Shenanigans/ReSkate): the city of Los Santos street for street at 1:1, with its
interiors, collision, bus stops for fast travel, a pause-menu map and a loading picture per level.

**Nothing from GTA V is in this repository.** There are no models, textures or map data here, only code. You point the
tools at your install, they read it (never write to it) and build the levels on your PC. That is also why you get to
choose the area, the size and the quality.

Project page with maps of every level: see [`docs/`](docs/index.html).

## What you get

| Layout | Levels | Level size | Props | For |
|---|---|---|---|---|
| `sections` | 50 | 800 m | yes | the lightest: fastest loading and best frame rate, more level switches |
| `city` | 16 | 1.4 km | yes | the same streets in fewer, larger levels (four of the small squares each) |
| `landmarks` | 6 | 800 m to 1 km | yes | one famous place in the middle of a level |
| `big` | 2 | 2 km | no | a clean canvas for your own rails and ramps, and the longest lines |

Or any box of the map you like, with `level`.

Why several layouts: all of Los Santos does not fit in one skate. level. The game has a fixed table of texture slots
(20,480, about 8,000 used by the game itself) and a fixed mesh memory pool; one 2 km level of downtown with everything
in it needs about 15,000 more textures and hangs on loading. Bigger levels mean fewer loading screens but more in memory
at once. "Props" are the loose street objects (lamp posts, benches, bins, fences, signs, most plants): with them the
streets are as they really are; without them a level is far lighter and an open canvas for the game's builder.

## What you need

- Windows, GTA V installed, and skate. set up with ReSkate.
- ReSkate Studio (the map compiler of the ReSkate project; `reskate_cli.exe`).
- [.NET 8 SDK](https://dotnet.microsoft.com/download) and Python 3 with `pip install numpy pillow`.
- Disk space: about 7 GB for the one-time preparation, then 0.1 to 1 GB per level. 16 GB of RAM or more.

## Steps

```
copy config.example.json config.json        (then edit it: where GTA V, skate., ReSkate Studio, Mods and a work folder are)
python lossantos.py check
python lossantos.py prepare                 once: builds the tools, reads collision / interiors / water from your game
```

Build a ready-made layout, all of it or only the squares you want (codes as on the maps in `docs/`):

```
python lossantos.py build sections
python lossantos.py build city --only B3,C3
python lossantos.py build big
```

or a level of your own, anywhere on the map:

```
python lossantos.py level Pier --region -2000,-1700,-1200,-700 --spawn -1630,-1010
```

Then merge a layout into one mod (identical data of several levels is stored once) and install it:

```
python lossantos.py pack city
python lossantos.py install LosSantos_City
```

Building pauses while skate. is running: an export or compile takes several GB of memory, and the game needs it.
Everything is resumable; a level that is already built is skipped unless you pass `--force`.

## Choosing where

`--region west,south,east,north` is a box in the map's own coordinates, in metres. X grows to the east, Y to the north.
Legion Square is at about (195, -934), the airport around (-1200, -2900), the Vinewood sign near (710, 1200).
`--spawn x,y` is where you start; the tools move it to the nearest open road. The grid maps in `docs/` use the same
coordinates: the small squares are 600 m, starting at x = -2500 in the west and y = 1200 in the north.

## Choosing size and quality

| Option | What it does | Guide |
|---|---|---|
| size of `--region` | how much of the map is in one level | 800 m runs everywhere; 1.4 km is fine in most places and needs `--max-textures` downtown; 2 km needs `--no-props` as well |
| `--no-props` | leaves out street furniture and most plants | a clean canvas, and a far lighter level |
| `--no-interiors` | leaves out the metro, tunnels, car parks, shops | saves textures where you do not need them |
| `--textures 256` | largest texture side in pixels | 256 is the default; 512 is sharper and about three times the size |
| `--detail 1` | which of the game's model versions is used | 0 best, 1 default, 2 and 3 lighter |
| `--max-textures 5400` | keeps the most visible textures, flat colours for the rest | about 5,400 per level is safe; the build prints how many a level has |
| `--far-view 0.03` | how detailed the distant city is | 0.03 is light; up to 0.5 looks better on open ground and costs memory |

`python lossantos.py model NAME --region ...` writes any region, or the whole map, as an ordinary glTF scene
(metres, Y up) for other uses.

## How it works

1. `tools/gta-export` (C#, on [CodeWalker.Core](https://github.com/dexyfex/CodeWalker)) reads the game's collision,
   interiors, materials and water into the work folder.
2. `tools/gta-skate` (C#) selects the map objects of a region, builds meshes and textures, collision pieces that the
   game can find grind edges on, bus stops, the pause-menu picture, and writes ReSkate Studio's "normalized map".
3. `tools/build_sections.py` drives a layout: export, texture budget (`tools/texbudget`), loading picture
   (`tools/make_art.py`), a check for geometry Studio would blow up (`tools/uv_gate.py`), then `reskate_cli compile-map`.
4. `tools/merge_pack.py` merges many compiled levels into one mod the way ReSkate itself merges mods at launch.

A level is stamped with the exact Skate.exe it was built for: rebuild after a game update.

## Status

This is how the author's own levels were built, on one PC. The steps above have **not yet been run on a clean second
machine**; expect rough edges, and please open an issue with the log from the work folder's `logs/` if something fails.

## Credits and licence

Made by **petergowild** over several days, for the love of skate. and GTA, partly with the help of Claude (Anthropic's
AI assistant), which wrote much of the code. Discord: `petergowild`.

- Code: GNU General Public License v3 or later, see [LICENSE](LICENSE). `tools/fbebx.py` and parts of
  `tools/merge_pack.py` are Python ports of code from ReSkate, which is GPL-3.
- `third_party/codewalker-bin`: CodeWalker.Core by dexyfex, unmodified, under its own licence (see the notice beside it).
- ReSkate Studio is not part of this repository; get it from the ReSkate project.

A fan project. Not affiliated with or endorsed by Rockstar Games, Take-Two Interactive, Electronic Arts or Full Circle.
Grand Theft Auto and skate. are trademarks of their owners. You need to own both games; do not share levels built from
game data you do not have the right to distribute.
