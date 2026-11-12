using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // VRChat's material-contact receivers (contact components that drive material properties) name the renderers they
    // drive, which used to put those renderers beyond merging entirely. This models them so the skinned mesh merger can
    // still merge: a renderer's contact set is the set of logical contacts aimed at it, and two renderers may merge only
    // when their sets match. Receivers on the same object with identical serialized data are one logical contact
    // covering the union of their target lists (as VRChat's own contact chunking across several components implies),
    // and a merge rewrites every aimed receiver's list onto the host and dedupes the duplicates that mapping makes.
    // The SDK ships as assemblies, so behaviour beyond the two structural property names below is never hard-coded:
    // each receiver's whole serialized data (minus its targets and its self/prefab plumbing) is the fingerprint that
    // decides whether two receivers are one contact. A receiver whose data cannot be read, and a material receiver in
    // the Arclight Exclude list (whose references this pass must not rewrite), keeps its renderers apart; unreadable
    // data fails closed, back to the old blocking behaviour.
    internal sealed class MaterialContacts
    {
        internal const string ReceiverTypeName = "VRCContactReceiver";
        // Structural property names; if the SDK renames either, every receiver reads as unreadable and its renderers
        // keep today's no-merge behaviour.
        private const string WriteFlag = "writeToMaterials";
        private const string TargetArray = "shaderTargetRenderers";

        private sealed class Receiver
        {
            internal Component ReceiverComponent;
            internal bool Readable;     // both structural properties found and the whole serialized data read
            internal bool IsMaterial;   // writeToMaterials; otherwise the renderer reference cannot drive anything
            internal string Identity = ""; // logical-contact identity, unique per receiver when not usable
        }

        private readonly Dictionary<Component, Receiver> receivers = new Dictionary<Component, Receiver>();
        private readonly Dictionary<Renderer, string> keys = new Dictionary<Renderer, string>();
        private readonly Dictionary<Renderer, List<Receiver>> aimedAt = new Dictionary<Renderer, List<Receiver>>();

        internal static MaterialContacts Build(GameObject root)
        {
            var contacts = new MaterialContacts();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component.GetType().Name != ReceiverTypeName) continue;
                var receiver = new Receiver { ReceiverComponent = component };
                using (var serialized = new SerializedObject(component))
                {
                    var flag = serialized.FindProperty(WriteFlag);
                    var array = serialized.FindProperty(TargetArray);
                    receiver.Readable = flag != null && flag.propertyType == SerializedPropertyType.Boolean && array != null && array.isArray;
                    if (receiver.Readable)
                    {
                        receiver.IsMaterial = flag.boolValue;
                        string fingerprint = Fingerprint(serialized, component);
                        receiver.Readable = fingerprint != null;
                        if (receiver.Readable && receiver.IsMaterial)
                        {
                            bool excluded = Exclusions.Excluded(component);
                            // Equal data on one object means one contact; excluded receivers must not have their
                            // references rewritten, so each poisons its renderers with a per-receiver identity.
                            receiver.Identity = !excluded
                                ? AnimationUtility.CalculateTransformPath(component.transform, root.transform) + "#" + fingerprint
                                : "excluded:" + component.GetInstanceID();
                        }
                    }
                }
                contacts.receivers[component] = receiver;
                if (!receiver.Readable || !receiver.IsMaterial) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var array = serialized.FindProperty(TargetArray);
                    for (int i = 0; i < array.arraySize; i++)
                    {
                        // Null entries (a deleted target) just drop out, as VRChat itself ignores them.
                        if (!(array.GetArrayElementAtIndex(i).objectReferenceValue is Renderer renderer)) continue;
                        if (!contacts.aimedAt.TryGetValue(renderer, out var list)) contacts.aimedAt.Add(renderer, list = new List<Receiver>());
                        list.Add(receiver);
                    }
                }
            }
            // Key each renderer by its sorted contact identities; identical sets merge together.
            foreach (var pair in contacts.aimedAt)
            {
                var sb = new StringBuilder();
                foreach (var identity in pair.Value.Select(r => r.Identity).Distinct().OrderBy(s => s, StringComparer.Ordinal))
                    sb.Append(sb.Length == 0 ? identity : ";" + identity);
                contacts.keys[pair.Key] = sb.ToString();
            }
            return contacts;
        }

        // The merge-key part for a renderer's contact set; "" when nothing is aimed at it.
        internal string KeyFor(Renderer renderer) => renderer && keys.TryGetValue(renderer, out var key) ? key : "";

        // Why a component referencing a renderer stops it merging; null when the reference is allowed through.
        internal string Blocking(Component component)
        {
            if (!receivers.TryGetValue(component, out var receiver)) return "another component references the renderer";
            if (!receiver.Readable) return "could not read its material contact components";
            if (!receiver.IsMaterial) return null; // Inert: it names the renderer but nothing can drive it.
            if (Exclusions.Excluded(receiver.ReceiverComponent)) return "its material contact sits in the Arclight Exclude list";
            return null;
        }

        // A merge replaced these renderers by the host in every aimed receiver's target list; the host keeps its slot
        // and duplicate results of the mapping are removed. Only arrays this model read are touched; per component the
        // list only shrinks, so the SDK's per-receiver renderer cap is never exceeded. The count of receivers rewritten.
        internal int Retarget(IReadOnlyList<SkinnedMeshRenderer> members, SkinnedMeshRenderer host)
        {
            var touched = members.Where(m => m).Distinct()
                .SelectMany(m => aimedAt.TryGetValue(m, out var list) ? list : Enumerable.Empty<Receiver>())
                .Select(r => r.ReceiverComponent).Distinct().ToList();
            int repacked = 0;
            // A failure part-way through puts every receiver list already rewritten back, so a failed merge catch
            // never leaves contacts aimed at the host while the meshes were restored.
            var applied = new List<(Component Component, List<Object> Original)>();
            try
            {
                foreach (var component in touched)
                    using (var serialized = new SerializedObject(component))
                    {
                        var array = serialized.FindProperty(TargetArray);
                        if (array == null) continue;
                        var original = Enumerable.Range(0, array.arraySize).Select(i => array.GetArrayElementAtIndex(i).objectReferenceValue).ToList();
                        var kept = new List<Object>();
                        var seen = new HashSet<Object>();
                        foreach (var value in original)
                        {
                            if (!value) { kept.Add(null); continue; }
                            var mapped = members.Any(m => m == value) ? (Object)host : value;
                            if (mapped && !seen.Add(mapped)) continue; // The member slot joined the host's; one entry does it.
                            kept.Add(mapped);
                        }
                        if (kept.Count == original.Count && kept.Zip(original, (a, b) => a == b).All(same => same)) continue;
                        for (int i = original.Count - 1; i >= kept.Count; i--) array.DeleteArrayElementAtIndex(i);
                        for (int i = 0; i < kept.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = kept[i];
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        applied.Add((component, original));
                        repacked++;
                    }
            }
            catch
            {
                foreach (var entry in applied)
                    using (var serialized = new SerializedObject(entry.Component))
                    {
                        var array = serialized.FindProperty(TargetArray);
                        if (array == null) continue;
                        for (int i = array.arraySize - 1; i >= 0; i--) array.DeleteArrayElementAtIndex(i);
                        for (int i = 0; i < entry.Original.Count; i++)
                        {
                            array.InsertArrayElementAtIndex(i);
                            array.GetArrayElementAtIndex(i).objectReferenceValue = entry.Original[i];
                        }
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                throw;
            }
            return repacked;
        }

        // Every serialized value of the component, in the order Unity walks them, as one string; null when a property
        // of a kind this cannot read appears. Skips the renderer targets (the contact set carries them), the
        // component's own object links, its cosmetic Unity fields and prefab plumbing, none of which decide contact
    // behaviour.
        private static string Fingerprint(SerializedObject serialized, Component owner)
        {
            var sb = new StringBuilder();
            var it = serialized.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                string path = it.propertyPath;
                int dot = path.IndexOf('.');
                string head = dot < 0 ? path : path.Substring(0, dot);
                if (head == "m_GameObject" || head == "m_ObjectHideFlags" || head == "m_EditorHideFlags" || head == "m_Name" ||
                    head == "m_EditorClassIdentifier" || head == TargetArray || head.StartsWith("m_CorrespondingSourceObject", StringComparison.Ordinal) ||
                    head.StartsWith("m_Prefab", StringComparison.Ordinal)) { enter = false; continue; }
                enter = true;
                switch (it.propertyType)
                {
                    case SerializedPropertyType.Generic:
                    case SerializedPropertyType.ArraySize: break; // Headers; their values follow as children.
                    case SerializedPropertyType.Integer: Append(sb, path, "i" + it.longValue); break;
                    case SerializedPropertyType.Boolean: Append(sb, path, it.boolValue ? "1" : "0"); break;
                    case SerializedPropertyType.Float: Append(sb, path, "f" + it.doubleValue.ToString("R")); break;
                    case SerializedPropertyType.String: Append(sb, path, "s" + it.stringValue.Length + ":" + it.stringValue); break;
                    case SerializedPropertyType.Enum:
                        Append(sb, path, "e" + (it.enumValueIndex >= 0 && it.enumNames != null && it.enumValueIndex < it.enumNames.Length
                            ? it.enumNames[it.enumValueIndex] : "#" + it.enumValueIndex));
                        break;
                    case SerializedPropertyType.LayerMask: Append(sb, path, "m" + it.intValue); break;
                    case SerializedPropertyType.ObjectReference:
                        var value = it.objectReferenceValue;
                        if (value == owner || value is GameObject gameObject && gameObject == owner.gameObject) break;
                        Append(sb, path, "r" + (value ? value.GetType().Name + "#" + value.GetInstanceID() : "0"));
                        break;
                    case SerializedPropertyType.Vector2: Append(sb, path, "v" + V(it.vector2Value.x, it.vector2Value.y)); break;
                    case SerializedPropertyType.Vector3: Append(sb, path, "v" + V(it.vector3Value.x, it.vector3Value.y, it.vector3Value.z)); break;
                    case SerializedPropertyType.Vector4: Append(sb, path, "v" + V(it.vector4Value.x, it.vector4Value.y, it.vector4Value.z, it.vector4Value.w)); break;
                    case SerializedPropertyType.Quaternion: var q = it.quaternionValue; Append(sb, path, "v" + V(q.x, q.y, q.z, q.w)); break;
                    case SerializedPropertyType.Vector2Int: Append(sb, path, "v" + it.vector2IntValue.x + "," + it.vector2IntValue.y); break;
                    case SerializedPropertyType.Vector3Int: Append(sb, path, "v" + it.vector3IntValue.x + "," + it.vector3IntValue.y + "," + it.vector3IntValue.z); break;
                    case SerializedPropertyType.Rect: Append(sb, path, "q" + V(it.rectValue.x, it.rectValue.y, it.rectValue.width, it.rectValue.height)); break;
                    case SerializedPropertyType.RectInt: Append(sb, path, "q" + it.rectIntValue.x + "," + it.rectIntValue.y + "," + it.rectIntValue.width + "," + it.rectIntValue.height); break;
                    case SerializedPropertyType.Bounds:
                        Append(sb, path, "b" + V(it.boundsValue.center.x, it.boundsValue.center.y, it.boundsValue.center.z) +
                            "/" + V(it.boundsValue.extents.x, it.boundsValue.extents.y, it.boundsValue.extents.z));
                        break;
                    case SerializedPropertyType.BoundsInt:
                        Append(sb, path, "b" + it.boundsIntValue.center.x + "," + it.boundsIntValue.center.y + "," + it.boundsIntValue.center.z +
                            "/" + it.boundsIntValue.size.x + "," + it.boundsIntValue.size.y + "," + it.boundsIntValue.size.z);
                        break;
                    case SerializedPropertyType.Color: Append(sb, path, "c" + V(it.colorValue.r, it.colorValue.g, it.colorValue.b, it.colorValue.a)); break;
                    default: return null; // A kind this cannot read: fail closed, keep the renderers apart.
                }
            }
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string path, string token) => sb.Append(path).Append('=').Append(token).Append(';');
        private static string V(params float[] values) => string.Join(",", values.Select(v => v.ToString("R")));
    }
}
