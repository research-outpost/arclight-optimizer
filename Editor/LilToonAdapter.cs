using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    internal static class LilToonSourceGuard
    {
        internal const string Root = "Packages/jp.lilxyzw.liltoon";
        private static bool checkedSources;
        private static string failure;
        internal static string SourceFingerprint { get; private set; }
        public static void BeginScan() { checkedSources = false; failure = null; SourceFingerprint = null; }

        public static string Validate()
        {
            if (checkedSources) return failure;
            checkedSources = true;
            string diskRoot = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Root + "/package.json")?.resolvedPath ?? Root;
            failure = ValidatePackage(diskRoot, out var fingerprint);
            SourceFingerprint = fingerprint;
            return failure;
        }

        internal static bool IsCompatibleVersion(string version) => version != null &&
            System.Text.RegularExpressions.Regex.IsMatch(version,
                @"\A2\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z");

        // Hashes invalidate generated outputs; they never reject a modified 2.x package.
        internal static string ValidatePackage(string diskRoot, out string fingerprint)
        {
            fingerprint = null;
            try
            {
                string normalizedRoot = Path.GetFullPath(diskRoot).Replace('\\', '/').TrimEnd('/');
                var package = JsonUtility.FromJson<PackageVersion>(File.ReadAllText(LongPath.For(normalizedRoot + "/package.json")));
                if (package == null || package.name != "jp.lilxyzw.liltoon")
                    return "Could not identify the installed lilToon package; original texture retained.";
                if (!IsCompatibleVersion(package.version))
                    return "Only lilToon 2.x.x is supported; installed version is " + (package.version ?? "unknown") + ". Original texture retained.";
                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".shader", ".hlsl", ".lilblock", ".lilcontainer", ".lilinternal", ".cs" };
                var lines = Directory.GetFiles(normalizedRoot, "*", SearchOption.AllDirectories)
                    .Where(p => extensions.Contains(Path.GetExtension(p))).Select(p =>
                        p.Replace('\\', '/').Substring(normalizedRoot.Length + 1) + "=" + FingerprintService.Hash(
                            Encoding.UTF8.GetBytes(File.ReadAllText(LongPath.For(p)).Replace("\r\n", "\n"))))
                    .OrderBy(s => s, StringComparer.Ordinal);
                fingerprint = FingerprintService.Hash(Encoding.UTF8.GetBytes(package.version + "\n" + string.Join("\n", lines)));
                return null;
            }
            catch (Exception e) { return "Cannot read lilToon package information: " + e.Message; }
        }
        [Serializable] private sealed class PackageVersion { public string name; public string version; }
    }

    internal sealed class LilToonAdapter : ITextureSamplingAdapter
    {
        private const string Id = "liltoon-2.x-static-v9";
        // Every material entry in the package's Shader folder (not the ltspass_/ltsother helper passes).
        // Standard, one/two-pass transparent, overlay and outline-only (_oo) entries all use the same
        // ltspass_opaque/cutout/transparent passes; tessellation (lts_tess*) adds only barycentric UV
        // interpolation and position displacement. Lite, fur, gem, refraction and fake-shadow entries use
        // the same field macros with the differences modeled below. See Documentation~/LILTOON.md.
        internal static readonly HashSet<string> Variants = new HashSet<string>(
            ("lts lts_o lts_oo lts_cutout lts_cutout_o lts_cutout_oo lts_trans lts_trans_o lts_trans_oo " +
             "lts_onetrans lts_onetrans_o lts_twotrans lts_twotrans_o lts_overlay lts_overlay_one " +
             "lts_tess lts_tess_o lts_tess_cutout lts_tess_cutout_o lts_tess_trans lts_tess_trans_o " +
             "lts_tess_onetrans lts_tess_onetrans_o lts_tess_twotrans lts_tess_twotrans_o " +
             "ltsl ltsl_o ltsl_cutout ltsl_cutout_o ltsl_trans ltsl_trans_o ltsl_onetrans ltsl_onetrans_o " +
             "ltsl_twotrans ltsl_twotrans_o ltsl_overlay ltsl_overlay_one " +
             "lts_fur lts_fur_cutout lts_fur_two lts_furonly lts_furonly_cutout lts_furonly_two " +
             "lts_gem lts_ref lts_ref_blur lts_fakeshadow " +
             "ltsmulti ltsmulti_o ltsmulti_fur ltsmulti_gem ltsmulti_ref")
            .Split(' ').Select(name => name + ".shader"));

        // Narrow compatibility inference for standard lilSSAO entries; no blanket custom-shader acceptance.
        internal static bool TryGetStandardVariant(string assetPath, string shaderName, out string file)
        {
            file = Path.GetFileName(assetPath);
            if (assetPath == LilToonSourceGuard.Root + "/Shader/" + file && Variants.Contains(file))
                return true;
            if (string.IsNullOrEmpty(assetPath) ||
                !(assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath.StartsWith("Packages/", StringComparison.Ordinal)) ||
                Path.GetExtension(assetPath) != ".lilcontainer")
                return false;
            string entry = Path.GetFileNameWithoutExtension(assetPath);
            string expected;
            switch (entry)
            {
                case "lts": expected = "lilToon/lilSSAO/lilToon"; break;
                case "lts_o": expected = "Hidden/lilToon/lilSSAO/OpaqueOutline"; break;
                case "lts_cutout": expected = "Hidden/lilToon/lilSSAO/Cutout"; break;
                case "lts_cutout_o": expected = "Hidden/lilToon/lilSSAO/CutoutOutline"; break;
                case "lts_trans": expected = "Hidden/lilToon/lilSSAO/Transparent"; break;
                case "lts_trans_o": expected = "Hidden/lilToon/lilSSAO/TransparentOutline"; break;
                default: return false;
            }
            if (shaderName != expected) return false;
            file = entry + ".shader";
            return true;
        }

        // Every texture declaration in the standard 2.x entries has an explicit classification.
        // Unknown/custom additions are still discovered by Material.GetTexturePropertyNames().
        internal enum Coordinates { Main, MainThenOwn, RawOwn, SelectedRawOwn, SelectedRawFixed, SelectedMainOwn, EmissionMask, FixedMain, Outline, Alias, Unsupported }
        internal sealed class Field
        {
            public Coordinates Coordinates;
            public TextureSemantics Semantics;
            public string Reason;
        }
        internal static readonly Dictionary<string, Field> Fields = CreateFields();
        private static Dictionary<string, Field> CreateFields()
        {
            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            void Add(Coordinates coordinates, TextureSemantics semantics, string names, string reason = null)
            {
                foreach (string name in names.Split(' '))
                    fields.Add(name, new Field { Coordinates = coordinates, Semantics = semantics, Reason = reason });
            }
            Add(Coordinates.Main, TextureSemantics.Color, "_MainTex _ShadowColorTex _Shadow2ndColorTex _Shadow3rdColorTex");
            Add(Coordinates.Main, TextureSemantics.Data, "_MainColorAdjustMask _Main2ndBlendMask _Main3rdBlendMask _RimShadeMask _TriMask _FurMask");
            Add(Coordinates.MainThenOwn, TextureSemantics.Color, "_RimColorTex _BacklightColorTex _ReflectionColorTex");
            Add(Coordinates.MainThenOwn, TextureSemantics.Data, "_AlphaMask _MatCapBlendMask _MatCap2ndBlendMask _Bump2ndScaleMask _AnisotropyScaleMask _AnisotropyShiftNoiseMask _SmoothnessTex _MetallicGlossMap");
            Add(Coordinates.FixedMain, TextureSemantics.Data, "_OutlineWidthMask _ShadowStrengthMask _ShadowBorderMask _ShadowBlurMask _FurLengthMask");
            Add(Coordinates.SelectedRawOwn, TextureSemantics.Color, "_Main2ndTex _Main3rdTex _EmissionMap _Emission2ndMap");
            Add(Coordinates.SelectedMainOwn, TextureSemantics.Color, "_GlitterColorTex");
            Add(Coordinates.SelectedMainOwn, TextureSemantics.Data, "_AudioLinkMask");
            Add(Coordinates.EmissionMask, TextureSemantics.Data, "_EmissionBlendMask _Emission2ndBlendMask");
            Add(Coordinates.RawOwn, TextureSemantics.Data, "_DissolveMask _DissolveNoiseMask _Main2ndDissolveMask _Main2ndDissolveNoiseMask _Main3rdDissolveMask _Main3rdDissolveNoiseMask _FurNoiseMask");
            Add(Coordinates.Outline, TextureSemantics.Color, "_OutlineTex");
            Add(Coordinates.Alias, TextureSemantics.Color, "_BaseMap _BaseColorMap");
            Add(Coordinates.MainThenOwn, TextureSemantics.Normal, "_BumpMap");
            Add(Coordinates.SelectedRawFixed, TextureSemantics.Normal, "_Bump2ndMap");
            Add(Coordinates.Unsupported, TextureSemantics.Data, "_AnisotropyTangentMap _MatCapBumpMap _MatCap2ndBumpMap _OutlineVectorTex _FurVectorTex",
                "Normal/tangent-vector field requires separate sampling and encoding validation; only the first and second bump maps are supported. Original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Color, "_MatCapTex _MatCap2ndTex",
                "Matcap images use view/normal-dependent sampling rather than static mesh UV coverage; original retained. Matcap blend masks are supported separately.");
            Add(Coordinates.Unsupported, TextureSemantics.Color, "_MainGradationTex _EmissionGradTex _Emission2ndGradTex _Ramp",
                "Colour/lighting lookup texture: coordinates come from colour, lighting or time rather than mesh UVs; original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Data, "_AudioLinkLocalMap",
                "AudioLink lookup data uses time/signal coordinates rather than mesh UVs; original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Data, "_DitherTex",
                "Dither texture uses screen-space coordinates; original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Color, "_ReflectionCubeTex",
                "Environment cubemap uses directional sampling and cannot be represented as an ordinary replacement PNG; original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Color, "_GlitterShapeTex",
                "Glitter shape uses procedural randomized/atlas coordinates rather than direct mesh UV coverage; original retained.");
            Add(Coordinates.Unsupported, TextureSemantics.Data, "_ParallaxMap",
                "Parallax/POM lookup and displaced sampling are outside the static coverage model; original retained.");
            return fields;
        }

        public bool Matches(Material material) => material.shader &&
            material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;

        public SamplingDescription Describe(Material material, string property) =>
            DescribeWithSourceShader(material, property, material.shader);

        internal SamplingDescription DescribeWithSourceShader(Material material, string property, Shader sourceShader)
        {
            string path = AssetDatabase.GetAssetPath(sourceShader);
            if (!TryGetStandardVariant(path, sourceShader.name, out string file))
                return Unsupported("Unsupported lilToon shader entry. Standard 2.x opaque, cutout and transparent entries, standard Multi entries and recognized lilSSAO containers, are supported.");
            bool inferredSsao = Path.GetExtension(path) == ".lilcontainer";
            if (inferredSsao && !SpsLilToonAdapter.HasLilToonInventory(material))
                return Unsupported("The lilSSAO container does not expose the expected lilToon property inventory; original retained.");
            string inference = inferredSsao
                ? "lilSSAO container recognized; assumes standard lilToon static UV conventions for inherited fields. "
                : "";
            bool multi = file.StartsWith("ltsmulti", StringComparison.Ordinal);
            if (multi)
            {
                float mode = MaterialInputs.Float(material, "_TransparentMode");
                // Opaque|Cutout|Transparent|Refraction|Fur|FurCutout|Gem. The entry file selects the sampling family.
                if (float.IsNaN(mode) || mode < 0 || mode > 6 || mode != Mathf.Floor(mode))
                    return Unsupported("lilToon Multi has an invalid rendering mode; original retained.");
                // Multi compiles these features from keywords and overrides the corresponding _Use toggles.
                // Guard stale/animated keywords too, even if the stored material toggle reads zero.
                if (material.IsKeywordEnabled("_PARALLAXMAP") || material.IsKeywordEnabled("PIXELSNAP_ON") ||
                    material.IsKeywordEnabled("_MAPPING_6_FRAMES_LAYOUT") || material.IsKeywordEnabled("_SUNDISK_HIGH_QUALITY"))
                    return Unsupported("lilToon Multi parallax/POM or AudioLink keywords enable sampling outside the static UV model; original retained.");
            }
            if (GraphicsSettings.currentRenderPipeline) return Unsupported("This lilToon adapter supports the Built-in Render Pipeline only.");
            string failure = LilToonSourceGuard.Validate();
            if (failure != null) return Unsupported(failure);
            string compatibilityId = Id + (inferredSsao ? "/lilssao-inventory-v1" : "") + "/" + LilToonSourceGuard.SourceFingerprint;
            if (!Fields.TryGetValue(property, out var field))
                return Unsupported("Custom or unrecognized lilToon texture field " + property + "; discovered, but its sampling is not modeled.");
            if (field.Coordinates == Coordinates.Alias)
                return new SamplingDescription { AdapterId = compatibilityId, Supported = true, NotSampled = true,
                    Reason = inference + "Compatibility alias; treated as unused by the standard lilToon 2.x Built-in sampling model." };
            if (field.Coordinates == Coordinates.Unsupported) return Unsupported(field.Reason);
            bool outline = file.EndsWith("_o.shader", StringComparison.Ordinal) || file.EndsWith("_oo.shader", StringComparison.Ordinal);
            // Gem reads _SmoothnessTex at the main UV only; refraction blur reads it with a fixed Repeat sampler.
            bool gem = file == "lts_gem.shader" || file == "ltsmulti_gem.shader";
            bool refractionBlur = file == "lts_ref_blur.shader";

            if (property != "_AudioLinkMask" && Enabled(material, "_UseAudioLink")) return Unsupported("AudioLink-dependent material settings are not yet certified.");
            if (Enabled(material, "_UseParallax") || Enabled(material, "_UsePOM"))
                return Unsupported("Parallax/POM can move texture sampling outside static mesh UVs.");
            if (Enabled(material, "_ShiftBackfaceUV")) return Unsupported("Backface UV shifting requires an additional sampling model; retained.");
            // FakeShadow has no scroll/rotate properties.
            if (material.HasProperty("_MainTex_ScrollRotate") && MaterialInputs.Vector(material, "_MainTex_ScrollRotate") != Vector4.zero)
                return Unsupported("Main UV scrolling/rotation is outside this static lilToon subset.");
            if (outline && MaterialInputs.Vector(material, "_OutlineTex_ScrollRotate") != Vector4.zero)
                return Unsupported("Outline UV scrolling/rotation is outside this static lilToon subset.");

            if (property == "_ShadowStrengthMask" && (Enabled(material, "_ShadowStrengthMaskLOD") || Enabled(material, "_ShadowMaskType")))
                return Unsupported("Shadow mask forced gradients/SDF modes are not yet verified; retained.");

            if ((property == "_ShadowBorderMask" || property == "_ShadowBlurMask") && Enabled(material, property + "LOD"))
                return Unsupported("Shadow mask forced gradients are not yet modeled; original retained.");

            int channel = 0;
            if (field.Coordinates == Coordinates.SelectedRawOwn || field.Coordinates == Coordinates.SelectedRawFixed || field.Coordinates == Coordinates.SelectedMainOwn)
            {
                float mode = MaterialInputs.Float(material, property + "_UVMode");
                if (float.IsNaN(mode) || mode < 0 || mode > 3 || mode != Mathf.Floor(mode))
                    return Unsupported(property + " requires static mesh UV0-UV3; view/rim/matcap and invalid UV modes are retained.");
                channel = (int)mode;
                if (material.HasProperty(property + "_ScrollRotate") && MaterialInputs.Vector(material, property + "_ScrollRotate") != Vector4.zero)
                    return Unsupported(property + " UV scrolling/rotation is outside the static sampling model.");
            }
            if ((property == "_EmissionMap" && Enabled(material, "_EmissionParallaxDepth")) ||
                (property == "_Emission2ndMap" && Enabled(material, "_Emission2ndParallaxDepth")))
                return Unsupported("Emission parallax can move texture sampling outside static mesh UVs.");
            if ((field.Coordinates == Coordinates.EmissionMask || property.EndsWith("DissolveNoiseMask", StringComparison.Ordinal)) &&
                MaterialInputs.Vector(material, property + "_ScrollRotate") != Vector4.zero)
                return Unsupported("Mask UV scrolling/rotation is outside the static sampling model.");
            if (property == "_Main2ndTex" || property == "_Main3rdTex")
            {
                foreach (string suffix in new[] { "Angle", "IsLeftOnly", "IsRightOnly", "ShouldCopy",
                    "ShouldFlipMirror", "ShouldFlipCopy", "IsMSDF" })
                    if (Enabled(material, property + suffix))
                        return Unsupported("Main layer texture uses rotation, mirror/copy or MSDF settings outside the static subset.");
                // Atlas sampling can run even when IsDecal is false. Only the default identity atlas is modeled.
                if (MaterialInputs.Vector(material, property + "DecalAnimation") != new Vector4(1, 1, 1, 30) ||
                    MaterialInputs.Vector(material, property + "DecalSubParam") != new Vector4(1, 1, 0, 1))
                    return Unsupported("Main layer texture has non-default atlas/animation settings; original retained.");
                Vector2 layerScale = MaterialInputs.Scale(material, property);
                if (layerScale.x == 0 || layerScale.y == 0)
                    return Unsupported("Main layer texture has a zero tiling axis; decal shader variants divide by this scale.");
            }

            var result = new SamplingDescription { AdapterId = compatibilityId, Supported = true,
                Semantics = field.Semantics, Reason = inference,
                Paths = new List<SamplingPath>() };
            var source = material.GetTexture(property);
            var mainSampler = material.GetTexture("_MainTex");
            if (!source || !mainSampler) return Unsupported("A concrete assigned main texture/sampler is required.");

            if (field.Coordinates == Coordinates.SelectedRawOwn)
                Add(result, MaterialInputs.Scale(material, property), MaterialInputs.Offset(material, property), source,
                    "Selected raw mesh UV / own sampler", channel: channel);
            else if (field.Coordinates == Coordinates.SelectedRawFixed)
            {
                // OVERRIDE_NORMAL_2ND uses raw UV0-UV3, never the main texture transform.
                Vector2 scale = MaterialInputs.Scale(material, property);
                Vector2 offset = MaterialInputs.Offset(material, property);
                Add(result, scale, offset, null, "Second bump / fixed Repeat", true, channel);
                Add(result, scale, offset, source, "Second bump / legacy sampler", channel: channel);
            }
            else if (field.Coordinates == Coordinates.SelectedMainOwn)
            {
                var samplers = property == "_AudioLinkMask" ? new[] { source } : new[] { mainSampler, source }.Distinct();
                foreach (var sampler in samplers)
                    if (channel == 0)
                        AddComposed(result, material, "_MainTex", MaterialInputs.Scale(material, property),
                            MaterialInputs.Offset(material, property), sampler, "Selected main UV0 / property ST");
                    else
                        Add(result, MaterialInputs.Scale(material, property), MaterialInputs.Offset(material, property),
                            sampler, "Selected raw UV / property ST", channel: channel);
            }
            else if (field.Coordinates == Coordinates.RawOwn)
            {
                foreach (var sampler in new[] { mainSampler, source }.Distinct())
                    Add(result, MaterialInputs.Scale(material, property), MaterialInputs.Offset(material, property),
                        sampler, "Dissolve raw UV0 / property ST");
            }
            else if (field.Coordinates == Coordinates.EmissionMask)
            {
                // ANIMATE_EMISSION_MASK_UV changes the basis even with zero scroll/rotation.
                // Union both compiled feature paths rather than relying on project shader settings.
                foreach (var sampler in new[] { mainSampler, source }.Distinct())
                {
                    Add(result, MaterialInputs.Scale(material, property), MaterialInputs.Offset(material, property), sampler,
                        "Emission mask / raw UV0 feature path");
                    AddComposed(result, material, "_MainTex", MaterialInputs.Scale(material, property),
                        MaterialInputs.Offset(material, property), sampler, "Emission mask / main UV feature path");
                }
            }
            else if (field.Coordinates == Coordinates.FixedMain)
            {
                // lil_common_vert computes uvMain using _MainTex_ST only. lilGetOutlineWidth samples LOD 0
                // with lil_sampler_linear_repeat; it does NOT apply _OutlineWidthMask_ST.
                // ShadowStrengthMask also uses main UVs and fixed Repeat, ignoring its own ST.
                Add(result, MaterialInputs.Scale(material, "_MainTex"), MaterialInputs.Offset(material, "_MainTex"), null,
                    property == "_OutlineWidthMask" ? "Outline vertex LOD0 / fixed Repeat" : "Shadow mask / fixed Repeat", true);
                Add(result, MaterialInputs.Scale(material, "_MainTex"), MaterialInputs.Offset(material, "_MainTex"), source,
                    "Legacy sampler fallback");
            }
            else if (property == "_OutlineTex")
                Add(result, MaterialInputs.Scale(material, property), MaterialInputs.Offset(material, property), source, "Outline passes");
            else
            {
                // Rim shade samples uvMain directly; unlike reflection colour, it ignores its own ST.
                bool ignoreOwnTransform = field.Coordinates == Coordinates.Main;
                Vector2 extraScale = ignoreOwnTransform ? Vector2.one : MaterialInputs.Scale(material, property);
                Vector2 extraOffset = ignoreOwnTransform ? Vector2.zero : MaterialInputs.Offset(material, property);
                AddComposed(result, material, "_MainTex", extraScale, extraOffset, mainSampler, "Main / shadow / meta");
                // Include the source's own sampler too: legacy texture macros can ignore the named sampler.
                if (source != mainSampler) AddComposed(result, material, "_MainTex", extraScale, extraOffset, source, "Legacy sampler fallback");
                if (property == "_SmoothnessTex" && gem)
                    foreach (var sampler in new[] { mainSampler, source }.Distinct())
                        AddComposed(result, material, "_MainTex", Vector2.one, Vector2.zero, sampler, "Gem smoothness / main UV");
                if (property == "_SmoothnessTex" && refractionBlur)
                    AddComposed(result, material, "_MainTex", extraScale, extraOffset, null, "Refraction blur smoothness / fixed Repeat", true);
                if (property == "_AlphaMask" && outline)
                {
                    AddComposed(result, material, "_OutlineTex", extraScale, extraOffset, mainSampler, "Outline alpha / shadow");
                    if (source != mainSampler) AddComposed(result, material, "_OutlineTex", extraScale, extraOffset, source, "Outline legacy sampler fallback");
                }
            }
            result.Reason = inference + "lilToon 2.x static mesh-UV compatibility model; disabled feature paths are conservatively retained in coverage.";
            return result;
        }

        private static bool Enabled(Material material, string property) => material.HasProperty(property) && MaterialInputs.Float(material, property) != 0;
        private static SamplingDescription Unsupported(string reason) => ShaderAdapterRegistry.Unsupported(Id, reason);
        private static void AddComposed(SamplingDescription result, Material material, string basis, Vector2 scale, Vector2 offset, Texture sampler, string label, bool repeat = false) =>
            Add(result, Vector2.Scale(MaterialInputs.Scale(material, basis), scale), Vector2.Scale(MaterialInputs.Offset(material, basis), scale) + offset, sampler, label, repeat);
        private static void Add(SamplingDescription result, Vector2 scale, Vector2 offset, Texture sampler, string label, bool repeat = false, int channel = 0) =>
            result.Paths.Add(new SamplingPath { UvChannel = channel, Scale = scale, Offset = offset, SamplerTexture = sampler, FixedRepeat = repeat, Label = label });
    }
}
