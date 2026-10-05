using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Removes triangles that can never cover a pixel, in any pose or blend shape state:
    //  - two corners use the same vertex index;
    //  - two corners use different vertices that are identical in every input the vertex shader gets (position,
    //    normal, tangent, colour, every UV, bone weights and every blend shape frame delta), so they land on the same
    //    point whatever the deformation.
    // Triangles that are only flat in the bind pose are kept. Vertices are never touched, so vertex numbering stays.
    // Only meshes whose every material (including animation swaps) has no tessellation or geometry stage, which could
    // turn a flat triangle into visible geometry, and does not read SV_PrimitiveID, which removal would renumber.
    internal static class DegenerateTriangleRemover
    {
        internal sealed class Result { public int Meshes, Triangles; }

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
                if (group.Any(p => p.Renderer.GetComponent<Cloth>())) continue; // Cloth builds its constraints from the triangles.
                if (group.Any(p => p.Renderer.sharedMaterials.Concat(analysis.SwappedMaterials(p.Renderer)).Any(m => !TriangleCountInvisible(m)) || SkinnedMeshMerger.ReadsVertexId(p.Renderer, analysis))) continue;
                var keep = Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTopology(s) == MeshTopology.Triangles ? Survivors(mesh, s) : null).ToArray();
                int removed = Enumerable.Range(0, mesh.subMeshCount).Sum(s => keep[s] == null ? 0 : (int)(mesh.GetIndexCount(s) - keep[s].Length) / 3);
                if (removed == 0) continue;

                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (keep[s] != null) copy.SetIndices(keep[s], MeshTopology.Triangles, s, false, (int)mesh.GetBaseVertex(s));
                copy.bounds = mesh.bounds;
                register(mesh, copy);
                foreach (var (renderer, _) in group)
                {
                    if (renderer is SkinnedMeshRenderer skinned) SkinnedMeshMerger.Rebind(skinned, () => skinned.sharedMesh = copy);
                    else renderer.GetComponent<MeshFilter>().sharedMesh = copy;
                }
                result.Meshes++;
                result.Triangles += removed;
            }
            return result;
        }

        // The submesh's indices (relative to its base vertex) without its never-visible triangles.
        internal static int[] Survivors(Mesh mesh, int submesh)
        {
            var indices = mesh.GetIndices(submesh, false);
            int baseVertex = (int)mesh.GetBaseVertex(submesh);
            Vertices identical = null;
            var kept = new List<int>(indices.Length);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                bool flat = a == b || b == c || a == c;
                if (!flat)
                {
                    identical = identical ?? new Vertices(mesh);
                    flat = identical.Same(a + baseVertex, b + baseVertex) || identical.Same(b + baseVertex, c + baseVertex) || identical.Same(a + baseVertex, c + baseVertex);
                }
                if (!flat) { kept.Add(a); kept.Add(b); kept.Add(c); }
            }
            return kept.ToArray();
        }

        // Exact comparison of everything a vertex shader can get for two vertices (Equals, not Unity's approximate ==).
        private sealed class Vertices
        {
            private readonly Mesh mesh;
            private readonly Vector3[] positions;
            private List<Func<int, int, bool>> rest;

            internal Vertices(Mesh mesh) { this.mesh = mesh; positions = mesh.vertices; }

            internal bool Same(int a, int b) => positions[a].Equals(positions[b]) && (rest ?? (rest = Channels())).All(same => same(a, b));

            private List<Func<int, int, bool>> Channels()
            {
                var channels = new List<Func<int, int, bool>>();
                if (mesh.HasVertexAttribute(VertexAttribute.Normal)) { var n = mesh.normals; channels.Add((a, b) => n[a].Equals(n[b])); }
                if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) { var t = mesh.tangents; channels.Add((a, b) => t[a].Equals(t[b])); }
                if (mesh.HasVertexAttribute(VertexAttribute.Color)) { var c = mesh.colors; channels.Add((a, b) => c[a].Equals(c[b])); }
                for (int i = 0; i < 8; i++)
                {
                    if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + i)) continue;
                    var uv = new List<Vector4>();
                    mesh.GetUVs(i, uv);
                    channels.Add((a, b) => uv[a].Equals(uv[b]));
                }
                var perVertex = mesh.GetBonesPerVertex();
                if (perVertex.Length > 0)
                {
                    var counts = perVertex.ToArray();
                    var weights = mesh.GetAllBoneWeights().ToArray();
                    var start = new int[counts.Length];
                    for (int v = 1; v < counts.Length; v++) start[v] = start[v - 1] + counts[v - 1];
                    channels.Add((a, b) => counts[a] == counts[b] && Enumerable.Range(0, counts[a]).All(i =>
                        weights[start[a] + i].boneIndex == weights[start[b] + i].boneIndex && weights[start[a] + i].weight.Equals(weights[start[b] + i].weight)));
                }
                int count = mesh.vertexCount;
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
                        mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                        channels.Add((a, b) => dv[a].Equals(dv[b]) && dn[a].Equals(dn[b]) && dt[a].Equals(dt[b]));
                    }
                return channels;
            }
        }

        internal static readonly Dictionary<string, bool> Safe = new Dictionary<string, bool>();
        private static readonly ShaderType[] TriangleStages = { ShaderType.Vertex, ShaderType.Fragment };
        private static readonly VRChatMobileAdapter MobileShaders = new VRChatMobileAdapter();

        // Whether removing never-visible triangles cannot change what this material draws: no hull, domain or geometry
        // stage in any pass, and no stage that reads SV_PrimitiveID.
        internal static bool TriangleCountInvisible(Material material)
        {
            if (!material) return true;
            if (!material.shader || SkinnedMeshMerger.Broken(material.shader)) return false;
            // As in SkinnedMeshMerger.ReadsVertexId: on Android only VRChat's pinned mobile shaders are trusted to the Direct3D answer.
            if (VertexStreamStripper.Android && !MobileShaders.Matches(material)) return false;
            string key = VertexStreamStripper.CacheKey(material);
            if (Safe.TryGetValue(key, out bool cached)) return cached;
            string fact = ShaderFacts.Key(material, "d3d", "no-hull-domain-geometry|" + string.Join(",", TriangleStages) + "|SV_PrimitiveID");
            if (ShaderFacts.TryGet("triangles", fact, out string known)) return Safe[key] = known == "1";
            bool clean = true;
            bool safe = true;
            try
            {
                var sub = ShaderUtil.GetShaderData(material.shader).ActiveSubshader;
                for (int p = 0; p < sub.PassCount && safe; p++)
                {
                    var pass = sub.GetPass(p);
                    if (pass.HasShaderStage(ShaderType.Hull) || pass.HasShaderStage(ShaderType.Domain) || pass.HasShaderStage(ShaderType.Geometry)) { safe = false; break; }
                    foreach (var stage in TriangleStages)
                    {
                        if (!pass.HasShaderStage(stage)) continue;
                        var info = pass.CompileVariant(stage, material.shaderKeywords, ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                        if (!info.Success) clean = false; // Cautious, but possibly a one-off compiler failure: not kept on disk.
                        if (!info.Success || DxbcInputs.Has(info.ShaderData, "SV_PrimitiveID")) { safe = false; break; }
                    }
                }
            }
            catch (Exception) { safe = false; clean = false; }
            // As in SkinnedMeshMerger.ReadsVertexId: clear an error the compile recorded (Broken() returned for an earlier one).
            finally { if (ShaderUtil.ShaderHasError(material.shader)) ShaderUtil.ClearShaderMessages(material.shader); }
            if (clean) ShaderFacts.Put("triangles", fact, safe ? "1" : "0");
            return Safe[key] = safe;
        }

        private static Mesh MeshOf(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
            : renderer is MeshRenderer && renderer.GetComponent<MeshFilter>() is MeshFilter filter && filter ? filter.sharedMesh : null;
    }
}
