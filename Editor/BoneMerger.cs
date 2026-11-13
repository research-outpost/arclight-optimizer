using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Merges skinned bones that never move relative to their parent into the parent, so the avatar has fewer bones and
    // transforms. A vertex weighted to the bone stays weighted to the same slot; the slot now names the parent, and its
    // bind pose becomes (the bone's fixed local matrix) x (the old bind pose), so the slot's skinning matrix is the same
    // product as before, multiplied in a different order. That rounds once more in float: the accepted difference is the
    // same as static mesh merging's, at most 1/255 per colour channel (agreed by the project owner).
    // A bone qualifies when:
    //  - it has no children (leaves go first; a parent whose children all went is tried again) and no component but its
    //    Transform (active or not: a bone drives skinning either way), is not a humanoid bone, and is not under an Arclight Exclude component;
    //  - nothing moves it locally or changes its scale (animation, constraints, physics, PhysBones, Head Chop) and nothing
    //    animates its activeness, and no animation curve takes it or its object as a value;
    //  - only skinned renderers refer to it, each through its bone list (not as root bone or probe anchor), and each such
    //    renderer has no Cloth, no animation swaps its mesh, and it is the only user of its mesh or every user of that mesh
    //    has the very same bone list (the mesh is then edited once and stays shared; renderers with different bone lists
    //    would each need their own copy of the mesh, which costs more than the bone saves).
    internal static class BoneMerger
    {
        internal sealed class Result { public int Bones; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var humanoid = new HashSet<Transform>(root.GetComponentsInChildren<Animator>(true).Where(a => a.isHuman)
                .SelectMany(a => Enumerable.Range(0, (int)HumanBodyBones.LastBone).Select(b => a.GetBoneTransform((HumanBodyBones)b))).Where(t => t));
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            while (true)
            {
                var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh).ToList();
                // Whether every user of the mesh holds the same bones in the same slots (true for a single user).
                var alike = renderers.GroupBy(r => r.sharedMesh).ToDictionary(g => g.Key, g => { var first = g.First().bones; return g.All(r => r.bones.SequenceEqual(first)); });
                bool RendererOk(SkinnedMeshRenderer r) => r && r.sharedMesh && alike[r.sharedMesh] && !swaps.Contains(r.sharedMesh) &&
                    !r.GetComponent<Cloth>() && !analysis.IsAnimated(r, p => p == "m_Mesh") && !Exclusions.Excluded(r) &&
                    r.sharedMesh.bindposes.Length == r.bones.Length && !analysis.ReferencesTo(r.sharedMesh).Any(c => !(c is SkinnedMeshRenderer));
                var merged = new List<Transform>();
                var copies = new HashSet<Mesh>(); // This round's build copies: a second bone merged this round edits the same copy.
                foreach (var bone in renderers.SelectMany(r => r.bones).Where(b => b).Distinct().ToList())
                {
                    if (!Mergeable(bone, root.transform, humanoid, analysis)) continue;
                    var users = analysis.ReferencesTo(bone).ToList();
                    if (users.Count == 0 || users.Any(c => !(c is SkinnedMeshRenderer r) || !RendererOk(r) || r.rootBone == bone || r.probeAnchor == bone)) continue;
                    if (analysis.ReferencesTo(bone.gameObject).Any(c => c.gameObject != bone.gameObject)) continue;
                    var local = Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
                    // Every user of a mesh holds the bone in the same slots (alike), so each mesh is edited once for all of them.
                    foreach (var sharing in users.Cast<SkinnedMeshRenderer>().GroupBy(r => r.sharedMesh).ToList())
                    {
                        var mesh = sharing.Key;
                        var copy = mesh;
                        if (!copies.Contains(mesh))
                        {
                            copy = Object.Instantiate(mesh);
                            copy.name = mesh.name;
                            register(mesh, copy);
                            alike[copy] = true;
                            copies.Add(copy);
                        }
                        var slots = sharing.First().bones;
                        var bindposes = copy.bindposes;
                        for (int i = 0; i < slots.Length; i++)
                            if (slots[i] == bone) bindposes[i] = local * bindposes[i];
                        copy.bindposes = bindposes;
                        foreach (var renderer in sharing.ToList())
                        {
                            var bones = renderer.bones;
                            for (int i = 0; i < bones.Length; i++)
                                if (bones[i] == bone) bones[i] = bone.parent;
                            SkinnedMeshMerger.Rebind(renderer, () =>
                            {
                                renderer.bones = bones;
                                renderer.sharedMesh = copy;
                            });
                        }
                    }
                    merged.Add(bone);
                }
                if (merged.Count == 0) return result;
                foreach (var bone in merged) Object.DestroyImmediate(bone.gameObject);
                result.Bones += merged.Count;
                analysis.RescanReferences();
            }
        }

        private static bool Mergeable(Transform bone, Transform root, HashSet<Transform> humanoid, AvatarAnalysis analysis) =>
            bone != root && bone.IsChildOf(root) && bone.parent && bone.childCount == 0 && !humanoid.Contains(bone) &&
            bone.GetComponents<Component>().Length == 1 && !Exclusions.Excluded(bone) &&
            !analysis.MovesLocally(bone) && !analysis.ScaleChanges(bone) && !analysis.IsAnimated(bone.gameObject) &&
            !analysis.AnimatedObjectValues.Contains(bone) && !analysis.AnimatedObjectValues.Contains(bone.gameObject);
    }
}
