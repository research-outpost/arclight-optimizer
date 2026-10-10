using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // A material keeps every texture it was ever given, including slots its current shader does not declare (left over
    // from a previous shader), and keywords its shader does not have. The shader can never read either, but the upload
    // still carries the textures, and the leftovers make otherwise identical materials look different to merging.
    // Each material the avatar can show (renderer slots and animation swaps) that holds such data is replaced by a
    // build copy without it; source materials are never edited. Also cleared, because nothing can sample them:
    //  - on audited lilToon entries, textures whose feature switch is off and no animation can turn on (every lookup of
    //    these slots sits inside the switch's branch in lilToon 2.3.4's lil_common_frag.hlsl);
    //  - on materials whose shader is missing (drawn with Unity's error shader), every texture, fallback slots included unless it is swapped with a material that is not pink.
    // Otherwise VRChat's fallback slots are always kept, so nothing the shader or the fallback can sample or compile changes.
    // Undeclared textures go for shaders whose source is audited (lilToon entries, Unity's built-ins, VRChat's mobile
    // shaders), and on PC for any other shader when no compiled variant binds them (VertexStreamStripper.Uniforms). Materials an excluded renderer or a renderer with a property block uses are left alone.
    internal static class StaleTextureCleaner
    {
        // VRChat's shader fallback (what others see with shaders blocked) reads these slots by name, whatever the shader
        // declares, so they are never cleared.
        internal static readonly HashSet<string> FallbackSlots = new HashSet<string>(StringComparer.Ordinal)
            { "_MainTex", "_BumpMap", "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap", "_DetailNormalMap", "_DetailAlbedoMap", "_MatCap",
              "_SpecGlossMap", "_ParallaxMap", "_DetailMask" };

        // A material tagged VRCFallback=toonstandard is shown with Toon Standard, which copies every property of the same
        // name (ramp, masks, colours, toggles). Null keeps everything: the SDK's Toon Standard could not be found.
        private static readonly string[] ToonStandardShaders = { "VRChat/Mobile/Toon Standard", "VRChat/Mobile/Toon Standard (Outline)" };
        internal static HashSet<string> ToonStandardFallbackNames(Material material)
        {
            var empty = new HashSet<string>(StringComparer.Ordinal);
            if (!material.shader || Array.IndexOf(ToonStandardShaders, material.shader.name) >= 0 ||
                material.GetTag("VRCFallback", false, "").IndexOf("toonstandard", StringComparison.OrdinalIgnoreCase) < 0) return empty;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string shaderName in ToonStandardShaders)
            {
                var shader = Shader.Find(shaderName);
                if (!shader) return null;
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string name = shader.GetPropertyName(i);
                    names.Add(name);
                    if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture) names.Add(name + "_ST");
                }
            }
            return names;
        }

        internal sealed class Result { public int Materials, Textures, Keywords, Values; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, AnimationRewriter rewriter)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            // Materials an excluded renderer draws or swaps in stay untouched (the swap rewrite below is not per renderer), and so
            // do materials a renderer with a property block draws: the block can switch on a feature the material stores as off.
            var untouched = new HashSet<Material>(renderers.Where(r => Exclusions.Excluded(r) || TextureUsageScanner.HasPropertyBlock(r))
                .SelectMany(r => r.sharedMaterials.Concat(analysis.SwappedMaterials(r))).Where(m => m));
            var materials = renderers.SelectMany(r => r.sharedMaterials).Concat(analysis.AnimatedObjectValues.OfType<Material>())
                .Where(m => m && m.shader && !untouched.Contains(m)).Distinct().ToList();
            var animated = new HashSet<string>(analysis.Bindings.Where(b => b.Property.StartsWith("material.", StringComparison.Ordinal))
                .Select(b => b.Property.Substring(9).Split('.')[0]), StringComparer.Ordinal);
            // A feature switch only matters on the materials of the renderers it animates: each material gets the switches animated on
            // the renderers that draw it or swap it in. Curves whose renderer cannot be told, and materials an animation swaps in on a
            // renderer that cannot be found, keep the avatar-wide set.
            var unplacedNames = new HashSet<string>(analysis.Bindings.Where(b => b.Property.StartsWith("material.", StringComparison.Ordinal) &&
                !(b.Target is Renderer r && renderers.Contains(r))).Select(b => b.Property.Substring(9).Split('.')[0]), StringComparer.Ordinal);
            var perMaterial = new Dictionary<Material, HashSet<string>>();
            foreach (var renderer in renderers)
            {
                var names = analysis.AnimatedMaterialProperties(renderer).ToList();
                foreach (var m in renderer.sharedMaterials.Concat(analysis.SwappedMaterials(renderer)).Where(m => m))
                {
                    if (!perMaterial.TryGetValue(m, out var set)) perMaterial[m] = set = new HashSet<string>(unplacedNames, StringComparer.Ordinal);
                    set.UnionWith(names);
                }
            }
            Func<string, bool> AnimatedOn(Material m) => perMaterial.TryGetValue(m, out var set) ? set.Contains : (Func<string, bool>)animated.Contains;
            // A material with a missing shader keeps VRChat's fallback slots when an animation can swap between it and a
            // material that is not pink (on the same renderer): the swap may be how the avatar shows it, so nothing more than
            // before is cleared there. A swap among pink materials only clears them fully; a material an animation swaps in on a
            // renderer that cannot be found keeps them.
            bool Pink(Material m) => !m.shader || m.shader.name == "Hidden/InternalErrorShader";
            var swapped = new HashSet<Material>();
            var placed = new HashSet<Material>();
            foreach (var renderer in renderers)
            {
                var swaps = analysis.SwappedMaterials(renderer).Where(m => m).ToList();
                if (swaps.Count == 0) continue;
                placed.UnionWith(swaps);
                var shown = renderer.sharedMaterials.Where(m => m).Concat(swaps).ToList();
                if (shown.Any(m => !Pink(m))) swapped.UnionWith(shown);
            }
            swapped.UnionWith(analysis.AnimatedObjectValues.OfType<Material>().Where(m => !placed.Contains(m)));
            var replacements = new Dictionary<Material, Material>();
            foreach (var material in materials)
            {
                var stale = StaleTextures(material, AnimatedOn(material));
                if (swapped.Contains(material)) stale.ExceptWith(FallbackSlots);
                int keywords = InvalidKeywords(material);
                var values = StaleValues(material);
                if (stale.Count == 0 && keywords == 0 && values.Count == 0) continue;
                var copy = new Material(material) { name = material.name };
                using (var serialized = new SerializedObject(copy))
                {
                    var textures = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                    for (int i = textures.arraySize - 1; i >= 0; i--)
                        if (stale.Contains(textures.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue)) textures.DeleteArrayElementAtIndex(i);
                    foreach (string list in ValueLists)
                    {
                        var array = serialized.FindProperty(list);
                        for (int i = array == null ? -1 : array.arraySize - 1; i >= 0; i--)
                            if (values.Contains(array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue)) array.DeleteArrayElementAtIndex(i);
                    }
                    if (keywords > 0) serialized.FindProperty("m_InvalidKeywords")?.ClearArray();
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                replacements[material] = copy;
                register(material, copy);
                result.Materials++;
                result.Textures += stale.Count;
                result.Keywords += keywords;
                result.Values += values.Count;
            }
            if (replacements.Count == 0) return result;
            foreach (var renderer in renderers)
            {
                if (Exclusions.Excluded(renderer)) continue;
                var slots = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] && replacements.TryGetValue(slots[i], out var copy)) { slots[i] = copy; changed = true; }
                if (changed) renderer.sharedMaterials = slots;
            }
            rewriter?.RewriteValues(value => value is Material m && replacements.TryGetValue(m, out var copy) ? copy : null);
            return result;
        }

        private static readonly string[] ValueLists = { "m_SavedProperties.m_Floats", "m_SavedProperties.m_Colors", "m_SavedProperties.m_Ints" };
        // Values VRChat's shader fallback (Standard) may read by name; the list matches Avatar Optimizer's.
        private static readonly HashSet<string> FallbackValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "_Color", "_EmissionColor", "_Cutoff", "_Glossiness", "_GlossMapScale", "_Metallic", "_BumpScale", "_OcclusionStrength",
            "_DetailNormalMapScale", "_Mode", "_SrcBlend", "_DstBlend", "_ZWrite", "_MainTex_ST",
            "_SpecColor", "_SpecularHighlights", "_GlossyReflections", "_SmoothnessTextureChannel", "_Parallax", "_UVSec"
        };

        // Saved floats, colours and integers under names the shader does not declare (left from a previous shader). Unity
        // binds a saved value to any shader uniform of that name, declared in Properties or not, so a name is cleared only
        // when no stage of any pass reads it on PC, compiled with the material's keywords alone and with each global keyword (from the compiled constant
        // buffers). VRChat's fallback values always stay.
        internal static HashSet<string> StaleValues(Material material)
        {
            var stale = new HashSet<string>(StringComparer.Ordinal);
            var shader = material.shader;
            if (SkinnedMeshMerger.Broken(shader) || !GeneratedTargetValidator.IsStandalone) return stale; // Only PC compiles are read.
            var fallback = ToonStandardFallbackNames(material);
            if (fallback == null) return stale;
            using (var serialized = new SerializedObject(material))
                foreach (string list in ValueLists)
                {
                    var array = serialized.FindProperty(list);
                    for (int i = 0; array != null && i < array.arraySize; i++)
                    {
                        string name = array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue;
                        if (shader.FindPropertyIndex(name) < 0 && !FallbackValues.Contains(name) && !fallback.Contains(name)) stale.Add(name);
                    }
                }
            if (stale.Count == 0) return stale;
            // ShaderLab render state ([_Cull], [_ZWrite], stencil and blend values) reads a saved value by name without a
            // constant buffer: any name in brackets in the shader file, or in the files of shaders it takes passes from
            // (UsePass), stays. Unity's built-in shaders declare every name their state reads. Any other shader without a
            // readable file keeps all its values.
            var source = ShaderSource(shader, new HashSet<Shader>());
            if (source == null) return new HashSet<string>(StringComparer.Ordinal);
            stale.RemoveWhere(name => source.Contains("[" + name + "]"));
            if (stale.Count == 0) return stale;
            var read = VertexStreamStripper.Uniforms(material, stale.Select(name => new[] { name }).ToList());
            if (read == null) return new HashSet<string>(StringComparer.Ordinal);
            stale.ExceptWith(read);
            return stale;
        }

        // The text of the shader's file and of every shader its UsePass lines name; "" for Unity's built-in shaders; null
        // when a file cannot be read or a UsePass target cannot be found.
        private static string ShaderSource(Shader shader, HashSet<Shader> seen)
        {
            if (!shader || !seen.Add(shader)) return shader ? "" : null;
            string path = AssetDatabase.GetAssetPath(shader);
            if (path.StartsWith("Resources/unity_builtin", StringComparison.Ordinal) || path.StartsWith("Library/unity default resources", StringComparison.Ordinal))
                return "";
            if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)) return null;
            string text = System.IO.File.ReadAllText(path);
            var all = new System.Text.StringBuilder(text);
            foreach (System.Text.RegularExpressions.Match use in System.Text.RegularExpressions.Regex.Matches(text, "UsePass\\s+\"([^\"]+)/[^/\"]+\""))
            {
                string other = ShaderSource(Shader.Find(use.Groups[1].Value), seen);
                if (other == null) return null;
                all.Append('\n').Append(other);
            }
            return all.ToString();
        }

        private static bool AuditedSource(Material material)
        {
            var shader = material.shader;
            if (VertexStreamStripper.IsAuditedLilToonShader(shader, out _) || new VRChatMobileAdapter().Matches(material)) return true;
            string path = AssetDatabase.GetAssetPath(shader);
            return path.StartsWith("Resources/unity_builtin", StringComparison.Ordinal) || path.StartsWith("Library/unity default resources", StringComparison.Ordinal);
        }

        // Saved texture entries holding a texture under a name the shader does not declare. Only for shaders an adapter
        // recognizes: shader code can bind a material texture it never lists in its Properties block.
        internal static HashSet<string> StaleTextures(Material material, Func<string, bool> animated = null)
        {
            var stale = new HashSet<string>(StringComparer.Ordinal);
            // Only a missing shader, drawn with the error shader on every machine. ShaderHasError can report a variant or platform
            // this material never uses, and isSupported answers for this editor's graphics device, not the players'.
            bool errorShader = material.shader.name == "Hidden/InternalErrorShader";
            var fallback = ToonStandardFallbackNames(material);
            var switchedOff = new HashSet<string>(StringComparer.Ordinal);
            if (VertexStreamStripper.IsAuditedLilToonShader(material.shader, out bool outline))
            {
                foreach (var pair in LilToonSwitchedTextures)
                    if (material.HasProperty(pair.Switch) && material.GetFloat(pair.Switch) == 0 && !(animated?.Invoke(pair.Switch) ?? true))
                        switchedOff.UnionWith(pair.Textures);
                // Only the outline passes (FORWARD_OUTLINE, FORWARD_ADD_OUTLINE, SHADOW_CASTER_OUTLINE, which define LIL_OUTLINE) read
                // these; an entry without "_o" uses none of them (lts.shader and the other audited entries UsePass only FORWARD,
                // FORWARD_BACK, FORWARD_ADD, SHADOW_CASTER and META).
                if (!outline) switchedOff.UnionWith(OutlineTextures);
            }
            if (VRChatMobileAdapter.IsAuditedToonStandard(material))
                foreach (var (keyword, textures) in VRChatMobileAdapter.ToonStandardKeywordSlots)
                    if (!material.IsKeywordEnabled(keyword)) switchedOff.UnionWith(textures);
            // An undeclared texture is cleared only for shaders whose source has been audited (lilToon entries, Unity's built-ins,
            // VRChat's mobile shaders): code can bind a texture it never declares, and a name match proves nothing about the code.
            bool audited = AuditedSource(material);
            // On PC the compiled variants show which textures are bound; one no variant binds is never read, declared or not (a
            // feature compiled out of this entry or by the material's keywords, or a slot kept only for other render pipelines,
            // such as lilToon's _BaseMap). Animation cannot change keywords, so the variants are fixed for the build.
            HashSet<string> bound = null;
            // Its tiling (_ST), size (_TexelSize) and HDR decode values come from the same saved entry, so none may be read either.
            string[] Uses(string name) => new[] { name, name + "_ST", name + "_TexelSize", name + "_HDR" };
            var asked = new List<string[]>(); // Every name NeverBound will be asked about, so the compile can stop once each is found.
            bool Unbound(string name) => audited || NeverBound(name);
            bool NeverBound(string name)
            {
                if (!GeneratedTargetValidator.IsStandalone || SkinnedMeshMerger.Broken(material.shader)) return false;
                if (bound == null) bound = VertexStreamStripper.Uniforms(material, asked) ?? new HashSet<string> { null };
                // A partial set only answers the names it was asked about; any other name gets the complete one.
                var answer = asked.Any(g => g[0] == name) ? bound : VertexStreamStripper.Uniforms(material) ?? new HashSet<string> { null };
                return !answer.Contains(null) && !Uses(name).Any(answer.Contains);
            }
            bool Candidate(string name, Object texture) => texture && (!(FallbackSlots.Contains(name) || fallback == null || fallback.Contains(name)) || errorShader);
            using (var serialized = new SerializedObject(material))
            {
                var textures = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                for (int i = 0; textures != null && i < textures.arraySize; i++)
                {
                    var entry = textures.GetArrayElementAtIndex(i);
                    string name = entry.FindPropertyRelative("first").stringValue;
                    if (Candidate(name, entry.FindPropertyRelative("second.m_Texture").objectReferenceValue) && !errorShader && !switchedOff.Contains(name) &&
                        (material.shader.FindPropertyIndex(name) >= 0 || !audited)) asked.Add(Uses(name));
                }
                for (int i = 0; textures != null && i < textures.arraySize; i++)
                {
                    var entry = textures.GetArrayElementAtIndex(i);
                    string name = entry.FindPropertyRelative("first").stringValue;
                    var texture = entry.FindPropertyRelative("second.m_Texture").objectReferenceValue;
                    if (!texture || (FallbackSlots.Contains(name) || fallback == null || fallback.Contains(name)) && !errorShader) continue; // Without its shader the material is broken for everyone; nothing is kept for the fallback.
                    if (errorShader || switchedOff.Contains(name) ||
                        (material.shader.FindPropertyIndex(name) < 0 ? Unbound(name) : NeverBound(name))) stale.Add(name);
                }
            }
            return stale;
        }

        // lilToon 2.3.4 feature switches and the textures only read inside them (checked in lil_common_frag.hlsl: Main2nd
        // 740-814, Main3rd 836-910, matcaps 1517-1636, rim 1643-1757, backlight 1233-1267, glitter 1758-1807, emission
        // 1815-1955; no other include samples these slots).
        private static readonly (string Switch, string[] Textures)[] LilToonSwitchedTextures =
        {
            ("_UseMain2ndTex", new[] { "_Main2ndTex", "_Main2ndBlendMask", "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask" }),
            ("_UseMain3rdTex", new[] { "_Main3rdTex", "_Main3rdBlendMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask" }),
            ("_UseMatCap", new[] { "_MatCapTex", "_MatCapBlendMask", "_MatCapBumpMap" }),
            ("_UseMatCap2nd", new[] { "_MatCap2ndTex", "_MatCap2ndBlendMask", "_MatCap2ndBumpMap" }),
            ("_UseRim", new[] { "_RimColorTex" }),
            ("_SSAO", new[] { "_SSAOMask" }), // lilSSAO only (see VertexStreamStripper.AuditedLilSsao); lilToon has no _SSAO.
            ("_UseBacklight", new[] { "_BacklightColorTex" }),
            ("_UseGlitter", new[] { "_GlitterColorTex", "_GlitterShapeTex" }),
            ("_UseEmission", new[] { "_EmissionMap", "_EmissionBlendMask", "_EmissionGradTex" }),
            ("_UseEmission2nd", new[] { "_Emission2ndMap", "_Emission2ndBlendMask", "_Emission2ndGradTex" }),
            ("_UseRimShade", new[] { "_RimShadeMask" }), // lilGetRimShade: sampled only inside if(_UseRimShade) (lil_common_frag.hlsl 1202-1213).
        };

        private static readonly string[] OutlineTextures = { "_OutlineTex", "_OutlineWidthMask", "_OutlineVectorTex" };

        // Keywords Unity keeps on the material that its shader does not declare (it never compiles them).
        internal static int InvalidKeywords(Material material)
        {
            // Toon Standard's features are keywords the fallback copies from the material.
            var fallback = ToonStandardFallbackNames(material);
            if (fallback == null || fallback.Count > 0) return 0;
            using (var serialized = new SerializedObject(material))
                return serialized.FindProperty("m_InvalidKeywords")?.arraySize ?? 0;
        }
    }
}
