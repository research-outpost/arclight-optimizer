using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Expression menu icons the texture scan never sees (they are not on materials). Outfits often carry the same icon file
    // under another name; every control then points at one of them and the copies are not uploaded. Only icons whose files
    // are byte-identical and whose import settings are identical merge, so each imports to the same texture. The menu tree
    // is copied for the build first (the source menus are never edited); nothing changes when it cannot be read.
    internal static class MenuIconMerger
    {
        internal static int Run(GameObject root)
        {
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return 0;
            Object menu;
            using (var serialized = new SerializedObject(descriptor)) menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
            if (!menu) return 0;

            var menus = new List<Object>();
            if (!Collect(menu, menus)) return 0;
            var icons = menus.SelectMany(Icons).Select(p => p.Texture).Distinct().ToList();
            var keeper = new Dictionary<Texture2D, Texture2D>();
            foreach (var group in icons.GroupBy(Key).Where(g => g.Key != null && g.Count() > 1))
                foreach (var duplicate in group.Skip(1)) keeper[duplicate] = group.First();
            if (keeper.Count == 0) return 0;

            // Copies of every menu, with submenus pointing at the copies.
            var copies = menus.ToDictionary(m => m, m => { var copy = Object.Instantiate(m); copy.name = m.name; return copy; });
            foreach (var copy in copies.Values)
                using (var serialized = new SerializedObject(copy))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Object value && value)
                        {
                            if (copies.TryGetValue(value, out var sub)) iterator.objectReferenceValue = sub;
                            else if (value is Texture2D icon && keeper.TryGetValue(icon, out var kept)) iterator.objectReferenceValue = kept;
                        }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            using (var serialized = new SerializedObject(descriptor))
            {
                serialized.FindProperty("expressionsMenu").objectReferenceValue = copies[menu];
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return keeper.Count;
        }

        private static bool Collect(Object menu, List<Object> into)
        {
            if (into.Contains(menu)) return true;
            into.Add(menu);
            using (var serialized = new SerializedObject(menu))
            {
                var controls = serialized.FindProperty("controls");
                if (controls == null) return false;
                for (int i = 0; i < controls.arraySize; i++)
                {
                    var sub = controls.GetArrayElementAtIndex(i).FindPropertyRelative("subMenu")?.objectReferenceValue;
                    if (sub && !Collect(sub, into)) return false;
                }
            }
            return true;
        }

        private static IEnumerable<(string Path, Texture2D Texture)> Icons(Object menu)
        {
            var found = new List<(string, Texture2D)>();
            using (var serialized = new SerializedObject(menu))
            {
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Texture2D icon && icon)
                        found.Add((iterator.propertyPath, icon));
            }
            return found;
        }

        // The file's bytes and every import setting; null for anything that is not a texture file of its own.
        private static string Key(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(texture) || !(AssetImporter.GetAtPath(path) is TextureImporter importer) || !File.Exists(LongPath.For(path)))
                return null;
            return FingerprintService.FileHash(path) + "|" + Path.GetExtension(path).ToLowerInvariant() + "|" + TextureSafety.SettingsFingerprint(importer);
        }
    }
}
