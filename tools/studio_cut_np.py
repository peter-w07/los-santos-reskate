# Vectorised emulation of ReSkate Studio's texture-repeat cut (reskate_cli.exe RVA 0x295850 with its clip helper
# at 0x295270), mode "repeat". count(uv, pos) -> triangles emitted per input triangle.
#   per axis: lo = floor(min + 1e-5), hi = floor(max - 1e-5)
#   lo == hi on both axes: kept whole (moved by -lo).
#   else for each u tile: clip to [i, i+1] (only when lo_u != hi_u), drop if < 3 vertices; for each v tile: clip to
#   [j, j+1] (only when lo_v != hi_v), drop if < 3 vertices or |sum of fan cross products in 3D| <= 1e-10; emit n - 2.
# The clip is Sutherland-Hodgman, float32, a point on the line is inside, "emit current if inside, then the crossing
# if current and next differ"; a vertex lying exactly on a tile line can therefore be emitted twice (DUP_COUNTS).
import numpy as np
EPS = np.float32(1e-5)
K = 10
DUP_COUNTS = True      # set by calibrate(): whether a duplicated vertex adds a (zero-area) triangle to Studio's count
ROW_EXACT_LIMIT = 48   # column polygons crossing more rows than this are counted by formula


def _clip(P, n, axis, bound, sign):
    """P [B, K, C] float32, n [B], bound [B] float32, sign +1 (keep >= bound) or -1 (keep <= bound)."""
    B, Kc, C = P.shape
    ar = np.arange(Kc)[None, :]
    valid = ar < n[:, None]
    nxt_i = np.where(ar + 1 < n[:, None], ar + 1, 0)
    cur = P; nxt = np.take_along_axis(P, nxt_i[:, :, None], axis=1)
    s = np.float32(sign)
    dc = (cur[:, :, axis] - bound[:, None]) * s
    dn = (nxt[:, :, axis] - bound[:, None]) * s
    inc = dc >= 0; inn = dn >= 0
    e_cur = valid & inc
    e_int = valid & (inc != inn)
    with np.errstate(divide='ignore', invalid='ignore'):
        t = np.where(e_int, dc / (dc - dn), np.float32(0)).astype(np.float32)
    I = cur + (nxt - cur) * t[:, :, None]
    cnt = e_cur.astype(np.int64) + e_int.astype(np.int64)
    off = np.cumsum(cnt, axis=1) - cnt
    n2 = cnt.sum(axis=1)
    out = np.zeros((B, Kc, C), np.float32)
    rows = np.arange(B)[:, None].repeat(Kc, 1)
    m = e_cur & (off < Kc)
    out[rows[m], off[m]] = cur[m]
    o2 = off + e_cur
    m = e_int & (o2 < Kc)
    out[rows[m], o2[m]] = I[m]
    return out, np.minimum(n2, Kc)


def _fan_norm(P, n):
    """|sum over the fan of cross products| in 3D (columns 2..4), as Studio's degenerate test."""
    p0 = P[:, 0, 2:5]
    acc = np.zeros((P.shape[0], 3), np.float32)
    for k in range(2, P.shape[1]):
        m = (k < n)
        a = P[:, k - 1, 2:5] - p0; b = P[:, k, 2:5] - p0
        acc += np.cross(a, b) * m[:, None]
    return np.linalg.norm(acc, axis=1)


def _distinct(P, n):
    """number of vertices after removing a vertex equal to its predecessor (cyclic)"""
    B, Kc, C = P.shape
    ar = np.arange(Kc)[None, :]
    prev_i = np.where(ar - 1 >= 0, ar - 1, n[:, None] - 1)
    prev_i = np.clip(prev_i, 0, Kc - 1)
    prev = np.take_along_axis(P, prev_i[:, :, None], axis=1)
    same = np.all(P[:, :, :2] == prev[:, :, :2], axis=2) & (ar < n[:, None])
    return n - same.sum(axis=1)


def count(uv, pos, chunk=400000, stats=None):
    """uv [N,3,2], pos [N,3,3] -> int64 [N] triangles after Studio's repeat cut."""
    uv = np.asarray(uv, np.float32); pos = np.asarray(pos, np.float32)
    N = len(uv)
    out = np.ones(N, np.int64)
    mn = uv.min(axis=1); mx = uv.max(axis=1)
    lo = np.floor(mn + EPS).astype(np.int64); hi = np.maximum(np.floor(mx - EPS).astype(np.int64), lo)
    span = hi - lo + 1
    cut = np.nonzero((span[:, 0] > 1) | (span[:, 1] > 1))[0]
    if len(cut) == 0:
        return out
    out[cut] = 0
    # (triangle, u tile) pairs
    nu = span[cut, 0]
    order = np.argsort(-nu)
    cut = cut[order]; nu = nu[order]
    start = 0
    while start < len(cut):
        # take triangles until the pair budget is used
        csum = np.cumsum(nu[start:])
        take = max(1, int(np.searchsorted(csum, chunk, side='right')))
        sel = cut[start:start + take]; nsel = nu[start:start + take]
        start += take
        tri_of = np.repeat(np.arange(len(sel)), nsel)
        first = np.cumsum(nsel) - nsel
        iu = lo[sel, 0][tri_of] + (np.arange(len(tri_of)) - first[tri_of])
        P = np.zeros((len(tri_of), K, 5), np.float32)
        P[:, :3, :2] = uv[sel][tri_of]; P[:, :3, 2:5] = pos[sel][tri_of]
        n = np.full(len(tri_of), 3, np.int64)
        multi_u = (span[sel, 0] > 1)[tri_of]
        if multi_u.any():
            Pc, nc = _clip(P[multi_u], n[multi_u], 0, iu[multi_u].astype(np.float32), +1)
            Pc, nc = _clip(Pc, nc, 0, (iu[multi_u] + 1).astype(np.float32), -1)
            P[multi_u] = Pc; n[multi_u] = nc
        keep = n >= 3
        P = P[keep]; n = n[keep]; tri_of = tri_of[keep]
        if len(P) == 0:
            continue
        tsel = sel[tri_of]
        multi_v = span[tsel, 1] > 1
        # columns of a triangle that does not span v tiles: emitted as they are
        if (~multi_v).any():
            Pm = P[~multi_v]; nm = n[~multi_v]
            ok = _fan_norm(Pm, nm) > 1e-10
            c = (nm if DUP_COUNTS else _distinct(Pm, nm)) - 2
            np.add.at(out, tsel[~multi_v][ok], np.maximum(c[ok], 0))
        if multi_v.any():
            Pv = P[multi_v]; nv_ = n[multi_v]; tv = tsel[multi_v]
            ar = np.arange(K)[None, :]
            vv = np.where(ar < nv_[:, None], Pv[:, :, 1], np.nan)
            vmin = np.nanmin(vv, axis=1); vmax = np.nanmax(vv, axis=1)
            j0 = np.maximum(lo[tv, 1], np.floor(vmin).astype(np.int64))
            j1 = np.minimum(hi[tv, 1], np.ceil(vmax).astype(np.int64) - 1)
            j1 = np.maximum(j1, j0)
            rows = j1 - j0 + 1
            big = rows > ROW_EXACT_LIMIT
            if big.any():
                # formula: (m - 2) + 2 * inner lines - vertices on those lines
                Pb = Pv[big]; nb = nv_[big]
                vb = np.where(np.arange(K)[None, :] < nb[:, None], Pb[:, :, 1], np.nan)
                onl = (vb == np.floor(vb)) & (vb >= (j0[big] + 1)[:, None]) & (vb <= j1[big][:, None])
                c = (_distinct(Pb, nb) - 2) + 2 * (rows[big] - 1) - onl.sum(axis=1)
                np.add.at(out, tv[big], np.maximum(c, 0))
                if stats is not None: stats['formula_columns'] = stats.get('formula_columns', 0) + int(big.sum())
            sm = ~big
            if sm.any():
                Ps = Pv[sm]; ns = nv_[sm]; ts = tv[sm]; r = rows[sm]; j0s = j0[sm]
                col_of = np.repeat(np.arange(len(Ps)), r)
                firstc = np.cumsum(r) - r
                jv = j0s[col_of] + (np.arange(len(col_of)) - firstc[col_of])
                # in sub-chunks
                for a in range(0, len(col_of), chunk):
                    cc = col_of[a:a + chunk]; jj = jv[a:a + chunk]
                    Q, nq = _clip(Ps[cc], ns[cc], 1, jj.astype(np.float32), +1)
                    Q, nq = _clip(Q, nq, 1, (jj + 1).astype(np.float32), -1)
                    ok = (nq >= 3)
                    ok[ok] &= _fan_norm(Q[ok], nq[ok]) > 1e-10
                    c = (nq if DUP_COUNTS else _distinct(Q, nq)) - 2
                    np.add.at(out, ts[cc][ok], np.maximum(c[ok], 0))
    return out


if __name__ == '__main__':
    import studio_cut, random, time
    def quad_tris(u0, v0, u1, v1, n=1):
        us = np.linspace(u0, u1, n + 1); vs = np.linspace(v0, v1, n + 1); o = []
        for iz in range(n):
            for ix in range(n):
                a = (us[ix], vs[iz]); b = (us[ix + 1], vs[iz]); c = (us[ix], vs[iz + 1]); d = (us[ix + 1], vs[iz + 1])
                o += [(a, c, b), (b, c, d)]
        return o
    def run(ts):
        uv = np.array(ts, np.float32)
        pos = np.concatenate([uv * 0.37, np.zeros((len(uv), 3, 1), np.float32)], axis=2)
        return int(count(uv, pos).sum())
    for box, n in (((0, 0, 8, 1), 1), ((0, 0, 1, 8), 1), ((0, 0, 8, 8), 1), ((0, 0, 64, 64), 1), ((0.25, 0, 2.75, 1), 1), ((0, 0, 8, 8), 4), ((0, 0, 8, 8), 8), ((0, 0, 8, 8), 16),
                   ((0.5, 0, 1.5, 1), 1), ((-3.5, -2.5, -2.5, -1.5), 1), ((0, 0, 512, 512), 1), ((0, 0, 4096, 1), 1)):
        ts = quad_tris(*box, n=n)
        ex = sum(studio_cut.pieces_exact(t) for t in ts) if (box[2] - box[0]) * (box[3] - box[1]) <= 70000 else None
        print(box, n, 'python exact', ex, 'numpy', run(ts))
    random.seed(1)
    ts = [[(random.uniform(-3, 6), random.uniform(-3, 6)) for _ in range(3)] for _ in range(3000)]
    a = [studio_cut.pieces_exact(t) for t in ts]
    uv = np.array(ts, np.float32); pos = np.concatenate([uv * 0.37, np.zeros((len(uv), 3, 1), np.float32)], axis=2)
    b = count(uv, pos)
    print('random triangles: python', sum(a), 'numpy', int(b.sum()), 'mismatching', int((np.array(a) != b).sum()))
    t0 = time.time()
    uv = np.random.default_rng(2).uniform(-2, 5, (300000, 3, 2)).astype(np.float32); pos = np.concatenate([uv * 0.37, np.zeros((len(uv), 3, 1), np.float32)], axis=2)
    print('300k random triangles ->', int(count(uv, pos).sum()), 'in %.1fs' % (time.time() - t0))
