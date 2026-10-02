using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    internal sealed class NoTextureChangesException : InvalidOperationException
    {
        public const string Explanation = "Clearing unused pixels and adding the edge-extension band would not change any RGBA pixels. Original retained; no duplicate needed.";
        public NoTextureChangesException() : base(Explanation) { }
    }

    internal sealed class NoFileSizeReductionException : InvalidOperationException
    {
        public readonly long SourceBytes, CandidateBytes;
        public readonly bool Compressed;
        public NoFileSizeReductionException(long sourceBytes, long candidateBytes, bool compressed = false)
            : base(Explain(sourceBytes, candidateBytes, compressed))
        { SourceBytes = sourceBytes; CandidateBytes = candidateBytes; Compressed = compressed; }
        public static string Explain(long sourceBytes, long candidateBytes, bool compressed = false) => compressed
            ? $"No compressed-size saving: estimated candidate {candidateBytes:N0} bytes, original {sourceBytes:N0} bytes (compressed texture data as stored in the avatar bundle). Original retained."
            : $"No PNG file-size saving: candidate {candidateBytes:N0} bytes, original {sourceBytes:N0} bytes. Original retained.";
    }

    internal static class TextureFileSizePolicy
    {
        public static bool IsSmaller(long sourceBytes, long candidateBytes) =>
            sourceBytes > 0 && candidateBytes > 0 && candidateBytes < sourceBytes;

        public static void Validate(long sourceBytes, long candidateBytes)
        {
            if (!IsSmaller(sourceBytes, candidateBytes)) throw new NoFileSizeReductionException(sourceBytes, candidateBytes);
        }

        public static bool IsSmaller(Texture2D source, Texture2D candidate) =>
            IsSmaller(new FileInfo(AssetDatabase.GetAssetPath(source)).Length,
                new FileInfo(AssetDatabase.GetAssetPath(candidate)).Length);

        // Mapping made under the compressed-size estimate or by padding repair: the PNG file sizes do not decide.
        public static bool SkipsPngGate(GeneratedTextureMapping mapping) =>
            mapping.repairedPadding || mapping.compressedSourceBytes > 0;

        // Estimated mappings keep their estimate; older ones are still judged by their original PNG rule.
        public static bool IsBeneficial(GeneratedTextureMapping mapping) =>
            mapping.compressedSourceBytes > 0
                ? IsSmaller(mapping.compressedSourceBytes, mapping.compressedReplacementBytes)
                : mapping.repairedPadding || IsSmaller(mapping.source, mapping.replacement);

        public static NonBeneficialTextureAnalysis Find(TextureOptimizationManifest manifest, string id, string recipe) =>
            manifest.nonBeneficialTextures?.FirstOrDefault(m => m.sourceId == id && m.recipeHash == recipe);

        public static void Remember(TextureOptimizationManifest manifest, Texture2D source, string id, string recipe, NoFileSizeReductionException e)
        {
            if (manifest.nonBeneficialTextures == null)
                manifest.nonBeneficialTextures = new System.Collections.Generic.List<NonBeneficialTextureAnalysis>();
            manifest.nonBeneficialTextures.RemoveAll(m => m.sourceId == id && m.recipeHash == recipe);
            manifest.nonBeneficialTextures.Add(new NonBeneficialTextureAnalysis
                { source = source, sourceId = id, recipeHash = recipe, sourceBytes = e.SourceBytes, candidateBytes = e.CandidateBytes, compressedEstimate = e.Compressed,
                  lastUsedDay = CacheCleanup.Today });
            // Remove mappings only; existing PNG assets are never deleted.
            manifest.mappings.RemoveAll(m => m.sourceId == id && m.recipeHash == recipe);
            EditorUtility.SetDirty(manifest);
        }
    }

    internal enum TextureResultKind
    {
        Generated, Reused, Unchanged, NotSmaller, Skipped, NotSampled, NotProcessed
    }

    internal sealed class TextureGenerationResult
    {
        public Texture Source;
        public TextureResultKind Outcome;
        public string Reason;
        public string Operation;
        public bool CacheLookup;
        public bool CacheValidation;
        // Per-texture preparation time in this run. It excludes the shared scan and duplicate detection.
        public long PreparationMilliseconds = -1;
    }

    internal sealed class GenerationSummary
    {
        public int Generated, Reused, Unchanged, NotSmaller, Skipped;
        public readonly System.Collections.Generic.List<TextureGenerationResult> TextureResults =
            new System.Collections.Generic.List<TextureGenerationResult>();
        public override string ToString() =>
            $"{Generated} generated | {Reused} reused | {Unchanged} no changes needed | {NotSmaller} no size saving | {Skipped} skipped.";
    }

    internal static class GenerationCoordinator
    {
        // Extend by one quarter of the imported padding, then round up in source texels.
        internal static Exception SelectNoOutputException(NoFileSizeReductionException rejectedCandidate, bool unchangedFallback)
        {
            if (rejectedCandidate != null) return rejectedCandidate;
            if (unchangedFallback) return new NoTextureChangesException();
            return new InvalidOperationException("No texture candidate was produced.");
        }
        internal static int ExtensionRadius(int padding, int encodedSize, int importedSize) =>
            (int)Math.Ceiling(padding * 0.25 * encodedSize / importedSize);

        // Number of generated PNGs that needed the API copy plus a second import (for tests/diagnostics).
        internal static int ImporterFallbacks;

        // Writing the source importer's metadata before the first import lets Unity compress the new PNG
        // once, with exactly the source settings, instead of importing with defaults and then reimporting.
        // Only the GUID, userData and asset-bundle assignment differ. Unexpected layouts return null,
        // which falls back to copying settings through the importer API.
        // Unity tools and other editor scripts still fail on paths past MAX_PATH, so generated files keep their full path,
        // including the longest file written ("<name>.png.writing") and room for a revision suffix, within 259 characters.
        // The name is the source name (shortened as needed) plus 8 recipe hex digits; the manifest keeps the full identity.
        internal static string OutputPath(string folder, string sourceName, string recipe)
        {
            const int RevisionSuffix = 4; // AssetDatabase.GenerateUniqueAssetPath appends " 1", " 2", ...
            string hash = "_" + recipe.Substring(0, 8);
            string safeName = new string(sourceName.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());
            int budget = 259 - RevisionSuffix - Path.GetFullPath(folder + "/" + hash + ".png.writing").Length;
            if (budget < 0)
                throw new IOException("The cache folder path is too long for generated textures; move the project to a shorter path. Original retained.");
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + hash + ".png";
        }

        internal static string CopiedImporterMeta(string sourceMetaPath, string userData)
        {
            if (!File.Exists(LongPath.For(sourceMetaPath))) return null;
            string text = File.ReadAllText(LongPath.For(sourceMetaPath));
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { newline }, StringSplitOptions.None);
            int guid = 0, textureImporter = 0, user = 0, bundle = 0, variant = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("guid: ", StringComparison.Ordinal)) { lines[i] = "guid: " + Guid.NewGuid().ToString("N"); guid++; }
                else if (line == "TextureImporter:") textureImporter++;
                else if (line.StartsWith("  userData:", StringComparison.Ordinal)) { lines[i] = "  userData: " + userData; user++; }
                else if (line.StartsWith("  assetBundleName:", StringComparison.Ordinal)) { lines[i] = "  assetBundleName: "; bundle++; }
                else if (line.StartsWith("  assetBundleVariant:", StringComparison.Ordinal)) { lines[i] = "  assetBundleVariant: "; variant++; }
            }
            if (guid != 1 || textureImporter != 1 || user != 1 || bundle > 1 || variant > 1) return null;
            return string.Join(newline, lines);
        }

        internal static GeneratedTextureMapping GenerateOne(TextureGroup group, string folder, string recipe, Action cancel = null, UvCoverageCache coverageCache = null, bool allowNewRevision = false, OptimizationProgress progress = null)
        {
            Action checkCancelled = cancel ?? (progress == null ? null : (Action)progress.CheckCancelled);

            progress?.Stage("Validating texture source");
            checkCancelled?.Invoke();
            if (group.Warning != null) throw new InvalidOperationException(group.Warning);
            folder = TextureSafety.ValidateFolder(folder);
            var source = (Texture2D)group.Source;
            var sourceImporter = TextureSafety.ValidateUsage(group);
            if (!group.ActiveUses.Any()) throw new InvalidOperationException("No sampled texture uses.");
            foreach (var use in group.ActiveUses)
            {
                checkCancelled?.Invoke();
                if (!use.Sampling.Supported || (use.Sampling.Semantics != TextureSemantics.Color && use.Sampling.Semantics != TextureSemantics.Data && use.Sampling.Semantics != TextureSemantics.Normal))
                    throw new InvalidOperationException("Only modeled colour, mask or normal sampling is eligible.");
            }

            string sourcePath = AssetDatabase.GetAssetPath(source);
            bool flattened = !SourceImages.IsPng(sourcePath);
            // A PSD (or other flattened) source and its PNG replacement are only comparable by estimated compressed size,
            // so without an estimate (Play Mode, a failed bundle build) these sources are kept as they are.
            if (flattened && !BundleSizeEstimator.TryMeasure(sourcePath, out _))
                throw new InvalidOperationException("Sources other than PNG need the compressed-size estimate, which is unavailable here; original retained.");
            progress?.Stage(flattened ? "Flattening source image" : "Reading and decoding PNG", !flattened);
            checkCancelled?.Invoke();
            var bytes = File.ReadAllBytes(LongPath.For(sourcePath));
            PngInfo info;
            var pixels = flattened ? SourceImages.Flatten(sourcePath, folder, out info) : PngPixels.Decode(bytes, out info);
            checkCancelled?.Invoke();
            bool normal = sourceImporter.textureType == TextureImporterType.NormalMap;
            if (normal)
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((i & 0xffff) == 0) checkCancelled?.Invoke();
                    if (pixels[i].a != 255)
                        throw new InvalidOperationException("Non-opaque normal PNG alpha is unsupported; original retained.");
                }

            progress?.Stage("Calculating UV coverage");
            checkCancelled?.Invoke();
            var mask = UvCoverageRasterizer.Build(group, info.Width, info.Height, out var sampled, coverageCache, checkCancelled);
            checkCancelled?.Invoke();
            if (normal && mask.Count(used => !used) < 256)
                throw new InvalidOperationException("Fewer than 256 unused texels remain after padding; no useful output.");
            // Normal maps fill with the flat normal (edit authored RGB, never Unity's platform-packed form).
            // Colour textures prefer the dominant sampled colour, excluding original safety padding.
            // Keep the edge/cleared-colour fallbacks and file-size gate. Data maps retain their old policy.
            progress?.Stage("Choosing fill colour");
            checkCancelled?.Invoke();
            bool colour = group.ActiveUses.All(use => use.Sampling.Semantics == TextureSemantics.Color);
            bool repair = !normal && group.RepairsPadding;
            if (repair && !sampled.Any(used => used)) throw new InvalidOperationException("No sampled UV texels to rebuild padding from.");
            if (repair) mask = sampled; // Repair existing padding; preserve only the modeled sampled coverage.
            // Channels any use can read. Only the Standalone (PC) import is ever given a smaller format.
            var channels = group.ActiveUses.Aggregate((TextureChannels)0, (read, use) => read | use.Sampling.Channels);
            TextureImporterFormat? ChannelFormat(TextureImporter importer) =>
                GeneratedTargetValidator.IsStandalone ? ChannelFormats.Choose(importer, channels) : null;
            // A texture with (almost) nothing to clear can still shrink through its format alone.
            bool formatOnly = ChannelFormat(sourceImporter) != null;
            TextureImporterFormat? standaloneFormat = null;
            var candidates = repair ? new[] { BackgroundValueDetector.DetectUsed(pixels, sampled).Value }
                : normal ? new[] { new Color32(128, 128, 255, 255) }
                : new[] { colour ? BackgroundValueDetector.DetectUsed(pixels, sampled) : null,
                    BackgroundValueDetector.DetectEdge(pixels, mask, info.Width, info.Height),
                    formatOnly && mask.Count(used => !used) < 256 ? BackgroundValueDetector.DetectUsed(pixels, sampled) : BackgroundValueDetector.Detect(pixels, mask) }
                    .Where(c => c.HasValue).Select(c => c.Value).Distinct().ToArray();
            int padding = AvatarTextureOptimizer.GetPaddingPixels(source.width, source.height);
            int radiusX = ExtensionRadius(padding, info.Width, source.width);
            int radiusY = ExtensionRadius(padding, info.Height, source.height);
            if (repair)
            {
                radiusX += (int)Math.Ceiling((double)padding * info.Width / source.width);
                radiusY += (int)Math.Ceiling((double)padding * info.Height / source.height);
            }
            var samplingPaths = group.ActiveUses.SelectMany(use => use.Sampling.GetPaths()).ToArray();
            bool repeatX = samplingPaths.Any(path => path.WrapU(source) == TextureWrapMode.Repeat);
            bool repeatY = samplingPaths.Any(path => path.WrapV(source) == TextureWrapMode.Repeat);
            int preserved = mask.Count(used => used);
            Color32 background = default;
            Color32[] output = null;
            string path = null;
            Texture2D replacement = null;
            long compressedSource = 0, compressedReplacement = 0;
            bool unchanged = false;
            NoFileSizeReductionException notSmaller = null;
            // What ships is the compressed texture inside the bundle, not the PNG: a larger PNG with flat unused areas
            // can still shrink. Without a usable estimate (Play Mode, a failed build) the PNG file size decides.
            // Padding repair is gated the same way; without an estimate it accepts any size, as before.
            progress?.Stage("Estimating original compressed size", false);
            bool estimate = BundleSizeEstimator.TryMeasure(AssetDatabase.GetAssetPath(source), out compressedSource);
            checkCancelled?.Invoke();

            // Validates, writes and imports one candidate PNG as a new asset. Nothing is overwritten.
            (string Path, Texture2D Replacement) Materialize(byte[] png, Color32[] expected)
            {
                progress?.Stage("Validating encoded PNG");
                checkCancelled?.Invoke();
                var decoded = PngPixels.Decode(png, out var encodedInfo);
                checkCancelled?.Invoke();
                if (encodedInfo.Width != info.Width || encodedInfo.Height != info.Height || !decoded.SequenceEqual(expected))
                    throw new InvalidOperationException("PNG round-trip changed encoded pixels; output not written.");
                progress?.Stage("Rechecking source inputs");
                checkCancelled?.Invoke();
                if (FingerprintService.Recipe(group) != recipe || FingerprintService.SourceHash(group) != FingerprintService.Hash(bytes))
                    throw new InvalidOperationException("Inputs changed during generation; retry.");
                checkCancelled?.Invoke();
                string outPath = OutputPath(folder, source.name, recipe);
                if (File.Exists(outPath) || File.Exists(outPath + ".meta"))
                {
                    if (!allowNewRevision)
                        throw new InvalidOperationException("Output path already exists without a matching valid mapping. Keep or move it before regenerating; it will not be overwritten.");
                    outPath = AssetDatabase.GenerateUniqueAssetPath(outPath);
                    if (File.Exists(outPath) || File.Exists(outPath + ".meta"))
                        throw new InvalidOperationException("Could not allocate a new cache revision without overwriting an existing asset.");
                }
                // Final cancellable checkpoint, before any staging files are created.
                progress?.Stage("Preparing output PNG");
                checkCancelled?.Invoke();
                // CreateNew prevents accidental overwrites. The .writing file is not a PNG Unity can import.
                string staging = outPath + ".writing";
                string userData = "AvatarTextureOptimizer:" + recipe;
                string meta = CopiedImporterMeta(AssetDatabase.GetAssetPath(source) + ".meta", userData);
                bool ownsStaging = false, ownsMeta = false;
                progress?.Stage("Writing PNG", false);
                try
                {
                    using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                    {
                        ownsStaging = true;
                        stream.Write(png, 0, png.Length);
                    }
                    if (meta != null)
                    {
                        using (var stream = new FileStream(outPath + ".meta", FileMode.CreateNew, FileAccess.Write))
                        {
                            ownsMeta = true;
                            var metaBytes = new System.Text.UTF8Encoding(false).GetBytes(meta);
                            stream.Write(metaBytes, 0, metaBytes.Length);
                        }
                    }
                    File.Move(staging, outPath);
                    ownsStaging = ownsMeta = false;
                }
                finally
                {
                    if (ownsStaging && File.Exists(staging)) File.Delete(staging);
                    if (ownsMeta && File.Exists(outPath + ".meta")) File.Delete(outPath + ".meta");
                }
                progress?.Stage("Importing PNG", false);
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(outPath);
                if (meta == null || importer.userData != userData ||
                    TextureSafety.SettingsFingerprint(importer) != TextureSafety.SettingsFingerprint(sourceImporter))
                {
                    // The copied metadata was unavailable or not applied: copy settings through the API and reimport.
                    ImporterFallbacks++;
                    TextureSafety.CopyImporter(sourceImporter, importer);
                    importer.userData = userData;
                    importer.SaveAndReimport();
                }
                // Decided on the imported output: clearing unused pixels can already leave its alpha fully opaque.
                standaloneFormat = ChannelFormat(importer);
                if (standaloneFormat != null)
                {
                    progress?.Stage("Importing with " + standaloneFormat.Value + " format", false);
                    importer.SetPlatformTextureSettings(ChannelFormats.Standalone(importer, standaloneFormat.Value));
                    importer.SaveAndReimport();
                }
                progress?.Stage("Validating imported PNG", false);
                var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
                GeneratedTargetValidator.ValidatePair(source, imported, repair || estimate, (int)(standaloneFormat ?? 0));
                return (outPath, imported);
            }

            foreach (var candidate in candidates)
            {
                progress?.Stage("Filling unused pixels");
                checkCancelled?.Invoke();
                var attempt = (Color32[])pixels.Clone();
                for (int i = 0; i < attempt.Length; i++)
                {
                    if ((i & 0xffff) == 0) checkCancelled?.Invoke();
                    if (!mask[i]) attempt[i] = candidate;
                }
                progress?.Stage("Extending padding");
                EdgeValueExtender.Extend(pixels, attempt, mask, info.Width, info.Height, radiusX, radiusY, repeatX, repeatY, checkCancelled);
                // Unchanged pixels are still worth writing for a smaller format, which only the estimate can show.
                if (!(formatOnly && estimate) && attempt.SequenceEqual(pixels)) { unchanged = true; continue; }
                checkCancelled?.Invoke();
                progress?.Stage("Encoding PNG");
                var encoded = PngPixels.Encode(attempt, info);
                checkCancelled?.Invoke();
                bool pngSmaller = TextureFileSizePolicy.IsSmaller(bytes.LongLength, encoded.LongLength);
                void RejectByPngSize()
                {
                    if (notSmaller == null || encoded.LongLength < notSmaller.CandidateBytes)
                        notSmaller = new NoFileSizeReductionException(bytes.LongLength, encoded.LongLength);
                }
                // Without an estimate, compare complete encoded files (including preserved profiles) before writing anything.
                if (!repair && !estimate && !pngSmaller) { RejectByPngSize(); continue; }

                var written = Materialize(encoded, attempt);
                if (estimate)
                {
                    progress?.Stage("Estimating compressed size", false);
                    if (BundleSizeEstimator.TryMeasure(written.Path, out long candidateCompressed))
                    {
                        if (!TextureFileSizePolicy.IsSmaller(compressedSource, candidateCompressed))
                        {
                            // The candidate is a fresh asset of this run; drop it so a rejected texture leaves nothing behind.
                            AssetDatabase.DeleteAsset(written.Path);
                            if (notSmaller == null || candidateCompressed < notSmaller.CandidateBytes)
                                notSmaller = new NoFileSizeReductionException(compressedSource, candidateCompressed, true);
                            continue;
                        }
                        compressedReplacement = candidateCompressed;
                    }
                    else
                    {
                        // The estimate failed mid-run: this and later candidates fall back to the PNG file size.
                        estimate = false;
                        compressedSource = 0;
                        if (flattened || !pngSmaller) { AssetDatabase.DeleteAsset(written.Path); RejectByPngSize(); continue; }
                    }
                }
                background = candidate; output = attempt; path = written.Path; replacement = written.Replacement;
                break;
            }
            if (output == null)
            {
                // A changed candidate rejected by the size gate is the useful result, even if a later
                // fallback candidate happened to match the source pixels exactly.
                throw SelectNoOutputException(notSmaller, unchanged);
            }
            return new GeneratedTextureMapping
            {
                source = source, replacement = replacement, sourceId = FingerprintService.Id(source),
                recipeHash = recipe, outputHash = FingerprintService.FileHash(path),
                outputImporterHash = FingerprintService.ImporterHash(replacement), width = info.Width, height = info.Height,
                padding = padding, preservedPixels = preserved, background = background, repairedPadding = repair, hasReportMetadata = true, extensionRadiusX = radiusX, extensionRadiusY = radiusY,
                compressedSourceBytes = estimate ? compressedSource : 0, compressedReplacementBytes = estimate ? compressedReplacement : 0,
                standaloneFormat = (int)(standaloneFormat ?? 0),
                // Only the active target has actually been imported. This does not certify visual equivalence.
                standaloneValidated = GeneratedTargetValidator.IsStandalone,
                androidValidated = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
            };
        }
    }
}
