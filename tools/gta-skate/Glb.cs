using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GtaSkate;

/// <summary>One glTF primitive. Positions and normals are in GTA / Blender space (z up); uv is glTF's (v down).</summary>
public sealed class GlbPrim
{
    public string Material;
    public float[] Pos, Nrm, Uv;   // Nrm and Uv may be null
    public int[] Idx;
}

/// <summary>
/// Minimal binary glTF writer for ReSkate Studio's normalized map: one mesh, one primitive per material,
/// y up (x, z, -y), the way Blender's exporter writes an object at the identity transform.
/// </summary>
public static class Glb
{
    public static void Write(string path, string name, IReadOnlyList<GlbPrim> prims)
    {
        using var bin = new MemoryStream();
        var views = new List<(long off, long len, int target)>();
        var accessors = new List<Action<Utf8JsonWriter>>();
        var primJson = new List<(int pos, int nrm, int uv, int idx, int mat)>();
        var matNames = new List<string>();

        int View(ReadOnlySpan<byte> data, int target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add((bin.Length, data.Length, target));
            bin.Write(data);
            return views.Count - 1;
        }

        foreach (var p in prims)
        {
            int nv = p.Pos.Length / 3;
            var pos = new float[nv * 3];
            float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
            for (int i = 0; i < nv; i++)
            {
                float x = p.Pos[i * 3], y = p.Pos[i * 3 + 2], z = -p.Pos[i * 3 + 1];
                pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
                if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; if (z < minz) minz = z; if (z > maxz) maxz = z;
            }
            int vPos = View(MemoryMarshal.AsBytes<float>(pos), 34962);
            int aPos = accessors.Count;
            float[] mn = { minx, miny, minz }, mx = { maxx, maxy, maxz };
            accessors.Add(w =>
            {
                w.WriteNumber("bufferView", vPos); w.WriteNumber("componentType", 5126); w.WriteNumber("count", nv); w.WriteString("type", "VEC3");
                w.WriteStartArray("min"); foreach (var v in mn) w.WriteNumberValue(v); w.WriteEndArray();
                w.WriteStartArray("max"); foreach (var v in mx) w.WriteNumberValue(v); w.WriteEndArray();
            });
            int aNrm = -1, aUv = -1;
            if (p.Nrm != null)
            {
                var nrm = new float[nv * 3];
                for (int i = 0; i < nv; i++) { nrm[i * 3] = p.Nrm[i * 3]; nrm[i * 3 + 1] = p.Nrm[i * 3 + 2]; nrm[i * 3 + 2] = -p.Nrm[i * 3 + 1]; }
                int v = View(MemoryMarshal.AsBytes<float>(nrm), 34962);
                aNrm = accessors.Count;
                accessors.Add(w => { w.WriteNumber("bufferView", v); w.WriteNumber("componentType", 5126); w.WriteNumber("count", nv); w.WriteString("type", "VEC3"); });
            }
            if (p.Uv != null)
            {
                int v = View(MemoryMarshal.AsBytes<float>(p.Uv), 34962);
                aUv = accessors.Count;
                accessors.Add(w => { w.WriteNumber("bufferView", v); w.WriteNumber("componentType", 5126); w.WriteNumber("count", nv); w.WriteString("type", "VEC2"); });
            }
            int ni = p.Idx.Length;
            int vIdx = View(MemoryMarshal.AsBytes<int>(p.Idx), 34963);
            int aIdx = accessors.Count;
            accessors.Add(w => { w.WriteNumber("bufferView", vIdx); w.WriteNumber("componentType", 5125); w.WriteNumber("count", ni); w.WriteString("type", "SCALAR"); });
            int mat = -1;
            if (p.Material != null)
            {
                mat = matNames.IndexOf(p.Material);
                if (mat < 0) { mat = matNames.Count; matNames.Add(p.Material); }
            }
            primJson.Add((aPos, aNrm, aUv, aIdx, mat));
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        using var js = new MemoryStream();
        using (var w = new Utf8JsonWriter(js))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset"); w.WriteString("version", "2.0"); w.WriteString("generator", "gta-skate"); w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes"); w.WriteStartObject(); w.WriteStartArray("nodes"); w.WriteNumberValue(0); w.WriteEndArray(); w.WriteEndObject(); w.WriteEndArray();
            w.WriteStartArray("nodes"); w.WriteStartObject(); w.WriteString("name", name); w.WriteNumber("mesh", 0); w.WriteEndObject(); w.WriteEndArray();
            if (matNames.Count > 0)
            {
                w.WriteStartArray("materials");
                foreach (var m in matNames) { w.WriteStartObject(); w.WriteString("name", m); w.WriteEndObject(); }
                w.WriteEndArray();
            }
            w.WriteStartArray("meshes"); w.WriteStartObject(); w.WriteString("name", name);
            w.WriteStartArray("primitives");
            foreach (var p in primJson)
            {
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", p.pos);
                if (p.nrm >= 0) w.WriteNumber("NORMAL", p.nrm);
                if (p.uv >= 0) w.WriteNumber("TEXCOORD_0", p.uv);
                w.WriteEndObject();
                w.WriteNumber("indices", p.idx);
                if (p.mat >= 0) w.WriteNumber("material", p.mat);
                w.WriteNumber("mode", 4);
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndArray();
            w.WriteStartArray("accessors");
            foreach (var a in accessors) { w.WriteStartObject(); a(w); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("bufferViews");
            foreach (var v in views)
            {
                w.WriteStartObject(); w.WriteNumber("buffer", 0); w.WriteNumber("byteOffset", v.off); w.WriteNumber("byteLength", v.len); w.WriteNumber("target", v.target); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("buffers"); w.WriteStartObject(); w.WriteNumber("byteLength", bin.Length); w.WriteEndObject(); w.WriteEndArray();
            w.WriteEndObject();
        }
        while (js.Length % 4 != 0) js.WriteByte(0x20);

        Paths.AtomicWrite(path, s =>
        {
            Span<byte> h = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x46546C67); BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(8), (uint)(12 + 8 + js.Length + 8 + bin.Length));
            s.Write(h);
            Span<byte> c = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(c, (uint)js.Length); BinaryPrimitives.WriteUInt32LittleEndian(c.Slice(4), 0x4E4F534A);
            s.Write(c); s.Write(js.GetBuffer(), 0, (int)js.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(c, (uint)bin.Length); BinaryPrimitives.WriteUInt32LittleEndian(c.Slice(4), 0x004E4942);
            s.Write(c); s.Write(bin.GetBuffer(), 0, (int)bin.Length);
        });
    }
}

/// <summary>One object record of map.json.</summary>
public sealed class MapObject
{
    public string Name, Mesh, Material, CollisionMesh;
    public float[] SharedOrigin;       // not null: the compiler instances this mesh (prototype bounding-box centre, y up)
    public int Primitive;
    public float[] Transform;          // right, up, forward, translation (y up)
    public bool Render = true;
    public string CollisionMode = "none";
    public int CollisionPacked = 32;

    /// <summary>Studio's 12 numbers for a GTA-space basis and position: conv(v) = (x, z, -y).</summary>
    public static float[] Rows(SharpDX.Vector3 ex, SharpDX.Vector3 ey, SharpDX.Vector3 ez, SharpDX.Vector3 p) => new[]
    {
        ex.X, ex.Z, -ex.Y,
        ez.X, ez.Z, -ez.Y,
        -ey.X, -ey.Z, ey.Y,
        p.X, p.Z, -p.Y,
    };

    public static float[] Translation(float x, float y, float z) => new[] { 1f, 0, 0, 0, 1f, 0, 0, 0, 1f, x, z, -y };
}
