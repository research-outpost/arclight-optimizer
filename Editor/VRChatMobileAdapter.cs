using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // VRChat SDK mobile shaders (com.vrchat.base, Sample Assets/Shaders/Mobile), audited from SDK 3.10.5.
    // Each entry is pinned to a hash of its shader file and the include files it uses, so an SDK update that
    // changes one shader retains only that shader's textures until it is re-audited.
    //  - Toon Lit, Diffuse, MatCap Lit: _MainTex at UV0 with its _ST, own sampler. _MatCap is view-space.
    //  - Bumped Diffuse, both Bumped Mapped Specular files: as above; [NoScaleOffset] _BumpMap reuses
    //    the surface shader's uv_MainTex, i.e. _MainTex's _ST.
    //  - Standard Lite: main maps at UV0 with _MainTex's _ST; detail albedo/normal at UV0 or UV1 (_UVSec)
    //    with _DetailAlbedoMap's _ST; own samplers. No parallax.
    //  - Toon Standard (+ Outline): every map uses its own _ST and sampler (atlasing is compiled out).
    //    Masks, _MainTex and _BumpMap read UV0; emission and detail maps read UV0 or UV1 by selector;
    //    _AudioLinkMask reads UV0-UV3 through two selectors with _MainTex's sampler; the outline pass
    //    reads _OutlineMask at raw UV0 in the vertex stage. _Ramp and _Matcap are lookups.
    // Particle shaders only draw on particle systems, which are not scanned; world shaders are not listed.
    internal sealed class VRChatMobileAdapter : ITextureSamplingAdapter
    {
        internal const string Id = "vrchat-mobile-sdk3.10-static-v1";
        internal const string Root = "Packages/com.vrchat.base";
        internal const string Folder = Root + "/Runtime/VRCSDK/Sample Assets/Shaders/Mobile/";
        internal enum Kind { Main, MainBump, StandardLite, ToonStandard, ToonStandardOutline }

        private static readonly string[] ToonStandardSources = new[]
        {
            "CG/AudioLinkEffects.cginc", "CG/DataStructs.cginc", "CG/Definitions.cginc", "CG/Helpers.cginc", "CG/Lighting.cginc",
            "CG/Outlines.cginc", "CG/VertexFragment.cginc", "CG/VRCAtlasingShim.cginc", "Dependencies/AudioLink.cginc"
        }.Select(file => "ToonStandard/" + file).ToArray();

        // Shader file (relative to Folder) -> model, source files hashed with it, audited hash.
        internal static readonly Dictionary<string, (Kind, string[], string)> Audited = new Dictionary<string, (Kind, string[], string)>(StringComparer.Ordinal)
        {
            { "VRChat-Mobile-ToonLit.shader", (Kind.Main, new string[0], "e5159c81759e2e0840fde9e936d69649419166860d790ad3253b792415cb3084") },
            { "VRChat-Mobile-Diffuse.shader", (Kind.Main, new[] { "VRChat.cginc" }, "8a81f18144fa0b556d55d70d271e0ed75cb37cea2410cf85f8737115ab9717fb") },
            { "VRChat-Mobile-MatCapLit.shader", (Kind.Main, new string[0], "8a821506c5d5008862aeba8e35bd79c90465bc20e6c421a69c99dfea9adc9d57") },
            { "VRChat-Mobile-BumpedDiffuse.shader", (Kind.MainBump, new[] { "VRChat.cginc" }, "5af22d8600b1a879356f4f61959a212f06cd2ce8195fed9eb967e619d3c48460") },
            { "VRChat-Mobile-BumpedMappedSpecular.shader", (Kind.MainBump, new string[0], "889dae94aaac6e43156ddd3ba34930ebe4dd924044d59505cdb5dadf6bfd9909") },
            { "VRChat-Mobile-BumpedSpecular.shader", (Kind.MainBump, new string[0], "d3ac29bb9462e42efdf15de097f422d7bb78f91e3ff706670977b3c8613b27c4") },
            { "VRChat-Mobile-StandardLite.shader", (Kind.StandardLite, new[] { "VRChat.cginc" }, "60e2b2bb2443dd1af7ba43a540c61454ae241581475851e8da03797a8622ffa8") },
            { "ToonStandard/ToonStandard.shader", (Kind.ToonStandard, ToonStandardSources, "02a4e38b292620043c10a6b58579888580f4140cff0e8d806504a24bd5cb55cf") },
            { "ToonStandard/ToonStandardOutline.shader", (Kind.ToonStandardOutline, ToonStandardSources, "844d5487b780368ce163daa2cffb94c334fcdc530de47148fa8573ee2e8732a3") },
        };

        private static readonly HashSet<string> OwnUv0Data = new HashSet<string>(StringComparer.Ordinal)
            { "_HueShiftMask", "_OcclusionMap", "_DetailMask", "_MetallicMap", "_GlossMap", "_MatcapMask", "_ColorMask" };

        public bool Matches(Material material) => material.shader && FileOf(material.shader) != null;

        private static string FileOf(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            if (!path.StartsWith(Folder, StringComparison.Ordinal)) return null;
            string file = path.Substring(Folder.Length);
            return Audited.ContainsKey(file) ? file : null;
        }

        public SamplingDescription Describe(Material material, string property)
        {
            var result = DescribeSampling(material, property);
            if (result.Supported && !result.NotSampled) result.Channels = Channels(material, property);
            return result;
        }

        // Toon Standard masks read through SAMPLE_MASK (or the outline's tex2Dlod) as tex[<name>Channel].
        private static readonly HashSet<string> ToonStandardSelectedMasks = new HashSet<string>(StringComparer.Ordinal)
            { "_OcclusionMap", "_DetailMask", "_MetallicMap", "_GlossMap", "_MatcapMask", "_HueShiftMask", "_OutlineMask" };

        // Channel reads in the pinned 3.10.5 sources. Toon Lit and MatCap Lit return alpha 1; Diffuse, Bumped
        // Diffuse and Standard Lite are opaque surface shaders whose generated output uses UNITY_OPAQUE_ALPHA.
        // Bumped (Mapped) Specular read _MainTex alpha as gloss. Toon Standard compiles no alpha keyword (its
        // shader_feature line is commented out), so GetAlpha returns 1 and main alpha is never read; its masks read
        // the channel their selector picks. _DetailAlbedoMap (alpha blends detail), _ColorMask (four channels) and
        // _AudioLinkMask (whole-texel modes) keep every channel.
        private static TextureChannels Channels(Material material, string property)
        {
            switch (FileOf(material.shader))
            {
                case "ToonStandard/ToonStandard.shader": case "ToonStandard/ToonStandardOutline.shader":
                    if (property == "_MainTex" || property == "_EmissionMap") return TextureChannels.RGB;
                    if (!ToonStandardSelectedMasks.Contains(property) || !material.HasProperty(property + "Channel")) return TextureChannels.All;
                    int channel = Selector(material, property + "Channel");
                    return channel >= 0 && channel <= 3 ? PoiyomiAdapter.ChannelOf(channel) : TextureChannels.All;
                case "VRChat-Mobile-ToonLit.shader": case "VRChat-Mobile-MatCapLit.shader":
                case "VRChat-Mobile-Diffuse.shader": case "VRChat-Mobile-BumpedDiffuse.shader":
                    return property == "_MainTex" ? TextureChannels.RGB : TextureChannels.All;
                case "VRChat-Mobile-StandardLite.shader":
                    switch (property)
                    {
                        case "_MainTex": case "_EmissionMap": case "_DetailAlbedoMap": return TextureChannels.RGB;
                        case "_MetallicGlossMap": return TextureChannels.R | TextureChannels.A;
                        case "_OcclusionMap": return TextureChannels.G;
                        default: return TextureChannels.All; // _DetailMask reads alpha; normal maps keep their format.
                    }
                default: return TextureChannels.All;
            }
        }

        private SamplingDescription DescribeSampling(Material material, string property)
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline)
                return Unsupported("VRChat mobile shaders are supported on the Built-in Render Pipeline only.");
            string file = FileOf(material.shader);
            string failure = SourceFailure(file);
            if (failure != null) return Unsupported(failure);
            var stale = ShaderAdapterRegistry.StaleProperty(Id, material, property);
            if (stale != null) return stale;
            var (kind, _, _) = Audited[file];
            if (property == "_MainTex") return UvPath(TextureSemantics.Color, 0, material, property);
            switch (kind)
            {
                case Kind.Main:
                    break;
                case Kind.MainBump:
                    if (property == "_BumpMap") return UvPath(TextureSemantics.Normal, 0, material, "_MainTex");
                    break;
                case Kind.StandardLite:
                    if (property == "_MetallicGlossMap" || property == "_OcclusionMap" || property == "_DetailMask")
                        return UvPath(TextureSemantics.Data, 0, material, "_MainTex");
                    if (property == "_BumpMap") return UvPath(TextureSemantics.Normal, 0, material, "_MainTex");
                    if (property == "_EmissionMap") return UvPath(TextureSemantics.Color, 0, material, "_MainTex");
                    if (property == "_DetailAlbedoMap" || property == "_DetailNormalMap")
                        return UvPath(property == "_DetailAlbedoMap" ? TextureSemantics.Color : TextureSemantics.Normal,
                            MaterialInputs.Float(material, "_UVSec") == 0 ? 0 : 1, material, "_DetailAlbedoMap");
                    break;
                case Kind.ToonStandard:
                case Kind.ToonStandardOutline:
                    if (OwnUv0Data.Contains(property)) return UvPath(TextureSemantics.Data, 0, material, property);
                    if (property == "_BumpMap") return UvPath(TextureSemantics.Normal, 0, material, property);
                    // Selectors are uints: 0 picks UV0, any other value UV1 (UV3 for the 4-way AudioLink ones).
                    if (property == "_EmissionMap" || property == "_DetailAlbedoMap" || property == "_DetailNormalMap")
                    {
                        int selected = Selector(material, property == "_EmissionMap" ? "_EmissionUV" : "_DetailUV");
                        if (selected < 0) return Unsupported(InvalidSelector);
                        return UvPath(property == "_EmissionMap" || property == "_DetailAlbedoMap" ? TextureSemantics.Color : TextureSemantics.Normal,
                            selected == 0 ? 0 : 1, material, property);
                    }
                    if (property == "_AudioLinkMask")
                    {
                        var main = material.GetTexture("_MainTex");
                        var channels = new[] { Selector(material, "_ALMaskUVChannel"), Selector(material, "_ALEffectUVChannel") };
                        if (channels.Any(channel => channel < 0)) return Unsupported(InvalidSelector);
                        var paths = channels.Select(channel => Math.Min(channel, 3)).Distinct()
                            .Select(uv => new SamplingPath { UvChannel = uv, Scale = MaterialInputs.Scale(material, property),
                                Offset = MaterialInputs.Offset(material, property), SamplerTexture = main, FixedRepeat = !main,
                                Label = "Toon Standard AudioLink mask UV" + uv }).ToList();
                        return new SamplingDescription { AdapterId = Id, Supported = true, Semantics = TextureSemantics.Data, Paths = paths };
                    }
                    if (property == "_OutlineMask" && kind == Kind.ToonStandardOutline)
                        return Supported(TextureSemantics.Data, new SamplingPath { UvChannel = 0, Label = "Toon Standard outline mask raw UV0" });
                    break;
            }
            return Unsupported(property.IndexOf("matcap", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Matcaps are sampled by view-space normals; original retained."
                : property == "_Ramp" ? "The shadow ramp is a lookup texture; original retained."
                : "Unverified VRChat mobile shader texture property; original retained.");
        }

        private const string InvalidSelector = "A Toon Standard UV selector is negative or fractional, which the shader's uint conversion leaves undefined; original retained.";

        // ShaderLab "Int" properties are stored as floats, "Integer" ones as ints. -1 when the value is not
        // a non-negative whole number.
        private static int Selector(Material material, string property)
        {
            int index = material.shader.FindPropertyIndex(property);
            if (index >= 0 && material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Int)
                return Math.Max(MaterialInputs.Int(material, property), -1);
            float value = MaterialInputs.Float(material, property);
            return value >= 0 && value == Mathf.Floor(value) && value < int.MaxValue ? (int)value : -1;
        }

        // Fingerprint of a shader file and its includes, line endings normalized; null if unreadable.
        internal static string SourceHash(string file)
        {
            try
            {
                string root = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Root + "/package.json")?.resolvedPath ?? Root;
                string folder = Path.Combine(root, Folder.Substring(Root.Length + 1));
                var (_, sources, _) = Audited[file];
                var text = new StringBuilder();
                foreach (string source in new[] { file }.Concat(sources))
                    text.Append(source).Append('\n').Append(File.ReadAllText(LongPath.For(Path.Combine(folder, source))).Replace("\r\n", "\n")).Append('\n');
                return FingerprintService.Hash(Encoding.UTF8.GetBytes(text.ToString()));
            }
            catch (Exception) { return null; }
        }

        // Toon Standard declares these textures only inside shader_feature blocks (CG/Definitions.cginc lines 1-28); with the
        // keyword off a texture is not declared, so it is never read. Material keywords cannot be animated.
        internal static readonly (string Keyword, string[] Textures)[] ToonStandardKeywordSlots =
        {
            ("USE_OCCLUSION_MAP", new[] { "_OcclusionMap" }), // USE_EMISSION_MAP is #defined in the forward pass, so _EmissionMap is always read.
            ("USE_NORMAL_MAPS", new[] { "_BumpMap", "_DetailNormalMap" }), ("USE_SPECULAR", new[] { "_MetallicMap", "_GlossMap" }),
            ("USE_DETAIL_MAPS", new[] { "_DetailAlbedoMap", "_DetailMask", "_DetailNormalMap" }), ("USE_MATCAP", new[] { "_Matcap", "_MatcapMask" }),
            ("USE_AUDIOLINK", new[] { "_AudioLinkMask" }),
        };

        internal static bool IsAuditedToonStandard(Material material) => material.shader && FileOf(material.shader) is string file &&
            (Audited[file].Item1 == Kind.ToonStandard || Audited[file].Item1 == Kind.ToonStandardOutline) && SourceFailure(file) == null;

        private static string SourceFailure(string file) => ShaderAdapterRegistry.PinnedSourceFailure(Folder + file, Audited[file].Item3,
            () => SourceHash(file), "This VRChat SDK's " + Path.GetFileNameWithoutExtension(file) + " shader differs from the audited SDK 3.10.5 source; original retained.");

        private static SamplingDescription UvPath(TextureSemantics semantics, int uv, Material material, string stProperty) =>
            ShaderAdapterRegistry.UvPath(Id, semantics, uv, material, stProperty);

        private static SamplingDescription Supported(TextureSemantics semantics, SamplingPath path) => new SamplingDescription
        {
            AdapterId = Id, Supported = true, Semantics = semantics, Paths = new List<SamplingPath> { path }
        };

        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
    }
}
