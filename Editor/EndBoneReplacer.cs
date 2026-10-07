using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Replaces a PhysBone chain's end bones with its Endpoint Position, so the avatar has fewer transforms and the solver
    // fewer real bones. An end bone only gives its parent a point to aim at; the endpoint is that same point, as a virtual
    // bone at the same local position. VRC.Dynamics evaluates every curve at i / (deepest index + e - 1) and the radius at
    // i / (deepest index + e), where e is 1 with an endpoint (VRCPhysBoneBase.CalcBoneRatio and CalcTransformRatio): removing a level and
    // adding the endpoint leaves both ratios unchanged. A play mode test runs both chains side by side and compares them.
    // A PhysBone qualifies when:
    //  - it has no Endpoint Position yet, and is not under an Arclight Exclude component;
    //  - every leaf of its chain (ignored branches left out) is an end bone: the only child of its parent, not the chain
    //    root, active, with no component but its Transform, unit scale and identity rotation, no children, nothing
    //    referencing, animating or skinning to it, and not a humanoid bone;
    //  - all those end bones sit at exactly the same non-zero local position, which becomes the endpoint.
    // Avatar Optimizer's version (ReplaceEndBoneWithEndpointPosition) was the model: it also skips overlapping chains, a set
    // endpoint and a root outside the avatar, and notes that with stretch or squish the end bone's own transform can move,
    // which matters only when something uses it. It accepts positions within 0.00001 and averages them; Arclight needs
    // them equal. It allows end bones at forks under rules per Multi-Child Type; Arclight needs each to be an only child.
    internal static class EndBoneReplacer
    {
        internal sealed class Result { public int PhysBones, Bones; }

        internal static Result Run(AvatarAnalysis analysis)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            // Bones a mesh is skinned to stay.
            var kept = new HashSet<Transform>(analysis.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b));
            // Humanoid bones stay: the Animator maps them, and VRChat reads some of them itself.
            kept.UnionWith(analysis.Root.GetComponentsInChildren<Animator>(true).Where(a => a.isHuman)
                .SelectMany(a => Enumerable.Range(0, (int)HumanBodyBones.LastBone).Select(b => a.GetBoneTransform((HumanBodyBones)b))).Where(t => t));
            var physBones = analysis.Root.GetComponentsInChildren<Component>(true).Where(c => c && c.GetType().Name == "VRCPhysBone").ToList();
            // Chains that share a transform (nested or overlapping PhysBones) are left alone: removing one's end bones would
            // change the other's chain.
            var owners = new Dictionary<Transform, int>();
            foreach (var t in physBones.SelectMany(p => UnusedObjectRemover.PhysBoneRoot(p).GetComponentsInChildren<Transform>(true)))
                owners[t] = owners.TryGetValue(t, out int n) ? n + 1 : 1;
            foreach (var physBone in physBones)
            {
                if (Exclusions.Excluded(physBone) || !UnusedObjectRemover.PhysBoneRoot(physBone).IsChildOf(analysis.Root.transform)) continue;
                if (UnusedObjectRemover.PhysBoneRoot(physBone).GetComponentsInChildren<Transform>(true).Any(t => owners[t] > 1)) continue;
                var root = UnusedObjectRemover.PhysBoneRoot(physBone);
                // An animation that writes the endpoint or the ignore list before the chain starts would see a different chain.
                if (analysis.IsAnimated(physBone, p => p.StartsWith("endpointPosition", StringComparison.Ordinal) ||
                    p.StartsWith("ignoreTransforms", StringComparison.Ordinal) || p.StartsWith("rootTransform", StringComparison.Ordinal))) continue;
                using (var serialized = new SerializedObject(physBone))
                {
                    var endpoint = serialized.FindProperty("endpointPosition");
                    var ignoreList = serialized.FindProperty("ignoreTransforms");
                    if (endpoint == null || ignoreList == null || !Same(endpoint.vector3Value, Vector3.zero)) continue;
                    var ignored = new HashSet<Transform>(Enumerable.Range(0, ignoreList.arraySize)
                        .Select(i => ignoreList.GetArrayElementAtIndex(i).objectReferenceValue as Transform).Where(t => t));
                    var leaves = new List<Transform>();
                    void Walk(Transform t)
                    {
                        var children = Enumerable.Range(0, t.childCount).Select(t.GetChild).Where(c => !ignored.Contains(c)).ToList();
                        if (children.Count == 0) leaves.Add(t);
                        foreach (var child in children) Walk(child);
                    }
                    Walk(root);
                    if (leaves.Count == 0 || !leaves.All(leaf => EndBone(leaf, root, kept, analysis))) continue;
                    var position = leaves[0].localPosition;
                    if (Same(position, Vector3.zero) || leaves.Any(l => !Same(l.localPosition, position))) continue;
                    endpoint.vector3Value = position;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    foreach (var leaf in leaves) Object.DestroyImmediate(leaf.gameObject);
                    result.PhysBones++;
                    result.Bones += leaves.Count;
                }
            }
            if (result.Bones > 0) analysis.RescanReferences();
            return result;
        }

        private static bool Same(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);

        private static bool EndBone(Transform leaf, Transform root, HashSet<Transform> kept, AvatarAnalysis analysis) =>
            leaf != root && leaf.parent && leaf.parent.childCount == 1 && leaf.childCount == 0 && leaf.gameObject.activeSelf &&
            leaf.GetComponents<Component>().Length == 1 && Same(leaf.localScale, Vector3.one) && leaf.localRotation.x == 0 && leaf.localRotation.y == 0 && leaf.localRotation.z == 0 && leaf.localRotation.w == 1 &&
            !Exclusions.Excluded(leaf) && !kept.Contains(leaf) &&
            !analysis.ReferencesTo(leaf).Any() && !analysis.ReferencesTo(leaf.gameObject).Any() &&
            !analysis.IsAnimated(leaf) && !analysis.IsAnimated(leaf.gameObject) &&
            !analysis.AnimatedObjectValues.Contains(leaf) && !analysis.AnimatedObjectValues.Contains(leaf.gameObject);
    }
}
