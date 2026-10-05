using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Points every renderer slot (and every material-swap curve in a build-owned clip) at one material when two
    // materials are identical in everything Unity serializes: shader, every property and texture reference,
    // keywords, render queue, override tags, disabled passes and flags. Only the build avatar's renderers and
    // build-owned clips are edited, never a material or any source asset. A material is left alone, as both
    // keeper and duplicate, when any of these hold:
    //  - a material.* curve animates a renderer that uses it (or a swap curve on that renderer lists it),
    //  - the avatar has override controllers, a PlayableDirector or an unreadable material (animation or
    //    serialization cannot be fully known).
    // Anything left unrewritten keeps its own identical material, so skipping a reference never changes the look.
    internal static class DuplicateMaterialMerger
    {
        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.Ordinal)
            { "m_Name", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset" };
        private static readonly HashSet<string> SortedStrings = new HashSet<string>(StringComparer.Ordinal)
            { "m_ValidKeywords", "m_InvalidKeywords", "m_ShaderKeywords", "m_DisabledShaderPasses", "m_StringTagMap" };

        // Returns the number of materials replaced by an identical one.
        internal static int Merge(GameObject root, IEnumerable<RuntimeAnimatorController> controllers,
            Func<Motion, bool> isSpecialMotion, Func<UnityEngine.Object, bool> isBuildOwned)
        {
            var list = controllers.Where(c => c).ToArray();
            if (list.Any(c => !(c is AnimatorController)) ||
                root.GetComponentsInChildren<UnityEngine.Playables.PlayableDirector>(true).Length > 0) return 0;

            var clips = list.Cast<AnimatorController>().SelectMany(AnimationClipDeduplicator.AllClips)
                .Concat(root.GetComponentsInChildren<Animation>(true).SelectMany(a => AnimationUtility.GetAnimationClips(a.gameObject)))
                .Where(clip => clip).Distinct().ToArray();
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && !Exclusions.Excluded(r)).ToArray();
            var excluded = AnimatedMaterials(root, clips, renderers);

            // Renderer-held materials first, so a keeper is one the avatar already uses.
            var candidates = new List<Material>();
            var seen = new HashSet<Material>();
            void Add(UnityEngine.Object value)
            {
                if (value is Material m && m && !excluded.Contains(m) && seen.Add(m)) candidates.Add(m);
            }
            foreach (var renderer in renderers) foreach (var material in renderer.sharedMaterials) Add(material);
            var ownedClips = clips.Where(clip => isBuildOwned(clip) && !isSpecialMotion(clip)).ToArray();
            foreach (var clip in ownedClips)
                foreach (var binding in SwapBindings(clip))
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding)) Add(key.value);

            var keepers = new Dictionary<string, List<(Material Material, byte[] Data)>>(StringComparer.Ordinal);
            var replace = new Dictionary<Material, Material>();
            foreach (var material in candidates)
            {
                byte[] data = Signature(material);
                if (data == null) continue;
                string hash = FingerprintService.Hash(data);
                if (!keepers.TryGetValue(hash, out var bucket)) keepers.Add(hash, bucket = new List<(Material, byte[])>());
                var keeper = bucket.FirstOrDefault(k => k.Data.SequenceEqual(data)).Material;
                if (keeper == null) bucket.Add((material, data));
                else replace.Add(material, keeper);
            }
            if (replace.Count == 0) return 0;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (!materials[i] || !replace.TryGetValue(materials[i], out var keeper)) continue;
                    materials[i] = keeper;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            foreach (var clip in ownedClips)
                foreach (var binding in SwapBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    bool changed = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (!(keys[i].value is Material material) || !replace.TryGetValue(material, out var keeper)) continue;
                        keys[i].value = keeper;
                        changed = true;
                    }
                    if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                }
            return replace.Count;
        }

        private static IEnumerable<EditorCurveBinding> SwapBindings(AnimationClip clip) =>
            AnimationUtility.GetObjectReferenceCurveBindings(clip)
                .Where(b => b.propertyName != null && b.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal));

        // Materials on renderers that a material.* curve animates, plus the materials their swap curves list.
        private static HashSet<Material> AnimatedMaterials(GameObject root, AnimationClip[] clips, Renderer[] renderers)
        {
            var animatedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.propertyName != null && binding.propertyName.StartsWith("material.", StringComparison.OrdinalIgnoreCase))
                        animatedPaths.Add(binding.path ?? "");
            var excluded = new HashSet<Material>();
            if (animatedPaths.Count == 0) return excluded;

            foreach (var renderer in renderers)
                if (animatedPaths.Any(path => PathMatches(root, renderer, path)))
                    excluded.UnionWith(renderer.sharedMaterials.Where(m => m));
            foreach (var clip in clips)
                foreach (var binding in SwapBindings(clip))
                    if (renderers.Any(r => PathMatches(root, r, binding.path ?? "") && animatedPaths.Any(path => PathMatches(root, r, path))))
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            if (key.value is Material material) excluded.Add(material);
            return excluded;
        }

        // Curve paths are relative to the animator that plays them, which may sit below the avatar root, so any
        // renderer whose path ends with the curve path counts. An empty path is the animator's own object.
        private static bool PathMatches(GameObject root, Renderer renderer, string curvePath)
        {
            if (curvePath.Length == 0)
                return renderer.GetComponent<Animator>() != null || renderer.GetComponent<Animation>() != null;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            return path == curvePath || path.EndsWith("/" + curvePath, StringComparison.Ordinal);
        }

        // Null when any part of the material cannot be compared exactly.
        internal static byte[] Signature(Material material)
        {
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    var iterator = new SerializedObject(material).GetIterator();
                    bool enter = true;
                    while (iterator.Next(enter))
                    {
                        enter = true;
                        if (iterator.depth == 0 && Skipped.Contains(iterator.name)) { enter = false; continue; }
                        if (iterator.depth == 0 && iterator.name == "m_SavedProperties") { WriteSavedProperties(writer, iterator); enter = false; continue; }
                        if (iterator.depth == 0 && SortedStrings.Contains(iterator.name)) { WriteSortedStrings(writer, iterator); enter = false; continue; }
                        if (iterator.propertyType == SerializedPropertyType.Generic) continue;
                        writer.Write(iterator.propertyPath);
                        WriteLeaf(writer, iterator);
                    }
                    writer.Flush();
                    return stream.ToArray();
                }
            }
            catch (NotSupportedException) { return null; }
        }

        // Entries are keyed by property name and written in name order, so serialization order cannot differ.
        private static void WriteSavedProperties(BinaryWriter writer, SerializedProperty saved)
        {
            foreach (string kind in new[] { "m_TexEnvs", "m_Ints", "m_Floats", "m_Colors" })
            {
                var array = saved.FindPropertyRelative(kind);
                writer.Write(kind);
                if (array == null) continue;
                var entries = new List<(string Key, byte[] Value)>();
                for (int i = 0; i < array.arraySize; i++)
                {
                    var element = array.GetArrayElementAtIndex(i);
                    using (var stream = new MemoryStream())
                    using (var inner = new BinaryWriter(stream))
                    {
                        WriteTree(inner, element.FindPropertyRelative("second"));
                        inner.Flush();
                        entries.Add((element.FindPropertyRelative("first").stringValue, stream.ToArray()));
                    }
                }
                foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    writer.Write(entry.Key); writer.Write(entry.Value.Length); writer.Write(entry.Value);
                }
            }
        }

        private static void WriteSortedStrings(BinaryWriter writer, SerializedProperty property)
        {
            writer.Write(property.name);
            var values = new List<string>();
            if (property.propertyType == SerializedPropertyType.String) values.Add(property.stringValue);
            else
                for (int i = 0; i < property.arraySize; i++)
                {
                    var element = property.GetArrayElementAtIndex(i);
                    values.Add(element.propertyType == SerializedPropertyType.String
                        ? element.stringValue
                        : element.FindPropertyRelative("first").stringValue + "=" + element.FindPropertyRelative("second").stringValue);
                }
            values.Sort(StringComparer.Ordinal);
            writer.Write(values.Count);
            foreach (string value in values) writer.Write(value ?? "");
        }

        private static void WriteTree(BinaryWriter writer, SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.Generic) { WriteLeaf(writer, property); return; }
            var iterator = property.Copy();
            var end = property.GetEndProperty();
            bool enter = true;
            while (iterator.Next(enter) && !SerializedProperty.EqualContents(iterator, end))
            {
                enter = true;
                if (iterator.propertyType == SerializedPropertyType.Generic) continue;
                writer.Write(iterator.name);
                WriteLeaf(writer, iterator);
            }
        }

        // Full precision for every numeric type; an unknown type makes the whole material ineligible.
        private static void WriteLeaf(BinaryWriter writer, SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.LayerMask: case SerializedPropertyType.Enum:
                case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character:
                    writer.Write(p.longValue); break;
                case SerializedPropertyType.Boolean: writer.Write(p.boolValue); break;
                case SerializedPropertyType.Float: writer.Write(p.doubleValue); break;
                case SerializedPropertyType.String: writer.Write(p.stringValue ?? ""); break;
                case SerializedPropertyType.Color:
                    var color = p.colorValue; writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a); break;
                case SerializedPropertyType.ObjectReference:
                    // Same object, not merely an equal one. Instance IDs are stable for the build.
                    writer.Write(p.objectReferenceValue ? p.objectReferenceValue.GetInstanceID() : 0); break;
                case SerializedPropertyType.Vector2: var v2 = p.vector2Value; writer.Write(v2.x); writer.Write(v2.y); break;
                case SerializedPropertyType.Vector3: var v3 = p.vector3Value; writer.Write(v3.x); writer.Write(v3.y); writer.Write(v3.z); break;
                case SerializedPropertyType.Vector4:
                    var v4 = p.vector4Value; writer.Write(v4.x); writer.Write(v4.y); writer.Write(v4.z); writer.Write(v4.w); break;
                case SerializedPropertyType.Rect:
                    var r = p.rectValue; writer.Write(r.x); writer.Write(r.y); writer.Write(r.width); writer.Write(r.height); break;
                default:
                    throw new NotSupportedException("Material property type " + p.propertyType + " cannot be compared exactly.");
            }
        }
    }
}
