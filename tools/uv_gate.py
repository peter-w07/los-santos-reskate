# Gate for a finished export, to run before Studio:   python uv_gate.py <map folder> [max_ratio=1.5] [max_tiles=4096]
# Predicts Studio's compiled triangle count (triangles= in its log) with the calibrated emulation of its cut at
# texture repeats, lists the parts that grow most, and exits 1 when the count exceeds max_ratio x the exported
# triangles or when a triangle Studio will cut lies over more than max_tiles texture tiles.
# The prediction matched Studio exactly on LS-D5 (13,429,736), big14 (28,565,296) and the capped LS-F5 (8,168,464).
# Triangles over 65,536 tiles are estimated (2 x UV area + UV outline) so that an unfixed export still finishes.
import os, sys, json, collections, time
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import studio_cut_np
studio_cut_np.ROW_EXACT_LIMIT = 4096
from survey import load_glb, eligible
EPS = 1e-5


def main(root, max_ratio=1.5, max_tiles=4096.0):
    t0 = time.time()
    doc = json.load(open(os.path.join(root, 'map.json'), encoding='utf-8'))
    mats = doc['materials']
    recs = collections.defaultdict(list)
    for o in doc['objects']:
        if 'mesh' in o and o.get('render', True) is not False: recs[(o['mesh'], o.get('primitive', 0))].append(o)
    drawn = out = over_n = est_n = 0
    parts = []
    for mesh in sorted(set(k[0] for k in recs)):
        for pi, pr in enumerate(load_glb(os.path.join(root, mesh))):
            rl = recs.get((mesh, pi))
            if not rl or pr is None: continue
            pos, uv, ix = pr
            ntri = len(ix); n = len(rl)
            el = [o for o in rl if eligible(mats[o['material']])]
            if uv is None or not el:
                drawn += ntri * n; out += ntri * n; continue
            t = uv[ix]; p3 = pos[ix]
            mn = t.min(axis=1); mx = t.max(axis=1)
            lo = np.floor(mn + EPS); hi = np.maximum(np.floor(mx - EPS), lo)
            tiles = (hi[:, 0] - lo[:, 0] + 1) * (hi[:, 1] - lo[:, 1] + 1)
            small = tiles <= 65536
            pieces = np.ones(ntri)
            if small.any(): pieces[small] = studio_cut_np.count(t[small], p3[small])
            if (~small).any():
                tb = t[~small]
                a_uv = 0.5 * np.abs((tb[:, 1, 0] - tb[:, 0, 0]) * (tb[:, 2, 1] - tb[:, 0, 1]) - (tb[:, 2, 0] - tb[:, 0, 0]) * (tb[:, 1, 1] - tb[:, 0, 1]))
                pieces[~small] = np.round(2 * a_uv + np.abs(tb - np.roll(tb, 1, axis=1)).sum(axis=(1, 2)))
                est_n += int((~small).sum())
            # a mesh used by cut and uncut materials is cut for both (Studio cuts the mesh, not the record)
            p = int(pieces.sum())
            drawn += ntri * n; out += p * n
            over_n += int((tiles > max_tiles).sum())
            if p > ntri:
                m = mats[el[0]['material']]
                parts.append((p * n - ntri * n, mesh, pi, '%s/%s' % (m.get('shader_override') or m.get('domain'), m.get('alpha')), ntri, n, float(tiles.max()), m.get('texture')))
    parts.sort(reverse=True)
    ratio = out / max(1, drawn)
    print('%s: exported %d triangles, predicted compiled %d (x%.3f); %d cut triangles over %g tiles%s; %.0f s' % (
        root, drawn, out, ratio, over_n, max_tiles, ' (%d estimated)' % est_n if est_n else '', time.time() - t0))
    for g, mesh, pi, cls, ntri, n, tm, tex in parts[:10]:
        print('   +%9d  %s primitive %d  %s  %d triangles x %d  largest triangle %d tiles  %s' % (g, mesh, pi, cls, ntri, n, tm, tex))
    ok = ratio <= max_ratio and over_n == 0
    print('GATE ' + ('ok' if ok else 'FAILED'))
    return 0 if ok else 1


if __name__ == '__main__':
    a = sys.argv
    sys.exit(main(a[1], float(a[2]) if len(a) > 2 else 1.5, float(a[3]) if len(a) > 3 else 4096.0))
