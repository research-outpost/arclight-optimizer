using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Small PhysBone and contact cleanups, each with no effect on what anyone sees or what any animator reads:
    //  - A PhysBone's parameter is cleared when no animator or expression parameter is named after it (the prefix
    //    itself or prefix_ followed by anything: _IsGrabbed, _IsPosed, _Angle, _Stretch, _Squish) and no other
    //    component names it. The values it would write reach nothing: OSC and the animator only see parameters the
    //    animator declares. With no parameter, the unobserved-PhysBone rules can then apply.
    //  - A contact receiver goes when its parameter is empty or named by no animator or expression parameter and no
    //    other component, and nothing references the receiver. A receiver does nothing but write that parameter.
    //    Senders stay: other avatars read them.
    //  - Empty entries, and entries repeating the one directly before, leave PhysBone collider lists. Back to back, the
    //    second push out of the same shape finds nothing to correct.
    //  - Is Animated is turned off when nothing but the PhysBone itself moves any transform of its chain (root
    //    included): no animation, humanoid mapping, constraint, physics body, Head Chop or other PhysBone. The pose
    //    it would blend with never changes.
    // Nothing changes when the avatar's animation cannot be read. Constraints are left alone: what Unity and VRChat
    // constraints do with no weighted source is not proven here.
    internal static class PhysicsCleaner
    {
        internal sealed class Result { public int Parameters, Receivers, Colliders, NotAnimated, MergedColliders; }

        internal static Result Run(AvatarAnalysis analysis, IEnumerable<string> animatorParameters, IEnumerable<string> expressionParameters)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var declared = new HashSet<string>(animatorParameters.Concat(expressionParameters).Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            var components = root.GetComponentsInChildren<Component>(true).Where(c => c && !(c is Transform)).ToList();
            var strings = components.ToDictionary(c => c, c => { var set = new HashSet<string>(StringComparer.Ordinal); ParameterCleaner.Strings(c, set); return set; });
            bool NamedElsewhere(Component self, Func<string, bool> matches) =>
                declared.Any(matches) || components.Any(c => c != self && strings[c].Any(matches));

            result.MergedColliders = MergeIdenticalColliders(components, analysis);
            if (result.MergedColliders > 0) components = components.Where(c => c).ToList();

            foreach (var physBone in components.Where(c => c.GetType().Name == "VRCPhysBone" && !Exclusions.Excluded(c)))
                using (var serialized = new SerializedObject(physBone))
                {
                    var parameter = serialized.FindProperty("parameter");
                    if (!string.IsNullOrEmpty(parameter?.stringValue))
                    {
                        string prefix = parameter.stringValue;
                        if (!NamedElsewhere(physBone, n => n == prefix || n.StartsWith(prefix + "_", StringComparison.Ordinal)))
                        {
                            parameter.stringValue = "";
                            result.Parameters++;
                        }
                    }

                    var colliders = serialized.FindProperty("colliders");
                    if (colliders != null && colliders.isArray)
                    {
                        // Only a repeat of the entry directly before goes: with another collider between them, a later
                        // push out of the first can matter again if collisions are resolved in list order.
                        Object previous = null;
                        for (int i = 0; i < colliders.arraySize; i++)
                        {
                            var entry = colliders.GetArrayElementAtIndex(i);
                            if (entry.propertyType != SerializedPropertyType.ObjectReference) break;
                            if (entry.objectReferenceValue && entry.objectReferenceValue != previous) { previous = entry.objectReferenceValue; continue; }
                            entry.objectReferenceValue = null; // Unity keeps the element when a set reference is cleared once.
                            colliders.DeleteArrayElementAtIndex(i--);
                            result.Colliders++;
                        }
                    }

                    var isAnimated = serialized.FindProperty("isAnimated");
                    if (isAnimated != null && isAnimated.boolValue && !analysis.IsAnimated(physBone, p => p == "isAnimated") &&
                        !UnusedObjectRemover.PhysBoneRoot(physBone).GetComponentsInChildren<Transform>(true).Any(t => analysis.MovesLocallyBesides(t, physBone)) &&
                        !AncestorScaleChanges(physBone, root.transform, analysis))
                    {
                        isAnimated.boolValue = false;
                        result.NotAnimated++;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

            foreach (var receiver in components.Where(c => c.GetType().Name == "VRCContactReceiver" && !Exclusions.Excluded(c)))
            {
                string name;
                using (var serialized = new SerializedObject(receiver)) name = serialized.FindProperty("parameter")?.stringValue;
                if (name == null || analysis.ReferencesTo(receiver).Any(c => c != receiver) || analysis.IsAnimated(receiver)) continue;
                if (name.Length > 0 && NamedElsewhere(receiver, n => n == name)) continue;
                Object.DestroyImmediate(receiver);
                result.Receivers++;
            }
            if (result.Receivers + result.Parameters + result.MergedColliders > 0) analysis.RescanReferences();
            return result;
        }

        // Two PhysBone colliders with the same settings on the same effective root (shape, size, offset, inside/bounds flags and
        // enabled state, compared field by field) collide identically, so every PhysBone list entry for the second can name the
        // first and the second goes. Only colliders that no animation touches, that nothing but PhysBones references, whose
        // objects are active and can never be switched off (a collider collides only while its object is active), and that no
        // PhysBone under Arclight Exclude lists (its list stays as it is).
        private static int MergeIdenticalColliders(List<Component> components, AvatarAnalysis analysis)
        {
            var physBones = components.Where(c => c.GetType().Name == "VRCPhysBone").ToList();
            var listedByExcluded = new HashSet<Object>(physBones.Where(Exclusions.Excluded).SelectMany(analysis.ReferencesFrom));
            var colliders = components.Where(c => c.GetType().Name == "VRCPhysBoneCollider" && !Exclusions.Excluded(c) && !analysis.IsAnimated(c) &&
                c.gameObject.activeInHierarchy && !analysis.ActivenessAnimated(c.gameObject) && !listedByExcluded.Contains(c) &&
                analysis.ReferencesTo(c).All(r => r == c || r.GetType().Name == "VRCPhysBone")).ToList();
            var replace = new Dictionary<Object, Component>();
            foreach (var group in colliders.GroupBy(Signature).Where(g => g.Count() > 1))
                foreach (var duplicate in group.Skip(1)) replace[duplicate] = group.First();
            if (replace.Count == 0) return 0;
            foreach (var physBone in physBones.Where(p => !Exclusions.Excluded(p)))
                using (var serialized = new SerializedObject(physBone))
                {
                    var list = serialized.FindProperty("colliders");
                    for (int i = 0; list != null && list.isArray && i < list.arraySize; i++)
                    {
                        var entry = list.GetArrayElementAtIndex(i);
                        if (entry.propertyType == SerializedPropertyType.ObjectReference && entry.objectReferenceValue && replace.TryGetValue(entry.objectReferenceValue, out var kept))
                            entry.objectReferenceValue = kept;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            foreach (var duplicate in replace.Keys) Object.DestroyImmediate(duplicate);
            return replace.Count;
        }

        // Every serialized value of a collider, with its root resolved (an empty Root Transform means its own object).
        internal static string Signature(Component collider)
        {
            var text = new System.Text.StringBuilder();
            using (var serialized = new SerializedObject(collider))
            {
                var rootProperty = serialized.FindProperty("rootTransform");
                var effectiveRoot = rootProperty?.objectReferenceValue is Transform t && t ? t : collider.transform;
                text.Append(collider.GetType().FullName).Append('|').Append(effectiveRoot.GetInstanceID());
                var it = serialized.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = true;
                    string path = it.propertyPath;
                    if (path == "m_GameObject" || path == "rootTransform" || path == "m_ObjectHideFlags" || path == "m_CorrespondingSourceObject" ||
                        path == "m_PrefabInstance" || path == "m_PrefabAsset") { enter = false; continue; }
                    if (it.hasChildren) continue;
                    text.Append('|').Append(path).Append('=');
                    switch (it.propertyType)
                    {
                        case SerializedPropertyType.Float: text.Append(it.doubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                        case SerializedPropertyType.Integer: case SerializedPropertyType.LayerMask: text.Append(it.longValue); break;
                        case SerializedPropertyType.Boolean: text.Append(it.boolValue); break;
                        case SerializedPropertyType.Enum: text.Append(it.enumValueIndex); break;
                        case SerializedPropertyType.String: text.Append(it.stringValue.Length).Append(':').Append(it.stringValue); break;
                        case SerializedPropertyType.ObjectReference: text.Append(it.objectReferenceInstanceIDValue); break;
                        case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character: text.Append(it.intValue); break;
                        case SerializedPropertyType.Color: { var c = it.colorValue; text.Append(c.r.ToString("R")).Append(',').Append(c.g.ToString("R")).Append(',').Append(c.b.ToString("R")).Append(',').Append(c.a.ToString("R")); break; }
                        default: return Guid.NewGuid().ToString(); // A field this comparison does not read: never equal.
                    }
                }
            }
            return text.ToString();
        }

        // VRChat freezes some PhysBone values when it is enabled at scale 0 and the scale later grows (the bug Avatar
        // Optimizer guards against); Is Animated stays on when any ancestor's scale can change: scale animation, a scale or
        // parent constraint, or Head Chop.
        // Checked from both the chain's root and the PhysBone's own object (they differ when Root Transform points elsewhere).
        private static bool AncestorScaleChanges(Component physBone, Transform root, AvatarAnalysis analysis)
        {
            // Scale and parent constraints change the scale of their target: their own object, or a VRC constraint's Target Transform.
            var scaled = new HashSet<Transform>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c is ScaleConstraint || c is ParentConstraint) scaled.Add(c.transform);
                else if (c && (c.GetType().Name == "VRCScaleConstraint" || c.GetType().Name == "VRCParentConstraint"))
                    using (var serialized = new SerializedObject(c))
                        scaled.Add(serialized.FindProperty("TargetTransform")?.objectReferenceValue as Transform ?? c.transform);
            }
            foreach (var start in new[] { UnusedObjectRemover.PhysBoneRoot(physBone), physBone.transform })
                for (var t = start; t; t = t == root ? null : t.parent)
                    if (scaled.Contains(t) || analysis.ScaleChanges(t) ||
                        analysis.IsAnimated(t, p => p.IndexOf("Scale", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }

        // Names in the avatar's expression parameters asset.
        internal static IEnumerable<string> ExpressionParameters(GameObject root)
        {
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) yield break;
            Object parameters;
            using (var serialized = new SerializedObject(descriptor)) parameters = serialized.FindProperty("expressionParameters")?.objectReferenceValue;
            if (!parameters) yield break;
            using (var serialized = new SerializedObject(parameters))
            {
                var array = serialized.FindProperty("parameters");
                for (int i = 0; array != null && i < array.arraySize; i++)
                    yield return array.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue;
            }
        }
    }
}
