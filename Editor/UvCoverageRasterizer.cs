using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // One scan's mesh reads. Validation, recipes and coverage read the same submesh UVs repeatedly.
    internal sealed class MeshSnapshotCache
    {
        private readonly Dictionary<(Mesh, int, int), MeshSnapshot> snapshots = new Dictionary<(Mesh, int, int), MeshSnapshot>();

        internal MeshSnapshot Read(Mesh mesh, int submesh, int channel)
        {
            var key = (mesh, submesh, channel);
            if (!snapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = MeshSnapshot.Read(mesh, submesh, channel);
                snapshots.Add(key, snapshot);
            }
            return snapshot;
        }
    }

    internal sealed class MeshSnapshot
    {
        public Vector2[] Uvs;
        public int[] Indices;
        private string uvHash;

        // Hash of the UV of every index, in index order: the exact triangle input to coverage.
        internal string UvHash
        {
            get
            {
                if (uvHash != null) return uvHash;
                using (var stream = new System.IO.MemoryStream())
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write(Indices.Length);
                    foreach (int index in Indices) { writer.Write(Uvs[index].x); writer.Write(Uvs[index].y); }
                    writer.Flush();
                    return uvHash = FingerprintService.Hash(stream.ToArray());
                }
            }
        }
        public static MeshSnapshot Read(Mesh mesh, int submesh, int channel)
        {
            if (!mesh || channel < 0 || channel > 7 || !mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)))
                throw new InvalidOperationException($"Missing UV{channel} channel.");
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                var descriptor = data.GetSubMesh(submesh);
                if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount % 3 != 0)
                    throw new InvalidOperationException("Invalid triangle submesh.");
                if (descriptor.indexCount > 6000000 || data.vertexCount > 4000000)
                    throw new InvalidOperationException("Mesh exceeds the processing budget.");
                using (var uv = new NativeArray<Vector2>(data.vertexCount, Allocator.TempJob))
                using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.TempJob))
                {
                    data.GetUVs(channel, uv);
                    data.GetIndices(indices, submesh, true);
                    var result = new MeshSnapshot { Uvs = uv.ToArray(), Indices = indices.ToArray() };
                    foreach (int index in result.Indices)
                        if (index < 0 || index >= result.Uvs.Length) throw new InvalidOperationException("Invalid mesh index.");
                    return result;
                }
            }
        }
    }

    // A scan-scoped cache. Packed bits keep the bounded working set small even for 4K masks.
    internal sealed class UvCoverageCache
    {
        internal const int MaxEntries = 8;
        internal const long MaxRetainedBytes = 32L * 1024 * 1024;

        private sealed class Entry
        {
            public string Key;
            public int Length;
            public byte[] Covered, Sampled;
        }

        private readonly Dictionary<string, LinkedListNode<Entry>> entries = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> recent = new LinkedList<Entry>();
        private long retainedBytes;

        internal int Hits { get; private set; }
        internal int Misses { get; private set; }
        internal int EntryCount => entries.Count;
        internal long RetainedBytes => retainedBytes;

        internal static string CreateKey(TextureGroup group, int width, int height, int padding, Action cancel)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var use in group.ActiveUses)
                foreach (var path in use.Sampling.GetPaths())
                {
                    cancel?.Invoke();
                    var snapshot = use.ReadMesh(path.UvChannel);
                    using (var pathStream = new System.IO.MemoryStream())
                    using (var pathWriter = new System.IO.BinaryWriter(pathStream))
                    {
                        pathWriter.Write(path.UvChannel);
                        pathWriter.Write(snapshot.UvHash);
                        pathWriter.Write(path.Scale.x); pathWriter.Write(path.Scale.y);
                        pathWriter.Write(path.Offset.x); pathWriter.Write(path.Offset.y);
                        pathWriter.Write(path.FixedRepeat);
                        pathWriter.Write((int)path.WrapU(group.Source));
                        pathWriter.Write((int)path.WrapV(group.Source));
                        var transforms = path.AnimatedTransforms;
                        pathWriter.Write(transforms != null);
                        pathWriter.Write(transforms?.Count ?? 0);
                        if (transforms != null)
                            foreach (var transform in transforms)
                            {
                                pathWriter.Write(transform.x); pathWriter.Write(transform.y);
                                pathWriter.Write(transform.z); pathWriter.Write(transform.w);
                            }
                        pathWriter.Flush();
                        // Coverage is a union, so order and duplicate identical paths do not affect its output.
                        paths.Add(FingerprintService.Hash(pathStream.ToArray()));
                    }
                }

            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(width); writer.Write(height);
                writer.Write(group.Source.width); writer.Write(group.Source.height);
                writer.Write(padding);
                writer.Write(paths.Count);
                foreach (string path in paths) writer.Write(path);
                writer.Flush();
                return FingerprintService.Hash(stream.ToArray());
            }
        }

        internal bool TryGet(string key, out bool[] covered, out bool[] sampled, Action cancel)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                Misses++;
                covered = sampled = null;
                return false;
            }

            recent.Remove(node);
            recent.AddFirst(node);
            covered = Unpack(node.Value.Covered, node.Value.Length, cancel);
            sampled = Unpack(node.Value.Sampled, node.Value.Length, cancel);
            Hits++;
            return true;
        }

        internal void Store(string key, bool[] covered, bool[] sampled, Action cancel)
        {
            if (covered.Length != sampled.Length) throw new ArgumentException("Coverage masks must have the same dimensions.");
            if (entries.TryGetValue(key, out var previous)) Remove(previous);

            var packedCovered = Pack(covered, cancel);
            var packedSampled = Pack(sampled, cancel);
            long size = packedCovered.LongLength + packedSampled.LongLength;
            if (size > MaxRetainedBytes) return;
            while (entries.Count >= MaxEntries || retainedBytes + size > MaxRetainedBytes)
                Remove(recent.Last);

            var entry = new Entry { Key = key, Length = covered.Length, Covered = packedCovered, Sampled = packedSampled };
            entries.Add(key, recent.AddFirst(entry));
            retainedBytes += size;
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            recent.Remove(node);
            entries.Remove(node.Value.Key);
            retainedBytes -= node.Value.Covered.LongLength + node.Value.Sampled.LongLength;
        }

        private static byte[] Pack(bool[] values, Action cancel)
        {
            var packed = new byte[(values.Length + 7) / 8];
            for (int i = 0; i < values.Length; i++)
            {
                if ((i & 65535) == 0) cancel?.Invoke();
                if (values[i]) packed[i >> 3] |= (byte)(1 << (i & 7));
            }
            return packed;
        }

        private static bool[] Unpack(byte[] packed, int length, Action cancel)
        {
            var values = new bool[length];
            for (int i = 0; i < length; i++)
            {
                if ((i & 65535) == 0) cancel?.Invoke();
                values[i] = (packed[i >> 3] & (1 << (i & 7))) != 0;
            }
            return values;
        }
    }

    // A retained outcome that depends only on the recipe (source, importer, UVs, sampling, padding and the threshold below), so it
    // is cached like "unchanged".
    internal sealed class RetainedException : InvalidOperationException
    {
        // Below this many texels left unused after padding, a texture is retained (no useful output). Part of the recipe.
        internal const int MinimumUnusedTexels = 256;
        public RetainedException(string message) : base(message) { }
    }

    // Work limits, not time, so the outcome is the same on every machine. Not cached: the work also depends on the order and
    // repeats of the uses (a mesh on several renderers counts each time), which the recipe does not record.
    internal sealed class CoverageBudget
    {
        public long Candidates, Regions;
        public int Pieces, Triangles;
        public Action CheckCancelled;

        public void Check()
        {
            CheckCancelled?.Invoke();
            // Regions counts every wrap region a polygon is clipped against, empty ones too: a sliver crossing many tiles costs a
            // clip per region without adding pieces.
            if (Candidates > 100000000 || Pieces > 1000000 || Triangles > 2000000 || Regions > 100000000)
                throw new InvalidOperationException($"UV analysis exceeded its processing budget (candidates {Candidates:N0}/100,000,000; pieces {Pieces:N0}/1,000,000; triangles {Triangles:N0}/2,000,000; wrap regions {Regions:N0}/100,000,000); texture retained.");
        }
    }

    internal static class UvCoverageRasterizer
    {
        internal const int MaxPixels = 16777216;
        private struct Interval { public double Min, Max; public int Tile; }
        private struct Point
        {
            public double X, Y;
            public Point(double x, double y) { X = x; Y = y; }
        }

        public static bool[] Build(TextureGroup group, int width, int height, Action cancel = null) =>
            Build(group, width, height, out _, null, cancel);

        public static bool[] Build(TextureGroup group, int width, int height, out bool[] sampled, Action cancel = null) =>
            Build(group, width, height, out sampled, null, cancel);

        internal static bool[] Build(TextureGroup group, int width, int height, out bool[] sampled, UvCoverageCache cache, Action cancel = null)
        {
            ValidateSize(width, height);
            // Padding is measured in the imported texture. Convert it back to PNG texels
            // per axis so platform downscaling does not shrink the requested protection.
            int padding = AvatarTextureOptimizer.GetPaddingPixels(group.Source.width, group.Source.height);
            string cacheKey = cache == null ? null : UvCoverageCache.CreateKey(group, width, height, padding, cancel);
            if (cache != null && cache.TryGet(cacheKey, out var cached, out sampled, cancel))
            {
                cancel?.Invoke();
                return cached;
            }
            int radiusX = (int)Math.Ceiling((double)padding * width / group.Source.width);
            int radiusY = (int)Math.Ceiling((double)padding * height / group.Source.height);
            var mask = new bool[width * height];
            var budget = new CoverageBudget { CheckCancelled = cancel };
            var wraps = new HashSet<(TextureWrapMode U, TextureWrapMode V)>();
            // Texels only ever turn on, so once every texel is on no later triangle can change the mask (a tiled texture often
            // fills it long before its triangles run out). firstFree only moves forward: linear in the mask size overall.
            int firstFree = 0;
            bool Full() { while (firstFree < mask.Length && mask[firstFree]) firstFree++; return firstFree == mask.Length; }
            foreach (var use in group.ActiveUses)
                foreach (var path in use.Sampling.GetPaths())
                {
                    if (Full()) { wraps.Add((path.WrapU(group.Source), path.WrapV(group.Source))); continue; }
                    var snapshot = use.ReadMesh(path.UvChannel);
                    var wrap = (U: path.WrapU(group.Source), V: path.WrapV(group.Source));
                    wraps.Add(wrap);
                    var transforms = path.AnimatedTransforms;
                    var hullInput = transforms == null ? null : new List<Point>(transforms.Count * 3);
                    for (int i = 0; i < snapshot.Indices.Length; i += 3)
                    {
                        if (Full()) break;
                        Vector2 u0 = snapshot.Uvs[snapshot.Indices[i]], u1 = snapshot.Uvs[snapshot.Indices[i + 1]], u2 = snapshot.Uvs[snapshot.Indices[i + 2]];
                        if (transforms == null)
                        {
                            Vector2 a = Vector2.Scale(u0, path.Scale) + path.Offset;
                            Vector2 b = Vector2.Scale(u1, path.Scale) + path.Offset;
                            Vector2 c = Vector2.Scale(u2, path.Scale) + path.Offset;
                            Rasterize(mask, width, height, a, b, c, wrap.U, wrap.V, budget);
                            continue;
                        }
                        // The triangle's positions are multilinear in the animated values, so the union of
                        // all animated triangles lies inside the hull of its corner transforms.
                        hullInput.Clear();
                        foreach (var t in transforms)
                        {
                            hullInput.Add(new Point(u0.x * t.x + t.z, u0.y * t.y + t.w));
                            hullInput.Add(new Point(u1.x * t.x + t.z, u1.y * t.y + t.w));
                            hullInput.Add(new Point(u2.x * t.x + t.z, u2.y * t.y + t.w));
                        }
                        RasterizePolygon(mask, width, height, ConvexHull(hullInput), wrap.U, wrap.V, budget);
                    }
                }
            budget.Check();
            if (wraps.Count == 0) throw new InvalidOperationException("No sampled texture uses.");
            sampled = mask;
            var result = new bool[mask.Length];
            // Padding the full union under every applicable sampler is deliberately conservative.
            foreach (var wrap in wraps)
            {
                var padded = Pad(mask, width, height, radiusX, radiusY, wrap.U, wrap.V, cancel);
                for (int i = 0; i < result.Length; i++) result[i] |= padded[i];
            }
            cache?.Store(cacheKey, result, sampled, cancel);
            return result;
        }

        public static void ValidateSize(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > MaxPixels)
                throw new InvalidOperationException("Texture exceeds the 8192px / 16-megapixel MVP memory limit.");
        }

        public static void Rasterize(bool[] mask, int width, int height, Vector2 a, Vector2 b, Vector2 c,
            TextureWrapMode wrapU, TextureWrapMode wrapV, CoverageBudget budget = null) =>
            RasterizePolygon(mask, width, height,
                new List<Point> { new Point(a.x, a.y), new Point(b.x, b.y), new Point(c.x, c.y) }, wrapU, wrapV, budget);

        // Andrew's monotone chain; returns the hull counter-clockwise (a single point or segment when degenerate).
        private static List<Point> ConvexHull(List<Point> points)
        {
            var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            if (sorted.Count < 3) return sorted;
            var hull = new List<Point>(sorted.Count * 2);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                foreach (var p in sorted)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
                sorted.Reverse();
            }
            return hull.Count == 0 ? new List<Point> { sorted[0] } : hull;
        }

        private static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        // Rasterizes a convex polygon under the sampler's wrap modes.
        private static void RasterizePolygon(bool[] mask, int width, int height, List<Point> polygon,
            TextureWrapMode wrapU, TextureWrapMode wrapV, CoverageBudget budget = null)
        {
            budget = budget ?? new CoverageBudget();
            budget.Triangles++;
            budget.Check();
            double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
            double minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
            foreach (double value in new[] { minX, maxX, minY, maxY })
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1000000)
                    throw new InvalidOperationException("Nonfinite or pathological UV range.");
            var xs = Intervals(minX, maxX, wrapU);
            var ys = Intervals(minY, maxY, wrapV);
            if ((long)xs.Count * ys.Count > 4096) throw new InvalidOperationException("UV triangle crosses too many wrap regions.");
            budget.Regions += (long)xs.Count * ys.Count;
            budget.Check();
            foreach (var x in xs)
                foreach (var y in ys)
                {
                    var part = Clip(Clip(Clip(Clip(polygon, 0, x.Min, true), 0, x.Max, false), 1, y.Min, true), 1, y.Max, false);
                    if (part.Count == 0) continue;
                    budget.Pieces++;
                    for (int i = 0; i < part.Count; i++)
                        part[i] = new Point(Map(part[i].X, x.Tile, wrapU) * width, Map(part[i].Y, y.Tile, wrapV) * height);
                    if (part.Count < 3) RasterTriangle(mask, width, height, part[0], part[part.Count - 1], part[0], budget);
                    else for (int i = 1; i + 1 < part.Count; i++) RasterTriangle(mask, width, height, part[0], part[i], part[i + 1], budget);
                }
        }

        private static List<Interval> Intervals(double min, double max, TextureWrapMode mode)
        {
            var result = new List<Interval>();
            if (mode == TextureWrapMode.Repeat || mode == TextureWrapMode.Mirror)
            {
                int first = (int)Math.Floor(min), last = (int)Math.Floor(max);
                if ((long)last - first > 4096) throw new InvalidOperationException("Excessive UV tiling.");
                for (int tile = first; tile <= last; tile++) result.Add(new Interval { Min = tile, Max = tile + 1, Tile = tile });
            }
            else if (mode == TextureWrapMode.Clamp || mode == TextureWrapMode.MirrorOnce)
            {
                var edges = mode == TextureWrapMode.Clamp ? new[] { -1000001d, 0d, 1d, 1000001d } :
                    new[] { -1000001d, -1d, 0d, 1d, 1000001d };
                for (int i = 0; i + 1 < edges.Length; i++)
                    if (max >= edges[i] && min <= edges[i + 1]) result.Add(new Interval { Min = edges[i], Max = edges[i + 1] });
            }
            else throw new InvalidOperationException("Unknown wrap mode.");
            return result;
        }

        private static double Map(double value, int tile, TextureWrapMode mode)
        {
            if (mode == TextureWrapMode.Repeat) return value - tile;
            if (mode == TextureWrapMode.Mirror) return (tile & 1) == 0 ? value - tile : 1 - (value - tile);
            if (mode == TextureWrapMode.MirrorOnce) value = Math.Abs(value);
            return Math.Max(0, Math.Min(1, value));
        }

        private static List<Point> Clip(List<Point> input, int axis, double boundary, bool greater)
        {
            var output = new List<Point>();
            if (input.Count == 0) return output;
            Point previous = input[input.Count - 1];
            double pd = (axis == 0 ? previous.X : previous.Y) - boundary;
            bool pi = greater ? pd >= 0 : pd <= 0;
            foreach (var current in input)
            {
                double d = (axis == 0 ? current.X : current.Y) - boundary;
                bool inside = greater ? d >= 0 : d <= 0;
                if (inside != pi)
                {
                    double t = pd / (pd - d);
                    output.Add(new Point(previous.X + t * (current.X - previous.X), previous.Y + t * (current.Y - previous.Y)));
                }
                if (inside) output.Add(current);
                previous = current; pd = d; pi = inside;
            }
            return output;
        }

        private static void RasterTriangle(bool[] mask, int width, int height, Point a, Point b, Point c, CoverageBudget budget)
        {
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))) - 1);
            int x1 = Math.Min(width - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))) - 1);
            int y1 = Math.Min(height - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            for (int y = y0; y <= y1; y++)
            {
                if ((y & 63) == 0) budget.Check();
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    int index = row + x;
                    if (mask[index]) continue;
                    if (++budget.Candidates > 100000000) budget.Check();
                    if (Overlaps(a, b, c, x, y)) mask[index] = true;
                }
            }
            budget.Check();
        }

        private static bool Overlaps(Point a, Point b, Point c, int x, int y)
        {
            if (Math.Max(a.X, Math.Max(b.X, c.X)) < x - 1e-8 || Math.Min(a.X, Math.Min(b.X, c.X)) > x + 1 + 1e-8 ||
                Math.Max(a.Y, Math.Max(b.Y, c.Y)) < y - 1e-8 || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > y + 1 + 1e-8) return false;
            return Axis(a, b, c, b.Y - a.Y, a.X - b.X, x, y) && Axis(a, b, c, c.Y - b.Y, b.X - c.X, x, y) &&
                Axis(a, b, c, a.Y - c.Y, c.X - a.X, x, y);
        }

        private static bool Axis(Point a, Point b, Point c, double nx, double ny, int x, int y)
        {
            double center = nx * (x + .5) + ny * (y + .5), radius = .5 * (Math.Abs(nx) + Math.Abs(ny));
            double p = nx * a.X + ny * a.Y, q = nx * b.X + ny * b.Y, r = nx * c.X + ny * c.Y;
            return Math.Max(p, Math.Max(q, r)) >= center - radius - 1e-8 && Math.Min(p, Math.Min(q, r)) <= center + radius + 1e-8;
        }

        // Square dilation is a conservative superset of a circular radius, including diagonal protection.
        public static bool[] Pad(bool[] source, int width, int height, int radius, TextureWrapMode u, TextureWrapMode v, Action cancel = null) =>
            Pad(source, width, height, radius, radius, u, v, cancel);

        internal static bool[] Pad(bool[] source, int width, int height, int radiusX, int radiusY, TextureWrapMode u, TextureWrapMode v, Action cancel = null)
        {
            // A full-axis radius already covers every source texel for supported wrap modes.
            radiusX = Math.Min(radiusX, width);
            radiusY = Math.Min(radiusY, height);
            var horizontal = new bool[source.Length];
            var result = new bool[source.Length];
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancel?.Invoke();
                int count = 0;
                for (int k = -radiusX; k <= radiusX; k++) if (source[y * width + WrapIndex(k, width, u)]) count++;
                for (int x = 0; x < width; x++)
                {
                    horizontal[y * width + x] = count > 0;
                    if (source[y * width + WrapIndex(x - radiusX, width, u)]) count--;
                    if (source[y * width + WrapIndex(x + radiusX + 1, width, u)]) count++;
                }
            }
            for (int x = 0; x < width; x++)
            {
                if ((x & 63) == 0) cancel?.Invoke();
                int count = 0;
                for (int k = -radiusY; k <= radiusY; k++) if (horizontal[WrapIndex(k, height, v) * width + x]) count++;
                for (int y = 0; y < height; y++)
                {
                    result[y * width + x] = count > 0;
                    if (horizontal[WrapIndex(y - radiusY, height, v) * width + x]) count--;
                    if (horizontal[WrapIndex(y + radiusY + 1, height, v) * width + x]) count++;
                }
            }
            return result;
        }

        private static int WrapIndex(int index, int size, TextureWrapMode mode)
        {
            if (mode == TextureWrapMode.Repeat) return ((index % size) + size) % size;
            if (mode == TextureWrapMode.Mirror)
            {
                index = ((index % (2 * size)) + 2 * size) % (2 * size);
                return index < size ? index : 2 * size - 1 - index;
            }
            if (mode == TextureWrapMode.MirrorOnce && index < 0) index = -index - 1;
            return Math.Max(0, Math.Min(size - 1, index));
        }
    }
}
