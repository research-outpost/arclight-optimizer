using System;
using System.Collections.Generic;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // ORL Toon v1 and v2 (sh.orels.shaders 7.3, audited against the generator's module sources). Every variant
    // (Main, Cutout, Transparent, Transparent PrePass, LTCGI, UV Discard) builds from the same modules.
    //  - GLOBAL_uv = uv[_MainTexUVSet] * _MainTex_ST (UV1-UV4). _MainTex (own sampler), _AlphaTex and _EmissionMap
    //    read it through the main sampler.
    //  - "Synced With Albedo" maps (occlusion, specular, v1 metallic/reflectivity, the main normal map) read
    //    GLOBAL_uv * tiling; "Independent" reads uv0 * tiling.
    //  - v2 detail normals and their mask, the decal and matcap masks and the reflection mask read their own UV set
    //    and ST; v2 metallic reads uv0 * its ST. Outline texture and width mask read raw uv0.
    //  - Bicubic normal/alpha sampling reaches two texels, well inside the smallest protected padding.
    // Only opaque entries (no NEED_ALBEDO_ALPHA) ignore the main texture's alpha. Matcaps, decals, ramps, the DFG LUT
    // and AudioLink maps are retained.
    internal sealed class OrelsToonAdapter : ITextureSamplingAdapter
    {
        private const string Id = "orels-toon-7.3-v1";
        private static readonly HashSet<string> V1 = new HashSet<string>(StringComparer.Ordinal)
            { "Main", "Cutout", "Transparent", "Transparent PrePass", "LTCGI", "LTCGI Cutout", "UV Discard" };
        private static readonly HashSet<string> V2 = new HashSet<string>(StringComparer.Ordinal)
            { "Main", "Main LTCGI", "Cutout", "Cutout LTCGI", "Transparent", "Transparent PrePass" };
        private static readonly HashSet<string> Opaque = new HashSet<string>(StringComparer.Ordinal)
            { "orels1/Toon/Main", "orels1/Toon/LTCGI", "orels1/Toon/UV Discard", "orels1/Toon/v2/Main", "orels1/Toon/v2/Main LTCGI" };

        public bool Matches(Material material) => material.shader && Version(material.shader.name) > 0;

        private static int Version(string name) =>
            name.StartsWith("orels1/Toon/v2/", StringComparison.Ordinal) ? (V2.Contains(name.Substring(15)) ? 2 : 0)
            : name.StartsWith("orels1/Toon/", StringComparison.Ordinal) && V1.Contains(name.Substring(12)) ? 1 : 0;

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            bool v2 = Version(material.shader.name) == 2;
            int mainSet = Index(material, "_MainTexUVSet");
            if (mainSet < 0 || mainSet > 3) return Unsupported("Main texture UV set is outside UV1-UV4; original retained.");
            Vector2 mainScale = MaterialInputs.Scale(material, "_MainTex"), mainOffset = MaterialInputs.Offset(material, "_MainTex");
            var global = new SamplingPath { UvChannel = mainSet, Scale = mainScale, Offset = mainOffset, Label = "ORL GLOBAL_uv" };

            switch (property)
            {
                case "_MainTex":
                    return Result(TextureSemantics.Color, Opaque.Contains(material.shader.name) ? TextureChannels.RGB : TextureChannels.All,
                        global, null);
                case "_AlphaTex": return Result(TextureSemantics.Data, TextureChannels.R, global, Main(material));
                case "_EmissionMap":
                    return Result(TextureSemantics.Color, v2 ? Selected(material, "_EmissionMapChannel") : TextureChannels.RGB,
                        global, Main(material));
                case "_OcclusionMap": case "_OcclusionDetail":
                    return Tiled(material, property == "_OcclusionMap" ? "_Occlusion" : "_OcclusionDetail", global, TextureSemantics.Data,
                        v2 ? Channel(material, property == "_OcclusionMap" ? "_OcclusionChannel" : "_OcclusionDetailChannel") : TextureChannels.R,
                        Main(material));
                case "_SpecularMap":
                    return Tiled(material, "_Specular", global, TextureSemantics.Data, v2 ? TextureChannels.All : TextureChannels.RGB, Main(material));
                case "_SpecularMask":
                    return Tiled(material, "_SpecularMask", global, TextureSemantics.Data,
                        v2 ? Channel(material, "_SpecularMaskChannel") : TextureChannels.R, Main(material));
                case "_BumpMap": return Tiled(material, "_BumpMap", global, TextureSemantics.Normal, TextureChannels.All, null);
                case "_OutlineMask": return Result(TextureSemantics.Data, TextureChannels.R, Raw(), null);
                case "_OutlineTex": return Result(TextureSemantics.Color, TextureChannels.RGB, Raw(), v2 ? Main(material) : null);
            }
            if (!v2)
            {
                switch (property)
                {
                    case "_MetallicGlossMap":
                        return Tiled(material, "_MetallicGlossMap", global, TextureSemantics.Data, TextureChannels.R | TextureChannels.A, Main(material));
                    case "_ReflectivityMask":
                        return Tiled(material, "_ReflectivityMask", global, TextureSemantics.Data, TextureChannels.R, Main(material));
                    case "_DetailNormalMap": case "_DetailNormalMask":
                    {
                        bool mask = property == "_DetailNormalMask";
                        int set = Index(material, mask ? "_DetailNormalMaskUVSet" : "_DetailNormalsUVSet");
                        if (set < 0 || set > 3) return Unsupported("Detail UV set is outside UV1-UV4; original retained.");
                        float tiling = MaterialInputs.Float(material, mask ? "_DetailNormalMaskTiling" : "_DetailNormalTiling");
                        return Result(mask ? TextureSemantics.Data : TextureSemantics.Normal, mask ? TextureChannels.R : TextureChannels.All,
                            new SamplingPath { UvChannel = set, Scale = new Vector2(tiling, tiling), Label = "ORL detail UV" }, null);
                    }
                }
            }
            else
            {
                switch (property)
                {
                    case "_MetallicGlossMap":
                        return Result(TextureSemantics.Data, TextureChannels.R | TextureChannels.A, Own(material, property, 0), Main(material));
                    case "_ReflectionMask":
                        return OwnSet(material, property, "_ReflectionMaskUVSet", TextureSemantics.Data, Channel(material, "_ReflectionMaskChannel"), Main(material));
                    case "_DecalsMask": case "_MatcapsMask":
                        return OwnSet(material, property, property + "UVSet", TextureSemantics.Data, TextureChannels.All, Main(material));
                    case "_DetailNormalsMask":
                        return OwnSet(material, property, "_DetailNormalsMaskUVSet", TextureSemantics.Data, TextureChannels.All, Bump(material));
                    case "_DetailNormals0Map": case "_DetailNormals1Map": case "_DetailNormals2Map": case "_DetailNormals3Map":
                        return OwnSet(material, property, property.Replace("Map", "UVSet"), TextureSemantics.Normal, TextureChannels.All, Bump(material));
                }
            }
            return Unsupported(property.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0 ? "Matcaps are sampled by view-space normals; original retained."
                : property.StartsWith("_Decal", StringComparison.Ordinal) ? "Decals use their own placed and rotated coordinates; original retained."
                : property.IndexOf("Ramp", StringComparison.Ordinal) >= 0 || property == "_DFG" ? "Lookup textures are sampled by lighting, not mesh UVs; original retained."
                : "Unverified ORL Toon texture property; original retained.");
        }

        // lerp(GLOBAL_uv * tiling, uv0 * tiling, mode): mode 0 follows the albedo transform, 1 is raw uv0.
        private static SamplingDescription Tiled(Material material, string prefix, SamplingPath global, TextureSemantics semantics,
            TextureChannels channels, SamplerRef sampler)
        {
            // v2 specular has no _SpecularTilingMode property; the uniform stays 0 (synced).
            float mode = material.HasProperty(prefix + "TilingMode") ? MaterialInputs.Float(material, prefix + "TilingMode") : 0;
            float tiling = MaterialInputs.Float(material, prefix + "Tiling");
            if (mode != 0 && mode != 1) return Unsupported(prefix + "TilingMode blends two UV transforms; original retained.");
            var path = mode == 0
                ? new SamplingPath { UvChannel = global.UvChannel, Scale = global.Scale * tiling, Offset = global.Offset * tiling, Label = "ORL GLOBAL_uv * tiling" }
                : new SamplingPath { UvChannel = 0, Scale = new Vector2(tiling, tiling), Label = "ORL uv0 * tiling" };
            return Result(semantics, channels, path, sampler);
        }

        private static SamplingDescription OwnSet(Material material, string property, string setProperty, TextureSemantics semantics,
            TextureChannels channels, SamplerRef sampler)
        {
            int set = Index(material, setProperty);
            if (set < 0 || set > 3) return Unsupported(setProperty + " is outside UV1-UV4; original retained.");
            return Result(semantics, channels, Own(material, property, set), sampler);
        }

        private static SamplingPath Own(Material material, string property, int set) => new SamplingPath
            { UvChannel = set, Scale = MaterialInputs.Scale(material, property), Offset = MaterialInputs.Offset(material, property), Label = "ORL own UV" + set };

        private static SamplingPath Raw() => new SamplingPath { UvChannel = 0, Label = "ORL raw uv0" };

        // A borrowed sampler: the named texture's, or the default texture's Repeat when that slot is empty.
        private sealed class SamplerRef { public Texture Texture; }
        private static SamplerRef Main(Material material) => new SamplerRef { Texture = material.GetTexture("_MainTex") };
        private static SamplerRef Bump(Material material) => new SamplerRef { Texture = material.GetTexture("_BumpMap") };

        private static SamplingDescription Result(TextureSemantics semantics, TextureChannels channels, SamplingPath path, SamplerRef sampler)
        {
            if (sampler != null) { path.SamplerTexture = sampler.Texture; path.FixedRepeat = !sampler.Texture; }
            return new SamplingDescription { AdapterId = Id, Supported = true, Semantics = semantics, Channels = channels,
                Paths = new List<SamplingPath> { path } };
        }

        private static int Index(Material material, string property)
        {
            float value = MaterialInputs.Float(material, property);
            return value == Mathf.Round(value) ? (int)value : -1;
        }

        // [Enum(R,0,G,1,B,2,A,3)] channel selectors.
        private static TextureChannels Channel(Material material, string property)
        {
            switch (Index(material, property))
            {
                case 0: return TextureChannels.R;
                case 1: return TextureChannels.G;
                case 2: return TextureChannels.B;
                case 3: return TextureChannels.A;
                default: return TextureChannels.All;
            }
        }

        // [Enum(RGB,0,R,1,G,2,B,3,A,4)] selectors.
        private static TextureChannels Selected(Material material, string property)
        {
            int value = Index(material, property);
            return value == 0 ? TextureChannels.RGB : value >= 1 && value <= 4 ? (TextureChannels)(1 << (value - 1)) : TextureChannels.All;
        }

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
