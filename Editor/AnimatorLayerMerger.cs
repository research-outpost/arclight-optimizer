using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Fewer animator layers with the same animated values. A layer that is nothing but one always-playing state is
    // folded into a shared Direct blend tree layer (each such layer becomes a child weighted by a parameter that is
    // always 1), then emptied layers are removed. A layer qualifies only when the result cannot differ:
    //  - weight 1 in Override mode, no avatar mask, no IK pass, not synced to or from another layer, and no layer
    //    control behaviour anywhere names its index (so its weight never changes);
    //  - not the base layer (which always plays at weight 1 in Override mode);
    //  - a single default state, Write Defaults on, no transitions, behaviours, child state machines, time parameter
    //    or mirroring;
    //  - every clip under it holds each float value constant and has no object curves (so playback time and speed
    //    cannot matter); blend trees keep reading their own parameters;
    //  - no layer between it and its merged layer animates any of its properties, so moving it up changes no priority;
    //    a layer that would have to pass one hosts another merged layer where it stands (several merged layers).
    // A toggle state machine folds the same way, as a 1D blend tree on its parameter, when it switches in the frame the
    // parameter changes (a state machine with zero-duration transitions does; side-by-side tests play both frame by
    // frame) and the parameter only ever holds whole values:
    //  - the layer passes the checks above, except that it has several states and their transitions;
    //  - every state plays one constant clip, and every clip animates the same properties (so Write Defaults cannot
    //    matter);
    //  - every transition goes straight to a state or to Exit (which re-enters through the Entry transitions, first
    //    match wins, else the default state, in the same frame), with no exit time, zero duration, no mute or solo, and conditions
    //    on one Bool or Int parameter, or on two (an AND of both, folded as a 1D tree on the first whose children are 1D
    //    trees on the second);
    //  - for every value (every pair of values with two parameters), every state reaches the same state with its first transition, and that
    //    state stays (one transition per frame, so the machine always settles in the frame the value changes);
    //  - the parameter is an expression parameter of the same type that no component names (contacts and PhysBones
    //    write fractions), that no behaviour names except parameter drivers that Set a whole value (Add, Random and
    //    Copy follow the animator type, which becomes Float), and that no condition tests with NotEqual or uses as a
    //    mirror parameter in its controller.
    // The parameter becomes Float in that controller (VRChat converts values between animator types) and its other
    // conditions are rewritten to the matching Greater/Less tests. Emptied layers are only removed when no layer is synced and no layer
    // control behaviour exists, as both refer to layers by index; otherwise they stay as empty weight-0 layers.
    // Before folding, layers that can do nothing are removed (or emptied) the same way: no behaviours, no animated
    // animator parameters, not the base layer, not named by a layer control, and either
    //  - every curve names an object or component the avatar does not have, or
    //  - weight 0, animating only activeness, enabled flags and material properties, none of which another layer also
    //    animates (a weight-0 layer still binds its properties, which with Write Defaults rewrites their defaults every
    //    frame; that matters for anything something besides animation writes, such as bones PhysBones or constraints
    //    move, blend shapes VRChat drives and audio a behaviour plays, so such layers stay).
    internal static class AnimatorLayerMerger
    {
        // The always-1 weight parameter of this build's merged layers: "Arclight/AlwaysOne", with a number added when the avatar
        // already names that anywhere (a parameter, a blend tree, a state, a behaviour, an animated Animator property, an expression parameter), so nothing else can
        // write it and no authored tree can pass for a merged one (AnimationAnalysis.FoldedLayers). Null until Run picks it.
        internal static string AlwaysOne { get; private set; }

        internal static void NewBuild() => AlwaysOne = null;

        private static string PickAlwaysOne(List<(Transform Owner, VirtualAnimatorController Controller)> owned)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, controller) in owned)
            {
                named.UnionWith(controller.Parameters.Keys);
                foreach (var node in controller.AllReachableNodes())
                    if (node is VirtualBlendTree tree)
                    {
                        named.Add(tree.BlendParameter ?? ""); named.Add(tree.BlendParameterY ?? "");
                        foreach (var child in tree.Children) named.Add(child.DirectBlendParameter ?? "");
                    }
                    else if (node is VirtualTransitionBase transition) foreach (var condition in transition.Conditions) named.Add(condition.parameter);
                    else if (node is VirtualState state)
                    {
                        named.UnionWith(new[] { state.SpeedParameter, state.TimeParameter, state.CycleOffsetParameter, state.MirrorParameter }.Where(p => p != null));
                        foreach (var behaviour in state.Behaviours) ParameterCleaner.Strings(behaviour, named);
                    }
                    else if (node is VirtualClip clip) named.UnionWith(clip.GetFloatCurveBindings().Where(b => b.type == typeof(Animator)).Select(b => b.propertyName));
                    else if (node is VirtualStateMachine machine) foreach (var behaviour in machine.Behaviours) ParameterCleaner.Strings(behaviour, named);
                foreach (var behaviour in controller.Layers.SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v))) ParameterCleaner.Strings(behaviour, named);
            }
            foreach (var owner in owned.Select(o => o.Owner).Where(o => o).Select(o => o.root).Distinct())
                foreach (var component in owner.GetComponentsInChildren<Component>(true).Where(c => c && !(c is Transform)))
                {
                    ParameterCleaner.Strings(component, named);
                    if (component.GetType().Name == "VRCAvatarDescriptor")
                        using (var serialized = new SerializedObject(component))
                            ParameterCleaner.Strings(serialized.FindProperty("expressionParameters")?.objectReferenceValue, named);
                }
            string name = "Arclight/AlwaysOne";
            for (int n = 2; named.Contains(name); n++) name = "Arclight/AlwaysOne " + n;
            return name;
        }

        internal sealed class Result { public int Merged, Removed, Inert, Toggles, MergedClips; public readonly Dictionary<string, List<string>> WhyNot = new Dictionary<string, List<string>>(); }

        internal static Result Run(IEnumerable<VirtualAnimatorController> controllers) => Run(controllers.Select(c => ((Transform)null, c)));

        // owner: the object the controller's paths start from (null skips the missing-object test).
        // wholeValues: whether a parameter of this animator type is an expression parameter of the same type that no
        // component names (see WholeValues); null converts no toggles.
        // playables: each VRChat playable layer's controller and its playable name (FX, Action...), so layer controls from other
        // playables can be renumbered when a layer below them goes; null keeps the layers below them (emptied instead).
        internal static Result Run(IEnumerable<(Transform Owner, VirtualAnimatorController Controller)> controllers,
            Func<string, AnimatorControllerParameterType, bool> wholeValues = null, bool removeInert = true,
            IReadOnlyDictionary<VirtualAnimatorController, string> playables = null)
        {
            var result = new Result();
            var owned = controllers.Where(c => c.Controller != null).ToList();
            var list = owned.Select(c => c.Controller).ToList();
            if (AlwaysOne == null) AlwaysOne = PickAlwaysOne(owned);
            // Layer indices any layer control behaviour (from any controller) can change the weight of.
            var controlled = new HashSet<int>();
            var controls = new List<StateMachineBehaviour>();
            // NDMF rewrites a layer control that targets its own playable to the layer's virtual index; one from another
            // playable keeps the position. Either form names the layer.
            bool Controlled(VirtualLayer layer, int position) => controlled.Contains(position) || controlled.Contains(layer.VirtualLayerIndex);
            // Parameters a behaviour may write a fraction to, or reads in an unknown way.
            var behaviourNamed = new HashSet<string>(StringComparer.Ordinal);
            // Synced layers keep their own state behaviours apart from the states they share.
            foreach (var behaviour in list.SelectMany(c => c.AllReachableNodes()).SelectMany(Behaviours)
                .Concat(list.SelectMany(c => c.Layers).SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v))))
            {
                if (!behaviour) continue;
                if (behaviour.GetType().Name == "VRCAvatarParameterDriver")
                {
                    if (!DriverWrites(behaviour, behaviourNamed)) wholeValues = null;
                    continue;
                }
                ParameterCleaner.Strings(behaviour, behaviourNamed);
                // VRCPlayableLayerControl changes a whole playable's weight, which scales every layer alike; only per-layer control matters.
                if (behaviour.GetType().Name != "VRCAnimatorLayerControl") continue;
                using (var serialized = new SerializedObject(behaviour))
                {
                    var layer = serialized.FindProperty("layer");
                    if (layer == null) return result; // Unknown layer control: leave every controller alone.
                    controlled.Add(layer.intValue);
                    controls.Add(behaviour);
                }
            }

            // Removing a layer shifts the positions of the layers above it. A control on the same playable holds NDMF's
            // virtual index, which NDMF rewrites to the new position at commit; one from another playable holds a raw position,
            // which nobody rewrites. So a layer can be removed only above every raw position a control names; below that it
            // is emptied instead (an empty layer at weight 0 animates nothing).
            // Layer controls that name another playable than their own controller's: (behaviour, playable named). Null when any
            // control's own playable is unknown.
            List<(StateMachineBehaviour Behaviour, string Playable)> rawControls = playables == null ? null : new List<(StateMachineBehaviour, string)>();
            if (rawControls != null)
                foreach (var controller in list)
                {
                    var behaviours = controller.AllReachableNodes().SelectMany(Behaviours)
                        .Concat(controller.Layers.SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v)))
                        .Where(b => b && b.GetType().Name == "VRCAnimatorLayerControl").ToList();
                    if (behaviours.Count == 0) continue;
                    if (!playables.TryGetValue(controller, out string own)) { rawControls = null; break; }
                    foreach (var behaviour in behaviours)
                        using (var serialized = new SerializedObject(behaviour))
                        {
                            var playable = serialized.FindProperty("playable");
                            if (playable == null || playable.propertyType != SerializedPropertyType.Enum) { rawControls = null; break; }
                            string named = playable.enumNames[playable.enumValueIndex];
                            if (named != own) rawControls.Add((behaviour, named));
                        }
                    if (rawControls == null) break;
                }
            var virtualIndices = new HashSet<int>(list.SelectMany(c => c.Layers).Select(l => l.VirtualLayerIndex));
            int highestRaw = controlled.Where(v => !virtualIndices.Contains(v)).DefaultIfEmpty(-1).Max();
            int RemoveOrEmpty(VirtualAnimatorController controller, ICollection<VirtualLayer> layers)
            {
                var positions = controller.Layers.Select((l, i) => (l, i)).ToDictionary(p => p.l, p => p.i);
                // With every raw control known (it names another playable than its own controller's), the controls that name this
                // controller's playable are renumbered instead, and every emptied layer can go.
                var targeting = playables != null && playables.TryGetValue(controller, out string own) && rawControls != null
                    ? rawControls.Where(r => r.Playable == own).ToList() : null;
                var removable = targeting != null ? layers.ToList() : layers.Where(l => positions[l] > highestRaw).ToList();
                if (targeting != null)
                    foreach (var (behaviour, _) in targeting)
                        using (var serialized = new SerializedObject(behaviour))
                        {
                            var index = serialized.FindProperty("layer");
                            int shift = removable.Count(l => positions[l] < index.intValue);
                            if (shift == 0) continue;
                            index.intValue -= shift;
                            serialized.ApplyModifiedPropertiesWithoutUndo();
                        }
                // Read every control again: renumbered positions can coincide with other controls' old ones.
                if (targeting != null && targeting.Count > 0)
                {
                    controlled.Clear();
                    foreach (var control in controls)
                        using (var serialized = new SerializedObject(control)) controlled.Add(serialized.FindProperty("layer").intValue);
                }
                foreach (var layer in layers.Except(removable)) Empty(layer);
                if (removable.Count > 0) controller.RemoveLayers(removable.Contains);
                return removable.Count;
            }

            var allBindings = owned.SelectMany(c => c.Controller.Layers.Select(l => (c.Controller, Layer: l, Bindings: Bindings(l)))).ToList();
            foreach (var (owner, controller) in owned)
            {
                var layers = controller.Layers.ToList();
                if (layers.Any(l => l.SyncedLayerIndex >= 0)) continue;
                var inert = layers.Where((l, i) => removeInert && i > 0 && !Controlled(l, i) && Inert(l, owner, allBindings.Where(o => o.Layer != l).SelectMany(o => o.Bindings), owned.All(o => o.Owner == owner))).ToList();
                if (inert.Count > 0)
                {
                    result.Inert += inert.Count;
                    RemoveOrEmpty(controller, inert);
                    layers = controller.Layers.ToList();
                }

                // Parameters every use of which in this controller survives becoming Float.
                var retypable = new HashSet<string>(StringComparer.Ordinal);
                if (wholeValues != null)
                {
                    var blocked = new HashSet<string>(behaviourNamed, StringComparer.Ordinal);
                    foreach (var node in controller.AllReachableNodes())
                        if (node is VirtualTransitionBase transition)
                        {
                            foreach (var condition in transition.Conditions)
                                if (condition.mode == AnimatorConditionMode.NotEqual || condition.threshold != Mathf.Round(condition.threshold) &&
                                    controller.Parameters.TryGetValue(condition.parameter, out var p) && p.type == AnimatorControllerParameterType.Int)
                                    blocked.Add(condition.parameter);
                        }
                        else if (node is VirtualState state)
                        {
                            // Mirror needs a Bool. Speed, cycle offset and time act only on Float parameters, so a stale
                            // reference to a Bool or Int parameter would wake up once it became Float.
                            foreach (var name in new[] { state.MirrorParameter, state.SpeedParameter, state.CycleOffsetParameter, state.TimeParameter })
                                if (name != null) blocked.Add(name);
                        }
                        else if (node is VirtualBlendTree blend)
                        {
                            blocked.Add(blend.BlendParameter ?? ""); blocked.Add(blend.BlendParameterY ?? "");
                            foreach (var child in blend.Children) blocked.Add(child.DirectBlendParameter ?? "");
                        }
                        else if (node is VirtualClip clip)
                            foreach (var binding in clip.GetFloatCurveBindings())
                                if (binding.type == typeof(Animator)) blocked.Add(binding.propertyName);
                    foreach (var p in controller.Parameters.Values)
                        if ((p.type == AnimatorControllerParameterType.Bool || p.type == AnimatorControllerParameterType.Int) &&
                            !blocked.Contains(p.name) && wholeValues(p.name, p.type))
                            retypable.Add(p.name);
                }

                // Several merged layers: walking down from the top, a qualifying layer joins the first group none of whose
                // blocked properties it animates, or hosts a new group where it stands. A group's blocked set holds every
                // property animated between the current position and its host, members included, so a member only moves
                // up past layers it shares no property with (the highest writer of every property stays the same), and the
                // members of one Direct tree never sum the same property.
                var groups = new List<(List<(VirtualLayer Layer, VirtualMotion Motion, string Parameter)> Members, HashSet<EditorCurveBinding> Blocked)>();
                for (int i = layers.Count - 1; i >= 0; i--)
                {
                    var layer = layers[i];
                    var bindings = Bindings(layer);
                    // The base layer always plays at weight 1 in Override mode; removing it would promote the next layer.
                    if (i > 0 && !Controlled(layer, i))
                    {
                        var motion = Mergeable(layer);
                        string parameter = null;
                        if (motion == null && retypable.Count > 0) motion = Toggle(layer, controller, retypable, owner, out parameter);
                        if (motion == null) { string why = WhyNotFolded(layer, controller, retypable); if (!result.WhyNot.TryGetValue(why, out var named)) result.WhyNot[why] = named = new List<string>(); named.Add(layer.Name); }
                        if (motion != null)
                        {
                            int g = groups.FindIndex(x => !bindings.Overlaps(x.Blocked));
                            if (g < 0) { groups.Add((new List<(VirtualLayer, VirtualMotion, string)>(), new HashSet<EditorCurveBinding>())); g = groups.Count - 1; }
                            groups[g].Members.Add((layer, motion, parameter));
                        }
                    }
                    foreach (var group in groups) group.Blocked.UnionWith(bindings);
                }
                foreach (var (members, _) in groups)
                {
                    if (members.Count < 2) continue;
                    var merge = Enumerable.Reverse(members).ToList(); // Lowest index first; the highest becomes the merged layer.
                    var tree = VirtualBlendTree.Create("Arclight merged layers");
                    tree.BlendType = BlendTreeType.Direct;
                    // Plain clips become one child: every member is constant and animates its own properties, so at weight 1
                    // one clip with all their curves writes the same values with fewer children to evaluate. Clips with
                    // Animator curves (muscles, root motion, animator parameters) stay apart, as their clip settings matter.
                    var motions = merge.Select(m => m.Motion).ToList();
                    var plain = motions.OfType<VirtualClip>().Where(c => !c.GetFloatCurveBindings().Any(b => b.type == typeof(Animator))).ToList();
                    if (plain.Count > 1)
                    {
                        var combined = VirtualClip.Create("Arclight merged clips");
                        foreach (var clip in plain)
                            foreach (var binding in clip.GetFloatCurveBindings())
                                combined.SetFloatCurve(binding, clip.GetFloatCurve(binding));
                        motions[motions.IndexOf(plain[0])] = combined;
                        motions.RemoveAll(m => plain.Contains(m));
                        result.MergedClips += plain.Count - 1;
                    }
                    tree.Children = motions.Select(m => new VirtualBlendTree.VirtualChildMotion { Motion = m, DirectBlendParameter = AlwaysOne, TimeScale = 1 })
                        .ToImmutableList();
                    var host = merge[merge.Count - 1].Layer;
                    Empty(host);
                    host.DefaultWeight = 1;
                    var hostState = host.StateMachine.AddState("Arclight merged layers", tree);
                    hostState.WriteDefaultValues = true;
                    host.StateMachine.DefaultState = hostState;
                    host.Name = "Arclight merged layers";
                    foreach (string parameter in merge.Where(m => m.Parameter != null).SelectMany(m => m.Parameter.Split('\n')).Distinct().ToList())
                        if (controller.Parameters[parameter].type != AnimatorControllerParameterType.Float) Retype(controller, parameter);
                    result.Toggles += merge.Count(m => m.Parameter != null);
                    if (!controller.Parameters.ContainsKey(AlwaysOne))
                        controller.Parameters = controller.Parameters.Add(AlwaysOne,
                            new AnimatorControllerParameter { name = AlwaysOne, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
                    var emptied = merge.Take(merge.Count - 1).Select(m => m.Layer).ToList();
                    result.Merged += emptied.Count;
                    result.Removed += RemoveOrEmpty(controller, emptied);
                }
            }
            return result;
        }

        private static void Empty(VirtualLayer layer)
        {
            layer.StateMachine.AnyStateTransitions = layer.StateMachine.AnyStateTransitions.Clear();
            layer.StateMachine.EntryTransitions = layer.StateMachine.EntryTransitions.Clear();
            layer.StateMachine.StateMachineTransitions = layer.StateMachine.StateMachineTransitions.Clear();
            layer.StateMachine.StateMachines = layer.StateMachine.StateMachines.Clear();
            layer.StateMachine.States = layer.StateMachine.States.Clear();
            layer.StateMachine.DefaultState = null;
            layer.DefaultWeight = 0;
        }

        // Adds the parameters the driver may leave holding a fraction (anything but Set to a whole value);
        // false when the driver cannot be read.
        private static bool DriverWrites(StateMachineBehaviour driver, HashSet<string> into)
        {
            using (var serialized = new SerializedObject(driver))
            {
                var entries = serialized.FindProperty("parameters");
                if (entries == null || !entries.isArray) return false;
                for (int i = 0; i < entries.arraySize; i++)
                {
                    var entry = entries.GetArrayElementAtIndex(i);
                    var name = entry.FindPropertyRelative("name");
                    var type = entry.FindPropertyRelative("type");
                    var value = entry.FindPropertyRelative("value");
                    if (name == null || type == null || value == null) return false;
                    if (type.intValue != 0 || value.floatValue != Mathf.Round(value.floatValue)) into.Add(name.stringValue); // 0 is Set.
                }
            }
            return true;
        }

        // Expression parameters (name to animator type) that no component on the avatar names.
        internal static Func<string, AnimatorControllerParameterType, bool> WholeValues(GameObject root)
        {
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return null;
            Object parameters;
            using (var serialized = new SerializedObject(descriptor))
                parameters = serialized.FindProperty("expressionParameters")?.objectReferenceValue;
            if (!parameters) return null;
            var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            using (var serialized = new SerializedObject(parameters))
            {
                var array = serialized.FindProperty("parameters");
                if (array == null) return null;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var entry = array.GetArrayElementAtIndex(i);
                    string name = entry.FindPropertyRelative("name")?.stringValue;
                    int? valueType = entry.FindPropertyRelative("valueType")?.intValue;
                    if (string.IsNullOrEmpty(name) || valueType == null) continue;
                    if (types.ContainsKey(name)) { types[name] = AnimatorControllerParameterType.Trigger; continue; } // Declared twice: never matches.
                    types[name] = valueType == 0 ? AnimatorControllerParameterType.Int : valueType == 2 ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float;
                }
            }
            var named = new HashSet<string>(StringComparer.Ordinal);
            var prefixes = new List<string>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component == descriptor || component is Transform) continue;
                if (component.GetType().Name == "VRCPhysBone")
                    using (var serialized = new SerializedObject(component))
                    {
                        string prefix = serialized.FindProperty("parameter")?.stringValue;
                        if (!string.IsNullOrEmpty(prefix)) prefixes.Add(prefix + "_");
                    }
                ParameterCleaner.Strings(component, named);
            }
            return (name, type) => types.TryGetValue(name, out var declared) && declared == type && !named.Contains(name) &&
                !prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
        }

        // The toggle state machine as a 1D blend tree on its parameter when the layer qualifies (see above), else null.
        // owner: where the clips' paths start; with it, Write Defaults toggle states may animate different properties (see Filled).
        private static VirtualMotion Toggle(VirtualLayer layer, VirtualAnimatorController controller, HashSet<string> retypable, Transform owner, out string parameter)
        {
            parameter = null;
            var machine = layer.StateMachine;
            if (machine == null || layer.AvatarMask != null || layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.DefaultWeight != 1 || layer.IKPass) return null;
            if (machine.States.Count < 2 || machine.StateMachines.Count != 0 || machine.Behaviours.Count != 0 ||
                machine.StateMachineTransitions.Count != 0) return null;
            var states = machine.States.Select(s => s.State).ToList();
            bool writeDefaults = owner && states.All(s => s != null && s.WriteDefaultValues);
            if (states.Any(s => s == null || s.Behaviours.Count != 0 || s.TimeParameter != null || s.Mirror || s.MirrorParameter != null ||
                    !(s.Motion is VirtualClip clip && Constant(clip) || s.Motion == null && writeDefaults)) || !states.Contains(machine.DefaultState)) return null;
            var properties = new HashSet<EditorCurveBinding>(states.Where(s => s.Motion != null).SelectMany(s => ((VirtualClip)s.Motion).GetFloatCurveBindings()));
            var filled = Filled(states, properties, writeDefaults ? owner : null);
            if (filled == null) return null;

            var transitions = machine.AnyStateTransitions.Concat(states.SelectMany(s => s.Transitions)).ToList();
            if (transitions.Count == 0) return null;
            foreach (var t in transitions)
            {
                // Only a state's own transition can go to Exit.
                bool exit = t.IsExit && t.DestinationState == null && !machine.AnyStateTransitions.Contains(t);
                if (t.Mute || t.Solo || t.ExitTime != null || t.Duration != 0 || t.DestinationStateMachine != null ||
                    !exit && (t.IsExit || t.DestinationState == null || !states.Contains(t.DestinationState))) return null;
                // Unity ignores a transition with neither conditions nor exit time; Next would take it every frame.
                if (t.Conditions.Count == 0) return null;
            }
            // Entry transitions go straight to a state. One without conditions stays (how Unity orders it is untested).
            if (machine.EntryTransitions.Any(t => t.Mute || t.Solo || t.IsExit || t.DestinationStateMachine != null || t.Conditions.Count == 0 ||
                    t.DestinationState == null || !states.Contains(t.DestinationState))) return null;
            // One or two parameters (a toggle that tests two, such as an outfit part and its colour, is an AND of both).
            var conditions = transitions.Cast<VirtualTransitionBase>().Concat(machine.EntryTransitions).SelectMany(t => t.Conditions).ToList();
            var names = conditions.Select(c => c.parameter).Distinct().ToList();
            if (names.Count < 1 || names.Count > 2) return null;
            var lasts = new int[names.Count];
            for (int p = 0; p < names.Count; p++)
            {
                if (!retypable.Contains(names[p]) || !controller.Parameters.TryGetValue(names[p], out var declared)) return null;
                bool isBool = declared.type == AnimatorControllerParameterType.Bool;
                if (conditions.Where(c => c.parameter == names[p]).Any(c => isBool
                        ? c.mode != AnimatorConditionMode.If && c.mode != AnimatorConditionMode.IfNot
                        : c.mode != AnimatorConditionMode.Equals && c.mode != AnimatorConditionMode.Greater && c.mode != AnimatorConditionMode.Less)) return null;
                lasts[p] = isBool ? 1 : 255;
            }

            // The state the machine settles in for each combination of values; it must be reached from every state in one
            // transition, and stay.
            int lastB = names.Count == 2 ? lasts[1] : 0;
            var settle = new VirtualState[lasts[0] + 1, lastB + 1];
            for (int a = 0; a <= lasts[0]; a++)
                for (int b = 0; b <= lastB; b++)
                {
                    int va = a, vb = b;
                    Func<string, int> value = n => n == names[0] ? va : vb;
                    var target = Next(machine, states[0], value);
                    if (target == null || states.Any(s => Next(machine, s, value) != target)) return null;
                    settle[a, b] = target;
                }

            // The outer tree on the first parameter; with two, each run of equal rows is a tree on the second (or the state's
            // clip when the row is one state). Only whole values occur, so a run needs a child at each end and nothing between
            // runs is ever sampled.
            VirtualMotion Run(string name, int last, Func<int, VirtualMotion> motionAt, Func<int, int, bool> same, string treeName)
            {
                var tree = VirtualBlendTree.Create(treeName);
                tree.BlendType = BlendTreeType.Simple1D;
                tree.BlendParameter = name;
                tree.UseAutomaticThresholds = false;
                var children = ImmutableList.CreateBuilder<VirtualBlendTree.VirtualChildMotion>();
                for (int start = 0; start <= last;)
                {
                    int end = start;
                    while (end < last && same(end + 1, start)) end++;
                    var motion = motionAt(start);
                    children.Add(new VirtualBlendTree.VirtualChildMotion { Motion = motion, Threshold = start, TimeScale = 1 });
                    if (end > start) children.Add(new VirtualBlendTree.VirtualChildMotion { Motion = motion, Threshold = end, TimeScale = 1 });
                    start = end + 1;
                }
                tree.Children = children.ToImmutable();
                return tree;
            }
            bool SameRow(int x, int y) => Enumerable.Range(0, lastB + 1).All(b => settle[x, b] == settle[y, b]);
            VirtualMotion Row(int a) => Enumerable.Range(0, lastB + 1).All(b => settle[a, b] == settle[a, 0])
                ? filled[settle[a, 0]]
                : Run(names[1], lastB, b => filled[settle[a, b]], (x, y) => settle[a, x] == settle[a, y], layer.Name + " (" + names[1] + ")");
            parameter = string.Join("\n", names); // Every parameter the fold reads; each becomes Float.
            return names.Count == 1
                ? Run(names[0], lasts[0], a => filled[settle[a, 0]], (x, y) => settle[x, 0] == settle[y, 0], layer.Name)
                : Run(names[0], lasts[0], Row, SameRow, layer.Name);
        }

        // Each state's clip holding every property the layer animates. Where states animate the same properties, that is
        // their own clip. Otherwise (owner given, Write Defaults on in every state) a state that leaves a property out
        // writes the property's default, the value it held when the Animator bound; a constant curve at that value writes
        // the same. Only activeness, enabled flags and blend shape weights, read from the build avatar; anything else
        // returns null.
        private static Dictionary<VirtualState, VirtualMotion> Filled(List<VirtualState> states, HashSet<EditorCurveBinding> properties, Transform owner)
        {
            var filled = new Dictionary<VirtualState, VirtualMotion>();
            foreach (var state in states)
            {
                var own = state.Motion == null ? new HashSet<EditorCurveBinding>() : new HashSet<EditorCurveBinding>(((VirtualClip)state.Motion).GetFloatCurveBindings());
                if (state.Motion != null && own.SetEquals(properties)) { filled[state] = state.Motion; continue; }
                if (!owner) return null;
                var clip = state.Motion == null ? VirtualClip.Create(state.Name) : ((VirtualClip)state.Motion).Clone();
                foreach (var binding in properties.Where(p => !own.Contains(p)))
                {
                    var value = DefaultValue(owner, binding);
                    if (value == null) return null;
                    clip.SetFloatCurve(binding, new AnimationCurve(new Keyframe(0, value.Value)));
                }
                filled[state] = clip;
            }
            return filled;
        }

        private static float? DefaultValue(Transform owner, EditorCurveBinding binding)
        {
            var targets = AvatarAnalysis.Resolve(owner, binding.path).ToList();
            if (targets.Count != 1) return null;
            var target = targets[0];
            string property = binding.propertyName ?? "";
            if (binding.type == typeof(GameObject)) return property == "m_IsActive" ? (target.gameObject.activeSelf ? 1 : 0) : (float?)null;
            if (binding.type == null || !(target.GetComponent(binding.type) is Component component) || !component) return null;
            if (property == "m_Enabled")
                return component is Behaviour b ? (b.enabled ? 1 : 0) : component is Renderer r ? (r.enabled ? 1 : 0) : component is Collider c ? (c.enabled ? 1 : 0) : (float?)null;
            if (component is SkinnedMeshRenderer skinned && skinned.sharedMesh && property.StartsWith("blendShape.", StringComparison.Ordinal))
            {
                int index = skinned.sharedMesh.GetBlendShapeIndex(property.Substring("blendShape.".Length));
                return index < 0 ? (float?)null : skinned.GetBlendShapeWeight(index);
            }
            return null;
        }

        // The state after one frame at this value: Any State transitions first, then the state's own, first match wins. Exit
        // re-enters through the first Entry transition that holds, else the default state. Null when Exit would re-enter the
        // state it left (Unity would restart that state every frame; untested).
        private static VirtualState Next(VirtualStateMachine machine, VirtualState from, Func<string, int> value)
        {
            foreach (var t in machine.AnyStateTransitions)
                if ((t.CanTransitionToSelf || t.DestinationState != from) && t.Conditions.All(c => Holds(c, value(c.parameter)))) return t.DestinationState;
            foreach (var t in from.Transitions)
                if (t.Conditions.All(c => Holds(c, value(c.parameter))))
                {
                    if (!t.IsExit) return t.DestinationState;
                    var entered = machine.EntryTransitions.FirstOrDefault(e => e.Conditions.All(c => Holds(c, value(c.parameter))))?.DestinationState ?? machine.DefaultState;
                    return entered == from ? null : entered;
                }
            return from;
        }

        private static bool Holds(AnimatorCondition condition, int value)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.If: return value != 0;
                case AnimatorConditionMode.IfNot: return value == 0;
                case AnimatorConditionMode.Equals: return value == condition.threshold;
                case AnimatorConditionMode.Greater: return value > condition.threshold;
                case AnimatorConditionMode.Less: return value < condition.threshold;
                default: return false;
            }
        }

        // Makes the parameter Float in this controller and rewrites its conditions to the same tests on whole values.
        private static void Retype(VirtualAnimatorController controller, string name)
        {
            var old = controller.Parameters[name];
            controller.Parameters = controller.Parameters.SetItem(name, new AnimatorControllerParameter
            {
                name = name, type = AnimatorControllerParameterType.Float,
                defaultFloat = old.type == AnimatorControllerParameterType.Bool ? (old.defaultBool ? 1 : 0) : old.defaultInt,
            });
            foreach (var transition in controller.AllReachableNodes().OfType<VirtualTransitionBase>().ToList())
            {
                if (!transition.Conditions.Any(c => c.parameter == name)) continue;
                var conditions = ImmutableList.CreateBuilder<AnimatorCondition>();
                foreach (var c in transition.Conditions)
                {
                    if (c.parameter != name) { conditions.Add(c); continue; }
                    switch (c.mode)
                    {
                        case AnimatorConditionMode.If: conditions.Add(new AnimatorCondition { parameter = name, mode = AnimatorConditionMode.Greater, threshold = .5f }); break;
                        case AnimatorConditionMode.IfNot: conditions.Add(new AnimatorCondition { parameter = name, mode = AnimatorConditionMode.Less, threshold = .5f }); break;
                        case AnimatorConditionMode.Equals:
                            conditions.Add(new AnimatorCondition { parameter = name, mode = AnimatorConditionMode.Greater, threshold = c.threshold - .5f });
                            conditions.Add(new AnimatorCondition { parameter = name, mode = AnimatorConditionMode.Less, threshold = c.threshold + .5f });
                            break;
                        default: conditions.Add(c); break; // Greater and Less on whole values test the same.
                    }
                }
                transition.Conditions = conditions.ToImmutable();
            }
        }

        // A layer that cannot change any value (see above).
        // shareOwner: every controller's paths start from the same object, so equal bindings mean the same property.
        private static bool Inert(VirtualLayer layer, Transform owner, IEnumerable<EditorCurveBinding> others, bool shareOwner)
        {
            if (layer.StateMachine == null || layer.SyncedLayerIndex >= 0) return false;
            var nodes = layer.StateMachine.AllReachableNodes().ToList();
            if (nodes.SelectMany(Behaviours).Any()) return false;
            var bindings = Bindings(layer);
            if (bindings.Any(b => b.type == typeof(Animator))) return false;
            if (owner && bindings.Count > 0 && bindings.All(b => !AvatarAnalysis.Resolve(owner, b.path).Any(t =>
                    b.type == typeof(GameObject) || b.type == typeof(Transform) || b.type != null && t.GetComponent(b.type)))) return true;
            // Only properties nothing but animation writes: activeness, enabled flags and material properties. Transforms, blend
            // shapes VRChat drives and audio properties a behaviour sets would lose their per-frame default rewrite.
            return shareOwner && layer.DefaultWeight == 0 && bindings.All(b => b.type == typeof(GameObject) && b.propertyName == "m_IsActive" ||
                b.propertyName == "m_Enabled" || (b.propertyName ?? "").StartsWith("material.", StringComparison.Ordinal)) && !bindings.Overlaps(others);
        }

        private static IEnumerable<StateMachineBehaviour> Behaviours(VirtualNode node) =>
            node is VirtualState state ? state.Behaviours : node is VirtualStateMachine machine ? machine.Behaviours : Enumerable.Empty<StateMachineBehaviour>();

        // The layer's one motion when the layer qualifies (see above), else null.
        private static VirtualMotion Mergeable(VirtualLayer layer)
        {
            var machine = layer.StateMachine;
            if (machine == null || layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.DefaultWeight != 1 || layer.IKPass) return null;
            if (machine.States.Count != 1 || machine.StateMachines.Count != 0 || machine.Behaviours.Count != 0 ||
                machine.AnyStateTransitions.Count != 0 || machine.EntryTransitions.Count != 0 || machine.StateMachineTransitions.Count != 0) return null;
            var state = machine.States[0].State;
            // Write Defaults off is the same here: one state playing constant clips writes their values every frame either way, which
            // is what the folded child does. Only for a single clip (Write Defaults off changes how a blend tree mixes).
            if (state == null || machine.DefaultState != state || !state.WriteDefaultValues && !(state.Motion is VirtualClip) || state.Behaviours.Count != 0 ||
                state.Transitions.Count != 0 || state.TimeParameter != null || state.Mirror || state.MirrorParameter != null || state.Motion == null) return null;
            if (state.Motion.AllReachableNodes().OfType<VirtualClip>().Any(c => !Constant(c))) return null;
            if (state.Motion.AllReachableNodes().OfType<VirtualBlendTree>().Any(t => t.BlendType == BlendTreeType.Direct && t.NormalizedBlendValues)) return null;
            // An avatar mask filters only Transform and humanoid (Animator) curves; with none of those it changes nothing (MaskFilteringTests).
            if (layer.AvatarMask != null && state.Motion.AllReachableNodes().OfType<VirtualClip>()
                    .Any(c => c.GetFloatCurveBindings().Concat(c.GetObjectCurveBindings()).Any(b => b.type == typeof(Transform) || b.type == typeof(Animator)))) return null;
            return state.Motion;
        }

        // The first rule a layer that did not fold breaks, for the report (each is a condition of Mergeable or Toggle).
        private static string WhyNotFolded(VirtualLayer layer, VirtualAnimatorController controller, HashSet<string> retypable)
        {
            var machine = layer.StateMachine;
            if (machine == null) return "no state machine";
            if (layer.AvatarMask != null || layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.DefaultWeight != 1 || layer.IKPass)
                return "an avatar mask, additive blending, a weight below 1 or an IK pass";
            var states = machine.States.Select(s => s.State).Where(s => s != null).ToList();
            if (machine.StateMachines.Count != 0 || machine.Behaviours.Count != 0 || states.Any(s => s.Behaviours.Count != 0))
                return "sub-state machines or state behaviours (parameter drivers, tracking control...)";
            var transitions = machine.AnyStateTransitions.Concat(states.SelectMany(s => s.Transitions)).ToList();
            if (transitions.Any(t => t.ExitTime != null || t.Duration != 0)) return "a transition with exit time or a blend duration";
            if (machine.StateMachineTransitions.Count != 0 || transitions.Any(t => t.DestinationStateMachine != null)) return "transitions into a sub-state machine";
            if (states.Any(s => s.Motion != null && s.Motion.AllReachableNodes().OfType<VirtualClip>().Any(c => !Constant(c))))
                return "a clip that changes over time (an animation, not a toggle)";
            var names = transitions.SelectMany(t => t.Conditions).Select(c => c.parameter).Distinct().ToList();
            if (names.Count > 2) return "transitions on more than two parameters";
            if (names.Any(n => !retypable.Contains(n))) return "a parameter that can hold fractions or that something else writes";
            return "other conditions (Write Defaults, mirroring, time parameters, or states that take more than one transition)";
        }

        // Every curve holds one value for all time.
        private static bool Constant(VirtualClip clip)
        {
            if (clip.IsMarkerClip) return false;
            foreach (var binding in clip.GetFloatCurveBindings())
            {
                var keys = clip.GetFloatCurve(binding)?.keys;
                if (keys == null || keys.Length == 0) return false;
                // Equal values with flat or stepped (infinite) tangents hold that value; a finite slope would bulge between keys.
                if (keys.Length > 1 && keys.Any(k => !k.value.Equals(keys[0].value) || !Flat(k.inTangent) || !Flat(k.outTangent))) return false;
            }
            // Object curves (material and mesh swaps) inside a Direct blend tree are not covered by the side-by-side test.
            return !clip.GetObjectCurveBindings().Any();
        }

        internal static bool Flat(float tangent) => tangent == 0 || float.IsPositiveInfinity(tangent); // Unity writes stepped keys as +Infinity.

        private static HashSet<EditorCurveBinding> Bindings(VirtualLayer layer)
        {
            var set = new HashSet<EditorCurveBinding>();
            if (layer.StateMachine == null) return set;
            foreach (var clip in layer.StateMachine.AllReachableNodes().OfType<VirtualClip>())
            {
                set.UnionWith(clip.GetFloatCurveBindings().Select(Whole));
                set.UnionWith(clip.GetObjectCurveBindings());
            }
            return set;
        }

        // Unity animates a Transform's position, rotation (Euler or quaternion curves alike) and scale as one value each: a layer
        // that writes position x owns the whole position, so x and y in two layers overlap.
        internal static EditorCurveBinding Whole(EditorCurveBinding binding)
        {
            if (binding.type != typeof(Transform)) return binding;
            string p = binding.propertyName ?? "";
            string whole = p.StartsWith("m_LocalPosition", StringComparison.Ordinal) ? "position" : p.StartsWith("m_LocalScale", StringComparison.Ordinal) ? "scale" :
                p.StartsWith("m_LocalRotation", StringComparison.Ordinal) || p.StartsWith("localEuler", StringComparison.Ordinal) ? "rotation" : p;
            return EditorCurveBinding.FloatCurve(binding.path, typeof(Transform), whole);
        }
    }
}
