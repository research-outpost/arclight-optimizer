using System;
using System.Collections.Generic;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Silent's Filamented ("Silent/Filamented" and its Roughness, Specular and Cloth setups), audited 2026-11-16 on commit c60c974e
    // (2026-06-23; UnityStandardInput, UnityStandardCore, UnityStandardShadow; meta and deferred are Unity Standard's own passes).
    // TexCoords: texcoord.xy = uv0 * _MainTex_ST, texcoord.zw = uv[_UVSec] * _DetailAlbedoMap_ST (_UVSec 0-3; 2 and 3 only when
    // the mesh has those channels, else uv1). Every texture has its own sampler.
    //  - Main UV: _MainTex, _MetallicGlossMap, _SpecGlossMap, _BumpMap, _OcclusionMap, _EmissionMap, _DetailMask.
    //  - Detail UV: _DetailAlbedoMap, _DetailNormalMap.
    // _PARALLAXMAP offsets the main UV by a height lookup and _NORMALMAP_SHADOW marches the normal and height maps along the light
    // direction, so those materials are retained. Bakery RNM maps and the DFG LUT are lightmap/lookup textures and are retained.
    // Filamented has no releases or version label; this adapter covers the shader names above unaudited beyond that commit.
    // Channels are kept and nothing is cropped.
    internal sealed class FilamentedAdapter : ITextureSamplingAdapter
    {
        private const string Id = "filamented-c60c974e-v1";
        private static readonly string[] Shaders = { "Silent/Filamented", "Silent/Filamented (Roughness setup)", "Silent/Filamented (Specular setup)", "Silent/Filamented (Cloth setup)" };
        private static readonly HashSet<string> MainUv = new HashSet<string>(StringComparer.Ordinal)
            { "_MainTex", "_MetallicGlossMap", "_SpecGlossMap", "_BumpMap", "_OcclusionMap", "_EmissionMap", "_DetailMask" };

        public bool Matches(Material material) => material.shader && Array.IndexOf(Shaders, material.shader.name) >= 0;

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            if (material.IsKeywordEnabled("_PARALLAXMAP")) return Unsupported("Filamented parallax offsets the UVs by a height lookup; original retained.");
            if (material.IsKeywordEnabled("_NORMALMAP_SHADOW")) return Unsupported("Filamented normal-map shadows march the maps along the light direction; original retained.");
            if (MainUv.Contains(property))
                return Result(property, new SamplingPath { UvChannel = 0, Scale = MaterialInputs.Scale(material, "_MainTex"),
                    Offset = MaterialInputs.Offset(material, "_MainTex"), Label = "Filamented uv0 * _MainTex_ST" });
            if (property == "_DetailAlbedoMap" || property == "_DetailNormalMap")
            {
                float sec = material.HasProperty("_UVSec") ? MaterialInputs.Float(material, "_UVSec") : 0;
                var paths = new List<SamplingPath>();
                // _UVSec == 0 -> uv0; == 2 or 3 -> that channel when the mesh has it, else uv1; anything else -> uv1.
                if (sec == 0) paths.Add(Detail(material, 0));
                else
                {
                    paths.Add(Detail(material, 1));
                    if (sec == 2 || sec == 3) paths.Add(Detail(material, (int)sec));
                }
                return Result(property, paths.ToArray());
            }
            return Unsupported(property.StartsWith("_RNM", StringComparison.Ordinal) || property == "_DFG"
                ? "Lightmap or lookup texture; original retained." : "Unverified Filamented texture property; original retained.");
        }

        private static SamplingPath Detail(Material material, int set) => new SamplingPath { UvChannel = set,
            Scale = MaterialInputs.Scale(material, "_DetailAlbedoMap"), Offset = MaterialInputs.Offset(material, "_DetailAlbedoMap"), Label = "Filamented detail uv" + set };

        private static SamplingDescription Result(string property, params SamplingPath[] paths) => new SamplingDescription
        {
            AdapterId = Id, Supported = true, Channels = TextureChannels.All, Paths = new List<SamplingPath>(paths),
            Semantics = property == "_BumpMap" || property == "_DetailNormalMap" ? TextureSemantics.Normal
                : property == "_MainTex" || property == "_EmissionMap" || property == "_DetailAlbedoMap" ? TextureSemantics.Color : TextureSemantics.Data
        };

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
