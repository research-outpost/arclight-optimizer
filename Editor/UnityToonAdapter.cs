using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Unity Toon Shader (com.unity.toonshader, "Toon/Toon"), built-in pipeline subshader, audited 2026-11-16 on 0.16.1-preview
    // (BuiltIn/UCTS_DoubleShadeWithFeather, UCTS_ShadingGradeMap, UCTS_Outline, UCTS_ShadowCaster). Every pass sets Set_UV0 = uv0:
    //  - uv0 * _MainTex_ST: _MainTex (own sampler), _1st_ShadeMap and _2nd_ShadeMap (main sampler), _ClippingMask (also at its
    //    own ST in the outline pass, so both paths are kept).
    //  - uv0 * own ST, own sampler (or the main sampler for the normal map): _NormalMap, _Set_1st_ShadePosition,
    //    _Set_2nd_ShadePosition, _HighColor_Tex, _Set_HighColorMask, _Set_RimLightMask, _Set_MatcapMask, _OutlineTex, and in the
    //    outline vertex stage _Outline_Sampler and _BakedNormal (tex2Dlod level 0). _ShadingGradeMap reads level _BlurLevelSGM,
    //    so it is described only at level 0. _Emissive_Tex reads uv0 * own ST unless _EMISSIVE_ANIMATION scrolls, rotates or
    //    switches it to view coordinates.
    // Matcaps (and their rotated normal map) and the angel ring are view-dependent and retained; the HDRP/URP-only maps are
    // never read by the built-in subshader and are left to the stale/unverified path. Channels are kept and nothing is cropped.
    // Toon/Toon (Tessellation) builds from other sources and is not covered. Gated on the package version 0.16.x.
    internal sealed class UnityToonAdapter : ITextureSamplingAdapter
    {
        private const string Id = "unity-toon-0.16-v1";
        private const string Package = "Packages/com.unity.toonshader/package.json";
        private static readonly HashSet<string> MainTransform = Set("_MainTex _1st_ShadeMap _2nd_ShadeMap _ClippingMask");
        private static readonly HashSet<string> OwnTransform = Set("_NormalMap _Set_1st_ShadePosition _Set_2nd_ShadePosition _HighColor_Tex " +
            "_Set_HighColorMask _Set_RimLightMask _Set_MatcapMask _OutlineTex _Outline_Sampler _BakedNormal _ShadingGradeMap _Emissive_Tex");
        private static HashSet<string> Set(string names) => new HashSet<string>(names.Split(' '), StringComparer.Ordinal);

        public bool Matches(Material material) => material.shader && material.shader.name == "Toon/Toon";

        public SamplingDescription Describe(Material material, string property)
        {
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            if (!IsSupportedVersion(PackageVersion()))
                return Unsupported("Unity Toon Shader 0.16 is audited; installed com.unity.toonshader is " + (PackageVersion() ?? "not found") + ". Original retained.");
            if (property == "_ShadingGradeMap" && Value(material, "_BlurLevelSGM") != 0)
                return Unsupported("A blurred shading grade map reads a coarser mip level beyond the protected texels; original retained.");
            if (property == "_Emissive_Tex" && material.IsKeywordEnabled("_EMISSIVE_ANIMATION"))
                return Unsupported("Animated emission scrolls, rotates or uses view coordinates; original retained.");
            var main = material.GetTexture("_MainTex");
            if (MainTransform.Contains(property))
            {
                var paths = new List<SamplingPath> { Path(material, "_MainTex", property == "_MainTex" ? null : main, property != "_MainTex") };
                if (property == "_ClippingMask") paths.Add(Path(material, "_ClippingMask", null, false));
                return Result(property, paths);
            }
            if (OwnTransform.Contains(property))
                return Result(property, new List<SamplingPath> { Path(material, property, property == "_NormalMap" ? main : null, property == "_NormalMap") });
            return Unsupported(property.IndexOf("MatCap", StringComparison.Ordinal) >= 0 || property == "_AngelRing_Sampler"
                ? "View-dependent lookup; original retained." : "Unverified Unity Toon Shader texture property; original retained.");
        }

        private static SamplingPath Path(Material material, string st, Texture sampler, bool borrowed) => new SamplingPath
        {
            UvChannel = 0, Scale = MaterialInputs.Scale(material, st), Offset = MaterialInputs.Offset(material, st),
            SamplerTexture = borrowed ? sampler : null, FixedRepeat = borrowed && !sampler, Label = "UTS uv0 * " + st + "_ST"
        };

        private static SamplingDescription Result(string property, List<SamplingPath> paths) => new SamplingDescription
        {
            AdapterId = Id, Supported = true, Channels = TextureChannels.All, Paths = paths,
            Semantics = property == "_NormalMap" || property == "_BakedNormal" ? TextureSemantics.Normal
                : property == "_MainTex" || property.EndsWith("ShadeMap", StringComparison.Ordinal) || property == "_HighColor_Tex" ||
                  property == "_OutlineTex" || property == "_Emissive_Tex" ? TextureSemantics.Color : TextureSemantics.Data
        };

        private static float Value(Material material, string property) => material.HasProperty(property) ? MaterialInputs.Float(material, property) : 0;

        private static bool versionRead;
        private static string version;
        internal static string PackageVersion()
        {
            if (!versionRead)
            {
                versionRead = true;
                try { version = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Package)?.version; }
                catch (Exception) { version = null; }
            }
            return version;
        }

        internal static bool IsSupportedVersion(string v) => v != null && System.Text.RegularExpressions.Regex.IsMatch(v, @"\A0\.16\.\d+(-[0-9A-Za-z.-]+)?\z");

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
