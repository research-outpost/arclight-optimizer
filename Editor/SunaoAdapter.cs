using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Sunao Shader 1.6.x (audited: 1.6.2), every entry except Fur. All passes (forward, outline, shadow caster) build
    // their UV as uv0 * _MainTex_ST; with UV animation and scrolling off, MainUV and SubUV are both that UV:
    //  - _MainTex, _MetallicGlossMap (own samplers) and _SubTex, _AlphaMask, _OcclusionMap, _ShadeMask, _LightMask,
    //    _RimLitMask, _MatCapMask, _OutLineTexture (the main texture's sampler) read it directly.
    //  - _OutLineMask is read at it in the outline and shadow vertex stages (tex2Dlod, own sampler).
    //  - _BumpMap reads MixingTransformTex(SubUV, _MainTex_ST, _BumpMap_ST): the main transform applied twice, then its own.
    //  - _EmissionMap and _EmissionMap2 read MixingTransformTex(uv0, _MainTex_ST, own ST), own samplers; emission
    //    animation and scrolling must be off.
    // Decals, parallax emission and matcaps move sampling off the mesh UVs and are retained.
    internal sealed class SunaoAdapter : ITextureSamplingAdapter
    {
        private const string Id = "sunao-1.6-static-v1";

        public bool Matches(Material material) => material.shader &&
            material.shader.name.StartsWith("Sunao Shader/", StringComparison.Ordinal);

        public SamplingDescription Describe(Material material, string property)
        {
            if (material.shader.name == "Sunao Shader/Fur" || material.shader.name.EndsWith("/Fur", StringComparison.Ordinal))
                return Unsupported("Sunao Fur shells are not modeled; original retained.");
            if (!material.HasProperty("_VersionH") || Value(material, "_VersionH") != 1 || Value(material, "_VersionM") != 6)
                return Unsupported("Only Sunao Shader 1.6.x is supported; original retained.");
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            if (Value(material, "_UVAnimation") > 0 || Value(material, "_UVScrollX") != 0 || Value(material, "_UVScrollY") != 0)
                return Unsupported("Sunao UV animation or scrolling moves sampling over time; original retained.");

            Vector2 mainScale = MaterialInputs.Scale(material, "_MainTex"), mainOffset = MaterialInputs.Offset(material, "_MainTex");
            switch (property)
            {
                case "_MainTex": return Path(material, TextureSemantics.Color, mainScale, mainOffset, false, TextureChannels.All);
                case "_SubTex": return Path(material, TextureSemantics.Color, mainScale, mainOffset, true, TextureChannels.All);
                case "_OutLineTexture": return Path(material, TextureSemantics.Color, mainScale, mainOffset, true, TextureChannels.All);
                case "_MetallicGlossMap": return Path(material, TextureSemantics.Data, mainScale, mainOffset, false, TextureChannels.All);
                case "_MatCapMask": return Path(material, TextureSemantics.Data, mainScale, mainOffset, true, TextureChannels.All);
                case "_AlphaMask": case "_OcclusionMap": case "_ShadeMask": case "_LightMask": case "_RimLitMask":
                    return Path(material, TextureSemantics.Data, mainScale, mainOffset, true, TextureChannels.RGB);
                case "_OutLineMask": return Path(material, TextureSemantics.Data, mainScale, mainOffset, false, TextureChannels.RGB);
                case "_BumpMap":
                {
                    Vector2 scale = MaterialInputs.Scale(material, property), offset = MaterialInputs.Offset(material, property);
                    // (uv * S + O) * S * B + O + Bo
                    return Path(material, TextureSemantics.Normal, Vector2.Scale(Vector2.Scale(mainScale, mainScale), scale),
                        Vector2.Scale(Vector2.Scale(mainOffset, mainScale), scale) + mainOffset + offset, false, TextureChannels.All);
                }
                case "_EmissionMap": case "_EmissionMap2":
                {
                    if (property == "_EmissionMap" && (Value(material, "_EmissionAnimation") > 0 ||
                        Value(material, "_EmissionScrX") != 0 || Value(material, "_EmissionScrY") != 0))
                        return Unsupported("Sunao emission animation or scrolling moves sampling over time; original retained.");
                    Vector2 scale = MaterialInputs.Scale(material, property), offset = MaterialInputs.Offset(material, property);
                    return Path(material, TextureSemantics.Color, Vector2.Scale(mainScale, scale), mainOffset + offset, false, TextureChannels.All);
                }
            }
            return Unsupported(property.IndexOf("MatCap", StringComparison.Ordinal) >= 0
                ? "Matcaps are sampled by view-space normals; original retained."
                : property.StartsWith("_Parallax", StringComparison.Ordinal) ? "Parallax emission is view-dependent; original retained."
                : property == "_DecalTex" ? "Decals use their own placed, rotated and mirrored coordinates; original retained."
                : "Unverified Sunao texture property; original retained.");
        }

        private static float Value(Material material, string property) =>
            material.HasProperty(property) ? MaterialInputs.Float(material, property) : 0;

        // borrowsMain: read through _MainTex's sampler; with no main texture assigned that is the default white (Repeat).
        private static SamplingDescription Path(Material material, TextureSemantics semantics, Vector2 scale, Vector2 offset, bool borrowsMain, TextureChannels channels)
        {
            var main = borrowsMain ? material.GetTexture("_MainTex") : null;
            return new SamplingDescription
            {
                AdapterId = Id, Supported = true, Semantics = semantics, Channels = channels,
                Paths = new List<SamplingPath> { new SamplingPath { UvChannel = 0, Scale = scale, Offset = offset,
                    SamplerTexture = main, FixedRepeat = borrowsMain && !main, Label = "Sunao uv0 * _MainTex_ST" } }
            };
        }

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
