using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // ORL Toon v1 and v2 (sh.orels.shaders 7.3, audited against the generator's module sources; gated on the generator
    // package version, see GeneratorVersion). Every variant
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
    // Crop basis (TextureCropper), audited 2026-11-16 on the 7.4.0-dev.4 generator modules every v1 and v2 entry includes
    // (7.3.0/7.3.1 differ there only in the UV-set clamp): _MainTex_ST is read only to build GLOBAL_uv, and GLOBAL_uv only
    // feeds texture lookups, except v2 Emission's UV-tile mask (inside its emission-map branch) and AudioLink, whose mask and
    // UV-based effect take GLOBAL_uv as a coordinate (both block cropping while on). So the albedo transform is a basis for _MainTex, _AlphaTex, emission
    // and the "Synced With Albedo" maps at tiling 1, while AudioLink is off. Bicubic sampling sizes its grid with GetDimensions,
    // which a crop keeps texel-aligned. The v2 own-ST masks and detail normals read their _ST only for their own lookup. The v2
    // emission map is skipped when _EmissionMap_TexelSize.w <= 16 and metallic maps count as missing when
    // _MetallicGlossMap_TexelSize.z <= 8, so their crops keep at least 17 texels of height and 9 of width (MinCropSize; ORL
    // textures never take the one-colour collapse). Not cropped: synced maps at another tiling (the rewritten offset would be
    // scaled with them); "Independent", outline and v1 detail lookups (no _ST).
    // Ramps, matcaps and the DFG LUT read lighting or view coordinates only, so they stay uncropped without blocking the rest.
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

        // The generator package builds every ORL shader from its module sources, so its version is the shader's.
        // 7.3.0 is the audited line; 7.3.1 changed only LTCGI and 7.4.0-dev.1-dev.4 only clamped UV-set indices, added an
        // occlusion power and moved the specular mask to intensity (same selectors). 7.0-7.2 read the v2 decal, matcap and
        // detail-normal masks through their own samplers and are retained. Later 7.x releases are accepted unaudited.
        private const string GeneratorPackage = "Packages/sh.orels.shaders.generator/package.json";
        private static bool generatorRead;
        private static string generatorVersion;
        internal static string GeneratorVersion()
        {
            if (!generatorRead)
            {
                generatorRead = true;
                try { generatorVersion = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(GeneratorPackage)?.version; }
                catch (Exception) { generatorVersion = null; }
            }
            return generatorVersion;
        }

        internal static bool IsSupportedGenerator(string version)
        {
            var match = version == null ? null : System.Text.RegularExpressions.Regex.Match(version, @"\A7\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?\z");
            if (match == null || !match.Success || !int.TryParse(match.Groups[1].Value, out int minor) || !int.TryParse(match.Groups[2].Value, out int patch))
                return false;
            // A 7.3.0 prerelease predates the audited release.
            return minor > 3 || minor == 3 && (patch > 0 || !match.Groups[3].Success);
        }

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            if (!IsSupportedGenerator(GeneratorVersion()))
                return Unsupported("ORL Toon is supported from ORL Shader Generator 7.3.0 (7.x); installed generator is " +
                    (GeneratorVersion() ?? "not found") + ". Original retained.");
            bool v2 = Version(material.shader.name) == 2;
            int mainSet = Index(material, "_MainTexUVSet");
            if (mainSet < 0 || mainSet > 3) return Unsupported("Main texture UV set is outside UV1-UV4; original retained.");
            Vector2 mainScale = MaterialInputs.Scale(material, "_MainTex"), mainOffset = MaterialInputs.Offset(material, "_MainTex");
            var global = new SamplingPath { UvChannel = mainSet, Scale = mainScale, Offset = mainOffset, Label = "ORL GLOBAL_uv" };
            string block = !ShaderAdapterRegistry.CropBasisAllowed ? "ORL Toon cropping is proven on PC builds only; original retained on this target."
                : material.HasProperty("AL_MODE") && MaterialInputs.Float(material, "AL_MODE") != 0
                    ? "ORL AudioLink reads the albedo UV as an effect coordinate, so ORL Toon is not cropped while AudioLink is on."
                : v2 && material.HasProperty("_EmissionMaskByUVTile") && MaterialInputs.Float(material, "_EmissionMaskByUVTile") != 0
                    ? "ORL emission Mask By UV Tile reads the albedo UV as a coordinate, so ORL Toon is not cropped while it is on." : null;

            switch (property)
            {
                case "_MainTex":
                    return Result(TextureSemantics.Color,
                        Opaque.Contains(material.shader.name) && !ShaderAdapterRegistry.FallbackReadsAlpha(material) ? TextureChannels.RGB : TextureChannels.All,
                        global, null, Crop(block, "_MainTex"));
                case "_AlphaTex": return Result(TextureSemantics.Data, TextureChannels.R, global, Main(material), Crop(block, "_MainTex"));
                case "_EmissionMap":
                    return Result(TextureSemantics.Color, v2 ? Selected(material, "_EmissionMapChannel") : TextureChannels.RGB,
                        global, Main(material), Crop(block, "_MainTex", v2 ? EmissionMin : default));
                case "_OcclusionMap": case "_OcclusionDetail":
                    return Tiled(material, property == "_OcclusionMap" ? "_Occlusion" : "_OcclusionDetail", global, TextureSemantics.Data,
                        v2 ? Channel(material, property == "_OcclusionMap" ? "_OcclusionChannel" : "_OcclusionDetailChannel") : TextureChannels.R,
                        Main(material), block);
                case "_SpecularMap":
                    // v2 Specular.orlsource 98-100: each of three selectors reads specMap[min(c, 3)] unless it is above 3.
                    return Tiled(material, "_Specular", global, TextureSemantics.Data,
                        v2 ? Union(material, true, "_SpecIntensityChannel", "_SpecRoughnessChannel", "_SpecAlbedoTintChannel") : TextureChannels.RGB, Main(material), block);
                case "_SpecularMask":
                    return Tiled(material, "_SpecularMask", global, TextureSemantics.Data,
                        v2 ? Channel(material, "_SpecularMaskChannel") : TextureChannels.R, Main(material), block);
                case "_BumpMap": return Tiled(material, "_BumpMap", global, TextureSemantics.Normal, TextureChannels.All, null, block);
                case "_OutlineMask": return Result(TextureSemantics.Data, TextureChannels.R, Raw(), null);
                case "_OutlineTex": return Result(TextureSemantics.Color, TextureChannels.RGB, Raw(), v2 ? Main(material) : null);
            }
            if (!v2)
            {
                switch (property)
                {
                    case "_MetallicGlossMap":
                        return Tiled(material, "_MetallicGlossMap", global, TextureSemantics.Data, TextureChannels.R | TextureChannels.A, Main(material),
                            block, MetallicMin);
                    case "_ReflectivityMask":
                        return Tiled(material, "_ReflectivityMask", global, TextureSemantics.Data, TextureChannels.R, Main(material), block);
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
                        return Result(TextureSemantics.Data, TextureChannels.R | TextureChannels.A, Own(material, property, 0), Main(material), Crop(block, property, MetallicMin));
                    case "_ReflectionMask":
                        return OwnSet(material, property, "_ReflectionMaskUVSet", TextureSemantics.Data, Channel(material, "_ReflectionMaskChannel"), Main(material), block);
                    // Decals.orlsource 322 (maskTexture[maskChannel] per decal), Matcaps.orlsource 262-301 and Normals.orlsource
                    // 198-240 (mask[<layer's channel>]): the union of the four layers' selectors.
                    // A layer's channel matters only while its mask strength (lerp(1, mask, strength)) and, for matcaps, its strength
                    // (the product with _MatcapNStrength) are not 0.
                    case "_DecalsMask":
                        return OwnSet(material, property, property + "UVSet", TextureSemantics.Data, LiveLayers(material, i => "_Decal" + i, "_Decal{0}MaskStrength"), Main(material), block);
                    case "_MatcapsMask":
                        return OwnSet(material, property, property + "UVSet", TextureSemantics.Data,
                            LiveLayers(material, i => "_Matcap" + i, "_Matcap{0}MaskStrength", "_Matcap{0}Strength"), Main(material), block);
                    case "_DetailNormalsMask":
                        return OwnSet(material, property, "_DetailNormalsMaskUVSet", TextureSemantics.Data,
                            LiveLayers(material, i => "_DetailNormals" + i, "_DetailNormals{0}MaskStrength"), Bump(material), block);
                    case "_DetailNormals0Map": case "_DetailNormals1Map": case "_DetailNormals2Map": case "_DetailNormals3Map":
                        return OwnSet(material, property, property.Replace("Map", "UVSet"), TextureSemantics.Normal, TextureChannels.All, Bump(material), block);
                }
            }
            var unsupported = Unsupported(property.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0 ? "Matcaps are sampled by view-space normals; original retained."
                : property.StartsWith("_Decal", StringComparison.Ordinal) ? "Decals use their own placed and rotated coordinates; original retained."
                : property.IndexOf("Ramp", StringComparison.Ordinal) >= 0 || property == "_DFG" ? "Lookup textures are sampled by lighting, not mesh UVs; original retained."
                : "Unverified ORL Toon texture property; original retained.");
            unsupported.ReadsNoOtherTransform = LightingLookups.Contains(property);
            return unsupported;
        }

        // Read only at lighting, view or normal-derived coordinates (class comment): left uncropped without blocking the material.
        private static readonly HashSet<string> LightingLookups = new HashSet<string>(StringComparer.Ordinal)
            { "_Ramp", "_CustomGIDiffuseRamp", "_LTCGIRamp", "_Matcap", "_Matcap0Tex", "_Matcap1Tex", "_Matcap2Tex", "_Matcap3Tex", "_DFG" };

        // _EmissionMap_TexelSize.w > 16 (v2) and _MetallicGlossMap_TexelSize.z > 8: the smallest size that still counts as present.
        private static readonly Vector2Int EmissionMin = new Vector2Int(0, 17), MetallicMin = new Vector2Int(9, 0);

        // A crop decision for one lookup: the texture whose _ST it may rewrite (Basis) and the smallest crop allowed, or why it gets none.
        private sealed class CropRef { public string Basis, Reason; public Vector2Int Min; }
        private static CropRef Crop(string block, string basis, Vector2Int min = default) =>
            block == null ? new CropRef { Basis = basis, Min = min } : new CropRef { Reason = block };
        private static CropRef NoCrop(string reason) => new CropRef { Reason = reason };

        // lerp(GLOBAL_uv * tiling, uv0 * tiling, mode): mode 0 follows the albedo transform, 1 is raw uv0.
        // block: why the albedo transform may not be rewritten for this lookup (null when it may).
        private static SamplingDescription Tiled(Material material, string prefix, SamplingPath global, TextureSemantics semantics,
            TextureChannels channels, SamplerRef sampler, string block, Vector2Int min = default)
        {
            // v2 specular has no _SpecularTilingMode property; the uniform stays 0 (synced).
            float mode = material.HasProperty(prefix + "TilingMode") ? MaterialInputs.Float(material, prefix + "TilingMode") : 0;
            // A missing Tiling float would read as 0 and collapse the sampled area to a point; the shader's own
            // uniform default is (typically) 1, so claim nothing.
            if (!material.HasProperty(prefix + "Tiling")) return Unsupported(prefix + "Tiling is missing; original retained.");
            float tiling = MaterialInputs.Float(material, prefix + "Tiling");
            if (mode != 0 && mode != 1) return Unsupported(prefix + "TilingMode blends two UV transforms; original retained.");
            var path = mode == 0
                ? new SamplingPath { UvChannel = global.UvChannel, Scale = global.Scale * tiling, Offset = global.Offset * tiling, Label = "ORL GLOBAL_uv * tiling" }
                : new SamplingPath { UvChannel = 0, Scale = new Vector2(tiling, tiling), Label = "ORL uv0 * tiling" };
            var crop = mode != 0 ? null : tiling != 1 ? NoCrop(prefix + "Tiling is not 1, so the albedo transform cannot be rewritten for it.")
                : Crop(block, "_MainTex", min);
            return Result(semantics, channels, path, sampler, crop);
        }

        private static SamplingDescription OwnSet(Material material, string property, string setProperty, TextureSemantics semantics,
            TextureChannels channels, SamplerRef sampler, string block)
        {
            int set = Index(material, setProperty);
            if (set < 0 || set > 3) return Unsupported(setProperty + " is outside UV1-UV4; original retained.");
            return Result(semantics, channels, Own(material, property, set), sampler, Crop(block, property));
        }

        private static SamplingPath Own(Material material, string property, int set) => new SamplingPath
            { UvChannel = set, Scale = MaterialInputs.Scale(material, property), Offset = MaterialInputs.Offset(material, property), Label = "ORL own UV" + set };

        private static SamplingPath Raw() => new SamplingPath { UvChannel = 0, Label = "ORL raw uv0" };

        // A borrowed sampler: the named texture's, or the default texture's Repeat when that slot is empty.
        private sealed class SamplerRef { public Texture Texture; }
        private static SamplerRef Main(Material material) => new SamplerRef { Texture = material.GetTexture("_MainTex") };
        private static SamplerRef Bump(Material material) => new SamplerRef { Texture = material.GetTexture("_BumpMap") };

        private static SamplingDescription Result(TextureSemantics semantics, TextureChannels channels, SamplingPath path, SamplerRef sampler,
            CropRef crop = null)
        {
            if (sampler != null) { path.SamplerTexture = sampler.Texture; path.FixedRepeat = !sampler.Texture; }
            if (crop?.Basis != null) { path.Basis = crop.Basis; path.Dependencies = new[] { crop.Basis }; }
            return new SamplingDescription { AdapterId = Id, Supported = true, Semantics = semantics, Channels = channels,
                CropReason = crop?.Reason, MinCropSize = crop?.Min ?? default, Paths = new List<SamplingPath> { path } };
        }

        private static int Index(Material material, string property)
        {
            // A missing selector reads as 0 from GetFloat: claim nothing, let the call site widen or stand down.
            if (!material.HasProperty(property)) return -1;
            float value = MaterialInputs.Float(material, property);
            return value == Mathf.Round(value) ? (int)value : -1;
        }

        // [Enum(R,0,G,1,B,2,A,3)] channel selectors.
        private static TextureChannels Channel(Material material, string property)
        {
            // A missing selector float reads as 0 (channel R), claiming a too-narrow set; widen like Union does.
            if (!material.HasProperty(property)) return TextureChannels.All;
            switch (Index(material, property))
            {
                case 0: return TextureChannels.R;
                case 1: return TextureChannels.G;
                case 2: return TextureChannels.B;
                case 3: return TextureChannels.A;
                default: return TextureChannels.All;
            }
        }

        // The union of the four layers' mask channels, leaving out a layer whose strengths make the mask value irrelevant.
        private static TextureChannels LiveLayers(Material material, Func<int, string> prefix, params string[] strengths)
        {
            var selectors = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                bool live = strengths.All(s => { string p = string.Format(s, i); return !material.HasProperty(p) || MaterialInputs.Float(material, p) != 0; });
                if (live) selectors.Add(prefix(i) + "MaskChannel");
            }
            return Union(material, false, selectors.ToArray());
        }

        // The channels several selectors pick together. skipAbove3: a value above 3 reads nothing (it stands for None).
        private static TextureChannels Union(Material material, bool skipAbove3, params string[] selectors)
        {
            TextureChannels read = 0;
            foreach (string selector in selectors)
            {
                if (!material.HasProperty(selector)) return TextureChannels.All;
                if (skipAbove3 && MaterialInputs.Float(material, selector) > 3) continue;
                read |= Channel(material, selector);
            }
            return read;
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
