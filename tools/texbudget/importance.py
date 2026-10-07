# Read-only study: how much drawn surface each material / texture of a normalized map covers.
#   python importance.py <map folder> <out.json> [<export folder: interiors/index.json, optional street_nodes.csv>]
# Per material: drawn area (triangle area x placements), the same weighted for "seen from the street", shares that
# are roof, high up, inside a building interior, placed as an instance; the 200 m cells it appears in; and the
# number of pieces Studio's cut at texture repeats makes of it (uv_cut.py's estimate).
import json, struct, sys, os, math, collections, time
import numpy as np

root, outp = sys.argv[1], sys.argv[2]
export_dir = sys.argv[3] if len(sys.argv) > 3 else ''
t0 = time.time()
doc = json.load(open(os.path.join(root, 'map.json'), encoding='utf-8'))
stats = json.load(open(os.path.join(root, 'stats.json'), encoding='utf-8'))
region = stats['region']                      # gta x0, y0, x1, y1
mats = doc['materials']
recs = collections.defaultdict(list)          # (mesh, prim) -> [(material, transform, shared)]
for o in doc['objects']:
    if o.get('render') is False or 'mesh' not in o: continue
    recs[(o['mesh'], o.get('primitive', 0))].append((o['material'], o['transform'], bool(o.get('shared_geometry')), o['name']))
del doc
print('records', sum(len(v) for v in recs.values()), 'primitives', len(recs), 'materials', len(mats), '%.1fs' % (time.time() - t0), flush=True)

# ---- street level: z of the game's street nodes near each 8 m raster cell (3 nearest in plan)
M = 400.0
gx0, gy0, gx1, gy1 = region[0] - M, region[1] - M, region[2] + M, region[3] + M
nodes = []
# optional: a list of street points (x,y,z per line after a header). Without it the "seen from the street" figures
# are measured from height 0; the plain drawn area, which the texture budget ranks by, does not depend on it.
node_file = os.path.join(export_dir, 'street_nodes.csv')
if export_dir and os.path.isfile(node_file):
    with open(node_file) as f:
        next(f)
        for line in f:
            p = line.split(',')
            x, y, z = float(p[0]), float(p[1]), float(p[2])
            if gx0 - 300 <= x <= gx1 + 300 and gy0 - 300 <= y <= gy1 + 300: nodes.append((x, y, z))
nodes = np.array(nodes, dtype=np.float64).reshape(-1, 3)
RS = 8.0
nx, ny = int(math.ceil((gx1 - gx0) / RS)), int(math.ceil((gy1 - gy0) / RS))
cx = gx0 + (np.arange(nx) + 0.5) * RS; cy = gy0 + (np.arange(ny) + 0.5) * RS
street = np.zeros((ny, nx, 3), dtype=np.float32)
for j in range(ny if len(nodes) >= 3 else 0):
    d = (cx[:, None] - nodes[None, :, 0]) ** 2 + (cy[j] - nodes[None, :, 1]) ** 2
    k = np.argpartition(d, 2, axis=1)[:, :3]
    street[j] = nodes[k, 2]
print('street nodes', len(nodes), 'raster', nx, ny, '%.1fs' % (time.time() - t0), flush=True)

# ---- building interiors (not tunnels, metro, car parks: those are places to skate): local boxes
interiors = []
index_file = os.path.join(export_dir, 'interiors', 'index.json')
idx = json.load(open(index_file)) if export_dir and os.path.isfile(index_file) else {'interiors': []}
for it in idx['interiors']:
    if it['detached'] or not it['default_on'] or it['category'] != 'building_interior': continue
    mn, mx = it['min'], it['max']
    if mx[0] < gx0 or mn[0] > gx1 or mx[1] < gy0 or mn[1] > gy1: continue
    l2w = it['local_to_world']
    interiors.append((np.array(mn), np.array(mx), l2w, it['position'][2], np.array(it['local_min']), np.array(it['local_max'])))
del idx
print('building interiors near the region', len(interiors), flush=True)

CT = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}; NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
F = ['area', 'area_w', 'area_street', 'area_roof', 'area_high', 'area_interior', 'area_inst', 'tris_drawn', 'tris_unique', 'records', 'uv_pieces']
acc = collections.defaultdict(lambda: dict.fromkeys(F, 0.0))
cells = collections.defaultdict(set)          # material -> {(cx, cz)} 200 m cells (y-up world x, z)
cell_area = collections.defaultdict(float)    # (material, cell) -> area, to see how thin a pair is
uvmax = collections.defaultdict(float)
protos = collections.defaultdict(collections.Counter)   # material -> archetype names of the instances that use it

def weights(c, n, area):
    """c: world centroids (T,3) y up; n: unit world normals; returns weight, masks"""
    gx = c[:, 0]; gy = -c[:, 2]; gz = c[:, 1]
    ix = np.clip(((gx - gx0) / RS).astype(np.int64), 0, nx - 1); iy = np.clip(((gy - gy0) / RS).astype(np.int64), 0, ny - 1)
    sz = street[iy, ix]                                   # (T,3)
    dz = gz[:, None] - sz
    k = np.argmin(np.abs(dz - 1.0), axis=1)
    hs = dz[np.arange(len(k)), k]
    ah = np.abs(hs - 1.0)
    w = np.where(ah <= 10.0, 1.0, np.maximum(0.15, 10.0 / np.maximum(ah, 1e-3)))
    roof = (n[:, 1] > 0.7) & (hs > 6.0)
    w = np.where(roof, w * 0.3, w)
    inside = np.zeros(len(c), dtype=bool)
    if interiors:
        lo = np.array([gx.min(), gy.min(), gz.min()]); hi = np.array([gx.max(), gy.max(), gz.max()])
        for mn, mx, l2w, pz, lmn, lmx in interiors:
            if (hi < mn).any() or (lo > mx).any(): continue
            dx = gx - l2w[3]; dy = gy - l2w[7]
            lx = l2w[0] * dx + l2w[4] * dy; ly = l2w[1] * dx + l2w[5] * dy; lz = gz - pz
            inside |= (lx > lmn[0] + 0.25) & (lx < lmx[0] - 0.25) & (ly > lmn[1] + 0.25) & (ly < lmx[1] - 0.25) & (lz > lmn[2] - 0.1) & (lz < lmx[2] + 0.1)
    w = np.where(inside, w * 0.3, w)
    street_lvl = (ah <= 10.0) & ~roof & ~inside
    return w, street_lvl, roof, ah > 30.0, inside

meshes = sorted(set(k[0] for k in recs))
done = 0
for mi, mesh in enumerate(meshes):
    path = os.path.join(root, mesh)
    b = open(path, 'rb').read()
    jl, = struct.unpack_from('<I', b, 12); j = json.loads(b[20:20 + jl]); bin0 = 20 + jl + 8
    def accr(i):
        a = j['accessors'][i]; bv = j['bufferViews'][a['bufferView']]
        off = bin0 + bv.get('byteOffset', 0) + a.get('byteOffset', 0); n = NC[a['type']]
        return np.frombuffer(b, dtype=np.dtype('<' + CT[a['componentType']]), count=a['count'] * n, offset=off).reshape(a['count'], n)
    pi = 0
    for m in j.get('meshes', []):
        for p in m['primitives']:
            rl = recs.get((mesh, pi)); pi += 1
            if not rl: continue
            pos = accr(p['attributes']['POSITION']).astype(np.float64); ix = accr(p['indices']).reshape(-1, 3).astype(np.int64)
            p0 = pos[ix[:, 0]]; e1 = pos[ix[:, 1]] - p0; e2 = pos[ix[:, 2]] - p0
            cr = np.cross(e1, e2); cen = p0 + (e1 + e2) / 3.0
            T = len(ix)
            pieces = float(T)
            if 'TEXCOORD_0' in p['attributes']:
                uv = accr(p['attributes']['TEXCOORD_0']).astype(np.float64)[ix]
                span = np.floor(uv.max(axis=1)) - np.floor(uv.min(axis=1)) + 1
                pieces = float(np.sum(np.minimum(span[:, 0] * span[:, 1], 1e9)))
                umax = float(span.max())
            else: umax = 1.0
            for mat, tr, shared, name in rl:
                Mx = np.array([tr[0:3], tr[3:6], tr[6:9]], dtype=np.float64).T        # columns = images of x, y, z
                t = np.array(tr[9:12], dtype=np.float64)
                ident = abs(Mx - np.eye(3)).max() < 1e-9
                if ident: wc = cr; c = cen + t
                else:
                    cof = np.linalg.det(Mx) * np.linalg.inv(Mx).T
                    wc = cr @ cof.T; c = cen @ Mx.T + t
                ln = np.linalg.norm(wc, axis=1); area = 0.5 * ln
                n = wc / np.maximum(ln, 1e-20)[:, None]
                w, sl, roof, high, inside = weights(c, n, area)
                a = acc[mat]
                tot = float(area.sum())
                a['area'] += tot; a['area_w'] += float((area * w).sum()); a['area_street'] += float(area[sl].sum())
                a['area_roof'] += float(area[roof].sum()); a['area_high'] += float(area[high].sum()); a['area_interior'] += float(area[inside].sum())
                a['tris_drawn'] += T; a['records'] += 1; a['uv_pieces'] += pieces
                if shared or not ident:
                    a['area_inst'] += tot
                    nm = name.split(' / ')[0]; nm = nm.split('_', 1)[1] if '_' in nm else nm
                    protos[mat][nm] += 1
                if shared:
                    cc = (int(math.floor(t[0] / 200)), int(math.floor(t[2] / 200)))
                    cells[mat].add(cc); cell_area[(mat, cc)] += tot
                else:
                    cxz = np.floor(c[:, [0, 2]] / 200).astype(np.int64)
                    key = (cxz[:, 0] + 1000) * 100000 + (cxz[:, 1] + 1000)
                    uq, inv = np.unique(key, return_inverse=True)
                    sums = np.bincount(inv.ravel(), weights=area)
                    for u, s in zip(uq.tolist(), sums.tolist()):
                        cxi = u // 100000 - 1000; czi = u % 100000 - 1000
                        cells[mat].add((cxi, czi)); cell_area[(mat, (cxi, czi))] += s
                uvmax[mat] = max(uvmax[mat], umax)
            # unique triangles once per primitive, for the first material using it
            acc[rl[0][0]]['tris_unique'] += T
    done += 1
    if done % 50 == 0 or done == len(meshes): print('  meshes %d / %d  %.0fs' % (done, len(meshes), time.time() - t0), flush=True)

out = {'root': root, 'region': region, 'materials': {}}
for name, m in mats.items():
    a = acc.get(name)
    if a is None: continue
    out['materials'][name] = {
        'texture': m.get('texture'), 'alpha': m.get('alpha'), 'domain': m.get('domain'), 'double_sided': m.get('double_sided'),
        **{k: (round(v, 3) if isinstance(v, float) else v) for k, v in a.items()},
        'uv_max_span': uvmax[name],
        'cells': sorted(cells[name]),
        'cell_area': [round(cell_area[(name, c)], 2) for c in sorted(cells[name])],
        'protos': dict(protos[name].most_common(4)),
    }
json.dump(out, open(outp, 'w'))
print('wrote', outp, 'materials', len(out['materials']), '%.0fs' % (time.time() - t0))
