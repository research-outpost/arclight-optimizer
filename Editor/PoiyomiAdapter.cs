using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Any Poiyomi shader whose shader_master_label reads Poiyomi 9.x.x-12.x.x is accepted, locked or unlocked
    // (owner decision: versions other than the audited ones are supported unaudited). The model is chosen by
    // layout: shaders with the global _UVSettings* stage use the Toon 10.0.22 model; shaders without it use
    // the 9.x model audited from Pro 9.3.64 (9.3.67 Toon shares its raw-UV0 and Repeat-sampler rules). Entry
    // names containing "Fur" add the Lil Fur paths. Audited: every Toon 10.0.22 entry and Pro 9.3.64; see
    // Documentation~/POIYOMI.md. Missing sampling controls still retain the texture.
    // Lil Fur (10.0.22): UVs are interpolated inside each triangle, _FurMask is read at raw UV0 in the geometry
    // stage, and the fur fragment resets poiMesh.uv to raw mesh UVs after lighting setup, so later lookups skip
    // the global UV tiling/offset and the backface shift.
    internal sealed class PoiyomiAdapter : ITextureSamplingAdapter
    {
        private const string Id = "poiyomi-toon-static-v2";
        private const string ProId = "poiyomi-9x-static-v2";
        private const string VersionReason = "Poiyomi 9.x-12.x shaders are supported; this shader has no Poiyomi 9-12 version label. Original retained.";
        private static HashSet<string> Set(string fields) => new HashSet<string>(fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        internal static readonly HashSet<string> ModeledFields = Set(
            "_MainTex _BumpMap _AlphaMask _MainColorAdjustTexture _MainTintTexture _Bump2ndMap _Bump2ndScaleMask _BentNormalMap " +
            "_DetailMask _DetailTex _DetailNormalMap _BackFaceTexture _BackFaceMask _RGBMask _RGBAMetallicMaps _RGBASmoothnessMaps " +
            "_RedTexture _GreenTexture _BlueTexture _AlphaTexture _RgbNormalR _RgbNormalG _RgbNormalB _RgbNormalA _DecalMask " +
            "_LightingAOMaps _LightingDetailShadowMaps _LightingShadowMasks _LightDataSDFMap _ShadowColorTex _Shadow2ndColorTex _Shadow3rdColorTex " +
            "_ShadowStrengthMask _ShadowBorderMask _MultilayerMathBlurMap _1st_ShadeMap _2nd_ShadeMap _SkinThicknessMap _ClothMetallicSmoothnessMap " +
            "_SDFShadingTexture _AnisoColorMap _MatcapMask _Matcap2Mask _Matcap3Mask _Matcap4Mask _CubeMapMask _Set_RimLightMask _RimMask _RimTex _RimColorTex " +
            "_Set_Rim2LightMask _Rim2Mask _Rim2Tex _Rim2ColorTex _DepthRimMask _SSSThicknessMap _MochieMetallicMaps _AnisotropyMap _ClearCoatMaps " +
            "_RimEnviroMask _HighColor_Tex _Set_HighColorMask _SmoothnessTex _MetallicGlossMap _ReflectionColorTex _BacklightColorTex " +
            "_OutlineMask _OutlineTexture _DepthBulgeMask _DissolveNoiseTexture _DissolveToTexture _DissolveDetailNoise _DissolveMask _FlipbookMask _FlipbookMask1 " +
            "_EmissionMask _EmissionMap _EmissionMask1 _EmissionMap1 _EmissionMask2 _EmissionMap2 _EmissionMask3 _EmissionMap3 " +
            "_GlitterColorMap _GlitterMask _PathingMap _PathingColorMap _MirrorTexture _DepthMask _DepthTexture _ParallaxInternalMapMask " +
            "_VideoMaskTexture _VoronoiNoise _VoronoiMask _TruchetMask _ALDecalColorMask _VertexManipulationHeightMask _LookAtMask _VertexGlitchingMask _UzumoreMask _VertexBasicsMask " +
            "_GlobalMaskTexture0 _GlobalMaskTexture1 _GlobalMaskTexture2 _GlobalMaskTexture3 _DistortionMask _DistortionFlowTexture _DistortionFlowTexture1 _Heightmask _PPMask " +
            "_GrabPassBlendMap _FurMask _FurNoiseMask " +
            // Custom matcap normal maps: every 10.0.22 entry samples them only in calculateNormal, at poiUV(poiMesh.uv[<UV>], _ST) panned,
            // through the main sampler, like the other mesh-UV normal maps; only the matcap lookup after it is view-dependent.
            "_Matcap0NormalMap _Matcap1NormalMap _Matcap2NormalMap _Matcap3NormalMap");
        internal static readonly HashSet<string> ProModeledFields = BuildProModeledFields();
        private static readonly HashSet<string> Normals = Set("_BumpMap _Bump2ndMap _BentNormalMap _DetailNormalMap _RgbNormalR _RgbNormalG _RgbNormalB _RgbNormalA " +
            "_Matcap0NormalMap _Matcap1NormalMap _Matcap2NormalMap _Matcap3NormalMap");
        private static readonly HashSet<string> Repeat = Set("_LightDataSDFMap _ShadowBorderMask _SmoothnessTex _MetallicGlossMap _ReflectionColorTex _OutlineMask _DepthBulgeMask _DissolveDetailNoise _PathingMap _VertexManipulationHeightMask _LookAtMask _VertexGlitchingMask _UzumoreMask _VertexBasicsMask _Heightmask _FurMask _FurNoiseMask");
        private static readonly HashSet<string> ProRepeat = BuildProRepeat();
        private static readonly HashSet<string> VertexOnly = Set("_DepthBulgeMask _VertexManipulationHeightMask _LookAtMask _VertexGlitchingMask _UzumoreMask _VertexBasicsMask");
        private static readonly HashSet<string> VertexAndFragment = Set("_OutlineMask _DissolveNoiseTexture _DissolveDetailNoise");
        private static readonly HashSet<string> Raw = Set("_RimEnviroMask _DistortionMask");
        // 9.3.67 Lil Fur also reads [NoScaleOffset] _FurMask at raw UV0 with a Repeat sampler.
        private static readonly HashSet<string> ProRawUv0 = Set("_SmoothnessTex _MetallicGlossMap _ReflectionColorTex _FurMask");
        private static readonly string[] Colors = { "Red", "Green", "Blue", "Alpha" };
        // 10.x channel reads: every sample site of these fields in all seven Toon 10.0.22 entries takes a fixed
        // swizzle. Other fields read the whole texel or pick a channel from a material value, and 9.x layouts
        // were not audited for channels, so they keep every channel.
        private static readonly Dictionary<string, TextureChannels> ToonChannels = new Dictionary<string, TextureChannels>(StringComparer.Ordinal)
        {
            { "_Bump2ndScaleMask", TextureChannels.R }, { "_SkinThicknessMap", TextureChannels.R }, { "_SDFShadingTexture", TextureChannels.R },
            { "_SmoothnessTex", TextureChannels.R }, { "_MetallicGlossMap", TextureChannels.R }, { "_DissolveNoiseTexture", TextureChannels.R },
            { "_DissolveMask", TextureChannels.R }, { "_VertexGlitchingMask", TextureChannels.R },
            { "_DetailMask", TextureChannels.R | TextureChannels.G }, { "_LightDataSDFMap", TextureChannels.R | TextureChannels.G },
            { "_DetailTex", TextureChannels.RGB }, { "_BacklightColorTex", TextureChannels.RGB },
            { "_GlitterColorMap", TextureChannels.RGB }, { "_DepthTexture", TextureChannels.RGB },
        };
        // Fields whose only sample site in every Toon 10.0.22 entry is tex[<channel property>]: one channel
        // chosen by a material value (0-3 = R, G, B, A).
        private static readonly Dictionary<string, string> SelectedChannel = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "_BackFaceMask", "_BackFaceMaskChannel" }, { "_CubeMapMask", "_CubeMapMaskChannel" },
            { "_Set_RimLightMask", "_Set_RimLightMaskChannel" }, { "_Set_Rim2LightMask", "_Set_Rim2LightMaskChannel" },
            { "_DepthRimMask", "_DepthRimMaskChannel" }, { "_RimEnviroMask", "_RimEnviroChannel" },
            { "_Set_HighColorMask", "_Set_HighColorMaskChannel" }, { "_DepthBulgeMask", "_DepthBulgeMaskChannel" },
            { "_FlipbookMask", "_FlipbookMaskChannel" }, { "_FlipbookMask1", "_FlipbookMaskChannel1" },
            { "_EmissionMask", "_EmissionMaskChannel" }, { "_EmissionMask1", "_EmissionMask1Channel" },
            { "_EmissionMask2", "_EmissionMask2Channel" }, { "_EmissionMask3", "_EmissionMask3Channel" },
            { "_GlitterMask", "_GlitterMaskChannel" }, { "_DepthMask", "_DepthMaskChannel" },
            { "_ParallaxInternalMapMask", "_ParallaxInternalMapMaskChannel" }, { "_VideoMaskTexture", "_VideoMaskTextureChannel" },
            { "_VoronoiNoise", "_VoronoiNoiseChannel" }, { "_VoronoiMask", "_VoronoiMaskChannel" },
            { "_TruchetMask", "_TruchetMaskChannel" }, { "_VertexManipulationHeightMask", "_VertexManipulationHeightMapChannel" },
            { "_UzumoreMask", "_UzumoreMaskChannel" }, { "_Heightmask", "_HeightmaskChannel" }, { "_DistortionMask", "_DistortionMaskChannel" },
        };

        // Material values are read through MaterialInputs, so animating them is checked like any sampling input.
        // 9.x Toon (not Pro) channel reads: every sample site of these fields in the installed 9.0, 9.1 and 9.3 Toon entries
        // (all of them: Toon, Two Pass, Early Outline, Grab Pass, World, Lil Fur) takes .r alone. Only shaders named Poiyomi Toon
        // (locked ones keep the name) qualify: Pro 9.x was not audited, and SPS-patched copies hide which one they came from.
        private static readonly Dictionary<string, TextureChannels> NineToonChannels = new Dictionary<string, TextureChannels>(StringComparer.Ordinal)
            { { "_SkinThicknessMap", TextureChannels.R }, { "_DissolveMask", TextureChannels.R }, { "_SmoothnessTex", TextureChannels.R } };

        private static TextureChannels NineChannelsRead(Material material, string property) =>
            material.shader.name.IndexOf("Poiyomi Toon", StringComparison.Ordinal) >= 0 && NineToonChannels.TryGetValue(property, out var read) ? read : TextureChannels.All;

        private static TextureChannels ToonChannelsRead(Material material, string property)
        {
            if (ToonChannels.TryGetValue(property, out var fixedRead)) return fixedRead;
            if (SelectedChannel.TryGetValue(property, out string selector))
                return material.HasProperty(selector) ? ChannelOf(Number(material, selector)) : TextureChannels.All;
            // Every main-texture read is followed by mainTexture.a = max(mainTexture.a, _MainIgnoreTexAlpha), so the
            // texture's alpha is never used once Ignore Main Texture Alpha is 1 or more. (The video-pixelate resample
            // skips that line; it is already unsupported above.)
            if (property == "_MainTex" && material.HasProperty("_MainIgnoreTexAlpha") && Number(material, "_MainIgnoreTexAlpha") >= 1)
                return TextureChannels.RGB;
            if (property == "_MainTex" && ForcedOpaque(material)) return TextureChannels.RGB;
            return TextureChannels.All;
        }

        // Every non-fur pass of every Toon 10.0.22 entry ends with alpha = _AlphaForceOpaque ? 1 : alpha
        // (_AlphaForceOpaque2 for the Two Pass second pass) before alpha reaches the output, blending, clipping or
        // fog. Before that line, main alpha changes colour only through premultiply, lilToon-style reflection
        // transparency, video effects and the Grab Pass blend; everything else only rewrites alpha itself.
        // Lil Fur passes skip the force line and keep main alpha.
        private static bool ForcedOpaque(Material material)
        {
            string name = material.shader.name;
            if (name.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            foreach (string force in new[] { "_AlphaForceOpaque", "_AlphaForceOpaque2" })
                if (material.HasProperty(force) && Number(material, force) == 0) return false;
            if (!material.HasProperty("_AlphaForceOpaque") || On(material, "_AlphaPremultiply") || On(material, "_VideoEffectsEnable"))
                return false;
            bool reflection = On(material, "_StylizedSpecular") || material.IsKeywordEnabled("POI_STYLIZED_StylizedSpecular");
            return !(reflection && Number(material, "_ReflectionApplyTransparency") >= 0.5f);
        }

        // Index 0-3 picks R, G, B or A; anything else is undefined and keeps every channel.
        internal static TextureChannels ChannelOf(float index) =>
            index == 0 ? TextureChannels.R : index == 1 ? TextureChannels.G : index == 2 ? TextureChannels.B : index == 3 ? TextureChannels.A : TextureChannels.All;
        // What differs between the audited sampling layouts; everything else is shared.
        private sealed class Layout
        {
            internal string Id;
            internal HashSet<string> Fields, Repeat, VertexAndFragment, RawUv0;
            internal Func<string, string> Excluded;
            internal bool GlobalUv, PbrMaskRepeat;
        }
        // 10.x (audited: Toon 10.0.22 entries): global _UVSettings* stage.
        private static readonly Layout Toon10 = new Layout { Id = Id, Fields = ModeledFields, Repeat = Repeat,
            VertexAndFragment = VertexAndFragment, RawUv0 = Set(""), Excluded = ExcludedReason, GlobalUv = true };
        // 9.x (audited: Pro 9.3.64; 9.3.67 Toon matches): no global stage, raw-UV0 Repeat fields, Repeat PBR masks.
        private static readonly Layout NineX = new Layout { Id = ProId, Fields = ProModeledFields, Repeat = ProRepeat,
            VertexAndFragment = Set("_OutlineMask"), RawUv0 = ProRawUv0, Excluded = ProExcludedReason, PbrMaskRepeat = true };

        private static HashSet<string> BuildProModeledFields()
        {
            var fields = new HashSet<string>(ModeledFields, StringComparer.Ordinal);
            fields.ExceptWith(Set("_BentNormalMap _Bump2ndMap _Bump2ndScaleMask _DepthRimMask _FlipbookMask1 _MainTintTexture _VertexGlitchingMask _UzumoreMask " +
                "_Matcap0NormalMap _Matcap1NormalMap _Matcap2NormalMap _Matcap3NormalMap")); // Matcap normals: audited in 10.x only.
            fields.UnionWith(Set("_SSAOMask _SSAOColorMap _ConstellationMask"));
            return fields;
        }

        private static HashSet<string> BuildProRepeat()
        {
            var repeat = new HashSet<string>(Repeat, StringComparer.Ordinal);
            // Pro samples dissolve detail noise with the main texture sampler.
            repeat.Remove("_DissolveDetailNoise");
            // These Pro properties explicitly use sampler_trilinear_repeat.
            repeat.UnionWith(Set("_RGBMask _RedTexture _GreenTexture _BlueTexture _AlphaTexture _RgbNormalR _RgbNormalG _RgbNormalB _RgbNormalA _RGBAMetallicMaps _RGBASmoothnessMaps"));
            return repeat;
        }

        // SPS-patched copies are named by hash; their version label still marks them as Poiyomi. SPS changes only
        // vertex positions, so sampling is the underlying shader's.
        public bool Matches(Material material) => material.shader &&
            (material.shader.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
             SpsLilToonAdapter.IsSps(material.shader) && !SpsLilToonAdapter.HasLilToonInventory(material) && Version(material.shader) != null);

        public SamplingDescription Describe(Material material, string property)
        {
            if (material.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                return DescribeLocked(material, property);
            string version = Version(material.shader);
            if (version == null) return Unsupported(VersionReason);
            bool fur = material.shader.name.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0;
            var layout = material.shader.FindPropertyIndex("_UVSettingsTiling0") >= 0 ? Toon10 : NineX;
            string id = layout.Id + "/" + version;
            if (!layout.Fields.Contains(property))
                return Unsupported(id, layout.Excluded(property));
            try
            {
                if (property == "_MainTex" && (On(material, "_MainPixelMode") || On(material, "_VideoPixelateToResolution")))
                    throw new InvalidOperationException("Main texture pixel/grid sampling is not modeled.");
                if (property.StartsWith("_EmissionMap", StringComparison.Ordinal) &&
                    On(material, "_EmissionCenterOutEnabled" + property.Substring("_EmissionMap".Length)))
                    throw new InvalidOperationException("Emission center-out sampling is view-dependent.");
                if ((property == "_ShadowBorderMask" || property == "_LightDataSDFMap") && On(material, property + "LOD"))
                    throw new InvalidOperationException("Forced sampling LOD can read beyond the protected texel region.");
                if (property == "_VertexBasicsMask" && On(material, "_VertexWindEnabled"))
                    throw new InvalidOperationException("Vertex wind samples the basics mask through world-space noise.");
                if (property == "_SSAOColorMap" && On(material, "_SSAOAsRamp"))
                    throw new InvalidOperationException("SSAO color ramp mode samples this texture with a generated ramp coordinate.");
                string uv = property == "_VertexBasicsMask" ? "VertexBasicsMaskUV" : property == "_FlipbookMask1" ? "_FlipbookMaskUV1" : property + "UV";
                string pan = property == "_VertexBasicsMask" ? "_VertexBasicsMaskUVPan" : property == "_FlipbookMask1" ? "_FlipbookMaskPan1" : property + "Pan";
                Vector4 st = Raw.Contains(property) ? new Vector4(1, 1, 0, 0) : TextureST(material, property);
                string layer = Layer(property);
                if (layer != null) st = Merge(st, Vector(material, "_RGBA" + layer + "ScaleOffset"));
                var paths = new List<SamplingPath>();
                AddPath(material, layout, property, paths, uv, pan, property + "Stochastic", st, VertexOnly.Contains(property), layout.Repeat.Contains(property), fur);
                if (layout.VertexAndFragment.Contains(property))
                    AddPath(material, layout, property, paths, uv, pan, null, st, true, true, fur);
                if (fur && property == "_FurMask")
                    paths.Add(new SamplingPath { UvChannel = 0, Scale = new Vector2(st.x, st.y), Offset = new Vector2(st.z, st.w),
                        FixedRepeat = true, Label = "Poiyomi fur length raw UV0" });
                if (property == "_SDFShadingTexture")
                {
                    foreach (var path in paths.ToArray())
                        paths.Add(new SamplingPath { UvChannel = path.UvChannel, Scale = new Vector2(-path.Scale.x, path.Scale.y),
                            Offset = new Vector2(-path.Offset.x, path.Offset.y), SamplerTexture = path.SamplerTexture,
                            FixedRepeat = path.FixedRepeat, Label = "SDF flipped U" });
                }
                if (property.StartsWith("_GlobalMaskTexture", StringComparison.Ordinal) && On(material, property + "Split"))
                    foreach (string channel in new[] { "G", "B", "A" })
                        AddPath(material, layout, property, paths, uv, property + "SplitPan_" + channel, null,
                            Vector(material, property + "SplitTilingOffset_" + channel), false, false, fur);
                if (property == "_MochieMetallicMaps" && On(material, "_PBRSplitMaskSample"))
                    AddPath(material, layout, property, paths, "_MochieMetallicMasksUV", "_MochieMetallicMasksPan", "_PBRSplitMaskStochastic",
                        Vector(material, "_PBRMaskScaleTiling"), false, false, fur);
                if (property == "_RGBAMetallicMaps" || property == "_RGBASmoothnessMaps")
                    foreach (string color in Colors)
                        if (On(material, "_RGBA" + color + "PBRSplitMaskSample"))
                            AddPath(material, layout, property, paths, "_RGBA" + color + "PBRUV", "_RGBA" + color + "PBRMasksPan",
                                "_RGBA" + color + "PBRSplitMaskStochastic",
                                Merge(Vector(material, "_RGBA" + color + "PBRMaskScaleTiling"), Vector(material, "_RGBA" + color + "ScaleOffset")),
                                false, layout.PbrMaskRepeat, fur);
                return new SamplingDescription { AdapterId = id, Supported = true,
                    Semantics = Normals.Contains(property) ? TextureSemantics.Normal : TextureSemantics.Data, Paths = paths,
                    Channels = layout == Toon10 ? ToonChannelsRead(material, property) : NineChannelsRead(material, property) };
            }
            catch (InvalidOperationException e)
            {
                var excluded = Unsupported(id, e.Message + " Original retained.");
                excluded.OverrideExcluded = e.Message.StartsWith("Matcap UV", StringComparison.Ordinal);
                return excluded;
            }
        }

        // Thry's shader locker generates a shader from the original source with every non-animated value
        // baked in as a constant equal to the material's stored value, strips disabled features, and
        // records the original shader in material tags. Sampling is therefore the original model evaluated
        // with the stored values. Properties marked animated stay real properties; renamed ones are reached
        // by animation under a per-material name that cannot be mapped back here. The original shader must be
        // installed to describe the material, and the locked code must carry a 9-12 label too. The two labels
        // may differ (a material shipped locked with another release); that is accepted unaudited.
        private SamplingDescription DescribeLocked(Material material, string property)
        {
            if (Version(material.shader) == null) return Unsupported(VersionReason);
            var original = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(material.GetTag("OriginalShaderGUID", false, "")));
            if (!original || original.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) < 0 ||
                material.GetTag("OriginalShader", false, "") != original.name)
                return Unsupported("The locked Poiyomi material's original shader is not installed or could not be verified; original retained.");
            var copy = new Material(material) { shader = original };
            try
            {
                string keywords = material.GetTag("OriginalKeywords", false, null);
                if (keywords != null) copy.shaderKeywords = keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var result = Describe(copy, property);
                result.AdapterId += "/locked";
                if (MaterialInputs.Current.Any(input => IsRenamedAnimated(material, input)))
                    result.MaterialInputsUnknown = true;
                return result;
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }

        // "x.y.z" from a shader_master_label of Poiyomi 9.x.x through 12.x.x, else null.
        internal static string Version(Shader shader)
        {
            int index = shader.FindPropertyIndex("shader_master_label");
            if (index < 0) return null;
            var match = System.Text.RegularExpressions.Regex.Match(shader.GetPropertyDescription(index), @"Poiyomi (\d+)\.(\d+)\.(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int major) && major >= 9 && major <= 12
                ? match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value : null;
        }

        // Thry tags a property "<name>Animated" = "2" to rename it on lock; UV/Pan selectors are never renamed.
        // A texture's _ST is renamed with the texture.
        private static bool IsRenamedAnimated(Material material, string input)
        {
            string name = input.EndsWith("_ST", StringComparison.Ordinal) ? input.Substring(0, input.Length - 3) : input;
            if (name.EndsWith("UV", StringComparison.Ordinal) || name.EndsWith("Pan", StringComparison.Ordinal)) return false;
            return material.GetTag(name + "Animated", false, "") == "2";
        }

        // One selected mesh-UV lookup. 10.x layouts then apply the global per-channel tiling/offset to
        // fragment lookups; 9.x layouts read their raw-UV0 fields at UV0 with a Repeat sampler.
        private static void AddPath(Material m, Layout layout, string property, List<SamplingPath> paths, string uvProperty, string pan,
            string stochastic, Vector4 st, bool vertex, bool repeat, bool fur)
        {
            if (layout.RawUv0.Contains(property))
            {
                if ((On(m, "_PoiParallax") || m.IsKeywordEnabled("POI_PARALLAX")) && Number(m, "_ParallaxUV") == 0)
                    throw new InvalidOperationException("Parallax modifies the sampled UV channel.");
                paths.Add(new SamplingPath { UvChannel = 0, FixedRepeat = true, Label = "Poiyomi raw UV0" });
                return;
            }
            float selected = Number(m, uvProperty, true);
            if (selected == 9) throw new InvalidOperationException("Matcap UV sampling is excluded even with Allow Unsupported Shaders enabled.");
            if (selected < 0 || selected > 3 || selected != Mathf.Floor(selected))
                throw new InvalidOperationException(property + " requires mesh UV0-UV3; view, world, polar, distorted and screen-space UVs are not modeled.");
            int uv = (int)selected;
            if (Moving(m, pan) || On(m, stochastic)) throw new InvalidOperationException(property + " uses panning or stochastic sampling.");
            Vector2 scale = new Vector2(st.x, st.y), offset = new Vector2(st.z, st.w);
            Vector2 rawScale = scale, rawOffset = offset;
            if (!vertex)
            {
                if (layout.GlobalUv && (Moving(m, "_UVSettingsPan" + uv) || Number(m, "_UVSettingsRotate" + uv) != 0 || Number(m, "_UVSettingsAngle" + uv) != 0))
                    throw new InvalidOperationException("Poiyomi global UV rotation/panning is not modeled.");
                if ((On(m, "_PoiParallax") || m.IsKeywordEnabled("POI_PARALLAX")) && Number(m, "_ParallaxUV") == uv)
                    throw new InvalidOperationException("Parallax modifies the sampled UV channel.");
                if (layout.GlobalUv)
                {
                    Vector4 tiling = Vector(m, "_UVSettingsTiling" + uv), shift = Vector(m, "_UVSettingsOffset" + uv);
                    offset += Vector2.Scale(new Vector2(shift.x, shift.y), scale);
                    scale = Vector2.Scale(scale, new Vector2(tiling.x, tiling.y));
                }
            }
            // Most fields borrow MainTex's sampler; the shader's unassigned default white texture repeats.
            Texture sampler = repeat ? null : m.GetTexture("_MainTex");
            var path = new SamplingPath { UvChannel = uv, Scale = scale, Offset = offset,
                FixedRepeat = repeat || !sampler, SamplerTexture = sampler, Label = vertex ? "Poiyomi vertex UV" : "Poiyomi mesh UV" };
            paths.Add(path);
            if (!vertex && layout.GlobalUv && On(m, "_UVSettingsShiftBackfaceUV"))
                paths.Add(new SamplingPath { UvChannel = uv, Scale = scale, Offset = offset + new Vector2(st.x, 0),
                    FixedRepeat = path.FixedRepeat, SamplerTexture = sampler, Label = "Poiyomi backface UV" });
            if (!vertex && fur && (rawScale != scale || rawOffset != offset))
                paths.Add(new SamplingPath { UvChannel = uv, Scale = rawScale, Offset = rawOffset,
                    FixedRepeat = path.FixedRepeat, SamplerTexture = sampler, Label = "Poiyomi fur raw mesh UV" });
        }

        private static string Layer(string property)
        {
            for (int i = 0; i < Colors.Length; i++)
                if (property == "_" + Colors[i] + "Texture" || property == "_RgbNormal" + "RGBA"[i]) return Colors[i];
            return null;
        }
        private static Vector4 Merge(Vector4 a, Vector4 b) => new Vector4(a.x * b.x, a.y * b.y, a.z + b.z, a.w + b.w);
        private static Vector4 TextureST(Material m, string p)
        {
            Vector2 s = MaterialInputs.Scale(m, p), o = MaterialInputs.Offset(m, p);
            return new Vector4(s.x, s.y, o.x, o.y);
        }
        private static Vector4 Vector(Material m, string p)
        {
            if (!m.HasProperty(p)) throw new InvalidOperationException("Missing Poiyomi sampling control: " + p);
            var v = MaterialInputs.Vector(m, p);
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsNaN(v.w) ||
                float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z) || float.IsInfinity(v.w))
                throw new InvalidOperationException("Non-finite Poiyomi sampling control: " + p);
            return v;
        }
        private static float Number(Material m, string p, bool required = false)
        {
            if (string.IsNullOrEmpty(p) || !m.HasProperty(p))
            {
                if (required) throw new InvalidOperationException("Missing Poiyomi UV selector: " + p);
                return 0;
            }
            int index = m.shader.FindPropertyIndex(p);
            float value = index >= 0 && m.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Int
                ? MaterialInputs.Int(m, p) : MaterialInputs.Float(m, p);
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Non-finite Poiyomi control: " + p);
            return value;
        }
        private static bool Moving(Material m, string p) => m.HasProperty(p) && (Vector(m, p).x != 0 || Vector(m, p).y != 0);
        private static bool On(Material m, string p) => Number(m, p) != 0;
        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
        internal static string ExcludedReason(string property)
        {
            if (property.IndexOf("matcap", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Matcap images/custom normal paths are retained; only modeled mesh-UV blend masks are supported.";
            if (property.Contains("Cube") || property.Contains("Fallback") || property.Contains("Array"))
                return "Cubemap and array textures cannot be represented by the PNG replacement pipeline.";
            if (property.Contains("LUT") || property.Contains("Ramp") || property.Contains("Curve") || property == "_MainGradationTex" || property == "_ClothDFG")
                return "Lookup/ramp/curve textures are not sampled through mesh UV coverage.";
            if (property.StartsWith("_DecalTexture", StringComparison.Ordinal))
                return "Decal projection, animation and channel-separated sampling are not yet modeled.";
            if (property == "_FurVectorTex")
                return "Fur direction maps are tangent-space vector fields that require separate encoding validation; original retained.";
            if (property.StartsWith("_RNM", StringComparison.Ordinal))
                return "Bakery directional lightmaps are sampled at lightmap UVs; original retained.";
            return "Poiyomi procedural, derived-coordinate or unmodeled sampling path; original retained.";
        }

        private static string ProExcludedReason(string property)
        {
            if (property == "_UzumoreMask")
                return "This vertex mask uses fixed point/Clamp sampling that is not modeled; original retained.";
            if (property == "_DissolveEdgeGradient")
                return "Dissolve edge gradient is a lookup texture sampled with generated edge alpha coordinates; original retained.";
            if (property == "_TPS_BakedMesh" || property == "_OrificeData")
                return "Baked mesh/orifice data is shader-specific structured data rather than a mesh-UV image; original retained.";
            return ExcludedReason(property);
        }

        private static SamplingDescription Unsupported(string id, string reason) => new SamplingDescription
        {
            AdapterId = id,
            Supported = false,
            Reason = reason
        };
    }
}
