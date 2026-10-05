using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // A skinned mesh whose every vertex follows one bone at full weight needs no skinning: it becomes a plain
    // MeshRenderer on a new object under that bone, with the mesh baked into the bone's space, so every vertex lands
    // where skinning put it. The original object stays where it is (nothing is reparented).
    // Only when the result is provably the same:
    //  - One bone, weight exactly 1 everywhere; no blend shapes; no Cloth; nothing references or animates the
    //    renderer; neither the renderer's object nor the bone can be toggled; the bind pose does not mirror.
    //  - Object space: a skinned mesh is drawn in its root bone's frame with scale removed, a plain mesh in the
    //    bone's frame. Shaders only see the same distances when the bone's world scale is exactly 1 and cannot change
    //    (no scale animation or constraint above it). Shader effects that read absolute object-space positions must
    //    be off, so only audited lilToon 2.3.4 standard entries qualify, with Position dissolve, AudioLink position
    //    UVs and position-based layer dissolve off.
    //  - Probes and sorting: light and reflection probes are sampled at the anchor, or else the bounds centre. With
    //    the renderer's bounds updated every frame the centre is the same mesh box either way; with fixed bounds an
    //    anchor object at the old centre (under the root bone) keeps the sample point, and transparent materials,
    //    which sort by that centre, keep the mesh skinned.
    internal static class RigidAccessoryConverter
    {
        internal sealed class Result { public int Converted; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList())
            {
                if (Exclusions.Excluded(renderer) || !Eligible(renderer, analysis, swaps, out var bone) || Exclusions.Excluded(bone)) continue;
                Convert(renderer, bone, register);
                result.Converted++;
            }
            return result;
        }

        private static bool Eligible(SkinnedMeshRenderer renderer, AvatarAnalysis analysis, HashSet<Mesh> swaps, out Transform bone)
        {
            bone = null;
            var mesh = renderer.sharedMesh;
            if (!mesh || !renderer.enabled || mesh.blendShapeCount > 0 || swaps.Contains(mesh) || renderer.GetComponent<Cloth>() || renderer.HasPropertyBlock()) return false;
            if (analysis.BindingsOn(renderer).Any() || analysis.ReferencesTo(renderer).Any()) return false;
            if (analysis.ReferencesTo(mesh).Any(c => c != renderer)) return false;
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (mesh.vertexCount == 0 || perVertex.Length != mesh.vertexCount || perVertex.Any(c => c != 1) || weights.Any(w => w.weight != 1)) return false;
            int index = weights[0].boneIndex;
            if (weights.Any(w => w.boneIndex != index) || index >= renderer.bones.Length || !renderer.bones[index]) return false;
            bone = renderer.bones[index];
            if (mesh.bindposes[index].determinant <= 0) return false;
            if (analysis.ActivenessAnimated(renderer.gameObject) || analysis.ActivenessAnimated(bone.gameObject)) return false;
            // A skinned mesh draws whatever its bones' activeness; a plain mesh under an inactive bone would not.
            if (!bone.gameObject.activeInHierarchy) return false;
            if (!UnitScaleForever(bone, analysis)) return false;
            var rootBone = renderer.rootBone ? renderer.rootBone : renderer.transform;
            bool fixedBounds = !renderer.updateWhenOffscreen;
            bool probes = renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off;
            if (fixedBounds && !renderer.probeAnchor && probes && !UnitScaleForever(rootBone, analysis)) return false;
            // With Update When Offscreen the skinned centre is the box of the vertices in root-bone space, which no anchor
            // or plain mesh reproduces once the bone turns relative to the root bone: only unsampled or anchored probes.
            if (!fixedBounds && probes && !renderer.probeAnchor) return false;
            // Transparent materials sort by that centre too.
            var drawn = renderer.sharedMaterials.Concat(analysis.SwappedMaterials(renderer)).ToList(); // Swapped-in materials too.
            if (drawn.Any(m => !SkinnedMeshMerger.OrderIndependent(m))) return false;
            // lilToon pushes outline vertices out in object space. A plain mesh's object frame carries the avatar's runtime
            // scale (VRChat avatar scaling) and a skinned mesh's does not, so outline widths would change: no outline entries.
            return drawn.All(m => m && ObjectSpaceSafe(m) &&
                VertexStreamStripper.IsAuditedLilToonShader(m.shader, out bool outline) && !outline);
        }

        internal static bool UnitScaleForever(Transform transform, AvatarAnalysis analysis)
        {
            // Every local scale up to and including the root exactly 1 (a tolerance on the world scale would let a tiny scale through).
            for (var t = transform; t; t = t == analysis.Root.transform ? null : t.parent)
            {
                if (t.localScale.x != 1 || t.localScale.y != 1 || t.localScale.z != 1) return false;
                if (analysis.IsAnimated(t, p => p.StartsWith("m_LocalScale", StringComparison.Ordinal)) || analysis.ScaleChanges(t)) return false;
                if (t.GetComponents<Component>().Any(c => c is IConstraint || c && c.GetType().Name.StartsWith("VRC", StringComparison.Ordinal) && c.GetType().Name.EndsWith("Constraint", StringComparison.Ordinal))) return false;
            }
            return true;
        }

        // lilToon reads absolute object-space positions only for these features (lil_common_frag.hlsl, 2.3.4).
        internal static bool ObjectSpaceSafe(Material material)
        {
            if (!VertexStreamStripper.IsAuditedLilToonShader(material.shader, out _)) return false;
            float Get(string p) => material.HasProperty(p) ? material.GetFloat(p) : 0;
            Vector4 Vec(string p) => material.HasProperty(p) ? material.GetVector(p) : Vector4.zero;
            if (Vec("_DissolveParams").x == 3) return false;
            if (Vec("_Main2ndDissolveParams").x == 3 || Vec("_Main3rdDissolveParams").x == 3) return false;
            if (Get("_UseAudioLink") != 0 && Get("_AudioLinkUVMode") == 5) return false;
            return true;
        }

        private static void Convert(SkinnedMeshRenderer renderer, Transform bone, Action<Object, Object> register)
        {
            var mesh = renderer.sharedMesh;
            var bindpose = mesh.bindposes[mesh.GetAllBoneWeights()[0].boneIndex];
            var baked = Object.Instantiate(mesh);
            baked.name = mesh.name;
            baked.vertices = mesh.vertices.Select(v => bindpose.MultiplyPoint3x4(v)).ToArray();
            if (mesh.HasVertexAttribute(VertexAttribute.Normal))
                baked.normals = mesh.normals.Select(n => bindpose.MultiplyVector(n).normalized).ToArray(); // As skinning does.
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent))
                baked.tangents = mesh.tangents.Select(t => { var d = bindpose.MultiplyVector(t).normalized; return new Vector4(d.x, d.y, d.z, t.w); }).ToArray();
            baked.boneWeights = null;
            baked.bindposes = new Matrix4x4[0];
            baked.RecalculateBounds();

            var holder = new GameObject(renderer.name + " (Static)");
            holder.layer = renderer.gameObject.layer;
            holder.tag = renderer.gameObject.tag;
            holder.transform.SetParent(bone, false);
            holder.AddComponent<MeshFilter>().sharedMesh = baked;
            var target = holder.AddComponent<MeshRenderer>();
            target.sharedMaterials = renderer.sharedMaterials;
            target.shadowCastingMode = renderer.shadowCastingMode;
            target.receiveShadows = renderer.receiveShadows;
            target.lightProbeUsage = renderer.lightProbeUsage;
            target.reflectionProbeUsage = renderer.reflectionProbeUsage;
            target.lightProbeProxyVolumeOverride = renderer.lightProbeProxyVolumeOverride;
            target.motionVectorGenerationMode = renderer.motionVectorGenerationMode;
            target.allowOcclusionWhenDynamic = renderer.allowOcclusionWhenDynamic;
            target.renderingLayerMask = renderer.renderingLayerMask;
            target.sortingLayerID = renderer.sortingLayerID;
            target.sortingOrder = renderer.sortingOrder;
            target.rendererPriority = renderer.rendererPriority;
            target.probeAnchor = renderer.probeAnchor;
            if (!target.probeAnchor && !renderer.updateWhenOffscreen &&
                (renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off))
            {
                // Keep sampling where the fixed bounds' centre was: under the root bone, at that offset.
                var anchor = new GameObject(renderer.name + " (Probe Anchor)").transform;
                anchor.SetParent(renderer.rootBone ? renderer.rootBone : renderer.transform, false);
                anchor.localPosition = renderer.localBounds.center;
                target.probeAnchor = anchor;
            }
            register(mesh, baked);
            register(renderer, target);
            Object.DestroyImmediate(renderer);
        }
    }
}
