using System.Buffers.Binary;
using System.Collections.Concurrent;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace GtaSkate;

/// <summary>A decoded texture level: RGBA8, row 0 at the top.</summary>
public sealed class Pixels
{
    public int W, H;
    public byte[] Rgba;
}

/// <summary>A texture of the game with what the mesh builder needs to know about it.</summary>
public sealed class TexRef
{
    public Texture Tex;
    public ulong Key;            // identity of the source texture (dictionary + name, or embedded)
    public string Name = "";
    public bool HasAlpha;        // some texel is visibly transparent
    public float MeanAlpha = 1f;
}

public sealed class OutTexture
{
    public int Id;                 // index in TexLib.All; follows thread timing
    public ulong Content;          // hash of what the file holds: the same in every run
    public string Name, File, Format;
    public int W, H;
    public bool Alpha;
    public long Bytes;
    public uint Mean = 0x808080;   // mean colour 0xRRGGBB (alpha weighted), for the pause-map picture
    public bool Removed;           // no written mesh uses it: its file was deleted again (TexLib.Prune)
}

/// <summary>
/// Texture dictionaries of the game (loaded on demand under a byte budget) and the textures written for
/// ReSkate Studio: the game's own DXT data from the mip level that fits the size cap as a .dds (Studio takes
/// .dds and .png as they are), or a .png when the pixels had to change (tint) or the format is not DXT.
/// </summary>
public sealed class TexLib
{
    readonly Game _game;
    readonly long _budget;
    readonly string _outDir;
    public readonly int Cap;
    public bool PngOnly;         // portable model: no .dds, every texture a .png

    sealed class Slot
    {
        public RpfFileEntry Entry;
        public readonly object Gate = new();
        public YtdFile File;
        public long Bytes, LastUse;
        public HashSet<uint> Names;
        public bool Failed;
    }

    readonly ConcurrentDictionary<uint, Slot> _slots = new();
    long _loadedBytes, _clock;
    readonly object _evictGate = new();

    readonly ConcurrentDictionary<ulong, (bool hasAlpha, float mean)> _probe = new();
    readonly ConcurrentDictionary<(ulong key, bool alpha, uint tint), Lazy<int>> _byKey = new();
    readonly ConcurrentDictionary<ulong, int> _byContent = new();
    readonly List<OutTexture> _out = new();
    readonly object _outGate = new();
    public long Missing, Unsupported, Refs;
    public readonly ConcurrentDictionary<string, int> MissingNames = new();

    public TexLib(Game game, long budgetBytes, string outDir, int cap)
    {
        _game = game; _budget = budgetBytes; _outDir = outDir; Cap = cap;
        Directory.CreateDirectory(outDir);
    }

    public OutTexture[] All { get { lock (_outGate) return _out.ToArray(); } }

    public uint MeanOf(int id) { lock (_outGate) return id >= 0 && id < _out.Count ? _out[id].Mean : 0x808080u; }

    /// <summary>Content hash of an output texture (0: none). Unlike the id it does not depend on thread timing.</summary>
    public ulong ContentOf(int id) { lock (_outGate) return id >= 0 && id < _out.Count ? _out[id].Content : 0UL; }

    /// <summary>
    /// Call once, after the last Register and before All is read for the output: gives a file whose pixels
    /// arrived under several names its final name (the alphabetically first one, see Add).
    /// </summary>
    public void Finish()
    {
        lock (_outGate)
            foreach (var t in _out)
            {
                string file = $"{t.Name}_{(uint)(t.Content ^ (t.Content >> 32)):x8}{Path.GetExtension(t.File)}";
                if (file == t.File) continue;
                string from = Path.Combine(_outDir, t.File);
                if (System.IO.File.Exists(from)) System.IO.File.Move(from, Path.Combine(_outDir, file), true);
                t.File = file;
            }
    }

    /// <summary>
    /// Call after Finish: deletes the files of the textures whose id is not in <paramref name="used"/> and marks
    /// them Removed (they keep their place in All, which the materials index). A texture is written when its
    /// drawable is built, which is before it is known whether any of the geometry that shows it is kept.
    /// Returns how many were removed.
    /// </summary>
    public int Prune(HashSet<int> used)
    {
        int n = 0;
        lock (_outGate)
            foreach (var t in _out)
            {
                if (t.Removed || used.Contains(t.Id)) continue;
                System.IO.File.Delete(Path.Combine(_outDir, t.File));
                t.Removed = true; n++;
            }
        return n;
    }

    static uint MeanColor(byte[] rgba)
    {
        double r = 0, g = 0, b = 0, wsum = 0;
        for (int o = 0; o + 3 < rgba.Length; o += 4)
        {
            double a = rgba[o + 3] / 255.0;
            r += rgba[o] * a; g += rgba[o + 1] * a; b += rgba[o + 2] * a; wsum += a;
        }
        if (wsum < 1e-6) return 0x808080;
        return (uint)((int)(r / wsum) << 16 | (int)(g / wsum) << 8 | (int)(b / wsum));
    }

    // ---------------------------------------------------------------- dictionaries

    Slot GetSlot(uint hash) => _slots.GetOrAdd(hash, h =>
    {
        _game.Gfc.YtdDict.TryGetValue(h, out var e);
        return new Slot { Entry = e, Failed = e == null };
    });

    YtdFile Ensure(Slot s)
    {
        if (s.Failed) return null;
        s.LastUse = Interlocked.Increment(ref _clock);
        var f = s.File;
        if (f != null) return f;
        lock (s.Gate)
        {
            if (s.File != null) return s.File;
            if (s.Failed) return null;
            YtdFile ytd = null;
            try { ytd = _game.Load<YtdFile>(s.Entry); } catch (Exception) { }
            if (ytd?.TextureDict == null) { s.Failed = true; return null; }
            if (s.Names == null)
            {
                var names = new HashSet<uint>();
                var hashes = ytd.TextureDict.TextureNameHashes?.data_items;
                if (hashes != null) foreach (var h in hashes) names.Add(h);
                s.Names = names;
            }
            s.Bytes = ytd.TextureDict.MemoryUsage;
            s.File = ytd;
            if (Interlocked.Add(ref _loadedBytes, s.Bytes) > _budget) Evict();
            return ytd;
        }
    }

    void Evict()
    {
        lock (_evictGate)
        {
            if (Interlocked.Read(ref _loadedBytes) <= _budget) return;
            foreach (var s in _slots.Values.Where(x => x.File != null).OrderBy(x => x.LastUse))
            {
                if (Interlocked.Read(ref _loadedBytes) <= _budget * 3 / 4) break;
                s.File = null;
                Interlocked.Add(ref _loadedBytes, -s.Bytes);
            }
        }
    }

    /// <summary>Texture dictionary chain of an archetype: its own txd, then the gtxd parents.</summary>
    public uint[] Chain(uint txd)
    {
        var l = new List<uint>(4);
        uint h = txd; int guard = 0;
        while (h != 0 && guard++ < 16 && !l.Contains(h)) { l.Add(h); h = _game.Gfc.TryGetParentYtdHash(h); }
        return l.ToArray();
    }

    /// <summary>Texture reference of a shader -> the texture with pixel data.</summary>
    public TexRef Find(TextureBase tb, uint[] chain, DrawableBase d, uint drawableHash)
    {
        if (tb == null) return null;
        Interlocked.Increment(ref Refs);
        Texture tex = null; ulong key = 0;
        var t = tb as Texture;
        if (t?.Data?.FullData == null) t = d.ShaderGroup?.TextureDictionary?.Lookup(tb.NameHash);
        if (t?.Data?.FullData != null)
        {
            tex = t;
            key = 0x8000000000000000UL | ((ulong)(drawableHash & 0x7FFFFFFF) << 32) | tb.NameHash;
        }
        else
            foreach (var h in chain)
            {
                var s = GetSlot(h);
                if (s.Failed) continue;
                if (s.Names == null) Ensure(s);
                if (s.Names == null || !s.Names.Contains(tb.NameHash)) continue;
                var found = Ensure(s)?.TextureDict?.Lookup(tb.NameHash);
                if (found?.Data?.FullData == null) continue;
                tex = found; key = ((ulong)h << 32) | tb.NameHash;
                break;
            }
        if (tex == null)
        {
            Interlocked.Increment(ref Missing);
            if (MissingNames.Count < 400) MissingNames.AddOrUpdate(tb.Name ?? ("hash_" + tb.NameHash.ToString("x8")), 1, (_, c) => c + 1);
            return null;
        }
        var pr = _probe.GetOrAdd(key, _ =>
        {
            var p = Decode(tex, 64);
            if (p == null) return (false, 1f);
            long asum = 0; int transparent = 0, n = p.W * p.H;
            for (int i = 0; i < n; i++) { int a = p.Rgba[i * 4 + 3]; asum += a; if (a < 250) transparent++; }
            return (transparent > Math.Max(1, n / 200), asum / (255f * n));
        });
        return new TexRef { Tex = tex, Key = key, Name = (tex.Name ?? "tex").ToLowerInvariant(), HasAlpha = pr.hasAlpha, MeanAlpha = pr.mean };
    }

    // ---------------------------------------------------------------- pixels

    static int MipFor(Texture tex, int cap, out int w, out int h)
    {
        w = tex.Width; h = tex.Height;
        int mip = 0, levels = Math.Max(1, (int)tex.Levels);
        while (mip < levels - 1 && Math.Max(w, h) > cap) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); mip++; }
        return mip;
    }

    /// <summary>The game's own mip level whose larger side is at most <paramref name="cap"/> pixels, as RGBA8.</summary>
    public Pixels Decode(Texture tex, int cap)
    {
        if (tex == null) return null;
        int mip = MipFor(tex, cap, out int w, out int h);
        byte[] px;
        try { px = DDSIO.GetPixels(tex, mip); } catch (Exception) { px = null; }
        if (px == null || px.Length < w * h * 4) { Interlocked.Increment(ref Unsupported); return null; }
        var rgba = new byte[w * h * 4];
        for (int o = 0; o < rgba.Length; o += 4) { rgba[o] = px[o + 2]; rgba[o + 1] = px[o + 1]; rgba[o + 2] = px[o]; rgba[o + 3] = px[o + 3]; }   // BGRA -> RGBA
        return new Pixels { W = w, H = h, Rgba = rgba };
    }

    static readonly float[] ToLin = MakeToLin();
    static float[] MakeToLin()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) { float c = i / 255f; t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
        return t;
    }
    static byte FromLin(float v)
    {
        v = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    }

    /// <summary>
    /// Texture id of <paramref name="r"/> in the output, written on first use. <paramref name="tint"/> (0xRRGGBB,
    /// 0xFFFFFF = none) is multiplied in, in linear light, the way the game's tint shaders do it.
    /// </summary>
    public int Register(TexRef r, bool alpha, uint tint = 0xFFFFFF)
    {
        if (r == null) return -1;
        alpha = alpha && r.HasAlpha;
        return _byKey.GetOrAdd((r.Key, alpha, tint), _ => new Lazy<int>(() => Write(r, alpha, tint))).Value;
    }

    /// <summary>A 4 x 4 texture of one colour, for surfaces without a texture.</summary>
    public int RegisterFlat(uint rgb, byte a)
    {
        ulong key = 0x4000000000000000UL | ((ulong)a << 24) | rgb;
        return _byKey.GetOrAdd((key, a < 255, 0xFFFFFF), _ => new Lazy<int>(() =>
        {
            var px = new byte[4 * 4 * 4];
            for (int o = 0; o < px.Length; o += 4) { px[o] = (byte)(rgb >> 16); px[o + 1] = (byte)(rgb >> 8); px[o + 2] = (byte)rgb; px[o + 3] = a; }
            return Add($"flat_{rgb:x6}_{a:x2}", 4, 4, a < 255, key, "png", (path) => Png.Write(path, 4, 4, px, a < 255), ".png", rgb);
        })).Value;
    }

    int Add(string name, int w, int h, bool alpha, ulong content, string format, Action<string> write, string ext, uint mean)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
        if (safe.Length > 48) safe = safe.Substring(0, 48);
        OutTexture t;
        lock (_outGate)
        {
            if (_byContent.TryGetValue(content, out var known))
            {
                // the same pixels under another name: the alphabetically first name wins (Finish renames the file),
                // not the name of whichever thread got here first
                if (string.CompareOrdinal(safe, _out[known].Name) < 0) _out[known].Name = safe;
                return known;
            }
            t = new OutTexture { Id = _out.Count, Content = content, Name = safe, W = w, H = h, Alpha = alpha, Format = format, Mean = mean };
            t.File = $"{safe}_{(uint)(content ^ (content >> 32)):x8}{ext}";
            _out.Add(t);
            _byContent[content] = t.Id;
        }
        var path = Path.Combine(_outDir, t.File);
        write(path);
        t.Bytes = new FileInfo(path).Length;
        return t.Id;
    }

    int Write(TexRef r, bool alpha, uint tint)
    {
        var tex = r.Tex;
        int mip = MipFor(tex, Cap, out int w, out int h);
        string four = tex.Format switch
        {
            TextureFormat.D3DFMT_DXT1 => "DXT1", TextureFormat.D3DFMT_DXT3 => "DXT3", TextureFormat.D3DFMT_DXT5 => "DXT5", _ => null,
        };
        if (!PngOnly && tint == 0xFFFFFF && four != null && w >= 4 && h >= 4)
        {
            int block = four == "DXT1" ? 8 : 16;
            var data = tex.Data.FullData;
            long off = 0; int lw = tex.Width, lh = tex.Height;
            for (int l = 0; l < mip; l++) { off += Math.Max(1, (lw + 3) / 4) * (long)Math.Max(1, (lh + 3) / 4) * block; lw = Math.Max(1, lw >> 1); lh = Math.Max(1, lh >> 1); }
            int top = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
            // keep the levels that are really in the data, down to 4 px
            int levels = 0; long len = 0; lw = w; lh = h;
            int have = Math.Max(1, (int)tex.Levels) - mip;
            while (levels < have && lw >= 4 && lh >= 4)
            {
                long sz = Math.Max(1, (lw + 3) / 4) * (long)Math.Max(1, (lh + 3) / 4) * block;
                if (off + len + sz > data.Length) break;
                len += sz; levels++; lw >>= 1; lh >>= 1;
            }
            if (levels > 0)
            {
                var hh = Hash64.Create();
                hh.Add(w); hh.Add(h); hh.Add(four); hh.Add(alpha ? 1 : 0);
                hh.AddBytes(data.AsSpan((int)off, (int)len));      // every level that is written, so the file is a function of the hash
                int lv = levels; long o0 = off, ln = len;
                var small = Decode(tex, 32);
                uint mean0 = small != null ? MeanColor(small.Rgba) : 0x808080u;
                return Add(r.Name, w, h, alpha, hh.Value, four, path => Paths.AtomicWrite(path, s =>
                {
                    Span<byte> hd = stackalloc byte[128];
                    hd.Clear();
                    hd[0] = (byte)'D'; hd[1] = (byte)'D'; hd[2] = (byte)'S'; hd[3] = (byte)' ';
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(4), 124);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(8), 0x000A1007);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(12), (uint)h);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(16), (uint)w);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(20), (uint)top);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(28), (uint)lv);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(76), 32);
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(80), 4);
                    hd[84] = (byte)four[0]; hd[85] = (byte)four[1]; hd[86] = (byte)four[2]; hd[87] = (byte)four[3];
                    BinaryPrimitives.WriteUInt32LittleEndian(hd.Slice(108), lv > 1 ? 0x00401008u : 0x00001000u);
                    s.Write(hd);
                    s.Write(data, (int)o0, (int)ln);
                }), ".dds", mean0);
            }
        }

        var p = Decode(tex, Cap);
        if (p == null) return -1;
        var rgba = p.Rgba;
        if (tint != 0xFFFFFF)
        {
            float tr = ToLin[(tint >> 16) & 255], tg = ToLin[(tint >> 8) & 255], tb = ToLin[tint & 255];
            var lr = new byte[256]; var lg = new byte[256]; var lb = new byte[256];
            for (int i = 0; i < 256; i++) { lr[i] = FromLin(ToLin[i] * tr); lg[i] = FromLin(ToLin[i] * tg); lb[i] = FromLin(ToLin[i] * tb); }
            for (int o = 0; o < rgba.Length; o += 4) { rgba[o] = lr[rgba[o]]; rgba[o + 1] = lg[rgba[o + 1]]; rgba[o + 2] = lb[rgba[o + 2]]; }
        }
        var ch = Hash64.Create();
        ch.Add(p.W); ch.Add(p.H); ch.Add(alpha ? 1 : 0);
        if (alpha) ch.AddBytes(rgba);
        else for (int o = 0; o < rgba.Length; o += 4) ch.Add((uint)(rgba[o] | rgba[o + 1] << 8 | rgba[o + 2] << 16));
        return Add(r.Name, p.W, p.H, alpha, ch.Value, "png", path => Png.Write(path, p.W, p.H, rgba, alpha), ".png", MeanColor(rgba));
    }
}
