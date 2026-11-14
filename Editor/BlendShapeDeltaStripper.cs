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
    // Skipped on avatars using d4rk Avatar Optimizer, whose merge fills missing tangents with zeros, until after its merge
    // (afterD4rk, on its final meshes; PostOptimizerMerge).
    internal static class BlendShapeDeltaStripper
    {
        internal sealed class Result { public int Meshes, Frames, DroppedFrames; }

        // afterD4rk: d4rk's merge has run (see VertexStreamStripper.Run); eligible: when given, only meshes it accepts are rebuilt.
        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, bool afterD4rk = false, Func<Mesh, bool> eligible = null)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            if (!afterD4rk && RivalState.Of(root).StructureChanges) return result;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var group in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh).GroupBy(r => r.sharedMesh))
            {
                var mesh = group.Key;
                if (mesh.blendShapeCount == 0 || !mesh.isReadable || eligible != null && !eligible(mesh) || swaps.Contains(mesh) || group.Any(Exclusions.Excluded) ||
                    root.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh == mesh && Exclusions.Excluded(f))) continue;
                if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer) && !(c is MeshFilter))) continue;
                var copy = Strip(mesh, out int frames, out int dropped);
                if (!copy) continue;
                register(mesh, copy);
                foreach (var renderer in group) SkinnedMeshMerger.Rebind(renderer, () => renderer.sharedMesh = copy);
                // A plain mesh renderer sharing the mesh ignores blend shapes; it takes the copy too, so only one ships.
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh == mesh)) filter.sharedMesh = copy;
                result.Meshes++;
                result.Frames += frames;
                result.DroppedFrames += dropped;
            }
            return result;
        }

        // A copy whose frames keep only normal and tangent deltas the mesh has a channel for, or null when none change.
        internal static Mesh Strip(Mesh mesh, out int changedFrames, out int droppedFrames)
        {
            changedFrames = 0;
            droppedFrames = 0;
            var stored = StoredDeltas(mesh);
            if (stored == null) return null;
            bool normals = mesh.HasVertexAttribute(VertexAttribute.Normal), tangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            int count = mesh.vertexCount;
            var plan = new List<(string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt)>();
            int frame = 0;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                var frames = new List<(string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt)>();
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++, frame++)
                {
                    Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    bool keepNormals = stored[frame].Normals && normals, keepTangents = stored[frame].Tangents && tangents;
                    if (keepNormals != stored[frame].Normals || keepTangents != stored[frame].Tangents) changedFrames++;
                    frames.Add((mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f), dv, keepNormals ? dn : null, keepTangents ? dt : null));
                }
                droppedFrames += DropRedundant(frames);
                plan.AddRange(frames);
            }
            if (changedFrames + droppedFrames == 0) return null;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.ClearBlendShapes();
            foreach (var p in plan) copy.AddBlendShapeFrame(p.Name, p.Weight, p.Dv, p.Dn, p.Dt);
            copy.bounds = mesh.bounds;
            return copy;
        }

        // Drops interior frames whose deltas are exactly their neighbours' linear interpolation evaluated at the dropped
        // frame's own weight: Unity interpolates piecewise-linearly in weight, so the surviving segment reproduces the
        // dropped frame exactly and it carries no geometry. Exact float equality only (the same lerp Unity performs); a
        // frame that differs by one float step stays. Returns how many frames were dropped.
        private static int DropRedundant(List<(string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt)> frames)
        {
            int dropped = 0;
            for (int i = frames.Count - 2; i >= 1; i--)
            {
                if (!Redundant(frames[i - 1], frames[i], frames[i + 1])) continue;
                frames.RemoveAt(i);
                dropped++;
            }
            return dropped;
        }

        private static bool Redundant((string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt) a,
            (string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt) mid,
            (string Name, float Weight, Vector3[] Dv, Vector3[] Dn, Vector3[] Dt) b)
        {
            // Frames are authored in increasing weight; a repeated or descending weight has no interpolated point.
            if (!(b.Weight > a.Weight) || !(mid.Weight > a.Weight) || !(mid.Weight < b.Weight)) return false;
            float t = (mid.Weight - a.Weight) / (b.Weight - a.Weight);
            return Interpolates(a.Dv, b.Dv, mid.Dv, t) && Interpolates(a.Dn, b.Dn, mid.Dn, t) && Interpolates(a.Dt, b.Dt, mid.Dt, t);
        }

        // Every plane of `mid` equals a + (b - a) * t in the same float arithmetic Unity uses.
        private static bool Interpolates(Vector3[] a, Vector3[] b, Vector3[] mid, float t)
        {
            // A frame that stores no delta is only redundant when its neighbours store none either: if they do, the
            // dropped frame would leave the surface un-deformed at its own weight.
            if (mid == null) return a == null && b == null;
            if (a == null || b == null) return false;
            for (int i = 0; i < mid.Length; i++)
            {
                if (a[i].x + (b[i].x - a[i].x) * t != mid[i].x || a[i].y + (b[i].y - a[i].y) * t != mid[i].y ||
                    a[i].z + (b[i].z - a[i].z) * t != mid[i].z) return false;
            }
            return true;
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
