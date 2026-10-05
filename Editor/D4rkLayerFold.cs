using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // d4rk Avatar Optimizer adds the toggles it merges as one more layer at the top of the FX controller: a single Write
    // Defaults state playing a Direct blend tree. It runs after Arclight's own layer fold, so that fold never sees it. When
    // a lower layer already plays such a tree, d4rk's children join that tree and its layer goes (one layer fewer).
    // Exact under the fold's rules:
    //  - both layers have weight 1, Override blending, no IK pass and no avatar mask (the lower one may have one when d4rk's
    //    tree animates no Transform or humanoid value, the only curves a mask filters), no synced layer, and no VRC Animator Layer
    //    Control names either of them (a control scales a layer's weight, which would now cover both trees);
    //  - each plays one Write Defaults state with no transitions or behaviours: a Direct tree that does not normalize,
    //    whose clips all hold one value (with moving clips, Unity plays a larger Direct tree at a different speed); constant
    //    material swaps are allowed, as the trees never bind the same property;
    //  - the two trees never animate the same property (Transform position, rotation and scale count as one value each),
    //    and no layer between them animates one of d4rk's properties, so every property keeps its highest writer;
    //  - d4rk's layer is the top layer, so removing it renumbers nothing; both trees are build copies (never source assets).
    // A Direct tree that does not normalize adds its children's weighted values, so moving d4rk's children (each with its
    // own weight parameter) into the lower tree adds the same values.
    internal static class D4rkLayerFold
    {
        internal const string LayerPrefix = "d4rkAvatarOptimizer";

        // Returns the name of the layer d4rk's tree joined, or null. isOwned: whether an asset is a build copy.
        internal static string Run(AnimatorController controller, IEnumerable<RuntimeAnimatorController> all, Func<Object, bool> isOwned)
        {
            var layers = controller.layers;
            int top = layers.Length - 1;
            if (top < 2 || !layers[top].name.StartsWith(LayerPrefix, StringComparison.Ordinal)) return null;
            if (layers.Any(l => l.syncedLayerIndex >= 0)) return null;
            var controlled = Controls(all);
            if (controlled == null || controlled.Contains(top)) return null;
            var source = Tree(layers[top], masked: false);
            if (source == null || !isOwned(controller)) return null;
            var properties = Bindings(source);
            // An avatar mask filters only Transform and humanoid curves, so a masked layer can take d4rk's tree when it has none.
            bool maskable = properties.All(p => p.type != typeof(Transform) && p.type != typeof(Animator));
            for (int i = top - 1; i >= 1; i--)
            {
                var target = Tree(layers[i], masked: maskable);
                if (target != null && !controlled.Contains(i) && isOwned(target) && !Bindings(target).Overlaps(properties))
                {
                    target.children = target.children.Concat(source.children).ToArray();
                    EditorUtility.SetDirty(target);
                    controller.layers = layers.Take(top).ToArray();
                    EditorUtility.SetDirty(controller);
                    return layers[i].name;
                }
                // A layer between them that animates one of d4rk's properties would end up above it.
                var between = layers[i].stateMachine ? Clips(layers[i].stateMachine).SelectMany(Bindings) : Enumerable.Empty<EditorCurveBinding>();
                if (between.Any(properties.Contains)) return null;
            }
            return null;
        }

        // Layer indices any VRC Animator Layer Control names (from any playable, conservatively); null if one cannot be read.
        private static HashSet<int> Controls(IEnumerable<RuntimeAnimatorController> all)
        {
            var named = new HashSet<int>();
            foreach (var controller in all.OfType<AnimatorController>())
                foreach (var layer in controller.layers)
                    foreach (var behaviour in Machines(layer.stateMachine).SelectMany(m => m.behaviours.Concat(m.states.SelectMany(s => s.state.behaviours))))
                    {
                        if (!behaviour || behaviour.GetType().Name != "VRCAnimatorLayerControl") continue;
                        using (var serialized = new SerializedObject(behaviour))
                        {
                            var index = serialized.FindProperty("layer");
                            if (index == null) return null;
                            named.Add(index.intValue);
                        }
                    }
            return named;
        }

        private static IEnumerable<AnimatorStateMachine> Machines(AnimatorStateMachine machine) =>
            machine ? new[] { machine }.Concat(machine.stateMachines.SelectMany(c => Machines(c.stateMachine))) : Enumerable.Empty<AnimatorStateMachine>();

        // The layer's Direct tree when the layer qualifies (see above), else null.
        // masked: whether the layer may have an avatar mask.
        private static BlendTree Tree(AnimatorControllerLayer layer, bool masked)
        {
            var machine = layer.stateMachine;
            if (!machine || layer.defaultWeight != 1 || layer.blendingMode != AnimatorLayerBlendingMode.Override || layer.avatarMask && !masked || layer.iKPass) return null;
            if (machine.states.Length != 1 || machine.stateMachines.Length != 0 || machine.behaviours.Length != 0 || machine.anyStateTransitions.Length != 0 ||
                machine.entryTransitions.Length != 0) return null;
            var state = machine.states[0].state;
            if (!state || machine.defaultState != state || !state.writeDefaultValues || state.behaviours.Length != 0 || state.transitions.Length != 0 ||
                state.timeParameterActive || state.mirror || state.mirrorParameterActive || state.speed != 1 || state.speedParameterActive) return null;
            if (!(state.motion is BlendTree tree) || tree.blendType != BlendTreeType.Direct || Normalizes(tree)) return null;
            var clips = Clips(tree).ToList();
            if (Trees(tree).Any(t => t.blendType == BlendTreeType.Direct && Normalizes(t)) || clips.Any(c => !Constant(c))) return null;
            return tree;
        }

        private static bool Normalizes(BlendTree tree)
        {
            using (var serialized = new SerializedObject(tree)) return serialized.FindProperty("m_NormalizedBlendValues")?.boolValue ?? true;
        }

        private static IEnumerable<BlendTree> Trees(BlendTree tree) =>
            new[] { tree }.Concat(tree.children.Select(c => c.motion).OfType<BlendTree>().SelectMany(Trees));

        private static IEnumerable<AnimationClip> Clips(Motion motion) =>
            motion is AnimationClip clip ? new[] { clip } : motion is BlendTree tree ? tree.children.SelectMany(c => Clips(c.motion)) : Enumerable.Empty<AnimationClip>();

        private static IEnumerable<AnimationClip> Clips(AnimatorStateMachine machine) =>
            Machines(machine).SelectMany(m => m.states).SelectMany(s => Clips(s.state.motion));

        // Every curve holds one value. Object curves (material swaps) are allowed: the trees never bind the same property, so
        // the child that sets an object value is the same before and after.
        private static bool Constant(AnimationClip clip)
        {
            if (!clip) return false;
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null || keys.Length == 0 || keys.Any(k => k.value != keys[0].value)) return false;
            }
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var keys = AnimationUtility.GetEditorCurve(clip, binding)?.keys;
                if (keys == null || keys.Length == 0) return false;
                if (keys.Length > 1 && keys.Any(k => !k.value.Equals(keys[0].value) || !AnimatorLayerMerger.Flat(k.inTangent) || !AnimatorLayerMerger.Flat(k.outTangent))) return false;
            }
            return true;
        }

        private static HashSet<EditorCurveBinding> Bindings(BlendTree tree) => new HashSet<EditorCurveBinding>(Clips(tree).SelectMany(Bindings));

        private static IEnumerable<EditorCurveBinding> Bindings(AnimationClip clip) => clip
            ? AnimationUtility.GetCurveBindings(clip).Select(AnimatorLayerMerger.Whole).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
            : Enumerable.Empty<EditorCurveBinding>();
    }
}
