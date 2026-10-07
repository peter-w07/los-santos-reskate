using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

public sealed class FlattenStats
{
    public int Groups, Triangles, DegenerateTriangles, BadIndexPolys, Boxes, Spheres, Capsules, Cylinders;
    public int StandaloneBounds, ClothSkipped, UnknownBounds, NonRigidTransforms, NonCompositeRoot, NullChildren;
    public int PrimTriangles;
}

/// <summary>
/// Turns one YBN (static world bounds) into a flat world-space LSCT mesh: one group per composite child,
/// real triangles first, then primitives (kept analytically AND tessellated to triangles).
/// </summary>
public sealed class YbnFlattener
{
    public const int TessellationVersion = 2; // bump when tessellation or flattening rules change (invalidates caches)

    readonly List<GroupRec> _groups = new();
    readonly List<V3> _verts = new();
    readonly List<uint> _tris = new();
    readonly List<TriAttr> _attrs = new();
    readonly List<PrimRec> _prims = new();
    public readonly FlattenStats Stats = new();
    byte _layer;

    public static Mesh Flatten(Bounds root, byte layer, uint nameHash, out FlattenStats stats)
        => Flatten(root, layer, nameHash, Matrix.Identity, out stats);

    /// <summary>
    /// As above, with a rigid transform applied to the whole file (row vectors: v * rootXf). Interior (MLO)
    /// bounds are stored in interior-local space and are placed with the transform of each interior instance.
    /// </summary>
    public static Mesh Flatten(Bounds root, byte layer, uint nameHash, Matrix rootXf, out FlattenStats stats)
    {
        var f = new YbnFlattener { _layer = layer };
        if (root is BoundComposite comp)
        {
            f.AddComposite(comp, rootXf);
        }
        else if (root != null)
        {
            f.Stats.NonCompositeRoot++;
            f.AddLeaf(root, rootXf, 0xFFFF, 0, 0);
        }
        stats = f.Stats;
        if (f._groups.Count > ushort.MaxValue) throw new InvalidDataException("more than 65535 groups in one ybn");
        var m = new Mesh
        {
            Groups = f._groups.ToArray(),
            Verts = f._verts.ToArray(),
            Tris = f._tris.ToArray(),
            Attrs = f._attrs.ToArray(),
            Prims = f._prims.ToArray(),
        };
        m.Header.Kind = 1;
        m.Header.TileX = unchecked((int)nameHash);
        m.RecomputeBounds();
        return m;
    }

    void AddComposite(BoundComposite comp, Matrix parent)
    {
        var kids = comp.Children?.data_items;
        if (kids == null) return;
        for (int i = 0; i < kids.Length; i++)
        {
            var c = kids[i];
            if (c == null) { Stats.NullChildren++; continue; }
            // SharpDX row vectors: v * child * parent
            var xf = c.Transform * parent;
            if (c is BoundComposite nested) { AddComposite(nested, xf); continue; }
            AddLeaf(c, xf, (ushort)Math.Min(i, 0xFFFE), (uint)c.CompositeFlags1.Flags1, (uint)c.CompositeFlags1.Flags2);
        }
    }

    void AddLeaf(Bounds b, Matrix xf, ushort child, uint typeFlags, uint includeFlags)
    {
        if (b is BoundCloth) { Stats.ClothSkipped++; return; }

        var sv = xf.ScaleVector;
        if (Math.Abs(sv.X - 1f) > 1e-3f || Math.Abs(sv.Y - 1f) > 1e-3f || Math.Abs(sv.Z - 1f) > 1e-3f) Stats.NonRigidTransforms++;

        var g = new GroupRec
        {
            Child = child,
            BoundType = (byte)b.Type,
            Info = (byte)(_layer & 3),
            TypeFlags = typeFlags,
            IncludeFlags = includeFlags,
            FirstTri = (uint)_attrs.Count,
            FirstPrim = (uint)_prims.Count,
        };
        ushort gi = (ushort)_groups.Count;
        int firstVert = _verts.Count;

        switch (b)
        {
            case BoundGeometry geom: AddGeometry(geom, ref xf, gi); break;
            case BoundBox box:
            {
                var c = (box.BoxMin + box.BoxMax) * 0.5f;
                var h = (box.BoxMax - box.BoxMin) * 0.5f;
                var attr = LeafAttr(b, gi, 3);
                AddBox(Vector3.TransformCoordinate(c, xf),
                    Vector3.TransformNormal(new Vector3(h.X, 0, 0), xf),
                    Vector3.TransformNormal(new Vector3(0, h.Y, 0), xf),
                    Vector3.TransformNormal(new Vector3(0, 0, h.Z), xf), attr);
                Stats.StandaloneBounds++;
                break;
            }
            case BoundSphere sph:
            {
                AddSphere(Vector3.TransformCoordinate(sph.SphereCenter, xf), sph.SphereRadius, LeafAttr(b, gi, 1));
                Stats.StandaloneBounds++;
                break;
            }
            case BoundCapsule cap:
            {
                var ext = new Vector3(0, Math.Max(0f, cap.SphereRadius - cap.Margin), 0);
                AddCapsule(Vector3.TransformCoordinate(cap.SphereCenter - ext, xf), Vector3.TransformCoordinate(cap.SphereCenter + ext, xf), cap.Margin, LeafAttr(b, gi, 2));
                Stats.StandaloneBounds++;
                break;
            }
            case BoundCylinder cyl:
            {
                var e = (cyl.BoxMax - cyl.BoxMin);
                var half = new Vector3(0, Math.Abs(e.Y) * 0.5f, 0);
                AddCylinder(Vector3.TransformCoordinate(cyl.SphereCenter - half, xf), Vector3.TransformCoordinate(cyl.SphereCenter + half, xf), Math.Abs(e.X) * 0.5f, LeafAttr(b, gi, 4));
                Stats.StandaloneBounds++;
                break;
            }
            case BoundDisc disc:
            {
                var half = new Vector3(disc.Margin, 0, 0);
                AddCylinder(Vector3.TransformCoordinate(disc.SphereCenter - half, xf), Vector3.TransformCoordinate(disc.SphereCenter + half, xf), disc.SphereRadius, LeafAttr(b, gi, 4));
                Stats.StandaloneBounds++;
                break;
            }
            default:
                Stats.UnknownBounds++;
                return;
        }

        g.NTris = (uint)_attrs.Count - g.FirstTri;
        g.NPrims = (uint)_prims.Count - g.FirstPrim;
        if (g.NTris == 0 && g.NPrims == 0)
        {
            _verts.RemoveRange(firstVert, _verts.Count - firstVert);
            return; // empty child: no group emitted
        }
        var mn = new V3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new V3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = firstVert; i < _verts.Count; i++)
        {
            var v = _verts[i];
            if (v.X < mn.X) mn.X = v.X; if (v.Y < mn.Y) mn.Y = v.Y; if (v.Z < mn.Z) mn.Z = v.Z;
            if (v.X > mx.X) mx.X = v.X; if (v.Y > mx.Y) mx.Y = v.Y; if (v.Z > mx.Z) mx.Z = v.Z;
        }
        g.Min = mn; g.Max = mx;
        _groups.Add(g);
        Stats.Groups++;
    }

    static TriAttr LeafAttr(Bounds b, ushort group, byte kind) => new()
    {
        Material = b.MaterialIndex,
        Kind = kind,
        MatFlags = b.PolyFlags,
        Group = group,
        ProcId = b.ProceduralId,
        RoomPed = b.RoomId_and_PedDensity,
    };

    static TriAttr PolyAttr(BoundMaterial_s m, ushort group, byte kind) => new()
    {
        Material = m.Type.Index,
        Kind = kind,
        MatFlags = (ushort)m.Flags,
        Group = group,
        ProcId = m.ProceduralId,
        RoomPed = (byte)((m.RoomId & 0x1F) | ((m.PedDensity & 7) << 5)),
    };

    void AddGeometry(BoundGeometry g, ref Matrix xf, ushort gi)
    {
        var polys = g.Polygons;
        var src = g.Vertices;
        if (polys == null || src == null) return;

        // world positions of the source vertices
        var world = new Vector3[src.Length];
        var cen = g.CenterGeom;
        bool ident = xf.IsIdentity;
        for (int i = 0; i < src.Length; i++)
        {
            var v = src[i] + cen;
            world[i] = ident ? v : Vector3.TransformCoordinate(v, xf);
        }

        // pass 1: real triangles, with compacted vertices (only those triangles use)
        var remap = new int[src.Length];
        Array.Fill(remap, -1);
        int n = src.Length;
        for (int i = 0; i < polys.Length; i++)
        {
            if (polys[i] is not BoundPolygonTriangle t) continue;
            int a = t.vertIndex1, b = t.vertIndex2, c = t.vertIndex3;
            if (a >= n || b >= n || c >= n) { Stats.BadIndexPolys++; continue; }
            if (a == b || b == c || a == c) { Stats.DegenerateTriangles++; continue; }
            _tris.Add(Map(a, remap, world));
            _tris.Add(Map(b, remap, world));
            _tris.Add(Map(c, remap, world));
            _attrs.Add(PolyAttr(g.GetMaterial(i), gi, 0));
            Stats.Triangles++;
        }

        // pass 2: primitives
        for (int i = 0; i < polys.Length; i++)
        {
            var p = polys[i];
            switch (p)
            {
                case BoundPolygonSphere s:
                    if (s.sphereIndex >= n) { Stats.BadIndexPolys++; break; }
                    AddSphere(world[s.sphereIndex], s.sphereRadius, PolyAttr(g.GetMaterial(i), gi, 1));
                    break;
                case BoundPolygonCapsule c:
                    if (c.capsuleIndex1 >= n || c.capsuleIndex2 >= n) { Stats.BadIndexPolys++; break; }
                    AddCapsule(world[c.capsuleIndex1], world[c.capsuleIndex2], c.capsuleRadius, PolyAttr(g.GetMaterial(i), gi, 2));
                    break;
                case BoundPolygonBox b:
                {
                    int i1 = b.boxIndex1, i2 = b.boxIndex2, i3 = b.boxIndex3, i4 = b.boxIndex4;
                    if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i1 >= n || i2 >= n || i3 >= n || i4 >= n) { Stats.BadIndexPolys++; break; }
                    Vector3 p1 = world[i1], p2 = world[i2], p3 = world[i3], p4 = world[i4];
                    // the four vertices are alternating corners of the box (an inscribed tetrahedron)
                    var cenb = (p1 + p2 + p3 + p4) * 0.25f;
                    var ax = ((p2 + p3) - (p1 + p4)) * 0.25f;
                    var ay = ((p2 + p4) - (p1 + p3)) * 0.25f;
                    var az = ((p3 + p4) - (p1 + p2)) * 0.25f;
                    AddBox(cenb, ax, ay, az, PolyAttr(g.GetMaterial(i), gi, 3));
                    break;
                }
                case BoundPolygonCylinder c:
                    if (c.cylinderIndex1 >= n || c.cylinderIndex2 >= n) { Stats.BadIndexPolys++; break; }
                    AddCylinder(world[c.cylinderIndex1], world[c.cylinderIndex2], c.cylinderRadius, PolyAttr(g.GetMaterial(i), gi, 4));
                    break;
            }
        }
    }

    uint Map(int i, int[] remap, Vector3[] world)
    {
        int r = remap[i];
        if (r < 0)
        {
            r = _verts.Count;
            remap[i] = r;
            _verts.Add(new V3(world[i]));
        }
        return (uint)r;
    }

    // ---------------------------------------------------------------- primitives

    uint V(Vector3 v) { _verts.Add(new V3(v)); return (uint)(_verts.Count - 1); }

    void T(uint a, uint b, uint c, TriAttr attr)
    {
        _tris.Add(a); _tris.Add(b); _tris.Add(c);
        _attrs.Add(attr);
        Stats.PrimTriangles++;
    }

    unsafe void EndPrim(byte kind, TriAttr attr, uint firstTri, ReadOnlySpan<float> p)
    {
        var r = new PrimRec
        {
            Kind = kind, Material = attr.Material, MatFlags = attr.MatFlags, Group = attr.Group, ProcId = attr.ProcId, RoomPed = attr.RoomPed,
            FirstTri = firstTri, NTris = (uint)_attrs.Count - firstTri,
        };
        for (int i = 0; i < p.Length && i < 12; i++) r.P[i] = p[i];
        _prims.Add(r);
    }

    // Round primitives are mostly thin (poles, rails, branches: median capsule radius is under 0.2 m), so the
    // tessellation is deliberately coarse for small radii; the exact shape is always available in the prim table.
    static int Segments(float r) => r <= 0.25f ? 4 : r <= 0.5f ? 6 : r <= 2f ? 8 : r <= 8f ? 12 : 16;

    static void Basis(Vector3 axis, out Vector3 u, out Vector3 v)
    {
        var helper = Math.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(helper, axis));
        v = Vector3.Cross(axis, u);
    }

    void AddBox(Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, TriAttr attr)
    {
        uint first = (uint)_attrs.Count;
        // make the half-axis frame right handed so the winding below faces outwards
        if (Vector3.Dot(Vector3.Cross(ax, ay), az) < 0) az = -az;
        Span<uint> k = stackalloc uint[8];
        for (int i = 0; i < 8; i++)
        {
            var p = c + ((i & 1) != 0 ? ax : -ax) + ((i & 2) != 0 ? ay : -ay) + ((i & 4) != 0 ? az : -az);
            k[i] = V(p);
        }
        // corner bit order: x=1, y=2, z=4
        T(k[0], k[2], k[3], attr); T(k[0], k[3], k[1], attr); // -z
        T(k[4], k[5], k[7], attr); T(k[4], k[7], k[6], attr); // +z
        T(k[0], k[1], k[5], attr); T(k[0], k[5], k[4], attr); // -y
        T(k[2], k[6], k[7], attr); T(k[2], k[7], k[3], attr); // +y
        T(k[0], k[4], k[6], attr); T(k[0], k[6], k[2], attr); // -x
        T(k[1], k[3], k[7], attr); T(k[1], k[7], k[5], attr); // +x
        Stats.Boxes++;
        Span<float> p12 = stackalloc float[12] { c.X, c.Y, c.Z, ax.X, ax.Y, ax.Z, ay.X, ay.Y, ay.Z, az.X, az.Y, az.Z };
        EndPrim(3, attr, first, p12);
    }

    /// <summary>Ring of S vertices around axis at centre c (radius r in the u/v plane, offset along axis by h).</summary>
    uint Ring(Vector3 c, Vector3 u, Vector3 v, float r, int S)
    {
        uint first = (uint)_verts.Count;
        for (int i = 0; i < S; i++)
        {
            double a = 2.0 * Math.PI * i / S;
            V(c + u * (float)(Math.Cos(a) * r) + v * (float)(Math.Sin(a) * r));
        }
        return first;
    }

    /// <summary>Quad band between two rings; ring b lies further along +axis than ring a.</summary>
    void Band(uint a, uint b, int S, TriAttr attr)
    {
        for (int i = 0; i < S; i++)
        {
            uint i0 = (uint)i, i1 = (uint)((i + 1) % S);
            T(a + i0, a + i1, b + i1, attr);
            T(a + i0, b + i1, b + i0, attr);
        }
    }

    void AddSphere(Vector3 c, float r, TriAttr attr)
    {
        uint first = (uint)_attrs.Count;
        r = Math.Abs(r);
        int S = Segments(r), R = Math.Max(2, S / 2);
        var axis = Vector3.UnitZ; Basis(axis, out var u, out var v);
        uint bottom = V(c - axis * r);
        uint prev = 0;
        for (int j = 1; j < R; j++)
        {
            double phi = -Math.PI / 2 + Math.PI * j / R;
            uint ring = Ring(c + axis * (float)(Math.Sin(phi) * r), u, v, (float)(Math.Cos(phi) * r), S);
            if (j == 1) for (int i = 0; i < S; i++) T(bottom, ring + (uint)((i + 1) % S), ring + (uint)i, attr);
            else Band(prev, ring, S, attr);
            prev = ring;
        }
        uint top = V(c + axis * r);
        for (int i = 0; i < S; i++) T(top, prev + (uint)i, prev + (uint)((i + 1) % S), attr);
        Stats.Spheres++;
        Span<float> p = stackalloc float[4] { c.X, c.Y, c.Z, r };
        EndPrim(1, attr, first, p);
    }

    void AddCylinder(Vector3 a, Vector3 b, float r, TriAttr attr)
    {
        uint first = (uint)_attrs.Count;
        r = Math.Abs(r);
        var d = b - a;
        float len = d.Length();
        var axis = len > 1e-6f ? d / len : Vector3.UnitZ;
        Basis(axis, out var u, out var v);
        int S = Segments(r);
        uint ra = Ring(a, u, v, r, S), rb = Ring(b, u, v, r, S);
        Band(ra, rb, S, attr);
        for (int i = 1; i < S - 1; i++)
        {
            T(ra, ra + (uint)(i + 1), ra + (uint)i, attr); // cap at a faces -axis
            T(rb, rb + (uint)i, rb + (uint)(i + 1), attr); // cap at b faces +axis
        }
        Stats.Cylinders++;
        Span<float> p = stackalloc float[7] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r };
        EndPrim(4, attr, first, p);
    }

    void AddCapsule(Vector3 a, Vector3 b, float r, TriAttr attr)
    {
        uint first = (uint)_attrs.Count;
        r = Math.Abs(r);
        var d = b - a;
        float len = d.Length();
        var axis = len > 1e-6f ? d / len : Vector3.UnitZ;
        Basis(axis, out var u, out var v);
        int S = Segments(r), H = r <= 1f ? 1 : Math.Max(2, S / 4);

        // hemisphere at a (towards -axis), from pole up to the equator ring at a
        uint pa = V(a - axis * r);
        uint prev = 0;
        for (int j = 1; j <= H; j++)
        {
            double phi = -Math.PI / 2 + (Math.PI / 2) * j / H;
            uint ring = Ring(a + axis * (float)(Math.Sin(phi) * r), u, v, (float)(Math.Cos(phi) * r), S);
            if (j == 1) for (int i = 0; i < S; i++) T(pa, ring + (uint)((i + 1) % S), ring + (uint)i, attr);
            else Band(prev, ring, S, attr);
            prev = ring;
        }
        // side
        uint rb = Ring(b, u, v, r, S);
        Band(prev, rb, S, attr);
        prev = rb;
        // hemisphere at b
        for (int j = 1; j < H; j++)
        {
            double phi = (Math.PI / 2) * j / H;
            uint ring = Ring(b + axis * (float)(Math.Sin(phi) * r), u, v, (float)(Math.Cos(phi) * r), S);
            Band(prev, ring, S, attr);
            prev = ring;
        }
        uint pb = V(b + axis * r);
        for (int i = 0; i < S; i++) T(pb, prev + (uint)i, prev + (uint)((i + 1) % S), attr);
        Stats.Capsules++;
        Span<float> p = stackalloc float[7] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r };
        EndPrim(2, attr, first, p);
    }
}
