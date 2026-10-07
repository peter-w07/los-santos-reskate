# Read-only: per texture file of a normalized map: size, format, mean colour, how far its texels are from that
# mean (what is lost when the texture becomes one flat colour), and its alpha coverage.
#   python texinfo.py <map folder> <out.json>
import json, struct, sys, os, glob
import numpy as np
from PIL import Image

def rgb565(c):
    r = ((c >> 11) & 31).astype(np.float32) * (255.0 / 31); g = ((c >> 5) & 63).astype(np.float32) * (255.0 / 63); b = (c & 31).astype(np.float32) * (255.0 / 31)
    return np.stack([r, g, b], axis=-1)

def colour_block(blk, dxt1):
    """blk: (N, 8) uint8 -> rgba (N, 16, 4) float"""
    c0 = blk[:, 0].astype(np.uint32) | (blk[:, 1].astype(np.uint32) << 8); c1 = blk[:, 2].astype(np.uint32) | (blk[:, 3].astype(np.uint32) << 8)
    p0 = rgb565(c0); p1 = rgb565(c1)
    four = (c0 > c1) | (not dxt1)
    pal = np.zeros((len(blk), 4, 4), dtype=np.float32)
    pal[:, 0, :3] = p0; pal[:, 1, :3] = p1; pal[:, :, 3] = 255
    f = four[:, None]
    pal[:, 2, :3] = np.where(f, (2 * p0 + p1) / 3, (p0 + p1) / 2)
    pal[:, 3, :3] = np.where(f, (p0 + 2 * p1) / 3, 0)
    pal[:, 3, 3] = np.where(four, 255, 0)
    bits = blk[:, 4].astype(np.uint32) | (blk[:, 5].astype(np.uint32) << 8) | (blk[:, 6].astype(np.uint32) << 16) | (blk[:, 7].astype(np.uint32) << 24)
    sel = (bits[:, None] >> (2 * np.arange(16, dtype=np.uint32))[None, :]) & 3
    return np.take_along_axis(pal, sel[:, :, None].astype(np.int64).repeat(4, axis=2), axis=1)

def decode_dds(b, want=64):
    h, w = struct.unpack_from('<II', b, 12); mips = max(1, struct.unpack_from('<I', b, 28)[0]); four = b[84:88].decode('ascii', 'replace')
    bs = 8 if four == 'DXT1' else 16
    off = 128; lw, lh = w, h; lvl = 0
    while lvl < mips - 1 and max(lw, lh) > want:
        off += max(1, (lw + 3) // 4) * max(1, (lh + 3) // 4) * bs; lw = max(1, lw >> 1); lh = max(1, lh >> 1); lvl += 1
    bw, bh = max(1, (lw + 3) // 4), max(1, (lh + 3) // 4)
    data = np.frombuffer(b, dtype=np.uint8, count=bw * bh * bs, offset=off).reshape(bw * bh, bs)
    if four == 'DXT1': px = colour_block(data, True)
    else:
        px = colour_block(data[:, 8:], False)
        if four == 'DXT3':
            a = data[:, :8]
            nib = np.stack([a & 15, a >> 4], axis=-1).reshape(len(data), 16).astype(np.float32) * 17
            px[:, :, 3] = nib
        else:
            a0 = data[:, 0].astype(np.float32); a1 = data[:, 1].astype(np.float32)
            bits = np.zeros(len(data), dtype=np.uint64)
            for k in range(6): bits |= data[:, 2 + k].astype(np.uint64) << np.uint64(8 * k)
            sel = ((bits[:, None] >> (3 * np.arange(16, dtype=np.uint64))[None, :]) & np.uint64(7)).astype(np.int64)
            pal = np.zeros((len(data), 8), dtype=np.float32)
            pal[:, 0] = a0; pal[:, 1] = a1
            big = a0 > a1
            for k in range(2, 8):
                pal[:, k] = np.where(big, ((8 - k) * a0 + (k - 1) * a1) / 7, ((6 - k) * a0 + (k - 1) * a1) / 5 if k < 6 else (0 if k == 6 else 255))
            px[:, :, 3] = np.take_along_axis(pal, sel, axis=1)
    return w, h, mips, four, px.reshape(-1, 4)

def stats(px):
    a = px[:, 3] / 255.0; ws = a.sum()
    if ws < 1e-6: return [128, 128, 128], 0.0, 0.0, float(a.mean()), float((a < 0.5).mean())
    mean = (px[:, :3] * a[:, None]).sum(axis=0) / ws
    d = (px[:, :3] - mean) / 255.0
    rms = float(np.sqrt(((d * d).mean(axis=1) * a).sum() / ws))
    luma = px[:, :3] @ np.array([0.299, 0.587, 0.114], dtype=np.float32) / 255.0
    op = luma[a > 0.5]
    lr = float(np.percentile(op, 95) - np.percentile(op, 5)) if len(op) else 0.0
    return [int(v) for v in mean], rms, lr, float(a.mean()), float((a < 0.5).mean())

if __name__ == '__main__':
    root, outp = sys.argv[1], sys.argv[2]
    out = {}
    files = sorted(glob.glob(os.path.join(root, 'textures', '*')))
    for i, path in enumerate(files):
        name = 'textures/' + os.path.basename(path)
        try:
            if path.lower().endswith('.dds'):
                b = open(path, 'rb').read()
                w, h, mips, four, px = decode_dds(b)
            else:
                im = Image.open(path); w, h = im.size; mips = 1; four = 'png/' + im.mode
                im = im.convert('RGBA')
                if max(w, h) > 64: im = im.resize((max(1, w * 64 // max(w, h)), max(1, h * 64 // max(w, h))), Image.BOX)
                px = np.asarray(im, dtype=np.float32).reshape(-1, 4)
            mean, rms, lr, am, cut = stats(px)
            out[name] = {'w': w, 'h': h, 'mips': mips, 'format': four, 'bytes': os.path.getsize(path), 'mean': mean, 'rms': round(rms, 4), 'luma_range': round(lr, 4), 'alpha_mean': round(am, 4), 'alpha_cut': round(cut, 4)}
        except Exception as e:
            out[name] = {'error': repr(e)[:120]}
        if (i + 1) % 2000 == 0: print(i + 1, '/', len(files), flush=True)
    json.dump(out, open(outp, 'w'))
    print('wrote', outp, len(out), 'errors', sum(1 for v in out.values() if 'error' in v))
