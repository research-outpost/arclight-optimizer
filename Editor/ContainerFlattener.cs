using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Empty containers with an identity transform: an object whose only component is its Transform, at local position 0,
    // rotation identity and scale 1 (compared exactly), always active, is a multiplication by the identity, so its children can sit on its
    // parent with the same local values and the same world matrices, and it goes (one transform fewer to update).
    // Only when nothing can tell the difference:
    //  - nothing animates or references it, and it is not a humanoid bone of any Animator, an ancestor of one, or inside a PhysBone chain
    //    (a chain simulates every transform below its root);
    //  - it is not above an object a VRC Animator Play Audio behaviour names by path;
    //  - nothing at or below it is under Arclight Exclude (its children would move);
    //  - after the move, every animation path still names exactly what it named before: paths through it are rewritten,
    //    and no other path starts to match (a dead path that would now find an object) or becomes ambiguous; a moved
    //    child never shares a name with its new siblings;
    //  - the avatar has no legacy Animation component (its clips are not rewritten here) and no generic Avatar asset on any Animator.
    // Sibling order is kept: the children take the container's place.
    internal static class ContainerFlattener
    {
        internal sealed class Result { public int Containers; }

        internal static Result Run(AvatarAnalysis analysis, AnimationRewriter rewriter)
        {
            var result = new Result();
            if (!analysis.Complete || analysis.PlayAudioTargets == null) return result;
            var root = analysis.Root;
            if (root.GetComponentsInChildren<Animation>(true).Any()) return result;
            // Every Animator's rig: a generic Avatar binds by path, a humanoid one by its bones.
            var animators = root.GetComponentsInChildren<Animator>(true);
            if (animators.Any(a => a.avatar && !a.avatar.isHuman)) return result;
            var humanoid = new List<Transform>();
            foreach (var animator in animators.Where(a => a.isHuman))
                for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                    if (animator.GetBoneTransform(bone) is Transform t && t) humanoid.Add(t);
            var chainRoots = root.GetComponentsInChildren<Component>(true).Where(c => c && c.GetType().Name == "VRCPhysBone").Select(UnusedObjectRemover.PhysBoneRoot).ToList();
            var audio = analysis.PlayAudioTargets;

            bool Qualifies(Transform t) => t != root.transform && t.childCount > 0 && t.GetComponents<Component>().Length == 1 &&
                t.gameObject.activeSelf && Exact(t.localPosition, Vector3.zero) && Exact(t.localScale, Vector3.one) &&
                t.localRotation.x == 0 && t.localRotation.y == 0 && t.localRotation.z == 0 && t.localRotation.w == 1 &&
                !t.GetComponentsInChildren<Transform>(true).Any(c => Exclusions.Excluded(c.gameObject)) && !analysis.BindingsOn(t.gameObject).Any() && !analysis.BindingsOn(t).Any() &&
                analysis.ReferencesTo(t).Count == 0 && analysis.ReferencesTo(t.gameObject).Count == 0 &&
                !humanoid.Any(h => h.IsChildOf(t)) && !chainRoots.Any(r => t.IsChildOf(r)) && !audio.Any(a => a.IsChildOf(t));

            // Decided deepest first on the hierarchy as it will be (flattened containers replaced by their children).
            var flattened = new HashSet<Transform>();
            IEnumerable<Transform> Children(Transform t) => t.Cast<Transform>().SelectMany(c => flattened.Contains(c) ? Children(c) : new[] { c });
            foreach (var container in root.GetComponentsInChildren<Transform>(true).Where(Qualifies).OrderByDescending(Depth))
            {
                var moved = Children(container).Select(c => c.name).ToList();
                var siblings = Children(container.parent).Where(c => c != container).Select(c => c.name).ToList();
                if (moved.Distinct().Count() != moved.Count || moved.Any(siblings.Contains)) continue;
                flattened.Add(container);
            }
            if (flattened.Count == 0) return result;

            // Every curve must name the same objects afterwards: as before, or through its rewritten path.
            IEnumerable<Transform> After(Transform owner, string path)
            {
                IEnumerable<Transform> current = new[] { owner };
                if (string.IsNullOrEmpty(path)) return current;
                foreach (string part in path.Split('/')) current = current.SelectMany(t => Children(t).Where(c => c.name == part)).ToArray();
                return current;
            }
            string NewPath(Transform owner, Transform target)
            {
                var names = new List<string>();
                for (var t = target; t && t != owner; t = t.parent) if (!flattened.Contains(t)) names.Add(t.name);
                names.Reverse();
                return string.Join("/", names);
            }
            var rewrite = new Dictionary<(Transform, string), string>();
            foreach (var (owner, curvePath) in analysis.CurvePaths)
            {
                var before = AvatarAnalysis.Resolve(owner, curvePath).ToList();
                if (flattened.Contains(owner) || before.Any(flattened.Contains)) return result;
                if (before.Count == 1 && flattened.Any(f => before[0].IsChildOf(f) && f.IsChildOf(owner) && f != owner))
                {
                    string path = NewPath(owner, before[0]);
                    if (!After(owner, path).SequenceEqual(before)) return result;
                    rewrite[(owner, curvePath)] = path;
                }
                else if (!After(owner, curvePath).OrderBy(t => t.GetInstanceID()).SequenceEqual(before.OrderBy(t => t.GetInstanceID()))) return result;
            }

            // Avatar Masks name transforms by path too: the same rules, except that an entry for a container itself is dropped
            // (nothing animates a container).
            var maskRewrite = new Dictionary<(Transform, string), string>();
            foreach (var (owner, maskPath) in rewriter.MaskPaths())
            {
                if (string.IsNullOrEmpty(maskPath)) continue;
                var before = AvatarAnalysis.Resolve(owner, maskPath).ToList();
                if (flattened.Contains(owner)) return result;
                if (before.Count == 1 && flattened.Contains(before[0])) maskRewrite[(owner, maskPath)] = null;
                else if (before.Any(flattened.Contains)) return result;
                else if (before.Count == 1 && flattened.Any(f => before[0].IsChildOf(f) && f.IsChildOf(owner) && f != owner))
                {
                    string path = NewPath(owner, before[0]);
                    if (!After(owner, path).SequenceEqual(before)) return result;
                    maskRewrite[(owner, maskPath)] = path;
                }
                else if (!After(owner, maskPath).OrderBy(t => t.GetInstanceID()).SequenceEqual(before.OrderBy(t => t.GetInstanceID()))) return result;
            }
            if (maskRewrite.Count > 0)
                rewriter.RewriteMasks((owner, path) => maskRewrite.TryGetValue((owner, path), out string next) ? next : path);

            if (rewrite.Count > 0)
                rewriter.Rewrite((owner, binding) => rewrite.TryGetValue((owner, binding.path), out string path)
                    ? new EditorCurveBinding { path = path, type = binding.type, propertyName = binding.propertyName } : (EditorCurveBinding?)null);
            foreach (var container in flattened.OrderByDescending(Depth))
            {
                var parent = container.parent;
                int index = container.GetSiblingIndex();
                foreach (var child in container.Cast<Transform>().ToList())
                {
                    child.SetParent(parent, false);
                    child.SetSiblingIndex(index++);
                }
                UnityEngine.Object.DestroyImmediate(container.gameObject);
                result.Containers++;
            }
            analysis.RescanReferences();
            return result;
        }

        private static int Depth(Transform t) { int d = 0; for (var p = t.parent; p; p = p.parent) d++; return d; }
        // Unity's == on vectors and quaternions allows a small error; a container is flattened only when it is exactly identity.
        private static bool Exact(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    }
}
