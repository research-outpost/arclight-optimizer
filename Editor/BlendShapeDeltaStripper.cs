using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Blend shape frames keep their normal and tangent deltas even when the mesh has no normals or tangents for them to
    // move (typically tangents, after the vertex stream stripper removed a channel no material reads). Such deltas
    // change nothing; the frame is stored without them (less blend shape memory, and vertices whose only delta was one
    // of them drop out of Unity's sparse blend shape data). Unity already drops all-zero deltas when a frame is added.
    // Skipped on avatars using d4rk Avatar Optimizer, whose merge fills missing tangents with zeros.
    internal static class BlendShapeDeltaStripper
    {
        internal sealed class Result { public int Meshes, Frames; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            if (D4rkOrdering.MayRun(root)) return result;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var group in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh).GroupBy(r => r.sharedMesh))
            {
                var mesh = group.Key;
                if (mesh.blendShapeCount == 0 || swaps.Contains(mesh) || group.Any(Exclusions.Excluded) ||
                    root.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh == mesh && Exclusions.Excluded(f))) continue;
                if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer) && !(c is MeshFilter))) continue;
                var copy = Strip(mesh, out int frames);
                if (!copy) continue;
                register(mesh, copy);
                foreach (var renderer in group) SkinnedMeshMerger.Rebind(renderer, () => renderer.sharedMesh = copy);
                // A plain mesh renderer sharing the mesh ignores blend shapes; it takes the copy too, so only one ships.
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh == mesh)) filter.sharedMesh = copy;
                result.Meshes++;
                result.Frames += frames;
            }
            return result;
        }

        // A copy whose frames keep only normal and tangent deltas the mesh has a channel for, or null when none change.
        internal static Mesh Strip(Mesh mesh, out int changedFrames)
        {
            changedFrames = 0;
            var stored = StoredDeltas(mesh);
            if (stored == null) return null;
            bool normals = mesh.HasVertexAttribute(VertexAttribute.Normal), tangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            int count = mesh.vertexCount;
            var plan = new List<(string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt)>();
            int frame = 0;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++, frame++)
                {
                    Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    bool keepNormals = stored[frame].Normals && normals, keepTangents = stored[frame].Tangents && tangents;
                    if (keepNormals != stored[frame].Normals || keepTangents != stored[frame].Tangents) changedFrames++;
                    plan.Add((mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f), dv, keepNormals ? dn : null, keepTangents ? dt : null));
                }
            if (changedFrames == 0) return null;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.ClearBlendShapes();
            foreach (var p in plan) copy.AddBlendShapeFrame(p.Name, p.Weight, p.Dv, p.Dn, p.Dt);
            copy.bounds = mesh.bounds;
            return copy;
        }

        // Whether each frame (in shape, then frame order) stores normal and tangent deltas; null when unreadable.
        internal static (bool Normals, bool Tangents)[] StoredDeltas(Mesh mesh)
        {
            using (var serialized = new SerializedObject(mesh))
            {
                var shapes = serialized.FindProperty("m_Shapes.shapes");
                if (shapes == null) return null;
                var frames = Enumerable.Range(0, shapes.arraySize).Select(i => shapes.GetArrayElementAtIndex(i))
                    .Select(s => (s.FindPropertyRelative("hasNormals")?.boolValue ?? true, s.FindPropertyRelative("hasTangents")?.boolValue ?? true)).ToArray();
                int total = Enumerable.Range(0, mesh.blendShapeCount).Sum(mesh.GetBlendShapeFrameCount);
                return frames.Length == total ? frames : null;
            }
        }
    }
}
