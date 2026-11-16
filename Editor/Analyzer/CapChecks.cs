using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor.Analyzer
{
    // The opt-in Texture Size Cap: textures the avatar's materials use that are large enough for the cap to be worth
    // turning on. This is an offer, not a warning — nothing is wrong with a big texture, and Arclight never resizes a
    // source file, so the card only points at a setting that would make build copies smaller.
    internal static partial class AvatarAnalyzer
    {
        private const int CapCandidate = 2048, CapTarget = 1024;

        private static void CapChecks(Avatar avatar, List<Finding> findings)
        {
            var component = avatar.Root.GetComponent<AvatarTextureOptimizer>();
            if (!component || !component.enabled || component.textureSizeCap != TextureSizeCap.Off) return;
            // Textures the user already excluded from the cap are not candidates: the card points at the setting, and a
            // texture it would be told to keep is one the setting no longer touches.
            var excluded = new HashSet<Texture2D>(component.textureSizeCapExclude.Where(t => t));
            var large = avatar.Root.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) && !EditorOnly(r.transform, avatar.Root.transform))
                .SelectMany(r => r.sharedMaterials).Where(m => m)
                .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture))
                .OfType<Texture2D>().Where(t => t).Distinct()
                .Where(t => !excluded.Contains(t) && Mathf.Max(t.width, t.height) >= CapCandidate)
                .Select(t => (Texture: t, Size: Mathf.Max(t.width, t.height), Bytes: FileLength(t)))
                .OrderByDescending(t => t.Size).ThenByDescending(t => t.Bytes).ToList();
            if (large.Count == 0) return;
            long bytes = large.Sum(t => t.Bytes);
            double factor = 1.0 - (double)CapTarget * CapTarget / ((double)large[0].Size * large[0].Size);
            findings.Add(new Finding
            {
                Severity = Severity.TidyUp, Key = "texcap|", Target = large[0].Texture,
                Title = N(large.Count, "texture") + " " + (large.Count == 1 ? "is" : "are") + " " + CapCandidate + " px or larger",
                Detail = N(large.Count, "texture") + " at " + CapCandidate + " px or more hold most of this avatar's texture bytes. " +
                    "The Texture Size Cap setting writes a build copy of each at " + CapTarget + " px, about a quarter of the size (less for bigger ones), " +
                    "used only if smaller. They look softer when displayed large. " +
                    "Source files: " + Bytes(bytes) + ".\n" + Bullets(large.Select(t => t.Texture.name + " (" + t.Size + " px, " + Bytes(t.Bytes) + ")")),
                Fix = "Set Texture Size Cap to " + CapTarget + " px (PC only) on the Arclight Optimizer component. Check the avatar close up afterwards; Exclude Textures From Cap keeps single textures at full size, and Exclude Objects keeps whole objects.",
                Identity = string.Join("\n", large.Select(t => t.Texture.name + "|" + t.Size)),
                Data = new Fixes.CapTextures { Component = component }
            });
        }

        private static long FileLength(Texture2D texture)
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(texture);
            try { return string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length; } catch (Exception) { return 0; }
        }

        private static string Bytes(long bytes) =>
            bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("F1") + " MiB" : (bytes / 1024.0).ToString("F0") + " KiB";
    }
}
