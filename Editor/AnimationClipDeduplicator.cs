using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Avatars whose NDMF pass asked for the later merge. NDMF removes the component during the pass, so the
    // request travels here (keyed by the build avatar object) to the SDK hook that runs after d4rk.
    internal static class MergeRequests
    {
        private static readonly HashSet<int> Requested = new HashSet<int>();
        internal static void Add(GameObject avatar) => Requested.Add(avatar.GetInstanceID());
        internal static bool Take(GameObject avatar) => Requested.Remove(avatar.GetInstanceID());
    }

    // Points every controller reference to a clip at one clip with identical data, so the bundle carries one
    // copy. Clip identity carries no behaviour in a state or blend tree: the Animator only reads clip data.
    // Runs on committed controllers after the other optimizers (d4rkAvatarOptimizer rewrites clips and can
    // make different clips identical). The caller must pass only controllers generated for this build, never
    // source assets. VRChat proxy, legacy, humanoid/root-motion and additive-reference-pose clips are left
    // alone, and a controller with a synced layer is skipped whole (its per-state motion overrides are not
    // rewritten). Clips also held outside the controllers keep their own reference: that only forgoes a saving.
    internal static class AnimationClipDeduplicator
    {
        private static readonly FieldInfo[] SettingsFields =
            typeof(AnimationClipSettings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();

        private static readonly HashSet<string> MuscleNames = new HashSet<string>(HumanTrait.MuscleName, StringComparer.Ordinal);

        // Returns the number of clips whose references now point to an identical clip.
        internal static int MergeCommitted(IEnumerable<AnimatorController> controllers, Func<Motion, bool> isSpecialMotion)
        {
            var usable = controllers.Where(c => c && c.layers.All(layer => layer.syncedLayerIndex < 0)).Distinct().ToArray();
            var states = new List<AnimatorState>();
            var trees = new List<BlendTree>();
            foreach (var controller in usable)
                foreach (var layer in controller.layers)
                    Collect(layer.stateMachine, states, trees, new HashSet<UnityEngine.Object>());
            var clips = states.Select(state => state.motion).Concat(trees.SelectMany(tree => tree.children.Select(c => c.motion)))
                .OfType<AnimationClip>().Distinct().ToArray();

            // Clips map to the first clip with an equal full signature. The hash only buckets.
            var keepers = new Dictionary<string, List<(AnimationClip Clip, byte[] Data)>>(StringComparer.Ordinal);
            var replace = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var clip in clips)
            {
                byte[] data = Signature(clip, isSpecialMotion);
                if (data == null) continue;
                string hash = FingerprintService.Hash(data);
                if (!keepers.TryGetValue(hash, out var bucket)) keepers.Add(hash, bucket = new List<(AnimationClip, byte[])>());
                var keeper = bucket.FirstOrDefault(k => k.Data.SequenceEqual(data)).Clip;
                if (keeper == null) bucket.Add((clip, data));
                else replace.Add(clip, keeper);
            }
            if (replace.Count == 0) return 0;

            Motion Map(Motion motion) => motion is AnimationClip clip && replace.TryGetValue(clip, out var keeper) ? keeper : motion;
            foreach (var state in states)
                if (Map(state.motion) != state.motion) state.motion = Map(state.motion);
            foreach (var tree in trees)
            {
                var children = tree.children;
                bool changed = false;
                for (int i = 0; i < children.Length; i++)
                {
                    var mapped = Map(children[i].motion);
                    if (mapped == children[i].motion) continue;
                    children[i].motion = mapped;
                    changed = true;
                }
                if (changed) tree.children = children;
            }
            return replace.Count;
        }

        // Every clip a controller can play, including per-state overrides of synced layers and nested blend trees.
        internal static IEnumerable<AnimationClip> AllClips(AnimatorController controller)
        {
            var states = new List<AnimatorState>();
            var trees = new List<BlendTree>();
            var seen = new HashSet<UnityEngine.Object>();
            var layers = controller.layers;
            foreach (var layer in layers) Collect(layer.stateMachine, states, trees, seen);
            var motions = states.Select(state => state.motion).Concat(trees.SelectMany(tree => tree.children.Select(c => c.motion))).ToList();
            foreach (var layer in layers.Where(l => l.syncedLayerIndex >= 0 && l.syncedLayerIndex < layers.Length))
            {
                var overrideStates = new List<AnimatorState>();
                Collect(layers[layer.syncedLayerIndex].stateMachine, overrideStates, new List<BlendTree>(), new HashSet<UnityEngine.Object>());
                foreach (var state in overrideStates)
                {
                    var overridden = layer.GetOverrideMotion(state);
                    var overrideTrees = new List<BlendTree>();
                    CollectTrees(overridden, overrideTrees, new HashSet<UnityEngine.Object>());
                    motions.Add(overridden);
                    motions.AddRange(overrideTrees.SelectMany(tree => tree.children.Select(c => c.motion)));
                }
            }
            return motions.OfType<AnimationClip>().Distinct();
        }

        internal static void Collect(AnimatorStateMachine machine, List<AnimatorState> states, List<BlendTree> trees,
            HashSet<UnityEngine.Object> seen)
        {
            if (!machine || !seen.Add(machine)) return;
            foreach (var child in machine.states)
            {
                if (!child.state || !seen.Add(child.state)) continue;
                states.Add(child.state);
                CollectTrees(child.state.motion, trees, seen);
            }
            foreach (var child in machine.stateMachines) Collect(child.stateMachine, states, trees, seen);
        }

        internal static void CollectTrees(Motion motion, List<BlendTree> trees, HashSet<UnityEngine.Object> seen)
        {
            if (!(motion is BlendTree tree) || !seen.Add(tree)) return;
            trees.Add(tree);
            foreach (var child in tree.children) CollectTrees(child.motion, trees, seen);
        }

        // Every value the clip carries except its name. Null when the clip is not eligible.
        internal static byte[] Signature(AnimationClip clip, Func<Motion, bool> isSpecialMotion)
        {
            if (!clip || clip.legacy || clip.isHumanMotion || clip.hasMotionCurves || clip.hasRootCurves ||
                isSpecialMotion(clip)) return null;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.additiveReferencePoseClip != null) return null;
            var floatBindings = AnimationUtility.GetCurveBindings(clip);
            // Animator-typed bindings are humanoid muscle and root-motion curves, but also animated parameters (AAP).
            if (floatBindings.Any(binding => binding.type == typeof(Animator) && IsHumanoidProperty(binding.propertyName))) return null;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(clip.frameRate); writer.Write((int)clip.wrapMode);
                writer.Write(new SerializedObject(clip).FindProperty("m_UseHighQualityCurve").boolValue);
                var bounds = clip.localBounds;
                writer.Write(bounds.center.x); writer.Write(bounds.center.y); writer.Write(bounds.center.z);
                writer.Write(bounds.extents.x); writer.Write(bounds.extents.y); writer.Write(bounds.extents.z);
                foreach (var field in SettingsFields)
                {
                    object value = field.GetValue(settings);
                    if (value is UnityEngine.Object) continue; // additiveReferencePoseClip, checked above.
                    writer.Write(field.Name);
                    writer.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                }

                var events = AnimationUtility.GetAnimationEvents(clip);
                writer.Write(events.Length);
                foreach (var e in events)
                {
                    writer.Write(e.time); writer.Write(e.functionName ?? ""); writer.Write(e.stringParameter ?? "");
                    writer.Write(e.floatParameter); writer.Write(e.intParameter); writer.Write((int)e.messageOptions);
                    writer.Write(e.objectReferenceParameter ? e.objectReferenceParameter.GetInstanceID() : 0);
                }

                foreach (var binding in Sorted(floatBindings))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null) continue;
                    Write(writer, binding);
                    writer.Write((int)curve.preWrapMode); writer.Write((int)curve.postWrapMode);
                    var keys = curve.keys;
                    writer.Write(keys.Length);
                    for (int i = 0; i < keys.Length; i++)
                    {
                        var key = keys[i];
                        writer.Write(key.time); writer.Write(key.value);
                        writer.Write(key.inTangent); writer.Write(key.outTangent);
                        writer.Write(key.inWeight); writer.Write(key.outWeight); writer.Write((int)key.weightedMode);
                        writer.Write((int)AnimationUtility.GetKeyLeftTangentMode(curve, i));
                        writer.Write((int)AnimationUtility.GetKeyRightTangentMode(curve, i));
                        writer.Write(AnimationUtility.GetKeyBroken(curve, i));
                    }
                }
                writer.Write("|object curves|");
                foreach (var binding in Sorted(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys == null) continue;
                    Write(writer, binding);
                    writer.Write(keys.Length);
                    foreach (var key in keys)
                    {
                        writer.Write(key.time);
                        // Same object, not merely an equal one. Instance IDs are stable for the build.
                        writer.Write(key.value ? key.value.GetInstanceID() : 0);
                    }
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        // Muscle curves, and root/motion/IK-goal transforms such as "RootT.x" or "LeftFootQ.w".
        private static bool IsHumanoidProperty(string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            if (MuscleNames.Contains(property)) return true;
            int dot = property.IndexOf('.');
            string head = dot < 0 ? property : property.Substring(0, dot);
            return head == "RootT" || head == "RootQ" || head == "MotionT" || head == "MotionQ" ||
                   ((head.StartsWith("Left", StringComparison.Ordinal) || head.StartsWith("Right", StringComparison.Ordinal)) &&
                    (head.EndsWith("T", StringComparison.Ordinal) || head.EndsWith("Q", StringComparison.Ordinal)) &&
                    (head.IndexOf("Foot", StringComparison.Ordinal) > 0 || head.IndexOf("Hand", StringComparison.Ordinal) > 0));
        }

        private static IEnumerable<EditorCurveBinding> Sorted(IEnumerable<EditorCurveBinding> bindings) =>
            bindings.OrderBy(b => b.path, StringComparer.Ordinal).ThenBy(b => b.type?.AssemblyQualifiedName, StringComparer.Ordinal)
                .ThenBy(b => b.propertyName, StringComparer.Ordinal);

        private static void Write(BinaryWriter writer, EditorCurveBinding binding)
        {
            writer.Write(binding.path ?? ""); writer.Write(binding.type?.AssemblyQualifiedName ?? "");
            writer.Write(binding.propertyName ?? ""); writer.Write(binding.isPPtrCurve); writer.Write(binding.isDiscreteCurve);
        }
    }
}
