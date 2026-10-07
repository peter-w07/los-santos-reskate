"""Frostbite patch containers as ReSkate reads and writes them (skate. 2025).

Python port of the parts of ReSkate (github.com/Dingo-Shenanigans/ReSkate, Engine/Resource and
Engine/Vfs) that merge_pack.py needs: superbundle TOC, bundle region, binary bundle manifest,
the native DB container of layout.toc, and the CAS block stream.  Every writer here is the
counterpart of ReSkate's own writer, so a file written by this module reads back in ReSkate's
merge exactly as one of its own.
"""
import ctypes
import glob
import hashlib
import os
import struct
import zlib

ENVELOPE = 0x22C
MAGIC = b'\x00\xd1\xce\x01'
FNV_PRIME = 0x01000193
FNV_SEED = 0x811C9DC5
BUNDLE_SALT = 0x7065636E
BUNDLE_MAGIC = 0xED1CEDB8

EBX, RES, CHUNK = 0, 1, 2


# --------------------------------------------------------------------------------------------
# TOC
# --------------------------------------------------------------------------------------------

def toc_hash(key, seed=FNV_SEED):
    for b in key:
        if b >= 128:
            b -= 256
        seed = ((seed * FNV_PRIME) & 0xFFFFFFFF) ^ (b & 0xFFFFFFFF)
    return seed % FNV_PRIME


def _perfect_hash(keys):
    size = len(keys)
    hmap = [-1] * size
    order = [None] * size
    if not size:
        return hmap, order
    if len(set(keys)) != size:
        raise ValueError('TOC perfect-hash keys must be unique')
    buckets = [[] for _ in range(size)]
    first = [toc_hash(k) % size for k in keys]
    for index, slot in enumerate(first):
        buckets[slot].append(index)
    buckets.sort(key=lambda b: -len(b))          # stable, like std::stable_sort
    used = [False] * size
    at = 0
    while at < size and len(buckets[at]) > 1:
        bucket = buckets[at]
        seed = 1
        while True:
            indices = []
            ok = True
            for member in bucket:
                index = toc_hash(keys[member], seed) % size
                if used[index] or index in indices:
                    ok = False
                    break
                indices.append(index)
            if ok:
                break
            seed += 1
        hmap[first[bucket[0]]] = seed
        for i, member in enumerate(bucket):
            order[indices[i]] = member
            used[indices[i]] = True
        at += 1
    free = 0
    while at < size and buckets[at]:
        while used[free]:
            free = (free + 1) % size
        member = buckets[at][0]
        hmap[first[member]] = -free - 1
        order[free] = member
        used[free] = True
        free = (free + 1) % size
        at += 1
    return hmap, order


def _hash_lookup(hmap, ordered_keys, key):
    if not hmap or len(hmap) != len(ordered_keys):
        return None
    value = hmap[toc_hash(key) % len(hmap)]
    index = (-value - 1) if value < 0 else toc_hash(key, value) % len(hmap)
    if index >= len(ordered_keys) or ordered_keys[index] != key:
        return None
    return index


class Loc(tuple):
    """CAS identifier: (patch, installChunk, archive)."""
    __slots__ = ()

    def __new__(cls, patch, chunk, archive):
        return tuple.__new__(cls, (bool(patch), chunk, archive))

    patch = property(lambda s: s[0])
    chunk = property(lambda s: s[1])
    archive = property(lambda s: s[2])


def _decode_id(v):
    return Loc((v & (0xFF << 48)) != 0, (v >> 16) & 0xFFFFFFFF, v & 0xFFFF)


def _encode_id(loc):
    return ((1 if loc[0] else 0) << 48) | (loc[1] << 16) | loc[2]


class TocChunk:
    __slots__ = ('guid', 'loc', 'offset', 'size', 'removed')

    def __init__(self, guid, loc=None, offset=0, size=0, removed=False):
        self.guid, self.loc, self.offset, self.size, self.removed = guid, loc, offset, size, removed

    def key(self):
        return (self.loc, self.offset, self.size, self.removed)


class Toc:
    def __init__(self):
        self.flags = 3
        self.bundles = []        # [name, region bytes]
        self.chunks = []         # TocChunk
        self.bundle_hash = []
        self.chunk_hash = []


def _huffman(d, names_offset, names_count, table_offset, table_count):
    words = struct.unpack_from('>%dI' % names_count, d, names_offset)
    vals = struct.unpack_from('>%dI' % table_count, d, table_offset)
    nodes = []
    byv = {}
    left = None
    counter = 0
    root = None
    for v in vals:
        if v in byv:
            node = byv[v]
        else:
            node = len(nodes)
            nodes.append((v, None, None))
            byv[v] = node
        if left is None:
            left = node
            continue
        parent = len(nodes)
        nodes.append((counter, left, node))
        byv.setdefault(counter, parent)
        root = parent
        counter += 1
        left = None
    nbits = len(words) * 32

    def decode(bit):
        out = []
        while bit < nbits:
            n = root
            while nodes[n][1] is not None:
                one = (words[bit >> 5] >> (bit & 31)) & 1
                n = nodes[n][2] if one else nodes[n][1]
                bit += 1
            ch = (~nodes[n][0]) & 0xFFFF
            if ch == 0:
                return ''.join(out)
            out.append(chr(ch))
        raise ValueError('TOC Huffman name is unterminated')
    return decode


def read_toc(data):
    if isinstance(data, str):
        with open(data, 'rb') as f:
            data = f.read()
    if len(data) < ENVELOPE + 48 or data[:4] != MAGIC:
        raise ValueError('TOC envelope is invalid')
    d = data[ENVELOPE:]
    (bho, bdo, bcount, cho, cgo, ccount, _a, _b, nameso, cdo, dcount, flags) = \
        struct.unpack_from('>IIiIIiIIIIiI', d, 0)
    decode = None
    if flags & 4:
        ncount, tcount, toff = struct.unpack_from('>III', d, 48)
        decode = _huffman(d, nameso, ncount, toff, tcount)
    toc = Toc()
    toc.flags = flags
    toc.bundle_hash = list(struct.unpack_from('>%di' % bcount, d, bho))
    for i in range(bcount):
        no, sf, ro = struct.unpack_from('>IIQ', d, bdo + 16 * i)
        size = sf & 0x3FFFFFFF
        if sf >> 30 != 1:
            raise ValueError('TOC bundle region is not inline')
        if decode:
            name = decode(no)
        else:
            end = d.index(b'\0', nameso + no)
            name = d[nameso + no:end].decode('latin1')
        toc.bundles.append([name, bytes(d[ro:ro + size])])
    if ccount:
        toc.chunk_hash = list(struct.unpack_from('>%di' % ccount, d, cho))
        words = struct.unpack_from('>%dI' % dcount, d, cdo)
        for i in range(ccount):
            pos = cgo + 20 * i
            guid = bytes(d[pos:pos + 16][::-1])
            enc, = struct.unpack_from('>i', d, pos + 16)
            if enc == -1:
                toc.chunks.append(TocChunk(guid, removed=True))
                continue
            flag = (enc & 0xFFFFFFFF) >> 24
            w = enc & 0xFFFFFF
            if flag != 0x80 or w + 4 > len(words):
                raise ValueError('TOC chunk data reference is invalid')
            ident = (words[w] << 32) | words[w + 1]
            toc.chunks.append(TocChunk(guid, _decode_id(ident), words[w + 2], words[w + 3]))
    return toc


def verify_toc(toc):
    keys = [b[0].lower().encode('latin1') for b in toc.bundles]
    for i, k in enumerate(keys):
        if _hash_lookup(toc.bundle_hash, keys, k) != i:
            raise ValueError('TOC bundle perfect hash does not resolve')
    keys = [c.guid for c in toc.chunks]
    for i, k in enumerate(keys):
        if _hash_lookup(toc.chunk_hash, keys, k) != i:
            raise ValueError('TOC chunk perfect hash does not resolve')


def _align(v, a):
    return (v + a - 1) & ~(a - 1)


def write_toc(bundles, chunks=(), flags=3):
    """bundles: [(name, region)], chunks: [TocChunk].  Mirrors fb::write_patch_toc."""
    if flags & 4:
        raise ValueError('patch TOC writer uses uncompressed names')
    bkeys = [b[0].lower().encode('latin1') for b in bundles]
    bmap, border = _perfect_hash(bkeys)
    ckeys = [c.guid for c in chunks]
    cmap, corder = _perfect_hash(ckeys)

    names = bytearray()
    name_offsets = []
    for src in border:
        name_offsets.append(len(names))
        names += bundles[src][0].encode('latin1') + b'\0'

    words = []
    table = bytearray()
    for src in corder:
        c = chunks[src]
        table += c.guid[::-1]
        if c.removed:
            table += struct.pack('>i', -1)
        else:
            table += struct.pack('>I', (0x80 << 24) | len(words))
            ident = _encode_id(c.loc)
            words += [ident >> 32, ident & 0xFFFFFFFF, c.offset, c.size]

    pos = 48
    bho = pos
    pos = _align(pos + len(bundles) * 4, 8)
    bdo = pos
    pos = _align(pos + len(bundles) * 16, 4)
    cho = pos
    pos = _align(pos + len(chunks) * 4, 4)
    cgo = pos
    pos = _align(pos + len(table), 4)
    cdo = pos
    pos += len(words) * 4
    nameso = pos
    pos = _align(pos + len(names), 4)
    region_offsets = []
    for src in border:
        region_offsets.append(pos)
        pos = _align(pos + len(bundles[src][1]), 4)

    out = bytearray(ENVELOPE + pos)
    out[:4] = MAGIC
    struct.pack_into('>IIiIIiIIIIiI', out, ENVELOPE, bho, bdo, len(bundles),
                     cho if chunks else 0, cgo if chunks else 0, len(chunks),
                     cdo, cdo, nameso, cdo, len(words), flags)
    struct.pack_into('>%di' % len(bmap), out, ENVELOPE + bho, *bmap)
    for i, src in enumerate(border):
        region = bundles[src][1]
        if len(region) > 0x3FFFFFFF:
            raise OverflowError('TOC bundle region exceeds 30-bit size')
        struct.pack_into('>IIQ', out, ENVELOPE + bdo + 16 * i, name_offsets[i],
                         (1 << 30) | len(region), region_offsets[i])
        out[ENVELOPE + region_offsets[i]:ENVELOPE + region_offsets[i] + len(region)] = region
    if chunks:
        struct.pack_into('>%di' % len(cmap), out, ENVELOPE + cho, *cmap)
        out[ENVELOPE + cgo:ENVELOPE + cgo + len(table)] = table
        struct.pack_into('>%dI' % len(words), out, ENVELOPE + cdo, *words)
    out[ENVELOPE + nameso:ENVELOPE + nameso + len(names)] = names
    return bytes(out)


# --------------------------------------------------------------------------------------------
# Bundle region (the per-bundle file table in a TOC)
# --------------------------------------------------------------------------------------------

def read_region(r):
    """-> (files [(Loc, offset, size)], inline manifest bytes)."""
    mo, ms, lo, total, do, ro, co, _z, total2 = struct.unpack_from('>iiIiIIIIi', r, 0)
    if mo < 0 or ms < 0 or total < 0 or total != total2 or ro != do or co != do:
        raise ValueError('Bundle region header is invalid')
    inline = bytes(r[mo:mo + ms]) if ms else b''
    files = []
    pos = do
    cur = None
    for i in range(total):
        flag = r[lo + i]
        if flag == 1:
            enc, = struct.unpack_from('>I', r, pos)
            pos += 4
            cur = Loc((enc & 0x00FF0000) != 0, (enc >> 8) & 0xFF, enc & 0xFF)
        elif flag != 0:
            v, = struct.unpack_from('>Q', r, pos)
            pos += 8
            cur = _decode_id(v)
        if cur is None:
            raise ValueError('First bundle file has no CAS identifier')
        off, size = struct.unpack_from('>II', r, pos)
        pos += 8
        files.append((cur, off, size))
    return files, inline


def write_region(files, inline=b''):
    table = bytearray()
    flags = bytearray()
    prev = None
    for loc, off, size in files:
        if prev is not None and prev == loc:
            flags.append(0)
        else:
            flags.append(0x80)
            table += struct.pack('>Q', _encode_id(loc))
            prev = loc
        table += struct.pack('>II', off, size)
    if flags:
        flags[0] |= 0x04
    data_offset = 0x24
    meta_offset = data_offset + len(table) if inline else 0
    loc_offset = (meta_offset + len(inline)) if inline else data_offset + len(table)
    head = struct.pack('>iiIiIIIIi', meta_offset, len(inline), loc_offset, len(files),
                       data_offset, data_offset, data_offset, 0, len(files))
    return head + bytes(table) + bytes(inline) + bytes(flags)


# --------------------------------------------------------------------------------------------
# Binary bundle manifest (the first file of a region: names, sha1s, sizes)
# --------------------------------------------------------------------------------------------

class Asset:
    __slots__ = ('kind', 'name', 'sha1', 'size', 'rtype', 'rmeta', 'rid', 'guid', 'loff', 'lsize')

    def __init__(self, kind):
        self.kind = kind
        self.name = ''
        self.sha1 = b''
        self.size = 0          # originalSize (ebx, res)
        self.rtype = 0
        self.rmeta = b''
        self.rid = 0
        self.guid = b''
        self.loff = 0
        self.lsize = 0

    def key(self):
        return (self.kind, self.guid if self.kind == CHUNK else self.name.lower())

    def ident(self):
        """Everything the manifest says about the asset: equal idents are the same manifest row."""
        return (self.kind, self.name, self.sha1, self.size, self.rtype, self.rmeta, self.rid,
                self.guid, self.loff, self.lsize)


class Bundle:
    def __init__(self):
        self.ebx, self.res, self.chunks = [], [], []
        self.meta = b''
        self.endian = '<'

    def assets(self):
        return self.ebx + self.res + self.chunks


def is_bundle(m):
    if len(m) < 36:
        return False
    declared, = struct.unpack_from('>I', m, 0)
    m1, = struct.unpack_from('>I', m, 4)
    m2, = struct.unpack_from('<I', m, 4)
    return declared == len(m) - 4 and (m1 ^ BUNDLE_SALT == BUNDLE_MAGIC or m2 ^ BUNDLE_SALT == BUNDLE_MAGIC)


def read_bundle(m):
    if len(m) < 36:
        raise ValueError('Binary bundle header is truncated')
    declared, = struct.unpack_from('>I', m, 0)
    if declared != len(m) - 4:
        raise ValueError('Binary bundle size does not match its stream')
    magic, = struct.unpack_from('>I', m, 4)
    en = '>'
    if magic ^ BUNDLE_SALT != BUNDLE_MAGIC:
        magic, = struct.unpack_from('<I', m, 4)
        en = '<'
    if magic ^ BUNDLE_SALT != BUNDLE_MAGIC:
        raise ValueError('Binary bundle magic is unsupported')
    total, ne, nr, nc, strings, mo, ms = struct.unpack_from(en + '7I', m, 8)
    if ne + nr + nc != total:
        raise ValueError('Binary bundle asset counts do not add up')
    strings += 4
    pos = 36
    hashes = [bytes(m[pos + 20 * i:pos + 20 * i + 20]) for i in range(total)]
    pos += 20 * total

    def s(off):
        e = m.index(b'\0', strings + off)
        return m[strings + off:e].decode('latin1')
    b = Bundle()
    b.endian = en
    for i in range(ne):
        a = Asset(EBX)
        no, a.size = struct.unpack_from(en + 'II', m, pos)
        pos += 8
        a.name = s(no)
        a.sha1 = hashes[i]
        b.ebx.append(a)
    for i in range(nr):
        a = Asset(RES)
        no, a.size = struct.unpack_from(en + 'II', m, pos)
        pos += 8
        a.name = s(no)
        a.sha1 = hashes[ne + i]
        b.res.append(a)
    for a in b.res:
        a.rtype, = struct.unpack_from(en + 'I', m, pos)
        pos += 4
    for a in b.res:
        a.rmeta = bytes(m[pos:pos + 16])
        pos += 16
    for a in b.res:
        a.rid, = struct.unpack_from(en + 'Q', m, pos)
        pos += 8
    for i in range(nc):
        a = Asset(CHUNK)
        a.guid = bytes(m[pos:pos + 16])       # little endian: as stored (= the TOC's chunk guid)
        if en == '>':
            a.guid = a.guid[::-1]
        a.loff, a.lsize = struct.unpack_from(en + 'II', m, pos + 16)
        pos += 24
        a.sha1 = hashes[ne + nr + i]
        a.size = (a.loff & 0xFFFF) | a.lsize
        b.chunks.append(a)
    if ms:
        b.meta = bytes(m[4 + mo:4 + mo + ms])
    return b


def write_bundle(b):
    """Mirrors fb::write_binary_bundle (little endian, as Studio and ReSkate write)."""
    en = '<'
    total = len(b.ebx) + len(b.res) + len(b.chunks)
    strings = bytearray()
    offsets = []
    for a in b.ebx + b.res:
        offsets.append(len(strings))
        strings += a.name.encode('latin1') + b'\0'
    sha_start = 36
    ebx_start = sha_start + total * 20
    res_start = ebx_start + len(b.ebx) * 8
    type_start = res_start + len(b.res) * 8
    meta_start = type_start + len(b.res) * 4
    id_start = meta_start + len(b.res) * 16
    chunk_start = id_start + len(b.res) * 8
    string_start = chunk_start + len(b.chunks) * 24
    cmeta_start = string_start + len(strings)
    length = cmeta_start + len(b.meta)
    out = bytearray()
    out += struct.pack('>I', length - 4)
    out += struct.pack(en + 'I', BUNDLE_MAGIC ^ BUNDLE_SALT)
    out += struct.pack(en + '7I', total, len(b.ebx), len(b.res), len(b.chunks), string_start - 4,
                       (cmeta_start - 4) if b.meta else 0, len(b.meta))
    for a in b.ebx + b.res + b.chunks:
        out += a.sha1
    for i, a in enumerate(b.ebx + b.res):
        out += struct.pack(en + 'II', offsets[i], a.size)
    for a in b.res:
        out += struct.pack(en + 'I', a.rtype)
    for a in b.res:
        if len(a.rmeta) != 16:
            raise ValueError('Binary bundle resource metadata must be 16 bytes')
        out += a.rmeta
    for a in b.res:
        out += struct.pack(en + 'Q', a.rid)
    for a in b.chunks:
        out += a.guid + struct.pack(en + 'II', a.loff, a.lsize)
    out += strings
    out += b.meta
    assert len(out) == length
    return bytes(out)


# --------------------------------------------------------------------------------------------
# Native DB (layout.toc, initfs)
# --------------------------------------------------------------------------------------------

class Node:
    __slots__ = ('type', 'named', 'terminated', 'name', 'payload', 'children')

    def __init__(self, type=0, named=False, name='', payload=b''):
        self.type, self.named, self.name, self.payload = type, named, name, payload
        self.terminated = False
        self.children = []

    def field(self, key):
        for c in self.children:
            if c.named and c.name == key:
                return c
        return None

    @property
    def text(self):
        t = self.payload
        if t.endswith(b'\0'):
            t = t[:-1]
        return t.decode('latin1')


def _db_var(d, pos):
    value = 0
    shift = 0
    while True:
        byte = d[pos]
        pos += 1
        value |= (byte & 127) << shift
        if not byte & 128:
            return value, pos
        shift += 7


def _db_read(d, pos):
    tag = d[pos]
    pos += 1
    node = Node(tag & 31)
    if not node.type:
        return node, pos
    node.named = (tag & 128) == 0
    if node.named:
        end = d.index(b'\0', pos)
        node.name = d[pos:end].decode('latin1')
        pos = end + 1
    if node.type in (1, 2):
        size, pos = _db_var(d, pos)
        end = pos + size
        while pos < end:
            child, pos = _db_read(d, pos)
            if not child.type:
                node.terminated = True
                break
            node.children.append(child)
        if pos != end:
            raise ValueError('DB container length mismatch')
    elif node.type in (7, 19):
        size, pos = _db_var(d, pos)
        node.payload = bytes(d[pos:pos + size])
        pos += size
    else:
        size = {6: 1, 8: 4, 11: 4, 9: 8, 12: 8, 15: 16, 16: 20}.get(node.type)
        if size is None:
            raise ValueError('Unsupported DB value type %d' % node.type)
        node.payload = bytes(d[pos:pos + size])
        pos += size
    return node, pos


def _db_write_var(out, value):
    while True:
        byte = value & 127
        value >>= 7
        if not value:
            out.append(byte)
            return
        out.append(byte | 128)


def _db_emit(node, out):
    if not node.type:
        out.append(0)
        return
    out.append(node.type | (0 if node.named else 128))
    if node.named:
        out += node.name.encode('latin1') + b'\0'
    if node.type in (1, 2):
        body = bytearray()
        for c in node.children:
            _db_emit(c, body)
        if node.terminated:
            body.append(0)
        _db_write_var(out, len(body))
        out += body
        return
    if node.type in (7, 19):
        _db_write_var(out, len(node.payload))
    out += node.payload


def read_db_file(path):
    with open(path, 'rb') as f:
        data = f.read()
    if len(data) <= ENVELOPE or data[:4] != MAGIC:
        raise ValueError('Unrecognized native envelope in ' + path)
    root, pos = _db_read(data, ENVELOPE)
    return root, data[:ENVELOPE], data[pos:]


def write_db_file(root, envelope=None, trailer=b''):
    out = bytearray(envelope if envelope is not None else MAGIC + bytes(ENVELOPE - 4))
    _db_emit(root, out)
    out += trailer
    return bytes(out)


def db_string(text, name=None):
    n = Node(7, name is not None, name or '', text.encode('latin1') + b'\0')
    return n


def db_named_record(field, value):
    rec = Node(2)
    rec.terminated = True
    rec.children.append(db_string(value, field))
    return rec


# --------------------------------------------------------------------------------------------
# CAS block stream
# --------------------------------------------------------------------------------------------

_oodle = None


def _load_oodle(game_root):
    global _oodle
    if _oodle is not None:
        return _oodle
    names = sorted(glob.glob(os.path.join(game_root or '', 'oo2core_*_win64.dll')))
    if not names:
        raise RuntimeError("Oodle CAS data needs the game folder (oo2core_*_win64.dll not found in %r)" % game_root)
    _oodle = ctypes.CDLL(names[-1])
    _oodle.OodleLZ_Decompress.restype = ctypes.c_int64
    _oodle.OodleLZ_Decompress.argtypes = [ctypes.c_char_p, ctypes.c_int64, ctypes.c_char_p, ctypes.c_int64,
                                          ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_void_p,
                                          ctypes.c_int64, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                          ctypes.c_int64, ctypes.c_int]
    return _oodle


def decode_cas(enc, game_root=None):
    out = bytearray()
    pos = 0
    n = len(enc)
    while pos < n:
        if n - pos < 8:
            raise ValueError('Truncated CAS block header')
        decoded, = struct.unpack_from('>I', enc, pos)
        comp, = struct.unpack_from('<H', enc, pos + 4)
        size, = struct.unpack_from('>H', enc, pos + 6)
        flags = comp >> 8
        size += (flags & 0x0F) << 16
        dictionary = (decoded & 0xFF000000) != 0
        decoded &= 0x00FFFFFF
        comp &= 0x7F
        pos += 8
        block = enc[pos:pos + size]
        if len(block) != size:
            raise ValueError('Truncated CAS stream')
        pos += size
        if comp == 0x00:
            if size != decoded:
                raise ValueError('Raw CAS block size does not match its header')
            out += block
        elif comp == 0x02:
            got = zlib.decompress(bytes(block))
            if len(got) != decoded:
                raise ValueError('zlib CAS block failed to decode')
            out += got
        elif comp in (0x11, 0x15, 0x19):
            dll = _load_oodle(game_root)
            buf = ctypes.create_string_buffer(decoded)
            got = dll.OodleLZ_Decompress(bytes(block), size, buf, decoded, 1, 0, 0, None, 0, None, None, None, 0, 3)
            if got != decoded:
                raise ValueError('Oodle CAS block failed to decode')
            out += buf.raw
        elif comp == 0x0F and not dictionary:
            import zstandard
            out += zstandard.ZstdDecompressor().decompress(bytes(block), max_output_size=decoded)
        else:
            raise ValueError('Unsupported CAS compression type %d' % comp)
    return bytes(out)


def encode_cas_raw(data, block=0x10000):
    """fb::encode_cas with CasCompression::raw: blocks stored as they are."""
    out = bytearray()
    count = max(1, (len(data) + block - 1) // block)
    for i in range(count):
        part = data[i * block:(i + 1) * block]
        packed = ((len(part) & 0xFFFFFF) << 32) | (0x00 << 24) | (0x7 << 20) | len(part)
        out += struct.pack('>Q', packed) + part
    return bytes(out)


def sha1(data):
    return hashlib.sha1(data).digest()


# --------------------------------------------------------------------------------------------
# A mod / Patch folder on disk
# --------------------------------------------------------------------------------------------

class Folder:
    """One mod folder: its TOCs and its cas archives, addressed the way its TOCs address them."""

    def __init__(self, root):
        self.root = root
        self.name = os.path.basename(os.path.normpath(root))
        self.tocs = []
        self.archives = {}        # (lower dir relative to Win32, number) -> path
        win = os.path.join(root, 'Win32')
        for base, _dirs, files in os.walk(win):
            for f in files:
                rel = os.path.relpath(os.path.join(base, f), root).replace('\\', '/')
                low = f.lower()
                if low.endswith('.toc'):
                    self.tocs.append(rel)
                elif low.endswith('.cas'):
                    stem = low[:-4]
                    digits = stem[len(stem.rstrip('0123456789')):]
                    if digits:
                        d = os.path.relpath(base, win).replace('\\', '/').lower()
                        self.archives[(d, int(digits))] = os.path.join(base, f)
        self.tocs.sort()
        self._handles = {}
        self.chunk_dirs = {}      # installChunk id -> lower dir relative to Win32
        self._layout = None

    def layout(self):
        if self._layout is None:
            self._layout = read_db_file(os.path.join(self.root, 'layout.toc'))
            root = self._layout[0]
            manifest = root.field('installManifest')
            chunks = manifest.field('installChunks') if manifest else None
            for entry in (chunks.children if chunks else []):
                name = entry.field('name')
                index = entry.field('persistentIndex')
                if name is None or index is None or name.type != 7 or index.type != 8:
                    continue
                self.chunk_dirs[struct.unpack('<I', index.payload)[0]] = name.text.lower()
        return self._layout

    def directory(self, chunk):
        self.layout()
        return self.chunk_dirs[chunk]

    def archive_path(self, loc):
        return self.archives.get((self.directory(loc[1]), loc[2]))

    def read(self, loc, offset, size):
        path = self.archive_path(loc)
        if path is None:
            raise KeyError('%s: no archive for %r' % (self.name, (loc,)))
        f = self._handles.get(path)
        if f is None:
            f = self._handles[path] = open(path, 'rb')
        f.seek(offset)
        data = f.read(size)
        if len(data) != size:
            raise IOError('%s: %d bytes at %d lie past the end of %s' % (self.name, size, offset, path))
        return data

    def close(self):
        for f in self._handles.values():
            f.close()
        self._handles = {}


def read_any(folder, base, loc, offset, size):
    """A file a TOC of `folder` references: in the folder's own archives when it is a patch
    placement, in the base game's (`base`, a Folder over <game>/Data) otherwise."""
    if loc.patch:
        return folder.read(loc, offset, size)
    if base is None:
        raise KeyError('base game data is needed for %r' % (loc,))
    return base.read(loc, offset, size)


def list_bundle(folder, region, game_root=None, base=None):
    """-> (files, Bundle, first, raw manifest) the way ReSkate's merge reads a bundle's asset list."""
    files, inline = read_region(region)
    if inline:
        return files, read_bundle(inline), 0, inline
    if not files:
        return files, None, 0, None
    expected = len(files) - 1
    for loc, off, size in files:
        if size > (32 << 20) or (not loc.patch and base is None):
            continue
        try:
            raw = read_any(folder, base, loc, off, size)
            try:
                b = read_bundle(raw)
            except ValueError:
                raw = decode_cas(raw, game_root)
                b = read_bundle(raw)
        except Exception:
            continue
        if len(b.ebx) + len(b.res) + len(b.chunks) == expected:
            return files, b, 1, raw
    return files, None, 0, None


def find_asset(folder, toc, bundle_name, kind, asset_name, game_root=None, base=None):
    """-> (Asset, (loc, offset, size)) of one named asset of one bundle, or None."""
    want = bundle_name.lower()
    for name, region in toc.bundles:
        if name.lower() != want:
            continue
        files, b, first, _raw = list_bundle(folder, region, game_root, base)
        if b is None:
            return None
        for a, f in zip(b.assets(), files[first:]):
            if a.kind == kind and a.name.lower() == asset_name.lower():
                return a, f
    return None


# --------------------------------------------------------------------------------------------
# Shader lookup tables of a level root (ReSkate Engine/Resource/shader_lookup.cpp)
# --------------------------------------------------------------------------------------------

PROGRAM_LOOKUP = 0xE2E6955B
TEXTURE_LOOKUP = 0x2D254A89


def _lookup_offset(resource, meta):
    if len(meta) != 16:
        raise ValueError('Program lookup metadata is not 16 bytes')
    program, relocation, lookup = struct.unpack_from('<III', meta, 0)
    offset = program + relocation
    if offset > len(resource) or lookup < 4 or lookup != len(resource) - offset:
        raise ValueError('Program lookup metadata does not match its payload')
    return offset


def _program_rows(resource, meta):
    offset = _lookup_offset(resource, meta)
    count, = struct.unpack_from('<I', resource, offset)
    offset += 4
    rows = {}
    for _ in range(count):
        key, values = struct.unpack_from('<QI', resource, offset)
        offset += 12
        if not values or values > 4096 or values * 4 > len(resource) - offset:
            raise ValueError('Program lookup rows are invalid')
        row = resource[offset:offset + 4 * values]
        offset += 4 * values
        rows.setdefault(key, row)       # std::map operator[] then push_back: a repeated key appends
    if offset != len(resource):
        raise ValueError('Program lookup has trailing data')
    return rows


def merge_program_lookup(base, edits):
    """base, edits: (resource bytes, 16-byte resource meta).  -> (resource, meta, added, conflicts)"""
    start = _lookup_offset(*base)
    base_rows = _program_rows(*base)
    rows = dict(base_rows)
    added = conflicts = 0
    for edit in edits:
        for key, values in sorted(_program_rows(*edit).items()):
            if key in base_rows:
                continue
            if key not in rows:
                rows[key] = values
                added += 1
            elif rows[key] != values:
                conflicts += 1
    out = bytearray(base[0][:start])
    out += struct.pack('<I', len(rows))
    for key in sorted(rows):
        out += struct.pack('<QI', key, len(rows[key]) // 4) + rows[key]
    meta = bytearray(base[1])
    struct.pack_into('<I', meta, 8, len(out) - start)
    return bytes(out), bytes(meta), added, conflicts


def _texture_rows(resource):
    if len(resource) % 16:
        raise ValueError('Texture lookup does not have its native layout')
    rows = {}
    for offset in range(0, len(resource), 16):
        key, value = struct.unpack_from('<QQ', resource, offset)
        rows.setdefault(key, value)
    return rows


def merge_texture_lookup(base, edits):
    base_rows = _texture_rows(base[0])
    rows = dict(base_rows)
    added = conflicts = 0
    for edit in edits:
        for key, value in sorted(_texture_rows(edit[0]).items()):
            if key in base_rows:
                continue
            if key not in rows:
                rows[key] = value
                added += 1
            elif rows[key] != value:
                conflicts += 1
    out = bytearray()
    for key in sorted(rows):
        out += struct.pack('<QQ', key, rows[key])
    return bytes(out), bytes(base[1]), added, conflicts
