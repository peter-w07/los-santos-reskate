# Read-only survey of a normalized map: per-triangle UV extents, the triangles Studio will cut at texture repeats
# (calibrated rule, see studio_cut.py and matrix_results.json), the predicted compiled triangle count, the worst offenders.
#   python survey.py <map folder> [out.json]
import json, struct, sys, os, glob, collections, math, time
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import studio_cut_np
studio_cut_np.ROW_EXACT_LIMIT = 4096

CT = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}; NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
EPS = 1e-5


def eligible(m):
    """Studio cuts at texture repeats only: domain decal (any alpha), or domain surface with alpha blend.
    (opaque, mask, foliage, glass, ocean keep their UVs; base_color_extension is 'repeat' in every export.)"""
    d = m.get('shader_override') or m.get('domain') or 'surface'
    a = m.get('alpha', 'auto')
    if d == 'decal': return True
    if d == 'surface' and a in ('blend', 'blended'): return True
    return False


def load_glb(path):
    b = open(path, 'rb').read()
    jl, = struct.unpack_from('<I', b, 12); j = json.loads(b[20:20 + jl]); bin0 = 20 + jl + 8
    def acc(i):
        a = j['accessors'][i]; bv = j['bufferViews'][a['bufferView']]
        off = bin0 + bv.get('byteOffset', 0) + a.get('byteOffset', 0); n = NC[a['type']]
        dt = np.dtype('<' + CT[a['componentType']]); stride = bv.get('byteStride', 0)
        if stride and stride != dt.itemsize * n:
            arr = np.frombuffer(b, dtype=np.uint8, count=stride * (a['count'] - 1) + dt.itemsize * n, offset=off)
            idx = (np.arange(a['count'])[:, None] * stride + np.arange(dt.itemsize * n)[None, :]).ravel()
            return np.frombuffer(arr[idx].tobytes(), dtype=dt).reshape(a['count'], n)
        return np.frombuffer(b, dtype=dt, count=a['count'] * n, offset=off).reshape(a['count'], n)
    out = []
    for m in j.get('meshes', []):
        for p in m['primitives']:
            at = p['attributes']
            if 'indices' not in p: out.append(None); continue
            ix = acc(p['indices']).reshape(-1, 3).astype(np.int64)
            pos = acc(at['POSITION']).astype(np.float64)
            uv = acc(at['TEXCOORD_0']).astype(np.float64) if 'TEXCOORD_0' in at else None
            out.append((pos, uv, ix))
    return out


BINS = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 4096, 1e30]
BIN_NAMES = ['<=1', '1-2', '2-4', '4-8', '8-16', '16-32', '32-64', '64-128', '128-256', '256-512', '512-1024', '1024-4096', '>4096']


def main(root, outp=None):
    t0 = time.time()
    doc = json.load(open(os.path.join(root, 'map.json'), encoding='utf-8'))
    mats = doc['materials']
    recs = collections.defaultdict(list)
    for o in doc['objects']:
        if 'mesh' in o and o.get('render', True) is not False:
            recs[(o['mesh'], o.get('primitive', 0))].append(o)
    hist = collections.defaultdict(lambda: [[0, 0, 0.0] for _ in BINS])       # class -> bin -> [drawn tris, predicted out tris, area m2]
    tot = collections.Counter()
    offenders = []
    worst_tris = []
    files = sorted(set(k[0] for k in recs))
    for fi, mesh in enumerate(files):
        prims = load_glb(os.path.join(root, mesh))
        for pi, pr in enumerate(prims):
            rl = recs.get((mesh, pi))
            if not rl or pr is None: continue
            pos, uv, ix = pr
            ntri = len(ix)
            # one mesh may be placed with several materials; group the placements by material
            bymat = collections.Counter(o['material'] for o in rl)
            if uv is None:
                tot['drawn'] += ntri * len(rl); tot['out'] += ntri * len(rl); continue
            t = uv[ix]                                               # tri, corner, 2
            mn = t.min(axis=1); mx = t.max(axis=1)
            ext = (mx - mn)                                          # per axis UV extent
            emax = ext.max(axis=1)
            lo = np.floor(mn + EPS); hi = np.maximum(np.floor(mx - EPS), lo)
            span = (hi - lo + 1)
            cells = span[:, 0] * span[:, 1]
            p3 = pos[ix]
            area = 0.5 * np.linalg.norm(np.cross(p3[:, 1] - p3[:, 0], p3[:, 2] - p3[:, 0]), axis=1)
            b = np.searchsorted(np.array(BINS), emax, side='left')
            b = np.minimum(b, len(BINS) - 1)
            pieces = None
            for mat, n in bymat.items():
                m = mats[mat]
                dom = m.get('shader_override') or m.get('domain'); cls = '%s/%s' % (dom, m.get('alpha'))
                el = eligible(m)
                out = np.ones(ntri)
                if el:
                    if pieces is None:
                        pieces = studio_cut_np.count(t, p3).astype(np.float64)
                    out = pieces
                    moved = int(((cells == 1) & ((lo[:, 0] != 0) | (lo[:, 1] != 0))).sum()); split = int((cells > 1).sum())
                    tot['studio_changed_tris'] += (moved + split)     # counted once per mesh by Studio
                    tot['meshes_outside'] += 1 if (moved + split) else 0
                h = hist[cls + (' CUT' if el else '')]
                cnt = np.bincount(b, minlength=len(BINS)); osum = np.bincount(b, weights=out, minlength=len(BINS)); asum = np.bincount(b, weights=area, minlength=len(BINS))
                for i in range(len(BINS)):
                    h[i][0] += int(cnt[i]) * n; h[i][1] += int(osum[i]) * n; h[i][2] += float(asum[i]) * n
                tot['drawn'] += ntri * n; tot['out'] += int(out.sum()) * n
                if el:
                    tot['eligible_drawn'] += ntri * n; tot['eligible_out'] += int(out.sum()) * n
                    grow = int(out.sum()) - ntri
                    if grow > 0:
                        o = [r for r in rl if r['material'] == mat][0]
                        tr = o['transform']
                        k = int(np.argmax(out))
                        c = p3[k].mean(axis=0)
                        w = [tr[0] * c[0] + tr[3] * c[1] + tr[6] * c[2] + tr[9], tr[1] * c[0] + tr[4] * c[1] + tr[7] * c[2] + tr[10], tr[2] * c[0] + tr[5] * c[1] + tr[8] * c[2] + tr[11]]
                        big = cells > 1
                        offenders.append(dict(mesh=mesh, primitive=pi, record=o['name'], placements=n, material=mat, texture=m.get('texture'), cls=cls,
                                              tris=ntri, cut_tris=int(big.sum()), out_tris=int(out.sum()), growth_drawn=grow * n,
                                              max_span_u=float(span[:, 0].max()), max_span_v=float(span[:, 1].max()), max_abs_uv=float(np.abs(uv[ix]).max()),
                                              area_m2=float(area.sum()), cut_area_m2=float(area[big].sum()),
                                              worst_tri=dict(pieces=int(out[k]), span=[float(span[k, 0]), float(span[k, 1])], area_m2=float(area[k]),
                                                             uv=[[round(float(x), 3) for x in q] for q in t[k]], gta_xyz=[round(w[0], 1), round(-w[2], 1), round(w[1], 1)]),
                                              # how dense the tiling is: repeats per metre along the longest edge of the worst triangle
                                              repeats_per_m=float(emax[k] / max(1e-6, np.linalg.norm(p3[k].max(axis=0) - p3[k].min(axis=0))))))
        if fi % 200 == 0:
            print('  %d/%d files, %.0fs' % (fi, len(files), time.time() - t0), file=sys.stderr, flush=True)
    offenders.sort(key=lambda o: -o['growth_drawn'])
    res = dict(map=root, totals=dict(tot), hist={k: v for k, v in hist.items()}, bins=BIN_NAMES, offenders=offenders[:400], n_offenders=len(offenders),
               growth_all_offenders=sum(o['growth_drawn'] for o in offenders), seconds=round(time.time() - t0, 1))
    if outp:
        json.dump(res, open(outp, 'w'), indent=1)
    print('%s: drawn %.3f M triangles -> predicted after the texture-repeat cut %.3f M (x%.2f); cut-eligible drawn %.3f M -> %.3f M; %d primitives grow' % (
        root, tot['drawn'] / 1e6, tot['out'] / 1e6, tot['out'] / max(1, tot['drawn']), tot['eligible_drawn'] / 1e6, tot['eligible_out'] / 1e6, len(offenders)))
    print('  Studio warning would read about: %d meshes, %d triangles split or moved' % (tot['meshes_outside'], tot['studio_changed_tris']))
    print('  per-triangle UV extent (max of u, v), drawn triangles -> predicted triangles, by material class:')
    print('  %-26s' % 'class' + ''.join('%14s' % n for n in BIN_NAMES))
    for cls in sorted(hist):
        h = hist[cls]
        print('  %-26s' % cls + ''.join('%14s' % ('%d' % x[0] if x[0] == x[1] else '%d>%s' % (x[0], ('%.2fM' % (x[1] / 1e6)) if x[1] >= 1e6 else '%d' % x[1])) for x in h))
    print('  worst offenders (growth in drawn triangles):')
    for o in offenders[:25]:
        print('   +%9d  %-22s x%-3d %-16s tris %6d cut %6d span %5.0f x %5.0f  area %8.1f m2  %.2f rep/m  at %s  %s' % (
            o['growth_drawn'], o['mesh'].replace('meshes/', ''), o['placements'], o['cls'], o['tris'], o['cut_tris'], o['max_span_u'], o['max_span_v'], o['cut_area_m2'], o['repeats_per_m'],
            o['worst_tri']['gta_xyz'], (o['texture'] or '').replace('textures/', '')))
    return res


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
