#!/usr/bin/env python3
"""Reference reader for LSCT files (collision tiles and per-ybn meshes), written from docs/export-format.md.

Independent of the C# exporter on purpose: it is used to check that the documented layout is what is on disk.

    python lsct.py info  <file.lsct>            header, groups, material histogram
    python lsct.py check <export_dir> [n]       validate n tiles (default all) against collision/index.json
"""
import json
import os
import struct
import sys

import numpy as np

HEADER = struct.Struct("<4sIIiiffIIII3f3f5IQ")  # 96 bytes
assert HEADER.size == 96

GROUP_DT = np.dtype([
    ("ybn_id", "<u4"), ("child", "<u2"), ("bound_type", "u1"), ("info", "u1"),
    ("type_flags", "<u4"), ("include_flags", "<u4"),
    ("first_tri", "<u4"), ("n_tris", "<u4"), ("first_prim", "<u4"), ("n_prims", "<u4"),
    ("min", "<f4", 3), ("max", "<f4", 3),
])
ATTR_DT = np.dtype([
    ("material", "u1"), ("kind", "u1"), ("mat_flags", "<u2"), ("group", "<u2"), ("proc_id", "u1"), ("room_ped", "u1"),
])
PRIM_DT = np.dtype([
    ("kind", "u1"), ("material", "u1"), ("mat_flags", "<u2"), ("group", "<u2"), ("proc_id", "u1"), ("room_ped", "u1"),
    ("first_tri", "<u4"), ("n_tris", "<u4"), ("p", "<f4", 12),
])
assert GROUP_DT.itemsize == 56 and ATTR_DT.itemsize == 8 and PRIM_DT.itemsize == 64

TYPE_FLAGS = ["UNKNOWN", "MAP_WEAPON", "MAP_DYNAMIC", "MAP_ANIMAL", "MAP_COVER", "MAP_VEHICLE", "VEHICLE_NOT_BVH",
              "VEHICLE_BVH", "VEHICLE_BOX", "PED", "RAGDOLL", "ANIMAL", "ANIMAL_RAGDOLL", "OBJECT", "OBJECT_ENV_CLOTH",
              "PLANT", "PROJECTILE", "EXPLOSION", "PICKUP", "FOLIAGE", "FORKLIFT_FORKS", "TEST_WEAPON", "TEST_CAMERA",
              "TEST_AI", "TEST_SCRIPT", "TEST_VEHICLE_WHEEL", "GLASS", "MAP_RIVER", "SMOKE", "UNSMASHED", "MAP_STAIRS",
              "MAP_DEEP_SURFACE"]


def flag_names(f):
    return "|".join(n for i, n in enumerate(TYPE_FLAGS) if f >> i & 1) or "NONE"


class Lsct:
    def __init__(self, path):
        data = np.fromfile(path, dtype=np.uint8)
        raw = data.tobytes()
        (magic, self.version, self.kind, self.tile_x, self.tile_y, self.tile_size, self.margin,
         ng, nv, nt, np_, mnx, mny, mnz, mxx, mxy, mxz,
         og, ov, ot, oa, op, self.signature) = HEADER.unpack_from(raw, 0)
        if magic != b"LSCT":
            raise ValueError("not an LSCT file: " + path)
        if self.version != 1:
            raise ValueError("unsupported LSCT version %d" % self.version)
        self.bb_min = (mnx, mny, mnz)
        self.bb_max = (mxx, mxy, mxz)
        self.groups = np.frombuffer(raw, GROUP_DT, ng, og)
        self.verts = np.frombuffer(raw, "<f4", nv * 3, ov).reshape(nv, 3)
        self.tris = np.frombuffer(raw, "<u4", nt * 3, ot).reshape(nt, 3)
        self.attrs = np.frombuffer(raw, ATTR_DT, nt, oa)
        self.prims = np.frombuffer(raw, PRIM_DT, np_, op)
        self.size = len(raw)
        self.expected_size = op + np_ * 64


def info(path):
    m = Lsct(path)
    print("%s: kind=%s version=%d tile=(%d,%d) size=%g margin=%g" % (
        path, "tile" if m.kind == 0 else "mesh", m.version, m.tile_x, m.tile_y, m.tile_size, m.margin))
    print("  groups=%d verts=%d tris=%d prims=%d bytes=%d" % (len(m.groups), len(m.verts), len(m.tris), len(m.prims), m.size))
    print("  bbox %s .. %s" % (m.bb_min, m.bb_max))
    for i, g in enumerate(m.groups[:40]):
        # bits 4-7 are only set in interior layer tiles: category index and the detached flag
        print("  group %3d ybn=%d child=%d bound=%d layer=%d grouped=%d default_off=%d category=%d detached=%d tris=%d prims=%d type=%s" % (
            i, g["ybn_id"], g["child"], g["bound_type"], g["info"] & 3, g["info"] >> 2 & 1, g["info"] >> 3 & 1,
            g["info"] >> 4 & 7, g["info"] >> 7 & 1, g["n_tris"], g["n_prims"], flag_names(int(g["type_flags"]))))
    if len(m.groups) > 40:
        print("  ... %d more groups" % (len(m.groups) - 40))
    mats, counts = np.unique(m.attrs["material"], return_counts=True)
    print("  materials: " + ", ".join("%d:%d" % (a, b) for a, b in sorted(zip(mats, counts), key=lambda t: -t[1])[:12]))
    kinds, counts = np.unique(m.attrs["kind"], return_counts=True)
    print("  tri kinds: " + ", ".join("%d:%d" % (a, b) for a, b in zip(kinds, counts)))


def check_tile(export_dir, t, ybn_count):
    """Validate every invariant promised by docs/export-format.md for one tile. Returns a list of problems."""
    errs = []
    path = os.path.join(export_dir, "collision", t["file"])
    m = Lsct(path)
    if m.size != m.expected_size:
        errs.append("file size %d != expected %d" % (m.size, m.expected_size))
    if m.kind != 0:
        errs.append("kind != 0")
    if (m.tile_x, m.tile_y) != (t["x"], t["y"]):
        errs.append("tile coords differ from index")
    for k, n in (("tris", len(m.tris)), ("verts", len(m.verts)), ("prims", len(m.prims)), ("groups", len(m.groups))):
        if t[k] != n:
            errs.append("%s count %d != index %d" % (k, n, t[k]))
    if len(m.tris) and int(m.tris.max()) >= len(m.verts):
        errs.append("vertex index out of range")
    if not np.isfinite(m.verts).all():
        errs.append("non-finite vertex")
    # every vertex is referenced
    used = np.zeros(len(m.verts), bool)
    used[m.tris.ravel()] = True
    if not used.all():
        errs.append("%d unreferenced vertices" % int((~used).sum()))
    # groups partition the triangle array in order and attrs.group agrees
    pos = 0
    ppos = 0
    for gi, g in enumerate(m.groups):
        if int(g["first_tri"]) != pos:
            errs.append("group %d first_tri %d != %d" % (gi, g["first_tri"], pos)); break
        if int(g["first_prim"]) != ppos:
            errs.append("group %d first_prim mismatch" % gi); break
        n = int(g["n_tris"])
        if n == 0:
            errs.append("empty group %d" % gi)
        if not (m.attrs["group"][pos:pos + n] == gi).all():
            errs.append("attr.group mismatch in group %d" % gi); break
        if int(g["ybn_id"]) >= ybn_count:
            errs.append("ybn_id out of range")
        gv = m.verts[m.tris[pos:pos + n].ravel()]
        if n and (np.abs(gv.min(0) - g["min"]).max() > 1e-3 or np.abs(gv.max(0) - g["max"]).max() > 1e-3):
            errs.append("group %d bbox mismatch" % gi)
        # primitives of the group: contiguous triangle ranges at the end of the group
        k = m.attrs["kind"][pos:pos + n]
        nplain = int((k == 0).sum())
        if (k[:nplain] != 0).any():
            errs.append("group %d: real triangles are not first" % gi)
        tp = pos + nplain
        for p in m.prims[ppos:ppos + int(g["n_prims"])]:
            if int(p["first_tri"]) != tp:
                errs.append("prim range not contiguous in group %d" % gi); break
            if int(p["group"]) != gi:
                errs.append("prim.group mismatch")
            pk = m.attrs["kind"][tp:tp + int(p["n_tris"])]
            if not (pk == p["kind"]).all():
                errs.append("prim kind != its triangles' kind")
            tp += int(p["n_tris"])
        if tp != pos + n:
            errs.append("group %d: primitive triangles do not fill the tail" % gi)
        pos += n
        ppos += int(g["n_prims"])
    if pos != len(m.tris):
        errs.append("groups do not cover all triangles")
    # header bbox
    if len(m.verts):
        if np.abs(m.verts.min(0) - np.array(m.bb_min)).max() > 1e-3 or np.abs(m.verts.max(0) - np.array(m.bb_max)).max() > 1e-3:
            errs.append("header bbox mismatch")
        if abs(t["z_min"] - m.bb_min[2]) > 1e-3 or abs(t["z_max"] - m.bb_max[2]) > 1e-3:
            errs.append("index z range mismatch")
    # every real triangle's plan-view bounding box touches the tile rectangle grown by the margin
    s, mg = m.tile_size, m.margin
    x0, x1 = m.tile_x * s - mg, (m.tile_x + 1) * s + mg
    y0, y1 = m.tile_y * s - mg, (m.tile_y + 1) * s + mg
    real = m.attrs["kind"] == 0
    tv = m.verts[m.tris[real]]
    if len(tv):
        lo, hi = tv.min(1), tv.max(1)
        bad = (hi[:, 0] < x0) | (lo[:, 0] > x1) | (hi[:, 1] < y0) | (lo[:, 1] > y1)
        if bad.any():
            errs.append("%d triangles do not touch the tile" % int(bad.sum()))
    return errs, m


def check(export_dir, limit=None):
    idx = json.load(open(os.path.join(export_dir, "collision", "index.json")))
    tiles = idx["tiles"]
    if limit:
        step = max(1, len(tiles) // limit)
        tiles = tiles[::step][:limit]
    nbad = 0
    tris = 0
    zmin, zmax = 1e9, -1e9
    for i, t in enumerate(tiles):
        errs, m = check_tile(export_dir, t, len(idx["ybns"]))
        tris += len(m.tris)
        zmin, zmax = min(zmin, m.bb_min[2]), max(zmax, m.bb_max[2])
        if errs:
            nbad += 1
            print("BAD %s: %s" % (t["file"], "; ".join(errs[:5])))
    print("checked %d tiles, %d triangles, z %.1f..%.1f, %d tiles with problems" % (len(tiles), tris, zmin, zmax, nbad))
    return nbad == 0


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "info":
        info(sys.argv[2])
    elif len(sys.argv) >= 3 and sys.argv[1] == "check":
        ok = check(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else None)
        sys.exit(0 if ok else 1)
    else:
        print(__doc__)
