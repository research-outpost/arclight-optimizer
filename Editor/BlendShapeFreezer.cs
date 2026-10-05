using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Bakes blend shapes that nothing can change into the mesh, and drops shapes that move nothing.
    //  - Frozen: a shape no clip animates (or every clip only animates to the weight it already has) and VRChat does
    //    not drive (visemes, eyelids), at the same weight on every
    //    renderer using the mesh. Its delta at that weight is added to positions, normals and tangents, as Unity's
    //    skinning adds it each frame, and the shape is removed (weight 0: removed with nothing to add). Multi-frame
    //    shapes follow Unity's piecewise interpolation; a weight outside the frame range keeps the shape.
    //  - Removed: a shape whose every frame is zero, unless VRChat drives it (a clip animating it then animates
    //    nothing, as before).
    //  - Kept: shapes MMD worlds animate by name on the root "Body" mesh, everything on meshes with Cloth, meshes an
    //    animation swaps in or another component uses, and renderers that a component other than the avatar
    //    descriptor references. The descriptor's eyelid indices are renumbered to the new shape order.
    // Nothing changes when the avatar's animation cannot be read.
    internal static class BlendShapeFreezer
    {
        internal sealed class Result { public int Meshes, Frozen, Removed; public long Bytes; }

        // Shape names MMD dance worlds animate on a mesh named "Body" at the avatar root (from Avatar Optimizer's list
        // of the Yi MMD World and Xoriu sheets).
        internal static readonly HashSet<string> MmdShapes = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "Ah", "あ", "i", "Ch", "い", "u", "U", "う", "e", "E", "え", "o", "Oh", "お", "Niyari", "Grin", "にやり",
            "Mouse_2", "∧", "Wa", "ワ", "Omega", "ω", "Mouse_1", "▲", "MouseUP", "Mouth Horn Raise", "口角上げ", "MouseDW",
            "Mouth Horn Lower", "口角下げ", "MouseWD", "Mouth Side Widen", "口横広げ", "n", "ん", "Niyari2", "にやり２",
            "a 2", "あ２", "□", "ω□", "Smile", "にっこり", "Pero", "ぺろっ", "Bero-tehe", "てへぺろ", "Bero-tehe2", "てへぺろ２",
            "Blink", "まばたき", "Blink Happy", "笑い", "> <", "Close><", "はぅ", "EyeSmall", "Pupil", "瞳小", "Wink-c",
            "Wink 2 Right", "ｳｨﾝｸ２右", "Wink-b", "Wink 2", "ウィンク２", "Wink", "ウィンク", "Wink-a", "Wink Right", "ウィンク右",
            "Howawa", "Calm", "なごみ", "Jito-eye", "Stare", "じと目", "Ha!!!", "Surprised", "びっくり", "Kiri-eye", "Slant",
            "ｷﾘｯ", "EyeHeart", "Heart", "はぁと", "EyeStar", "Star Eye", "星目", "EyeFunky", "恐ろしい子！", "O O", "はちゅ目",
            "EyeSmall-v", "瞳縦潰れ", "EyeUnderli", "光下", "EyHi-Off", "ハイライト消", "EyeRef-off", "映り込み消", "Smily",
            "Cheerful", "にこり", "Up", "Upper", "上", "Down", "Lower", "下", "Serious", "真面目", "Trouble", "Sadness", "困る",
            "Get angry", "Anger", "怒り", "Front", "前", "Joy", "喜び", "Wao!?", "わぉ!?", "Howawa ω", "なごみω", "Wail",
            "悲しむ", "Hostility", "敵意", "Blush", "照れ", "ToothAnon", "歯無し下", "ToothBnon", "歯無し上", "涙"
        };

        private static bool AnimatedOnlyTo(AvatarAnalysis analysis, SkinnedMeshRenderer renderer, string shape, float weight)
        {
            if (analysis.BlendShapeDrivenByPlatform(renderer, shape)) return false;
            var bindings = analysis.BindingsOn(renderer, p => p == "blendShape." + shape).ToList();
            // A summed curve (additive, or a non-normalized Direct tree) adds its value, so only 0 leaves the weight unchanged.
            return bindings.Count > 0 && bindings.All(b => b.FloatCurve != null && b.FloatCurve.keys.Length > 0 && (!b.Summed || weight == 0) &&
                b.FloatCurve.keys.All(k => k.value == weight && AnimatorLayerMerger.Flat(k.inTangent) && AnimatorLayerMerger.Flat(k.outTangent)));
        }

        // keepMmdShapes: keep the MMD world shape names on the root "Body" mesh (the component's MMD Support setting).
        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, bool keepMmdShapes = true)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var users = new Dictionary<Mesh, List<SkinnedMeshRenderer>>();
            var blocked = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component is SkinnedMeshRenderer skinned && skinned.sharedMesh)
                {
                    if (Exclusions.Excluded(skinned)) blocked.Add(skinned.sharedMesh);
                    if (!users.TryGetValue(skinned.sharedMesh, out var list)) users.Add(skinned.sharedMesh, list = new List<SkinnedMeshRenderer>());
                    list.Add(skinned);
                }
                else if (component is MeshFilter filter && filter.sharedMesh) blocked.Add(filter.sharedMesh);
            }

            foreach (var pair in users)
            {
                var mesh = pair.Key;
                var renderers = pair.Value;
                if (mesh.blendShapeCount == 0 || blocked.Contains(mesh)) continue;
                if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer))) continue;
                if (renderers.Any(r => r.GetComponent<Cloth>() || analysis.IsAnimated(r, p => p == "m_Mesh") ||
                    analysis.ReferencesTo(r).Any(c => c.GetType().Name != "VRCAvatarDescriptor"))) continue;

                bool mmdBody = keepMmdShapes && renderers.Any(r => r.name == "Body" && r.transform.parent == root.transform);
                var freeze = new Dictionary<int, float>();
                var remove = new HashSet<int>();
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    string name = mesh.GetBlendShapeName(shape);
                    bool driven = renderers.Any(r => analysis.BlendShapeDrivenByPlatform(r, name)) || mmdBody && MmdShapes.Contains(name);
                    if (driven) continue;
                    if (IsZero(mesh, shape)) { remove.Add(shape); continue; }
                    float weight = renderers[0].GetBlendShapeWeight(shape);
                    if (renderers.Any(r => r.GetBlendShapeWeight(shape) != weight)) continue;
                    // Animated only to the weight it already has: every value the animator can write is that weight (an
                    // interpolation of equal values, or the Write Defaults value; curves that add to others must be 0). Its curves animate
                    // a shape that no longer exists, which changes nothing.
                    if (renderers.Any(r => analysis.BlendShapeAnimated(r, name) && !AnimatedOnlyTo(analysis, r, name, weight))) continue;
                    if (Frames(mesh, shape, weight) == null) continue;
                    freeze[shape] = weight;
                }
                if (freeze.Count + remove.Count == 0) continue;

                var copy = Build(mesh, freeze, remove);
                if (!copy) continue;
                long before = Size(mesh), after = Size(copy);
                register(mesh, copy);
                var kept = Enumerable.Range(0, mesh.blendShapeCount).Where(s => !freeze.ContainsKey(s) && !remove.Contains(s)).ToArray();
                foreach (var renderer in renderers)
                {
                    var weights = kept.Select(s => renderer.GetBlendShapeWeight(s)).ToArray();
                    renderer.sharedMesh = copy;
                    for (int i = 0; i < kept.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
                    RenumberEyelids(root, renderer, kept);
                }
                result.Meshes++;
                result.Frozen += freeze.Count;
                result.Removed += remove.Count;
                result.Bytes += before - after;
            }
            return result;
        }

        private static bool IsZero(Mesh mesh, int shape)
        {
            var v = new Vector3[mesh.vertexCount]; var n = new Vector3[mesh.vertexCount]; var t = new Vector3[mesh.vertexCount];
            for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
            {
                mesh.GetBlendShapeFrameVertices(shape, f, v, n, t);
                if (v.Any(x => !x.Equals(Vector3.zero)) || n.Any(x => !x.Equals(Vector3.zero)) || t.Any(x => !x.Equals(Vector3.zero))) return false;
            }
            return true;
        }

        // The frames and factors Unity applies at this weight, or null when the weight is outside what can be reproduced.
        internal static (int Frame, float Factor)[] Frames(Mesh mesh, int shape, float weight)
        {
            int count = mesh.GetBlendShapeFrameCount(shape);
            if (weight == 0) return Array.Empty<(int, float)>();
            if (count == 1) return new[] { (0, weight / mesh.GetBlendShapeFrameWeight(shape, 0)) };
            float first = mesh.GetBlendShapeFrameWeight(shape, 0), last = mesh.GetBlendShapeFrameWeight(shape, count - 1);
            if (first > 0 && weight >= 0 && weight < first) return new[] { (0, weight / first) };
            if (weight < first || weight > last) return null; // Extrapolation: not reproduced.
            for (int i = 1; i < count; i++)
            {
                float a = mesh.GetBlendShapeFrameWeight(shape, i - 1), b = mesh.GetBlendShapeFrameWeight(shape, i);
                if (weight <= b)
                {
                    float ratio = (weight - a) / (b - a);
                    return new[] { (i - 1, 1 - ratio), (i, ratio) };
                }
            }
            return null;
        }

        private static Mesh Build(Mesh mesh, Dictionary<int, float> freeze, HashSet<int> remove)
        {
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            var vertices = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents;
            bool hasNormals = normals.Length == vertices.Length, hasTangents = tangents.Length == vertices.Length;
            var dv = new Vector3[vertices.Length]; var dn = new Vector3[vertices.Length]; var dt = new Vector3[vertices.Length];
            foreach (var pair in freeze.OrderBy(p => p.Key))
                foreach (var (frame, factor) in Frames(mesh, pair.Key, pair.Value))
                {
                    mesh.GetBlendShapeFrameVertices(pair.Key, frame, dv, dn, dt);
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        vertices[i] += dv[i] * factor;
                        if (hasNormals) normals[i] += dn[i] * factor;
                        if (hasTangents) tangents[i] += (Vector4)(dt[i] * factor);
                    }
                }
            copy.ClearBlendShapes();
            copy.vertices = vertices;
            if (hasNormals) copy.normals = normals;
            if (hasTangents) copy.tangents = tangents;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                if (freeze.ContainsKey(shape) || remove.Contains(shape)) continue;
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    copy.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f), dv, dn, dt);
                }
            }
            copy.bounds = mesh.bounds;
            return copy;
        }

        // Approximate stored size: base vertex data plus, per frame, an index and three deltas for each vertex it moves
        // (Unity stores blend shapes sparsely).
        private static long Size(Mesh mesh)
        {
            long entries = 0;
            int n = mesh.vertexCount;
            Vector3[] v = new Vector3[n], nr = new Vector3[n], t = new Vector3[n];
            for (int s = 0; s < mesh.blendShapeCount; s++)
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    mesh.GetBlendShapeFrameVertices(s, f, v, nr, t);
                    for (int i = 0; i < n; i++)
                        if (v[i].x != 0 || v[i].y != 0 || v[i].z != 0 || nr[i].x != 0 || nr[i].y != 0 || nr[i].z != 0 || t[i].x != 0 || t[i].y != 0 || t[i].z != 0) entries++;
                }
            return MeshAndAudioOptimizer.MeshBytes(mesh) + entries * 40;
        }

        // The descriptor stores eyelid shapes by index; point them at the same shapes in the new order.
        internal static void RenumberEyelids(GameObject root, SkinnedMeshRenderer renderer, int[] kept)
        {
            foreach (var descriptor in root.GetComponents<Component>().Where(c => c && c.GetType().Name == "VRCAvatarDescriptor"))
                using (var serialized = new SerializedObject(descriptor))
                {
                    if (serialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh")?.objectReferenceValue != renderer) continue;
                    var indices = serialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                    if (indices == null) continue;
                    for (int i = 0; i < indices.arraySize; i++)
                    {
                        var element = indices.GetArrayElementAtIndex(i);
                        int next = Array.IndexOf(kept, element.intValue);
                        element.intValue = next >= 0 ? next : -1;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
        }
    }
}
