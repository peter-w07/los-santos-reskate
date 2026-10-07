"""Folders of this machine, read from config.json beside lossantos.py (copy config.example.json and fill it in).

    gta      the GTA V folder (GTA5.exe); only ever read
    skate    the folder with the Skate.exe that ReSkate launches (the tools read it to stamp and merge levels)
    skate_compile  optional: another copy of the game for the compiler to read (default: the same folder)
    studio   the ReSkate Studio folder (reskate_cli.exe)
    mods     ReSkate's Mods folder, for `lossantos.py install`
    work     a folder on a disk with room: everything the tools write goes here (maps, levels, packs, pictures)
    export   optional: where `prepare` keeps the collision, interiors and water read from the game (default work/export)
"""
import json
import os
import subprocess
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(REPO, "tools")
LAYOUTS = os.path.join(REPO, "layouts")
_path = os.path.join(REPO, "config.json")
if not os.path.isfile(_path):
    raise SystemExit("config.json is missing: copy config.example.json to config.json and fill in your folders")
_c = json.load(open(_path, encoding="utf-8"))


def _folder(key, required=True):
    v = _c.get(key)
    if not v:
        if required:
            raise SystemExit(f'config.json: "{key}" is not set')
        return None
    return os.path.abspath(os.path.expandvars(os.path.expanduser(v)))


GTA = _folder("gta")
SKATE = _folder("skate")
STUDIO = _folder("studio")
MODS = _folder("mods", required=False)
WORK = _folder("work")
EXPORT = _folder("export", required=False) or os.path.join(WORK, "export")
SKATE_COMPILE = _folder("skate_compile", required=False) or SKATE     # optional: the Steam install, if Studio should read that one
STUDIO_CLI = os.path.join(STUDIO, "reskate_cli.exe")
EXPORTER = ["dotnet", os.path.join(TOOLS, "gta-skate", "bin", "Release", "net8.0", "gta-skate.dll")]
AUTHOR = _c.get("author", "")


def work(*parts):
    return os.path.join(WORK, *parts)


def skate_running():
    out = subprocess.run(["tasklist", "/fi", "imagename eq Skate.exe"], capture_output=True, text=True).stdout
    return "Skate.exe" in out


def wait_for_game():
    """Exports and compiles take several GB of memory: they wait while the game is open, so a play session is
    never starved (set "build_while_playing": true in config.json to skip the wait)."""
    said = False
    while not _c.get("build_while_playing") and skate_running():
        if not said:
            print("    waiting: skate. is running (building continues when it is closed)", flush=True)
            said = True
        time.sleep(10)


def run_heavy(cmd, log, cwd=None, append=False):
    """One export or compile, never while the game is open. Returns the exit code."""
    wait_for_game()
    with open(log, "a" if append else "w", encoding="utf-8") as fh:
        return subprocess.run(cmd, stdout=fh, stderr=subprocess.STDOUT, cwd=cwd).returncode
