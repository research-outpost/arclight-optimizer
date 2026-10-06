using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Removes animator parameters whose name nothing else on the avatar mentions: no transition
    // condition, blend tree, state speed/time/offset/mirror parameter, state machine behaviour (parameter drivers,
    // layer controls and any other behaviour, read field by field), animation curve, expressions menu control, or
    // component field (contacts, PhysBones by prefix, and anything else). Such a parameter can only hold a value that
    // nothing reads. Expression parameters all stay: OSC apps outside the avatar can read and write them, and VRChat syncs
    // the PC and Android uploads by their list position. Nothing changes when any controller or menu cannot be read.
    internal static class ParameterCleaner
    {
        internal sealed class Result { public int Animator; public List<string> Names = new List<string>(); }

        internal static Result Run(GameObject root, IEnumerable<VirtualAnimatorController> controllers, AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var list = controllers.Where(c => c != null).ToList();
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return result;

            var used = AnimatorReads(root, list, analysis);
            if (used == null) return result;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component == descriptor || component is Transform) continue;
                if (component.GetType().Name == "VRCPhysBone")
                    using (var serialized = new SerializedObject(component))
                    {
                        string prefix = serialized.FindProperty("parameter")?.stringValue;
                        if (!string.IsNullOrEmpty(prefix)) used.Add(prefix + "_*"); // Matched by prefix below.
                    }
                Strings(component, used);
            }
            var prefixes = used.Where(u => u.EndsWith("_*", StringComparison.Ordinal)).Select(u => u.Substring(0, u.Length - 1)).ToList();
            bool Used(string name) => used.Contains(name) || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

            foreach (var controller in list)
            {
                var unused = controller.Parameters.Keys.Where(name => !Used(name)).ToList();
                if (unused.Count == 0) continue;
                controller.Parameters = controller.Parameters.RemoveRange(unused);
                result.Animator += unused.Count;
                result.Names.AddRange(unused);
            }

            result.Names = result.Names.Distinct().ToList();
            return result;
        }

        // Parameter names the animators and the menu read: transition conditions, blend trees, state parameters, behaviour fields
        // (synced layer overrides too), Animator curves and menu controls. Not component fields. Null when a controller or the
        // menu cannot be read.
        internal static HashSet<string> AnimatorReads(GameObject root, IEnumerable<VirtualAnimatorController> controllers, AvatarAnalysis analysis)
        {
            var list = controllers.Where(c => c != null).ToList();
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return null;
            var used = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var node in list.SelectMany(c => c.AllReachableNodes()))
                    switch (node)
                    {
                        case VirtualTransitionBase transition:
                            foreach (var condition in transition.Conditions) used.Add(condition.parameter);
                            break;
                        case VirtualBlendTree tree:
                            used.Add(tree.BlendParameter); used.Add(tree.BlendParameterY);
                            foreach (var child in tree.Children) used.Add(child.DirectBlendParameter);
                            break;
                        case VirtualState state:
                            used.Add(state.SpeedParameter); used.Add(state.TimeParameter); used.Add(state.CycleOffsetParameter); used.Add(state.MirrorParameter);
                            foreach (var behaviour in state.Behaviours) Strings(behaviour, used);
                            break;
                        case VirtualStateMachine machine:
                            foreach (var behaviour in machine.Behaviours) Strings(behaviour, used);
                            break;
                    }
            }
            catch (Exception) { return null; }
            // A synced layer keeps its own behaviours for the states it shares; AllReachableNodes does not visit them.
            foreach (var behaviour in list.SelectMany(c => c.Layers).SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v)))
                Strings(behaviour, used);
            foreach (var binding in analysis.Bindings.Where(b => b.Curve.type == typeof(Animator))) used.Add(binding.Property);

            Object menu;
            using (var serialized = new SerializedObject(descriptor))
            {
                // The descriptor's own strings (visemes, eye bones, collider names) are not parameters; its menu is read
                // separately.
                menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
            }
            if (menu && !MenuStrings(menu, used, new HashSet<Object>())) return null;
            used.Remove(null); // Inactive state parameters.
            return used;
        }

        // VRChat's built-in parameters: VRChat or OSC may read them whatever the animators do, so drivers writing them stay.
        private static readonly HashSet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK",
            "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent", "VRCEmote",
            "VRCFaceBlendH", "VRCFaceBlendV"
        };

        // Removes parameter driver entries that Set, Add or Copy into a parameter nothing reads: no transition, blend tree,
        // state parameter, other behaviour field (a Copy source counts), animation curve, menu control or component field
        // mentions it, it is not an expression parameter (OSC and sync can read those) and not one of VRChat's built-in
        // parameters. Such a write changes nothing anyone can observe. A driver left with no entries is removed, so a toggle
        // layer that only carried it can fold. Random entries stay (they draw from the shared random sequence). Drivers in
        // synced layer overrides are read but not changed. Repeats until stable: a removed Copy entry no longer reads its source.
        internal static int RemoveDeadDrivers(GameObject root, IEnumerable<VirtualAnimatorController> controllers, AvatarAnalysis analysis)
        {
            if (!analysis.Complete) return 0;
            var list = controllers.Where(c => c != null).ToList();
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return 0;
            var keep = new HashSet<string>(BuiltIn, StringComparer.Ordinal);
            Object menu, expressions;
            using (var serialized = new SerializedObject(descriptor))
            {
                menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
                expressions = serialized.FindProperty("expressionParameters")?.objectReferenceValue;
            }
            if (expressions)
                using (var serialized = new SerializedObject(expressions))
                {
                    var parameters = serialized.FindProperty("parameters");
                    if (parameters == null) return 0;
                    for (int i = 0; i < parameters.arraySize; i++) keep.Add(parameters.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue);
                }
            int removed = 0;
            for (int round = 0; round < 8; round++)
            {
                var read = new HashSet<string>(keep, StringComparer.Ordinal);
                var owners = new List<(Func<ImmutableList<StateMachineBehaviour>> Get, Action<ImmutableList<StateMachineBehaviour>> Set)>();
                try
                {
                    foreach (var node in list.SelectMany(c => c.AllReachableNodes()))
                        switch (node)
                        {
                            case VirtualTransitionBase transition:
                                foreach (var condition in transition.Conditions) read.Add(condition.parameter);
                                break;
                            case VirtualBlendTree tree:
                                read.Add(tree.BlendParameter); read.Add(tree.BlendParameterY);
                                foreach (var child in tree.Children) read.Add(child.DirectBlendParameter);
                                break;
                            case VirtualState state:
                                read.Add(state.SpeedParameter); read.Add(state.TimeParameter); read.Add(state.CycleOffsetParameter); read.Add(state.MirrorParameter);
                                foreach (var behaviour in state.Behaviours) Reads(behaviour, read);
                                owners.Add((() => state.Behaviours, b => state.Behaviours = b));
                                break;
                            case VirtualStateMachine machine:
                                foreach (var behaviour in machine.Behaviours) Reads(behaviour, read);
                                owners.Add((() => machine.Behaviours, b => machine.Behaviours = b));
                                break;
                        }
                }
                catch (Exception) { return removed; }
                foreach (var behaviour in list.SelectMany(c => c.Layers).SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v)))
                    Strings(behaviour, read);
                foreach (var binding in analysis.Bindings.Where(b => b.Curve.type == typeof(Animator))) read.Add(binding.Property);
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (!component || component == descriptor || component is Transform) continue;
                    if (component.GetType().Name == "VRCPhysBone")
                        using (var serialized = new SerializedObject(component))
                        {
                            string prefix = serialized.FindProperty("parameter")?.stringValue;
                            if (!string.IsNullOrEmpty(prefix)) read.Add(prefix + "_*");
                        }
                    Strings(component, read);
                }
                if (menu && !MenuStrings(menu, read, new HashSet<Object>())) return removed;
                var prefixes = read.Where(u => u != null && u.EndsWith("_*", StringComparison.Ordinal)).Select(u => u.Substring(0, u.Length - 1)).ToList();
                bool Read(string name) => read.Contains(name) || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

                int before = removed;
                foreach (var (get, set) in owners)
                {
                    var behaviours = get();
                    var kept = behaviours.Where(b => !(IsDriver(b) && Strip(b, Read, ref removed))).ToImmutableList();
                    if (kept.Count != behaviours.Count) set(kept);
                }
                if (removed == before) break;
            }
            return removed;
        }

        private static bool IsDriver(StateMachineBehaviour behaviour) => behaviour && behaviour.GetType().Name == "VRCAvatarParameterDriver";

        // Removes the driver's dead entries; true when nothing is left, so the driver itself can go.
        private static bool Strip(StateMachineBehaviour driver, Func<string, bool> read, ref int removed)
        {
            using (var serialized = new SerializedObject(driver))
            {
                var parameters = serialized.FindProperty("parameters");
                if (parameters == null) return false;
                for (int i = parameters.arraySize - 1; i >= 0; i--)
                {
                    var entry = parameters.GetArrayElementAtIndex(i);
                    var type = entry.FindPropertyRelative("type");
                    string name = entry.FindPropertyRelative("name")?.stringValue;
                    if (type == null || type.enumValueIndex == 2 || string.IsNullOrEmpty(name) || read(name)) continue; // 2: Random.
                    parameters.DeleteArrayElementAtIndex(i);
                    removed++;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return parameters.arraySize == 0;
            }
        }

        // What a behaviour reads: every string field, except the parameters a driver writes (its entries' "name").
        private static void Reads(StateMachineBehaviour behaviour, HashSet<string> into)
        {
            if (!IsDriver(behaviour)) { Strings(behaviour, into); return; }
            using (var serialized = new SerializedObject(behaviour))
            {
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(iterator.stringValue) &&
                        !(iterator.propertyPath.StartsWith("parameters.Array.data[", StringComparison.Ordinal) && iterator.propertyPath.EndsWith("].name", StringComparison.Ordinal)))
                        into.Add(iterator.stringValue);
            }
        }

        // Every string field of the object.
        internal static void Strings(Object target, HashSet<string> into)
        {
            if (!target) return;
            using (var serialized = new SerializedObject(target))
            {
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(iterator.stringValue)) into.Add(iterator.stringValue);
            }
        }

        // Strings of the menu and its submenus; false when a menu cannot be read.
        private static bool MenuStrings(Object menu, HashSet<string> into, HashSet<Object> seen)
        {
            if (!seen.Add(menu)) return true;
            using (var serialized = new SerializedObject(menu))
            {
                var controls = serialized.FindProperty("controls");
                if (controls == null) return false;
                Strings(menu, into);
                for (int i = 0; i < controls.arraySize; i++)
                {
                    var sub = controls.GetArrayElementAtIndex(i).FindPropertyRelative("subMenu")?.objectReferenceValue;
                    if (sub && !MenuStrings(sub, into, seen)) return false;
                }
            }
            return true;
        }
    }
}
