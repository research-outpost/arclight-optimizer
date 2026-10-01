using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // lilxyzw NonToon 0.1.3 on Shader Core 0.1.12, built-in pipeline passes. Shader Core generates the
    // shader at import from the .scshader, its shader library and the modules enabled for it in
    // ProjectSettings/jp.lilxyzw.shadercore.asset, so support is pinned to the audited generated source.
    // Forward, add, outline, shadow-caster and meta passes all read textures at interpolated mesh UVs
    // (outlines only move vertices; NonToonFur shells interpolate UVs inside each triangle):
    //  - _BaseTexture, _SharedMask, _NormalMap, Details _DetailMask and Shade _SDFMap: raw UV0 through
    //    _BaseTexture's sampler (these properties have no scale/offset).
    //  - Details Detail0-3 texture and normal map: the UV0-UV3 channel picked by DetailNUV, scaled by the
    //    detail texture's _ST (the normal map shares it), fixed Repeat sampler.
    //  - NonToonFur _FurNoiseMask: raw UV0 times _FurNoiseTiling, fixed Repeat sampler.
    // With _NormalMapWithRoughness the normal maps pack roughness into x/z, so they are data, not normals.
    internal sealed class NonToonAdapter : ITextureSamplingAdapter
    {
        private const string Id = "nontoon-0.1.3-static-v1";
        private const string Folder = "Packages/jp.lilxyzw.nontoon/Shaders/";
        internal const string ShaderPath = Folder + "NonToon.scshader";
        internal const string FurShaderPath = Folder + "NonToonFur.scshader";
        internal const string Details = "_jp_lilxyzw_nontoon_details_";
        private const string SdfMap = "_jp_lilxyzw_nontoon_shade_SDFMap";
        // SHA-256 of each generated "Shader Source" sub-asset (line endings normalized) with all ten
        // NonToon modules enabled, the package default.
        // ponytail: exact source pin; other module selections or releases are retained until audited.
        private static readonly Dictionary<string, string> AuditedSources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ShaderPath, "2134c34d1ec033eb46446cb4ad7a415113256b72a663f7e218a98ad4389224c8" },
            { FurShaderPath, "905fe45a3113e2f3528e11c35138819915830f171209e32a3ab51a6ac3cabc62" }
        };

        public bool Matches(Material material) => material.shader &&
            AuditedSources.ContainsKey(AssetDatabase.GetAssetPath(material.shader));

        public SamplingDescription Describe(Material material, string property)
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline)
                return Unsupported("This NonToon adapter supports the Built-in Render Pipeline only.");
            string failure = SourceFailure(AssetDatabase.GetAssetPath(material.shader));
            if (failure != null) return Unsupported(failure);
            // The audited source reads no undeclared texture except _CameraDepthTexture and _GrabTexture.
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            var baseTexture = material.GetTexture("_BaseTexture");
            switch (property)
            {
                case "_BaseTexture": return BaseUv(baseTexture, TextureSemantics.Color);
                case "_SharedMask": case Details + "DetailMask": case SdfMap: return BaseUv(baseTexture, TextureSemantics.Data);
                case "_NormalMap": return BaseUv(baseTexture, NormalSemantics(material));
                case "_FurNoiseMask":
                    float tiling = MaterialInputs.Float(material, "_FurNoiseTiling");
                    if (float.IsNaN(tiling) || float.IsInfinity(tiling)) return Unsupported("Non-finite fur noise tiling; original retained.");
                    return Supported(TextureSemantics.Data, new SamplingPath { UvChannel = 0, Scale = new Vector2(tiling, tiling),
                        FixedRepeat = true, Label = "NonToon fur noise UV0" });
                case "_SharedGradients":
                    return Unsupported("Shared gradients are a ramp texture array sampled by lighting terms; original retained.");
                case "_NTDitherTex":
                    return Unsupported("The dither pattern is read by screen pixel position; original retained.");
            }
            for (int i = 0; i < 4; i++)
            {
                string texture = Details + "Detail" + i + "Texture";
                if (property != texture && property != Details + "Detail" + i + "NormalMap") continue;
                int uv = MaterialInputs.Int(material, Details + "Detail" + i + "UV");
                if (uv < 0 || uv > 3) return Unsupported("NonToon detail UV selector is outside UV0-UV3; original retained.");
                return Supported(property == texture ? TextureSemantics.Color : NormalSemantics(material), new SamplingPath { UvChannel = uv,
                    Scale = MaterialInputs.Scale(material, texture), Offset = MaterialInputs.Offset(material, texture),
                    FixedRepeat = true, Label = "NonToon detail UV" + uv });
            }
            return Unsupported(property.StartsWith("_jp_lilxyzw_nontoon_matcaps_", StringComparison.Ordinal)
                ? "Matcaps are sampled by view-space normals; original retained."
                : "Unrecognized NonToon texture field " + property + "; original retained.");
        }

        private static string SourceFailure(string path) => ShaderAdapterRegistry.PinnedSourceFailure(path, AuditedSources[path], () =>
            {
                var source = AssetDatabase.LoadAllAssetsAtPath(path).OfType<TextAsset>().FirstOrDefault();
                return source ? FingerprintService.Hash(Encoding.UTF8.GetBytes(source.text.Replace("\r\n", "\n"))) : null;
            },
            "The generated NonToon shader differs from the audited NonToon 0.1.3 / Shader Core 0.1.12 build with all ten NonToon modules; original retained.");

        private static TextureSemantics NormalSemantics(Material material) =>
            MaterialInputs.Int(material, "_NormalMapWithRoughness") != 0 ? TextureSemantics.Data : TextureSemantics.Normal;

        // Unassigned, the shader's default white texture samples with Repeat.
        private static SamplingDescription BaseUv(Texture baseTexture, TextureSemantics semantics) => Supported(semantics,
            new SamplingPath { UvChannel = 0, SamplerTexture = baseTexture, FixedRepeat = !baseTexture, Label = "NonToon raw UV0" });

        private static SamplingDescription Supported(TextureSemantics semantics, SamplingPath path) => new SamplingDescription
        {
            AdapterId = Id, Supported = true, Semantics = semantics, Paths = new List<SamplingPath> { path }
        };

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
