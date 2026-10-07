using System.Runtime.InteropServices;

namespace GtaExport;

// LSCT container: the single binary layout used both for per-ybn mesh caches (kind 1) and for
// 256 m map tiles (kind 0). Everything is little-endian, tightly packed. See docs/export-format.md.

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct V3
{
    public float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public V3(SharpDX.Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct LsctHeader // 96 bytes
{
    public uint Magic;        // "LSCT"
    public uint Version;      // 1
    public uint Kind;         // 0 = tile, 1 = per-ybn mesh
    public int TileX;         // tile: tile x index; mesh: jenkins hash of the ybn name
    public int TileY;         // tile: tile y index; mesh: 0
    public float TileSize;    // tile: 256; mesh: 0
    public float Margin;      // tile: overlap margin in metres; mesh: 0
    public uint NGroups;
    public uint NVerts;
    public uint NTris;
    public uint NPrims;
    public V3 Min;            // bounds of all vertices referenced by this file
    public V3 Max;
    public uint OffGroups;
    public uint OffVerts;
    public uint OffTris;
    public uint OffAttrs;
    public uint OffPrims;
    public ulong Signature;   // hash of the inputs, used to skip up-to-date files on resume

    public const uint MagicValue = 0x5443534C; // 'L','S','C','T'
    public const uint CurrentVersion = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GroupRec // 56 bytes
{
    public uint YbnId;        // index into index.json "ybns" (0 in mesh files)
    public ushort Child;      // composite child index, 0xFFFF if the ybn root is not a composite
    public byte BoundType;    // 0 sphere, 1 capsule, 3 box, 4 geometry, 8 geometry BVH, 12 disc, 13 cylinder
    public byte Info;         // bits 0-1 layer (0 mover, 1 hi@ weapon detail, 2 ma@ material); tiles only: bit 2 = in a script-managed map group, bit 3 = that group is guessed OFF by default
    public uint TypeFlags;    // composite child flags 1 (what this bound IS)
    public uint IncludeFlags; // composite child flags 2 (what it collides WITH)
    public uint FirstTri;
    public uint NTris;
    public uint FirstPrim;
    public uint NPrims;
    public V3 Min;
    public V3 Max;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TriAttr // 8 bytes
{
    public byte Material;     // index into materials.json
    public byte Kind;         // 0 real triangle, 1 sphere, 2 capsule, 3 box, 4 cylinder (tessellated primitive)
    public ushort MatFlags;   // per polygon material flags
    public ushort Group;      // index into this file's group table
    public byte ProcId;       // procedural id (procedural.meta)
    public byte RoomPed;      // room id (low 5 bits) | ped density (high 3 bits)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct PrimRec // 64 bytes
{
    public byte Kind;         // 1 sphere, 2 capsule, 3 box, 4 cylinder
    public byte Material;
    public ushort MatFlags;
    public ushort Group;
    public byte ProcId;
    public byte RoomPed;
    public uint FirstTri;     // tessellation of this primitive in the triangle arrays
    public uint NTris;
    public fixed float P[12]; // sphere: c.xyz r | capsule/cylinder: a.xyz b.xyz r | box: c.xyz ax.xyz ay.xyz az.xyz (half axes)
}

public sealed class Mesh
{
    public LsctHeader Header;
    public GroupRec[] Groups = Array.Empty<GroupRec>();
    public V3[] Verts = Array.Empty<V3>();
    public uint[] Tris = Array.Empty<uint>();      // 3 per triangle
    public TriAttr[] Attrs = Array.Empty<TriAttr>();
    public PrimRec[] Prims = Array.Empty<PrimRec>();

    public static unsafe int HeaderSize => sizeof(LsctHeader);

    public static unsafe void SelfCheck()
    {
        if (sizeof(LsctHeader) != 96 || sizeof(GroupRec) != 56 || sizeof(TriAttr) != 8 || sizeof(PrimRec) != 64 || sizeof(V3) != 12)
            throw new InvalidOperationException("LSCT struct layout mismatch");
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("little-endian only");
    }

    public void RecomputeBounds()
    {
        var mn = new V3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new V3(float.MinValue, float.MinValue, float.MinValue);
        foreach (ref readonly var v in Verts.AsSpan())
        {
            if (v.X < mn.X) mn.X = v.X; if (v.Y < mn.Y) mn.Y = v.Y; if (v.Z < mn.Z) mn.Z = v.Z;
            if (v.X > mx.X) mx.X = v.X; if (v.Y > mx.Y) mx.Y = v.Y; if (v.Z > mx.Z) mx.Z = v.Z;
        }
        if (Verts.Length == 0) { mn = default; mx = default; }
        Header.Min = mn; Header.Max = mx;
    }

    public long ByteSize => 96L + Groups.Length * 56L + Verts.Length * 12L + Tris.Length * 4L + Attrs.Length * 8L + Prims.Length * 64L;

    public void Write(Stream s)
    {
        Header.Magic = LsctHeader.MagicValue;
        Header.Version = LsctHeader.CurrentVersion;
        Header.NGroups = (uint)Groups.Length;
        Header.NVerts = (uint)Verts.Length;
        Header.NTris = (uint)Attrs.Length;
        Header.NPrims = (uint)Prims.Length;
        if (ByteSize > uint.MaxValue) throw new InvalidOperationException("LSCT file would exceed 4 GiB");
        uint off = 96;
        Header.OffGroups = off; off += (uint)Groups.Length * 56;
        Header.OffVerts = off; off += (uint)Verts.Length * 12;
        Header.OffTris = off; off += (uint)Tris.Length * 4;
        Header.OffAttrs = off; off += (uint)Attrs.Length * 8;
        Header.OffPrims = off;
        s.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Header, 1)));
        s.Write(MemoryMarshal.AsBytes(Groups.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(Verts.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(Tris.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(Attrs.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(Prims.AsSpan()));
    }

    public static bool TryReadHeader(string path, out LsctHeader h)
    {
        h = default;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            Span<byte> buf = stackalloc byte[96];
            int n = fs.ReadAtLeast(buf, 96, false);
            if (n < 96) return false;
            h = MemoryMarshal.Read<LsctHeader>(buf);
            if (h.Magic != LsctHeader.MagicValue || h.Version != LsctHeader.CurrentVersion) return false;
            long expect = (long)h.OffPrims + h.NPrims * 64L;
            return fs.Length == expect;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Only the header and the group table (cheap way to see which sources a tile holds).</summary>
    public static GroupRec[] ReadGroups(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> hb = stackalloc byte[96];
        fs.ReadExactly(hb);
        var h = MemoryMarshal.Read<LsctHeader>(hb);
        if (h.Magic != LsctHeader.MagicValue || h.Version != LsctHeader.CurrentVersion) throw new InvalidDataException("bad LSCT file: " + path);
        var groups = new GroupRec[h.NGroups];
        fs.Position = h.OffGroups;
        fs.ReadExactly(MemoryMarshal.AsBytes(groups.AsSpan()));
        return groups;
    }

    public static Mesh Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var span = data.AsSpan();
        if (data.Length < 96) throw new InvalidDataException("truncated LSCT: " + path);
        var m = new Mesh { Header = MemoryMarshal.Read<LsctHeader>(span) };
        ref var h = ref m.Header;
        if (h.Magic != LsctHeader.MagicValue) throw new InvalidDataException("bad LSCT magic: " + path);
        if (h.Version != LsctHeader.CurrentVersion) throw new InvalidDataException("unsupported LSCT version: " + path);
        m.Groups = MemoryMarshal.Cast<byte, GroupRec>(span.Slice((int)h.OffGroups, (int)h.NGroups * 56)).ToArray();
        m.Verts = MemoryMarshal.Cast<byte, V3>(span.Slice((int)h.OffVerts, (int)h.NVerts * 12)).ToArray();
        m.Tris = MemoryMarshal.Cast<byte, uint>(span.Slice((int)h.OffTris, (int)h.NTris * 12)).ToArray();
        m.Attrs = MemoryMarshal.Cast<byte, TriAttr>(span.Slice((int)h.OffAttrs, (int)h.NTris * 8)).ToArray();
        m.Prims = MemoryMarshal.Cast<byte, PrimRec>(span.Slice((int)h.OffPrims, (int)h.NPrims * 64)).ToArray();
        return m;
    }
}

/// <summary>Composite child type/include flag bits (same numbering as CodeWalker's EBoundCompositeFlags).</summary>
public static class BoundFlags
{
    public const uint UNKNOWN = 1u, MAP_WEAPON = 1u << 1, MAP_DYNAMIC = 1u << 2, MAP_ANIMAL = 1u << 3, MAP_COVER = 1u << 4,
        MAP_VEHICLE = 1u << 5, VEHICLE_NOT_BVH = 1u << 6, VEHICLE_BVH = 1u << 7, VEHICLE_BOX = 1u << 8, PED = 1u << 9,
        RAGDOLL = 1u << 10, ANIMAL = 1u << 11, ANIMAL_RAGDOLL = 1u << 12, OBJECT = 1u << 13, OBJECT_ENV_CLOTH = 1u << 14,
        PLANT = 1u << 15, PROJECTILE = 1u << 16, EXPLOSION = 1u << 17, PICKUP = 1u << 18, FOLIAGE = 1u << 19,
        FORKLIFT_FORKS = 1u << 20, TEST_WEAPON = 1u << 21, TEST_CAMERA = 1u << 22, TEST_AI = 1u << 23, TEST_SCRIPT = 1u << 24,
        TEST_VEHICLE_WHEEL = 1u << 25, GLASS = 1u << 26, MAP_RIVER = 1u << 27, SMOKE = 1u << 28, UNSMASHED = 1u << 29,
        MAP_STAIRS = 1u << 30, MAP_DEEP_SURFACE = 1u << 31;

    public static readonly string[] Names =
    {
        "UNKNOWN", "MAP_WEAPON", "MAP_DYNAMIC", "MAP_ANIMAL", "MAP_COVER", "MAP_VEHICLE", "VEHICLE_NOT_BVH", "VEHICLE_BVH",
        "VEHICLE_BOX", "PED", "RAGDOLL", "ANIMAL", "ANIMAL_RAGDOLL", "OBJECT", "OBJECT_ENV_CLOTH", "PLANT", "PROJECTILE",
        "EXPLOSION", "PICKUP", "FOLIAGE", "FORKLIFT_FORKS", "TEST_WEAPON", "TEST_CAMERA", "TEST_AI", "TEST_SCRIPT",
        "TEST_VEHICLE_WHEEL", "GLASS", "MAP_RIVER", "SMOKE", "UNSMASHED", "MAP_STAIRS", "MAP_DEEP_SURFACE",
    };

    public static string Describe(uint f)
    {
        if (f == 0) return "NONE";
        var parts = new List<string>();
        for (int i = 0; i < 32; i++) if ((f & (1u << i)) != 0) parts.Add(Names[i]);
        return string.Join("|", parts);
    }
}

public static class MatFlags
{
    public static readonly string[] Names =
    {
        "STAIRS", "NOT_CLIMBABLE", "SEE_THROUGH", "SHOOT_THROUGH", "NOT_COVER", "WALKABLE_PATH", "NO_CAM_COLLISION",
        "SHOOT_THROUGH_FX", "NO_DECAL", "NO_NAVMESH", "NO_RAGDOLL", "VEHICLE_WHEEL", "NO_PTFX", "TOO_STEEP_FOR_PLAYER",
        "NO_NETWORK_SPAWN", "NO_CAM_COLLISION_ALLOW_CLIPPING",
    };
}
