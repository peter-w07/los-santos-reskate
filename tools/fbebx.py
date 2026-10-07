"""EBX (RIFF) documents: read, combine edits, write.

A line-for-line Python port of ReSkate's Engine/Resource/ebx_document.cpp, ebx_merge.cpp and
ebx_writer.cpp (github.com/Dingo-Shenanigans/ReSkate), which is what ReSkate itself runs when two
mods edit one EBX asset.  merge_pack.py uses it to do that same merge ahead of time for the
assets every map mod edits (the level list, the loading-screen table, the root's sublevel
manager), so that one mod can carry many levels.  The port is checked against ReSkate's own
output in build/research/pack/oracle.py.

Values are two-item lists [tag, data] (lists, because a merge rewrites them in place):
  'n' nothing   'b' bool   'i' signed   'u' unsigned   'f' float (as a Python float)
  's' string    'g' guid (16 bytes)     'h' sha1       'r' resource id
  'p' pointer (kind, index): kind 0 null, 1 internal instance, 2 import
  't' type reference (primitive, primitiveType, descriptor, encoded)
  'x' boxed reference (encodedType, dataOffset, relativeOffset)
  'o' nested Object or None           'a' list of values
"""
import struct

T_INHERITED, T_DBOBJECT, T_STRUCT, T_POINTER, T_ARRAY, T_FIXEDARRAY, T_STRING, T_CSTRING, T_ENUM, \
    T_FILEREF, T_BOOL, T_INT8, T_UINT8, T_INT16, T_UINT16, T_INT32, T_UINT32, T_UINT64, T_INT64, \
    T_FLOAT32, T_FLOAT64, T_GUID, T_SHA1, T_RESOURCEREF, T_FUNCTION, T_TYPEREF, T_BOXEDVALUEREF, \
    T_INTERFACE, T_DELEGATE = range(0x1D)
CAT_ARRAY = 4
P_NULL, P_INTERNAL, P_EXTERNAL = 0, 1, 2
UNMATCHED = -1
OBJECT_ID_MASK = 0x01FFFFFF


class Type:
    __slots__ = ('name', 'name_hash', 'field_index', 'field_count', 'encoded', 'size', 'alignment', 'guid', 'signature')

    def __init__(self):
        self.name = ''
        self.guid = bytes(16)
        self.signature = 0


class Field:
    __slots__ = ('name', 'name_hash', 'encoded', 'class_ref', 'data_offset')

    @property
    def type(self):
        return (self.encoded >> 4) & 0x1F

    @property
    def category(self):
        return self.encoded & 0x0F


class Object:
    __slots__ = ('descriptor', 'fields')

    def __init__(self, descriptor=-1):
        self.descriptor = descriptor
        self.fields = []          # [descriptor index, name, value]


class Instance:
    __slots__ = ('fixup_type', 'descriptor', 'data_offset', 'exported', 'guid', 'object', 'raw')

    def __init__(self, fixup_type=0, descriptor=-1, data_offset=0, exported=False):
        self.fixup_type, self.descriptor, self.data_offset, self.exported = fixup_type, descriptor, data_offset, exported
        self.guid = bytes(16)
        self.object = None
        self.raw = b''


class Document:
    def __init__(self):
        self.file_guid = bytes(16)
        self.root_type = ''
        self.data_start = self.data_end = 0
        self.arrays_offset = self.boxed_offset = self.strings_offset = 0
        self.serialized = False
        self.hashed_names = False
        self.reflection = b''
        self.fixup_guids = []
        self.fixup_signatures = []
        self.boxed_ref_offsets = []
        self.types = []
        self.fields = []
        self.instances = []
        self.imports = []          # (file guid, class guid)
        self.arrays = []           # (offset, count, hash, encodedType, classRef)
        self.boxed = []            # [offset, count, hash, encodedType, classRef, raw bytes]
        self.pointer_offsets = []
        self.resource_offsets = []
        self.import_offsets = []
        self.typeinfo_offsets = []
        self.boxed_slots = []


def _align(v, a):
    return (v + a - 1) & ~(a - 1)


# --------------------------------------------------------------------------------------------
# reader
# --------------------------------------------------------------------------------------------

def _chunks(b):
    if b[:4] != b'RIFF':
        raise ValueError('EBX is not a RIFF document')
    end = struct.unpack_from('<I', b, 4)[0] + 8
    if end != len(b):
        raise ValueError('RIFF EBX size does not match its stream')
    form = b[8:12]
    if form not in (b'EBX\0', b'EBXS'):
        raise ValueError('RIFF document is not EBX or EBXS')
    out = {}
    pos = 12
    while pos < end:
        cid = b[pos:pos + 4]
        size, = struct.unpack_from('<I', b, pos + 4)
        begin = pos + 8
        if cid in out:
            raise ValueError('duplicate RIFF chunk %r' % cid)
        out[cid] = (begin, begin + size)
        pos = begin + size + (size & 1)
    return out, form


def read_file_guid(b):
    chunks, _ = _chunks(b)
    begin = chunks[b'EFIX'][0]
    return bytes(b[begin:begin + 16])


def read_document(b):
    b = bytes(b)
    chunks, form = _chunks(b)
    for need in (b'EBXD', b'EFIX', b'EBXX'):
        if need not in chunks:
            raise ValueError('missing RIFF %r chunk' % need)
    if (b'REFL' in chunks) == (b'RFL2' in chunks):
        raise ValueError('RIFF EBX must contain exactly one REFL or RFL2 chunk')
    doc = Document()
    doc.serialized = form == b'EBXS'
    doc.data_start = _align(chunks[b'EBXD'][0], 16)
    doc.data_end = chunks[b'EBXD'][1]
    u32 = lambda p: struct.unpack_from('<I', b, p)[0]
    i32 = lambda p: struct.unpack_from('<i', b, p)[0]
    u16 = lambda p: struct.unpack_from('<H', b, p)[0]

    # EFIX
    pos, end = chunks[b'EFIX']
    doc.file_guid = b[pos:pos + 16]
    pos += 16
    n = i32(pos)
    pos += 4
    doc.fixup_guids = [b[pos + 16 * i:pos + 16 * i + 16] for i in range(n)]
    pos += 16 * n
    n = i32(pos)
    pos += 4
    doc.fixup_signatures = list(struct.unpack_from('<%dI' % n, b, pos))
    pos += 4 * n
    exported = i32(pos)
    count = i32(pos + 4)
    pos += 8
    for index in range(count):
        offset = u32(pos)
        pos += 4
        class_ref = u16(doc.data_start + offset)
        if class_ref >= len(doc.fixup_guids):
            raise ValueError('EFIX instance type is invalid')
        doc.instances.append(Instance(class_ref, -1, offset, index < exported))

    def offsets():
        nonlocal pos
        n = i32(pos)
        pos += 4
        out = list(struct.unpack_from('<%dI' % n, b, pos))
        pos += 4 * n
        return out
    doc.pointer_offsets = offsets()
    doc.resource_offsets = offsets()
    n = i32(pos)
    pos += 4
    for i in range(n):
        doc.imports.append((b[pos:pos + 16], b[pos + 16:pos + 32]))
        pos += 32
    doc.import_offsets = offsets()
    doc.typeinfo_offsets = offsets()
    doc.arrays_offset, doc.boxed_offset, doc.strings_offset = struct.unpack_from('<III', b, pos)
    pos += 12
    if pos < end:
        doc.boxed_ref_offsets = offsets()
    if pos != end:
        raise ValueError('unexpected bytes at the end of EFIX')

    # EBXX
    pos, end = chunks[b'EBXX']
    na, nb = i32(pos), i32(pos + 4)
    pos += 8
    for i in range(na):
        doc.arrays.append(struct.unpack_from('<IiIHH', b, pos))
        pos += 16
    for i in range(nb):
        doc.boxed.append(list(struct.unpack_from('<IiIHH', b, pos)) + [b''])
        pos += 16
    if pos != end:
        raise ValueError('unexpected bytes at the end of EBXX')

    # reflection
    hashed = b'RFL2' in chunks
    begin, end = chunks[b'RFL2' if hashed else b'REFL']
    doc.hashed_names = hashed
    doc.reflection = b[begin:end]
    pos = begin
    n = i32(pos)
    pos += 4
    ids = []
    for i in range(n):
        ids.append((b[pos:pos + 16], u32(pos + 16)))
        pos += 20
    n = i32(pos)
    pos += 4
    for i in range(n):
        t = Type()
        t.name_hash, t.field_index, t.field_count, enc, t.size, t.alignment = struct.unpack_from('<IiHHHH', b, pos)
        t.encoded = enc >> 1
        pos += 16
        if i < len(ids):
            t.guid, t.signature = ids[i]
        doc.types.append(t)
    n = i32(pos)
    pos += 4
    for i in range(n):
        f = Field()
        f.name_hash, f.data_offset, enc, f.class_ref = struct.unpack_from('<IIHH', b, pos)
        f.encoded = enc >> 1
        pos += 12
        doc.fields.append(f)
    n = i32(pos)
    pos += 4 + 12 * n
    n = i32(pos)
    pos += 4 + 8 * n

    def cstr(at):
        e = b.index(b'\0', at, end)
        return b[at:e].decode('latin1')
    if hashed:
        n = i32(pos)
        pos += 4
        names = {}
        for i in range(n):
            h, o = struct.unpack_from('<II', b, pos)
            pos += 8
            names[h] = o
        strings = pos
        for t in doc.types:
            t.name = cstr(strings + names[t.name_hash])
        for f in doc.fields:
            f.name = cstr(strings + names[f.name_hash])
    else:
        strings = pos
        for t in doc.types:
            t.name = cstr(strings + t.name_hash)
        for f in doc.fields:
            f.name = cstr(strings + f.name_hash)

    # instances -> descriptors
    for inst in doc.instances:
        if inst.fixup_type >= len(doc.fixup_guids) or inst.fixup_type >= len(doc.fixup_signatures):
            continue
        want = (doc.fixup_guids[inst.fixup_type], doc.fixup_signatures[inst.fixup_type])
        for index, ident in enumerate(ids):
            if ident == want:
                inst.descriptor = index
                break
    if doc.instances:
        d = doc.instances[0].descriptor
        if 0 <= d < len(doc.types):
            doc.root_type = doc.types[d].name

    _ValueReader(b, doc).read_instances()

    doc.boxed.sort(key=lambda r: r[0])
    for index, rec in enumerate(doc.boxed):
        stop = doc.boxed[index + 1][0] if index + 1 < len(doc.boxed) else doc.strings_offset
        rec[5] = b[doc.data_start + rec[0]:doc.data_start + stop]
    return doc


class _ValueReader:
    def __init__(self, b, doc):
        self.b = b
        self.doc = doc
        self.by_offset = {}
        for index, inst in enumerate(doc.instances):
            self.by_offset.setdefault(inst.data_offset, index)

    def read_instances(self):
        doc = self.doc
        for inst in doc.instances:
            if not 0 <= inst.descriptor < len(doc.types):
                continue
            start = doc.data_start + inst.data_offset
            if inst.exported:
                inst.guid = self.b[start - 16:start]
            size = doc.types[inst.descriptor].size
            if start + size > doc.data_end:
                raise ValueError('EBX object exceeds EBXD')
            inst.raw = self.b[start:start + size]
            inst.object = self.read_object(inst.descriptor, start)

    def read_object(self, descriptor, start):
        if not 0 <= descriptor < len(self.doc.types):
            raise ValueError('EBX object type reference is invalid')
        obj = Object(descriptor)
        self.read_fields(obj, self.doc.types[descriptor], start)
        return obj

    def read_fields(self, out, t, start):
        doc = self.doc
        for i in range(t.field_count):
            di = t.field_index + i
            f = doc.fields[di]
            if f.type == T_INHERITED:
                self.read_fields(out, doc.types[f.class_ref], start)
                continue
            out.fields.append([di, f.name, self.read_field(f, start + f.data_offset)])

    def read_field(self, f, position):
        b = self.b
        doc = self.doc
        if f.category == CAT_ARRAY:
            disp, = struct.unpack_from('<i', b, position)
            target = position + disp
            if target < doc.data_start + 4 or target > doc.data_end:
                raise ValueError('EBX array displacement exceeds EBXD')
            count, = struct.unpack_from('<i', b, target - 4)
            if count < 0 or count > doc.data_end - target:
                raise ValueError('EBX array exceeds EBXD')
            values = []
            pos = target
            ftype = f.type
            for _ in range(count):
                value, after = self.read_value(ftype, f.class_ref, pos)
                values.append(value)
                if ftype == T_STRUCT:
                    pos += doc.types[f.class_ref].size
                elif ftype in (T_POINTER, T_CSTRING):
                    pos = _align(after, 8)
                else:
                    pos = after
            return ['a', values]
        return self.read_value(f.type, f.class_ref, position)[0]

    def read_value(self, t, class_ref, p):
        b = self.b
        doc = self.doc
        if t == T_BOOL:
            return ['b', b[p] != 0], p + 1
        if t == T_INT8:
            return ['i', struct.unpack_from('<b', b, p)[0]], p + 1
        if t == T_UINT8:
            return ['u', b[p]], p + 1
        if t == T_INT16:
            return ['i', struct.unpack_from('<h', b, p)[0]], p + 2
        if t == T_UINT16:
            return ['u', struct.unpack_from('<H', b, p)[0]], p + 2
        if t in (T_INT32, T_ENUM):
            return ['i', struct.unpack_from('<i', b, p)[0]], p + 4
        if t == T_UINT32:
            return ['u', struct.unpack_from('<I', b, p)[0]], p + 4
        if t == T_INT64:
            return ['i', struct.unpack_from('<q', b, p)[0]], p + 8
        if t == T_UINT64:
            return ['u', struct.unpack_from('<Q', b, p)[0]], p + 8
        if t == T_FLOAT32:
            return ['f', struct.unpack_from('<f', b, p)[0]], p + 4
        if t == T_FLOAT64:
            return ['f', struct.unpack_from('<d', b, p)[0]], p + 8
        if t == T_GUID:
            return ['g', b[p:p + 16]], p + 16
        if t == T_SHA1:
            return ['h', b[p:p + 20]], p + 20
        if t == T_RESOURCEREF:
            return ['r', struct.unpack_from('<Q', b, p)[0]], p + 8
        if t == T_STRING:
            raw = b[p:p + 32]
            return ['s', raw.split(b'\0', 1)[0].decode('latin1')], p + 32
        if t == T_CSTRING:
            return ['s', self.relative_string(p, struct.unpack_from('<I', b, p)[0])], p + 4
        if t == T_FILEREF:
            return ['s', self.relative_string(p, struct.unpack_from('<I', b, p)[0])], p + 8
        if t == T_POINTER:
            enc, = struct.unpack_from('<i', b, p)
            if enc == 0:
                return ['p', (P_NULL, -1)], p + 4
            if enc & 1:
                index = enc >> 1
                if not 0 <= index < len(doc.imports):
                    raise ValueError('EBX pointer import is invalid')
                return ['p', (P_EXTERNAL, index)], p + 4
            target = p + enc - doc.data_start
            if target not in self.by_offset:
                raise ValueError('EBX internal pointer target is invalid')
            return ['p', (P_INTERNAL, self.by_offset[target])], p + 4
        if t in (T_TYPEREF, T_DELEGATE):
            enc, = struct.unpack_from('<I', b, p)
            if enc == 0:
                return ['t', (False, 0, -1, 0)], p + 8
            if enc & 0x80000000:
                e = enc & 0x7FFFFFFF
                return ['t', (True, (e >> 5) & 0x1F, -1, enc)], p + 8
            return ['t', (False, 0, enc >> 2, enc)], p + 8
        if t == T_BOXEDVALUEREF:
            enc, = struct.unpack_from('<I', b, p)
            disp, = struct.unpack_from('<q', b, p + 8)
            target = p + 8 + disp - doc.data_start
            doc.boxed_slots.append(p - doc.data_start)
            return ['x', (enc, target, disp)], p + 16
        if t == T_STRUCT:
            return ['o', self.read_object(class_ref, p)], p
        return ['n', None], p

    def relative_string(self, base, disp):
        if disp == 0xFFFFFFFF:
            return ''
        target = base + disp
        if target < self.doc.data_start or target >= self.doc.data_end:
            raise ValueError('EBX string displacement exceeds EBXD')
        e = self.b.index(b'\0', target)
        return self.b[target:e].decode('latin1')


# --------------------------------------------------------------------------------------------
# writer
# --------------------------------------------------------------------------------------------

_SCALAR_SIZE = {T_BOOL: 1, T_INT8: 1, T_UINT8: 1, T_INT16: 2, T_UINT16: 2, T_INT32: 4, T_UINT32: 4, T_FLOAT32: 4,
                T_ENUM: 4, T_INT64: 8, T_UINT64: 8, T_FLOAT64: 8, T_RESOURCEREF: 8, T_POINTER: 8, T_CSTRING: 8,
                T_FILEREF: 8, T_TYPEREF: 8, T_DELEGATE: 8, T_GUID: 16, T_SHA1: 20, T_STRING: 32, T_BOXEDVALUEREF: 16}

_INT_FMT = {T_INT8: ('<b', -2**7, 2**7 - 1), T_UINT8: ('<B', 0, 2**8 - 1), T_INT16: ('<h', -2**15, 2**15 - 1),
            T_UINT16: ('<H', 0, 2**16 - 1), T_INT32: ('<i', -2**31, 2**31 - 1), T_ENUM: ('<i', -2**31, 2**31 - 1),
            T_UINT32: ('<I', 0, 2**32 - 1), T_INT64: ('<q', -2**63, 2**63 - 1), T_UINT64: ('<Q', 0, 2**64 - 1)}


def _align_w(v, a):
    if a <= 1:
        return v
    return (v + a - 1) // a * a


def ensure_fixup_type(doc, descriptor):
    t = doc.types[descriptor]
    count = min(len(doc.fixup_guids), len(doc.fixup_signatures))
    for index in range(count):
        if doc.fixup_guids[index] == t.guid and doc.fixup_signatures[index] == t.signature:
            return index
    doc.fixup_guids.insert(count, t.guid)
    doc.fixup_signatures.append(t.signature)
    return count


class _Builder:
    def __init__(self, doc):
        self.doc = doc
        self.payload = bytearray()
        self.instance_offsets = []
        self.pointer_offsets = []
        self.resource_offsets = []
        self.import_offsets = []
        self.typeinfo_offsets = []
        self.arrays_pending = []      # [slot, values, descriptor, emitted]
        self.strings_pending = []     # (slot, text)
        self.pointers_pending = []    # (slot, (kind, index))
        self.boxes_pending = []       # (slot, (encodedType, dataOffset, relativeOffset))
        self.arrays = []
        self.boxed = []
        self.arrays_offset = self.boxed_offset = self.empty_array_target = 0

    def run(self):
        self.validate()
        self.write_instances()
        self.write_arrays()
        self.write_boxed_values()
        string_offset = len(self.payload)
        self.write_strings()
        self.resolve_pointers()
        return self.write_riff(bytes(self.payload), self.write_fixup(string_offset), self.write_extra())

    def validate(self):
        doc = self.doc
        if not doc.reflection:
            raise ValueError('EBX has no donor reflection table')
        if len(doc.fixup_signatures) > len(doc.fixup_guids):
            raise ValueError('EBX fixup type table is invalid')
        internal = False
        for inst in doc.instances:
            internal |= not inst.exported
            if internal and inst.exported:
                raise ValueError('exported EBX instances must precede internal instances')
            if not 0 <= inst.descriptor < len(doc.types):
                raise ValueError('EBX instance has no valid type descriptor')
            if inst.fixup_type >= len(doc.fixup_guids):
                raise ValueError('EBX instance has no valid fixup type')

    def grow(self, end):
        if end > len(self.payload):
            self.payload.extend(bytes(end - len(self.payload)))

    def put(self, offset, data):
        self.grow(offset + len(data))
        self.payload[offset:offset + len(data)] = data

    def put_fmt(self, offset, fmt, value):
        self.put(offset, struct.pack(fmt, value))

    def write_instances(self):
        doc = self.doc
        cursor = 0
        for inst in doc.instances:
            t = doc.types[inst.descriptor]
            start = _align_w(cursor + (16 if inst.exported else 0), max(t.alignment, 1))
            if inst.exported:
                self.put(start - 16, inst.guid)
            self.grow(start + t.size)
            if inst.raw:
                self.put(start, inst.raw[:min(len(inst.raw), t.size)])
            self.put_fmt(start, '<H', inst.fixup_type)
            self.write_type(inst.descriptor, start, inst.object)
            self.instance_offsets.append(start)
            cursor = start + t.size

    @staticmethod
    def value_for(obj, descriptor):
        if obj is None:
            return None
        for f in obj.fields:
            if f[0] == descriptor:
                return f[2]
        return None

    def write_type(self, type_index, start, obj):
        doc = self.doc
        t = doc.types[type_index]
        self.grow(start + t.size)
        for ordinal in range(t.field_count):
            di = t.field_index + ordinal
            if di >= len(doc.fields):
                raise ValueError('EBX field range is invalid')
            f = doc.fields[di]
            if f.type == T_INHERITED:
                self.write_type(f.class_ref, start, obj)
                continue
            position = start + f.data_offset
            value = self.value_for(obj, di)
            if f.category == CAT_ARRAY:
                self.grow(position + 8)
                self.pointer_offsets.append(position)
                array = value[1] if value is not None and value[0] == 'a' else []
                self.arrays_pending.append([position, array, di, False])
            elif f.type == T_STRUCT:
                nested_start = _align_w(position, max(doc.types[f.class_ref].alignment, 1))
                nested = value[1] if value is not None and value[0] == 'o' else None
                self.write_type(f.class_ref, nested_start, nested)
            else:
                self.write_scalar(f, position, value)

    @staticmethod
    def number(value):
        """The numeric reading of a value, as ebx_writer's number<> takes it; None for a non-number."""
        if value is None:
            return None
        if value[0] in ('i', 'u', 'f', 'b'):
            return value[1]
        return None

    def write_scalar(self, f, position, value):
        t = f.type
        if t == T_BOOL:
            n = self.number(value)
            self.put(position, b'\x01' if n else b'\x00')
        elif t in _INT_FMT:
            fmt, low, high = _INT_FMT[t]
            n = self.number(value)
            if n is None:
                n = 0
            if value is not None and value[0] == 'f':
                n = int(n)
            elif value is not None and value[0] == 'b':
                n = int(n)
            elif not low <= n <= high:
                raise OverflowError('EBX integer field exceeds its native width')
            self.put_fmt(position, fmt, n)
        elif t == T_FLOAT32:
            n = self.number(value)
            n = float(n) if n is not None else 0.0
            if value is not None and value[0] == 'f' and (n != n or n in (float('inf'), float('-inf'))):
                raise OverflowError('EBX floating-point field must be finite')
            self.put_fmt(position, '<f', n)
        elif t == T_FLOAT64:
            n = self.number(value)
            n = float(n) if n is not None else 0.0
            if value is not None and value[0] == 'f' and (n != n or n in (float('inf'), float('-inf'))):
                raise OverflowError('EBX floating-point field must be finite')
            self.put_fmt(position, '<d', n)
        elif t == T_GUID:
            self.put(position, value[1] if value is not None and value[0] == 'g' else bytes(16))
        elif t == T_SHA1:
            self.put(position, value[1] if value is not None and value[0] == 'h' else bytes(20))
        elif t == T_STRING:
            text = value[1].encode('latin1') if value is not None and value[0] == 's' else b''
            if len(text) > 31:
                raise OverflowError('EBX fixed string exceeds 31 bytes')
            self.put(position, text + bytes(32 - len(text)))
        elif t in (T_CSTRING, T_FILEREF):
            self.put(position, bytes(8))
            self.pointer_offsets.append(position)
            self.strings_pending.append((position, value[1] if value is not None and value[0] == 's' else ''))
        elif t == T_RESOURCEREF:
            self.put_fmt(position, '<Q', value[1] if value is not None and value[0] == 'r' else 0)
            self.resource_offsets.append(position)
        elif t == T_POINTER:
            self.put(position, bytes(8))
            self.pointers_pending.append((position, value[1] if value is not None and value[0] == 'p' else (P_NULL, -1)))
        elif t in (T_TYPEREF, T_DELEGATE):
            self.grow(position + 8)
            encoded = value[1][3] if value is not None and value[0] == 't' else 0
            self.put_fmt(position, '<I', encoded)
            self.put_fmt(position + 4, '<I', 0)
            if encoded != 0 and t == T_TYPEREF:
                self.typeinfo_offsets.append(position)
        elif t == T_BOXEDVALUEREF:
            self.grow(position + 16)
            ref = value[1] if value is not None and value[0] == 'x' else None
            if ref is None or ref[0] == 0:
                self.put(position, bytes(16))
                return
            self.put_fmt(position, '<I', ref[0])
            self.put_fmt(position + 4, '<I', 0)
            self.typeinfo_offsets.append(position)
            self.pointer_offsets.append(position + 8)
            self.boxes_pending.append((position, ref))
        else:
            self.grow(position + _SCALAR_SIZE.get(t, 8))

    def element_stride(self, f):
        if f.type == T_STRUCT:
            return max(self.doc.types[f.class_ref].size, 1)
        return _SCALAR_SIZE.get(f.type, 8)

    def element_alignment(self, f):
        if f.type == T_STRUCT:
            return max(self.doc.types[f.class_ref].alignment, 1)
        return max(min(self.element_stride(f), 8), 1)

    def fixup_slot_for_descriptor(self, descriptor):
        doc = self.doc
        if descriptor >= len(doc.types):
            return 0xFFFF
        t = doc.types[descriptor]
        for index in range(min(len(doc.fixup_guids), len(doc.fixup_signatures))):
            if doc.fixup_guids[index] == t.guid and doc.fixup_signatures[index] == t.signature:
                return index
        return 0xFFFF

    def array_hash(self, f):
        raw_type = (f.encoded << 1) & 0xFFFF
        class_ref = self.fixup_slot_for_descriptor(f.class_ref) if f.type == T_STRUCT else 0xFFFF
        for rec in self.doc.arrays:
            if rec[3] == raw_type and rec[4] == class_ref:
                return rec[2]
        if self.doc.arrays:
            return self.doc.arrays[0][2]
        return f.name_hash

    def write_arrays(self):
        self.arrays_offset = _align_w(len(self.payload), 16)
        self.grow(self.arrays_offset + 32)
        self.empty_array_target = self.arrays_offset + 16
        index = 0
        while index < len(self.arrays_pending):
            if not self.arrays_pending[index][3]:
                self.emit_array(index)
            index += 1

    def emit_array(self, index):
        pending = self.arrays_pending[index]
        pending[3] = True
        slot, values, descriptor = pending[0], pending[1], pending[2]
        f = self.doc.fields[descriptor]
        if not values:
            self.put_fmt(slot, '<I', (self.empty_array_target - slot) & 0xFFFFFFFF)
            return
        data = _align_w(len(self.payload) + 4, self.element_alignment(f))
        stride = self.element_stride(f)
        self.put_fmt(data - 4, '<I', len(values))
        self.grow(data + stride * len(values))
        self.put_fmt(slot, '<I', (data - slot) & 0xFFFFFFFF)
        first_child = len(self.arrays_pending)
        for element, value in enumerate(values):
            start = data + element * stride
            if f.type == T_STRUCT:
                self.write_type(f.class_ref, start, value[1] if value[0] == 'o' else None)
            else:
                self.write_scalar(f, start, value)
        self.arrays.append((data, len(values), self.array_hash(f), (f.encoded << 1) & 0xFFFF,
                            self.fixup_slot_for_descriptor(f.class_ref) if f.type == T_STRUCT else 0xFFFF))
        child = first_child
        while child < len(self.arrays_pending):
            if not self.arrays_pending[child][3]:
                self.emit_array(child)
            child += 1

    def write_boxed_values(self):
        doc = self.doc
        self.boxed_offset = _align_w(len(self.payload), 16)
        self.grow(self.boxed_offset)
        moved = {}
        for rec in sorted(doc.boxed, key=lambda r: r[0]):
            if rec[0] < doc.boxed_offset:
                raise ValueError('EBX boxed record precedes its section')
            offset = self.boxed_offset + rec[0] - doc.boxed_offset
            self.put(offset, rec[5])
            for source, dest in ((doc.pointer_offsets, self.pointer_offsets), (doc.resource_offsets, self.resource_offsets),
                                 (doc.import_offsets, self.import_offsets), (doc.typeinfo_offsets, self.typeinfo_offsets)):
                for old in source:
                    if old < rec[0] or old - rec[0] >= len(rec[5]):
                        continue
                    dest.append(offset + old - rec[0])
            moved[rec[0]] = offset
            self.boxed.append((offset, rec[1], rec[2], rec[3], rec[4]))
        for slot, ref in self.boxes_pending:
            self.put_fmt(slot, '<I', ref[0])
            self.put_fmt(slot + 4, '<I', 0)
            found = moved.get(ref[1] & 0xFFFFFFFF)
            relative = ref[2] if found is None else found - (slot + 8)
            self.put_fmt(slot + 8, '<q', relative)

    def write_strings(self):
        offsets = {}
        for slot, text in self.strings_pending:
            at = offsets.get(text)
            if at is None:
                at = offsets[text] = len(self.payload)
                self.payload += text.encode('latin1') + b'\0'
            self.put_fmt(slot, '<I', (at - slot) & 0xFFFFFFFF)
            self.put_fmt(slot + 4, '<I', 0)

    def resolve_pointers(self):
        for slot, (kind, index) in self.pointers_pending:
            if kind == P_INTERNAL:
                if not 0 <= index < len(self.instance_offsets):
                    raise ValueError('EBX internal pointer index is invalid')
                self.put_fmt(slot, '<I', (self.instance_offsets[index] - slot) & 0xFFFFFFFF)
                self.pointer_offsets.append(slot)
            elif kind == P_EXTERNAL:
                if not 0 <= index < len(self.doc.imports):
                    raise ValueError('EBX import pointer index is invalid')
                self.put_fmt(slot, '<I', ((index << 1) | 1) & 0xFFFFFFFF)
                self.import_offsets.append(slot)
            self.put_fmt(slot + 4, '<I', 0)

    @staticmethod
    def table(values):
        values = sorted(set(values))
        return struct.pack('<I%dI' % len(values), len(values), *values)

    def write_fixup(self, string_offset):
        doc = self.doc
        out = bytearray()
        out += doc.file_guid
        out += struct.pack('<I', len(doc.fixup_guids))
        for g in doc.fixup_guids:
            out += g
        out += struct.pack('<I%dI' % len(doc.fixup_signatures), len(doc.fixup_signatures), *doc.fixup_signatures)
        out += struct.pack('<I', sum(1 for i in doc.instances if i.exported))
        out += self.table(self.instance_offsets)
        out += self.table(self.pointer_offsets)
        out += self.table(self.resource_offsets)
        out += struct.pack('<I', len(doc.imports))
        for fg, cg in doc.imports:
            out += fg + cg
        out += self.table(self.import_offsets)
        out += self.table(self.typeinfo_offsets)
        out += struct.pack('<III', self.arrays_offset, self.boxed_offset, string_offset)
        old_slots = sorted(doc.boxed_slots)
        new_slots = sorted(slot for slot, _ in self.boxes_pending)
        trailing = []
        for old in doc.boxed_ref_offsets:
            if old in old_slots:
                index = old_slots.index(old)
                if index < len(new_slots):
                    trailing.append(new_slots[index])
        out += self.table(trailing)
        return bytes(out)

    def write_extra(self):
        out = bytearray(struct.pack('<II', len(self.arrays), len(self.boxed)))
        for rec in sorted(self.arrays, key=lambda r: r[0]):
            out += struct.pack('<IIIHH', *rec)
        for rec in sorted(self.boxed, key=lambda r: r[0]):
            out += struct.pack('<IIIHH', *rec)
        return bytes(out)

    def write_riff(self, payload, fixup, extra):
        out = bytearray(b'RIFF\0\0\0\0')
        out += b'EBXS' if self.doc.serialized else b'EBX\0'

        def chunk(name, data):
            nonlocal out
            out += name + struct.pack('<I', len(data)) + data
            if len(data) & 1:
                out += b'\0'
        chunk(b'EBXD', bytes(12) + payload)
        chunk(b'EFIX', fixup)
        chunk(b'EBXX', extra)
        chunk(b'RFL2' if self.doc.hashed_names else b'REFL', self.doc.reflection)
        struct.pack_into('<I', out, 4, len(out) - 8)
        return bytes(out)


def write_document(doc):
    return _Builder(doc).run()


# --------------------------------------------------------------------------------------------
# merge
# --------------------------------------------------------------------------------------------

def clone_value(v):
    if v[0] == 'o':
        return ['o', clone_object(v[1]) if v[1] is not None else None]
    if v[0] == 'a':
        return ['a', [clone_value(e) for e in v[1]]]
    return [v[0], v[1]]


def clone_object(o):
    c = Object(o.descriptor)
    c.fields = [[f[0], f[1], clone_value(f[2])] for f in o.fields]
    return c


def clone_document(doc):
    c = Document()
    c.__dict__.update(doc.__dict__)
    for name in ('fixup_guids', 'fixup_signatures', 'boxed_ref_offsets', 'imports', 'arrays', 'pointer_offsets',
                 'resource_offsets', 'import_offsets', 'typeinfo_offsets', 'boxed_slots', 'types', 'fields'):
        setattr(c, name, list(getattr(doc, name)))
    c.boxed = [list(r) for r in doc.boxed]
    c.instances = []
    for inst in doc.instances:
        n = Instance(inst.fixup_type, inst.descriptor, inst.data_offset, inst.exported)
        n.guid, n.raw = inst.guid, inst.raw
        n.object = clone_object(inst.object) if inst.object is not None else None
        c.instances.append(n)
    return c


def _object_id(guid):
    return struct.unpack_from('<I', guid, 0)[0]


def _renumber(obj, guid, source):
    for f in obj.fields:
        if f[1] != 'Flags':
            continue
        v = f[2]
        if v[0] not in ('i', 'u'):
            return False
        current = v[1] & 0xFFFFFFFF
        renumbered = (current & ~OBJECT_ID_MASK & 0xFFFFFFFF) | (_object_id(guid) & OBJECT_ID_MASK)
        if renumbered == current:
            return False
        cloned = any(inst.exported and (_object_id(inst.guid) & OBJECT_ID_MASK) == (current & OBJECT_ID_MASK)
                     for inst in source.instances)
        if not cloned:
            return False
        v[1] = renumbered
        return True
    return False


def _type_or_none(doc, descriptor):
    return doc.types[descriptor].name if 0 <= descriptor < len(doc.types) else ''


def _match_instances(base, edit):
    match = [UNMATCHED] * len(base.instances)
    if not base.instances or not edit.instances:
        return match
    match[0] = 0
    exported = {}
    internal = []
    base_internal = []
    for index in range(1, len(edit.instances)):
        if edit.instances[index].exported:
            exported.setdefault(edit.instances[index].guid, index)
        else:
            internal.append(index)
    for index in range(1, len(base.instances)):
        if not base.instances[index].exported:
            base_internal.append(index)
            continue
        found = exported.get(base.instances[index].guid)
        if found is not None:
            match[index] = found
    if len(internal) == len(base_internal):
        for at in range(len(internal)):
            match[base_internal[at]] = internal[at]
    for index in range(len(match)):
        if match[index] != UNMATCHED and _type_or_none(base, base.instances[index].descriptor) != \
                _type_or_none(edit, edit.instances[match[index]].descriptor):
            match[index] = UNMATCHED
    return match


def _same(tag, a, b):
    if tag == 'f':
        return struct.pack('<d', a) == struct.pack('<d', b)
    return a == b


class _InPlace:
    def __init__(self, base, edit, match):
        self.base, self.edit, self.match = base, edit, match
        self.values = self.contested = 0
        self.uncarried = self.unread = False
        self.changes = []

    def fields(self, base, edit, result, root):
        if len(base.fields) != len(edit.fields) or len(base.fields) != len(result.fields):
            self.uncarried = True
            return
        for index in range(len(base.fields)):
            if base.fields[index][1] != edit.fields[index][1]:
                self.uncarried = True
                return
            self.value(base.fields[index][2], edit.fields[index][2], result.fields[index][2], root)

    def apply(self):
        for to, source in self.changes:
            to[0], to[1] = source[0], source[1]

    def value(self, base, edit, result, root_list):
        tag = base[0]
        if tag != edit[0] or tag != result[0]:
            self.uncarried = True
            return
        if tag == 'n':
            return
        if tag in 'biufsghr':
            if _same(tag, base[1], edit[1]):
                return
            self.values += 1
            if _same(tag, result[1], edit[1]):
                return
            if not _same(tag, result[1], base[1]):
                self.contested += 1
            self.changes.append((result, edit))
            return
        if tag == 'p':
            self.reference(base[1], edit[1])
            return
        if tag == 'o':
            if base[1] is None and edit[1] is None:
                return
            if base[1] is None or edit[1] is None or result[1] is None:
                self.uncarried = True
                return
            self.fields(base[1], edit[1], result[1], False)
            return
        if tag == 'a':
            before, after, mine = base[1], edit[1], result[1]
            if (len(after) < len(before)) if root_list else (len(after) != len(before)):
                self.uncarried = True
                return
            if len(mine) < len(before):
                self.uncarried = True
                return
            for index in range(len(before)):
                self.value(before[index], after[index], mine[index], False)
            return
        if tag == 't':
            b, a = base[1], edit[1]
            if b[0] != a[0]:
                self.uncarried = True
            elif b[0]:
                self.uncarried |= b[1] != a[1]
            else:
                self.uncarried |= _type_or_none(self.base, b[2]) != _type_or_none(self.edit, a[2])
            return
        if tag == 'x':
            b, a = base[1], edit[1]
            if (b[0] == 0) != (a[0] == 0):
                self.uncarried = True
            elif b[0] != 0:
                self.unread = True

    def reference(self, before, after):
        if before[0] != after[0]:
            self.uncarried = True
            return
        if before[0] == P_NULL:
            return
        if before[1] < 0 or after[1] < 0:
            self.uncarried |= before[1] != after[1]
            return
        frm, to = before[1], after[1]
        if before[0] == P_EXTERNAL:
            self.uncarried |= frm >= len(self.base.imports) or to >= len(self.edit.imports) or \
                self.base.imports[frm] != self.edit.imports[to]
            return
        if frm >= len(self.match):
            self.uncarried = True
            return
        if self.match[frm] == UNMATCHED:
            if self.base.instances[frm].exported:
                self.uncarried = True
            else:
                self.unread = True
        else:
            self.uncarried |= self.match[frm] != to


class _Carrier:
    def __init__(self, frm, to):
        self.frm, self.to = frm, to
        self.type_by_name = {}
        for index, t in enumerate(to.types):
            self.type_by_name.setdefault(t.name, index)
        self.instance_by_guid = {}
        for index, inst in enumerate(to.instances):
            self.instance_by_guid.setdefault(inst.guid, index)
        self.carried_internal = {}
        self.instances = 0
        self.renumbered = 0

    def type(self, descriptor):
        if not 0 <= descriptor < len(self.frm.types):
            raise ValueError('EBX type descriptor is out of range')
        name = self.frm.types[descriptor].name
        if name not in self.type_by_name:
            raise ValueError('EBX merge needs a type the base does not define: ' + name)
        return self.type_by_name[name]

    def instance(self, index):
        if index >= len(self.frm.instances):
            raise ValueError('EBX pointer is out of range')
        source = self.frm.instances[index]
        if source.exported:
            if source.guid in self.instance_by_guid:
                return self.instance_by_guid[source.guid]
        elif index in self.carried_internal:
            return self.carried_internal[index]
        if source.object is None:
            raise ValueError('EBX instance has no parsed object')
        descriptor = self.type(source.descriptor)
        carried = Instance(ensure_fixup_type(self.to, descriptor), descriptor, 0, source.exported)
        carried.guid = source.guid
        carried.object = Object(descriptor)
        self.to.instances.append(carried)
        at = len(self.to.instances) - 1
        if source.exported:
            self.instance_by_guid.setdefault(source.guid, at)
        else:
            self.carried_internal[index] = at
        carried.raw = source.raw
        copied = self.object(source.object)
        carried.object.fields = copied.fields
        if source.exported:
            self.renumbered += 1 if _renumber(carried.object, source.guid, self.frm) else 0
        self.instances += 1
        return at

    def imported(self, index):
        if not 0 <= index < len(self.frm.imports):
            raise ValueError('EBX import pointer is out of range')
        ref = self.frm.imports[index]
        for at, existing in enumerate(self.to.imports):
            if existing == ref:
                return at
        self.to.imports.append(ref)
        return len(self.to.imports) - 1

    def object(self, source):
        result = Object(self.type(source.descriptor))
        for f in source.fields:
            result.fields.append([f[0], f[1], self.value(f[2])])
        return result

    def value(self, source):
        tag = source[0]
        if tag == 'p':
            kind, index = source[1]
            if kind == P_INTERNAL and index >= 0:
                index = self.instance(index)
            elif kind == P_EXTERNAL:
                index = self.imported(index)
            return ['p', (kind, index)]
        if tag == 'o':
            return ['o', self.object(source[1]) if source[1] is not None else None]
        if tag == 'a':
            return ['a', [self.value(e) for e in source[1]]]
        if tag == 't':
            primitive, ptype, descriptor, encoded = source[1]
            if not primitive and descriptor >= 0:
                descriptor = self.type(descriptor)
            return ['t', (primitive, ptype, descriptor, encoded)]
        if tag == 'x':
            raise ValueError('EBX merge cannot carry a boxed value yet')
        return [source[0], source[1]]


def _remap_pointers(value, move_to):
    tag = value[0]
    if tag == 'p':
        kind, index = value[1]
        if kind == P_INTERNAL and 0 <= index < len(move_to):
            value[1] = (kind, move_to[index])
    elif tag == 'o':
        if value[1] is not None:
            for f in value[1].fields:
                _remap_pointers(f[2], move_to)
    elif tag == 'a':
        for e in value[1]:
            _remap_pointers(e, move_to)


def sort_instances(doc):
    if len(doc.instances) < 3:
        return
    exported = [i for i in range(1, len(doc.instances)) if doc.instances[i].exported]
    internal = [i for i in range(1, len(doc.instances)) if not doc.instances[i].exported]
    exported.sort(key=lambda i: doc.instances[i].guid)       # stable; memcmp order
    order = [0] + exported + internal
    if all(order[i] == i for i in range(len(order))):
        return
    move_to = [0] * len(order)
    for index, frm in enumerate(order):
        move_to[frm] = index
    doc.instances = [doc.instances[frm] for frm in order]
    for inst in doc.instances:
        if inst.object is not None:
            for f in inst.object.fields:
                _remap_pointers(f[2], move_to)


class MergeSummary:
    def __init__(self):
        self.instances = self.array_entries = self.renumbered = self.repeated = self.values = self.contested = 0
        self.uncarried = []
        self.unread = []

    def exact(self):
        return not self.uncarried and not self.unread

    def __repr__(self):
        return ('%d instance(s), %d list entries, %d changed value(s), %d contested, %d repeated, %d renumbered, '
                'uncarried %r, unread %r' % (self.instances, self.array_entries, self.values, self.contested,
                                             self.repeated, self.renumbered, self.uncarried, self.unread))


def merge_documents(base, edits):
    """ebx::merge_documents: the base plus what every edit added, edits applied in the order given."""
    result = clone_document(base)
    if not base.instances or base.instances[0].object is None:
        raise ValueError('EBX base has no root object')
    base_root = base.instances[0]
    base_lengths = {}
    for f in base_root.object.fields:
        if f[2][0] == 'a':
            base_lengths.setdefault(f[1], len(f[2][1]))
    totals = MergeSummary()
    appended = {}
    for index, edit in enumerate(edits):
        if edit is None:
            continue
        if not edit.instances or edit.instances[0].object is None:
            raise ValueError('EBX edit has no root object')
        edit_root = edit.instances[0]
        match = _match_instances(base, edit)
        changes = _InPlace(base, edit, match)
        for at in range(len(base.instances)):
            before = base.instances[at]
            if match[at] == UNMATCHED:
                if before.exported:
                    changes.uncarried = True
                else:
                    changes.unread = True
                continue
            after = edit.instances[match[at]]
            if before.object is None or after.object is None or result.instances[at].object is None:
                changes.unread |= before.object is not None or after.object is not None
                continue
            changes.fields(before.object, after.object, result.instances[at].object, at == 0)
        if changes.uncarried:
            totals.uncarried.append(index)
        else:
            changes.apply()
            totals.values += changes.values
            totals.contested += changes.contested
        if changes.unread:
            totals.unread.append(index)

        mapper = _Carrier(edit, result)
        mine = {}
        for f in edit_root.object.fields:
            if f[2][0] != 'a':
                continue
            edit_array = f[2][1]
            had = base_lengths.get(f[1], 0)
            if len(edit_array) <= had:
                continue
            target = None
            for rf in result.instances[0].object.fields:
                if rf[1] == f[1]:
                    target = rf[2][1] if rf[2][0] == 'a' else None
                    break
            if target is None:
                raise ValueError('EBX merge cannot find the base array ' + f[1])
            for at in range(had, len(edit_array)):
                carried = mapper.value(edit_array[at])
                if carried[0] == 'p' and carried[1][0] != P_NULL:
                    key = carried[1]
                    if key in appended.get(f[1], ()):
                        totals.repeated += 1
                        continue
                    mine.setdefault(f[1], set()).add(key)
                target.append(carried)
                totals.array_entries += 1
        for name, refs in mine.items():
            appended.setdefault(name, set()).update(refs)

        for at, inst in enumerate(edit.instances):
            if not inst.exported:
                continue
            if any(existing.exported and existing.guid == inst.guid for existing in result.instances):
                continue
            mapper.instance(at)
        totals.instances += mapper.instances
        totals.renumbered += mapper.renumbered
    sort_instances(result)
    return result, totals


# --------------------------------------------------------------------------------------------
# debugging aid
# --------------------------------------------------------------------------------------------

def dump(doc, limit=40, out=None):
    import sys
    out = out or sys.stdout

    def show(v, depth):
        tag = v[0]
        if tag == 'o':
            if v[1] is None:
                return 'null-struct'
            return '{' + ', '.join('%s=%s' % (f[1], show(f[2], depth + 1)) for f in v[1].fields) + '}'
        if tag == 'a':
            items = [show(e, depth + 1) for e in v[1][:limit]]
            return '[%d: %s%s]' % (len(v[1]), ', '.join(items), ', ...' if len(v[1]) > limit else '')
        if tag == 'p':
            kind, index = v[1]
            if kind == P_NULL:
                return 'null'
            if kind == P_INTERNAL:
                return '->#%d' % index
            fg, cg = doc.imports[index]
            return '=>%s/%s' % (fg.hex(), cg.hex())
        if tag in 'gh':
            return v[1].hex()
        return repr(v[1])
    out.write('file %s root %s instances %d imports %d types %d\n' % (doc.file_guid.hex(), doc.root_type,
                                                                      len(doc.instances), len(doc.imports), len(doc.types)))
    for index, inst in enumerate(doc.instances):
        name = doc.types[inst.descriptor].name if inst.descriptor >= 0 else '?'
        out.write('#%d %s %s %s\n' % (index, 'exp ' + inst.guid.hex() if inst.exported else 'int', name,
                                      show(['o', inst.object], 0) if inst.object is not None else '(unparsed)'))
