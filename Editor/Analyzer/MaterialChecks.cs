using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Transparent materials set up so they render darker than they should in some worlds. Judged on the avatar as authored (the
    // build only clones materials). Both read the blend values lilToon, Poiyomi and most toon shaders save as _SrcBlend/_DstBlend.
    //  - A blended material in the opaque range (render queue 2500 or lower): Unity draws its shadow pass into the camera depth
    //    texture, which a transparent shader dithers by alpha, so screen-space shadows, SSAO and world AO read noisy depth there.
    //  - A lilToon overlay with Max light blending (_BlendOpFA 4) over an opaque material of the same texture on the same
    //    renderer (blush, face or hair overlays): Max keeps only the brighter of the overlay's point or spot light and what is
    //    under it, so where the overlay covers the lit surface that light is lost, a darker patch in the overlay's shape.
    internal static partial class AvatarAnalyzer
    {
        private const int OpaqueQueueEnd = 2500, BlendOpMax = 4;

        private static bool Blended(Material m) => m && m.HasProperty("_DstBlend") && m.GetFloat("_DstBlend") != 0;

        private static void MaterialChecks(Avatar avatar, List<Finding> findings)
        {
            var renderers = avatar.Root.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) && !EditorOnly(r.transform, avatar.Root.transform)).ToList();

            var opaqueQueue = new List<string>();
            Material firstQueue = null;
            foreach (var material in renderers.SelectMany(r => r.sharedMaterials).Where(Blended).Distinct())
            {
                if (material.renderQueue > OpaqueQueueEnd) continue;
                opaqueQueue.Add(material.name + " (queue " + material.renderQueue + ")");
                firstQueue = firstQueue ?? material;
            }
            if (opaqueQueue.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "transqueue|", Target = firstQueue,
                    Title = N(opaqueQueue.Count, "transparent material") + " " + (opaqueQueue.Count == 1 ? "sits" : "sit") + " in the opaque render queue",
                    Detail = "Unity treats render queue 2500 and lower as opaque, so these blended materials are drawn into the camera depth texture as a speckled pattern. In worlds with realtime shadows or ambient occlusion that pattern shows as a darker veil over the see-through parts:\n" + Bullets(opaqueQueue),
                    Fix = "Set the render queue to 2501 or higher (3000 is the usual transparent queue), keeping their order relative to each other.",
                    Identity = string.Join("\n", opaqueQueue)
                });

            var maxBlend = new List<string>();
            Material firstMax = null;
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                foreach (var overlay in materials.Where(m => Blended(m) && m.HasProperty("_BlendOpFA") && (int)m.GetFloat("_BlendOpFA") == BlendOpMax).Distinct())
                {
                    var texture = overlay.HasProperty("_MainTex") ? overlay.GetTexture("_MainTex") : null;
                    var under = materials.FirstOrDefault(m => m && m != overlay && !Blended(m) && texture && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") == texture);
                    if (!under) continue;
                    string line = overlay.name + " over " + under.name + " on " + PathOf(avatar, renderer.gameObject);
                    if (maxBlend.Contains(line)) continue;
                    maxBlend.Add(line);
                    firstMax = firstMax ?? overlay;
                }
            }
            if (maxBlend.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "maxblend|", Target = firstMax,
                    Title = N(maxBlend.Count, "overlay material") + " may darken under point and spot lights",
                    Detail = "These transparent overlays use Max light blending and cover an opaque material with the same texture. Max keeps only the brighter of the overlay's point or spot light and what is under it, so where the overlay covers the lit surface that light is lost, and a darker patch in the overlay's shape appears in worlds with realtime point or spot lights:\n" + Bullets(maxBlend),
                    Fix = "In the overlay's lilToon Rendering settings, set Forward Add → Light Blending to Add (BlendOp RGB and Alpha), and keep Alpha Boost at 1.",
                    Identity = string.Join("\n", maxBlend)
                });
        }
    }
}
