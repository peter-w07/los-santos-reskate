using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>
/// Minimal reader for compiled game scripts (.ysc): header, code pages, string heap, native table, and a linear
/// disassembly that is just good enough to answer one question for the map-state audit: which map data (IPL)
/// names does which script hand to which native, and which names does it copy into which text-label slot.
/// Nothing is executed and no script content is written out except the matching IPL names and call sites.
/// </summary>
public sealed class YscScript
{
    public string Name;
    public byte[] Code;
    public byte[] Strings;
    public ulong[] Natives;
    public int Statics, Globals, Params;
    public List<Ins> Instructions = new();
    public string Table;            // opcode table that decoded cleanly ("v1" 127 opcodes, "v2" 131 opcodes)
    public int BadJumps, BadCalls;

    public struct Ins
    {
        public int Off;
        public byte Op;             // normalised to the v2 numbering
        public int A, B;            // operands (meaning depends on the opcode)
    }

    // normalised opcode numbers (v2 table)
    public const byte NOP = 0, INOT = 6, PUSH_U8 = 37, PUSH_U8_U8 = 38, PUSH_U8_U8_U8 = 39, PUSH_U32 = 40, PUSH_F = 41, DUP = 42, DROP = 43,
        NATIVE = 44, ENTER = 45, LEAVE = 46, LOAD = 47, STORE = 48, ARRAY_U8 = 52, LOCAL_U8 = 55, LOCAL_U8_LOAD = 56, LOCAL_U8_STORE = 57,
        STATIC_U8 = 58, STATIC_U8_LOAD = 59, STATIC_U8_STORE = 60, IADD_U8 = 61, IMUL_U8 = 62, IOFFSET = 63, IOFFSET_U8 = 64, IOFFSET_U8_LOAD = 65,
        IOFFSET_U8_STORE = 66, PUSH_S16 = 67, IADD_S16 = 68, IMUL_S16 = 69, IOFFSET_S16 = 70, IOFFSET_S16_LOAD = 71, IOFFSET_S16_STORE = 72,
        ARRAY_U16 = 73, LOCAL_U16 = 76, LOCAL_U16_LOAD = 77, LOCAL_U16_STORE = 78, STATIC_U16 = 79, STATIC_U16_LOAD = 80, STATIC_U16_STORE = 81,
        GLOBAL_U16 = 82, GLOBAL_U16_LOAD = 83, GLOBAL_U16_STORE = 84, J = 85, JZ = 86, IEQ_JZ = 87, ILE_JZ = 92, CALL = 93,
        STATIC_U24 = 94, STATIC_U24_LOAD = 95, STATIC_U24_STORE = 96, GLOBAL_U24 = 97, GLOBAL_U24_LOAD = 98, GLOBAL_U24_STORE = 99,
        PUSH_U24 = 100, SWITCH = 101, STRING = 102, STRINGHASH = 103, TL_ASSIGN_STRING = 104, TL_ASSIGN_INT = 105, TL_APPEND_STRING = 106,
        TL_APPEND_INT = 107, TL_COPY = 108, CATCH = 109, THROW = 110, CALLINDIRECT = 111, PUSH_M1 = 112, PUSH_0 = 113, PUSH_7 = 120,
        PUSH_FM1 = 121, PUSH_F0 = 122, PUSH_F7 = 129, IS_BIT_SET = 130;

    static readonly string[] Names = BuildNames();

    static string[] BuildNames()
    {
        var n = new string[256];
        var list = ("NOP IADD ISUB IMUL IDIV IMOD INOT INEG IEQ INE IGT IGE ILT ILE FADD FSUB FMUL FDIV FMOD FNEG FEQ FNE FGT FGE FLT FLE " +
            "VADD VSUB VMUL VDIV VNEG IAND IOR IXOR I2F F2I F2V PUSH_U8 PUSH_U8_U8 PUSH_U8_U8_U8 PUSH_U32 PUSH_F DUP DROP NATIVE ENTER LEAVE " +
            "LOAD STORE STORE_REV LOAD_N STORE_N ARRAY_U8 ARRAY_U8_LOAD ARRAY_U8_STORE LOCAL_U8 LOCAL_U8_LOAD LOCAL_U8_STORE STATIC_U8 " +
            "STATIC_U8_LOAD STATIC_U8_STORE IADD_U8 IMUL_U8 IOFFSET IOFFSET_U8 IOFFSET_U8_LOAD IOFFSET_U8_STORE PUSH_S16 IADD_S16 IMUL_S16 " +
            "IOFFSET_S16 IOFFSET_S16_LOAD IOFFSET_S16_STORE ARRAY_U16 ARRAY_U16_LOAD ARRAY_U16_STORE LOCAL_U16 LOCAL_U16_LOAD LOCAL_U16_STORE " +
            "STATIC_U16 STATIC_U16_LOAD STATIC_U16_STORE GLOBAL_U16 GLOBAL_U16_LOAD GLOBAL_U16_STORE J JZ IEQ_JZ INE_JZ IGT_JZ IGE_JZ ILT_JZ " +
            "ILE_JZ CALL STATIC_U24 STATIC_U24_LOAD STATIC_U24_STORE GLOBAL_U24 GLOBAL_U24_LOAD GLOBAL_U24_STORE PUSH_U24 SWITCH STRING " +
            "STRINGHASH TL_ASSIGN_STRING TL_ASSIGN_INT TL_APPEND_STRING TL_APPEND_INT TL_COPY CATCH THROW CALLINDIRECT PUSH_M1 PUSH_0 PUSH_1 " +
            "PUSH_2 PUSH_3 PUSH_4 PUSH_5 PUSH_6 PUSH_7 PUSH_FM1 PUSH_F0 PUSH_F1 PUSH_F2 PUSH_F3 PUSH_F4 PUSH_F5 PUSH_F6 PUSH_F7 IS_BIT_SET").Split(' ');
        for (int i = 0; i < list.Length; i++) n[i] = list[i];
        return n;
    }

    public static string OpName(byte op) => Names[op] ?? ("OP" + op);

    static int Ptr(byte[] d, int off) => (int)(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off)) & 0x0FFFFFFF);

    public static YscScript Load(string name, byte[] d)
    {
        var s = new YscScript { Name = name };
        int codePagesPtr = Ptr(d, 0x10);
        int codeLen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x1C));
        s.Params = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        s.Statics = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x24));
        s.Globals = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        int nativeCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x2C));
        int nativesPtr = Ptr(d, 0x40);
        int stringPagesPtr = Ptr(d, 0x68);
        int stringLen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x70));
        if (codeLen < 0 || codeLen > 64 << 20 || stringLen < 0 || stringLen > 64 << 20 || nativeCount < 0 || nativeCount > 1 << 20)
            throw new InvalidDataException("implausible ysc header");

        s.Code = ReadPaged(d, codePagesPtr, codeLen);
        s.Strings = ReadPaged(d, stringPagesPtr, stringLen);
        s.Natives = new ulong[nativeCount];
        for (int i = 0; i < nativeCount; i++)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(nativesPtr + i * 8));
            int rot = (codeLen + i) & 63;
            s.Natives[i] = (v << rot) | (rot == 0 ? 0 : v >> (64 - rot));
        }
        return s;
    }

    static byte[] ReadPaged(byte[] d, int tablePtr, int length)
    {
        var res = new byte[length];
        int pages = (length + 0x3FFF) >> 14;
        for (int p = 0; p < pages; p++)
        {
            int pp = Ptr(d, tablePtr + p * 8);
            int n = Math.Min(0x4000, length - p * 0x4000);
            Buffer.BlockCopy(d, pp, res, p * 0x4000, n);
        }
        return res;
    }

    public string StringAt(int idx)
    {
        if (idx < 0 || idx >= Strings.Length) return null;
        int e = idx;
        while (e < Strings.Length && Strings[e] != 0) e++;
        return Encoding.UTF8.GetString(Strings, idx, e - idx);
    }

    /// <summary>Decode with the newer 131-opcode table, fall back to the original 127-opcode one; keep the one without errors.</summary>
    public bool Disassemble()
    {
        foreach (var v2 in new[] { true, false })
        {
            if (TryDisassemble(v2, out var ins, out int badJ, out int badC))
            {
                Instructions = ins; Table = v2 ? "v2" : "v1"; BadJumps = badJ; BadCalls = badC;
                if (badJ == 0 && badC == 0) return true;
            }
        }
        return Instructions.Count > 0 && BadJumps == 0 && BadCalls == 0;
    }

    bool TryDisassemble(bool v2, out List<Ins> ins, out int badJumps, out int badCalls)
    {
        ins = new List<Ins>(Code.Length / 2);
        badJumps = badCalls = 0;
        var c = Code;
        int n = c.Length;
        var starts = new System.Collections.BitArray(n + 1);
        var jumps = new List<int>();
        var calls = new List<int>();
        int i = 0;
        while (i < n)
        {
            int off = i;
            byte raw = c[i++];
            int op = raw;
            if (!v2 && raw >= 94) op = raw + 3;                 // v1 has no STATIC_U24* (94..96)
            if (v2 && raw > 130) return false;
            if (!v2 && raw > 126) return false;
            int a = 0, b = 0;
            switch (op)
            {
                case PUSH_U8: case ARRAY_U8: case ARRAY_U8 + 1: case ARRAY_U8 + 2: case LOCAL_U8: case LOCAL_U8_LOAD: case LOCAL_U8_STORE:
                case STATIC_U8: case STATIC_U8_LOAD: case STATIC_U8_STORE: case IADD_U8: case IMUL_U8: case IOFFSET_U8: case IOFFSET_U8_LOAD:
                case IOFFSET_U8_STORE: case TL_ASSIGN_STRING: case TL_ASSIGN_INT: case TL_APPEND_STRING: case TL_APPEND_INT:
                    if (i + 1 > n) return false; a = c[i]; i += 1; break;
                case PUSH_U8_U8: if (i + 2 > n) return false; a = c[i]; b = c[i + 1]; i += 2; break;
                case PUSH_U8_U8_U8: if (i + 3 > n) return false; a = c[i + 1]; b = c[i + 2]; i += 3; break;
                case PUSH_U32: case PUSH_F: if (i + 4 > n) return false; a = BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(i)); i += 4; break;
                case NATIVE: if (i + 3 > n) return false; a = c[i]; b = (c[i + 1] << 8) | c[i + 2]; i += 3; break;
                case ENTER:
                    if (i + 4 > n) return false;
                    a = c[i]; b = c[i + 1] | (c[i + 2] << 8);
                    i += 4 + c[i + 3];
                    break;
                case LEAVE: if (i + 2 > n) return false; a = c[i]; b = c[i + 1]; i += 2; break;
                case PUSH_S16: case IADD_S16: case IMUL_S16: case IOFFSET_S16: case IOFFSET_S16_LOAD: case IOFFSET_S16_STORE:
                    if (i + 2 > n) return false; a = BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(i)); i += 2; break;
                case ARRAY_U16: case ARRAY_U16 + 1: case ARRAY_U16 + 2: case LOCAL_U16: case LOCAL_U16_LOAD: case LOCAL_U16_STORE:
                case STATIC_U16: case STATIC_U16_LOAD: case STATIC_U16_STORE: case GLOBAL_U16: case GLOBAL_U16_LOAD: case GLOBAL_U16_STORE:
                    if (i + 2 > n) return false; a = BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(i)); i += 2; break;
                case J: case JZ: case IEQ_JZ: case IEQ_JZ + 1: case IEQ_JZ + 2: case IEQ_JZ + 3: case IEQ_JZ + 4: case ILE_JZ:
                    if (i + 2 > n) return false; a = i + 2 + BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(i)); i += 2; jumps.Add(a); break;
                case CALL: case STATIC_U24: case STATIC_U24_LOAD: case STATIC_U24_STORE: case GLOBAL_U24: case GLOBAL_U24_LOAD: case GLOBAL_U24_STORE: case PUSH_U24:
                    if (i + 3 > n) return false; a = c[i] | (c[i + 1] << 8) | (c[i + 2] << 16); i += 3;
                    if (op == CALL) calls.Add(a);
                    break;
                case SWITCH:
                {
                    if (i + 1 > n) return false;
                    int cnt = c[i]; i += 1;
                    if (i + cnt * 6 > n) return false;
                    for (int k = 0; k < cnt; k++) jumps.Add(i + k * 6 + 6 + BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(i + k * 6 + 4)));
                    a = cnt; b = i; // b = offset of the case table
                    i += cnt * 6;
                    break;
                }
            }
            if (i > n) return false;
            starts[off] = true;
            ins.Add(new Ins { Off = off, Op = (byte)op, A = a, B = b });
        }
        foreach (var j in jumps) if (j != n && (j < 0 || j > n || !starts[j])) badJumps++;   // a jump to the end of the code is legal
        foreach (var t0 in calls)
        {
            int t = t0;
            // a call may land on the padding in front of a page-aligned function: NOPs, or a jump over the page end
            for (int hop = 0; hop < 64 && t >= 0 && t < n && starts[t]; hop++)
            {
                if (c[t] == NOP) t++;
                else if (c[t] == J && t + 3 <= n) t = t + 3 + BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(t + 1));
                else break;
            }
            if (t < 0 || t >= n || !starts[t] || c[t] != ENTER) badCalls++;
        }
        return true;
    }
}

public static class ScriptScan
{
    public sealed class Hit
    {
        public string Script, Kind, Str, Dest, Ctx;
        public int Func, Off, Argc, Retc, Size, Seq;
        public ulong Native;
    }

    /// <summary>
    /// audit --what scripts: every script of the newest script archive; records each place where a known map data
    /// (ymap / IPL) name is the last literal pushed before a native call, or is copied into a text label.
    /// </summary>
    /// <summary>
    /// The archive that holds the game's current scripts: the newest title-update copy of script_rel.rpf that
    /// contains building_controller.ysc (update2.rpf, else update.rpf, else the base archives).
    /// </summary>
    public static string FindScriptArchive(GameContext ctx)
    {
        string best = null; int bestRank = -1;
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            var p = rpf.Path;
            if (!p.EndsWith(@"\script_rel.rpf", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(@"\script.rpf", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Contains(@"\dlcpacks\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\dlc_patch\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!rpf.AllEntries.Any(e => e.NameLower == "building_controller.ysc")) continue;
            int rank = p.StartsWith(@"update\update2.rpf", StringComparison.OrdinalIgnoreCase) ? 3 : p.StartsWith(@"update\update.rpf", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            if (rank > bestRank) { bestRank = rank; best = p; }
        }
        return best;
    }

    /// <summary>Decode every script of the archive and collect the uses of known map data (.ymap) names.</summary>
    public static List<Hit> Collect(GameContext ctx, int threads, string archivePath, out List<(string name, int code, int strings, int natives, string table, bool ok, string err)> infoOut)
    {
        var gfc = ctx.Gfc;
        // known IPL names: every .ymap name of every archive
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rpf in gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            bool scriptRpf = archivePath != null && rpf.Path.Equals(archivePath, StringComparison.OrdinalIgnoreCase);
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe) continue;
                if (e.NameLower.EndsWith(".ymap")) known.Add(fe.GetShortNameLower());
                else if (scriptRpf && e.NameLower.EndsWith(".ysc")) entries[fe.GetShortNameLower()] = fe;
            }
        }

        var hits = new System.Collections.Concurrent.ConcurrentBag<Hit>();
        var info = new System.Collections.Concurrent.ConcurrentBag<(string name, int code, int strings, int natives, string table, bool ok, string err)>();
        Parallel.ForEach(entries.Values, new ParallelOptions { MaxDegreeOfParallelism = threads }, fe =>
        {
            var name = fe.GetShortNameLower();
            try
            {
                var data = fe.File.ExtractFile(fe);
                var s = YscScript.Load(name, data);
                bool ok = s.Disassemble();
                info.Add((name, s.Code.Length, s.Strings.Length, s.Natives.Length, s.Table ?? "", ok, ok ? "" : $"bad jumps {s.BadJumps}, bad calls {s.BadCalls}"));
                if (!ok) return;
                Analyse(s, known, hits);
            }
            catch (Exception ex)
            {
                info.Add((name, 0, 0, 0, "", false, ex.GetType().Name + ": " + ex.Message));
            }
        });
        infoOut = info.OrderBy(i => i.name, StringComparer.Ordinal).ToList();
        return hits.OrderBy(h => h.Script, StringComparer.Ordinal).ThenBy(h => h.Off).ToList();
    }

    public static void Run(GameContext ctx, string auditDir, int threads, string archiveFilter)
    {
        var archive = archiveFilter ?? FindScriptArchive(ctx);
        var list = Collect(ctx, threads, archive, out var info);
        Log.Info($"scripts: {info.Count} .ysc in {archive}");
        Paths.WriteJson(Path.Combine(auditDir, "scripts_ipl.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "Map data (IPL) names found in compiled game scripts: 'native' = last literal before a native call (native = the script's native table hash, the same value in every script), 'label' = literal copied into a text label slot. No script code is stored.");
            w.WriteStartArray("scripts");
            foreach (var i in info)
            {
                w.WriteStartObject();
                w.WriteString("name", i.name); w.WriteNumber("code", i.code); w.WriteNumber("strings", i.strings); w.WriteNumber("natives", i.natives);
                w.WriteString("table", i.table); w.WriteBoolean("ok", i.ok);
                if (i.err.Length > 0) w.WriteString("error", i.err);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("hits");
            foreach (var h in list)
            {
                w.WriteStartObject();
                w.WriteString("script", h.Script); w.WriteString("kind", h.Kind); w.WriteString("name", h.Str.ToLowerInvariant());
                w.WriteNumber("func", h.Func); w.WriteNumber("off", h.Off); w.WriteNumber("seq", h.Seq);
                if (h.Kind == "native") { w.WriteString("native", h.Native.ToString("x16")); w.WriteNumber("argc", h.Argc); w.WriteNumber("retc", h.Retc); w.WriteString("ctx", h.Ctx); }
                else { w.WriteString("dest", h.Dest); w.WriteNumber("size", h.Size); w.WriteString("ctx", h.Ctx); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        int okCount = info.Count(i => i.ok);
        Log.Info($"scripts: {okCount}/{info.Count} decoded cleanly ({info.Count(i => i.table == "v2" && i.ok)} with the 131-opcode table), {list.Count} IPL name uses -> scripts_ipl.json");
    }

    /// <summary>Development helper: where does the linear decode of one script go wrong?</summary>
    public static void Debug(GameContext ctx, string archiveFilter, string name)
    {
        archiveFilter ??= FindScriptArchive(ctx);
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null || !rpf.Path.Contains(archiveFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || fe.GetShortNameLower() != name || !e.NameLower.EndsWith(".ysc")) continue;
                var s = YscScript.Load(name, fe.File.ExtractFile(fe));
                bool ok = s.Disassemble();
                Console.WriteLine($"{name}: code {s.Code.Length}, table {s.Table}, ok {ok}, bad jumps {s.BadJumps}, bad calls {s.BadCalls}, {s.Instructions.Count} instructions");
                var starts = new HashSet<int>(s.Instructions.Select(i => i.Off));
                var hist = new int[256];
                foreach (var i in s.Instructions) hist[i.Op]++;
                Console.WriteLine("opcode use: " + string.Join(" ", Enumerable.Range(0, 256).Where(o => hist[o] > 0).Select(o => $"{YscScript.OpName((byte)o)}={hist[o]}")));
                int shown = 0;
                for (int k = 0; k < s.Instructions.Count && shown < 6; k++)
                {
                    var i = s.Instructions[k];
                    bool isJump = i.Op >= YscScript.J && i.Op <= YscScript.ILE_JZ;
                    bool badCall = false;
                    if (i.Op == YscScript.CALL)
                    {
                        int t = i.A;
                        for (int hop = 0; hop < 64 && t < s.Code.Length && starts.Contains(t); hop++)
                        {
                            if (s.Code[t] == YscScript.NOP) t++;
                            else if (s.Code[t] == YscScript.J) t = t + 3 + BinaryPrimitives.ReadInt16LittleEndian(s.Code.AsSpan(t + 1));
                            else break;
                        }
                        badCall = t >= s.Code.Length || !starts.Contains(t) || s.Code[t] != YscScript.ENTER;
                    }
                    if (badCall || isJump && i.A != s.Code.Length && !starts.Contains(i.A))
                    {
                        shown++;
                        Console.WriteLine($"BAD {YscScript.OpName(i.Op)} at {i.Off} -> {i.A} (start={starts.Contains(i.A)}, byte there={(i.A >= 0 && i.A < s.Code.Length ? s.Code[i.A] : -1)}), page offset of site {i.Off & 0x3FFF}, of target {i.A & 0x3FFF}");
                        // instructions just before the target
                        int near = s.Instructions.FindLastIndex(x => x.Off <= i.A);
                        for (int q = Math.Max(0, near - 6); q < Math.Min(s.Instructions.Count, near + 4); q++)
                            Console.WriteLine($"     {s.Instructions[q].Off,8} {YscScript.OpName(s.Instructions[q].Op)} {s.Instructions[q].A} {s.Instructions[q].B}");
                        Console.WriteLine("   site:");
                        for (int q = Math.Max(0, k - 5); q < Math.Min(s.Instructions.Count, k + 2); q++)
                            Console.WriteLine($"     {s.Instructions[q].Off,8} {YscScript.OpName(s.Instructions[q].Op)} {s.Instructions[q].A} {s.Instructions[q].B}");
                    }
                }
                return;
            }
        }
        Console.WriteLine("script not found: " + name);
    }

    /// <summary>Development helper: text disassembly of the named scripts into auditDir/ysc/ (game-derived, stays under work/).</summary>
    public static void Dump(GameContext ctx, string auditDir, string archiveFilter, string[] names)
    {
        archiveFilter ??= FindScriptArchive(ctx);
        var dir = Path.Combine(auditDir, "ysc");
        Directory.CreateDirectory(dir);
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null || !rpf.Path.Contains(archiveFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !e.NameLower.EndsWith(".ysc") || !want.Contains(fe.GetShortNameLower())) continue;
                var s = YscScript.Load(fe.GetShortNameLower(), fe.File.ExtractFile(fe));
                if (!s.Disassemble()) { Console.WriteLine("decode failed: " + s.Name); continue; }
                var sb = new StringBuilder();
                sb.Append($"; {s.Name} code={s.Code.Length} statics={s.Statics} globals={s.Globals} natives={s.Natives.Length}\n");
                var ins = s.Instructions;
                for (int k = 0; k < ins.Count; k++)
                {
                    var i = ins[k];
                    sb.Append(i.Off).Append(' ').Append(YscScript.OpName(i.Op));
                    switch (i.Op)
                    {
                        case YscScript.NATIVE: sb.Append(' ').Append(i.A >> 2).Append(' ').Append(i.A & 3).Append(' ').Append((i.B < s.Natives.Length ? s.Natives[i.B] : 0).ToString("x16")); break;
                        case YscScript.ENTER: case YscScript.LEAVE: case YscScript.PUSH_U8_U8: sb.Append(' ').Append(i.A).Append(' ').Append(i.B); break;
                        case YscScript.PUSH_U8_U8_U8: sb.Append(' ').Append(s.Code[i.Off + 1]).Append(' ').Append(i.A).Append(' ').Append(i.B); break;
                        case YscScript.PUSH_F: sb.Append(' ').Append(BitConverter.Int32BitsToSingle(i.A).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                        case YscScript.STRING:
                        {
                            int idx = k > 0 ? ConstOf(ins[k - 1]) : -1;
                            var str = idx >= 0 ? s.StringAt(idx) : null;
                            sb.Append(' ').Append(str == null ? "?" : JsonSerializer.Serialize(str));
                            break;
                        }
                        case YscScript.SWITCH:
                            for (int c = 0; c < i.A; c++)
                            {
                                int p = i.B + c * 6;
                                sb.Append(' ').Append(BinaryPrimitives.ReadInt32LittleEndian(s.Code.AsSpan(p))).Append(':').Append(p + 6 + BinaryPrimitives.ReadInt16LittleEndian(s.Code.AsSpan(p + 4)));
                            }
                            break;
                        default:
                            if (HasOperand(i.Op)) sb.Append(' ').Append(i.A);
                            break;
                    }
                    sb.Append('\n');
                }
                File.WriteAllText(Path.Combine(dir, s.Name + ".txt"), sb.ToString());
                Console.WriteLine($"wrote ysc/{s.Name}.txt ({ins.Count} instructions)");
            }
        }
    }

    static bool HasOperand(byte op) => op switch
    {
        YscScript.PUSH_U8 or YscScript.PUSH_U32 or YscScript.PUSH_S16 or YscScript.PUSH_U24 or YscScript.CALL or YscScript.TL_ASSIGN_STRING or YscScript.TL_ASSIGN_INT
            or YscScript.TL_APPEND_STRING or YscScript.TL_APPEND_INT => true,
        >= YscScript.ARRAY_U8 and <= YscScript.IOFFSET_U8_STORE => op != YscScript.IOFFSET,
        >= YscScript.IADD_S16 and <= YscScript.ILE_JZ => true,
        >= YscScript.STATIC_U24 and <= YscScript.GLOBAL_U24_STORE => true,
        _ => false,
    };

    static void Analyse(YscScript s, HashSet<string> known, System.Collections.Concurrent.ConcurrentBag<Hit> hits)
    {
        var ins = s.Instructions;
        int func = 0, seq = 0;
        for (int k = 0; k < ins.Count; k++)
        {
            var i = ins[k];
            if (i.Op == YscScript.ENTER) { func = i.Off; continue; }
            if (i.Op != YscScript.STRING || k == 0) continue;
            int idx = ConstOf(ins[k - 1]);
            if (idx < 0) continue;
            var str = s.StringAt(idx);
            if (string.IsNullOrEmpty(str) || !known.Contains(str)) continue;
            // what consumes it? look ahead a few instructions
            bool consumed = false;
            for (int j = k + 1; j < Math.Min(ins.Count, k + 8); j++)
            {
                var n = ins[j];
                if (n.Op == YscScript.NATIVE)
                {
                    if (j != k + 1) break; // only "literal is the last thing pushed before the call"
                    ulong hash = n.B < s.Natives.Length ? s.Natives[n.B] : 0;
                    hits.Add(new Hit { Script = s.Name, Kind = "native", Str = str, Func = func, Off = i.Off, Seq = seq++, Native = hash, Argc = n.A >> 2, Retc = n.A & 3, Ctx = Context(ins, j) });
                    consumed = true;
                    break;
                }
                if (n.Op == YscScript.TL_ASSIGN_STRING)
                {
                    var sb = new StringBuilder();
                    for (int q = k + 1; q < j; q++) { if (sb.Length > 0) sb.Append(' '); sb.Append(YscScript.OpName(ins[q].Op)).Append(':').Append(ins[q].A); }
                    hits.Add(new Hit { Script = s.Name, Kind = "label", Str = str, Func = func, Off = i.Off, Seq = seq++, Dest = sb.ToString(), Size = n.A, Ctx = CaseContext(s, ins, k) });
                    consumed = true;
                    break;
                }
                if (n.Op == YscScript.STRING || n.Op == YscScript.CALL || n.Op == YscScript.LEAVE || n.Op == YscScript.J || n.Op == YscScript.JZ) break;
            }
            // the literal goes somewhere else (an argument of a script function, a comparison, an array): still a use of the name
            if (!consumed)
                hits.Add(new Hit { Script = s.Name, Kind = "other", Str = str, Func = func, Off = i.Off, Seq = seq++, Dest = k + 1 < ins.Count ? YscScript.OpName(ins[k + 1].Op) + ":" + ins[k + 1].A : "", Ctx = "" });
        }
    }

    /// <summary>The two instructions after a native call (e.g. "INOT JZ" after IS_IPL_ACTIVE), as a hint of how the result is used.</summary>
    static string Context(List<YscScript.Ins> ins, int nativeAt)
    {
        var sb = new StringBuilder();
        for (int q = nativeAt + 1; q < Math.Min(ins.Count, nativeAt + 3); q++) { if (sb.Length > 0) sb.Append(' '); sb.Append(YscScript.OpName(ins[q].Op)); }
        return sb.ToString();
    }

    /// <summary>For text-label assignments: the value of the innermost switch case this instruction sits in (-1 if none found nearby).</summary>
    static string CaseContext(YscScript s, List<YscScript.Ins> ins, int at)
    {
        // walk back to the nearest SWITCH of the same function and find the case whose target is the last one <= this offset
        int off = ins[at].Off;
        for (int k = at; k >= 0; k--)
        {
            var i = ins[k];
            if (i.Op == YscScript.ENTER) break;
            if (i.Op != YscScript.SWITCH) continue;
            int best = int.MinValue, bestTarget = -1;
            for (int c = 0; c < i.A; c++)
            {
                int p = i.B + c * 6;
                int val = BinaryPrimitives.ReadInt32LittleEndian(s.Code.AsSpan(p));
                int target = p + 6 + BinaryPrimitives.ReadInt16LittleEndian(s.Code.AsSpan(p + 4));
                if (target <= off && target > bestTarget) { bestTarget = target; best = val; }
            }
            if (bestTarget >= 0) return "case " + best + " of switch@" + i.Off;
            break;
        }
        return "";
    }

    static int ConstOf(YscScript.Ins i)
    {
        switch (i.Op)
        {
            case YscScript.PUSH_U8: case YscScript.PUSH_S16: case YscScript.PUSH_U24: case YscScript.PUSH_U32: return i.A;
            case YscScript.PUSH_U8_U8: case YscScript.PUSH_U8_U8_U8: return i.B;
        }
        if (i.Op >= YscScript.PUSH_0 && i.Op <= YscScript.PUSH_7) return i.Op - YscScript.PUSH_0;
        return -1;
    }
}

/// <summary>
/// audit --what building-states: who writes the saved "building state" array (the global the building controller
/// reads its per-building state from), and with which constants. Finds, in every script, the functions that
/// store into that global array and every call to them whose arguments are literal constants.
/// </summary>
public static class BuildingStateScan
{
    public static void Run(GameContext ctx, string auditDir, int threads, string archiveFilter, int global)
    {
        archiveFilter ??= ScriptScan.FindScriptArchive(ctx);
        var entries = new List<RpfFileEntry>();
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null || !rpf.Path.Contains(archiveFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var e in rpf.AllEntries) if (e is RpfFileEntry fe && e.NameLower.EndsWith(".ysc")) entries.Add(fe);
        }
        var rows = new System.Collections.Concurrent.ConcurrentBag<(string script, string kind, int func, int off, string detail)>();
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = threads }, fe =>
        {
            YscScript s;
            try { s = YscScript.Load(fe.GetShortNameLower(), fe.File.ExtractFile(fe)); if (!s.Disassemble()) return; }
            catch (Exception) { return; }
            var ins = s.Instructions;
            // function table: start offset -> (index of ENTER, argc)
            var funcAt = new Dictionary<int, int>();
            for (int k = 0; k < ins.Count; k++) if (ins[k].Op == YscScript.ENTER) funcAt[ins[k].Off] = k;
            var setters = new Dictionary<int, string>();   // function start -> how it stores
            var wrappers = new HashSet<int>();
            int func = 0;
            for (int k = 0; k < ins.Count; k++)
            {
                var i = ins[k];
                if (i.Op == YscScript.ENTER) { func = i.Off; continue; }
                if (i.Op != YscScript.GLOBAL_U16 || i.A != global || k + 1 >= ins.Count) continue;
                var nx = ins[k + 1];
                if (nx.Op != YscScript.ARRAY_U8 + 2) continue;   // ARRAY_U8_STORE
                // value and index are the two pushes before
                string idx = k >= 1 ? Describe(ins[k - 1]) : "?";
                string val = k >= 2 ? Describe(ins[k - 2]) : "?";
                rows.Add((s.Name, "store", func, i.Off, $"state[{idx}] = {val}"));
                if (idx.StartsWith("arg") || val.StartsWith("arg")) setters[func] = $"state[{idx}] = {val}";
            }
            if (setters.Count == 0) return;
            func = 0;
            for (int k = 0; k < ins.Count; k++)
            {
                var i = ins[k];
                if (i.Op == YscScript.ENTER) { func = i.Off; continue; }
                if (i.Op != YscScript.CALL) continue;
                int t = i.A;
                for (int hop = 0; hop < 64 && t < s.Code.Length; hop++)
                {
                    if (s.Code[t] == YscScript.NOP) t++;
                    else if (s.Code[t] == YscScript.J) t = t + 3 + System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(s.Code.AsSpan(t + 1));
                    else break;
                }
                if (!setters.TryGetValue(t, out var how)) continue;
                int argc = ins[funcAt[t]].A;
                var consts = new List<string>();
                for (int q = k - 1; q >= 0 && consts.Count < argc; q--)
                {
                    var p = ins[q];
                    if (p.Op >= YscScript.PUSH_0 && p.Op <= YscScript.PUSH_7) consts.Add((p.Op - YscScript.PUSH_0).ToString());
                    else if (p.Op == YscScript.PUSH_M1) consts.Add("-1");
                    else if (p.Op == YscScript.PUSH_U8 || p.Op == YscScript.PUSH_S16 || p.Op == YscScript.PUSH_U24 || p.Op == YscScript.PUSH_U32) consts.Add(p.A.ToString());
                    else if (p.Op == YscScript.PUSH_U8_U8) { consts.Add(p.B.ToString()); consts.Add(p.A.ToString()); }
                    else if (p.Op == YscScript.PUSH_U8_U8_U8) { consts.Add(p.B.ToString()); consts.Add(p.A.ToString()); consts.Add(s.Code[p.Off + 1].ToString()); }
                    else break;
                }
                while (consts.Count > argc) consts.RemoveAt(consts.Count - 1);
                consts.Reverse();
                while (consts.Count < argc) consts.Insert(0, "?");
                rows.Add((s.Name, "call", func, i.Off, $"setter@{t}({string.Join(",", consts)}) [{how}]"));
                wrappers.Add(func);
            }
            // second level: SET_BUILDING_STATE(building, state, ...) is the function that calls the low-level store;
            // list every call of it with its literal arguments
            func = 0;
            for (int k = 0; k < ins.Count; k++)
            {
                var i = ins[k];
                if (i.Op == YscScript.ENTER) { func = i.Off; continue; }
                if (i.Op != YscScript.CALL) continue;
                int t = i.A;
                for (int hop = 0; hop < 64 && t < s.Code.Length; hop++)
                {
                    if (s.Code[t] == YscScript.NOP) t++;
                    else if (s.Code[t] == YscScript.J) t = t + 3 + System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(s.Code.AsSpan(t + 1));
                    else break;
                }
                if (!wrappers.Contains(t) || setters.ContainsKey(t)) continue;
                int argc = ins[funcAt[t]].A;
                var consts = new List<string>();
                for (int q = k - 1; q >= 0 && consts.Count < argc; q--)
                {
                    var p = ins[q];
                    if (p.Op >= YscScript.PUSH_0 && p.Op <= YscScript.PUSH_7) consts.Add((p.Op - YscScript.PUSH_0).ToString());
                    else if (p.Op == YscScript.PUSH_M1) consts.Add("-1");
                    else if (p.Op == YscScript.PUSH_U8 || p.Op == YscScript.PUSH_S16 || p.Op == YscScript.PUSH_U24 || p.Op == YscScript.PUSH_U32) consts.Add(p.A.ToString());
                    else if (p.Op == YscScript.PUSH_U8_U8) { consts.Add(p.B.ToString()); consts.Add(p.A.ToString()); }
                    else if (p.Op == YscScript.PUSH_U8_U8_U8) { consts.Add(p.B.ToString()); consts.Add(p.A.ToString()); consts.Add(s.Code[p.Off + 1].ToString()); }
                    else if (p.Op == YscScript.LOCAL_U8_LOAD) consts.Add("local" + p.A);
                    else break;
                }
                while (consts.Count > argc) consts.RemoveAt(consts.Count - 1);
                consts.Reverse();
                while (consts.Count < argc) consts.Insert(0, "?");
                rows.Add((s.Name, "set", func, i.Off, string.Join(",", consts)));
            }
        });
        var list = rows.OrderBy(r => r.script, StringComparer.Ordinal).ThenBy(r => r.off).ToList();
        Paths.WriteJson(Path.Combine(auditDir, "scripts_building_states.json"), w =>
        {
            w.WriteStartObject();
            w.WriteNumber("global", global);
            w.WriteStartArray("rows");
            foreach (var r in list)
            {
                w.WriteStartObject();
                w.WriteString("script", r.script); w.WriteString("kind", r.kind); w.WriteNumber("func", r.func); w.WriteNumber("off", r.off); w.WriteString("detail", r.detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Log.Info($"building states: {list.Count(r => r.kind == "set")} SET_BUILDING_STATE calls; {list.Count(r => r.kind == "store")} stores into global {global}, {list.Count(r => r.kind == "call")} calls of setter functions, in {list.Select(r => r.script).Distinct().Count()} scripts -> scripts_building_states.json");
    }

    static string Describe(YscScript.Ins i)
    {
        if (i.Op >= YscScript.PUSH_0 && i.Op <= YscScript.PUSH_7) return (i.Op - YscScript.PUSH_0).ToString();
        if (i.Op == YscScript.PUSH_U8 || i.Op == YscScript.PUSH_S16) return i.A.ToString();
        if (i.Op == YscScript.LOCAL_U8_LOAD) return "arg" + i.A;
        return YscScript.OpName(i.Op);
    }
}
