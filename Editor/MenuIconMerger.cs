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
    // are byte-identical and whose import settings are identical merge, so each imports to the same texture; PNG icons that
    // were saved again (different bytes) also merge when they decode to the same pixels, alpha kind and colour metadata
    // with identical import settings and imported size, the rule duplicate textures on materials merge under. The menu tree
    // is copied for the build first (the source menus are never edited); nothing changes when it cannot be read.
    // Every icon also gets an optimized build copy when one measures smaller (MenuIconOptimizer: mips dropped, an opaque
    // icon narrowed, and maxIconSize > 0 capping the resolution; 0 keeps the authored size). An icon whose file bytes and
    // import settings match a material texture already on the avatar points at that texture instead of carrying its own copy.
    internal static class MenuIconMerger
    {
        // What the pass did, so the report says which of the three things happened to the menu's icons.
        internal sealed class Result { public int Total, Merged, Optimized, Capped; }

        internal static Result Run(GameObject root, int maxIconSize = 0)
        {
            var result = new Result();
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return result;
            Object menu;
            using (var serialized = new SerializedObject(descriptor)) menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
            if (!menu) return result;

            var menus = new List<Object>();
            if (!Collect(menu, menus)) return result;
            var icons = menus.SelectMany(Icons).Select(p => p.Texture).Distinct().ToList();
            var keeper = new Dictionary<Texture2D, Texture2D>();
            // The same art often ships once as a material texture and again as a menu icon. Both are plain Texture2D
            // references, so an icon whose file bytes and import settings match a texture the materials already use points
            // at that texture and its own copy is not uploaded.
            var materialTextures = root.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials).Where(m => m)
                .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).OfType<Texture2D>()
                .Where(t => t && !icons.Contains(t)).Distinct().ToList();
            var onMaterials = new Dictionary<string, Texture2D>();
            foreach (var texture in materialTextures)
            {
                string key = Identity(texture);
                if (key != null && !onMaterials.ContainsKey(key)) onMaterials[key] = texture;
            }
            foreach (var icon in icons)
            {
                string key = Identity(icon);
                if (key != null && onMaterials.TryGetValue(key, out var shared)) keeper[icon] = shared;
            }
            // Re-saved art merges across the two domains too: same import settings and imported size, then the same decoded
            // pixels. Candidates are grouped by that key first, so the PNG decode is bounded by the few that can match.
            var candidates = icons.Where(i => !keeper.ContainsKey(i)).Concat(materialTextures).ToList();
            foreach (var group in candidates.GroupBy(DecodedKey).Where(g => g.Key != null && g.Count() > 1))
            {
                var materialSide = group.Where(t => !icons.Contains(t)).ToList();
                if (materialSide.Count == 0) continue;
                foreach (var icon in group.Where(t => icons.Contains(t)).ToList())
                    foreach (var texture in materialSide)
                    {
                        if (!TextureUsageScanner.SameDecodedPng(AssetDatabase.GetAssetPath(texture), AssetDatabase.GetAssetPath(icon))) continue;
                        keeper[icon] = texture;
                        break;
                    }
            }
            foreach (var group in icons.Where(i => !keeper.ContainsKey(i)).GroupBy(Identity).Where(g => g.Key != null && g.Count() > 1))
                foreach (var duplicate in group.Skip(1)) keeper[duplicate] = group.First();
            // Re-encoded copies: among the icons still apart, same import settings and imported size, then the same decoded image.
            foreach (var group in icons.Where(i => !keeper.ContainsKey(i)).GroupBy(DecodedKey).Where(g => g.Key != null && g.Count() > 1))
            {
                var kept = new List<Texture2D>();
                foreach (var icon in group)
                {
                    var same = kept.FirstOrDefault(k => TextureUsageScanner.SameDecodedPng(AssetDatabase.GetAssetPath(k), AssetDatabase.GetAssetPath(icon)));
                    if (same) keeper[icon] = same; else kept.Add(icon);
                }
            }
            result.Merged = keeper.Count;
            // A build copy measures smaller even without a cap: mips are never sampled by the menu, and an opaque icon
            // narrows from DXT5 to DXT1. Duplicates already point at a kept icon; both the duplicate and the kept icon move
            // to the copy.
            var resized = new Dictionary<Texture2D, Texture2D>();
            foreach (var icon in icons)
            {
                var copy = MenuIconOptimizer.Resize(icon, maxIconSize);
                if (!copy) continue;
                resized[icon] = copy;
                result.Optimized++;
                if (maxIconSize > 0 && copy.width < icon.width) result.Capped++;
            }
            if (resized.Count > 0)
            {
                foreach (var pair in keeper.ToList())
                    if (resized.TryGetValue(pair.Value, out var copy)) keeper[pair.Key] = copy;
                // A duplicate already points at the kept icon's copy; its own copy must not overwrite that, or the two
                // identical icons would ship as two uploads again.
                foreach (var pair in resized)
                    if (!keeper.ContainsKey(pair.Key)) keeper[pair.Key] = pair.Value;
                AudioMonoConverter.FlushUsage(); // One save for the whole pass, as the mono pass does.
            }
            if (keeper.Count == 0) return result;

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
            result.Total = keeper.Count;
            return result;
        }

        // Points every menu texture reference in `map` at its replacement, in this build's own menu copies. A menu that
        // is still an authored asset is never edited; the cap pass calls this so a texture it capped keeps one upload even
        // when a menu control also names it (MenuIconMerger may have pointed an icon at a material's texture earlier).
        internal static int Repoint(GameObject root, Dictionary<Texture2D, Texture2D> map)
        {
            if (map.Count == 0) return 0;
            var descriptor = root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor");
            if (!descriptor) return 0;
            Object menu;
            using (var serialized = new SerializedObject(descriptor)) menu = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
            if (!menu) return 0;
            var menus = new List<Object>();
            if (!Collect(menu, menus)) return 0;
            menu = EnsureCopies(descriptor, menu, menus);
            menus.Clear();
            if (!Collect(menu, menus)) return 0;
            int changed = 0;
            foreach (var one in menus.Where(one => one))
                using (var serialized = new SerializedObject(one))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                            iterator.objectReferenceValue is Texture2D icon && map.TryGetValue(icon, out var replacement))
                        {
                            iterator.objectReferenceValue = replacement;
                            changed++;
                        }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            return changed;
        }

        // The build's own copies of the menu tree. An authored menu (or one another plugin saved as an asset) is copied
        // first, so Arclight never edits a menu asset; a tree the build already owns is used as it is.
        private static Object EnsureCopies(Component descriptor, Object menu, List<Object> menus)
        {
            if (menus.All(one => one && !AssetDatabase.Contains(one))) return menu;
            var copies = menus.Where(one => one).ToDictionary(one => one, one =>
            {
                var copy = Object.Instantiate(one);
                copy.name = one.name;
                return copy;
            });
            foreach (var copy in copies.Values)
                using (var serialized = new SerializedObject(copy))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Object value &&
                            value && copies.TryGetValue(value, out var sub))
                            iterator.objectReferenceValue = sub;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            using (var serialized = new SerializedObject(descriptor))
            {
                serialized.FindProperty("expressionsMenu").objectReferenceValue = copies[menu];
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return copies[menu];
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

        // What re-encoded copies of one PNG icon share before their pixels are compared: every import setting and the imported
        // size and format. Null for anything that is not a PNG texture file of its own.
        private static string DecodedKey(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsMainAsset(texture) ||
                !(AssetImporter.GetAtPath(path) is TextureImporter importer) || !File.Exists(LongPath.For(path))) return null;
            return texture.width + "|" + texture.height + "|" + texture.format + "|" + texture.mipmapCount + "|" + TextureSafety.SettingsFingerprint(importer);
        }

        // The file's bytes and every import setting; null for anything that is not a texture file of its own. Shared with
        // CookieMerger, which merges identical cookies the same way.
        internal static string Identity(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(texture) || !(AssetImporter.GetAtPath(path) is TextureImporter importer) || !File.Exists(LongPath.For(path)))
                return null;
            return FingerprintService.FileHash(path) + "|" + Path.GetExtension(path).ToLowerInvariant() + "|" + TextureSafety.SettingsFingerprint(importer);
        }
    }
}
