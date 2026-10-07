using System.Runtime.InteropServices;
using System.Text.Json;

namespace GtaSkate;

/// <summary>A material of the portable model: a base colour texture next to the file, alpha mode, sidedness.</summary>
public sealed class GltfMaterial
{
    public string Name, TextureUri;
    public int Alpha = 1;          // 1 opaque, 2 mask, 3 blend
    public float Cutoff = 0.5f;
    public bool TwoSided;
}

/// <summary>
/// One standard glTF 2.0 scene (.gltf) for the whole map, for use outside ReSkate Studio: Blender, Unity,
/// Unreal, other engines. Geometry lives in many .bin buffers next to it (one per map tile and one per
/// repeated prop), textures are referenced by relative URI, and props that repeat are nodes sharing one mesh.
/// y up, metres, front faces counter-clockwise. Meshes are given in GTA space (z up) and converted here.
/// </summary>
public sealed class GltfDoc
{
    struct View { public int Buffer; public long Off, Len; public int Target; }
    struct Acc { public int View, Comp, Count; public string Type; public float[] Min, Max; }
    struct Prim { public int Pos, Nrm, Uv, Idx; public string Material; }

    readonly object _gate = new();
    readonly List<(string uri, long len)> _buffers = new();
    readonly List<View> _views = new();
    readonly List<Acc> _accs = new();
    readonly List<(string name, Prim[] prims)> _meshes = new();
    readonly List<(string name, int mesh, float[] rows)> _nodes = new();
    public long Triangles, Bytes;

    /// <summary>Writes one buffer file holding these meshes and returns the index of the first mesh.</summary>
    public int AddMeshes(string dir, string uri, IReadOnlyList<(string name, List<GlbPrim> prims)> meshes)
    {
        using var bin = new MemoryStream();
        var views = new List<View>(); var accs = new List<Acc>(); var outMeshes = new List<(string, Prim[])>();
        long tris = 0;
        int V(ReadOnlySpan<byte> data, int target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add(new View { Off = bin.Length, Len = data.Length, Target = target });
            bin.Write(data);
            return views.Count - 1;
        }
        foreach (var (mname, prims) in meshes)
        {
            var list = new List<Prim>();
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
                var pr = new Prim { Material = p.Material, Nrm = -1, Uv = -1 };
                accs.Add(new Acc { View = V(MemoryMarshal.AsBytes<float>(pos), 34962), Comp = 5126, Count = nv, Type = "VEC3", Min = new[] { minx, miny, minz }, Max = new[] { maxx, maxy, maxz } });
                pr.Pos = accs.Count - 1;
                if (p.Nrm != null)
                {
                    var nrm = new float[nv * 3];
                    for (int i = 0; i < nv; i++) { nrm[i * 3] = p.Nrm[i * 3]; nrm[i * 3 + 1] = p.Nrm[i * 3 + 2]; nrm[i * 3 + 2] = -p.Nrm[i * 3 + 1]; }
                    accs.Add(new Acc { View = V(MemoryMarshal.AsBytes<float>(nrm), 34962), Comp = 5126, Count = nv, Type = "VEC3" });
                    pr.Nrm = accs.Count - 1;
                }
                if (p.Uv != null)
                {
                    accs.Add(new Acc { View = V(MemoryMarshal.AsBytes<float>(p.Uv), 34962), Comp = 5126, Count = nv, Type = "VEC2" });
                    pr.Uv = accs.Count - 1;
                }
                accs.Add(new Acc { View = V(MemoryMarshal.AsBytes<int>(p.Idx), 34963), Comp = 5125, Count = p.Idx.Length, Type = "SCALAR" });
                pr.Idx = accs.Count - 1;
                tris += p.Idx.Length / 3;
                list.Add(pr);
            }
            outMeshes.Add((mname, list.ToArray()));
        }
        Paths.AtomicWrite(Path.Combine(dir, uri), s => s.Write(bin.GetBuffer(), 0, (int)bin.Length));
        lock (_gate)
        {
            int b = _buffers.Count, v0 = _views.Count, a0 = _accs.Count, m0 = _meshes.Count;
            _buffers.Add((uri.Replace('\\', '/'), bin.Length));
            foreach (var v in views) { var c = v; c.Buffer = b; _views.Add(c); }
            foreach (var a in accs) { var c = a; c.View += v0; _accs.Add(c); }
            foreach (var (n, prims) in outMeshes)
            {
                var ps = (Prim[])prims.Clone();
                for (int i = 0; i < ps.Length; i++)
                {
                    ps[i].Pos += a0; ps[i].Idx += a0;
                    if (ps[i].Nrm >= 0) ps[i].Nrm += a0;
                    if (ps[i].Uv >= 0) ps[i].Uv += a0;
                }
                _meshes.Add((n, ps));
            }
            Triangles += tris; Bytes += bin.Length;
            return m0;
        }
    }

    /// <param name="rows">12 numbers as in MapObject.Rows / Translation</param>
    public void AddNode(string name, int mesh, float[] rows) { lock (_gate) _nodes.Add((name, mesh, rows)); }

    public int Nodes => _nodes.Count;
    public int Meshes => _meshes.Count;

    public void Save(string path, IReadOnlyDictionary<string, GltfMaterial> materials)
    {
        var matIndex = new Dictionary<string, int>(); var matList = new List<GltfMaterial>(); var images = new List<string>(); var imgIndex = new Dictionary<string, int>();
        foreach (var (_, prims) in _meshes)
            foreach (var p in prims)
                if (p.Material != null && !matIndex.ContainsKey(p.Material) && materials.TryGetValue(p.Material, out var gm)) { matIndex[p.Material] = matList.Count; matList.Add(gm); }
        Paths.AtomicWrite(path, s =>
        {
            using var w = new Utf8JsonWriter(s);
            w.WriteStartObject();
            w.WriteStartObject("asset"); w.WriteString("version", "2.0"); w.WriteString("generator", "gta-skate"); w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes"); w.WriteStartObject(); w.WriteString("name", "map");
            w.WriteStartArray("nodes"); for (int i = 0; i < _nodes.Count; i++) w.WriteNumberValue(i); w.WriteEndArray(); w.WriteEndObject(); w.WriteEndArray();
            w.WriteStartArray("nodes");
            foreach (var (name, mesh, r) in _nodes)
            {
                w.WriteStartObject(); w.WriteString("name", name); w.WriteNumber("mesh", mesh);
                w.WriteStartArray("matrix");
                w.WriteNumberValue(r[0]); w.WriteNumberValue(r[1]); w.WriteNumberValue(r[2]); w.WriteNumberValue(0);
                w.WriteNumberValue(r[3]); w.WriteNumberValue(r[4]); w.WriteNumberValue(r[5]); w.WriteNumberValue(0);
                w.WriteNumberValue(r[6]); w.WriteNumberValue(r[7]); w.WriteNumberValue(r[8]); w.WriteNumberValue(0);
                w.WriteNumberValue(r[9]); w.WriteNumberValue(r[10]); w.WriteNumberValue(r[11]); w.WriteNumberValue(1);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("materials");
            foreach (var m in matList)
            {
                w.WriteStartObject(); w.WriteString("name", m.Name);
                w.WriteStartObject("pbrMetallicRoughness");
                if (m.TextureUri != null)
                {
                    if (!imgIndex.TryGetValue(m.TextureUri, out var img)) { img = images.Count; imgIndex[m.TextureUri] = img; images.Add(m.TextureUri); }
                    w.WriteStartObject("baseColorTexture"); w.WriteNumber("index", img); w.WriteEndObject();
                }
                w.WriteNumber("metallicFactor", 0); w.WriteNumber("roughnessFactor", 0.85);
                w.WriteEndObject();
                if (m.Alpha == 2) { w.WriteString("alphaMode", "MASK"); w.WriteNumber("alphaCutoff", m.Cutoff); }
                else if (m.Alpha == 3) w.WriteString("alphaMode", "BLEND");
                if (m.TwoSided) w.WriteBoolean("doubleSided", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (images.Count > 0)
            {
                w.WriteStartArray("samplers"); w.WriteStartObject(); w.WriteNumber("magFilter", 9729); w.WriteNumber("minFilter", 9987); w.WriteNumber("wrapS", 10497); w.WriteNumber("wrapT", 10497); w.WriteEndObject(); w.WriteEndArray();
                w.WriteStartArray("images"); foreach (var u in images) { w.WriteStartObject(); w.WriteString("uri", u); w.WriteEndObject(); } w.WriteEndArray();
                w.WriteStartArray("textures"); for (int i = 0; i < images.Count; i++) { w.WriteStartObject(); w.WriteNumber("sampler", 0); w.WriteNumber("source", i); w.WriteEndObject(); } w.WriteEndArray();
            }
            w.WriteStartArray("meshes");
            foreach (var (name, prims) in _meshes)
            {
                w.WriteStartObject(); w.WriteString("name", name);
                w.WriteStartArray("primitives");
                foreach (var p in prims)
                {
                    w.WriteStartObject();
                    w.WriteStartObject("attributes");
                    w.WriteNumber("POSITION", p.Pos);
                    if (p.Nrm >= 0) w.WriteNumber("NORMAL", p.Nrm);
                    if (p.Uv >= 0) w.WriteNumber("TEXCOORD_0", p.Uv);
                    w.WriteEndObject();
                    w.WriteNumber("indices", p.Idx);
                    if (p.Material != null && matIndex.TryGetValue(p.Material, out var mi)) w.WriteNumber("material", mi);
                    w.WriteNumber("mode", 4);
                    w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("accessors");
            foreach (var a in _accs)
            {
                w.WriteStartObject();
                w.WriteNumber("bufferView", a.View); w.WriteNumber("componentType", a.Comp); w.WriteNumber("count", a.Count); w.WriteString("type", a.Type);
                if (a.Min != null)
                {
                    w.WriteStartArray("min"); foreach (var v in a.Min) w.WriteNumberValue(v); w.WriteEndArray();
                    w.WriteStartArray("max"); foreach (var v in a.Max) w.WriteNumberValue(v); w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("bufferViews");
            foreach (var v in _views)
            {
                w.WriteStartObject(); w.WriteNumber("buffer", v.Buffer); w.WriteNumber("byteOffset", v.Off); w.WriteNumber("byteLength", v.Len); w.WriteNumber("target", v.Target); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("buffers");
            foreach (var (uri, len) in _buffers) { w.WriteStartObject(); w.WriteString("uri", uri); w.WriteNumber("byteLength", len); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
            w.Flush();
        });
    }
}
