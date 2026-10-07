using System;
using System.Collections.Generic;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Mochie Standard, Standard Lite and Standard Mobile (StandardInput.cginc). Every texture is read through
    // _DefaultSampler's sampler state. InitializeUVs builds each UV as
    // ScaleOffsetRotateScrollUV(uv[set], ST, rotation, scroll) = rotate(uv) * ST.xy + ST.zw + frac(time * scroll):
    //  - uv0.xy (_UVMain*, _MainTex_ST): base colour, normal, metallic, roughness, occlusion, packed and emission maps.
    //  - _DetailMask, _EmissionMask and _AlphaMask use their own set, rotation, scroll and ST.
    // Only mesh UV sets with no rotation or scroll are modeled. Stochastic, triplanar, supersampled and parallax
    // sampling move or widen lookups, so those materials are retained.
    //  - uv0.zw (_UVDetail*, _DetailMainTex_ST) through _DefaultDetailSampler: the detail maps (SampleDetailTexture). Standard
    //    2.13's shadow pass also reads the detail packed map at uv0.xy (2.14.1 reads uv0.zw), so that map unions both.
    // Audited on Standard 2.13 and 2.14.1 (Mochie Unity Shaders 1.77.1); the shader names do not tell them apart, so every rule
    // holds for both.
    internal sealed class MochieStandardAdapter : ITextureSamplingAdapter
    {
        private const string Id = "mochie-standard-v1";
        private static readonly string[] Shaders = { "Mochie/Standard", "Mochie/Standard Lite", "Mochie/Standard Mobile" };
        private static readonly string[] ModeKeywords = { "_STOCHASTIC_ON", "_TRIPLANAR_ON", "_SUPERSAMPLING_ON", "_PARALLAX_ON" };
        private static readonly string[] DetailModeKeywords = { "_STOCHASTIC_DETAIL_ON", "_TRIPLANAR_DETAIL_ON", "_SUPERSAMPLING_DETAIL_ON" };
        private static readonly Dictionary<string, TextureSemantics> Primary = new Dictionary<string, TextureSemantics>(StringComparer.Ordinal)
        {
            { "_MainTex", TextureSemantics.Color }, { "_EmissionMap", TextureSemantics.Color }, { "_NormalMap", TextureSemantics.Normal },
            { "_MetallicMap", TextureSemantics.Data }, { "_RoughnessMap", TextureSemantics.Data },
            { "_OcclusionMap", TextureSemantics.Data }, { "_PackedMap", TextureSemantics.Data },
            // 2.14.1 specular workflow (SampleSpecularMap); 2.13 has no such property.
            { "_SpecGlossMap", TextureSemantics.Data }
        };
        private static readonly Dictionary<string, TextureSemantics> Details = new Dictionary<string, TextureSemantics>(StringComparer.Ordinal)
        {
            { "_DetailMainTex", TextureSemantics.Color }, { "_DetailNormalMap", TextureSemantics.Normal }, { "_DetailMetallicMap", TextureSemantics.Data },
            { "_DetailRoughnessMap", TextureSemantics.Data }, { "_DetailOcclusionMap", TextureSemantics.Data }, { "_DetailPackedMap", TextureSemantics.Data }
        };

        // StandardInput.cginc (2.13 and 2.14.1): metallic .g in the surface and .r in the shadow pass (a float taking a float4);
        // roughness and occlusion .g; emission reaches only .rgb (lighting and meta); the packed map is read at the metallic,
        // roughness and occlusion channels (its height channel only with parallax, which is retained).
        private static TextureChannels PrimaryChannels(Material material, string property)
        {
            switch (property)
            {
                case "_MetallicMap": return TextureChannels.R | TextureChannels.G;
                case "_RoughnessMap": case "_OcclusionMap": return TextureChannels.G;
                case "_EmissionMap": return TextureChannels.RGB;
                case "_PackedMap": return Channel(material, "_MetallicChannel") | Channel(material, "_RoughnessChannel") | Channel(material, "_OcclusionChannel");
                case "_MainTex": return MainReadsAlpha(material) ? TextureChannels.All : TextureChannels.RGB;
                default: return TextureChannels.All;
            }
        }

        // IS_TRANSPARENT (StandardDefines.cginc) gates every main alpha read: the surface, shadow and picking passes. With
        // _AlphaSource 1 (alpha mask) or 2 (vertex colour) the alpha is replaced before use, except in Standard Mobile.
        private static bool MainReadsAlpha(Material material)
        {
            if (ShaderAdapterRegistry.FallbackReadsAlpha(material)) return true;
            if (!material.IsKeywordEnabled("_ALPHATEST_ON") && !material.IsKeywordEnabled("_ALPHABLEND_ON") && !material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return false;
            float source = material.HasProperty("_AlphaSource") ? MaterialInputs.Float(material, "_AlphaSource") : 0;
            return material.shader.name == "Mochie/Standard Mobile" || (source != 1 && source != 2);
        }

        // Detail maps: base colour RGB, scalar maps R (the float4 is truncated); alpha only as BlendColorsAlpha /
        // BlendScalarsAlpha's "Alpha" blend weight (blend type 1, an int cast of the float). The packed map is read at
        // the three selected channels in both the surface and the shadow pass.
        private static TextureChannels DetailChannels(Material material, string property)
        {
            switch (property)
            {
                case "_DetailMainTex": return TextureChannels.RGB | BlendAlpha(material, "_DetailMainTexBlend");
                case "_DetailMetallicMap": return TextureChannels.R | BlendAlpha(material, "_DetailMetallicBlend");
                case "_DetailRoughnessMap": return TextureChannels.R | BlendAlpha(material, "_DetailRoughnessBlend");
                case "_DetailOcclusionMap": return TextureChannels.R | BlendAlpha(material, "_DetailOcclusionBlend");
                case "_DetailPackedMap": return Channel(material, "_DetailMetallicChannel") | Channel(material, "_DetailRoughnessChannel") | Channel(material, "_DetailOcclusionChannel");
                default: return TextureChannels.All;
            }
        }

        private static TextureChannels BlendAlpha(Material material, string blend)
        {
            if (!material.HasProperty(blend)) return TextureChannels.A;
            float value = MaterialInputs.Float(material, blend);
            return value == Mathf.Round(value) && value != 1 ? 0 : TextureChannels.A;
        }
        // Mask texture -> UV property prefix and channel selector.
        private static readonly Dictionary<string, (string uv, string channel)> Masks = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            { "_DetailMask", ("_UVDetailMask", "_DetailMaskChannel") },
            { "_EmissionMask", ("_UVEmissionMask", "_EmissionMaskChannel") },
            { "_AlphaMask", ("_UVAlphaMask", "_AlphaMaskChannel") }
        };

        public bool Matches(Material material) => material.shader && Array.IndexOf(Shaders, material.shader.name) >= 0;

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            foreach (string keyword in ModeKeywords)
                if (material.IsKeywordEnabled(keyword))
                    return Unsupported(keyword + " moves or widens texture lookups; original retained.");

            if (Primary.TryGetValue(property, out var semantics))
                return Path(material, semantics, PrimaryChannels(material, property), "_UVMain", "_MainTex");
            if (Masks.TryGetValue(property, out var mask))
                return Path(material, TextureSemantics.Data, Channel(material, mask.channel), mask.uv, property);
            if (Details.TryGetValue(property, out var detail))
            {
                foreach (string keyword in DetailModeKeywords)
                    if (material.IsKeywordEnabled(keyword))
                        return Unsupported(keyword + " moves or widens detail lookups; original retained.");
                var detailPath = Path(material, detail, DetailChannels(material, property), "_UVDetail", "_DetailMainTex", "_DefaultDetailSampler");
                if (property != "_DetailPackedMap" || !detailPath.Supported) return detailPath;
                var shadowPath = Path(material, detail, detailPath.Channels, "_UVMain", "_MainTex", "_DefaultDetailSampler");
                if (!shadowPath.Supported) return shadowPath;
                detailPath.Paths.AddRange(shadowPath.Paths);
                return detailPath;
            }
            return Unsupported("Unverified Mochie Standard texture property; original retained.");
        }

        private static SamplingDescription Path(Material material, TextureSemantics semantics, TextureChannels channels, string uv, string stProperty,
            string samplerProperty = "_DefaultSampler")
        {
            float set = MaterialInputs.Float(material, uv + "Set");
            if (set != Mathf.Round(set) || set < 0 || set > 4) return Unsupported(uv + "Set is not a mesh UV set; original retained.");
            if (MaterialInputs.Float(material, uv + "Rotation") != 0) return Unsupported(uv + "Rotation rotates the UVs; original retained.");
            var scroll = MaterialInputs.Vector(material, uv + "Scroll");
            if (scroll.x != 0 || scroll.y != 0) return Unsupported(uv + "Scroll moves sampling over time; original retained.");
            var sampler = material.HasProperty(samplerProperty) ? material.GetTexture(samplerProperty) : null;
            return new SamplingDescription { AdapterId = Id, Supported = true, Semantics = semantics, Channels = channels,
                Paths = new List<SamplingPath> { new SamplingPath { UvChannel = (int)set, Scale = MaterialInputs.Scale(material, stProperty),
                    Offset = MaterialInputs.Offset(material, stProperty), SamplerTexture = sampler, FixedRepeat = !sampler,
                    Label = "Mochie " + uv + " UV" } } };
        }

        private static TextureChannels Channel(Material material, string property)
        {
            float value = MaterialInputs.Float(material, property);
            return value == 0 ? TextureChannels.R : value == 1 ? TextureChannels.G : value == 2 ? TextureChannels.B
                : value == 3 ? TextureChannels.A : TextureChannels.All;
        }

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
