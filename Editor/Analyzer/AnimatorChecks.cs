using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Step 2: Write Defaults, layers that never play, animation targets and parameter driver targets.
    internal static partial class AvatarAnalyzer
    {
        // Mixed Write Defaults in FX or Gesture: the usual cause of toggles that stick or reset. Judged as authored. Base and Action
        // are left out: VRChat's and locomotion templates mix them there by design.
        private static void WriteDefaults(Playable playable, List<Finding> findings)
        {
            if (playable.Name != "FX" && playable.Name != "Gesture") return;
            // States playing a Direct blend tree are left out: the recommended layout pairs Write Defaults on tree layers with off toggles.
            var states = playable.Controller.layers.SelectMany(l => States(l.stateMachine).Select(s => (Layer: l.name, State: s.Item2)))
                .Where(s => !(s.State.motion is BlendTree tree && tree.blendType == BlendTreeType.Direct)).ToList();
            var on = states.Where(s => s.State.writeDefaultValues).ToList();
            var off = states.Where(s => !s.State.writeDefaultValues).ToList();
            if (on.Count == 0 || off.Count == 0) return;
            var fewer = on.Count <= off.Count ? on : off;
            string fewerName = fewer == on ? "on" : "off", otherName = fewer == on ? "off" : "on";
            var layers = fewer.Select(s => s.Layer).Distinct().ToList();
            findings.Add(new Finding
            {
                Severity = Severity.WorthChecking,
                Key = "wd|" + playable.Name, Playable = playable.Name,
                Title = playable.Name + " mixes Write Defaults on and off, which can make toggles misbehave",
                Detail = "Most states (" + (states.Count - fewer.Count) + ") have Write Defaults " + otherName + "; " + AvatarAnalyzer.N(fewer.Count, "state", "has", "have") + " it " + fewerName +
                    ". Mixing the two is a common reason toggles stick or snap back.\nThe odd " + (fewer.Count == 1 ? "one is" : "ones are") + " in " + (layers.Count == 1 ? "layer " : "layers ") +
                    AvatarAnalyzer.Join(layers, 8) + ".",
                Fix = "Set every state in " + playable.Name + " to Write Defaults " + otherName + ", the setting most already use. Layers a tool installed with its own setting (face tracking, for example) can stay as they are.",
                Target = playable.Controller
            });
        }

        // Layers at weight 0 that no layer control ever raises, and layers with no states: they never do anything.
        private static void DeadLayers(Avatar avatar, Playable playable, List<Finding> findings)
        {
            var raised = new HashSet<int>();
            int controls = 0; // Layer Controls aimed at this playable: removing a layer would shift the indices they name.
            foreach (var behaviour in avatar.Playables.SelectMany(p => Behaviours(p.Controller)).Where(b => b && b.GetType().Name == "VRCAnimatorLayerControl"))
                using (var serialized = new SerializedObject(behaviour))
                {
                    var target = serialized.FindProperty("playable");
                    string named = target != null && target.propertyType == SerializedPropertyType.Enum ? target.enumNames[target.enumValueIndex] : null;
                    if (named == null || named == playable.Name) controls++;
                    if ((named == null || named == playable.Name) && (serialized.FindProperty("goalWeight")?.floatValue ?? 1) > 0)
                        raised.Add(serialized.FindProperty("layer")?.intValue ?? -1);
                }
            var layers = playable.Controller.layers;
            var dead = new List<string>();
            var deadNames = new List<string>();
            for (int i = 1; i < layers.Length; i++) // The first layer always plays at full weight.
            {
                var machine = layers[i].syncedLayerIndex >= 0 && layers[i].syncedLayerIndex < layers.Length ? layers[layers[i].syncedLayerIndex].stateMachine : layers[i].stateMachine;
                if (Divider(layers[i].name) && !States(machine).Any()) continue;
                if (!States(machine).Any()) { dead.Add("\"" + layers[i].name + "\" (no states)"); deadNames.Add(layers[i].name); }
                // A weight-0 layer still runs its state machine, so one holding behaviours (drivers, tracking or layer controls) acts.
                else if (layers[i].defaultWeight == 0 && !raised.Contains(i) && !Acts(layers[i], machine)) { dead.Add("\"" + layers[i].name + "\" (weight 0)"); deadNames.Add(layers[i].name); }
            }
            if (dead.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.TidyUp,
                Key = "layer|" + playable.Name, Playable = playable.Name,
                Title = AvatarAnalyzer.N(dead.Count, "layer") + " in " + playable.Name + (dead.Count == 1 ? " never does" : " never do") + " anything",
                Detail = (dead.Count == 1 ? "It's empty, or its weight is 0 and nothing ever turns it up, so it has no effect in game:\n"
                    : "They're empty, or their weight is 0 and nothing ever turns them up, so they have no effect in game:\n") +
                    string.Join("\n", dead.Take(8).Select(d => "• " + d)) + (dead.Count > 8 ? "\n• and " + (dead.Count - 8) + " more" : ""),
                Fix = (dead.Count == 1 ? "It's safe to remove (press Remove). If it should play, set its weight to 1 in the Layers tab instead." : "They're safe to remove (press Remove). If one of them should play, set its weight to 1 in the Layers tab instead."),
                Target = playable.Controller,
                // Removing is safe only when no layer index can shift under a Layer Control or a synced layer, and names are unique.
                Data = controls == 0 && layers.All(l => l.syncedLayerIndex < 0) && layers.Select(l => l.name).Distinct().Count() == layers.Length
                    ? new Fixes.Layers { Names = deadNames } : null
            });
        }

        private static bool Acts(UnityEditor.Animations.AnimatorControllerLayer layer, UnityEditor.Animations.AnimatorStateMachine machine) =>
            Machines(machine).Any(sm => sm.behaviours.Length > 0 || sm.states.Any(s => s.state && (s.state.behaviours.Length > 0 ||
                layer.syncedLayerIndex >= 0 && layer.GetOverrideBehaviours(s.state).Length > 0)));

        // A layer whose avatar mask turns off a transform its own clips move: masks filter Transform curves, so that movement never
        // shows (a hand mask copied onto an FX layer is the usual cause). Only transforms the mask lists as off are counted.
        private static void MaskedMoves(Playable playable, List<Finding> findings)
        {
            var blocked = new List<string>();
            foreach (var layer in playable.Controller.layers.Where(l => l.avatarMask && l.avatarMask.transformCount > 0))
            {
                var mask = layer.avatarMask;
                var off = new HashSet<string>(Enumerable.Range(0, mask.transformCount).Where(i => !mask.GetTransformActive(i)).Select(mask.GetTransformPath), StringComparer.Ordinal);
                if (off.Count == 0) continue;
                var seen = new HashSet<Motion>();
                var clips = new List<AnimationClip>();
                void Walk(Motion motion)
                {
                    if (!motion || !seen.Add(motion)) return;
                    if (motion is AnimationClip clip) clips.Add(clip);
                    else if (motion is BlendTree tree) foreach (var child in tree.children) Walk(child.motion);
                }
                var source = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < playable.Controller.layers.Length ? playable.Controller.layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;
                foreach (var (_, state) in States(source)) Walk(layer.syncedLayerIndex >= 0 ? layer.GetOverrideMotion(state) : state.motion);
                var paths = clips.SelectMany(AnimationUtility.GetCurveBindings).Where(b => b.type == typeof(Transform) && off.Contains(b.path)).Select(b => b.path).Distinct().ToList();
                if (paths.Count > 0) blocked.Add("\"" + layer.name + "\" (mask \"" + mask.name + "\"): " + string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? " and " + (paths.Count - 5) + " more" : ""));
            }
            if (blocked.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.Broken, Key = "mask|" + playable.Name, Playable = playable.Name, Target = playable.Controller,
                Title = AvatarAnalyzer.N(blocked.Count, "layer") + " in " + playable.Name + (blocked.Count == 1 ? " has" : " have") + " a mask that blocks " + (blocked.Count == 1 ? "its" : "their") + " own animations",
                Detail = "The layer's avatar mask switches off bones that its own animations move, so that movement never shows:\n" + string.Join("\n", blocked.Select(b => "• " + b)),
                Fix = "In the Layers tab, set the layer's mask to None, or to a mask that includes those bones. A mask copied from the hands layer is the usual cause."
            });
        }

        // An empty layer named like "----- Hair -----" labels a section on purpose.
        private static bool Divider(string name) =>
            name.Length >= 3 && "-=*_#/~".IndexOf(name[0]) >= 0 && name[1] == name[0] && name[2] == name[0];

        private static IEnumerable<StateMachineBehaviour> Behaviours(AnimatorController controller) =>
            controller.layers.SelectMany(l => Machines(l.stateMachine)).SelectMany(sm => sm.behaviours.Concat(sm.states.Where(s => s.state).SelectMany(s => s.state.behaviours)))
                .Concat(controller.layers.Where(l => l.syncedLayerIndex >= 0 && l.syncedLayerIndex < controller.layers.Length)
                    .SelectMany(l => States(controller.layers[l.syncedLayerIndex].stateMachine).SelectMany(s => l.GetOverrideBehaviours(s.Item2))));

        // One finding per object or component a clip animates that the avatar doesn't have; grouped per controller by Group.
        private static void AnimationTargets(Avatar avatar, Playable playable, List<Finding> findings)
        {
            var missing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); // Target to clip names.
            var curves = new Dictionary<string, Fixes.Curves>(StringComparer.Ordinal); // Target to the curves themselves, for the fix.
            foreach (var clip in Clips(playable.Controller))
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    if (binding.type == typeof(Animator) && binding.path.Length == 0) continue; // Parameters and muscles.
                    var animated = AnimationUtility.GetAnimatedObject(avatar.Root, binding);
                    string target;
                    if (animated)
                    {
                        // A blendshape the mesh doesn't have (renamed or missing after a mesh update) is a common reason face tracking does nothing.
                        if (!(animated is SkinnedMeshRenderer renderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) || !renderer.sharedMesh ||
                            renderer.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0) continue;
                        target = (binding.path.Length == 0 ? "(avatar root)" : Short(binding.path)) + " (no blendshape \"" + binding.propertyName.Substring("blendShape.".Length) + "\")";
                    }
                    else
                    {
                        var transform = binding.path.Length == 0 ? avatar.Root.transform : avatar.Root.transform.Find(binding.path);
                        target = (binding.path.Length == 0 ? "(avatar root)" : Short(binding.path)) + (transform ? " (no " + binding.type.Name + ")" : "");
                    }
                    if (!missing.TryGetValue(target, out var clips)) missing[target] = clips = new HashSet<string>(StringComparer.Ordinal);
                    clips.Add(clip.name);
                    if (!curves.TryGetValue(target, out var list)) curves[target] = list = new Fixes.Curves();
                    list.Bindings.Add((clip, binding, binding.isPPtrCurve));
                }
            foreach (var pair in missing)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "path|" + playable.Name + "|" + pair.Key, Playable = playable.Name,
                    Title = pair.Key, Detail = string.Join("\n", pair.Value), Target = playable.Controller, Data = curves[pair.Key]
                });
        }

        // The controller's clips, walked by hand: AnimatorController.animationClips logs an error for every missing parameter.
        private static IEnumerable<AnimationClip> Clips(AnimatorController controller)
        {
            var seen = new HashSet<Motion>();
            var clips = new List<AnimationClip>();
            void Walk(Motion motion)
            {
                if (!motion || !seen.Add(motion)) return;
                if (motion is AnimationClip clip) clips.Add(clip);
                else if (motion is BlendTree tree) foreach (var child in tree.children) Walk(child.motion);
            }
            foreach (var layer in controller.layers)
            {
                bool synced = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < controller.layers.Length;
                foreach (var (_, state) in States(synced ? controller.layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine))
                    Walk(synced ? layer.GetOverrideMotion(state) : state.motion);
            }
            return clips;
        }

        // Parameter driver entries that set or copy a parameter that no playable layer and no Expression Parameter has.
        private static void DriverTargets(Avatar avatar, List<Finding> findings)
        {
            var known = new HashSet<string>(avatar.Playables.SelectMany(p => p.Controller.parameters.Select(q => q.name)).Concat(avatar.Expressions.Select(e => e.Name)), StringComparer.Ordinal);
            foreach (var playable in avatar.Playables)
                foreach (var driver in Behaviours(playable.Controller).Where(b => b && b.GetType().Name == "VRCAvatarParameterDriver"))
                    using (var serialized = new SerializedObject(driver))
                    {
                        var entries = serialized.FindProperty("parameters");
                        for (int i = 0; entries != null && i < entries.arraySize; i++)
                        {
                            var entry = entries.GetArrayElementAtIndex(i);
                            var type = entry.FindPropertyRelative("type");
                            bool copy = type != null && type.propertyType == SerializedPropertyType.Enum && type.enumNames[type.enumValueIndex] == "Copy";
                            foreach (var (name, role) in new[] { (entry.FindPropertyRelative("name")?.stringValue, "sets"), (copy ? entry.FindPropertyRelative("source")?.stringValue : null, "copies from") })
                                if (!string.IsNullOrEmpty(name) && !known.Contains(name))
                                    findings.Add(new Finding
                                    {
                                        Severity = Severity.WorthChecking, Key = "driver|" + name, Playable = playable.Name,
                                        Title = name, Detail = role, Target = playable.Controller
                                    });
                        }
                    }
        }

        // Turns the per-target path and driver findings into one finding per controller (paths) and one for drivers.
        internal static List<Finding> Group(List<Finding> findings)
        {
            var result = findings.Where(f => !f.Key.StartsWith("path|", StringComparison.Ordinal) && !f.Key.StartsWith("driver|", StringComparison.Ordinal)).ToList();
            foreach (var group in findings.Where(f => f.Key.StartsWith("path|", StringComparison.Ordinal)).GroupBy(f => f.Playable))
            {
                var targets = group.GroupBy(f => f.Title).Select(g => g.First()).OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
                var clips = targets.SelectMany(f => f.Detail.Split('\n')).Distinct().ToList(); // Clip names may hold commas.
                result.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "path|" + group.Key, Playable = group.Key, Target = group.First().Target,
                    Data = new Fixes.Curves { Bindings = group.Select(f => f.Data).OfType<Fixes.Curves>().SelectMany(c => c.Bindings).Distinct().ToList() },
                    Title = group.Key + " animates " + AvatarAnalyzer.N(targets.Count, "thing") + " this avatar doesn't have",
                    Detail = "Those parts of the animations do nothing here. Usually an object was renamed or removed, or the animation was made for a different avatar:\n" +
                        string.Join("\n", targets.Take(6).Select(f => "• " + f.Title)) + (targets.Count > 6 ? "\n• and " + (targets.Count - 6) + " more" : "") +
                        "\nUsed in " + (clips.Count == 1 ? "the clip " : "the clips ") + AvatarAnalyzer.Join(clips, 4) + ".",
                    Fix = "If an object was renamed or moved, rename it back or fix the path in the clip (the Animation window shows missing properties in yellow). If they aren't needed, press Remove to delete those parts from the clips (each clip is backed up first), or ignore this."
                });
            }
            var drivers = findings.Where(f => f.Key.StartsWith("driver|", StringComparison.Ordinal)).GroupBy(f => f.Title).Select(g => g.First()).ToList();
            if (drivers.Count > 0)
                result.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "driver", Playable = drivers[0].Playable, Target = drivers[0].Target,
                    Title = AvatarAnalyzer.N(drivers.Count, "parameter driver", "changes", "change") + " a parameter that doesn't exist",
                    Detail = (drivers.Count == 1 ? "No animator or Expression Parameter has this name, so setting it does nothing, and copying from it always gives 0:\n"
                        : "No animator or Expression Parameter has these names, so setting them does nothing, and copying from them always gives 0:\n") +
                        string.Join("\n", drivers.Take(8).Select(f => "• \"" + f.Title + "\" (" + f.Detail + ")")) + (drivers.Count > 8 ? "\n• and " + (drivers.Count - 8) + " more" : ""),
                    Fix = "Add the parameter to the animator that should react to it, or fix the name in the driver."
                });
            return result;
        }
    }
}
