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
                    if (IsRotation(binding.propertyName)) continue;
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
