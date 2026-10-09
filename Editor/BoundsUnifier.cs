using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Unified Bounds: every skinned mesh gets the Hips as its root bone and one shared bounding box, with Update When
    // Offscreen off. Unity skips drawing a skinned mesh whenever its box is entirely off screen, so a box that is too small
    // (the common 1x1x1) makes meshes vanish in close framings, and different boxes or root bones keep meshes from merging.
    // The box is a cube around the Hips whose half-size is a proven reach: a vertex skinned to bone b is never farther from
    // the Hips than the length of b's chain down to the Hips, plus the vertex's distance from b, plus its largest blend
    // shape offsets, whatever the joints rotate to. That holds for every vertex of every qualifying mesh, including meshes
    // switched off, so nothing visible can be culled. A mesh keeps its own bounds when that reach is not fixed: a bone
    // between it and the Hips is constrained, moved by physics, animated in position or scale, or in a PhysBone that can
    // stretch; when it has no bones; or when it samples probes without an anchor (its bounds centre is then its lighting
    // point). It changes culling (meshes can draw when just off screen), so it is the component's Unified Bounds setting.
    internal static class BoundsUnifier
    {
        internal sealed class Result { public int Changed; public float Size; public readonly List<string> Kept = new List<string>(); }

        private const float Margin = 1.1f;

        internal static Result Run(AvatarAnalysis analysis)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var animator = root.GetComponent<Animator>();
            var hips = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (!hips) return result;
            Vector3 hipsScale = hips.lossyScale;
            if (Mathf.Abs(hipsScale.x) < 1e-6f || Mathf.Abs(hipsScale.y) < 1e-6f || Mathf.Abs(hipsScale.z) < 1e-6f) return result;

            var free = FreeTransforms(root);
            var chains = new Dictionary<Transform, float?>();
            float? Chain(Transform bone)
            {
                if (chains.TryGetValue(bone, out var known)) return known;
                float? length = null;
                if (bone == hips) length = 0;
                else if (bone.parent && !free.Contains(bone) &&
                         !analysis.IsAnimated(bone, p => p.StartsWith("m_LocalPosition", StringComparison.Ordinal) || p.StartsWith("m_LocalScale", StringComparison.Ordinal)))
                {
                    var parent = Chain(bone.parent);
                    if (parent.HasValue) length = parent.Value + Vector3.Distance(bone.position, bone.parent.position);
                }
                return chains[bone] = length;
            }

            var picked = new List<SkinnedMeshRenderer>();
            float radius = 0;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.sharedMesh || Exclusions.Excluded(renderer)) continue;
                bool probes = renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off;
                string why = probes && !renderer.probeAnchor ? "no probe anchor" : null;
                float reach = why == null ? Reach(renderer, Chain, out why) : 0;
                if (why != null) { result.Kept.Add(renderer.name + " (" + why + ")"); continue; }
                picked.Add(renderer);
                radius = Mathf.Max(radius, reach);
            }
            if (picked.Count == 0 || radius <= 0) return result;

            radius *= Margin;
            var size = new Vector3(radius / Mathf.Abs(hipsScale.x), radius / Mathf.Abs(hipsScale.y), radius / Mathf.Abs(hipsScale.z)) * 2;
            var bounds = new Bounds(Vector3.zero, size);
            foreach (var renderer in picked)
            {
                if (renderer.rootBone == hips && !renderer.updateWhenOffscreen && renderer.localBounds == bounds) continue;
                renderer.rootBone = hips;
                renderer.updateWhenOffscreen = false;
                renderer.localBounds = bounds;
                result.Changed++;
            }
            result.Size = radius * 2;
            if (result.Changed > 0) analysis.RescanReferences(); // Root bone references changed.
            return result;
        }

        // The farthest any vertex of this mesh can get from the Hips, or why that is unknown.
        private static float Reach(SkinnedMeshRenderer renderer, Func<Transform, float?> chain, out string why)
        {
            why = null;
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            if (bones.Length == 0 || bindposes.Length < bones.Length) { why = "no bones"; return 0; }

            // Per vertex, the sum of every blend shape's largest offset (mesh space): no weights between 0 and 1 reach farther.
            var vertices = mesh.vertices;
            var shapes = new float[vertices.Length];
            var deltas = new Vector3[vertices.Length];
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                var largest = new float[vertices.Length];
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    mesh.GetBlendShapeFrameVertices(s, f, deltas, null, null);
                    for (int i = 0; i < deltas.Length; i++) largest[i] = Mathf.Max(largest[i], deltas[i].magnitude);
                }
                for (int i = 0; i < shapes.Length; i++) shapes[i] += largest[i];
            }

            var matrices = new Matrix4x4[bones.Length];
            var scales = new float[bones.Length];
            var reaches = new float?[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                if (!bones[b]) continue;
                matrices[b] = bones[b].localToWorldMatrix * bindposes[b];
                scales[b] = Mathf.Max(matrices[b].GetColumn(0).magnitude, matrices[b].GetColumn(1).magnitude, matrices[b].GetColumn(2).magnitude);
                reaches[b] = chain(bones[b]);
            }

            float reach = 0;
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            for (int i = 0, w = 0; i < vertices.Length; i++)
            {
                int count = perVertex.Length > i ? perVertex[i] : 0;
                for (int k = 0; k < count; k++, w++)
                {
                    var weight = weights[w];
                    if (weight.weight <= 0) continue;
                    int b = weight.boneIndex;
                    if (b >= bones.Length || !bones[b]) { why = "a missing bone"; return 0; }
                    if (!reaches[b].HasValue) { why = bones[b].name + " can move freely or is outside the Hips"; return 0; }
                    float distance = reaches[b].Value + Vector3.Distance(matrices[b].MultiplyPoint3x4(vertices[i]), bones[b].position) + scales[b] * shapes[i];
                    reach = Mathf.Max(reach, distance);
                }
            }
            return reach;
        }

        // Transforms that something other than a joint rotation can move: constraint targets, physics bodies and joints, and
        // PhysBone chains that can stretch.
        private static HashSet<Transform> FreeTransforms(GameObject root)
        {
            var free = new HashSet<Transform>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component) continue;
                if (component is IConstraint || component is Rigidbody || component is Joint) { free.Add(component.transform); continue; }
                string type = component.GetType().Name;
                if (type.StartsWith("VRC", StringComparison.Ordinal) && type.EndsWith("Constraint", StringComparison.Ordinal))
                {
                    using (var serialized = new SerializedObject(component))
                        free.Add(serialized.FindProperty("TargetTransform")?.objectReferenceValue as Transform ?? component.transform);
                }
                else if (type == "VRCPhysBone")
                {
                    using (var serialized = new SerializedObject(component))
                    {
                        if ((serialized.FindProperty("maxStretch")?.floatValue ?? 1f) <= 0) continue;
                        var start = serialized.FindProperty("rootTransform")?.objectReferenceValue as Transform ?? component.transform;
                        free.UnionWith(start.GetComponentsInChildren<Transform>(true));
                    }
                }
            }
            return free;
        }
    }
}
