using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Lights on an avatar can carry a cookie texture, and the texture scan walks renderer materials only, so a cookie is
    // never deduplicated: two lights whose cookie files are byte-identical with identical import settings import to the
    // same texture and ship once when both point at one of them. Nothing else about a cookie changes here — it is not
    // cropped, narrowed or resized, because no UV coverage or sampling proof exists for it.
    internal static class CookieMerger
    {
        private static bool IsCookie(Texture2D texture) =>
            AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer &&
            importer.textureType == TextureImporterType.Cookie;

        internal static int Run(GameObject root)
        {
            // Only a texture imported as a Cookie is one Unity accepts back on the light.
            var lights = root.GetComponentsInChildren<Light>(true)
                .Where(light => light && light.cookie is Texture2D cookie && IsCookie(cookie)).ToList();
            if (lights.Count < 2) return 0;
            var keep = new Dictionary<string, Texture2D>();
            var merged = new Dictionary<Texture2D, Texture2D>();
            foreach (var cookie in lights.Select(light => (Texture2D)light.cookie).Distinct())
            {
                string key = MenuIconMerger.Identity(cookie);
                if (key == null) continue;
                if (keep.TryGetValue(key, out var kept)) merged[cookie] = kept;
                else keep[key] = cookie;
            }
            if (merged.Count == 0) return 0;
            foreach (var light in lights)
                if (merged.TryGetValue((Texture2D)light.cookie, out var kept))
                {
                    try { light.cookie = kept; }
                    catch (System.Exception) { /* Unity refused this light's cookie; it keeps its own. */ }
                }
            return merged.Count;
        }
    }
}
