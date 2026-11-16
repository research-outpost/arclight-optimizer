using System;
using System.Collections.Generic;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor
{
    // UnlitWF UnToon family (whiteflare, "UnlitWF/.../WF_UnToon_*": plain, Mobile, Outline, OutlineOnly, PowerCap, TriShade,
    // Transparent variants), audited 2026-11-16 on UnlitWF_Shader_20261101 (2.15.0; WF_UnToon, WF_UnToon_Function, _ShadowCaster,
    // _Meta, _ClearBackground, _LineCanceller, WF_Common_BuiltinRP). Vertex input uv = TEXCOORD0, uv2 = TEXCOORD1.
    //  - d.uv_main = uv0 * _MainTex_ST in the forward pass; the meta pass may read uv0 untransformed, so main-UV fields also
    //    keep a raw uv0 path. Read there: _MainTex and _BumpMap (own samplers), and through the main sampler the emission map,
    //    alpha mask, 2nd-texture mask, gradient and colour-change masks, AudioLink delay map, 2nd-normal mask, metallic and
    //    roughness maps, matcap masks, lame mask, shade base/1st/2nd/3rd textures and shade mask, rim, backlight and rim-shadow
    //    masks, overlay mask, distance-fade colour and outline colour. The outline mask is read there in the vertex stage
    //    (level 0, own sampler).
    //  - UV type selectors (0 = uv0, 1 = uv2) times own _ST: _BKT_BackTex, _TX2_MainTex (main sampler), _DetailNormalMap (own
    //    sampler). _DSV_CtrlTex reads uv0 * own _ST, and in the shadow pass (where uv1 is already main-transformed) the composition;
    //    the same composed path is kept for the back and 2nd textures. _OcclusionMap reads the main UV or uv2 (no own _ST).
    // Lame texture, overlay texture, gradient maps, emission scroll gradient and matcaps use procedural, screen, view or lookup
    // coordinates and are retained. Channels are kept and nothing is cropped. Tessellation and Custom variants are not covered.
    internal sealed class UnlitWFAdapter : ITextureSamplingAdapter
    {
        private const string Id = "unlitwf-untoon-2.15-v1";
        private static readonly HashSet<string> MainUv = Set("_MainTex _BumpMap _EmissionMap _AL_MaskTex _TX2_MaskTex _CGR_MaskTex _CLC_MaskTex " +
            "_ES_AU_DelayTex _NS_2ndMaskTex _MetallicGlossMap _SpecGlossMap _LME_MaskTex _TS_BaseTex _TS_1stTex _TS_2ndTex _TS_3rdTex _TS_MaskTex " +
            "_TR_MaskTex _TBL_MaskTex _TM_MaskTex _OVL_MaskTex _DFD_ColorTex _TL_CustomColorTex _TL_MaskTex");
        private static readonly HashSet<string> OwnSamplers = Set("_MainTex _BumpMap _DetailNormalMap _DSV_CtrlTex _TL_MaskTex");
        private static HashSet<string> Set(string names) => new HashSet<string>(names.Split(' '), StringComparer.Ordinal);

        public bool Matches(Material material) => material.shader && IsCovered(material.shader.name);

        internal static bool IsCovered(string name) => name.StartsWith("UnlitWF/", StringComparison.Ordinal) &&
            name.IndexOf("/WF_UnToon_", StringComparison.Ordinal) >= 0 && name.IndexOf("Tess", StringComparison.Ordinal) < 0 &&
            name.IndexOf("Custom", StringComparison.Ordinal) < 0 && name.IndexOf("BakeTexture", StringComparison.Ordinal) < 0 &&
            name.IndexOf("Hidden", StringComparison.Ordinal) < 0 && name.IndexOf("DepthOnly", StringComparison.Ordinal) < 0;

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            string version = Version(material.shader);
            if (!IsSupportedVersion(version))
                return Unsupported("UnlitWF 2.15 is audited; this shader's version label reads " + (version ?? "nothing") + ". Original retained.");
            var main = material.GetTexture("_MainTex");
            bool own = OwnSamplers.Contains(property);
            if (MainUv.Contains(property) || property.StartsWith("_HL_MaskTex", StringComparison.Ordinal))
                return Result(property, own ? null : main, own, MainPath(material), Raw(0, "UnlitWF raw uv0 (meta)"));
            switch (property)
            {
                case "_BKT_BackTex": case "_TX2_MainTex": case "_DetailNormalMap":
                {
                    string type = property == "_BKT_BackTex" ? "_BKT_UVType" : property == "_TX2_MainTex" ? "_TX2_UVType" : "_NS_UVType";
                    float set = Value(material, type);
                    if (set != 0 && set != 1) return Unsupported(type + " is not a mesh UV; original retained.");
                    var paths = new List<SamplingPath> { Own(material, property, set == 1 ? 1 : 0) };
                    if (set == 0 && property != "_DetailNormalMap") paths.Add(Composed(material, property));
                    return Result(property, own ? null : main, own, paths.ToArray());
                }
                case "_DSV_CtrlTex":
                    return Result(property, null, true, Own(material, property, 0), Composed(material, property));
                case "_OcclusionMap":
                {
                    float set = Value(material, "_AO_UVType");
                    if (set != 0 && set != 1) return Unsupported("_AO_UVType is not a mesh UV; original retained.");
                    return Result(property, main, false, set == 1 ? Raw(1, "UnlitWF uv2") : MainPath(material), Raw(0, "UnlitWF raw uv0 (meta)"));
                }
            }
            return Unsupported(property.IndexOf("Matcap", StringComparison.Ordinal) >= 0 ? "Matcaps are sampled by view-space normals; original retained."
                : "UnlitWF texture read at procedural, screen, view or lookup coordinates, or not verified; original retained.");
        }

        private static SamplingPath MainPath(Material material) => new SamplingPath { UvChannel = 0, Scale = MaterialInputs.Scale(material, "_MainTex"),
            Offset = MaterialInputs.Offset(material, "_MainTex"), Label = "UnlitWF uv0 * _MainTex_ST" };

        private static SamplingPath Own(Material material, string property, int set) => new SamplingPath { UvChannel = set,
            Scale = MaterialInputs.Scale(material, property), Offset = MaterialInputs.Offset(material, property), Label = "UnlitWF uv" + set + " * own ST" };

        // (uv0 * _MainTex_ST) * own ST: the shadow pass's uv1.
        private static SamplingPath Composed(Material material, string property)
        {
            Vector2 ms = MaterialInputs.Scale(material, "_MainTex"), mo = MaterialInputs.Offset(material, "_MainTex");
            Vector2 s = MaterialInputs.Scale(material, property), o = MaterialInputs.Offset(material, property);
            return new SamplingPath { UvChannel = 0, Scale = Vector2.Scale(ms, s), Offset = Vector2.Scale(mo, s) + o, Label = "UnlitWF shadow uv1 * own ST" };
        }

        private static SamplingPath Raw(int set, string label) => new SamplingPath { UvChannel = set, Scale = Vector2.one, Offset = Vector2.zero, Label = label };

        private static SamplingDescription Result(string property, Texture sampler, bool ownSampler, params SamplingPath[] paths)
        {
            foreach (var path in paths)
                if (!ownSampler) { path.SamplerTexture = sampler; path.FixedRepeat = !sampler; }
            return new SamplingDescription { AdapterId = Id, Supported = true, Channels = TextureChannels.All, Paths = new List<SamplingPath>(paths),
                Semantics = property == "_BumpMap" || property == "_DetailNormalMap" ? TextureSemantics.Normal
                    : property == "_MainTex" || property == "_EmissionMap" || property == "_BKT_BackTex" || property == "_TX2_MainTex" ||
                      property.StartsWith("_TS_", StringComparison.Ordinal) && property != "_TS_MaskTex" || property == "_DFD_ColorTex" ||
                      property == "_TL_CustomColorTex" ? TextureSemantics.Color : TextureSemantics.Data };
        }

        private static float Value(Material material, string property) => material.HasProperty(property) ? MaterialInputs.Float(material, property) : 0;

        // "2026/11/01 (2.15.0)" from the _CurrentVersion property's display name, else null.
        internal static string Version(Shader shader)
        {
            int index = shader.FindPropertyIndex("_CurrentVersion");
            if (index < 0) return null;
            var match = System.Text.RegularExpressions.Regex.Match(shader.GetPropertyDescription(index), @"\((\d+\.\d+\.\d+)\)");
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static bool IsSupportedVersion(string version) => version != null && version.StartsWith("2.15.", StringComparison.Ordinal);

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
