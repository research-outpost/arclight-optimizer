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
                Detail = (fewer.Count * 2 == states.Count ? "Half the states (" + (states.Count - fewer.Count) + ") have" : "Most states (" + (states.Count - fewer.Count) + ") have") + " Write Defaults " + otherName + "; " + AvatarAnalyzer.N(fewer.Count, "state", "has", "have") + " it " + fewerName +
                    ". Mixing the two is a common reason toggles stick or snap back.\nThe odd " + (fewer.Count == 1 ? "one is" : "ones are") + " in " + (layers.Count == 1 ? "layer " : "layers ") +
                    AvatarAnalyzer.Join(layers, 8) + ".",
                Fix = (fewer.Count * 2 == states.Count ? "Pick one setting and use it for these states" : "Set the odd states to Write Defaults " + otherName + ", the setting most already use") +
                    ", unless your template or an installed system needs them as they are (face tracking layers often do). States playing a Direct blend tree were left out of this count.",
                Identity = fewerName + "\n" + string.Join("\n", fewer.Select(s => s.Layer + "/" + s.State.name)),
                Target = playable.Controller
            });
        }

        // States that can't do what they look like: a Write Defaults off state with no animation keeps whatever the previous state
        // set (the classic stuck toggle), and a transition out with no conditions and no exit time, which Unity ignores, so that way
        // out never happens. Judged as authored.
        private static void DeadStates(Playable playable, List<Finding> findings)
        {
            var empty = new List<string>();
            var ignored = new List<string>();
            foreach (var layer in playable.Controller.layers.Where(l => l.syncedLayerIndex < 0))
            {
                foreach (var (_, state) in States(layer.stateMachine))
                {
                    // FX only: hand-pose layers in Gesture often leave idle states empty on purpose.
                    if (playable.Name == "FX" && !state.writeDefaultValues && !state.motion)
                        empty.Add("\"" + state.name + "\" in layer \"" + layer.name + "\"");
                    // Unity ignores such a transition ("needs at least one condition or an Exit Time to be valid").
                    if (state.transitions.Any(Ignored))
                        ignored.Add("\"" + state.name + "\" in layer \"" + layer.name + "\"");
                }
                foreach (var machine in Machines(layer.stateMachine))
                    foreach (var t in machine.anyStateTransitions.Where(Ignored))
                        ignored.Add("Any State → \"" + (t.destinationState ? t.destinationState.name : t.destinationStateMachine ? t.destinationStateMachine.name : "?") + "\" in layer \"" + layer.name + "\"");
            }
            if (empty.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "emptywd|" + playable.Name, Playable = playable.Name, Target = playable.Controller,
                    Title = AvatarAnalyzer.N(empty.Count, "state") + " in " + playable.Name + (empty.Count == 1 ? " has" : " have") + " Write Defaults off and no animation",
                    Detail = "With Write Defaults off, a state with no animation changes nothing, so whatever the previous state set stays. That's a common reason a toggle won't turn off:\n" +
                        string.Join("\n", empty.Take(8).Select(s => "• " + s)) + (empty.Count > 8 ? "\n• and " + (empty.Count - 8) + " more" : ""),
                    Fix = "Give each one an animation that sets the values it should have: for an \"off\" state, a clip that turns the things off.",
                    Identity = string.Join("\n", empty)
                });
            if (ignored.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "instant|" + playable.Name, Playable = playable.Name, Target = playable.Controller,
                    Title = AvatarAnalyzer.N(ignored.Count, "state") + " in " + playable.Name + (ignored.Count == 1 ? " has a transition" : " have transitions") + " that never happen",
                    Detail = "A transition out has no conditions and no exit time. Unity ignores a transition like that, so the state never leaves that way and can get stuck:\n" +
                        string.Join("\n", ignored.Take(8).Select(s => "• " + s)) + (ignored.Count > 8 ? "\n• and " + (ignored.Count - 8) + " more" : ""),
                    Fix = "Give that transition a condition, or tick Has Exit Time so it leaves when the animation ends. The Inspector shows a warning on it.",
                    Identity = string.Join("\n", ignored)
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
            // Removing is safe only when no layer index can shift under a Layer Control or a synced layer, and names are unique.
            bool removable = controls == 0 && layers.All(l => l.syncedLayerIndex < 0) && layers.Select(l => l.name).Distinct().Count() == layers.Length;
            findings.Add(new Finding
            {
                Severity = Severity.TidyUp,
                Key = "layer|" + playable.Name, Playable = playable.Name,
                Title = AvatarAnalyzer.N(dead.Count, "layer") + " in " + playable.Name + (dead.Count == 1 ? " never does" : " never do") + " anything",
                Detail = (dead.Count == 1 ? "It's empty, or its weight is 0 and nothing ever turns it up, so it has no effect in game:\n"
                    : "They're empty, or their weight is 0 and nothing ever turns them up, so they have no effect in game:\n") +
                    string.Join("\n", dead.Take(8).Select(d => "• " + d)) + (dead.Count > 8 ? "\n• and " + (dead.Count - 8) + " more" : ""),
                Fix = (dead.Count == 1 ? "It's safe to remove. If it should play, set its weight to 1 in the Layers tab instead." : "They're safe to remove. If one of them should play, set its weight to 1 in the Layers tab instead.") +
                    (removable ? "" : " Remove " + (dead.Count == 1 ? "it" : "them") + " by hand and check your Animator Layer Controls afterwards: they point at layers by number, and removing one shifts the rest."),
                Target = playable.Controller,
                Data = removable ? new Fixes.Layers { Names = deadNames } : null,
                Identity = string.Join("\n", dead)
            });
        }

        private static bool Acts(UnityEditor.Animations.AnimatorControllerLayer layer, UnityEditor.Animations.AnimatorStateMachine machine) =>
            Machines(machine).Any(sm => sm.behaviours.Length > 0 || sm.states.Any(s => s.state && (s.state.behaviours.Length > 0 ||
                layer.syncedLayerIndex >= 0 && layer.GetOverrideBehaviours(s.state).Length > 0)));

        // Animator Layer Controls aimed at a layer number the target controller doesn't have: they change nothing. Only controllers the
        // avatar sets are judged (VRChat's defaults are not read).
        private static void LayerControls(Avatar avatar, List<Finding> findings)
        {
            var wrong = new List<string>();
            Playable holder = null; // Where the first one is, for Show.
            foreach (var playable in avatar.Playables)
            {
                var where = Where(playable);
                foreach (var behaviour in Behaviours(playable.Controller).Where(b => b && b.GetType().Name == "VRCAnimatorLayerControl").Distinct())
                    using (var serialized = new SerializedObject(behaviour))
                    {
                        var target = serialized.FindProperty("playable");
                        if (target == null || target.propertyType != SerializedPropertyType.Enum || target.enumValueIndex < 0) continue;
                        string named = target.enumNames[target.enumValueIndex];
                        var aimed = avatar.Playables.FirstOrDefault(p => p.Name == named);
                        int layer = serialized.FindProperty("layer")?.intValue ?? 0;
                        if (aimed == null || layer >= 0 && layer < aimed.Controller.layers.Length) continue;
                        string line = "Layer " + layer + " of " + named + " (" + named + " has " + AvatarAnalyzer.N(aimed.Controller.layers.Length, "layer") +
                            ", numbered from 0), set in " + (where.TryGetValue(behaviour, out string place) ? place : playable.Name);
                        if (!wrong.Contains(line)) wrong.Add(line);
                        if (holder == null) holder = playable;
                    }
            }
            if (wrong.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.Broken, Key = "control|", Playable = holder.Name, Target = holder.Controller,
                Title = AvatarAnalyzer.N(wrong.Count, "Animator Layer Control") + " point" + (wrong.Count == 1 ? "s" : "") + " at a layer that doesn't exist",
                Detail = (wrong.Count == 1 ? "It changes nothing, so whatever it should turn on or off stays as it is:\n" : "They change nothing, so whatever they should turn on or off stays as it is:\n") +
                    string.Join("\n", wrong.Take(8).Select(w => "• " + w)) + (wrong.Count > 8 ? "\n• and " + (wrong.Count - 8) + " more" : ""),
                Fix = "Select the state with the Animator Layer Control and set Layer to the right number. Layers count from 0 at the top of the Layers tab; removing or adding a layer shifts the rest.",
                Identity = string.Join("\n", wrong)
            });
        }

        // Where each behaviour sits, as "FX → layer → state" (or the layer's state machine), so a card can say which one to open.
        private static Dictionary<StateMachineBehaviour, string> Where(Playable playable)
        {
            var where = new Dictionary<StateMachineBehaviour, string>();
            foreach (var layer in playable.Controller.layers)
                foreach (var machine in Machines(layer.stateMachine))
                {
                    foreach (var b in machine.behaviours.Where(b => b)) where[b] = playable.Name + " → " + layer.name + " → " + machine.name;
                    foreach (var s in machine.states.Where(s => s.state))
                        foreach (var b in s.state.behaviours.Where(b => b)) where[b] = playable.Name + " → " + layer.name + " → " + s.state.name;
                }
            return where;
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
            // Keyed by the full path and what is missing there; only the title is shortened, so two objects never share a key.
            var missing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); // Target to clip names.
            var curves = new Dictionary<string, Fixes.Curves>(StringComparer.Ordinal); // Target to the curves themselves, for the fix.
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            // Renderers whose mesh an animation swaps: a blendshape the starting mesh lacks may be on the swapped-in one.
            var meshSwapped = new HashSet<string>(avatar.Playables.SelectMany(p => Clips(p.Controller)).SelectMany(AnimationUtility.GetObjectReferenceCurveBindings)
                .Where(b => b.propertyName == "m_Mesh").Select(b => b.path), StringComparer.Ordinal);
            // Clips another Animator on the avatar also plays name paths from that Animator, not the avatar root.
            var otherAnimators = new HashSet<AnimationClip>(avatar.Root.GetComponentsInChildren<Animator>(true)
                .Where(a => a.gameObject != avatar.Root && a.runtimeAnimatorController is AnimatorController)
                .SelectMany(a => Clips((AnimatorController)a.runtimeAnimatorController)));
            foreach (var clip in Clips(playable.Controller).Where(c => !otherAnimators.Contains(c)))
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    if (binding.type == typeof(Animator) && binding.path.Length == 0) continue; // Parameters and muscles.
                    var animated = AnimationUtility.GetAnimatedObject(avatar.Root, binding);
                    string shown = binding.path.Length == 0 ? "(avatar root)" : Short(binding.path);
                    string target, title;
                    if (animated)
                    {
                        // A blendshape the mesh doesn't have (renamed or missing after a mesh update) is a common reason face tracking does nothing.
                        if (!(animated is SkinnedMeshRenderer renderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) || !renderer.sharedMesh ||
                            meshSwapped.Contains(binding.path) ||
                            renderer.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0) continue;
                        string shape = binding.propertyName.Substring("blendShape.".Length);
                        target = binding.path + "|blendShape|" + shape;
                        title = shown + " (no blendshape \"" + shape + "\")";
                    }
                    else
                    {
                        var transform = binding.path.Length == 0 ? avatar.Root.transform : avatar.Root.transform.Find(binding.path);
                        target = binding.path + (transform ? "|" + binding.type.FullName : "");
                        title = shown + (transform ? " (no " + binding.type.Name + ")" : "");
                    }
                    titles[target] = title;
                    if (!missing.TryGetValue(target, out var clips)) missing[target] = clips = new HashSet<string>(StringComparer.Ordinal);
                    clips.Add(clip.name);
                    if (!curves.TryGetValue(target, out var list)) curves[target] = list = new Fixes.Curves();
                    list.Bindings.Add((clip, binding, binding.isPPtrCurve));
                }
            foreach (var pair in missing)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "path|" + playable.Name + "|" + pair.Key, Playable = playable.Name,
                    Title = titles[pair.Key], Detail = string.Join("\n", pair.Value), Target = playable.Controller, Data = curves[pair.Key]
                });
        }

        // The controller's clips, walked by hand: AnimatorController.animationClips logs an error for every missing parameter.
        internal static IEnumerable<AnimationClip> ClipsOf(AnimatorController controller) => Clips(controller);

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
        // Every parameter name a Parameter Driver on the avatar sets.
        internal static IEnumerable<string> DriverSets(Avatar avatar)
        {
            foreach (var playable in avatar.Playables)
                foreach (var driver in Behaviours(playable.Controller).Where(b => b && b.GetType().Name == "VRCAvatarParameterDriver"))
                    using (var serialized = new SerializedObject(driver))
                    {
                        var entries = serialized.FindProperty("parameters");
                        for (int i = 0; entries != null && i < entries.arraySize; i++)
                        {
                            string name = entries.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue;
                            if (!string.IsNullOrEmpty(name)) yield return name;
                        }
                    }
        }

        private static void DriverTargets(Avatar avatar, List<Finding> findings)
        {
            // VRChat's own controllers (left as default, so not read here) use these built-ins.
            var known = new HashSet<string>(avatar.Playables.SelectMany(p => p.Controller.parameters.Select(q => q.name)).Concat(avatar.Expressions.Select(e => e.Name))
                .Concat(new[] { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV" }), StringComparer.Ordinal);
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
                var targets = group.GroupBy(f => f.Key).Select(g => g.First()).OrderBy(f => f.Title, StringComparer.Ordinal).ToList();
                var clips = targets.SelectMany(f => f.Detail.Split('\n')).Distinct().ToList(); // Clip names may hold commas.
                result.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "path|" + group.Key, Playable = group.Key, Target = group.First().Target,
                    Data = new Fixes.Curves { Bindings = group.Select(f => f.Data).OfType<Fixes.Curves>().SelectMany(c => c.Bindings).Distinct().ToList() },
                    Title = group.Key + " animates " + AvatarAnalyzer.N(targets.Count, "thing") + " this avatar doesn't have",
                    Detail = "Those parts of the animations do nothing here. Usually an object was renamed or removed, or the animation was made for a different avatar:\n" +
                        string.Join("\n", targets.Take(6).Select(f => "• " + f.Title)) + (targets.Count > 6 ? "\n• and " + (targets.Count - 6) + " more" : "") +
                        "\nUsed in " + (clips.Count == 1 ? "the clip " : "the clips ") + AvatarAnalyzer.Join(clips, 4) + ".",
                    Fix = "If an object was renamed or moved, rename it back or fix the path in the clip (the Animation window shows missing properties in yellow). If they aren't needed, delete those parts from the clips, or ignore this.",
                    Identity = string.Join("\n", targets.Select(f => f.Key)) + "\n" + string.Join("\n", clips)
                });
            }
            var drivers = findings.Where(f => f.Key.StartsWith("driver|", StringComparison.Ordinal)).GroupBy(f => f.Title).Select(g => g.First()).ToList();
            if (drivers.Count > 0)
                result.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "driver", Playable = drivers[0].Playable, Target = drivers[0].Target,
                    Title = "Parameter drivers use " + AvatarAnalyzer.N(drivers.Count, "parameter name") + " that " + (drivers.Count == 1 ? "doesn't" : "don't") + " exist",
                    Detail = (drivers.Count == 1 ? "No animator or Expression Parameter has this name, so setting it does nothing, and copying from it always gives 0:\n"
                        : "No animator or Expression Parameter has these names, so setting them does nothing, and copying from them always gives 0:\n") +
                        string.Join("\n", drivers.Take(8).Select(f => "• \"" + f.Title + "\" (" + f.Detail + ")")) + (drivers.Count > 8 ? "\n• and " + (drivers.Count - 8) + " more" : ""),
                    Fix = "Add the parameter to the animator that should react to it, or fix the name in the driver.",
                    Identity = string.Join("\n", drivers.Select(f => f.Title + " (" + f.Detail + ")"))
                });
            return result;
        }
    }
}
