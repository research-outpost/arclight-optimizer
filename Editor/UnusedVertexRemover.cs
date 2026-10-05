using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Smaller vertex data with the same pixels:
    //  - Vertices identical in every input the vertex shader gets (every vertex stream byte, bone weights and every
    //    blend shape delta) produce identical results; indices use the first of them and the rest go.
    //  - Vertices no index uses are never drawn; they go, with their weights and blend shape deltas.
    // Vertex data is copied byte for byte in its original formats, and the copy is checked against the original
    // before it is used. Vertices are renumbered, so meshes are skipped when any material (including animation swaps)
    // can read vertex numbering, when Cloth simulates them, and for skinned renderers that compute their bounds from
    // the vertices every frame (Update When Offscreen), whose bounds could shrink; those only lose exact duplicates.
    internal static class UnusedVertexRemover
    {
        internal sealed class Result { public int Meshes, Vertices; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var users = analysis.Root.GetComponentsInChildren<Renderer>(true)
                .Select(r => (Renderer: r, Mesh: MeshOf(r))).Where(p => p.Mesh).GroupBy(p => p.Mesh)
                .Where(g => !g.Any(p => Exclusions.Excluded(p.Renderer))); // A mesh an excluded renderer uses stays as it is.
            foreach (var group in users)
            {
                var mesh = group.Key;
                if (swaps.Contains(mesh)) continue;
                if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer) && !(c is MeshFilter))) continue;
                // Extra vertex streams override the mesh's own attributes, and a particle shape can emit from a renderer's vertices,
                // edges or triangles.
                if (group.Any(p => p.Renderer is MeshRenderer m && m.additionalVertexStreams || analysis.ReferencesTo(p.Renderer).Any(c => c is ParticleSystem))) continue;
                if (group.Any(p => p.Renderer.GetComponent<Cloth>())) continue;
                // Update When Offscreen bounds come from the vertices: only welding, which removes exact duplicates, is safe there.
                bool weldOnly = group.Any(p => p.Renderer is SkinnedMeshRenderer s && s.updateWhenOffscreen);
                if (group.Any(p => p.Renderer.sharedMaterials.Concat(analysis.SwappedMaterials(p.Renderer)).Any(SkinnedMeshMerger.ReadsVertexId))) continue;
                var copy = Compact(mesh, WeldMap(mesh), out int vertices, weldOnly);
                if (!copy) continue;
                register(mesh, copy);
                foreach (var (renderer, _) in group)
                {
                    if (renderer is SkinnedMeshRenderer skinned) SkinnedMeshMerger.Rebind(skinned, () => skinned.sharedMesh = copy);
                    else renderer.GetComponent<MeshFilter>().sharedMesh = copy;
                }
                result.Meshes++;
                result.Vertices += vertices;
            }
            return result;
        }

        // A copy without unused vertices, or null when there are none or the copy differs. weld (or null) maps each
        // vertex to an identical one indices use instead.
        internal static Mesh Compact(Mesh mesh, int[] weld, out int removedVertices, bool weldOnly = false)
        {
            removedVertices = 0;
            int count = mesh.vertexCount;
            var used = new bool[count];
            var indices = Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetIndices(s, true)).ToArray();
            if (weld != null) foreach (var list in indices) for (int i = 0; i < list.Length; i++) list[i] = weld[list[i]];
            foreach (var list in indices) foreach (int i in list) used[i] = true;
            // weldOnly: keep every vertex but the duplicates welded away, even ones no index uses.
            if (weldOnly) for (int v = 0; v < count; v++) used[v] |= weld == null || weld[v] == v;
            var map = new int[count];
            int next = 0;
            for (int v = 0; v < count; v++) map[v] = used[v] ? next++ : -1;
            var kept = Enumerable.Range(0, count).Where(v => used[v]).ToArray();

            var perVertex = mesh.GetBonesPerVertex().ToArray();
            var weights = mesh.GetAllBoneWeights().ToArray();
            var newPerVertex = new List<byte>(kept.Length);
            var newWeights = new List<BoneWeight1>(weights.Length);
            if (perVertex.Length == count)
            {
                var start = new int[count];
                for (int v = 1; v < count; v++) start[v] = start[v - 1] + perVertex[v - 1];
                foreach (int v in kept)
                {
                    newPerVertex.Add(perVertex[v]);
                    newWeights.AddRange(Enumerable.Range(start[v], perVertex[v]).Select(i => weights[i]));
                }
            }
            removedVertices = count - kept.Length;
            if (removedVertices == 0) return null;

            var copy = Rebuild(mesh, kept, map, indices);
            copy.name = mesh.name;
            if (perVertex.Length == count)
                using (var a = new NativeArray<byte>(newPerVertex.ToArray(), Allocator.Temp))
                using (var b = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
                    copy.SetBoneWeights(a, b);
            copy.bounds = mesh.bounds;
            MeshAndAudioOptimizer.KeepUvDensity(copy, new[] { mesh });
            if (Same(mesh, copy, kept, weld, newPerVertex, newWeights)) return copy;
            Object.DestroyImmediate(copy);
            removedVertices = 0;
            return null;
        }

        private static Mesh Rebuild(Mesh mesh, int[] kept, int[] map, int[][] indices)
        {
            var attributes = mesh.GetVertexAttributes().Where(d => d.attribute != VertexAttribute.BlendWeight && d.attribute != VertexAttribute.BlendIndices).ToArray();
            var copy = new Mesh { indexFormat = mesh.indexFormat };
            copy.SetVertexBufferParams(kept.Length, attributes);
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                var source = Enumerable.Range(0, data.vertexBufferCount).Select(s => data.GetVertexData<byte>(s)).ToArray();
                foreach (int stream in attributes.Select(d => d.stream).Distinct())
                {
                    int stride = copy.GetVertexBufferStride(stream);
                    var bytes = new byte[stride * kept.Length];
                    foreach (var d in attributes.Where(d => d.stream == stream))
                    {
                        int size = Size(d.format) * d.dimension;
                        int from = data.GetVertexAttributeOffset(d.attribute), fromStride = data.GetVertexBufferStride(data.GetVertexAttributeStream(d.attribute));
                        var src = source[data.GetVertexAttributeStream(d.attribute)];
                        int to = copy.GetVertexAttributeOffset(d.attribute);
                        for (int v = 0; v < kept.Length; v++)
                            NativeArray<byte>.Copy(src, kept[v] * fromStride + from, bytes, v * stride + to, size);
                    }
                    copy.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                }
            }
            copy.subMeshCount = mesh.subMeshCount;
            for (int s = 0; s < mesh.subMeshCount; s++) copy.SetIndices(indices[s].Select(i => map[i]).ToArray(), mesh.GetTopology(s), s, false, 0);
            copy.bindposes = mesh.bindposes;
            var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    copy.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f),
                        kept.Select(v => dv[v]).ToArray(), kept.Select(v => dn[v]).ToArray(), kept.Select(v => dt[v]).ToArray());
                }
            return copy;
        }

        // Each vertex mapped to the first vertex identical to it in every stream byte, bone weight and blend shape delta
        // (itself when there is none), or null when no two vertices are identical.
        internal static int[] WeldMap(Mesh mesh)
        {
            int count = mesh.vertexCount;
            var map = Enumerable.Range(0, count).ToArray();
            var perVertex = mesh.GetBonesPerVertex().ToArray();
            var weights = mesh.GetAllBoneWeights().ToArray();
            var start = new int[perVertex.Length];
            for (int v = 1; v < perVertex.Length; v++) start[v] = start[v - 1] + perVertex[v - 1];
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                var streams = Enumerable.Range(0, data.vertexBufferCount).Select(s => (Bytes: data.GetVertexData<byte>(s), Stride: data.GetVertexBufferStride(s)))
                    .Where(s => s.Stride > 0).ToArray();
                bool Same(int a, int b)
                {
                    foreach (var (bytes, stride) in streams)
                        for (int i = 0; i < stride; i++)
                            if (bytes[a * stride + i] != bytes[b * stride + i]) return false;
                    if (perVertex.Length == 0) return true;
                    if (perVertex[a] != perVertex[b]) return false;
                    for (int i = 0; i < perVertex[a]; i++)
                        if (!weights[start[a] + i].Equals(weights[start[b] + i])) return false;
                    return true;
                }
                ulong Hash(int v)
                {
                    ulong h = 14695981039346656037;
                    foreach (var (bytes, stride) in streams)
                        for (int i = 0; i < stride; i++) h = (h ^ bytes[v * stride + i]) * 1099511628211;
                    return h;
                }
                var firsts = new Dictionary<ulong, List<int>>();
                for (int v = 0; v < count; v++)
                {
                    ulong h = Hash(v);
                    if (!firsts.TryGetValue(h, out var candidates)) { firsts[h] = new List<int> { v }; continue; }
                    int match = candidates.FindIndex(c => Same(c, v));
                    if (match >= 0) map[v] = candidates[match];
                    else candidates.Add(v);
                }
            }
            if (Enumerable.Range(0, count).All(v => map[v] == v)) return null;
            // Blend shape deltas, a frame at a time: a vertex whose deltas differ from its match keeps its own data.
            Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    for (int v = 0; v < count; v++)
                    {
                        int m = map[v];
                        if (m != v && !(dv[v].Equals(dv[m]) && dn[v].Equals(dn[m]) && dt[v].Equals(dt[m]))) map[v] = v;
                    }
                }
            return Enumerable.Range(0, count).All(v => map[v] == v) ? null : map;
        }

        private static int Size(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                default: return 1;
            }
        }

        // Every vertex input of the copy equals the original's at the kept vertices, and every index points at the same one.
        private static bool Same(Mesh a, Mesh b, int[] kept, int[] weld, List<byte> perVertex, List<BoneWeight1> weights)
        {
            if (b.vertexCount != kept.Length || a.subMeshCount != b.subMeshCount || a.blendShapeCount != b.blendShapeCount) return false;
            var layout = new Func<Mesh, HashSet<(VertexAttribute, VertexAttributeFormat, int)>>(m =>
                new HashSet<(VertexAttribute, VertexAttributeFormat, int)>(m.GetVertexAttributes().Select(d => (d.attribute, d.format, d.dimension))));
            if (!layout(a).SetEquals(layout(b))) return false;
            bool Channel<T>(T[] x, T[] y) => x.Length == 0 ? y.Length == 0 : y.Length == kept.Length && kept.Select(v => x[v]).SequenceEqual(y);
            if (!Channel(a.vertices, b.vertices) || !Channel(a.normals, b.normals) || !Channel(a.tangents, b.tangents) || !Channel(a.colors, b.colors)) return false;
            for (int i = 0; i < 8; i++)
            {
                List<Vector4> x = new List<Vector4>(), y = new List<Vector4>();
                a.GetUVs(i, x); b.GetUVs(i, y);
                if (!Channel(x.ToArray(), y.ToArray())) return false;
            }
            if (!a.bindposes.SequenceEqual(b.bindposes)) return false;
            if (a.GetBonesPerVertex().Length > 0 && (!b.GetBonesPerVertex().SequenceEqual(perVertex) || !b.GetAllBoneWeights().SequenceEqual(weights))) return false;
            for (int s = 0; s < a.subMeshCount; s++)
            {
                if (a.GetTopology(s) != b.GetTopology(s)) return false;
                var x = a.GetIndices(s, true); var y = b.GetIndices(s, true);
                if (x.Length != y.Length || Enumerable.Range(0, x.Length).Any(i => kept[y[i]] != (weld == null ? x[i] : weld[x[i]]))) return false;
            }
            Vector3[] da = new Vector3[a.vertexCount], na = new Vector3[a.vertexCount], ta = new Vector3[a.vertexCount];
            Vector3[] db = new Vector3[b.vertexCount], nb = new Vector3[b.vertexCount], tb = new Vector3[b.vertexCount];
            for (int s = 0; s < a.blendShapeCount; s++)
            {
                if (a.GetBlendShapeName(s) != b.GetBlendShapeName(s) || a.GetBlendShapeFrameCount(s) != b.GetBlendShapeFrameCount(s)) return false;
                for (int f = 0; f < a.GetBlendShapeFrameCount(s); f++)
                {
                    if (a.GetBlendShapeFrameWeight(s, f) != b.GetBlendShapeFrameWeight(s, f)) return false;
                    a.GetBlendShapeFrameVertices(s, f, da, na, ta); b.GetBlendShapeFrameVertices(s, f, db, nb, tb);
                    if (!Channel(da, db) || !Channel(na, nb) || !Channel(ta, tb)) return false;
                }
            }
            return true;
        }

        private static Mesh MeshOf(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
            : renderer is MeshRenderer && renderer.GetComponent<MeshFilter>() is MeshFilter filter && filter ? filter.sharedMesh : null;
    }
}
