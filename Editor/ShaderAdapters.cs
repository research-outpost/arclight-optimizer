using System.Collections.Generic;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    public enum TextureSemantics { Color, Data, Normal, Unknown }

    // Texture channels a shader property can read in every compiled pass. Only an audited adapter narrows this;
    // anything else reads All, which keeps the source's compressed format.
    [System.Flags] public enum TextureChannels { R = 1, G = 2, B = 4, A = 8, RGB = R | G | B, All = RGB | A }

    public sealed class SamplingPath
    {
        public int UvChannel;
        public Vector2 Scale = Vector2.one;
        public Vector2 Offset;
        public Texture SamplerTexture;
        public bool FixedRepeat;
        public string Label;
        // Set only by animation modeling: every (scale.xy, offset.zw) the path can take. Coverage uses
        // the convex hull of each triangle under all of them. Null means the static Scale/Offset.
        public List<Vector4> AnimatedTransforms;
        // Set by an adapter that knows the path's whole transform is raw UV * Basis_ST.xy + Basis_ST.zw and that the
        // shader reads Basis_ST only for texture lookups; texture cropping may then rewrite that _ST. Dependencies lists
        // every texture whose _ST the path reads. Null means the path cannot be cropped.
        public string Basis;
        public string[] Dependencies;
        public TextureWrapMode WrapU(Texture source) => FixedRepeat ? TextureWrapMode.Repeat : (SamplerTexture ? SamplerTexture : source).wrapModeU;
        public TextureWrapMode WrapV(Texture source) => FixedRepeat ? TextureWrapMode.Repeat : (SamplerTexture ? SamplerTexture : source).wrapModeV;
    }

    public sealed class SamplingDescription
    {
        public string AdapterId;
        public bool Supported;
        public bool UnsafeOverride;
        public bool OverrideExcluded;
        // Only a verified shader adapter may assert that an assigned property is never sampled.
        public bool NotSampled;
        public string Reason;
        public TextureSemantics Semantics = TextureSemantics.Unknown;
        public TextureChannels Channels = TextureChannels.All;
        public List<SamplingPath> Paths;
        // Material float/vector/ST values read while describing this property. Animating any other
        // material value cannot change this description. Null means the inputs are unknown.
        public HashSet<string> MaterialInputs;
        // Set by an adapter when animation can reach its inputs under names it cannot map (e.g. locked
        // shaders with renamed animated properties); MaterialInputs then stays null.
        public bool MaterialInputsUnknown;

        public IEnumerable<SamplingPath> GetPaths()
        {
            if (NotSampled || Paths == null) yield break;
            foreach (var path in Paths) yield return path;
        }
    }

    /// <summary>Adapters must describe every sampling path for an eligible property.</summary>
    internal interface ITextureSamplingAdapter
    {
        bool Matches(Material material);
        SamplingDescription Describe(Material material, string property);
    }

    public static class ShaderAdapterRegistry
    {
        private static readonly List<ITextureSamplingAdapter> Adapters =
            new List<ITextureSamplingAdapter> { new KnownUnityShaderAdapter(), new StandardShaderAdapter(), new LilToonAdapter(), new SpsLilToonAdapter(), new PoiyomiAdapter(), new NonToonAdapter(), new VRChatMobileAdapter(), new SunaoAdapter(), new OrelsToonAdapter(), new MochieStandardAdapter() };

        private static readonly Dictionary<string, string> pinnedSourceFailures = new Dictionary<string, string>(System.StringComparer.Ordinal);

        // Whether a shader adapter recognizes the material's shader (audited sampling model).
        internal static bool Recognizes(Material material) => material && material.shader && Adapters.Exists(a => a.Matches(material));

        internal static void BeginScan() { LilToonSourceGuard.BeginScan(); pinnedSourceFailures.Clear(); }

        // For adapters pinned to an audited source hash: hashes each source once per scan. Returns the
        // failure reason, or null when the hash matches.
        internal static string PinnedSourceFailure(string key, string expectedHash, System.Func<string> hash, string failure)
        {
            if (!pinnedSourceFailures.TryGetValue(key, out string cached))
                pinnedSourceFailures[key] = cached = hash() == expectedHash ? null : failure;
            return cached;
        }

        public static SamplingDescription Describe(Material material, string property, bool allowUnsupportedShaders = false)
        {
            SamplingDescription result = null;
            foreach (var adapter in Adapters)
                if (adapter.Matches(material))
                {
                    result = MaterialInputs.Record(() => adapter.Describe(material, property));
                    break;
                }
            if (result == null)
            {
                result = Unsupported("generic-v1", "Unknown shader sampling; no verified adapter.");
                result.MaterialInputs = new HashSet<string>(System.StringComparer.Ordinal);
            }
            if (!allowUnsupportedShaders || result.Supported || result.NotSampled || result.OverrideExcluded ||
                !(material.GetTexture(property) is Texture2D)) return result;
            // Unknown matcap fields must never inherit the mesh-UV assumption.
            int propertyIndex = material.shader.FindPropertyIndex(property);
            string label = propertyIndex >= 0 ? material.shader.GetPropertyDescription(propertyIndex) : "";
            if (IsMatcap(property) || IsMatcap(label) || IsMatcap(material.shader.name))
                return Unsupported("matcap-override-excluded-v1",
                    "Matcap fields are excluded from Allow unsupported shaders; original texture retained.");
            var importer = UnityEditor.AssetImporter.GetAtPath(
                UnityEditor.AssetDatabase.GetAssetPath(material.GetTexture(property))) as UnityEditor.TextureImporter;
            // The override assumes UV0 plus this property's ST. Values that made the adapter reject the
            // field still matter: animating them could switch to a different verified sampling model.
            HashSet<string> inputs = null;
            if (result.MaterialInputs != null)
                inputs = new HashSet<string>(result.MaterialInputs, System.StringComparer.Ordinal) { property + "_ST" };
            return new SamplingDescription
            {
                AdapterId = "unsupported-uv0-override-v1/" + result.AdapterId,
                Supported = true, UnsafeOverride = true,
                Paths = new List<SamplingPath> { new SamplingPath { Scale = material.GetTextureScale(property),
                    Offset = material.GetTextureOffset(property), Label = "Assumed UV0" } },
                Semantics = importer && importer.textureType == UnityEditor.TextureImporterType.NormalMap
                    ? TextureSemantics.Normal : TextureSemantics.Data,
                MaterialInputs = inputs
            };
        }

        private static bool IsMatcap(string value) =>
            !string.IsNullOrEmpty(value) &&
            value.Replace(" ", "").Replace("_", "").Replace("-", "")
                .IndexOf("matcap", System.StringComparison.OrdinalIgnoreCase) >= 0;

        internal static SamplingDescription Unsupported(string id, string reason) =>
            new SamplingDescription { AdapterId = id, Reason = reason };

        // One mesh-UV path scaled by stProperty's tiling/offset, with the sampled texture's own sampler.
        // croppable: the adapter has audited that the shader reads stProperty's _ST only for texture lookups.
        internal static SamplingDescription UvPath(string id, TextureSemantics semantics, int uv, Material material, string stProperty, bool croppable = false) =>
            new SamplingDescription { AdapterId = id, Supported = true, Semantics = semantics, Paths = new List<SamplingPath> {
                new SamplingPath { UvChannel = uv, Scale = MaterialInputs.Scale(material, stProperty),
                    Offset = MaterialInputs.Offset(material, stProperty), Label = "Mesh UV" + uv,
                    Basis = croppable ? stProperty : null, Dependencies = new[] { stProperty } } } };

        // Engine and AudioLink globals that shader includes read without a material property.
        private static readonly HashSet<string> GlobalTextures = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "_AudioTexture", "_CameraDepthTexture", "_CameraDepthNormalsTexture", "_GrabTexture",
            "_LightTexture0", "_LightTextureB0", "_ShadowMapTexture"
        };

        // For adapters pinned to an audited source: a texture slot saved from a previous shader (a stale
        // _MainTex, say) is bound only to a uniform the source reads, and these sources read no undeclared
        // texture except engine/AudioLink globals. Returns null when the property must be described normally.
        internal static SamplingDescription StaleProperty(string id, Material material, string property)
        {
            if (material.shader.FindPropertyIndex(property) >= 0 || GlobalTextures.Contains(property) ||
                property.StartsWith("unity_", System.StringComparison.Ordinal)) return null;
            return new SamplingDescription { AdapterId = id, Supported = true, NotSampled = true,
                Reason = "Not a property of this shader; the audited source never reads it." };
        }
    }

    // Records which animatable material values (floats, ints, vectors/colours, texture ST) an adapter
    // reads. Texture references, keywords and HasProperty are not float-animatable and are not recorded.
    internal static class MaterialInputs
    {
        [System.ThreadStatic] private static HashSet<string> active;

        // Inputs read so far by the adapter call being recorded (empty outside a recording).
        internal static IEnumerable<string> Current => active ?? (IEnumerable<string>)System.Array.Empty<string>();

        internal static SamplingDescription Record(System.Func<SamplingDescription> describe)
        {
            var previous = active;
            var inputs = new HashSet<string>(System.StringComparer.Ordinal);
            active = inputs;
            try
            {
                var result = describe();
                if (result != null) result.MaterialInputs = result.MaterialInputsUnknown ? null : inputs;
                return result;
            }
            finally { active = previous; }
        }

        internal static float Float(Material material, string property) { active?.Add(property); return material.GetFloat(property); }
        internal static int Int(Material material, string property) { active?.Add(property); return material.GetInteger(property); }
        internal static Vector4 Vector(Material material, string property) { active?.Add(property); return material.GetVector(property); }
        // Texture scale/offset are animated through the "<property>_ST" vector.
        internal static Vector2 Scale(Material material, string property) { active?.Add(property + "_ST"); return material.GetTextureScale(property); }
        internal static Vector2 Offset(Material material, string property) { active?.Add(property + "_ST"); return material.GetTextureOffset(property); }
    }

    // Built-in Standard and Standard (Specular setup), per UnityStandardInput.cginc (Built-in RP).
    // TexCoords(): xy = TRANSFORM_TEX(uv0, _MainTex); zw = TRANSFORM_TEX(_UVSec == 0 ? uv0 : uv1, _DetailAlbedoMap).
    // Main, alpha, metallic/specular, normal, occlusion, emission and detail-mask lookups use xy; detail
    // albedo and detail normal use zw. Every lookup is tex2D, i.e. the texture's own sampler. Parallax
    // offsets all coordinates by view direction, so parallax materials are retained.
    internal sealed class StandardShaderAdapter : ITextureSamplingAdapter
    {
        private const string Id = "unity-standard-v1";
        private static readonly Dictionary<string, TextureSemantics> MainUvFields = new Dictionary<string, TextureSemantics>
        {
            { "_MainTex", TextureSemantics.Color }, { "_EmissionMap", TextureSemantics.Color },
            { "_MetallicGlossMap", TextureSemantics.Data }, { "_SpecGlossMap", TextureSemantics.Data },
            { "_OcclusionMap", TextureSemantics.Data }, { "_DetailMask", TextureSemantics.Data },
            { "_BumpMap", TextureSemantics.Normal }
        };
        private static readonly Dictionary<string, TextureSemantics> DetailUvFields = new Dictionary<string, TextureSemantics>
        {
            { "_DetailAlbedoMap", TextureSemantics.Color }, { "_DetailNormalMap", TextureSemantics.Normal }
        };

        public bool Matches(Material material) => material.shader &&
            (material.shader.name == "Standard" || material.shader.name == "Standard (Specular setup)") &&
            UnityEditor.AssetDatabase.GetAssetPath(material.shader) == "Resources/unity_builtin_extra";

        public SamplingDescription Describe(Material material, string property)
        {
            if (material.IsKeywordEnabled("_PARALLAXMAP") || material.GetTexture("_ParallaxMap"))
                return ShaderAdapterRegistry.Unsupported(Id, "Standard parallax offsets every texture lookup by view direction; original retained.");
            SamplingDescription result;
            if (MainUvFields.TryGetValue(property, out var semantics))
                result = ShaderAdapterRegistry.UvPath(Id, semantics, 0, material, "_MainTex", croppable: true);
            else if (DetailUvFields.TryGetValue(property, out semantics))
                result = ShaderAdapterRegistry.UvPath(Id, semantics, MaterialInputs.Float(material, "_UVSec") == 0 ? 0 : 1, material, "_DetailAlbedoMap", croppable: true);
            else return ShaderAdapterRegistry.Unsupported(Id, property == "_ParallaxMap"
                ? "Standard height maps drive view-dependent parallax; original retained."
                : "Unverified Standard texture property; original retained.");
            result.Channels = Channels(material, property);
            return result;
        }

        // Channel reads per UnityStandardInput.cginc and UnityStandardShadow.cginc (2022.3). Albedo alpha is read
        // only for alpha test/blend/premultiply or as the smoothness source; opaque output is UNITY_OPAQUE_ALPHA.
        // Keywords select the compiled variant and cannot be animated.
        private static TextureChannels Channels(Material material, string property)
        {
            bool albedoSmoothness = material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            switch (property)
            {
                case "_MainTex":
                    return albedoSmoothness || material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                        material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ? TextureChannels.All : TextureChannels.RGB;
                case "_MetallicGlossMap": return albedoSmoothness ? TextureChannels.R : TextureChannels.R | TextureChannels.A;
                case "_SpecGlossMap": return albedoSmoothness ? TextureChannels.RGB : TextureChannels.All;
                case "_OcclusionMap": return TextureChannels.G;
                case "_EmissionMap": case "_DetailAlbedoMap": return TextureChannels.RGB;
                default: return TextureChannels.All; // _DetailMask reads alpha; normal maps keep their format.
            }
        }
    }

    internal sealed class KnownUnityShaderAdapter : ITextureSamplingAdapter
    {
        public bool Matches(Material material) => material.shader &&
            material.shader.name == "Unlit/Texture" &&
            UnityEditor.AssetDatabase.GetAssetPath(material.shader) == "Resources/unity_builtin_extra";

        public SamplingDescription Describe(Material material, string property)
        {
            if (property != "_MainTex") return ShaderAdapterRegistry.Unsupported("unity-unlit-v1", "Unverified texture property.");
            var result = ShaderAdapterRegistry.UvPath("unity-unlit-texture-v1", TextureSemantics.Color, 0, material, property, croppable: true);
            result.Channels = TextureChannels.RGB; // The fragment ends with UNITY_OPAQUE_ALPHA.
            return result;
        }
    }

}
