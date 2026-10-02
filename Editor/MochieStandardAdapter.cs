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
    // sampling move or widen lookups, so those materials are retained. Detail maps are retained: the shadow pass
    // reads the detail packed map at the primary UV.
    internal sealed class MochieStandardAdapter : ITextureSamplingAdapter
    {
        private const string Id = "mochie-standard-v1";
        private static readonly string[] Shaders = { "Mochie/Standard", "Mochie/Standard Lite", "Mochie/Standard Mobile" };
        private static readonly string[] ModeKeywords = { "_STOCHASTIC_ON", "_TRIPLANAR_ON", "_SUPERSAMPLING_ON", "_PARALLAX_ON" };
        private static readonly Dictionary<string, TextureSemantics> Primary = new Dictionary<string, TextureSemantics>(StringComparer.Ordinal)
        {
            { "_MainTex", TextureSemantics.Color }, { "_EmissionMap", TextureSemantics.Color }, { "_NormalMap", TextureSemantics.Normal },
            { "_MetallicMap", TextureSemantics.Data }, { "_RoughnessMap", TextureSemantics.Data },
            { "_OcclusionMap", TextureSemantics.Data }, { "_PackedMap", TextureSemantics.Data }
        };
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
                return Path(material, semantics, TextureChannels.All, "_UVMain", "_MainTex");
            if (Masks.TryGetValue(property, out var mask))
                return Path(material, TextureSemantics.Data, Channel(material, mask.channel), mask.uv, property);
            return Unsupported(property.StartsWith("_Detail", StringComparison.Ordinal)
                ? "Mochie detail maps are also read at the primary UV by the shadow pass; original retained."
                : "Unverified Mochie Standard texture property; original retained.");
        }

        private static SamplingDescription Path(Material material, TextureSemantics semantics, TextureChannels channels, string uv, string stProperty)
        {
            float set = MaterialInputs.Float(material, uv + "Set");
            if (set != Mathf.Round(set) || set < 0 || set > 4) return Unsupported(uv + "Set is not a mesh UV set; original retained.");
            if (MaterialInputs.Float(material, uv + "Rotation") != 0) return Unsupported(uv + "Rotation rotates the UVs; original retained.");
            var scroll = MaterialInputs.Vector(material, uv + "Scroll");
            if (scroll.x != 0 || scroll.y != 0) return Unsupported(uv + "Scroll moves sampling over time; original retained.");
            var sampler = material.GetTexture("_DefaultSampler");
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
