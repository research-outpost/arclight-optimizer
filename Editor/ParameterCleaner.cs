using System;
using System.Collections.Generic;
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
            catch (Exception) { return result; }
            // A synced layer keeps its own behaviours for the states it shares; AllReachableNodes does not visit them.
            foreach (var behaviour in list.SelectMany(c => c.Layers).SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v)))
                Strings(behaviour, used);
            foreach (var binding in analysis.Bindings.Where(b => b.Curve.type == typeof(Animator))) used.Add(binding.Property);
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

            Object menu;
            using (var serialized = new SerializedObject(descriptor))
            {
                // The descriptor's own strings (visemes, eye bones, collider names) are not parameters; its menu is read
                // separately.
                menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
            }
            if (menu && !MenuStrings(menu, used, new HashSet<Object>())) return result;
            used.Remove(null); // Inactive state parameters.
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
