using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Texture memory that only the user can remove, stated as facts with sizes; Arclight changes nothing here. Sizes are the
    // built avatar's imported textures for the active platform.
    internal static class CostHints
    {
        private static readonly MethodInfo StorageSize = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.TextureUtil")?
            .GetMethod("GetStorageMemorySizeLong", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        internal static long Bytes(Texture texture) =>
            StorageSize != null ? (long)StorageSize.Invoke(null, new object[] { texture }) : UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);

        internal static List<string> Collect(GameObject root)
        {
            var hints = new List<string>();
            var materials = root.GetComponentsInChildren<Renderer>(true).Where(r => !Exclusions.Excluded(r))
                .SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToList();
            // From the saved entries: a material whose shader is missing declares no properties, but its textures still ship.
            IEnumerable<Texture> TexturesOf(Material m)
            {
                var found = new List<Texture>();
                using (var serialized = new SerializedObject(m))
                {
                    var saved = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                    for (int i = 0; saved != null && i < saved.arraySize; i++)
                        if (saved.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture").objectReferenceValue is Texture t && t) found.Add(t);
                }
                return found.Distinct();
            }

            // A material whose shader is missing renders pink in game, yet its textures still ship.
            foreach (var material in materials.Where(m => !m.shader || m.shader.name == "Hidden/InternalErrorShader"))
            {
                var textures = TexturesOf(material).ToList();
                if (textures.Count == 0) continue;
                hints.Add("Material " + material.name + " has no shader installed, so it renders pink in game; its " + textures.Count +
                    " texture(s) still take " + EditorUtility.FormatBytes(textures.Sum(Bytes)) + ". Install its shader or remove the material.");
            }

            // High Quality (BC7) on a texture with no alpha: Normal Quality stores it as DXT1, half the size, with a small quality loss.
            var bc7 = materials.SelectMany(TexturesOf).Distinct().OfType<Texture2D>()
                .Where(t => !AssetDatabase.GetAssetPath(t).StartsWith(AutomaticTextureOptimizer.CacheFolder + "/", StringComparison.Ordinal)) // Arclight's own copies follow their source.
                .Where(t => t.graphicsFormat == GraphicsFormat.RGBA_BC7_SRGB || t.graphicsFormat == GraphicsFormat.RGBA_BC7_UNorm)
                .Where(t => AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) is TextureImporter importer && importer.textureType != TextureImporterType.NormalMap && !importer.DoesSourceTextureHaveAlpha())
                .OrderByDescending(Bytes).ToList();
            if (bc7.Count > 0)
                hints.Add(bc7.Count + " texture(s) without alpha use High Quality compression (BC7, " + EditorUtility.FormatBytes(bc7.Sum(Bytes)) +
                    "). Normal Quality would store them in half the memory, with a small loss of quality: " +
                    string.Join(", ", bc7.Take(8).Select(t => t.name)) + (bc7.Count > 8 ? ", ..." : "") + ".");
            return hints;
        }
    }
}
