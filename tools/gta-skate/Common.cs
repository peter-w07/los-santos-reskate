// Log / Opts / Paths / JsonExt / Fnv64 are the same small helpers as tools/gta-export/Common.cs (copied so
// this tool builds on its own).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GtaSkate;

public static class Log
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly object Gate = new();

    public static void Info(string msg)
    {
        lock (Gate) Console.Error.WriteLine($"[{Clock.Elapsed.TotalSeconds,7:F1}s] {msg}");
    }

    public static void Warn(string msg) => Info("WARN: " + msg);

    public static double Seconds => Clock.Elapsed.TotalSeconds;
}

/// <summary>Tiny "--key value" / "--flag" command line parser.</summary>
public sealed class Opts
{
    readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Positional = new();

    public Opts(IEnumerable<string> args)
    {
        string pending = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                if (pending != null) _kv[pending] = "true";
                var k = a.Substring(2);
                int eq = k.IndexOf('=');
                if (eq > 0) { _kv[k.Substring(0, eq)] = k.Substring(eq + 1); pending = null; }
                else pending = k;
            }
            else if (pending != null) { _kv[pending] = a; pending = null; }
            else Positional.Add(a);
        }
        if (pending != null) _kv[pending] = "true";
    }

    public bool Has(string k) => _kv.ContainsKey(k);
    public string Str(string k, string def = null) => _kv.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => _kv.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
    public double Num(string k, double def) => _kv.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
    public bool Flag(string k) => _kv.TryGetValue(k, out var v) && v != "false" && v != "0";

    public double[] Nums(string k, int n)
    {
        if (!_kv.TryGetValue(k, out var v)) return null;
        var p = v.Split(',');
        if (p.Length != n) throw new ArgumentException($"--{k} expects {n} comma separated numbers");
        return p.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }
}

public static class Paths
{
    public const string DefaultGame = @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V";
    public const string DefaultOut = @"work/visual";
    public const string DefaultCollision = @"work/export";

    /// <summary>Forward slashes, for JSON.</summary>
    public static string Fwd(string p) => p?.Replace('\\', '/');

    /// <summary>Write via temp file + rename so an interrupted run never leaves a truncated output.</summary>
    public static void AtomicWrite(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp" + Environment.ProcessId + "_" + Environment.CurrentManagedThreadId;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            write(fs);
        File.Move(tmp, path, true);
    }

    public static void AtomicWriteBytes(string path, byte[] data) => AtomicWrite(path, s => s.Write(data, 0, data.Length));

    public static void AtomicWriteText(string path, string text) => AtomicWriteBytes(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        AtomicWrite(path, s =>
        {
            using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
            write(w);
            w.Flush();
        });
    }
}

public static class JsonExt
{
    public static void Vec3(this Utf8JsonWriter w, string name, float x, float y, float z)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(R(x)); w.WriteNumberValue(R(y)); w.WriteNumberValue(R(z));
        w.WriteEndArray();
    }

    public static void Vec3(this Utf8JsonWriter w, string name, SharpDX.Vector3 v) => Vec3(w, name, v.X, v.Y, v.Z);

    /// <summary>Round to 4 decimals so JSON floats stay short (0.1 mm is far below game precision).</summary>
    public static double R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0.0;
}

/// <summary>FNV-1a 64 bit, used for resume signatures.</summary>
public struct Fnv64
{
    ulong _h;
    public static Fnv64 Create() => new() { _h = 14695981039346656037UL };
    public void Add(ulong v) { for (int i = 0; i < 8; i++) { _h ^= (byte)(v >> (i * 8)); _h *= 1099511628211UL; } }
    public void Add(string s) { foreach (var c in s) { _h ^= c; _h *= 1099511628211UL; } _h ^= 0xFF; _h *= 1099511628211UL; }
    public ulong Value => _h;
}

/// <summary>
/// 64 bit content hash for resume signatures (multiply / rotate mixing over 8-byte words). Only compared with
/// values produced by this same code, so its exact definition matters only in that it is fixed and deterministic.
/// </summary>
public struct Hash64
{
    ulong _h;
    public static Hash64 Create(ulong seed = 0x9E3779B97F4A7C15UL) => new() { _h = seed };

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Add(ulong k)
    {
        k *= 0x87C37B91114253D5UL; k = (k << 31) | (k >> 33); k *= 0x4CF5AD432745937FUL;
        _h ^= k; _h = (_h << 27) | (_h >> 37); _h = _h * 5 + 0x52DCE729UL;
    }

    public void Add(int v) => Add((ulong)(uint)v);
    public void Add(uint v) => Add((ulong)v);
    public void Add(long v) => Add((ulong)v);
    public void AddF(float f) => Add((ulong)BitConverter.SingleToUInt32Bits(f));
    public void Add(string s) { Add((ulong)s.Length); foreach (var c in s) Add((ulong)c); }

    public void AddBytes(ReadOnlySpan<byte> b)
    {
        Add((ulong)b.Length);
        var w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b.Slice(0, b.Length & ~7));
        foreach (var k in w) Add(k);
        ulong tail = 0;
        for (int i = b.Length & ~7, s = 0; i < b.Length; i++, s += 8) tail |= (ulong)b[i] << s;
        if ((b.Length & 7) != 0) Add(tail);
    }

    public void AddSpan<T>(ReadOnlySpan<T> v) where T : unmanaged => AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v));

    public ulong Value
    {
        get
        {
            ulong h = _h;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return h;
        }
    }
}
