using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Mochie Uber and Uber (Outline), audited 2026-11-16 on Uber v1.35.2 (Mochie Unity Shaders 1.78: USPass, USFunctions, USBRDF,
    // USLighting). Every texture is read at a mesh UV built in the vertex stage or from i.rawUV (raw uv0):
    //  - i.uv.xy = Rotate2D(uv0 * _MainTex_ST + time * _MainTexScroll, _MainTexRot): base colour, normal, metallic, specular/gloss,
    //    smoothness, occlusion, AO tint, packed map, mirror texture, scatter and thickness maps, the four packed masks, and every
    //    separate mask unless _MASK_TRANSFORMS_ON gives it its own transform (below). The base colour is also read at raw uv0 in
    //    the vertex stage (a NaN guard multiplied by _NaNLmao = 0), which the main path's coverage already holds for unrotated UVs.
    //  - i.uv.zw = uv0 * _EmissionMap_ST + time * _EmissScroll: _EmissionMap. _EmissionMap2: rawUV * own ST + time * _EmissScroll2.
    //  - i.uv2.xy = Rotate2D(uv[_UVSec] * _DetailAlbedoMap_ST + time * _DetailScroll, _DetailRot): the five detail maps.
    //  - i.uv2.zw = uv0 * _RimTex_ST + time * _RimScroll (forward) or uv0 * _OutlineTex_ST + time * _OutlineScroll (outline pass).
    //  - With _MASK_TRANSFORMS_ON each separate mask also reads rawUV * its own ST + time * its scroll (both paths are kept, as
    //    packed masking and filtering still read some masks at i.uv.xy). _RefractionDissolveMask always reads its own transform.
    //    _PulseMask's own transform names _EmissPulseMask_ST, which has no property, so it is retained when transforms are on.
    //  - Raw uv0: _DetailShadowMap, _CubeBlendMask, _NearClipMask, _IridescenceMask, the vertex rounding/expansion masks and the
    //    outline thickness mask (also own ST + scroll with mask transforms); raw uv0 * own ST: _AlphaMask, _MainTex2, _BCNoiseTex,
    //    and the matcap normal maps (+ scroll).
    // UV distortion and parallax add offsets to i.uv*, so those materials are retained. Matcaps, ramps, flipbooks, cubemaps,
    // screen grabs and AudioLink textures are retained. Channels are not narrowed and nothing is cropped.
    internal sealed class MochieUberAdapter : ITextureSamplingAdapter
    {
        private const string Id = "mochie-uber-1.35-v1";
        private static readonly string[] Shaders = { "Mochie/Uber", "Mochie/Uber (Outline)" };

        private static readonly HashSet<string> MainUv = Set("_MainTex _BumpMap _MetallicGlossMap _SpecGlossMap _SmoothnessMap _OcclusionMap " +
            "_AOTintTex _PackedMap _MirrorTex _ScatterTex _ThicknessMap _PackedMask0 _PackedMask1 _PackedMask2 _PackedMask3");
        // Separate masks: i.uv.xy, plus rawUV * own ST + own scroll under _MASK_TRANSFORMS_ON. Value: the scroll property.
        private static readonly Dictionary<string, string> SeparateMasks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "_ReflectionMask", "_ReflectionMaskScroll" }, { "_SpecularMask", "_SpecularMaskScroll" }, { "_ERimMask", "_ERimMaskScroll" },
            { "_MatcapMask", "_MatcapMaskScroll" }, { "_MatcapBlendMask", "_MatcapBlendMaskScroll" }, { "_InterpMask", "_InterpMaskScroll" },
            { "_SubsurfaceMask", "_SubsurfaceMaskScroll" }, { "_RimMask", "_RimMaskScroll" }, { "_RefractionMask", "_RefractionMaskScroll" },
            { "_DetailMask", "_DetailMaskScroll" }, { "_ShadowMask", "_ShadowMaskScroll" }, { "_DiffuseMask", "_DiffuseMaskScroll" },
            { "_FilterMask", "_FilterMaskScroll" }, { "_EmissMask", "_EmissMaskScroll" }, { "_TeamColorMask", "_TeamColorMaskScroll" },
        };
        private static readonly HashSet<string> Detail = Set("_DetailAlbedoMap _DetailNormalMap _DetailRoughnessMap _DetailMetallic _DetailOcclusionMap");
        private static readonly HashSet<string> RawUv = Set("_DetailShadowMap _CubeBlendMask _NearClipMask _IridescenceMask _VertexRoundingMask _VertexExpansionMask");
        private static readonly HashSet<string> Normals = Set("_BumpMap _DetailNormalMap _MatcapNormal0 _MatcapNormal1");

        private static HashSet<string> Set(string names) => new HashSet<string>(names.Split(' '), StringComparer.Ordinal);

        public bool Matches(Material material) => material.shader && Array.IndexOf(Shaders, material.shader.name) >= 0;

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            if (!IsSupportedLabel(VersionLabel()))
                return Unsupported("Only Mochie Uber 1.35 is audited, and Mochie's Uber version label reads " + (VersionLabel() ?? "nothing readable") +
                    "; original retained.");
            if (material.IsKeywordEnabled("_UV_DISTORTION_ON")) return Unsupported("Mochie UV distortion offsets the mesh UVs; original retained.");
            if (material.IsKeywordEnabled("_PARALLAXMAP_ON")) return Unsupported("Mochie parallax offsets the mesh UVs; original retained.");
            bool sos = material.IsKeywordEnabled("_MASK_TRANSFORMS_ON");

            if (MainUv.Contains(property) || SeparateMasks.ContainsKey(property) || property == "_PulseMask")
            {
                var paths = new List<SamplingPath>();
                var main = MainPath(material);
                if (main == null) return Unsupported("Mochie main UV scrolling or rotation moves sampling; original retained.");
                paths.Add(main);
                if (sos && SeparateMasks.TryGetValue(property, out string scroll))
                {
                    var own = OwnPath(material, property, scroll);
                    if (own == null) return Unsupported(scroll + " moves sampling over time; original retained.");
                    paths.Add(own);
                }
                if (sos && property == "_PulseMask") return Unsupported("Mochie pulse mask transform reads an undeclared tiling; original retained.");
                if (property == "_PackedMask3" && material.shader.name.EndsWith("(Outline)", StringComparison.Ordinal))
                    paths.Add(Raw("Mochie outline thickness raw uv0"));
                return Result(property, paths, property == "_MainTex" ? null : material.GetTexture("_MainTex"), property != "_MainTex");
            }
            if (Detail.Contains(property))
            {
                if (!Still(material, "_DetailScroll") || Value(material, "_DetailRot") != 0)
                    return Unsupported("Mochie detail scrolling or rotation moves sampling; original retained.");
                float set = Value(material, "_UVSec");
                if (set != 0 && set != 1 && set != 2) return Unsupported("_UVSec is not a mesh UV set; original retained.");
                return Result(property, new List<SamplingPath> { Transformed(material, (int)set, "_DetailAlbedoMap", "Mochie detail UV") },
                    material.GetTexture("_MainTex"), true);
            }
            switch (property)
            {
                case "_EmissionMap":
                    return Still(material, "_EmissScroll") ? Result(property, new List<SamplingPath> { Transformed(material, 0, "_EmissionMap", "Mochie emission UV") }, null, false)
                        : Unsupported("Mochie emission scrolling moves sampling; original retained.");
                case "_EmissionMap2":
                    return Still(material, "_EmissScroll2") ? Result(property, new List<SamplingPath> { Transformed(material, 0, "_EmissionMap2", "Mochie emission 2 UV") }, null, false)
                        : Unsupported("Mochie emission scrolling moves sampling; original retained.");
                case "_RimTex":
                    return Still(material, "_RimScroll") ? Result(property, new List<SamplingPath> { Transformed(material, 0, "_RimTex", "Mochie rim UV") }, material.GetTexture("_MainTex"), true)
                        : Unsupported("Mochie rim scrolling moves sampling; original retained.");
                case "_OutlineTex":
                    return Still(material, "_OutlineScroll") ? Result(property, new List<SamplingPath> { Transformed(material, 0, "_OutlineTex", "Mochie outline UV") }, material.GetTexture("_MainTex"), true)
                        : Unsupported("Mochie outline scrolling moves sampling; original retained.");
                case "_OutlineMask":
                {
                    var paths = new List<SamplingPath> { Raw("Mochie outline thickness raw uv0") };
                    if (sos)
                    {
                        var own = OwnPath(material, property, "_OutlineMaskScroll");
                        if (own == null) return Unsupported("_OutlineMaskScroll moves sampling over time; original retained.");
                        paths.Add(own);
                    }
                    return Result(property, paths, material.GetTexture("_MainTex"), true);
                }
                case "_RefractionDissolveMask":
                {
                    var own = OwnPath(material, property, "_RefractionDissolveMaskScroll");
                    return own == null ? Unsupported("_RefractionDissolveMaskScroll moves sampling over time; original retained.")
                        : Result(property, new List<SamplingPath> { own }, material.GetTexture("_MainTex"), true);
                }
                case "_AlphaMask": case "_MainTex2": case "_BCNoiseTex":
                    return Result(property, new List<SamplingPath> { Transformed(material, 0, property, "Mochie raw uv0 * own ST") }, material.GetTexture("_MainTex"), true);
                case "_MatcapNormal0": case "_MatcapNormal1":
                {
                    var own = OwnPath(material, property, property + "Scroll");
                    return own == null ? Unsupported(property + "Scroll moves sampling over time; original retained.")
                        : Result(property, new List<SamplingPath> { own }, material.GetTexture("_MainTex"), true);
                }
            }
            if (RawUv.Contains(property))
                return Result(property, new List<SamplingPath> { Raw("Mochie raw uv0") }, material.GetTexture("_MainTex"), true);
            return Unsupported(property.IndexOf("Matcap", StringComparison.Ordinal) >= 0 ? "Matcaps are sampled by view-space normals; original retained."
                : property.IndexOf("Ramp", StringComparison.Ordinal) >= 0 ? "Lookup textures are sampled by lighting, not mesh UVs; original retained."
                : "Unverified Mochie Uber texture property; original retained.");
        }

        // i.uv.xy, or null while the main UV scrolls or rotates.
        private static SamplingPath MainPath(Material material) =>
            Still(material, "_MainTexScroll") && Value(material, "_MainTexRot") == 0 ? Transformed(material, 0, "_MainTex", "Mochie main UV") : null;

        // rawUV * own ST + time * scroll, or null while it scrolls.
        private static SamplingPath OwnPath(Material material, string property, string scroll) =>
            Still(material, scroll) ? Transformed(material, 0, property, "Mochie " + property + " own UV") : null;

        private static SamplingPath Transformed(Material material, int set, string stProperty, string label) => new SamplingPath
            { UvChannel = set, Scale = MaterialInputs.Scale(material, stProperty), Offset = MaterialInputs.Offset(material, stProperty), Label = label };

        private static SamplingPath Raw(string label) => new SamplingPath { UvChannel = 0, Scale = Vector2.one, Offset = Vector2.zero, Label = label };

        // A missing scroll property is a zero uniform; a missing float reads 0.
        private static bool Still(Material material, string property)
        {
            if (!material.HasProperty(property)) return true;
            var v = MaterialInputs.Vector(material, property);
            return v.x == 0 && v.y == 0;
        }

        private static float Value(Material material, string property) => material.HasProperty(property) ? MaterialInputs.Float(material, property) : 0;

        // borrowsMain: read through _MainTex's sampler state (the default white's Repeat when no main texture is assigned).
        private static SamplingDescription Result(string property, List<SamplingPath> paths, Texture main, bool borrowsMain)
        {
            foreach (var path in paths)
                if (borrowsMain) { path.SamplerTexture = main; path.FixedRepeat = !main; }
            return new SamplingDescription { AdapterId = Id, Supported = true, Channels = TextureChannels.All,
                Semantics = Normals.Contains(property) ? TextureSemantics.Normal : property == "_MainTex" || property.IndexOf("Emission", StringComparison.Ordinal) >= 0 ||
                    property == "_DetailAlbedoMap" || property == "_RimTex" || property == "_OutlineTex" || property == "_MainTex2" || property == "_ScatterTex" ||
                    property == "_AOTintTex" || property == "_MirrorTex" ? TextureSemantics.Color : TextureSemantics.Data,
                Paths = paths };
        }

        // Mochie.UberEditor.versionLabel ("v1.35.2"), read once per domain; null when the inspector or field is missing.
        private static bool labelRead;
        private static string versionLabel;
        private static string VersionLabel()
        {
            if (!labelRead)
            {
                labelRead = true;
                try
                {
                    var editor = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Mochie.UberEditor")).FirstOrDefault(t => t != null);
                    var field = editor?.GetField("versionLabel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (field != null && field.FieldType == typeof(string))
                        versionLabel = field.GetValue(Activator.CreateInstance(editor)) as string;
                }
                catch (Exception) { versionLabel = null; }
            }
            return versionLabel;
        }

        internal static bool IsSupportedLabel(string label) => label != null && System.Text.RegularExpressions.Regex.IsMatch(label, @"\Av1\.35(\.\d+)?\z");

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
