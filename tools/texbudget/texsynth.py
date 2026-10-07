# Helpers for the texture-budget synthetic maps: textures (PNG, DDS) and a multi-object normalized map.
import json, struct, os, shutil
import numpy as np
from PIL import Image
from synth import glb, quad, material, MAT_DEFAULT


def save_png(path, rgba):
    """rgba: (h, w, 4) uint8, row 0 at the top"""
    a = np.asarray(rgba, dtype=np.uint8)
    Image.fromarray(a if a[..., 3].min() < 255 else a[..., :3].copy(), 'RGBA' if a[..., 3].min() < 255 else 'RGB').save(path)


def to565(rgb):
    r, g, b = int(rgb[0]), int(rgb[1]), int(rgb[2])
    return ((r * 31 + 127) // 255) << 11 | ((g * 63 + 127) // 255) << 5 | ((b * 31 + 127) // 255)


def from565(c):
    r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31
    return ((r * 255 + 15) // 31, (g * 255 + 31) // 63, (b * 255 + 15) // 31)


def box_down(rgba):
    h, w = rgba.shape[:2]
    a = rgba.astype(np.float64).reshape(h // 2, 2, w // 2, 2, 4).mean(axis=(1, 3))
    return np.clip(a + 0.5, 0, 255).astype(np.uint8)


def bc1_flat_blocks(rgba):
    """BC1 for an image whose 4x4 blocks are each one colour (taken from the block's first texel)"""
    h, w = rgba.shape[:2]
    out = bytearray()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            c = to565(rgba[by, bx])
            out += struct.pack('<HHI', c, c, 0)
    return bytes(out)


def bc3_flat_blocks(rgba):
    h, w = rgba.shape[:2]
    out = bytearray()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            a = int(rgba[by, bx, 3]); c = to565(rgba[by, bx])
            out += struct.pack('<BB6s', a, a, b'\0' * 6) + struct.pack('<HHI', c, c, 0)
    return bytes(out)


def dds_header(w, h, levels, fourcc=None, linear=0):
    hd = bytearray(128)
    hd[0:4] = b'DDS '
    struct.pack_into('<I', hd, 4, 124)
    struct.pack_into('<I', hd, 8, 0x000A1007 if fourcc else 0x0002100F)
    struct.pack_into('<II', hd, 12, h, w)
    struct.pack_into('<I', hd, 20, linear)
    struct.pack_into('<I', hd, 28, levels)
    struct.pack_into('<I', hd, 76, 32)
    if fourcc:
        struct.pack_into('<I', hd, 80, 4); hd[84:88] = fourcc
    else:
        struct.pack_into('<I', hd, 80, 0x41)                       # RGB | ALPHAPIXELS
        struct.pack_into('<IIIII', hd, 88, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    struct.pack_into('<I', hd, 108, 0x00401008 if levels > 1 else 0x00001000)
    return bytes(hd)


def save_dds_bc(path, rgba, mips=True, fourcc=b'DXT1'):
    enc = bc1_flat_blocks if fourcc == b'DXT1' else bc3_flat_blocks
    levels = [np.asarray(rgba, dtype=np.uint8)]
    while mips and min(levels[-1].shape[:2]) > 4: levels.append(box_down(levels[-1]))
    body = b''
    for l in levels:
        # a level whose blocks are not flat any more: encode each block from its mean colour
        h, w = l.shape[:2]
        m = l.astype(np.float64).reshape(h // 4, 4, w // 4, 4, 4).mean(axis=(1, 3))
        flat = np.repeat(np.repeat(np.clip(m + 0.5, 0, 255).astype(np.uint8), 4, axis=0), 4, axis=1)
        body += enc(flat)
    h, w = levels[0].shape[:2]
    bs = 8 if fourcc == b'DXT1' else 16
    with open(path, 'wb') as f:
        f.write(dds_header(w, h, len(levels), fourcc, (w // 4) * (h // 4) * bs)); f.write(body)
    return len(levels)


def save_dds_rgba(path, rgba):
    a = np.asarray(rgba, dtype=np.uint8); h, w = a.shape[:2]
    with open(path, 'wb') as f:
        f.write(dds_header(w, h, 1, None, w * 4)); f.write(a[..., [2, 1, 0, 3]].tobytes())


def palette(n_cells, cell, colours):
    """(n_cells*cell)^2 image of n_cells^2 flat cells; colours: list of (r, g, b, a), row-major"""
    img = np.zeros((n_cells * cell, n_cells * cell, 4), dtype=np.uint8)
    for i in range(n_cells * n_cells):
        cy, cx = divmod(i, n_cells)
        img[cy * cell:(cy + 1) * cell, cx * cell:(cx + 1) * cell] = colours[i % len(colours)]
    return img


def cell_centre(i, n_cells):
    cy, cx = divmod(i, n_cells)
    return ((cx + 0.5) / n_cells, (cy + 0.5) / n_cells)


def write_map(root, name, mats, objs, spawn=(100, 1, 100)):
    doc = {"format": 1, "name": name, "display_name": name, "units": "meters", "up": "y", "forward": "-z",
           "generator": "texbudget-synth (written without Blender)", "materials": mats, "surface_profiles": {},
           "streaming_distances": {"near": 50, "medium": 250, "far": 500}, "objects": objs, "lights": [],
           "audio_volumes": [], "audio_emitters": [], "interaction_prefabs": [], "trigger_effects": [], "vfx_prefabs": [],
           "npc_routes": [], "warnings": [], "travel_points": [], "spawn": {"name": "spawn", "position": list(spawn), "yaw": 0}}
    with open(os.path.join(root, 'map.json'), 'w', encoding='utf-8') as f:
        json.dump(doc, f)


def obj(name, mesh, mat, at, collide=False, **extra):
    o = {"name": name, "placement_mode": "authored_mesh", "mesh": mesh, "primitive": 0, "material": mat,
         "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, at[0], at[1], at[2]], "static": True,
         "collision_mode": "triangle_mesh" if collide else "none", "collision_material": "material_0032", "collision_material_packed": 32}
    o.update(extra)
    return o


def fresh(root):
    if os.path.isdir(root): shutil.rmtree(root)
    os.makedirs(os.path.join(root, 'meshes')); os.makedirs(os.path.join(root, 'textures'))
