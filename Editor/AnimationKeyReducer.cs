using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Removes keyframes that change nothing about playback, in build-owned clips only:
    //  - a float key strictly inside a flat run: it, the kept key before it and the key after it hold the same value
    //    with zero tangents (no weighted tangents), so the merged segment is the same constant;
    //  - an object-reference key that repeats the previous kept key's object (keys hold until the next one).
    // The first and last keys always stay, so clip length, looping and normalized time are unchanged. Rotation
    // curves are skipped, because Unity rebuilds quaternions from their components at shared key times. After a
    // float curve is rewritten it is sampled densely against the original and restored on any difference.
    // Like MergeRequests: the NDMF pass records the avatar, the post-optimizer hook reduces its clips.
    internal static class KeyReductionRequests
    {
        private static readonly HashSet<int> Requested = new HashSet<int>();
        internal static void Add(GameObject avatar) => Requested.Add(avatar.GetInstanceID());
        internal static bool Take(GameObject avatar) => Requested.Remove(avatar.GetInstanceID());
    }

    internal static class AnimationKeyReducer
    {
        private const int SamplesPerSegment = 32;

        // Returns the number of keys removed.
        internal static int Reduce(IEnumerable<AnimationClip> clips)
        {
            int removed = 0;
            foreach (var clip in clips.Where(c => c && !c.legacy).Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (IsRotation(binding.propertyName) || binding.type == typeof(Animator) && IsAnimatorQuaternion(binding.propertyName)) continue;
                    var original = AnimationUtility.GetEditorCurve(clip, binding);
                    if (original == null || original.length < 3) continue;
                    var reduced = ReduceFloat(original.keys);
                    if (reduced.Length == original.length) continue;
                    var curve = new AnimationCurve(reduced) { preWrapMode = original.preWrapMode, postWrapMode = original.postWrapMode };
                    AnimationUtility.SetEditorCurve(clip, binding, curve);
                    if (SamePlayback(original, AnimationUtility.GetEditorCurve(clip, binding))) removed += original.length - reduced.Length;
                    else AnimationUtility.SetEditorCurve(clip, binding, original);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys == null || keys.Length < 3) continue;
                    var kept = new List<ObjectReferenceKeyframe> { keys[0] };
                    for (int i = 1; i < keys.Length - 1; i++)
                        if (keys[i].value != kept[kept.Count - 1].value) kept.Add(keys[i]);
                    kept.Add(keys[keys.Length - 1]);
                    if (kept.Count == keys.Length) continue;
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, kept.ToArray());
                    removed += keys.Length - kept.Count;
                }
            }
            return removed;
        }

        private static bool IsRotation(string property) => property != null &&
            (property.StartsWith("m_LocalRotation", StringComparison.Ordinal) || property.StartsWith("localEulerAngles", StringComparison.Ordinal) ||
             property.StartsWith("m_LocalEulerAngles", StringComparison.Ordinal));

        // Root, motion and IK goal rotations (RootQ.x, MotionQ.w, LeftHandQ.y, ...) are quaternions Unity rebuilds from all four
        // components at shared times, like Transform rotations.
        private static bool IsAnimatorQuaternion(string property) => property != null && property.Length > 3 &&
            property[property.Length - 3] == 'Q' && property[property.Length - 2] == '.' && "xyzw".IndexOf(property[property.Length - 1]) >= 0;

        internal static Keyframe[] ReduceFloat(Keyframe[] keys)
        {
            var kept = new List<Keyframe> { keys[0] };
            for (int i = 1; i < keys.Length - 1; i++)
            {
                var previous = kept[kept.Count - 1];
                var key = keys[i];
                var next = keys[i + 1];
                bool flat = key.value == previous.value && key.value == next.value &&
                    previous.outTangent == 0 && key.inTangent == 0 && key.outTangent == 0 && next.inTangent == 0 &&
                    previous.weightedMode == WeightedMode.None && key.weightedMode == WeightedMode.None && next.weightedMode == WeightedMode.None;
                if (!flat) kept.Add(key);
            }
            kept.Add(keys[keys.Length - 1]);
            return kept.ToArray();
        }

        // Removes curves that drive nothing on the finished avatar (Unity skips them): their path names no object, the object
        // has no component of their type, or they set a blend shape the renderer's mesh lacks and no animation anywhere on the
        // avatar (meshSwapped: any controller, other Animators, legacy clips) swaps that mesh. Animator parameter curves
        // always stay. Paths start from root, so clips any other Animator also plays are left alone. A clip's length is
        // kept: if the longest curve is dead, it stays. Returns the number of curves removed.
        internal static int RemoveDead(IEnumerable<AnimationClip> clips, Transform root, ISet<AnimationClip> elsewhere, ISet<Transform> meshSwapped)
        {
            var list = clips.Where(c => c && !c.legacy && !elsewhere.Contains(c)).Distinct().ToList();
            bool Dead(EditorCurveBinding b)
            {
                if (b.type == typeof(Animator)) return false;
                var targets = AvatarAnalysis.Resolve(root, b.path).ToList();
                if (targets.Count == 0) return true;
                if (b.type == typeof(GameObject) || b.type == typeof(Transform)) return false;
                if (b.type == null) return true; // A type that no longer exists binds nothing.
                var components = targets.Select(t => t.GetComponent(b.type)).Where(c => c).ToList();
                if (components.Count == 0) return true;
                string property = b.propertyName ?? "";
                if (property.StartsWith("blendShape.", StringComparison.Ordinal) && !targets.Any(meshSwapped.Contains))
                {
                    string shape = property.Substring("blendShape.".Length);
                    return components.All(c => c is SkinnedMeshRenderer s && s.sharedMesh && s.sharedMesh.GetBlendShapeIndex(shape) < 0);
                }
                return false;
            }
            int removed = 0;
            foreach (var clip in list)
            {
                var floats = AnimationUtility.GetCurveBindings(clip).Select(b => (Binding: b, End: AnimationUtility.GetEditorCurve(clip, b)?.keys.LastOrDefault().time ?? 0, Dead: Dead(b))).ToList();
                var objects = AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(b => (Binding: b, End: AnimationUtility.GetObjectReferenceCurve(clip, b)?.LastOrDefault().time ?? 0, Dead: Dead(b))).ToList();
                var all = floats.Select(f => (f.Binding, f.End, f.Dead, Float: true)).Concat(objects.Select(o => (o.Binding, o.End, o.Dead, Float: false))).ToList();
                var dead = all.Where(c => c.Dead).ToList();
                if (dead.Count == 0) continue;
                float end = all.Max(c => c.End);
                // Keep the clip's length: if no live curve reaches its end, the longest dead curve stays.
                if (!all.Any(c => !c.Dead && c.End >= end)) dead.Remove(dead.OrderByDescending(c => c.End).First());
                float length = clip.length;
                var saved = dead.Select(c => (c.Binding, c.Float, Curve: c.Float ? AnimationUtility.GetEditorCurve(clip, c.Binding) : null,
                    Keys: c.Float ? null : AnimationUtility.GetObjectReferenceCurve(clip, c.Binding))).ToList();
                foreach (var c in dead)
                    if (c.Float) AnimationUtility.SetEditorCurve(clip, c.Binding, null);
                    else AnimationUtility.SetObjectReferenceCurve(clip, c.Binding, null);
                if (clip.length != length)
                {
                    // Start times can differ too; put everything back rather than change timing.
                    foreach (var s in saved)
                        if (s.Float) AnimationUtility.SetEditorCurve(clip, s.Binding, s.Curve);
                        else AnimationUtility.SetObjectReferenceCurve(clip, s.Binding, s.Keys);
                    continue;
                }
                removed += dead.Count;
            }
            return removed;
        }

        // Exact equality at every key and at evenly spaced points inside every original segment.
        private static bool SamePlayback(AnimationCurve original, AnimationCurve reduced)
        {
            if (reduced == null) return false;
            var keys = original.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (original.Evaluate(keys[i].time) != reduced.Evaluate(keys[i].time)) return false;
                if (i == keys.Length - 1) break;
                float start = keys[i].time, length = keys[i + 1].time - start;
                for (int s = 1; s < SamplesPerSegment; s++)
                {
                    float t = start + length * s / SamplesPerSegment;
                    if (original.Evaluate(t) != reduced.Evaluate(t)) return false;
                }
            }
            return true;
        }
    }
}
