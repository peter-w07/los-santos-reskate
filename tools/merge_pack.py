#!/usr/bin/env python3
"""Merge several ReSkate map mods (ReSkate Studio "Patch" folders) into ONE mod with many levels.

    python tools/merge_pack.py --name LosSantos build/mods/LosSantos_D5_LegionSquare build/mods/LosSantos_C5_Alta ...
    python tools/merge_pack.py --name LosSantos "build/mods/LosSantos_*"         # wildcards are expanded here
    python tools/merge_pack.py --name LosSantos --from-dir build/mods            # every mod folder in it
    python tools/merge_pack.py --name LosSantos2 build/pack/LosSantos build/mods/LosSantos_H4_New   # a pack is an input too
    python tools/merge_pack.py --verify build/pack/LosSantos "build/mods/LosSantos_*"

The first input has the highest priority, as the first mod in mods.json has; it only matters where two inputs
disagree, and the tool stops at the cases ReSkate would settle by dropping one input's data.

Output: build/pack/<name>/ = the mod folder (copy it to Mods/<name>), and beside it <name>.report.json and
<name>.trainer/ (per-level trainer.json folders, see the end of this text).

What it does, and why it is what ReSkate expects (ReSkate source, Engine/Vfs/mod_merge*.cpp):
  * A mod is a folder with layout.toc, superbundle TOCs under Win32/ and cas archives.  ReSkate merges every
    enabled mod into one patch at launch: superbundles with the same path are combined bundle by bundle, and
    inside a bundle asset by asset; each mod archive is given an index of its own and the references follow.
    A mod may ship several archives and several levels (mod_merge_grid.cpp: "A mod carrying several maps").
  * Every Studio map ships its own level superbundle (unique to it) plus edited copies of two game superbundles:
    Win32/globals.toc (level description list, loading-screen table, loading-screen widgets) and
    Win32/levels/game/dingolevel_root/dingolevel_root.toc (the root's sublevel manager and the two shader lookup
    tables).  Level superbundles are passed through; the shared ones are combined here exactly the way ReSkate
    combines them between mods: the EBX assets several maps edit are merged with a Python port of ReSkate's
    merge_documents / write_document (tools/fbebx.py, byte-identical to ReSkate's own output for the user's
    24 installed mods: build/research/pack/oracle.py), the shader tables with a port of shader_lookup.cpp.
  * Every referenced payload is copied into the pack's archives once: identical bytes (same texture, mesh, cell
    data in two sections) are stored a single time.  Archives are cut at --archive-mb (default 1024, the size of
    the game's own archives; the engine addresses 4 GiB) and numbered with indices the game does not declare.
  * layout.toc lists every level superbundle; reskate-levels.json lists every level (ReSkate reads at most 64
    per mod); the Skate.exe stamp (.reskate-studio-patch) is carried over and must be the same in every input.

The Trainer reads one trainer.json per folder under Mods (one level, or all levels of that mod), so per-level
spots cannot live inside one pack folder: <name>.trainer/.<name>_<level>/trainer.json carries a "level" key;
copy those dot-folders into Mods (ReSkate ignores folders starting with a dot, the Trainer reads them).
"""
import argparse
import collections
import ctypes
import datetime
import glob
import hashlib
import json
import os
import shutil
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fbpatch as fb       # noqa: E402
import fbebx               # noqa: E402

try:        # the folder with Skate.exe and Data: from config.json when there is one
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import cfg as _cfg
    DEFAULT_GAME = _cfg.SKATE
    DEFAULT_PACK_DIR = os.path.join(_cfg.WORK, 'pack')
except SystemExit:
    DEFAULT_GAME = None
    DEFAULT_PACK_DIR = None
PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAX_LEVELS = 64                      # custom_level_manifest_max_levels
ARCHIVE_HARD_LIMIT = 0xFFFFFFFF      # a placement holds a 32-bit offset
MATERIAL_GRID_TYPES = ('materialgrid',)
SMALL_FILES = ('initfs_win32', '.reskate-studio-patch')


def log(*parts):
    print(*parts, flush=True)


class MergeError(Exception):
    pass


# --------------------------------------------------------------------------------------------
# CAS encoding as ReSkate writes merged assets (fb::encode_cas: Oodle Kraken level 4, 64 KiB blocks)
# --------------------------------------------------------------------------------------------

def encode_cas(data, game_root, block=0x10000):
    try:
        dll = fb._load_oodle(game_root)
        dll.OodleLZ_Compress.restype = ctypes.c_int64
        dll.OodleLZ_Compress.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int64, ctypes.c_char_p, ctypes.c_int,
                                         ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int64]
        dll.OodleLZ_GetCompressedBufferSizeNeeded.restype = ctypes.c_int64
        dll.OodleLZ_GetCompressedBufferSizeNeeded.argtypes = [ctypes.c_int, ctypes.c_int64]
    except Exception:
        dll = None
    out = bytearray()
    count = max(1, (len(data) + block - 1) // block)
    for index in range(count):
        part = bytes(data[index * block:(index + 1) * block])
        payload, compression = part, 0x00
        if dll is not None and part:
            capacity = dll.OodleLZ_GetCompressedBufferSizeNeeded(8, len(part))
            buf = ctypes.create_string_buffer(int(capacity))
            length = dll.OodleLZ_Compress(8, part, len(part), buf, 4, None, None, None, None, 0)
            if 0 < length < len(part):
                payload, compression = buf.raw[:length], 0x11
        out += struct.pack('>I', len(part) & 0xFFFFFF)
        out += bytes([compression, 0x70 | ((len(payload) >> 16) & 0x0F)])
        out += struct.pack('>H', len(payload) & 0xFFFF)
        out += payload
    encoded = bytes(out)
    if fb.decode_cas(encoded, game_root) != bytes(data):
        raise MergeError('CAS encoding did not read back')
    return encoded


# --------------------------------------------------------------------------------------------
# The pack's archives
# --------------------------------------------------------------------------------------------

class Store:
    """Appends payloads to <out>/Win32/<package>/cas_NN.cas, each distinct payload once."""

    def __init__(self, out, declared, limit):
        self.out = out
        self.declared = declared            # package dir -> archive indices the game declares
        self.limit = limit
        self.current = {}                   # dir -> [archive, handle, size]
        self.used = collections.defaultdict(list)     # dir -> archives written
        self.by_hash = {}                   # (dir, sha1, size) -> (archive, offset)
        self.cache = {}                     # (source id, dir, archive, offset, size) -> (archive, offset)
        self.stored = 0
        self.saved = 0
        self.refs = 0

    def _next_index(self, directory):
        taken = set(self.used[directory])
        if 1 not in taken:
            return 1                         # mods are built against archive 1; ReSkate re-places it itself
        declared = self.declared.get(directory, set())
        for candidate in range(2, 0x10000):
            if candidate not in declared and candidate not in taken:
                return candidate
        raise MergeError('no free archive index in ' + directory)

    def _open(self, directory):
        index = self._next_index(directory)
        path = os.path.join(self.out, 'Win32', *directory.split('/'), 'cas_%02d.cas' % index)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        state = [index, open(path, 'wb'), 0]
        self.current[directory] = state
        self.used[directory].append(index)
        return state

    def put(self, directory, data):
        key = (directory, hashlib.sha1(data).digest(), len(data))
        found = self.by_hash.get(key)
        self.refs += 1
        if found is not None:
            self.saved += len(data)
            return found
        state = self.current.get(directory) or self._open(directory)
        pad = (-state[2]) % 4
        if state[2] + pad + len(data) > self.limit and state[2] > 0:
            state[1].close()
            state = self._open(directory)
            pad = 0
        if pad:
            state[1].write(bytes(pad))
            state[2] += pad
        if state[2] + len(data) > ARCHIVE_HARD_LIMIT:
            raise MergeError('a single payload does not fit a 4 GiB archive')
        at = (state[0], state[2])
        state[1].write(data)
        state[2] += len(data)
        self.stored += len(data)
        self.by_hash[key] = at
        return at

    def relocate(self, folder, loc, offset, size):
        """A patch placement of `folder` -> its placement in the pack."""
        directory = folder.directory(loc.chunk)
        key = (id(folder), directory, loc.archive, offset, size)
        found = self.cache.get(key)
        if found is None:
            found = self.cache[key] = self.put(directory, folder.read(loc, offset, size))
        else:
            self.refs += 1
        return fb.Loc(True, loc.chunk, found[0]), found[1], size

    def close(self):
        for state in self.current.values():
            state[1].close()


# --------------------------------------------------------------------------------------------
# Reading the inputs
# --------------------------------------------------------------------------------------------

def declared_archives(layout_root, chunk_dirs):
    declared = collections.defaultdict(set)
    for field in ('layeredInstallChunkFiles', 'unlayeredInstallChunkFiles'):
        node = layout_root.field(field)
        if node is None or node.type != 19:
            continue
        payload = node.payload
        for at in range(0, len(payload) - 7, 8):
            archive, chunk = struct.unpack_from('<HI', payload, at)
            if chunk in chunk_dirs:
                declared[chunk_dirs[chunk]].add(archive)
    return declared


def layout_changelist(root):
    def find(node):
        found = node.field('pipelineCodeChangelists')
        if found is not None:
            return found
        for child in node.children:
            got = find(child)
            if got is not None:
                return got
        return None
    field = find(root)
    if field is None:
        return ''
    return field.text if not field.children else ','.join(c.text for c in field.children)


def read_small(path):
    with open(path, 'rb') as f:
        return f.read()


class Source:
    def __init__(self, root):
        root = os.path.abspath(root)
        if os.path.isdir(os.path.join(root, 'Patch')) and not os.path.isfile(os.path.join(root, 'layout.toc')):
            root = os.path.join(root, 'Patch')            # a Studio staging folder
        for need in ('layout.toc', 'reskate-levels.json', '.reskate-studio-patch'):
            if not os.path.isfile(os.path.join(root, need)):
                raise MergeError('%s is not a Studio map mod: no %s' % (root, need))
        self.root = root
        self.folder = fb.Folder(root)
        self.name = self.folder.name if self.folder.name.lower() != 'patch' else os.path.basename(os.path.dirname(root))
        self.folder.name = self.name
        self.layout = self.folder.layout()[0]
        self.levels = json.loads(read_small(os.path.join(root, 'reskate-levels.json')).decode('utf-8'))['levels']
        self.stamp = read_small(os.path.join(root, '.reskate-studio-patch'))
        self.tocs = {rel.lower(): rel for rel in self.folder.tocs}
        self.trainer = None
        trainer = os.path.join(root, 'trainer.json')
        if os.path.isfile(trainer):
            self.trainer = json.loads(read_small(trainer).decode('utf-8'))
        self.size = sum(os.path.getsize(p) for p in self.folder.archives.values())


# --------------------------------------------------------------------------------------------
# The merge
# --------------------------------------------------------------------------------------------

class Entry:
    __slots__ = ('asset', 'file', 'folder', 'base', 'owner', 'meta')

    def __init__(self, asset, file, folder, base, owner, meta):
        self.asset, self.file, self.folder, self.base, self.owner, self.meta = asset, file, folder, base, owner, meta


class State:
    def __init__(self, name):
        self.name = name
        self.assets = []
        self.seen = {}
        self.history = collections.defaultdict(list)     # key -> [Entry] (ebx and resources)
        self.base_sha = {}
        self.manifest_chunk = None
        self.fallback_meta = b''
        self.providers = 0


def chunk_meta_entries(bundle):
    """The bundle's chunkMeta rows, one per chunk, or None when they cannot be told apart."""
    if not bundle.meta:
        return [None] * len(bundle.chunks) if not bundle.chunks else None
    try:
        node, used = fb._db_read(bundle.meta, 0)
    except Exception:
        return None
    if used != len(bundle.meta) or node.type != 1 or len(node.children) != len(bundle.chunks):
        return None
    return list(node.children)


class Merger:
    def __init__(self, sources, out, game_root, limit, name):
        self.sources = sources
        self.out = out
        self.game_root = game_root
        self.name = name
        self.base = None
        data = os.path.join(game_root, 'Data') if game_root else None
        if data and os.path.isfile(os.path.join(data, 'layout.toc')):
            self.base = fb.Folder(data)
            self.base.name = 'base game'
        first = sources[0]
        self.store = Store(out, declared_archives(first.layout, first.folder.chunk_dirs), limit)
        self.notes = []
        self.merged_assets = []        # (toc, bundle, asset, what)
        self.report = {}

    def note(self, text):
        self.notes.append(text)
        log('   note:', text)

    # ---- pass-through ----------------------------------------------------------------------
    def relocate_files(self, folder, files):
        out = []
        for loc, offset, size in files:
            if loc.patch:
                out.append(self.store.relocate(folder, loc, offset, size))
            else:
                out.append((loc, offset, size))
        return out

    def pass_region(self, folder, region):
        files, inline = fb.read_region(region)
        return fb.write_region(self.relocate_files(folder, files), inline)

    def pass_chunk(self, folder, chunk):
        if chunk.removed or not chunk.loc.patch:
            return chunk
        loc, offset, size = self.store.relocate(folder, chunk.loc, chunk.offset, chunk.size)
        return fb.TocChunk(chunk.guid, loc, offset, size)

    def pass_toc(self, source, rel):
        toc = fb.read_toc(os.path.join(source.root, rel))
        fb.verify_toc(toc)
        bundles = [(name, self.pass_region(source.folder, region)) for name, region in toc.bundles]
        chunks = [self.pass_chunk(source.folder, c) for c in toc.chunks]
        return fb.write_toc(bundles, chunks, toc.flags & ~4)

    # ---- combining one superbundle several inputs ship ---------------------------------------
    def decode(self, entry):
        loc, offset, size = entry.file
        return fb.decode_cas(fb.read_any(entry.folder, self.base, loc, offset, size), self.game_root)

    def combine(self, rel, providers):
        """providers: sources that ship this TOC, highest priority first.  Mirrors mod_merge_combine.cpp."""
        tocs = [(s, fb.read_toc(os.path.join(s.root, s.tocs[rel]))) for s in providers]
        for _s, toc in tocs:
            fb.verify_toc(toc)
        base_toc = None
        if self.base is not None:
            for candidate in self.base.tocs:
                if candidate.lower() == rel:
                    base_toc = fb.read_toc(os.path.join(self.base.root, candidate))
        counts = collections.Counter()
        for _s, toc in tocs:
            for name, _region in toc.bundles:
                counts[name.lower()] += 1
        shared = {name for name, n in counts.items() if n > 1}
        if shared and base_toc is None:
            raise MergeError('%s: %d bundle(s) are shipped by several inputs and the game has no such superbundle to '
                             'merge them over (is the same level in two inputs? is --game right?)' % (rel, len(shared)))

        order = []
        states = {}
        passed = {}

        def absorb(name, region, folder, is_base, owner):
            key = name.lower()
            files, bundle, first, raw = fb.list_bundle(folder, region, self.game_root, self.base)
            if bundle is None:
                raise MergeError('%s: the asset list of %s (%s) could not be read' % (rel, name, owner))
            if key not in states:
                order.append(key)
                states[key] = State(name)
            state = states[key]
            if files:
                state.manifest_chunk = files[0][0].chunk
            if not state.fallback_meta:
                state.fallback_meta = bundle.meta
            metas = chunk_meta_entries(bundle)
            first_chunk = len(bundle.ebx) + len(bundle.res)
            for position, (asset, file) in enumerate(zip(bundle.assets(), files[first:])):
                meta = False
                if asset.kind == fb.CHUNK:
                    meta = metas[position - first_chunk] if metas is not None else False
                entry = Entry(asset, file, folder, is_base, owner, meta)
                ident = asset.key()
                if asset.kind in (fb.EBX, fb.RES):
                    state.history[ident].append(entry)
                if is_base:
                    state.base_sha[ident] = asset.sha1
                at = state.seen.get(ident)
                if at is None:
                    state.seen[ident] = len(state.assets)
                    state.assets.append(entry)
                    continue
                if is_base:
                    continue
                previous = state.assets[at]
                shipped = state.base_sha.get(ident)
                if shipped is not None and asset.sha1 == shipped and previous.asset.sha1 != shipped:
                    continue               # an untouched copy never wins over a changed one
                if asset.kind == fb.CHUNK and not previous.base and previous.asset.sha1 != asset.sha1:
                    raise MergeError('%s: %s: chunk %s differs between %s and %s' % (rel, name, asset.guid.hex(),
                                                                                    previous.owner, owner))
                if asset.kind != fb.CHUNK and not previous.base and previous.owner != owner and \
                        previous.asset.sha1 != asset.sha1 and shipped is None:
                    raise MergeError('%s: %s: %s and %s both add a different asset called %s' % (
                        rel, name, previous.owner, owner, asset.name))
                state.assets[at] = entry

        if base_toc is not None:
            for name, region in base_toc.bundles:
                if name.lower() in shared:
                    absorb(name, region, self.base, True, 'base game')
        for source, toc in reversed(tocs):                 # lowest priority first
            for name, region in toc.bundles:
                key = name.lower()
                if key in shared:
                    if key not in states:
                        raise MergeError('%s: %s is shipped by several inputs but is not a bundle of the game' % (rel, name))
                    absorb(name, region, source.folder, False, source.name)
                    states[key].providers += 1
                else:
                    passed[key] = (name, self.pass_region(source.folder, region))
                    order.append(key)

        bundles = []
        for key in order:
            if key in passed:
                bundles.append(passed[key])
                continue
            state = states[key]
            directory = self.base.directory(state.manifest_chunk)
            self.combine_assets(rel, state, directory)
            manifest = fb.Bundle()
            files = []
            for kind, into in ((fb.EBX, manifest.ebx), (fb.RES, manifest.res), (fb.CHUNK, manifest.chunks)):
                for entry in state.assets:
                    if entry.asset.kind != kind:
                        continue
                    into.append(entry.asset)
                    loc, offset, size = entry.file
                    if loc.patch and entry.folder is not None:
                        files.append(self.store.relocate(entry.folder, loc, offset, size))
                    else:
                        files.append(entry.file)
            manifest.meta = self.chunk_meta(rel, state)
            raw = fb.write_bundle(manifest)
            archive, offset = self.store.put(directory, raw)
            files.insert(0, (fb.Loc(True, state.manifest_chunk, archive), offset, len(raw)))
            bundles.append((state.name, fb.write_region(files, b'')))
            log('   %s: %d inputs combined: %d ebx, %d resources, %d chunks' % (
                state.name, state.providers, len(manifest.ebx), len(manifest.res), len(manifest.chunks)))

        # TOC-level chunks: every input's own, the highest priority winning.
        base_chunks = {c.guid: c for c in base_toc.chunks} if base_toc is not None else {}
        chunk_at = {}
        chunks = []
        for source, toc in reversed(tocs):
            for chunk in toc.chunks:
                moved = self.pass_chunk(source.folder, chunk)
                at = chunk_at.get(chunk.guid)
                if at is None:
                    chunk_at[chunk.guid] = len(chunks)
                    chunks.append(moved)
                    continue
                shipped = base_chunks.get(chunk.guid)
                carried = shipped is not None and chunk.key() == shipped.key()
                changed = shipped is not None and chunks[at].key() != shipped.key()
                if carried and changed:
                    continue
                if chunks[at].key() != moved.key():
                    raise MergeError('%s: TOC chunk %s differs between inputs' % (rel, chunk.guid.hex()))
                chunks[at] = moved
        flags = tocs[0][1].flags & ~4
        return fb.write_toc(bundles, chunks, flags)

    def chunk_meta(self, rel, state):
        chunks = [e for e in state.assets if e.asset.kind == fb.CHUNK]
        if not chunks:
            return state.fallback_meta
        if any(e.meta is False or e.meta is None for e in chunks):
            self.note('%s: %s: chunk metadata rows could not be matched to chunks; kept one input\'s table as ReSkate does'
                      % (rel, state.name))
            return state.fallback_meta
        node = fb.Node(1, True, 'chunkMeta')
        node.terminated = True
        if state.fallback_meta:
            try:
                template = fb._db_read(state.fallback_meta, 0)[0]
                node.named, node.name, node.terminated = template.named, template.name, template.terminated
            except Exception:
                pass
        node.children = [e.meta for e in chunks]
        out = bytearray()
        fb._db_emit(node, out)
        return bytes(out)

    def combine_assets(self, rel, state, directory):
        for entry in state.assets:
            asset = entry.asset
            table = asset.kind == fb.RES and asset.rtype in (fb.PROGRAM_LOOKUP, fb.TEXTURE_LOOKUP)
            if asset.kind != fb.EBX and not table:
                if asset.kind == fb.RES:
                    copies = state.history.get(asset.key(), [])
                    base = [c for c in copies if c.base]
                    edits = {c.asset.sha1 for c in copies if not c.base and base and c.asset.sha1 != base[0].asset.sha1}
                    if len(edits) > 1:
                        raise MergeError('%s: %s: the inputs change resource %s (type 0x%08X) in different ways; '
                                         'ReSkate would keep one copy only' % (rel, state.name, asset.name, asset.rtype))
                continue
            copies = state.history.get(asset.key(), [])
            base = next((c for c in copies if c.base), None)
            if base is None:
                continue
            edits = [c for c in copies if not c.base and c.asset.sha1 != base.asset.sha1]
            if len(edits) < 2:
                continue
            if all(e.asset.sha1 == edits[0].asset.sha1 for e in edits):
                continue                                   # the same change from each of them is one change
            base_bytes = self.decode(base)
            edit_bytes = [self.decode(e) for e in edits]
            new_meta = None
            if table:
                merge = fb.merge_program_lookup if asset.rtype == fb.PROGRAM_LOOKUP else fb.merge_texture_lookup
                rebuilt, new_meta, added, conflicts = merge((base_bytes, base.asset.rmeta),
                                                            [(b, e.asset.rmeta) for b, e in zip(edit_bytes, edits)])
                if not added:
                    continue
                what = '%d material row(s)' % added
                if conflicts:
                    what += ', %d disagreed' % conflicts
                    self.note('%s: %d shader table row(s) differ between inputs; the lowest-priority input\'s were kept, '
                              'as ReSkate does' % (asset.name, conflicts))
            else:
                base_doc = fbebx.read_document(base_bytes)
                root_type = base_doc.root_type.lower()
                if any(t in root_type for t in MATERIAL_GRID_TYPES):
                    raise MergeError('%s: the inputs edit the game\'s live material grid in place; not supported here' % asset.name)
                if asset.name.lower().startswith('items/') and 'collection' in asset.name.lower():
                    raise MergeError('%s: item lists are not merged by this tool' % asset.name)
                docs = [fbebx.read_document(b) for b in edit_bytes]
                combined, summary = fbebx.merge_documents(base_doc, docs)
                if not summary.instances and not summary.array_entries:
                    if not summary.values or not summary.exact():
                        raise MergeError('%s: the inputs change it in ways that cannot be combined (%r)' % (asset.name, summary))
                if summary.uncarried or summary.unread:
                    self.note('%s: some in-place changes were not carried (%r); ReSkate merges the same way' % (asset.name, summary))
                rebuilt = fbebx.write_document(combined)
                check = fbebx.read_document(rebuilt)
                if check.file_guid != base_doc.file_guid or len(check.instances) != len(combined.instances):
                    raise MergeError('%s: the merged document did not read back' % asset.name)
                what = '%d instance(s), %d list entries' % (summary.instances, summary.array_entries)
                if summary.values:
                    what += ', %d changed value(s)' % summary.values
                if summary.contested:
                    what += ', %d disagreed' % summary.contested
            encoded = encode_cas(rebuilt, self.game_root)
            archive, offset = self.store.put(directory, encoded)
            merged = fb.Asset(asset.kind)
            merged.name, merged.rtype, merged.rid = asset.name, asset.rtype, asset.rid
            merged.rmeta = new_meta if new_meta is not None else asset.rmeta
            merged.size = len(rebuilt)
            merged.sha1 = fb.sha1(encoded)                 # as Studio and the game hash a payload: as stored
            entry.asset = merged
            entry.file = (fb.Loc(True, state.manifest_chunk, archive), offset, len(encoded))
            entry.folder = None
            entry.owner = 'merged'
            self.merged_assets.append({'toc': rel, 'bundle': state.name, 'asset': asset.name,
                                       'kind': ('ebx', 'res')[asset.kind], 'edits': len(edits), 'what': what,
                                       'bytes': len(rebuilt), 'content_sha1': fb.sha1(rebuilt).hex()})
            log('   %s: combined %d edits (%s)' % (asset.name, len(edits), what))

    # ---- layout ----------------------------------------------------------------------------
    def build_layout(self):
        first = self.sources[0]
        root, envelope, trailer = fb.read_db_file(os.path.join(first.root, 'layout.toc'))
        rows = root.field('superBundles')
        if rows is None or rows.type != 1:
            raise MergeError('layout.toc has no superBundles list')
        known = {r.field('name').text.lower() for r in rows.children if r.field('name') is not None}
        chunks = root.field('installManifest').field('installChunks')
        for source in self.sources[1:]:
            other = source.layout
            for row in other.field('superBundles').children:
                name = row.field('name')
                if name is None or name.type != 7 or name.text.lower() in known:
                    continue
                known.add(name.text.lower())
                at = 0
                while at < len(rows.children):
                    existing = rows.children[at].field('name')
                    if existing is not None and existing.type == 7 and existing.text.lower() > name.text.lower():
                        break
                    at += 1
                rows.children.insert(at, fb.db_named_record('name', name.text))
            other_manifest = other.field('installManifest')
            for chunk in (other_manifest.field('installChunks').children if other_manifest else []):
                chunk_name = chunk.field('name')
                held = chunk.field('superbundles')
                if chunk_name is None or held is None:
                    continue
                target = next((c for c in chunks.children if c.field('name') is not None and
                               c.field('name').text == chunk_name.text), None)
                target_list = target.field('superbundles') if target is not None else None
                if target_list is None:
                    continue
                have = {e.text.lower() for e in target_list.children if e.type == 7}
                for entry in held.children:
                    if entry.type != 7 or entry.text.lower() in have:
                        continue
                    have.add(entry.text.lower())
                    at = 0
                    while at < len(target_list.children):
                        if target_list.children[at].type == 7 and target_list.children[at].text.lower() > entry.text.lower():
                            break
                        at += 1
                    target_list.children.insert(at, fb.db_string(entry.text))
        # Declare the archives the pack adds, as ReSkate's merge does for the archives it re-indexes.
        dir_chunks = collections.defaultdict(list)
        for chunk, directory in first.folder.chunk_dirs.items():
            dir_chunks[directory].append(chunk)
        added = 0
        for field in ('layeredInstallChunkFiles',):
            node = root.field(field)
            if node is None or node.type != 19:
                continue
            table = bytearray(node.payload)
            for directory, archives in self.store.used.items():
                for chunk in dir_chunks.get(directory, []):
                    ident = struct.pack('<I', chunk)
                    for archive in archives:
                        insert_at = 0
                        owns = already = placed = False
                        for at in range(0, len(table) - 7, 8):
                            if table[at + 2:at + 6] != ident:
                                continue
                            owns = True
                            current, = struct.unpack_from('<H', table, at)
                            if current == archive:
                                already = True
                            if not placed and current > archive:
                                insert_at = at
                                placed = True
                            if not placed:
                                insert_at = at + 8
                        if not owns or already:
                            continue
                        table[insert_at:insert_at] = struct.pack('<H', archive) + ident + b'\0\0'
                        added += 1
            node.payload = bytes(table)
        if added:
            log('   layout: declared %d pack archive(s)' % added)
        return fb.write_db_file(root, envelope, trailer)

    # ---- everything ------------------------------------------------------------------------
    def run(self):
        sources = self.sources
        stamp = sources[0].stamp
        build = layout_changelist(sources[0].layout)
        for s in sources:
            if s.stamp != stamp:
                raise MergeError('%s was built for another Skate.exe than %s (.reskate-studio-patch differs); rebuild it'
                                 % (s.name, sources[0].name))
            if layout_changelist(s.layout) != build:
                raise MergeError('%s was made for game build %s, %s for %s' % (s.name, layout_changelist(s.layout),
                                                                              sources[0].name, build))
        if self.base is not None:
            game_build = layout_changelist(self.base.layout()[0])
            if game_build != build:
                raise MergeError('the inputs were made for game build %s, the installed game is %s' % (build, game_build))
        for small in SMALL_FILES:
            data = read_small(os.path.join(sources[0].root, small))
            for s in sources[1:]:
                if read_small(os.path.join(s.root, small)) != data:
                    raise MergeError('%s differs between %s and %s' % (small, sources[0].name, s.name))

        levels = []
        seen = {}
        for s in sources:
            for level in s.levels:
                key = level['asset'].lower()
                if key in seen:
                    raise MergeError('level %s is in both %s and %s' % (level['asset'], seen[key], s.name))
                seen[key] = s.name
                levels.append(level)
        if len(levels) > MAX_LEVELS:
            raise MergeError('%d levels: ReSkate reads at most %d levels from one mod; make two packs' % (len(levels), MAX_LEVELS))

        providers = collections.OrderedDict()
        for s in sources:
            for key in s.tocs:
                providers.setdefault(key, []).append(s)

        os.makedirs(self.out, exist_ok=True)
        written = {}
        # Shared superbundles first, so the small launch data sits at the start of archive 1.
        for key in sorted(providers, key=lambda k: (len(providers[k]) < 2, k)):
            who = providers[key]
            rel = who[0].tocs[key]
            started = time.time()
            if len(who) == 1:
                data = self.pass_toc(who[0], rel)
                self.store.cache.clear()        # placements of this input's own ranges are not asked for again
                who[0].folder.close()
                log('%s: from %s (%.1f s)' % (rel, who[0].name, time.time() - started))
            else:
                log('%s: combining %d inputs' % (rel, len(who)))
                data = self.combine(key, who)
            check = fb.read_toc(data)
            fb.verify_toc(check)
            path = os.path.join(self.out, *rel.split('/'))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(data)
            written[rel] = len(data)
        self.store.close()

        with open(os.path.join(self.out, 'layout.toc'), 'wb') as f:
            f.write(self.build_layout())
        for small in SMALL_FILES:
            shutil.copyfile(os.path.join(sources[0].root, small), os.path.join(self.out, small))
        text = json.dumps({'schema': 1, 'levels': levels}, indent=2, ensure_ascii=True)
        if len(text) > 64 * 1024:
            raise MergeError('reskate-levels.json would exceed 64 KiB')
        with open(os.path.join(self.out, 'reskate-levels.json'), 'w', encoding='ascii', newline='\n') as f:
            f.write(text + '\n')
        build_info = {'schema': 1, 'tool': 'ReSkateStudio', 'version': '1.0.0',
                      'built': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
                      'packed_by': 'merge_pack.py', 'maps': [lv['asset'].split('/')[-1] for lv in levels]}
        src_build = os.path.join(sources[0].root, 'reskate-build.json')
        if os.path.isfile(src_build):
            try:
                info = json.loads(read_small(src_build).decode('utf-8'))
                build_info['tool'] = info.get('tool', build_info['tool'])
                build_info['version'] = info.get('version', build_info['version'])
            except Exception:
                pass
        with open(os.path.join(self.out, 'reskate-build.json'), 'w', encoding='ascii', newline='\n') as f:
            json.dump(build_info, f, indent=2)
            f.write('\n')
        author = 'unknown'
        src_manifest = os.path.join(sources[0].root, 'manifest.json')
        if os.path.isfile(src_manifest):
            try:
                author = json.loads(read_small(src_manifest).decode('utf-8')).get('author', author)
            except Exception:
                pass
        manifest = {'name': self.name, 'author': author, 'version_number': '1.0.0', 'dependencies': [],
                    'description': '%d levels in one mod: %s' % (len(levels), ', '.join(lv['displayName'] for lv in levels))[:1000]}
        with open(os.path.join(self.out, 'manifest.json'), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(manifest, f, indent=2)
            f.write('\n')

        # Per-level trainer files (see the module text).
        trainer_root = self.out.rstrip('\\/') + '.trainer'
        if os.path.isdir(trainer_root):
            shutil.rmtree(trainer_root)
        trainers = 0
        for s in sources:
            if s.trainer is None or len(s.levels) != 1:
                continue
            level = s.levels[0]['asset']
            folder = os.path.join(trainer_root, '.%s_%s' % (self.name, level.split('/')[-1].replace('dingolevel_reskate_', '')))
            os.makedirs(folder, exist_ok=True)
            data = dict(s.trainer)
            data['level'] = level.lower()
            with open(os.path.join(folder, 'trainer.json'), 'w', encoding='utf-8', newline='\n') as f:
                json.dump(data, f, indent=1)
            trainers += 1

        archives = {}
        for directory, used in self.store.used.items():
            for index in used:
                path = os.path.join(self.out, 'Win32', *directory.split('/'), 'cas_%02d.cas' % index)
                archives['%s/cas_%02d.cas' % (directory, index)] = os.path.getsize(path)
        in_bytes = sum(s.size for s in sources)
        out_bytes = sum(archives.values())
        self.report = {
            'name': self.name, 'out': self.out, 'inputs': [{'name': s.name, 'root': s.root, 'archive_bytes': s.size,
                                                            'levels': [lv['asset'] for lv in s.levels]} for s in sources],
            'levels': levels, 'archives': archives, 'tocs': written,
            'input_archive_bytes': in_bytes, 'pack_archive_bytes': out_bytes,
            'payload_references': self.store.refs, 'bytes_stored_once': self.store.stored,
            'bytes_not_stored_again': self.store.saved,
            'merged_assets': self.merged_assets, 'notes': self.notes, 'trainer_folders': trainers,
            'archive_limit': self.store.limit,
        }
        with open(self.out.rstrip('\\/') + '.report.json', 'w', encoding='utf-8') as f:
            json.dump(self.report, f, indent=1)
        log('\n%d levels from %d inputs -> %s' % (len(levels), len(sources), self.out))
        log('archives: inputs %.1f MB -> pack %.1f MB (%.1f %% smaller); %d archive file(s), largest %.1f MB' % (
            in_bytes / 2**20, out_bytes / 2**20, 100.0 * (in_bytes - out_bytes) / max(in_bytes, 1), len(archives),
            max(archives.values()) / 2**20))
        return self.report


# --------------------------------------------------------------------------------------------
# Verification without the game
# --------------------------------------------------------------------------------------------

def verify(pack_root, source_roots, game_root, quiet=False):
    """Re-read the pack the way ReSkate's merge reads a mod and compare every payload with the inputs."""
    problems = []
    stats = collections.Counter()

    def bad(text):
        problems.append(text)
        if len(problems) <= 40:
            log('   PROBLEM:', text)

    pack = fb.Folder(pack_root)
    base = None
    if game_root and os.path.isfile(os.path.join(game_root, 'Data', 'layout.toc')):
        base = fb.Folder(os.path.join(game_root, 'Data'))
    report_path = pack_root.rstrip('\\/') + '.report.json'
    merged_names = set()
    if os.path.isfile(report_path):
        with open(report_path, encoding='utf-8') as f:
            merged_names = {(m['toc'].lower(), m['bundle'].lower(), m['asset'].lower()) for m in json.load(f)['merged_assets']}

    # 1. the pack on its own: TOCs resolve, every bundle lists, every placement lies inside an archive of the pack
    sizes = {key: os.path.getsize(path) for key, path in pack.archives.items()}
    for key, size in sizes.items():
        if size > ARCHIVE_HARD_LIMIT:
            bad('archive %s/cas_%02d.cas is %d bytes: over 4 GiB' % (key[0], key[1], size))
    referenced = collections.defaultdict(list)
    pack_tocs = {}
    for rel in pack.tocs:
        toc = fb.read_toc(os.path.join(pack_root, rel))
        try:
            fb.verify_toc(toc)
        except Exception as error:
            bad('%s: %s' % (rel, error))
        if len({n.lower() for n, _ in toc.bundles}) != len(toc.bundles):
            bad('%s: duplicate bundle names' % rel)
        bundles = {}
        for name, region in toc.bundles:
            files, bundle, first, raw = fb.list_bundle(pack, region, game_root, base)
            if bundle is None:
                bad('%s: %s: asset list not readable' % (rel, name))
                continue
            if first != 1 or len(bundle.assets()) != len(files) - 1:
                bad('%s: %s: manifest lists %d assets, region holds %d files' % (rel, name, len(bundle.assets()), len(files)))
            for loc, offset, size in files:
                if not loc.patch:
                    continue
                key = (pack.directory(loc.chunk), loc.archive)
                if key not in sizes:
                    bad('%s: %s: placement in %s/cas_%02d.cas, which the pack does not ship' % (rel, name, key[0], key[1]))
                elif offset + size > sizes[key]:
                    bad('%s: %s: %d bytes at %d lie past the end of %s/cas_%02d.cas' % (rel, name, size, offset, key[0], key[1]))
                else:
                    referenced[key].append((offset, size))
            keys = [a.key() for a in bundle.assets()]
            if len(set(keys)) != len(keys):
                bad('%s: %s: an asset is listed twice' % (rel, name))
            bundles[name.lower()] = (files, bundle, first, raw)
            stats['bundles'] += 1
            stats['assets'] += len(keys)
        for chunk in toc.chunks:
            if chunk.removed or not chunk.loc.patch:
                continue
            key = (pack.directory(chunk.loc.chunk), chunk.loc.archive)
            if key not in sizes or chunk.offset + chunk.size > sizes[key]:
                bad('%s: TOC chunk %s lies outside the pack archives' % (rel, chunk.guid.hex()))
            else:
                referenced[key].append((chunk.offset, chunk.size))
        pack_tocs[rel.lower()] = (toc, bundles)
        stats['tocs'] += 1
    # every byte of every archive is referenced (nothing stray, nothing lost)
    for key, size in sizes.items():
        covered = 0
        end = 0
        for offset, length in sorted(set(referenced.get(key, []))):
            if offset + length > end:
                covered += offset + length - max(offset, end)
                end = offset + length
        stats['archive_bytes'] += size
        stats['referenced_bytes'] += covered
        if size - covered > 4 * (len(set(referenced.get(key, []))) + 1):
            bad('%s/cas_%02d.cas: %d of %d bytes are referenced by no TOC' % (key[0], key[1], size - covered, size))

    # 2. layout and level list
    root = pack.layout()[0]
    names = [r.field('name').text for r in root.field('superBundles').children]
    if [n.lower() for n in names] != sorted(n.lower() for n in names):
        bad('layout.toc: superBundles are not in name order (the engine searches the list that way)')
    in_chunks = set()
    for chunk in root.field('installManifest').field('installChunks').children:
        held = chunk.field('superbundles')
        entries = [e.text.lower() for e in (held.children if held is not None else []) if e.type == 7]
        if entries != sorted(entries):
            bad('layout.toc: superbundles of %s are not in name order' % chunk.field('name').text)
        in_chunks.update(entries)
    with open(os.path.join(pack_root, 'reskate-levels.json'), 'rb') as f:
        raw_levels = f.read()
    if raw_levels.startswith(b'\xef\xbb\xbf') or len(raw_levels) > 64 * 1024:
        bad('reskate-levels.json has a BOM or exceeds 64 KiB')
    levels = json.loads(raw_levels.decode('utf-8'))
    if set(levels) != {'schema', 'levels'} or levels['schema'] != 1 or len(levels['levels']) > MAX_LEVELS:
        bad('reskate-levels.json is not a schema 1 manifest of at most %d levels' % MAX_LEVELS)
    for level in levels['levels']:
        if set(level) != {'asset', 'displayName', 'startPoints'}:
            bad('reskate-levels.json: unexpected fields in %r' % level)
        superbundle = 'win32/' + level['asset'].lower()
        if superbundle not in {n.lower() for n in names}:
            bad('layout.toc does not list %s' % superbundle)
        if superbundle not in in_chunks:
            bad('layout.toc: no install chunk holds %s' % superbundle)
        if superbundle + '.toc' not in pack_tocs:
            bad('the pack has no %s.toc' % superbundle)
        elif superbundle not in pack_tocs[superbundle + '.toc'][1]:
            bad('%s.toc has no level bundle %s' % (superbundle, superbundle))
    for rel in pack_tocs:
        if rel[:-4] not in {n.lower() for n in names}:
            bad('layout.toc does not list the superbundle of %s' % rel)
    stats['levels'] = len(levels['levels'])

    # 3. every input, asset by asset
    def same_bytes(folder, file, pfile, what):
        loc, offset, size = file
        ploc, poffset, psize = pfile
        if not loc.patch:
            if tuple(ploc) != tuple(loc) or (poffset, psize) != (offset, size):
                bad('%s: a base-game placement changed' % what)
            stats['base_refs'] += 1
            return
        if not ploc.patch or psize != size or ploc.chunk != loc.chunk:
            bad('%s: placement kind/size/package changed' % what)
            return
        if folder.read(loc, offset, size) != pack.read(ploc, poffset, psize):
            bad('%s: bytes differ from the input' % what)
        stats['payloads_compared'] += 1
        stats['bytes_compared'] += size

    for source_root in source_roots:
        source = Source(source_root)
        for key, rel in source.tocs.items():
            if key not in pack_tocs:
                bad('%s: %s is not in the pack' % (source.name, rel))
                continue
            ptoc, pbundles = pack_tocs[key]
            toc = fb.read_toc(os.path.join(source.root, rel))
            base_bundles = {}
            if base is not None:
                for candidate in base.tocs:
                    if candidate.lower() == key:
                        base_bundles = {n.lower(): r for n, r in fb.read_toc(os.path.join(base.root, candidate)).bundles}
            for name, region in toc.bundles:
                if name.lower() not in pbundles:
                    bad('%s: bundle %s is not in the pack' % (source.name, name))
                    continue
                files, bundle, first, raw = fb.list_bundle(source.folder, region, game_root, base)
                pfiles, pbundle, pfirst, praw = pbundles[name.lower()]
                index = {a.key(): (a, f) for a, f in zip(pbundle.assets(), pfiles[pfirst:])}
                base_sha = {}
                if name.lower() in base_bundles:
                    _bf, bbundle, _b1, _br = fb.list_bundle(base, base_bundles[name.lower()], game_root, base)
                    base_sha = {a.key(): a.sha1 for a in bbundle.assets()}
                whole = raw == praw
                if whole:
                    same_bytes(source.folder, files[0], pfiles[0], '%s: %s: manifest' % (source.name, name))
                    stats['bundles_identical'] += 1
                else:
                    stats['bundles_rebuilt'] += 1
                    # a rebuilt bundle keeps, for every chunk, the chunkMeta row its input had
                    rows, prows = chunk_meta_entries(bundle), chunk_meta_entries(pbundle)
                    if bundle.chunks and rows is not None:
                        if prows is None:
                            bad('%s: %s: the pack\'s chunk metadata does not have one row per chunk' % (source.name, name))
                        else:
                            def row_bytes(node):
                                out = bytearray()
                                if node is not None:
                                    fb._db_emit(node, out)
                                return bytes(out)
                            where = {a.guid: row_bytes(r) for a, r in zip(pbundle.chunks, prows)}
                            for a, r in zip(bundle.chunks, rows):
                                if where.get(a.guid) != row_bytes(r):
                                    bad('%s: %s: chunk metadata row of %s changed' % (source.name, name, a.guid.hex()))
                                stats['chunk_meta_rows'] += 1
                for asset, file in zip(bundle.assets(), files[first:]):
                    label = '%s: %s: %s' % (source.name, name, asset.name or asset.guid.hex())
                    found = index.get(asset.key())
                    if found is None:
                        bad(label + ': not in the pack')
                        continue
                    passet, pfile = found
                    if passet.ident() == asset.ident():
                        same_bytes(source.folder, file, pfile, label)
                        continue
                    if (key, name.lower(), asset.name.lower()) in merged_names:
                        stats['assets_merged_seen'] += 1
                        continue
                    if base_sha.get(asset.key()) == asset.sha1:
                        stats['unchanged_copy_replaced'] += 1     # another input changed it; that one is checked there
                        continue
                    bad(label + ': the pack holds a different asset under this name')
            pchunks = {c.guid: c for c in ptoc.chunks}
            for chunk in toc.chunks:
                found = pchunks.get(chunk.guid)
                if found is None or found.removed != chunk.removed:
                    bad('%s: %s: TOC chunk %s is not in the pack' % (source.name, rel, chunk.guid.hex()))
                elif not chunk.removed:
                    same_bytes(source.folder, (chunk.loc, chunk.offset, chunk.size), (found.loc, found.offset, found.size),
                               '%s: %s: TOC chunk %s' % (source.name, rel, chunk.guid.hex()))
            stats['source_tocs'] += 1
        source.folder.close()
        if not quiet:
            log('   %s: compared' % source.name)

    # 4. the merged assets: each one decodes, and holds what every input added
    if merged_names and base is not None:
        for key, bundle_name, asset_name in sorted(merged_names):
            ptoc, pbundles = pack_tocs[key]
            pfiles, pbundle, pfirst, _ = pbundles[bundle_name]
            for asset, file in zip(pbundle.assets(), pfiles[pfirst:]):
                if asset.kind == fb.CHUNK or asset.name.lower() != asset_name:
                    continue
                encoded = pack.read(*file)
                data = fb.decode_cas(encoded, game_root)
                if len(data) != asset.size or fb.sha1(encoded) != asset.sha1:
                    bad('%s: merged asset size/sha1 does not match its manifest row' % asset_name)
                if asset.kind == fb.EBX:
                    doc = fbebx.read_document(data)
                    if fbebx.write_document(doc) != data:
                        bad('%s: merged document does not round-trip' % asset_name)
                    guids = [i.guid for i in doc.instances[1:] if i.exported]
                    if guids != sorted(guids):
                        bad('%s: exported instances are not in guid order' % asset_name)
                stats['merged_assets_checked'] += 1
    pack.close()
    if base is not None:
        base.close()
    log('verify: %d TOCs, %d bundles, %d asset rows; %d input payloads (%.1f MB) byte-identical in the pack, %d base-game '
        'references unchanged, %d bundles identical / %d rebuilt, %d merged assets checked; archives %.1f MB, %.1f MB referenced'
        % (stats['tocs'], stats['bundles'], stats['assets'], stats['payloads_compared'], stats['bytes_compared'] / 2**20,
           stats['base_refs'], stats['bundles_identical'], stats['bundles_rebuilt'], stats['merged_assets_checked'],
           stats['archive_bytes'] / 2**20, stats['referenced_bytes'] / 2**20))
    log('verify: %s' % ('OK, no problems' if not problems else '%d PROBLEM(S)' % len(problems)))
    return problems, dict(stats)


# --------------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description='Merge ReSkate Studio map mods into one mod with many levels.')
    ap.add_argument('sources', nargs='*', help='mod folders or Studio staging folders (with or without \\Patch)')
    ap.add_argument('--name', help='name of the pack = name of the mod folder')
    ap.add_argument('--from-dir', help='take every sub-folder of this directory that holds a layout.toc')
    ap.add_argument('--out', help='output mod folder (default: build/pack/<name>)')
    ap.add_argument('--game', default=DEFAULT_GAME, help='game folder (Skate.exe, Data, oo2core dll); read only')
    ap.add_argument('--archive-mb', type=int, default=1024, help='largest pack archive in MB (default 1024, max 4095)')
    ap.add_argument('--verify', metavar='PACK', help='only verify PACK against the given inputs')
    ap.add_argument('--no-verify', action='store_true', help='skip the verification pass after merging')
    ap.add_argument('--force', action='store_true', help='replace an existing output folder')
    args = ap.parse_args()

    roots = []
    for given in args.sources:              # PowerShell does not expand wildcards for a program
        roots.extend(sorted(glob.glob(given)) if any(c in given for c in '*?[') else [given])
    if args.from_dir:
        for entry in sorted(os.listdir(args.from_dir)):
            path = os.path.join(args.from_dir, entry)
            if os.path.isfile(os.path.join(path, 'layout.toc')) or os.path.isfile(os.path.join(path, 'Patch', 'layout.toc')):
                roots.append(path)
    if not roots:
        ap.error('no inputs')
    try:
        if args.verify:
            problems, _ = verify(os.path.abspath(args.verify), roots, args.game)
            return 1 if problems else 0
        if not args.name:
            ap.error('--name is required')
        if args.name.startswith('.') or not all(c.isalnum() or c in '_-. ' for c in args.name):
            ap.error('the name must be a valid mod folder name (letters, digits, _ - . space; no leading dot)')
        if not 1 <= args.archive_mb <= 4095:
            ap.error('--archive-mb must be between 1 and 4095')
        out = os.path.abspath(args.out or os.path.join(DEFAULT_PACK_DIR or os.path.join(PROJECT, 'work', 'pack'), args.name))
        if os.path.exists(out):
            if not args.force:
                ap.error('%s exists; use --force to replace it' % out)
            if not os.path.isfile(os.path.join(out, 'layout.toc')) and os.listdir(out):
                ap.error('%s does not look like a pack; not removing it' % out)
            shutil.rmtree(out)
        started = time.time()
        sources = [Source(r) for r in roots]
        log('%d inputs, %.1f MB of archives' % (len(sources), sum(s.size for s in sources) / 2**20))
        merger = Merger(sources, out, args.game, args.archive_mb * 2**20, args.name)
        if merger.base is None:
            raise MergeError('game data not found under %s (needed to combine globals and the level root)' % args.game)
        merger.run()
        for s in sources:
            s.folder.close()
        merger.base.close()
        log('merged in %.0f s' % (time.time() - started))
        if not args.no_verify:
            problems, stats = verify(out, roots, args.game, quiet=True)
            with open(out.rstrip('\\/') + '.report.json', encoding='utf-8') as f:
                report = json.load(f)
            report['verify'] = {'problems': problems, 'stats': stats}
            with open(out.rstrip('\\/') + '.report.json', 'w', encoding='utf-8') as f:
                json.dump(report, f, indent=1)
            return 1 if problems else 0
        return 0
    except MergeError as error:
        log('ERROR:', error)
        return 2


if __name__ == '__main__':
    sys.exit(main())
