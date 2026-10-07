# Builds a tiny synthetic normalized map (map.json + GLBs + PNG textures) for Studio's compile-map.
# A "case" is one quad (or a triangle soup) with chosen UVs and its own material.
import json, struct, zlib, os, shutil
import numpy as np


def png(path, w, h, rgba_fn):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            raw.extend(bytes(rgba_fn(x, y)))
    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)))
        f.write(chunk(b'IDAT', zlib.compress(bytes(raw), 9)))
        f.write(chunk(b'IEND', b''))


def glb(path, name, pos, nrm, uv, idx):
    pos = np.asarray(pos, '<f4'); nrm = np.asarray(nrm, '<f4'); uv = np.asarray(uv, '<f4'); idx = np.asarray(idx, '<u4').ravel()
    parts = [pos.tobytes(), nrm.tobytes(), uv.tobytes(), idx.tobytes()]
    offs = []; o = 0
    for p in parts:
        offs.append(o); o += len(p)
    j = {"asset": {"version": "2.0", "generator": "uv-synth"}, "scene": 0, "scenes": [{"nodes": [0]}],
         "nodes": [{"name": name, "mesh": 0}], "materials": [{"name": "m"}],
         "meshes": [{"name": name, "primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3, "material": 0, "mode": 4}]}],
         "accessors": [
             {"bufferView": 0, "componentType": 5126, "count": len(pos), "type": "VEC3", "min": pos.min(0).tolist(), "max": pos.max(0).tolist()},
             {"bufferView": 1, "componentType": 5126, "count": len(nrm), "type": "VEC3"},
             {"bufferView": 2, "componentType": 5126, "count": len(uv), "type": "VEC2"},
             {"bufferView": 3, "componentType": 5125, "count": len(idx), "type": "SCALAR"}],
         "bufferViews": [
             {"buffer": 0, "byteOffset": offs[0], "byteLength": len(parts[0]), "target": 34962},
             {"buffer": 0, "byteOffset": offs[1], "byteLength": len(parts[1]), "target": 34962},
             {"buffer": 0, "byteOffset": offs[2], "byteLength": len(parts[2]), "target": 34962},
             {"buffer": 0, "byteOffset": offs[3], "byteLength": len(parts[3]), "target": 34963}],
         "buffers": [{"byteLength": o}]}
    js = json.dumps(j, separators=(',', ':')).encode()
    js += b' ' * (-len(js) % 4)
    binb = b''.join(parts); binb += b'\0' * (-len(binb) % 4)
    with open(path, 'wb') as f:
        f.write(struct.pack('<4sII', b'glTF', 2, 12 + 8 + len(js) + 8 + len(binb)))
        f.write(struct.pack('<I4s', len(js), b'JSON')); f.write(js)
        f.write(struct.pack('<I4s', len(binb), b'BIN\0')); f.write(binb)


def quad(size, u0, v0, u1, v1, n=1):
    """A flat quad in the x/z plane (y up), n x n sub-quads, local origin at its centre.
    Front face (counter-clockwise seen from +y). UV runs u0..u1 along x and v0..v1 along z."""
    xs = np.linspace(-size / 2, size / 2, n + 1); zs = np.linspace(-size / 2, size / 2, n + 1)
    us = np.linspace(u0, u1, n + 1); vs = np.linspace(v0, v1, n + 1)
    pos = []; uv = []
    for iz in range(n + 1):
        for ix in range(n + 1):
            pos.append((xs[ix], 0.0, zs[iz])); uv.append((us[ix], vs[iz]))
    idx = []
    for iz in range(n):
        for ix in range(n):
            a = iz * (n + 1) + ix; b = a + 1; c = a + n + 1; d = c + 1
            idx += [a, c, b, b, c, d]         # counter-clockwise from +y (x right, z towards viewer)
    nrm = [(0.0, 1.0, 0.0)] * len(pos)
    return pos, nrm, uv, idx


MAT_DEFAULT = {"surface": "default", "srgb": True, "domain": "surface", "shader_override": "surface",
               "alpha_source": "constant", "alpha_cutoff": 0.5, "cast_shadows": True, "double_sided": False,
               "transparent_shadow": False, "alpha": "opaque", "blend_method": "clip", "roughness": 0.85, "metallic": 0}


def material(kind, texture, **extra):
    m = dict(MAT_DEFAULT); m["texture"] = texture
    if kind == 'opaque':
        pass
    elif kind == 'mask':
        m.update(alpha="mask", alpha_source="base_color_texture", blend_method="clip", double_sided=True)
    elif kind == 'blend':
        m.update(alpha="blend", alpha_source="base_color_texture", blend_method="blend")
    elif kind == 'decal_blend':
        m.update(domain="decal", shader_override="decal", alpha="blend", alpha_source="base_color_texture", blend_method="blend")
    elif kind == 'decal_opaque':
        m.update(domain="decal", shader_override="decal")
    elif kind == 'decal_mask':
        m.update(domain="decal", shader_override="decal", alpha="mask", alpha_source="base_color_texture", blend_method="clip")
    elif kind == 'foliage_mask':
        m.update(domain="foliage", shader_override="foliage", alpha="mask", alpha_source="base_color_texture", blend_method="clip", double_sided=True)
    elif kind == 'foliage_blend':
        m.update(domain="foliage", shader_override="foliage", alpha="blend", alpha_source="base_color_texture", blend_method="blend", double_sided=True)
    elif kind == 'glass':
        m.update(domain="glass", shader_override="glass", alpha="blend", alpha_source="base_color_texture", blend_method="blend")
    else:
        raise ValueError(kind)
    m.update(extra)
    return m


def build(root, name, cases, ground=True, textures=None):
    """cases: list of dicts {name, pos, nrm, uv, idx, material(dict), at:(x,y,z), shared:bool, extra:dict}"""
    if os.path.isdir(root):
        shutil.rmtree(root)
    os.makedirs(os.path.join(root, 'meshes')); os.makedirs(os.path.join(root, 'textures'))
    # textures: an opaque checker and one with a soft alpha edge
    def checker(x, y):
        c = 200 if ((x // 4) + (y // 4)) % 2 == 0 else 60
        return (c, 90, 255 - c, 255)
    def soft(x, y):
        c = 200 if ((x // 4) + (y // 4)) % 2 == 0 else 60
        return (c, 90, 255 - c, 40 + (x * 13 + y * 7) % 200)
    png(os.path.join(root, 'textures', 'checker.png'), 16, 16, checker)
    png(os.path.join(root, 'textures', 'soft.png'), 16, 16, soft)
    for t, (w, h, fn) in (textures or {}).items():
        png(os.path.join(root, 'textures', t), w, h, fn)
    mats = {}; objs = []
    if ground:
        p, n, u, i = quad(40.0, 0, 0, 1, 1)
        glb(os.path.join(root, 'meshes', 'ground.glb'), 'ground', p, n, u, i)
        mats['m_ground'] = material('opaque', 'textures/checker.png')
        objs.append({"name": "ground", "placement_mode": "authored_mesh", "mesh": "meshes/ground.glb", "primitive": 0, "material": "m_ground",
                     "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 100], "static": True,
                     "collision_mode": "triangle_mesh", "collision_material": "material_0032", "collision_material_packed": 32})
    for c in cases:
        mesh = c.get('mesh')
        if mesh is None:
            mesh = 'meshes/%s.glb' % c['name']
            glb(os.path.join(root, mesh), c['name'], c['pos'], c['nrm'], c['uv'], c['idx'])
        mk = 'm_' + c['name']
        mats[mk] = c['material']
        at = c.get('at', (100, 1, 100))
        o = {"name": c['name'], "placement_mode": "authored_mesh", "mesh": mesh, "primitive": 0, "material": mk,
             "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, at[0], at[1], at[2]], "static": True,
             "collision_mode": "none", "collision_material": "material_0032", "collision_material_packed": 32}
        o.update(c.get('extra', {}))
        objs.append(o)
    doc = {"format": 1, "name": name, "display_name": name, "units": "meters", "up": "y", "forward": "-z",
           "generator": "uv-synth (written without Blender)", "materials": mats, "surface_profiles": {},
           "streaming_distances": {"near": 50, "medium": 250, "far": 500}, "objects": objs, "lights": [],
           "audio_volumes": [], "audio_emitters": [], "interaction_prefabs": [], "trigger_effects": [], "vfx_prefabs": [],
           "npc_routes": [], "warnings": [], "travel_points": [], "spawn": {"name": "spawn", "position": [100, 1, 100], "yaw": 0}}
    with open(os.path.join(root, 'map.json'), 'w', encoding='utf-8') as f:
        json.dump(doc, f)
    return doc
