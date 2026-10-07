using System.Collections.Concurrent;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaSkate;

/// <summary>A material of the pack. Alpha and Domain use ReSkate Studio's numbers (sk8_material).</summary>
public sealed class Material
{
    public int Id;
    public string Name;
    public VClass Class;
    public int Tex = -1;
    public int Alpha = 1;          // 1 opaque, 2 mask (cut-out), 3 blend
    public float Cutoff = 0.5f;
    public int Domain;             // 0 surface, 1 decal, 2 foliage
    public bool TwoSided, Emissive;
    public long Tris;
}

public sealed class MatLib
{
    readonly TexLib _tex;
    readonly Dictionary<string, Material> _map = new();
    readonly HashSet<string> _names = new(StringComparer.Ordinal);
    readonly List<Material> _list = new();
    public MatLib(TexLib tex) { _tex = tex; }
    public Material[] All { get { lock (_map) return _list.ToArray(); } }
    public Material ById(int id) { lock (_map) return _list[id]; }

    public Material Get(VClass cls, int tex, bool texAlpha, string texName, bool emissive)
    {
        var m = new Material { Class = cls, Tex = tex, Emissive = emissive };
        switch (cls)
        {
            case VClass.Cutout: m.Alpha = texAlpha ? 2 : 1; m.TwoSided = true; break;
            case VClass.Leaves: m.Alpha = texAlpha ? 2 : 1; m.Cutoff = 0.33f; m.Domain = 2; m.TwoSided = true; break;
            case VClass.Decal: m.Alpha = texAlpha ? 3 : 1; m.Domain = 1; break;
            case VClass.Glass: case VClass.Alpha: case VClass.Water: m.Alpha = texAlpha ? 3 : 1; break;
        }
        var safe = new string((texName ?? "").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (safe.Length > 35) safe = safe.Substring(0, 35);
        // The label is part of the identity: two game textures with the same pixels share one output texture, and
        // without it the material would be named after whichever of them a build thread reached first.
        string key = $"{(int)cls}|{tex}|{m.Alpha}|{(emissive ? 1 : 0)}|{safe}";
        lock (_map)
        {
            if (_map.TryGetValue(key, out var known)) return known;
            m.Id = _list.Count;       // index into All. It follows thread timing, so it is never part of a name or of an order.
            string kind = cls switch { VClass.Cutout => "cut", VClass.Leaves => "leaf", VClass.Decal => "decal", VClass.Glass => "glass", VClass.Alpha => "alpha", VClass.Water => "water", VClass.Terrain => "ter", VClass.Wood => "wood", _ => "m" };
            // The name says what the material is (class, the texture's pixels, alpha mode, emissive, label), so the
            // same material has the same name in every run and in every export. At most 52 characters, as before.
            var h = Hash64.Create();
            h.Add((int)cls); h.Add(_tex.ContentOf(tex)); h.Add(m.Alpha); h.Add(emissive ? 1 : 0); h.Add(safe);
            ulong id40 = h.Value & 0xFFFFFFFFFFUL;
            m.Name = $"{kind}_{safe}_{id40:x10}";
            if (!_names.Add(m.Name))
            {
                Log.Warn($"material name {m.Name} is taken by another material; this one gets a run-dependent suffix");
                m.Name += "_" + m.Id; _names.Add(m.Name);
            }
            _list.Add(m);
            _map[key] = m;
            return m;
        }
    }
}

/// <summary>A run of triangles of a drawable that will share one material.</summary>
public sealed class SrcPart
{
    public VClass Class;
    public bool Emissive;
    public int Material = -1;          // set when the part is not tinted
    public TexRef TintTex;             // tinted: the base texture, multiplied by a palette colour per entity
    public byte[] Pal; public int PalW, PalH, TintCol;
    public int[] TintHist;             // triangles per palette column when the part mixes many columns
    public float[] Pos, Nrm, Uv;
    public int[] Idx;
    public int NVerts => Pos.Length / 3;
}

public sealed class SrcMesh
{
    public uint Hash;
    public string Name = "";
    public List<SrcPart> Parts = new();
    public Vector3 Min, Max;
    public int Tris, Verts;
    public bool IsTree, HasTint;
    public long Bytes;
    // the archetype's own collision (props: benches, rails, poles, trunks), model space; null when it has none
    public float[] ColPos;
    public int[] ColIdx;
    public byte[] ColSurf;         // skate surface per triangle (index into Collision.Surfaces)
}

public sealed class BuildStats
{
    public long Drawables, Geometries, SkippedShader, SkippedProxy, BadLayout, NoDiffuse, SourceTris, OutTris, FlippedGeoms, NoNormals, BadUv, RepeatTris, RepeatMasked;
}

/// <summary>Drawable -> textured, model-space parts (positions, normals, uv, indices) grouped by material.</summary>
public sealed class MeshBuilder
{
    readonly TexLib _tex;
    readonly MatLib _mats;
    readonly ShaderTable _shaders;
    public readonly BuildStats Stats = new();
    public int Level;                  // drawable LOD: 0 high ... 3 vlow
    public int TreeLevel = 1;          // ... for drawables that are vegetation

    const uint P_Diffuse = (uint)ShaderParamNames.DiffuseSampler, P_Layer0 = (uint)ShaderParamNames.TextureSampler_layer0,
        P_Layer1 = (uint)ShaderParamNames.TextureSampler_layer1, P_Layer2 = (uint)ShaderParamNames.TextureSampler_layer2,
        P_Layer3 = (uint)ShaderParamNames.TextureSampler_layer3, P_Lookup = (uint)ShaderParamNames.lookupSampler,
        P_TintPal = (uint)ShaderParamNames.TintPaletteSampler, P_DiffPal = (uint)ShaderParamNames.TextureSamplerDiffPal,
        P_PlateBg = (uint)ShaderParamNames.PlateBgSampler;

    public MeshBuilder(TexLib tex, MatLib mats, ShaderTable shaders) { _tex = tex; _mats = mats; _shaders = shaders; }

    static float HalfToFloat(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);

    static void ReadUv(byte[] vb, int o, VertexComponentType t, out float u, out float v)
    {
        if (t == VertexComponentType.Float2) { u = BitConverter.ToSingle(vb, o); v = BitConverter.ToSingle(vb, o + 4); }
        else if (t == VertexComponentType.Half2) { u = HalfToFloat(BitConverter.ToUInt16(vb, o)); v = HalfToFloat(BitConverter.ToUInt16(vb, o + 2)); }
        else { u = 0; v = 0; }
        if (!float.IsFinite(u)) u = 0;
        if (!float.IsFinite(v)) v = 0;
    }

    /// <summary>Model transforms for drawables with a skeleton (mostly fragments): same rules as CodeWalker's renderer.</summary>
    static Matrix[] ModelTransforms(DrawableBase d, DrawableModel[] models)
    {
        var sk = d.Skeleton;
        if (sk == null) return null;
        Matrix[] mt = sk.Transformations;
        bool usepose = false;
        if (d is FragDrawable fd)
        {
            var pose = fd.OwnerFragment?.BoneTransforms?.Items;
            if (pose != null)
            {
                mt ??= new Matrix[pose.Length];
                int n = Math.Min(pose.Length, mt.Length);
                mt = (Matrix[])mt.Clone();
                for (int i = 0; i < n; i++)
                {
                    var r1 = pose[i].Row1; var r2 = pose[i].Row2; var r3 = pose[i].Row3;
                    mt[i] = new Matrix(r1.X, r2.X, r3.X, 0f, r1.Y, r2.Y, r3.Y, 0f, r1.Z, r2.Z, r3.Z, 0f, r1.W, r2.W, r3.W, 1f);
                }
                usepose = true;
            }
        }
        if (mt == null) return null;
        var res = new Matrix[models.Length];
        var pinds = sk.ParentIndices;
        for (int mi = 0; mi < models.Length; mi++)
        {
            var model = models[mi];
            int b = model.BoneIndex;
            var trans = b < mt.Length ? mt[b] : Matrix.Identity;
            if (!usepose)
            {
                trans.Column4 = Vector4.UnitW;
                short p = (pinds != null && b < pinds.Length) ? pinds[b] : (short)-1;
                int guard = 0;
                while (p >= 0 && pinds != null && p < pinds.Length && guard++ < 256)
                {
                    var pt = p < mt.Length ? mt[p] : Matrix.Identity;
                    pt.Column4 = Vector4.UnitW;
                    trans = Matrix.Multiply(trans, pt);
                    p = pinds[p];
                }
            }
            res[mi] = model.HasSkin != 0 ? Matrix.Identity : trans;
        }
        return res;
    }

    static DrawableModel[] PickModels(DrawableBase d, int level)
    {
        var dm = d.DrawableModels;
        if (dm == null) return d.AllModels;
        var lods = new[] { dm.High, dm.Med, dm.Low, dm.VLow };
        for (int l = Math.Clamp(level, 0, 3); l >= 0; l--) if (lods[l] != null && lods[l].Length > 0) return lods[l];
        for (int l = 0; l < 4; l++) if (lods[l] != null && lods[l].Length > 0) return lods[l];
        return d.AllModels;
    }

    static bool LooksLikeTree(DrawableBase d, ShaderTable shaders)
    {
        var models = PickModels(d, 0);
        if (models == null) return false;
        long trees = 0, other = 0;
        foreach (var m in models)
        {
            if (m?.Geometries == null) continue;
            foreach (var g in m.Geometries)
            {
                if (g?.Shader == null || g.IndexBuffer?.Indices == null) continue;
                if (shaders.Get(g.Shader).Trees) trees += g.IndexBuffer.Indices.Length; else other += g.IndexBuffer.Indices.Length;
            }
        }
        return trees > 0 && trees * 2 >= other;
    }

    sealed class Bucket
    {
        public readonly List<int> Tri = new();   // source vertex indices, 3 per triangle
    }

    public SrcMesh Build(DrawableBase d, Archetype arch)
    {
        var mesh = new SrcMesh { Hash = arch.Hash.Hash, Name = (arch.Name ?? "").ToLowerInvariant() };
        Interlocked.Increment(ref Stats.Drawables);
        bool tree = LooksLikeTree(d, _shaders);
        mesh.IsTree = tree;
        var models = PickModels(d, tree ? TreeLevel : Level);
        if (models == null || models.Length == 0) return mesh;
        var chain = _tex.Chain(arch.TextureDict);
        var xforms = ModelTransforms(d, models);
        var decoded = new Dictionary<ulong, Pixels>();
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);

        TexRef Ref(TextureBase tb) => _tex.Find(tb, chain, d, mesh.Hash);
        Pixels Px(TextureBase tb, int cap)
        {
            var t = Ref(tb);
            if (t == null) return null;
            if (decoded.TryGetValue(t.Key, out var p)) return p;
            p = _tex.Decode(t.Tex, cap);
            decoded[t.Key] = p;
            return p;
        }

        for (int mi = 0; mi < models.Length; mi++)
        {
            var model = models[mi];
            if (model?.Geometries == null) continue;
            if ((model.RenderMaskFlags & 1) == 0) { Interlocked.Add(ref Stats.SkippedProxy, model.Geometries.Length); continue; }
            bool useXf = xforms != null && !xforms[mi].IsIdentity;
            var xf = useXf ? xforms[mi] : Matrix.Identity;

            foreach (var geom in model.Geometries)
            {
                if (geom?.Shader == null || geom.VertexData?.VertexBytes == null || geom.IndexBuffer?.Indices == null) continue;
                Interlocked.Increment(ref Stats.Geometries);
                var sh = _shaders.Get(geom.Shader);
                var ind = geom.IndexBuffer.Indices;
                int ntri = ind.Length / 3;
                if (sh.Skip || sh.Class == VClass.Grass) { Interlocked.Increment(ref Stats.SkippedShader); continue; }

                var decl = geom.VertexData.Info;
                var vb = geom.VertexData.VertexBytes;
                int stride = decl.Stride, count = stride > 0 ? vb.Length / stride : 0;
                uint fl = decl.Flags;
                if ((fl & 1) == 0 || decl.GetComponentType(0) != VertexComponentType.Float3 || count == 0)
                { Interlocked.Increment(ref Stats.BadLayout); continue; }
                int oPos = decl.GetComponentOffset(0), oNrm = -1, oCol0 = -1, oCol1 = -1, oUv0 = -1, oUv1 = -1;
                VertexComponentType tUv0 = default, tUv1 = default;
                if ((fl & (1u << 3)) != 0 && decl.GetComponentType(3) == VertexComponentType.Float3) oNrm = decl.GetComponentOffset(3);
                if ((fl & (1u << 4)) != 0 && decl.GetComponentType(4) is VertexComponentType.Colour or VertexComponentType.UByte4) oCol0 = decl.GetComponentOffset(4);
                if ((fl & (1u << 5)) != 0 && decl.GetComponentType(5) is VertexComponentType.Colour or VertexComponentType.UByte4) oCol1 = decl.GetComponentOffset(5);
                if ((fl & (1u << 6)) != 0) { oUv0 = decl.GetComponentOffset(6); tUv0 = decl.GetComponentType(6); }
                if ((fl & (1u << 7)) != 0) { oUv1 = decl.GetComponentOffset(7); tUv1 = decl.GetComponentType(7); }

                // shader textures
                TexRef diff = null;
                Pixels pal = null, lookup = null;
                var layers = new TexRef[4];
                TextureBase firstLayer = null;
                var cls = sh.Class;
                if (cls != VClass.Water)
                {
                    var pl = geom.Shader.ParametersList;
                    var ps = pl?.Parameters; var hs = pl?.Hashes;
                    if (ps != null && hs != null)
                        for (int i = 0; i < ps.Length && i < hs.Length; i++)
                        {
                            if (ps[i].DataType != 0) continue;
                            if (ps[i].Data is not TextureBase tb) continue;
                            uint h = (uint)hs[i];
                            if (sh.Terrain)
                            {
                                if (h == P_Layer0) layers[0] = Ref(tb);
                                else if (h == P_Layer1) layers[1] = Ref(tb);
                                else if (h == P_Layer2) layers[2] = Ref(tb);
                                else if (h == P_Layer3) layers[3] = Ref(tb);
                                else if (h == P_Lookup) lookup = Px(tb, 256);
                                else if (h == P_Diffuse) diff = Ref(tb);
                            }
                            else
                            {
                                if (h == P_Diffuse || h == P_PlateBg) diff = Ref(tb);
                                else if (h == P_TintPal || h == P_DiffPal) pal = Px(tb, 4096);
                                else if (h == P_Layer0 && firstLayer == null) firstLayer = tb;
                            }
                        }
                    if (!sh.Terrain && diff == null && firstLayer != null) diff = Ref(firstLayer);
                    if (!sh.Terrain && diff == null) Interlocked.Increment(ref Stats.NoDiffuse);
                    if (sh.Trees && diff != null) cls = diff.MeanAlpha >= 0.9f ? VClass.Wood : VClass.Leaves;
                    else if (tree && cls == VClass.Opaque) cls = VClass.Wood;
                    else if (tree && cls == VClass.Cutout) cls = VClass.Leaves;
                }
                bool emissive = (sh.Flags & VFlags.Emissive) != 0 && (sh.Flags & VFlags.NightOnly) == 0;
                bool tinted = pal != null && (sh.Flags & VFlags.Tinted) != 0;
                int oTint = sh.Tint2 ? oCol1 : oCol0;
                if (oTint < 0) tinted = false;
                bool lookupOnly = sh.Terrain && sh.File.Contains("_cm");
                bool vc1Only = sh.Terrain && sh.File.StartsWith("terrain_cb_w_4lyr_lod");

                // vertices in model space
                var pos = new Vector3[count];
                var nrm = oNrm >= 0 ? new Vector3[count] : null;
                var uv = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    int b = i * stride;
                    var p = new Vector3(BitConverter.ToSingle(vb, b + oPos), BitConverter.ToSingle(vb, b + oPos + 4), BitConverter.ToSingle(vb, b + oPos + 8));
                    if (useXf) p = Vector3.TransformCoordinate(p, xf);
                    pos[i] = p;
                    if (nrm != null)
                    {
                        var n = new Vector3(BitConverter.ToSingle(vb, b + oNrm), BitConverter.ToSingle(vb, b + oNrm + 4), BitConverter.ToSingle(vb, b + oNrm + 8));
                        if (useXf) n = Vector3.TransformNormal(n, xf);
                        float len = n.Length();
                        nrm[i] = len > 1e-6f && float.IsFinite(len) ? n / len : Vector3.UnitZ;
                    }
                    if (oUv0 >= 0) { ReadUv(vb, b + oUv0, tUv0, out float u, out float v); uv[i] = new Vector2(u, v); }
                }

                // A few geometries carry garbage in their texture coordinates (values in the billions). Studio cuts
                // triangles at every texture repeat, so one such triangle would keep it busy forever.
                bool badUv = false;
                for (int i = 0; i < count && !badUv; i++) badUv = MathF.Abs(uv[i].X) > 8192f || MathF.Abs(uv[i].Y) > 8192f;
                if (badUv) { Array.Clear(uv); Interlocked.Increment(ref Stats.BadUv); }

                // winding: Studio wants counter-clockwise fronts, which is the game's own index order (only about
                // 0.1 % of the geometries with normals need a flip). Decide from the stored normals when there are
                // any; a geometry without normals (pools, fountains, some decals) keeps the game's order, so its
                // generated normals and the decal offset below point out of the surface.
                bool flip = false;
                if (nrm != null)
                {
                    double s = 0;
                    for (int t = 0; t < ntri; t++)
                    {
                        int i0 = ind[t * 3], i1 = ind[t * 3 + 1], i2 = ind[t * 3 + 2];
                        if (i0 >= count || i1 >= count || i2 >= count) continue;
                        s += Vector3.Dot(Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]), nrm[i0] + nrm[i1] + nrm[i2]);
                    }
                    flip = s < 0;
                }
                else Interlocked.Increment(ref Stats.NoNormals);
                if (flip) Interlocked.Increment(ref Stats.FlippedGeoms);

                // terrain: weight of the four layers at every vertex
                float[] w4 = null;
                if (sh.Terrain)
                {
                    w4 = new float[count * 4];
                    for (int i = 0; i < count; i++)
                    {
                        int b = i * stride;
                        float wg = 0, wb = 0;
                        if (oCol1 >= 0) { wg = vb[b + oCol1 + 1] / 255f; wb = vb[b + oCol1 + 2] / 255f; }
                        if (!vc1Only && lookup != null && oUv1 >= 0)
                        {
                            ReadUv(vb, b + oUv1, tUv1, out float lu, out float lv);
                            int px = (int)MathF.Floor((lu - MathF.Floor(lu)) * lookup.W), py = (int)MathF.Floor((lv - MathF.Floor(lv)) * lookup.H);
                            int o = (Math.Clamp(py, 0, lookup.H - 1) * lookup.W + Math.Clamp(px, 0, lookup.W - 1)) * 4;
                            float lg = lookup.Rgba[o + 1] / 255f, lb = lookup.Rgba[o + 2] / 255f;
                            float f = lookupOnly || oCol0 < 0 ? 0f : vb[b + oCol0 + 3] / 255f;
                            wg = lg * (1 - f) + wg * f; wb = lb * (1 - f) + wb * f;
                        }
                        w4[i * 4] = (1 - wb) * (1 - wg); w4[i * 4 + 1] = wb * (1 - wg); w4[i * 4 + 2] = (1 - wb) * wg; w4[i * 4 + 3] = wb * wg;
                    }
                }

                // Tinted geometry: a building uses a few palette columns (walls, roof, trim) and each gets its own
                // colour; vegetation and clutter spread their vertices over many columns for a gradient, which
                // becomes one mean colour instead of a texture copy per column.
                int[] tintHist = null;
                bool tintMerge = false;
                if (tinted)
                {
                    tintHist = new int[256];
                    for (int t = 0; t < ntri; t++) { int i0 = ind[t * 3]; if (i0 < count) tintHist[vb[i0 * stride + oTint + 2]]++; }
                    tintMerge = tintHist.Count(c => c > 0) > 4;
                }

                // Studio cuts a triangle at every texture repeat when its material is a decal or is blended (it leaves
                // opaque and alpha-tested surfaces alone). A stain stretched over thousands of repeats becomes millions
                // of pieces: one rust decal took a section from 6 M to 42 M triangles. Such a decal triangle is left
                // out (about 20 per section); a blended surface with one (a chain-link fence) is alpha tested instead.
                bool cutDecal = cls == VClass.Decal;
                bool cutBlend = (cls == VClass.Glass || cls == VClass.Alpha) && diff != null && diff.HasAlpha;
                if (cutBlend)
                {
                    bool over = false;
                    for (int t = 0; t < ntri && !over; t++)
                    {
                        int i0 = ind[t * 3], i1 = ind[t * 3 + 1], i2 = ind[t * 3 + 2];
                        over = i0 < count && i1 < count && i2 < count && RepeatTiles(uv[i0], uv[i1], uv[i2]) > MaxRepeatTiles;
                    }
                    if (over) { cls = VClass.Cutout; Interlocked.Increment(ref Stats.RepeatMasked); }
                }

                // triangles into buckets: terrain by strongest layer, tinted geometry by palette column
                var buckets = new SortedDictionary<int, Bucket>();
                for (int t = 0; t < ntri; t++)
                {
                    int i0 = ind[t * 3], i1 = ind[t * 3 + 1], i2 = ind[t * 3 + 2];
                    if (i0 >= count || i1 >= count || i2 >= count) continue;
                    if (!(Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]).LengthSquared() > 1e-14f)) continue;
                    if (cutDecal && RepeatTiles(uv[i0], uv[i1], uv[i2]) > MaxRepeatTiles) { Interlocked.Increment(ref Stats.RepeatTris); continue; }
                    int key = 0;
                    if (w4 != null)
                    {
                        float best = -1;
                        for (int l = 0; l < 4; l++)
                        {
                            if (layers[l] == null) continue;
                            float w = w4[i0 * 4 + l] + w4[i1 * 4 + l] + w4[i2 * 4 + l];
                            if (w > best) { best = w; key = l; }
                        }
                    }
                    else if (tinted && !tintMerge) key = vb[i0 * stride + oTint + 2];
                    if (!buckets.TryGetValue(key, out var bk)) buckets[key] = bk = new Bucket();
                    if (flip) { bk.Tri.Add(i0); bk.Tri.Add(i2); bk.Tri.Add(i1); }
                    else { bk.Tri.Add(i0); bk.Tri.Add(i1); bk.Tri.Add(i2); }
                }
                Interlocked.Add(ref Stats.SourceTris, ntri);

                foreach (var kv in buckets)
                {
                    var tri = kv.Value.Tri;
                    var tex = sh.Terrain ? (layers[kv.Key] ?? diff) : diff;
                    var part = new SrcPart { Class = cls, Emissive = emissive };
                    // compact the vertices this bucket uses
                    var map = new Dictionary<int, int>();
                    var idx = new int[tri.Count];
                    var order = new List<int>();
                    for (int i = 0; i < tri.Count; i++)
                    {
                        if (!map.TryGetValue(tri[i], out var ni)) { ni = order.Count; map[tri[i]] = ni; order.Add(tri[i]); }
                        idx[i] = ni;
                    }
                    int nv = order.Count;
                    part.Pos = new float[nv * 3]; part.Nrm = new float[nv * 3]; part.Uv = new float[nv * 2]; part.Idx = idx;
                    Vector3[] gn = null;
                    if (nrm == null)
                    {
                        gn = new Vector3[nv];
                        for (int i = 0; i < idx.Length; i += 3)
                        {
                            var a = pos[order[idx[i]]]; var b = pos[order[idx[i + 1]]]; var c = pos[order[idx[i + 2]]];
                            var fn = Vector3.Cross(b - a, c - a);
                            gn[idx[i]] += fn; gn[idx[i + 1]] += fn; gn[idx[i + 2]] += fn;
                        }
                    }
                    for (int i = 0; i < nv; i++)
                    {
                        int s = order[i];
                        var n = nrm != null ? nrm[s] : (gn[i].LengthSquared() > 1e-20f ? Vector3.Normalize(gn[i]) : Vector3.UnitZ);
                        var p = pos[s];
                        if (cls == VClass.Decal) p += n * 0.012f;       // keep decals off the surface they lie on
                        part.Pos[i * 3] = p.X; part.Pos[i * 3 + 1] = p.Y; part.Pos[i * 3 + 2] = p.Z;
                        part.Nrm[i * 3] = n.X; part.Nrm[i * 3 + 1] = n.Y; part.Nrm[i * 3 + 2] = n.Z;
                        part.Uv[i * 2] = uv[s].X; part.Uv[i * 2 + 1] = uv[s].Y;
                        mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                    }
                    if (cls == VClass.Water)
                        part.Material = _mats.Get(VClass.Water, _tex.RegisterFlat(0x30627A, 190), true, "water", false).Id;
                    else if (tinted && tex != null)
                    {
                        part.TintTex = tex; part.Pal = pal.Rgba; part.PalW = pal.W; part.PalH = pal.H;
                        part.TintHist = tintMerge ? tintHist : null; part.TintCol = kv.Key;
                        mesh.HasTint = true;
                    }
                    else if (tex != null)
                        part.Material = _mats.Get(cls, _tex.Register(tex, cls != VClass.Opaque && cls != VClass.Terrain && cls != VClass.Wood), tex.HasAlpha, tex.Name, emissive).Id;
                    else
                        part.Material = _mats.Get(cls == VClass.Terrain ? VClass.Terrain : VClass.Opaque, _tex.RegisterFlat(0x808080, 255), false, "grey", false).Id;
                    mesh.Parts.Add(part);
                    mesh.Tris += idx.Length / 3; mesh.Verts += nv;
                    mesh.Bytes += part.Pos.Length * 4L * 2 + part.Uv.Length * 4L + idx.Length * 4L;
                }
            }
        }
        if (mesh.Tris == 0) { mn = Vector3.Zero; mx = Vector3.Zero; }
        mesh.Min = mn; mesh.Max = mx;
        Interlocked.Add(ref Stats.OutTris, mesh.Tris);
        return mesh;
    }

    /// <summary>Most texture tiles one triangle of a decal or blended material may lie over.</summary>
    const float MaxRepeatTiles = 256f;

    /// <summary>Number of texture tiles (unit squares of uv) the triangle's uv box touches, as Studio counts them.</summary>
    static float RepeatTiles(Vector2 a, Vector2 b, Vector2 c)
    {
        const float eps = 1e-5f;
        float u0 = MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) + eps), u1 = MathF.Max(MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) - eps), u0);
        float v0 = MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) + eps), v1 = MathF.Max(MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - eps), v0);
        return (u1 - u0 + 1) * (v1 - v0 + 1);
    }

    /// <summary>Material of a part for an entity with tint row <paramref name="row"/>.</summary>
    public int MaterialOf(SrcPart p, uint row)
    {
        if (p.Material >= 0) return p.Material;
        int y = (int)Math.Min(row, (uint)(p.PalH - 1));
        int r, g, b;
        if (p.TintHist != null)
        {
            long sr = 0, sg = 0, sb = 0, n = 0;
            for (int c = 0; c < 256 && c < p.PalW; c++)
            {
                int wgt = p.TintHist[c];
                if (wgt == 0) continue;
                int oc = (y * p.PalW + c) * 4;
                sr += (long)p.Pal[oc] * wgt; sg += (long)p.Pal[oc + 1] * wgt; sb += (long)p.Pal[oc + 2] * wgt; n += wgt;
            }
            if (n == 0) n = 1;
            r = (int)(sr / n); g = (int)(sg / n); b = (int)(sb / n);
        }
        else
        {
            int o = (y * p.PalW + Math.Clamp(p.TintCol, 0, p.PalW - 1)) * 4;
            r = p.Pal[o]; g = p.Pal[o + 1]; b = p.Pal[o + 2];
        }
        // 4 bits per channel keep the number of tinted texture copies down; a near-white tint is no tint
        uint col = (uint)((r & 0xF0 | 8) << 16 | (g & 0xF0 | 8) << 8 | (b & 0xF0 | 8));
        if (r >= 0xE0 && g >= 0xE0 && b >= 0xE0) col = 0xFFFFFF;
        bool wantAlpha = p.Class != VClass.Opaque && p.Class != VClass.Terrain && p.Class != VClass.Wood;
        int tex = _tex.Register(p.TintTex, wantAlpha, col);
        // no tint: the same label as the untinted material of this texture, so the two stay one material
        string label = col == 0xFFFFFF ? p.TintTex.Name : p.TintTex.Name + "_t" + col.ToString("x6");
        return _mats.Get(p.Class, tex, p.TintTex.HasAlpha, label, p.Emissive).Id;
    }
}
