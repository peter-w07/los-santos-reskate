using System.Text.Json;
using SharpDX;

namespace GtaSkate;

/// <summary>
/// The pause-menu map picture. When ReSkate Studio compiles a .blend it renders one with Blender (top view,
/// unlit, transparent outside the map) into ui/map_image.png with ui/map_image.json (the picture's world
/// rectangle) and ui/map_clouds.png (where the map's clouds part). Our normalized map is written without
/// Blender, so the same three files are made here: a top-down software rasteriser draws every triangle in the
/// mean colour of its texture, highest surface on top, with a little shading by slope.
/// Framing and file contents follow studio_map_import.py (_render_pause_map, _finish_pause_map).
/// </summary>
public sealed class PauseMap
{
    public readonly int W, H;      // 3840 x 2160 is what Studio's own renderer makes; larger only for the overview picture
    const float Padding = 1.1f, MinHalfHeight = 25f;
    readonly float[] _depth;
    readonly uint[] _rgba;
    readonly object _gate = new();
    readonly float _minX, _maxX, _minY, _maxY;     // picture rectangle in map coordinates (x east, y north)
    readonly float _sx, _sy;
    public long Triangles;

    public PauseMap(double[] region, Vector3 off, int height = 2160)
    {
        H = height; W = height * 16 / 9;
        _depth = new float[W * H]; _rgba = new uint[W * H];
        Array.Fill(_depth, float.NegativeInfinity);
        float lowX = (float)region[0] + off.X, highX = (float)region[2] + off.X, lowY = (float)region[1] + off.Y, highY = (float)region[3] + off.Y;
        float aspect = (float)W / H;
        float cx = (lowX + highX) * 0.5f, cy = (lowY + highY) * 0.5f;
        float hh = MathF.Max(MathF.Max((highY - lowY) * 0.5f, (highX - lowX) * 0.5f / aspect), MinHalfHeight) * Padding;
        float hw = hh * aspect;
        _minX = cx - hw; _maxX = cx + hw; _minY = cy - hh; _maxY = cy + hh;
        _sx = W / (_maxX - _minX); _sy = H / (_maxY - _minY);
    }

    /// <summary>Triangles whose vertex i is at basis * pos[i] + origin (map coordinates), in one colour.</summary>
    public void Draw(ReadOnlySpan<float> pos, ReadOnlySpan<int> idx, Vector3 origin, Vector3 ex, Vector3 ey, Vector3 ez, uint rgb)
    {
        float r = ((rgb >> 16) & 255), g = ((rgb >> 8) & 255), b = (rgb & 255);
        lock (_gate)
        {
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int i0 = idx[t] * 3, i1 = idx[t + 1] * 3, i2 = idx[t + 2] * 3;
                var p0 = origin + ex * pos[i0] + ey * pos[i0 + 1] + ez * pos[i0 + 2];
                var p1 = origin + ex * pos[i1] + ey * pos[i1 + 1] + ez * pos[i1 + 2];
                var p2 = origin + ex * pos[i2] + ey * pos[i2 + 1] + ez * pos[i2 + 2];
                float x0 = (p0.X - _minX) * _sx, y0 = (_maxY - p0.Y) * _sy, x1 = (p1.X - _minX) * _sx, y1 = (_maxY - p1.Y) * _sy, x2 = (p2.X - _minX) * _sx, y2 = (_maxY - p2.Y) * _sy;
                int bx0 = (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))), bx1 = (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2)));
                int by0 = (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))), by1 = (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2)));
                if (bx1 < 0 || by1 < 0 || bx0 >= W || by0 >= H) continue;
                // shading by slope: flat ground and roofs at full colour, walls darker
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                float nl = n.Length();
                float shade = nl > 1e-12f ? 0.62f + 0.38f * MathF.Abs(n.Z) / nl : 1f;
                uint col = 0xFF000000u | (uint)(b * shade) << 16 | (uint)(g * shade) << 8 | (uint)(r * shade);   // little-endian RGBA
                float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                bx0 = Math.Max(bx0, 0); by0 = Math.Max(by0, 0); bx1 = Math.Min(bx1, W - 1); by1 = Math.Min(by1, H - 1);
                Triangles++;
                if (MathF.Abs(area) < 1e-6f || (bx1 - bx0 <= 1 && by1 - by0 <= 1))
                {
                    // smaller than a pixel: one sample at its centre keeps thin things (rails, poles) visible
                    int px = (int)((x0 + x1 + x2) / 3f), py = (int)((y0 + y1 + y2) / 3f);
                    if ((uint)px < W && (uint)py < H)
                    {
                        float z = (p0.Z + p1.Z + p2.Z) / 3f;
                        int o = py * W + px;
                        // equal heights: the larger colour value wins, so the picture does not depend on the order of the calls
                        if (z > _depth[o] || (z == _depth[o] && col > _rgba[o])) { _depth[o] = z; _rgba[o] = col; }
                    }
                    continue;
                }
                float inv = 1f / area;
                for (int py = by0; py <= by1; py++)
                {
                    float fy = py + 0.5f;
                    for (int px = bx0; px <= bx1; px++)
                    {
                        float fx = px + 0.5f;
                        float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                        float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                        float w2 = 1f - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = p0.Z * w0 + p1.Z * w1 + p2.Z * w2;
                        int o = py * W + px;
                        // equal heights: the larger colour value wins, so the picture does not depend on the order of the calls
                        if (z > _depth[o] || (z == _depth[o] && col > _rgba[o])) { _depth[o] = z; _rgba[o] = col; }
                    }
                }
            }
        }
    }

    static float[,] BoxBlur(float[,] a, int radius)
    {
        int h = a.GetLength(0), w = a.GetLength(1);
        var t = new float[h, w]; var o = new float[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -radius; k <= radius; k++) { int xx = Math.Clamp(x + k, 0, w - 1); s += a[y, xx]; n++; }
                t[y, x] = s / n;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -radius; k <= radius; k++) { int yy = Math.Clamp(y + k, 0, h - 1); s += t[yy, x]; n++; }
                o[y, x] = s / n;
            }
        return o;
    }

    /// <summary>Writes ui/map_image.png, ui/map_image.json and ui/map_clouds.png under <paramref name="mapDir"/>.</summary>
    public void Save(string mapDir)
    {
        var ui = Path.Combine(mapDir, "ui");
        Directory.CreateDirectory(ui);
        // the sea: nothing draws its surface here, so ground below sea level is tinted, deeper water more
        for (int i = 0; i < _rgba.Length; i++)
        {
            float z = _depth[i];
            if (!(z < -0.3f) || (_rgba[i] >> 24) == 0) continue;
            float t = Math.Clamp(0.55f + -z / 14f, 0.55f, 0.92f);
            uint c = _rgba[i];
            uint r = (uint)((c & 255) * (1 - t) + 30 * t), g = (uint)(((c >> 8) & 255) * (1 - t) + 86 * t), b = (uint)(((c >> 16) & 255) * (1 - t) + 122 * t);
            _rgba[i] = 0xFF000000u | b << 16 | g << 8 | r;
        }
        var bytes = new byte[W * H * 4];
        Buffer.BlockCopy(_rgba, 0, bytes, 0, bytes.Length);
        Png.Write(Path.Combine(ui, "map_image.png"), W, H, bytes, true);

        // Game axes: +X is map +X, +Z is map -Y; the picture's top edge is +Y (north).
        Paths.WriteJson(Path.Combine(ui, "map_image.json"), w =>
        {
            w.WriteStartObject();
            w.WriteNumber("min_x", _minX); w.WriteNumber("max_x", _maxX);
            w.WriteNumber("min_z", -_maxY); w.WriteNumber("max_z", -_minY);
            w.WriteEndObject();
        });

        // cloud mask: clear where the map is, grown by a margin and faded (studio_map_import._finish_pause_map)
        const int grid = 256, size = 1024;
        int rows = Math.Max(1, (int)MathF.Round(grid * (float)H / W));
        var square = new float[grid, grid];
        int top = (grid - rows) / 2;
        for (int ry = 0; ry < rows; ry++)
            for (int rx = 0; rx < grid; rx++)
            {
                int y0 = ry * H / rows, y1 = Math.Min(H, (ry + 1) * H / rows), x0 = rx * W / grid, x1 = Math.Min(W, (rx + 1) * W / grid);
                bool covered = false;
                for (int y = y0; y < y1 && !covered; y += 2)
                    for (int x = x0; x < x1; x += 2)
                        if ((_rgba[y * W + x] >> 24) != 0) { covered = true; break; }
                square[top + ry, rx] = covered ? 1f : 0f;
            }
        int margin = Math.Max(1, (int)MathF.Round(grid * 0.035f));
        var clear = BoxBlur(BoxBlur(square, margin), margin);
        for (int y = 0; y < grid; y++) for (int x = 0; x < grid; x++) clear[y, x] = clear[y, x] > 0.02f ? 1f : 0f;
        int fade = Math.Max(1, (int)MathF.Round(grid * 0.05f / 2f));
        clear = BoxBlur(BoxBlur(clear, fade), fade);
        var mask = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float fy = y * (grid - 1f) / (size - 1f); int ya = (int)fy, yb = Math.Min(ya + 1, grid - 1); float wy = fy - ya;
            for (int x = 0; x < size; x++)
            {
                float fx = x * (grid - 1f) / (size - 1f); int xa = (int)fx, xb = Math.Min(xa + 1, grid - 1); float wx = fx - xa;
                float v = (clear[ya, xa] * (1 - wx) + clear[ya, xb] * wx) * (1 - wy) + (clear[yb, xa] * (1 - wx) + clear[yb, xb] * wx) * wy;
                mask[(y * size + x) * 4 + 3] = (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
            }
        }
        Png.Write(Path.Combine(ui, "map_clouds.png"), size, size, mask, true);
    }
}
