# Emulates the proposed  --max-textures N  on an existing normalized map (the source is only read, files are copied):
# keeps the N most important textures; every other material is re-pointed to a shared palette texture (the UVs of
# its triangles collapsed to the centre of its colour cell) or, for see-through decals, dropped. Writes a new map.
#   python repoint2.py <src map> <imp.json> <tex.json> <N> <dst map> [--weight area|loss] [--no-merge]
# Rules (the design proposed for the exporter):
#   weight of a texture   loss = drawn area x (contrast + 0.02) / root cost;  contrast = rms of its texels about the
#                         mean colour (+ spread of alpha when a material uses its alpha); root cost 1, or 2 when a
#                         mask / blend / decal / foliage material uses it. "--weight area": plain drawn area.
#   opaque palette        textures/palette_opaque.dds, 1024 px DXT1, ONE mip level, 256 x 256 cells of one 4 x 4
#                         block; the cell of colour c (RGB 5-6-5) is (c & 255, c >> 8): the file is the same in every
#                         export and a UV depends only on the colour.
#   translucent palette   textures/palette_blend.dds, 512 px DXT5, one level, 128 x 128 cells, colour 5-5-4 bits,
#                         alpha 128 everywhere (so the opacity texture Studio derives from it is uniform in all mips).
#   tail material         decal + blend or mask -> dropped; decal + opaque -> pal_decal; opaque -> pal_opaque[_2s];
#                         mask / blend with mean alpha >= 0.5 -> pal_opaque[_2s] (mask: _2s), else pal_blend.
import json, struct, sys, os, shutil, collections, math, time
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from texsynth import dds_header

args = [a for a in sys.argv[1:] if not a.startswith('--')]
flags = [a for a in sys.argv[1:] if a.startswith('--')]
plain = '--weight' in sys.argv and sys.argv[sys.argv.index('--weight') + 1] == 'area'
if '--weight' in sys.argv: args.remove(sys.argv[sys.argv.index('--weight') + 1])
merge = '--no-merge' not in flags
src, impf, texf, N, dst = args[0], args[1], args[2], int(args[3]), args[4]
t0 = time.time()
imp = json.load(open(impf))['materials']; tinfo = json.load(open(texf))
doc = json.load(open(os.path.join(src, 'map.json'), encoding='utf-8'))
mats = doc['materials']

def klass(m):
    a = m.get('alpha', 'opaque'); d = m.get('domain', 'surface')
    return 'B' if a == 'blend' or d == 'decal' else 'M' if a == 'mask' or d == 'foliage' else 'O'

# ---- rank textures
tex = {}
for name, m in mats.items():
    t = m.get('texture')
    if not t: continue
    e = tex.setdefault(t, dict(area=0.0, cls=set(), alpha=False))
    e['cls'].add(klass(m)); e['alpha'] |= m.get('alpha', 'opaque') != 'opaque'
    if name in imp: e['area'] += imp[name]['area']
def cost(t): return sum(1 if c == 'O' else 2 for c in tex[t]['cls'])
def contrast(t):
    ti = tinfo.get(t, {}); c = ti.get('rms', 0.1)
    if tex[t]['alpha']:
        p = min(1.0, max(0.0, ti.get('alpha_cut', 0.0))); c += math.sqrt(p * (1 - p))
    return c
def weight(t):
    e = tex[t]
    return e['area'] if plain else e['area'] * (contrast(t) + 0.02) / cost(t)
order = sorted(tex, key=lambda t: (-weight(t), t))
head = set(order[:N]); tail = set(order[N:])
A = sum(e['area'] for e in tex.values())
print('textures %d, kept %d, tail %d holding %.3f %% of drawn area; ranking: %s' % (len(tex), len(head), len(tail), 100 * sum(tex[t]['area'] for t in tail) / max(A, 1e-9), 'plain area' if plain else 'area x contrast / root cost'))

# ---- what every tail material becomes
def key565(t):
    m = tinfo.get(t, {}).get('mean', [128, 128, 128])
    return ((m[0] * 31 + 127) // 255) << 11 | ((m[1] * 63 + 127) // 255) << 5 | ((m[2] * 31 + 127) // 255)
def key554(t):
    m = tinfo.get(t, {}).get('mean', [128, 128, 128])
    return ((m[0] * 31 + 127) // 255) << 9 | ((m[1] * 31 + 127) // 255) << 4 | ((m[2] * 15 + 127) // 255)
def uv_opaque(c): return ((c & 255) + 0.5) / 256.0, ((c >> 8) + 0.5) / 256.0
def uv_blend(c): return ((c & 127) + 0.5) / 128.0, ((c >> 7) + 0.5) / 128.0
plan = {}                 # material -> ('keep',) | ('drop',) | (palette material, u, v)
counts = collections.Counter(); area_by = collections.Counter(); used_o = set(); used_b = set()
for name, m in mats.items():
    t = m.get('texture')
    if not t or t in head: plan[name] = ('keep',); continue
    alpha = m.get('alpha', 'opaque'); dom = m.get('domain', 'surface'); two = bool(m.get('double_sided'))
    cov = tinfo.get(t, {}).get('alpha_mean', 1.0)
    a = imp.get(name, {}).get('area', 0.0)
    if dom == 'decal' and alpha != 'opaque': plan[name] = ('drop',); k = 'see-through decals: dropped'
    elif dom == 'decal': c = key565(t); used_o.add(c); plan[name] = ('pal_decal',) + uv_opaque(c); k = 'opaque decals -> pal_decal'
    elif alpha == 'opaque': c = key565(t); used_o.add(c); plan[name] = ('pal_opaque_2s' if two else 'pal_opaque',) + uv_opaque(c); k = 'opaque -> pal_opaque'
    elif cov >= 0.5:
        c = key565(t); used_o.add(c); plan[name] = ('pal_opaque_2s' if (two or alpha == 'mask') else 'pal_opaque',) + uv_opaque(c); k = '%s, mostly solid -> pal_opaque' % alpha
    else: c = key554(t); used_b.add(c); plan[name] = ('pal_blend',) + uv_blend(c); k = '%s, mostly holes -> pal_blend' % alpha
    counts[k] += 1; area_by[k] += a
for k, v in sorted(counts.items()): print('   %-40s %5d materials, %9.0f m2 (%.3f %% of drawn area)' % (k, v, area_by[k], 100 * area_by[k] / A))
print('   palette cells in use: opaque %d of 65536, translucent %d of 16384' % (len(used_o), len(used_b)))

if os.path.isdir(dst): shutil.rmtree(dst)
os.makedirs(os.path.join(dst, 'meshes')); os.makedirs(os.path.join(dst, 'textures'))
# palette files: identical in every export
c = np.arange(65536, dtype='<u2')
blk = np.zeros(65536, dtype=[('c0', '<u2'), ('c1', '<u2'), ('ix', '<u4')]); blk['c0'] = c; blk['c1'] = c
with open(os.path.join(dst, 'textures', 'palette_opaque.dds'), 'wb') as f: f.write(dds_header(1024, 1024, 1, b'DXT1', 65536 * 8)); f.write(blk.tobytes())
i = np.arange(16384, dtype=np.uint32); r5 = i >> 9; g5 = (i >> 4) & 31; b4 = i & 15
c565 = (r5 << 11) | (((g5 << 1) | (g5 >> 4)) << 5) | ((b4 << 1) | (b4 >> 3))
blk = np.zeros(16384, dtype=[('a0', 'u1'), ('a1', 'u1'), ('ai', 'u1', 6), ('c0', '<u2'), ('c1', '<u2'), ('ix', '<u4')])
blk['a0'] = 128; blk['a1'] = 128; blk['c0'] = c565; blk['c1'] = c565
with open(os.path.join(dst, 'textures', 'palette_blend.dds'), 'wb') as f: f.write(dds_header(512, 512, 1, b'DXT5', 16384 * 16)); f.write(blk.tobytes())

# ---- meshes
CT = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}; NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
def read_glb(path):
    b = open(path, 'rb').read()
    jl, = struct.unpack_from('<I', b, 12); j = json.loads(b[20:20 + jl]); bin0 = 20 + jl + 8
    def acc(i):
        a = j['accessors'][i]; bv = j['bufferViews'][a['bufferView']]
        off = bin0 + bv.get('byteOffset', 0) + a.get('byteOffset', 0); n = NC[a['type']]
        return np.frombuffer(b, dtype=np.dtype('<' + CT[a['componentType']]), count=a['count'] * n, offset=off).reshape(a['count'], n)
    prims = []
    for m in j['meshes']:
        for p in m['primitives']:
            at = p['attributes']
            prims.append(dict(pos=acc(at['POSITION']), nrm=acc(at['NORMAL']) if 'NORMAL' in at else None, uv=acc(at['TEXCOORD_0']) if 'TEXCOORD_0' in at else None,
                              idx=acc(p['indices']).ravel().astype(np.uint32), mat=j['materials'][p['material']]['name'] if 'material' in p else None))
    return j['nodes'][0].get('name', 'mesh'), prims
def write_glb(path, name, prims):
    parts = []; views = []; accs = []; pj = []; mnames = []; size = [0]
    def view(data, target):
        pad = (-size[0]) % 4
        if pad: parts.append(b'\0' * pad); size[0] += pad
        parts.append(data); views.append({"buffer": 0, "byteOffset": size[0], "byteLength": len(data), "target": target}); size[0] += len(data); return len(views) - 1
    for p in prims:
        pos = np.ascontiguousarray(p['pos'], '<f4'); att = {}
        accs.append({"bufferView": view(pos.tobytes(), 34962), "componentType": 5126, "count": len(pos), "type": "VEC3", "min": pos.min(0).tolist(), "max": pos.max(0).tolist()}); att["POSITION"] = len(accs) - 1
        if p['nrm'] is not None:
            accs.append({"bufferView": view(np.ascontiguousarray(p['nrm'], '<f4').tobytes(), 34962), "componentType": 5126, "count": len(pos), "type": "VEC3"}); att["NORMAL"] = len(accs) - 1
        if p['uv'] is not None:
            accs.append({"bufferView": view(np.ascontiguousarray(p['uv'], '<f4').tobytes(), 34962), "componentType": 5126, "count": len(pos), "type": "VEC2"}); att["TEXCOORD_0"] = len(accs) - 1
        idx = np.ascontiguousarray(p['idx'], '<u4')
        accs.append({"bufferView": view(idx.tobytes(), 34963), "componentType": 5125, "count": len(idx), "type": "SCALAR"})
        if p['mat'] not in mnames: mnames.append(p['mat'])
        pj.append({"attributes": att, "indices": len(accs) - 1, "material": mnames.index(p['mat']), "mode": 4})
    binb = b''.join(parts); binb += b'\0' * ((-len(binb)) % 4)
    j = {"asset": {"version": "2.0", "generator": "texbudget repoint"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": name, "mesh": 0}],
         "materials": [{"name": n} for n in mnames], "meshes": [{"name": name, "primitives": pj}], "accessors": accs, "bufferViews": views, "buffers": [{"byteLength": len(binb)}]}
    js = json.dumps(j, separators=(',', ':')).encode(); js += b' ' * ((-len(js)) % 4)
    with open(path, 'wb') as f:
        f.write(struct.pack('<4sII', b'glTF', 2, 12 + 8 + len(js) + 8 + len(binb)))
        f.write(struct.pack('<I4s', len(js), b'JSON')); f.write(js); f.write(struct.pack('<I4s', len(binb), b'BIN\0')); f.write(binb)

MAXV = 60000
from concurrent.futures import ThreadPoolExecutor
_pool = ThreadPoolExecutor(12); _jobs = []
def copy(a, b): _jobs.append(_pool.submit(shutil.copyfile, a, b))
render = [o for o in doc['objects'] if o.get('render') is not False and 'mesh' in o]
other = [o for o in doc['objects'] if not (o.get('render') is not False and 'mesh' in o)]
by_mesh = collections.defaultdict(list)
for o in render: by_mesh[o['mesh']].append(o)
new_objects = []; used_mats = set(); stat = collections.Counter()
for mi, (mesh, recs) in enumerate(sorted(by_mesh.items())):
    prim_mat = {}
    for o in recs: prim_mat[o['primitive']] = o['material']
    if all(plan[m][0] == 'keep' for m in prim_mat.values()):
        copy(os.path.join(src, mesh), os.path.join(dst, mesh))
        for o in recs: new_objects.append(o); used_mats.add(o['material'])
        stat['meshes unchanged'] += 1
        continue
    name, prims = read_glb(os.path.join(src, mesh))
    out = []; remap = {}                             # old primitive -> list of new primitive indices
    groups = collections.OrderedDict()
    for pi, p in enumerate(prims):
        m = prim_mat.get(pi)
        if m is None: continue
        pl = plan[m]
        if pl[0] == 'keep': remap[pi] = [len(out)]; out.append(dict(p, mat=m))
        elif pl[0] == 'drop': remap[pi] = []; stat['triangles dropped (see-through decals of the tail), unique'] += len(p['idx']) // 3
        else: groups.setdefault(pl[0] if merge else (pl[0], pi), []).append((pi, p, pl))
    for gk in sorted(groups, key=str):
        pm = gk if merge else gk[0]
        cur = None
        def flush():
            if cur and cur['n']:
                out.append(dict(pos=np.concatenate(cur['pos']), nrm=np.concatenate(cur['nrm']), uv=np.concatenate(cur['uv']), idx=np.concatenate(cur['idx']), mat=pm))
        for pi, p, pl in groups[gk]:
            nv = len(p['pos'])
            if cur is None or cur['n'] + nv > MAXV:
                flush(); cur = dict(pos=[], nrm=[], uv=[], idx=[], n=0)
            cur['pos'].append(p['pos']); cur['nrm'].append(p['nrm'] if p['nrm'] is not None else np.tile(np.array([[0, 1, 0]], '<f4'), (nv, 1)))
            cur['uv'].append(np.tile(np.array([[pl[1], pl[2]]], '<f4'), (nv, 1))); cur['idx'].append(p['idx'] + np.uint32(cur['n']))
            remap.setdefault(pi, []).append(len(out)); cur['n'] += nv
            stat['triangles re-pointed, unique'] += len(p['idx']) // 3
        flush()
    if not out: stat['meshes left with nothing'] += 1
    else: write_glb(os.path.join(dst, mesh), name, out)
    stat['meshes rewritten'] += 1
    origin = {}
    for k, p in enumerate(out):
        lo = p['pos'].min(0); hi = p['pos'].max(0)
        origin[k] = (((lo + hi) / 2).tolist(), float(((hi - lo) / 2).max()))
    inst = collections.OrderedDict()
    for o in recs: inst.setdefault((o['name'].split(' / ')[0], tuple(o['transform'])), []).append(o)
    for (base, tr), rl in inst.items():
        newp = collections.OrderedDict()             # new primitive -> a source record
        for o in rl:
            for k in remap.get(o['primitive'], []):
                if k not in newp or (o.get('shared_geometry') and not newp[k].get('shared_geometry')): newp[k] = o
        keys = sorted(newp)
        for n, k in enumerate(keys):
            srcrec = newp[k]
            o = dict(srcrec); o['name'] = base if len(keys) == 1 else '%s / section %d' % (base, n + 1)
            o['primitive'] = k; o['material'] = out[k]['mat']
            if plan[srcrec['material']][0] != 'keep' and srcrec.get('shared_geometry'):
                c, half = origin[k]
                if half <= 32.0: o['native_render_origin'] = c
                else:
                    for f in ('shared_geometry', 'world_transform_baked', 'native_render_origin'): o.pop(f, None)
                    stat['instance records no longer shared (merged primitive wider than 64 m)'] += 1
            new_objects.append(o); used_mats.add(o['material'])
    if (mi + 1) % 100 == 0: print('   meshes %d / %d  %.0fs' % (mi + 1, len(by_mesh), time.time() - t0), flush=True)
_copied = set()
for o in other:
    new_objects.append(o)
    for f in ('collision_mesh', 'mesh'):
        if f in o and o[f] not in _copied: _copied.add(o[f]); copy(os.path.join(src, o[f]), os.path.join(dst, o[f]))
    if o.get('material'): used_mats.add(o['material'])

tmpl = dict(next(m for m in mats.values() if m.get('alpha') == 'opaque' and m.get('domain') == 'surface'))
new_mats = {}
for n in sorted(used_mats):
    if n in mats: new_mats[n] = mats[n]; continue
    m = dict(tmpl); m.update(roughness=0.85, metallic=0.0, alpha_cutoff=0.5, transparent_shadow=False)
    if n == 'pal_decal': m.update(domain='decal', shader_override='decal', alpha='opaque', alpha_source='constant', blend_method='clip', double_sided=False, cast_shadows=False, texture='textures/palette_opaque.dds')
    elif n.startswith('pal_opaque'): m.update(domain='surface', shader_override='surface', alpha='opaque', alpha_source='constant', blend_method='clip', double_sided=n.endswith('_2s'), cast_shadows=True, texture='textures/palette_opaque.dds')
    else: m.update(domain='surface', shader_override='surface', alpha='blend', alpha_source='base_color_texture', blend_method='blend', double_sided=True, cast_shadows=False, texture='textures/palette_blend.dds')
    new_mats[n] = m
files = set(m['texture'] for m in new_mats.values() if m.get('texture'))
for f in sorted(files):
    if not f.startswith('textures/palette_'): copy(os.path.join(src, f), os.path.join(dst, f))
for f in ('palette_opaque.dds', 'palette_blend.dds'):
    if 'textures/' + f not in files: os.remove(os.path.join(dst, 'textures', f))
for j in _jobs: j.result()
_pool.shutdown()
doc['materials'] = new_mats; doc['objects'] = new_objects
doc['name'] = os.path.basename(os.path.normpath(dst)); doc['display_name'] = doc['name']
json.dump(doc, open(os.path.join(dst, 'map.json'), 'w', encoding='utf-8'))
if os.path.isdir(os.path.join(src, 'ui')): shutil.copytree(os.path.join(src, 'ui'), os.path.join(dst, 'ui'))
if os.path.exists(os.path.join(src, 'stats.json')): shutil.copyfile(os.path.join(src, 'stats.json'), os.path.join(dst, 'stats.json'))
pairs = set((m['texture'], klass(m)) for m in new_mats.values() if m.get('texture') and m.get('domain') != 'ocean')
pred = sum(1 if c == 'O' else 2 for t, c in pairs)
rough = len(set((m.get('metallic', 0), m.get('roughness', 0.85)) for m in new_mats.values() if m.get('texture')))
for k, v in sorted(stat.items()): print('   %-70s %d' % (k, v))
print('materials %d -> %d ; texture files %d -> %d ; render records %d -> %d' % (len(mats), len(new_mats), len(set(m.get("texture") for m in mats.values() if m.get("texture"))), len(files), len(render), len(new_objects) - len(other)))
print('predicted textures of the level root: %d of the map + %d default masks + 1 normal + 816 of the template = %d' % (pred, rough, pred + rough + 1 + 816))
json.dump({'n': N, 'kept': len(head), 'tail': len(tail), 'materials': len(new_mats), 'texture_files': len(files), 'predicted_root_textures': pred + rough + 1 + 816, 'stat': dict(stat), 'counts': dict(counts),
           'area_by': dict(area_by), 'palette_cells': [len(used_o), len(used_b)], 'render_records': [len(render), len(new_objects) - len(other)], 'weight': 'area' if plain else 'loss'},
          open(os.path.join(dst, 'repoint.json'), 'w'))
print('done in %.0fs -> %s' % (time.time() - t0, dst))
