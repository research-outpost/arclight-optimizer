using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Collects build notes and writes one easy-to-share report for each avatar name.
    internal sealed class OptimizationLog
    {
        private const int MaxNamePartLength = 48;
        private const string SkippedPrefix = "Skipped; original texture retained for all its assignments.";

        private readonly List<string> notes = new List<string>();
        private readonly HashSet<string> seenNotes = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ReplacementSnapshot> replacements = new List<ReplacementSnapshot>();
        private readonly List<TextureSnapshot> textureResults = new List<TextureSnapshot>();
        private readonly Dictionary<Texture, string> duplicateRejections = new Dictionary<Texture, string>();
        private readonly Dictionary<Texture, List<string>> textureIssues = new Dictionary<Texture, List<string>>();
        private bool emitted;
        private bool captured;
        private readonly bool isPlayMode;
        private string substitutionFailure;
        private int scannedGroups;
        private int eligibleGroups;
        private int duplicateAliases;
        private string reportPath;
        // Build savings by category, in report order: (bytes saved, what saved them, items whose size could not be estimated).
        private readonly List<(string Category, long Bytes, string Detail, int Unmeasured)> savings = new List<(string, long, string, int)>();

        internal OptimizationLog(bool isPlayMode = false) { this.isPlayMode = isPlayMode; }
        internal bool HasCaptured => captured;
        internal void Add(UnityEngine.Object related, string message)
        {
            if (related is Texture texture && !string.IsNullOrEmpty(message))
            {
                if (message.StartsWith("Duplicate kept:", StringComparison.Ordinal))
                    duplicateRejections[texture] = ReasonText(message);
                else
                {
                    if (!textureIssues.TryGetValue(texture, out var issues))
                        textureIssues[texture] = issues = new List<string>();
                    issues.Add(ReasonText(message));
                }
            }
            if (message != null && message.StartsWith("Substitution aborted:", StringComparison.Ordinal))
                substitutionFailure = ReasonText(message);

            string identity = related ? Compact(related.name) : "Build";
            string path = related ? AssetDatabase.GetAssetPath(related) : "";
            if (!string.IsNullOrEmpty(path)) identity += " [" + Compact(path) + "]";

            var detailLines = (message ?? "").Replace("\r", "").Split('\n')
                .Select(line => Compact(line.Replace(SkippedPrefix, "")))
                .Where(line => line.Length > 0).ToArray();
            if (detailLines.Length == 0) return;

            var note = new StringBuilder("- ").Append(identity).Append(": ").Append(detailLines[0]);
            for (int i = 1; i < detailLines.Length; i++)
                note.Append('\n').Append("  ").Append(detailLines[i]);
            string formatted = note.ToString();
            if (seenNotes.Add(formatted)) notes.Add(formatted);
        }
        // Capture file sizes and paths while the temporary selected manifest is still alive.
        // Emit runs after that manifest is destroyed, so it must not depend on Unity object refs.
        internal void Capture(ScanResult scan, TextureOptimizationManifest selected,
            GenerationSummary summary = null, SubstitutionState state = null, bool optimizeTextures = true)
        {
            if (captured) return;
            captured = true;
            scannedGroups = scan?.Groups?.Count ?? 0;
            eligibleGroups = scan?.Groups?.Count(group => group != null && group.Warning == null && group.ActiveUses.Any()) ?? 0;
            duplicateAliases = scan?.Duplicates?.Count ?? 0;
            replacements.Clear();
            textureResults.Clear();
            if (scan == null) return;
            long textureBytes = 0;
            int replaced = 0, merged = 0, textureUnmeasured = 0;

            var resultBySource = (summary?.TextureResults ?? new List<TextureGenerationResult>())
                .Where(result => result != null && result.Source)
                .GroupBy(result => result.Source)
                .ToDictionary(group => group.Key, group => group.Last());
            var mappingBySource = new Dictionary<Texture, GeneratedTextureMapping>();
            if (selected?.mappings != null)
            {
                foreach (var mapping in selected.mappings)
                {
                    if (mapping == null || !mapping.source || !mapping.replacement) continue;
                    mappingBySource[mapping.source] = mapping;
                    string sourcePath = AssetDatabase.GetAssetPath(mapping.source) ?? "";
                    string replacementPath = AssetDatabase.GetAssetPath(mapping.replacement) ?? "";
                    replacements.Add(new ReplacementSnapshot
                    {
                        SourceName = Compact(mapping.source.name),
                        SourcePath = Compact(sourcePath),
                        ReplacementName = Compact(mapping.replacement.name),
                        ReplacementPath = Compact(replacementPath),
                        SourceBytes = FileSize(sourcePath),
                        ReplacementBytes = FileSize(replacementPath)
                    });
                }
            }
            replacements.Sort((a, b) => string.CompareOrdinal(a.SourcePath, b.SourcePath));

            foreach (var group in scan.Groups.Where(group => group != null)
                         .OrderBy(group => AssetDatabase.GetAssetPath(group.Source), StringComparer.Ordinal))
            {
                resultBySource.TryGetValue(group.Source, out var result);
                mappingBySource.TryGetValue(group.Source, out var mapping);
                var row = new TextureSnapshot
                {
                    SourceName = group.Source ? Compact(group.Source.name) : "(missing texture)",
                    SourcePath = group.Source ? Compact(AssetDatabase.GetAssetPath(group.Source)) : "",
                    Outcome = result != null ? OutcomeName(result.Outcome) : FallbackOutcome(group, mapping, optimizeTextures),
                    Reason = result != null ? ReasonText(result.Reason) : FallbackReason(group, mapping, optimizeTextures),
                    Operation = result?.Operation,
                    PreparationMilliseconds = result?.PreparationMilliseconds ?? -1,
                    PreparationLabel = result == null ? null : result.CacheLookup
                        ? (result.CacheValidation ? "Cache lookup/validation time (this run)" : "Cache lookup time (this run)")
                        : "Preparation/generation time (this run)"
                };
                if (mapping != null) ApplyMapping(row, mapping);

                if (mapping != null && result != null &&
                    (result.Outcome == TextureResultKind.Generated || result.Outcome == TextureResultKind.Reused))
                {
                    string issue = null;
                    if (textureIssues.TryGetValue(group.Source, out var issues))
                        issue = issues.FirstOrDefault(value => !string.IsNullOrEmpty(value));
                    if (substitutionFailure != null) issue = substitutionFailure;
                    if (!string.IsNullOrEmpty(issue))
                    {
                        row.Outcome += " (not applied)";
                        row.Reason = issue;
                    }
                }
                if (state != null && state.Cancelled)
                {
                    bool completed = result != null &&
                        (result.Outcome == TextureResultKind.Generated || result.Outcome == TextureResultKind.Reused);
                    if (completed || ((result == null || result.Outcome == TextureResultKind.NotProcessed) &&
                        group.Warning == null && group.ActiveUses.Any()))
                    {
                        row.Outcome = completed ? OutcomeName(result.Outcome) + " (not applied)" : "Cancelled";
                        row.Reason = "Texture optimization was cancelled before application; original texture assignments were retained.";
                    }
                }
                if (mapping != null && result != null && !row.Outcome.EndsWith("(not applied)", StringComparison.Ordinal) && row.Outcome != "Cancelled" &&
                    (result.Outcome == TextureResultKind.Generated || result.Outcome == TextureResultKind.Reused))
                {
                    replaced++;
                    if (mapping.compressedSourceBytes > 0 && mapping.compressedReplacementBytes > 0)
                        textureBytes += mapping.compressedSourceBytes - mapping.compressedReplacementBytes;
                    else textureUnmeasured++;
                }
                textureResults.Add(row);
            }

            foreach (var duplicate in scan.Duplicates.OrderBy(pair => AssetDatabase.GetAssetPath(pair.Key), StringComparer.Ordinal))
            {
                string sourcePath = duplicate.Key ? AssetDatabase.GetAssetPath(duplicate.Key) : "";
                string targetPath = duplicate.Value ? AssetDatabase.GetAssetPath(duplicate.Value) : "";
                bool retained = duplicateRejections.TryGetValue(duplicate.Key, out var rejection) ||
                    state == null || !state.MergedDuplicates.Contains(duplicate.Key);
                textureResults.Add(new TextureSnapshot
                {
                    SourceName = duplicate.Key ? Compact(duplicate.Key.name) : "(missing texture)",
                    SourcePath = Compact(sourcePath),
                    Outcome = retained ? "Duplicate retained" : "Duplicate merged",
                    Reason = rejection ?? (retained
                        ? substitutionFailure ?? "Identical texture data was found, but this run did not confirm that its assignment was merged."
                        : "Equivalent texture pixels and compatible import settings match the retained texture; its uses were merged into " +
                          (string.IsNullOrEmpty(targetPath) ? Compact(duplicate.Value ? duplicate.Value.name : "(missing texture)") : Compact(targetPath)) + "."),
                    Operation = "Duplicate consolidation",
                    PreparationMilliseconds = -1
                });
                if (!retained)
                {
                    // The dropped copy matches the kept texture's pixels and import settings, so it would have shipped at the
                    // kept texture's size (usually already measured this session).
                    merged++;
                    if (BundleSizeEstimator.TryMeasure(targetPath, out long duplicateBytes)) textureBytes += duplicateBytes;
                    else textureUnmeasured++;
                }
            }
            textureResults.Sort((a, b) => string.CompareOrdinal(a.SourcePath, b.SourcePath));
            if (replaced + merged > 0)
                savings.Add(("Textures", textureBytes, Count(replaced, "replaced") + Count(merged, "duplicate(s) merged"), textureUnmeasured));
        }
        internal string Format(GameObject root, SubstitutionState state)
        {
            var output = new StringBuilder();
            output.AppendLine("Arclight Optimizer report");
            output.Append("Avatar: ").AppendLine(Compact(root ? root.name : "(unavailable)"));
            output.Append("Created (local): ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            output.Append("Target platform: ").AppendLine(EditorUserBuildSettings.activeBuildTarget.ToString());
            output.Append("Package version: ").AppendLine(PackageVersion());
            output.Append("Run context: ").AppendLine(isPlayMode ? "Play Mode" : "Outside Play Mode");
            if (state != null && state.Cancelled) output.AppendLine("Run status: Cancelled; original texture assignments retained.");
            output.AppendLine();
            AppendBuildSavings(output);
            output.AppendLine("Results");

            var summary = state != null ? state.Summary : null;
            output.Append("Generated: ").AppendLine(Number(summary?.Generated ?? 0));
            output.Append("Reused: ").AppendLine(Number(summary?.Reused ?? 0));
            output.Append("Unchanged: ").AppendLine(Number(summary?.Unchanged ?? 0));
            output.Append("No size reduction: ").AppendLine(Number(summary?.NotSmaller ?? 0));
            output.Append("Skipped: ").AppendLine(Number(summary?.Skipped ?? 0));
            output.Append("Applied: ").Append(Number(state?.AppliedTextures ?? 0)).AppendLine(" textures applied");
            output.Append("Duplicates merged: ").AppendLine(Number(state?.MergedDuplicates.Count ?? 0));
            if (captured)
                output.Append("Scan: ").Append(Number(scannedGroups)).Append(" unique textures; ")
                    .Append(Number(eligibleGroups)).AppendLine(" eligible");

            AppendSavings(output);
            AppendKeptByReason(output, summary);
            output.AppendLine();
            output.AppendLine("Replacements");
            if (replacements.Count == 0)
            {
                output.AppendLine("  No replacement PNGs were selected.");
            }
            else
            {
                foreach (var replacement in replacements)
                {
                    output.Append("- ").Append(replacement.SourceName);
                    if (replacement.SourcePath.Length > 0) output.Append(" ( ").Append(replacement.SourcePath).Append(" )");
                    output.AppendLine();
                    output.Append("  -> ").Append(replacement.ReplacementName);
                    if (replacement.ReplacementPath.Length > 0) output.Append(" ( ").Append(replacement.ReplacementPath).Append(" )");
                    output.AppendLine();
                    if (replacement.SourceBytes >= 0 && replacement.ReplacementBytes >= 0)
                    {
                        long saving = replacement.SourceBytes - replacement.ReplacementBytes;
                        output.Append("  PNG size: ").Append(Size(replacement.SourceBytes)).Append(" -> ")
                            .Append(Size(replacement.ReplacementBytes)).Append("; available saving ")
                            .AppendLine(Size(saving));
                    }
                    else
                    {
                        output.AppendLine("  PNG size: unavailable");
                    }
                }
            }

            output.AppendLine();
            AppendTextureResults(output);

            output.AppendLine("Notes");
            if (notes.Count == 0) output.AppendLine("  None.");
            else
                foreach (string note in notes) output.AppendLine(note);
            return output.ToString().TrimEnd();
        }

        internal void Emit(GameObject root, SubstitutionState state,
            string folder = AvatarTextureOptimizer.OutputFolder)
        {
            if (emitted) return;
            emitted = true;

            string reportPath = "";
            try
            {
                AutomaticTextureOptimizer.EnsureFolder(folder);
                reportPath = folder.TrimEnd('/') + "/" + ReportFileName(root ? root.name : "(unavailable)", isPlayMode, folder);
                RejectReparsePoint(reportPath);
                RejectReparsePoint(reportPath + ".meta");
                File.WriteAllText(reportPath, Format(root, state), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(reportPath, ImportAssetOptions.ForceSynchronousImport);
                this.reportPath = reportPath;
            }
            catch (Exception e)
            {
                string target = string.IsNullOrEmpty(reportPath) ? Compact(folder) : reportPath;
                Debug.LogWarning("Arclight Optimizer: Could not write report to '" + target + "': " + Compact(e.Message), root);
            }
        }

        // Passes add their savings; after the report is first written, later passes rewrite it in place.
        internal void AddSavings(string category, long bytes, string detail, int unmeasured = 0)
        {
            if (!string.IsNullOrEmpty(detail)) savings.Add((category, bytes, detail, unmeasured));
        }

        internal void Refresh(GameObject root, SubstitutionState state)
        {
            if (reportPath == null) return;
            try
            {
                File.WriteAllText(reportPath, Format(root, state), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(reportPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception e) { Debug.LogWarning("Arclight Optimizer: Could not update report '" + reportPath + "': " + Compact(e.Message), root); }
        }

        private void AppendBuildSavings(StringBuilder output)
        {
            output.Append("Saved this build: ");
            if (savings.Count == 0) { output.AppendLine("nothing measurable (no assets were replaced or merged)."); output.AppendLine(); return; }
            // Two kinds of figure that do not add up: compressed download estimates (textures, audio) and uncompressed mesh data.
            long download = savings.Where(s => s.Category != "Meshes").Sum(s => s.Bytes), mesh = savings.Where(s => s.Category == "Meshes").Sum(s => s.Bytes);
            output.AppendLine((download > 0 || mesh == 0 ? "about " + Size(download) + " estimated download" : "") + (download > 0 && mesh > 0 ? ", and " : "") +
                (mesh > 0 ? Size(mesh) + " of uncompressed mesh data" : "") + ".");
            foreach (var entry in savings)
            {
                output.Append("  ").Append(entry.Category).Append(": ").Append(Size(entry.Bytes)).Append(" (").Append(entry.Detail.TrimEnd(',', ' ')).Append(')');
                if (entry.Unmeasured > 0) output.Append("; ").Append(Number(entry.Unmeasured)).Append(" item(s) could not be estimated");
                output.AppendLine();
            }
            output.AppendLine("  Textures and audio are estimated compressed download sizes for this platform; meshes are uncompressed mesh data.");
            output.AppendLine();
        }

        internal static string Count(int count, string what) => count > 0 ? Number(count) + " " + what + ", " : "";

        private void AppendSavings(StringBuilder output)
        {
            output.Append("Available replacement PNG savings: ");
            if (replacements.Count == 0)
            {
                output.AppendLine("0 bytes (no replacement PNGs selected).");
            }
            else
            {
                var measured = replacements.Where(r => r.SourceBytes >= 0 && r.ReplacementBytes >= 0).ToArray();
                if (measured.Length == 0)
                {
                    output.AppendLine("unavailable (replacement file sizes could not be read).");
                }
                else
                {
                    long source = measured.Sum(r => r.SourceBytes);
                    long generated = measured.Sum(r => r.ReplacementBytes);
                    output.Append(Size(source - generated)).Append(" across ")
                        .Append(Number(measured.Length)).Append(" measured replacement(s) ")
                        .Append('(').Append(Size(source)).Append(" source; ")
                        .Append(Size(generated)).AppendLine(" generated).");
                    if (measured.Length != replacements.Count)
                        output.Append("  Sizes unavailable for ").Append(Number(replacements.Count - measured.Length)).AppendLine(" replacement(s).");
                }
            }
            output.AppendLine("  Encoded PNG file size only; not VRAM use or platform download size. Savings are available from selected replacements and do not imply every assignment was applied.");
        }

        private static string ReportFileName(string name, bool playMode, string folder)
        {
            string sanitized = Regex.Replace(name ?? "", @"[^A-Za-z0-9_-]+", "_").Trim('_', '-');
            if (sanitized.Length == 0) sanitized = "Avatar";
            if (sanitized.Length > MaxNamePartLength) sanitized = sanitized.Substring(0, MaxNamePartLength);

            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(name ?? ""));
            string hash = BitConverter.ToString(digest).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            const string prefix = "ArclightTextureOptimizer_";
            string suffix = (playMode ? "_Play" : "") + "_" + hash + ".txt";
            // Keep the full-name hash and leave space for Unity's .meta under Windows MAX_PATH.
            int nameBudget = 259 - Path.GetFullPath(Path.Combine(folder, prefix + suffix + ".meta")).Length;
            if (nameBudget < 0) throw new IOException("Report folder path is too long even without an avatar name.");
            if (sanitized.Length > nameBudget) sanitized = sanitized.Substring(0, nameBudget);
            return prefix + sanitized + suffix;
        }

        // Textures kept at their original, one line per reason (numbers and the part after the first sentence folded), most
        // common first, so a reader sees what keeps the most textures before the per-texture details.
        private static void AppendKeptByReason(StringBuilder output, GenerationSummary summary)
        {
            var kept = (summary?.TextureResults ?? new List<TextureGenerationResult>())
                .Where(r => (r.Outcome == TextureResultKind.Skipped || r.Outcome == TextureResultKind.NotSmaller) && r.Source)
                .GroupBy(r => Regex.Replace(Regex.Split(ReasonText(r.Reason) is string text && text.Length > 0 ? text : "No reason recorded", @"(?<=[.;])\s|\s\(")[0].TrimEnd('.', ';'), @"\d[\d,.]*", "#"))
                .Select(g => (Group: g, Bytes: g.Select(r => r.Source).Distinct().Sum(t => CostHints.Bytes(t))))
                .OrderByDescending(g => g.Bytes).ThenByDescending(g => g.Group.Count()).ToList();
            if (kept.Count == 0) return;
            output.AppendLine();
            output.AppendLine("Kept textures, by reason (most texture memory first)");
            foreach (var (group, bytes) in kept)
            {
                var names = group.Select(r => r.Source.name).Distinct().ToList();
                output.Append("  ").Append(Size(bytes)).Append(", ").Append(Number(group.Count())).Append(" x ").Append(group.Key).Append(": ")
                    .AppendLine(string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
            }
        }

        private void AppendTextureResults(StringBuilder output)
        {
            output.AppendLine();
            output.AppendLine("Per-texture results");
            if (!captured)
            {
                output.AppendLine("  No texture scan was captured.");
                return;
            }
            output.Append("Scanned texture assets: ").Append(Number(textureResults.Count))
                .Append(" (").Append(Number(scannedGroups)).Append(" unique scan groups; ")
                .Append(Number(duplicateAliases)).AppendLine(" duplicate aliases)");
            output.AppendLine("Preparation time measures this run per unique group, including cache lookup/validation and generation; shared scan and duplicate-detection time is excluded.");
            var outcomes = textureResults.GroupBy(result => result.Outcome)
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
            output.Append("Outcomes: ").AppendLine(outcomes.Length == 0 ? "none" :
                string.Join(", ", outcomes.Select(group => Number(group.Count()) + " " + group.Key.ToLowerInvariant())));

            foreach (var result in textureResults)
            {
                output.Append("- ").Append(result.SourceName);
                if (!string.IsNullOrEmpty(result.SourcePath)) output.Append(" (").Append(result.SourcePath).Append(")");
                output.Append(": ").AppendLine(result.Outcome);
                if (!string.IsNullOrEmpty(result.Reason)) output.Append("  Reason: ").AppendLine(result.Reason);
                if (!string.IsNullOrEmpty(result.Operation)) output.Append("  Operation: ").AppendLine(result.Operation);
                if (!string.IsNullOrEmpty(result.FillColour)) output.Append("  Fill colour: ").AppendLine(result.FillColour);
                if (!string.IsNullOrEmpty(result.PaddingMode)) output.Append("  Padding mode: ").AppendLine(result.PaddingMode);
                if (!string.IsNullOrEmpty(result.ReplacementPath))
                {
                    output.Append("  Replacement: ").AppendLine(result.ReplacementPath);
                    if (result.SourceBytes >= 0 && result.ReplacementBytes >= 0)
                        output.Append("  PNG size: ").Append(Size(result.SourceBytes)).Append(" -> ")
                            .AppendLine(Size(result.ReplacementBytes));
                    else output.AppendLine("  PNG size: unavailable");
                    if (result.CompressedSourceBytes > 0)
                        output.Append("  Estimated compressed size: ").Append(Size(result.CompressedSourceBytes)).Append(" -> ")
                            .AppendLine(Size(result.CompressedReplacementBytes));
                }
                if (result.PreparationMilliseconds >= 0)
                    output.Append("  " + result.PreparationLabel + ": ").Append(Number(result.PreparationMilliseconds)).AppendLine(" ms");
            }
        }

        private static string FallbackOutcome(TextureGroup group, GeneratedTextureMapping mapping, bool optimizeTextures)
        {
            if (mapping != null) return "Selected";
            if (!group.ActiveUses.Any()) return "Not sampled";
            if (group.Warning != null) return "Skipped";
            return optimizeTextures ? "Not processed" : "Not optimized";
        }

        private static string FallbackReason(TextureGroup group, GeneratedTextureMapping mapping, bool optimizeTextures)
        {
            if (mapping != null) return "A replacement mapping was selected.";
            if (!group.ActiveUses.Any()) return ReasonText(group.Warning ?? "No active sampled uses were found.");
            if (group.Warning != null) return ReasonText(group.Warning);
            return optimizeTextures
                ? "Preparation stopped before a result was recorded; see Notes."
                : "Texture optimization was disabled for this run; the original was retained.";
        }

        private static string OutcomeName(TextureResultKind outcome)
        {
            switch (outcome)
            {
                case TextureResultKind.Generated: return "Generated";
                case TextureResultKind.Reused: return "Reused";
                case TextureResultKind.Unchanged: return "Unchanged";
                case TextureResultKind.NotSmaller: return "No size reduction";
                case TextureResultKind.Skipped: return "Skipped";
                case TextureResultKind.NotSampled: return "Not sampled";
                default: return "Not processed";
            }
        }

        private static string ReasonText(string reason)
        {
            return string.Join(" | ", (reason ?? "").Replace("\r", "").Split('\n')
                .Select(line => Compact(line.Replace(SkippedPrefix, "")))
                .Where(line => line.Length > 0));
        }

        private static void ApplyMapping(TextureSnapshot row, GeneratedTextureMapping mapping)
        {
            string sourcePath = AssetDatabase.GetAssetPath(mapping.source) ?? "";
            string replacementPath = AssetDatabase.GetAssetPath(mapping.replacement) ?? "";
            row.ReplacementPath = Compact(replacementPath);
            row.SourceBytes = FileSize(sourcePath);
            row.ReplacementBytes = FileSize(replacementPath);
            row.CompressedSourceBytes = mapping.compressedSourceBytes;
            row.CompressedReplacementBytes = mapping.compressedReplacementBytes;
            if (!mapping.hasReportMetadata)
            {
                row.Operation = "Unavailable (legacy cache has no report metadata)";
                row.FillColour = "Unavailable (legacy cache has no report metadata)";
                row.PaddingMode = "Unavailable (legacy cache has no report metadata)";
                return;
            }

            var colour = mapping.background;
            row.FillColour = "RGBA(" + colour.r.ToString(CultureInfo.InvariantCulture) + ", " +
                colour.g.ToString(CultureInfo.InvariantCulture) + ", " +
                colour.b.ToString(CultureInfo.InvariantCulture) + ", " +
                colour.a.ToString(CultureInfo.InvariantCulture) + ")";
            row.Operation = mapping.repairedPadding ? "Mipmap padding repair" : "Texture optimization";
            string action = mapping.repairedPadding
                ? "Rebuild existing padding from sampled UV coverage"
                : "Clear unused pixels and extend UV island edges";
            row.PaddingMode = action + "; edge extension " + mapping.extensionRadiusX.ToString(CultureInfo.InvariantCulture) +
                " x " + mapping.extensionRadiusY.ToString(CultureInfo.InvariantCulture) +
                " source PNG texels; configured radius " + mapping.padding.ToString(CultureInfo.InvariantCulture) + " imported pixels." +
                (mapping.standaloneFormat == 0 ? "" : " PC format changed to " + (UnityEditor.TextureImporterFormat)mapping.standaloneFormat +
                    ": its shaders never read the dropped channels.");
        }

        internal static string PackageVersion()
        {
            try { return PackageInfo.FindForAssembly(typeof(OptimizationLog).Assembly)?.version ?? "unavailable"; }
            catch { return "unavailable"; }
        }
        private static void RejectReparsePoint(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("report path is a filesystem link");
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("report path is a directory");
        }
        private static long FileSize(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return -1;
            try
            {
                string path = assetPath.Replace('\\', '/');
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path)) return -1;
                return new FileInfo(path).Length;
            }
            catch { return -1; }
        }

        private static string Size(long bytes)
        {
            double magnitude = Math.Abs((double)bytes);
            if (magnitude >= 1048576) return (bytes / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            if (magnitude >= 1024) return (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            return Number(bytes) + " bytes";
        }
        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string Compact(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();

        private sealed class ReplacementSnapshot
        {
            public string SourceName;
            public string SourcePath;
            public string ReplacementName;
            public string ReplacementPath;
            public long SourceBytes;
            public long ReplacementBytes;
        }

        private sealed class TextureSnapshot
        {
            public string SourceName;
            public string SourcePath;
            public string Outcome;
            public string Reason;
            public string Operation;
            public string FillColour;
            public string PaddingMode;
            public string ReplacementPath;
            public long PreparationMilliseconds = -1;
            public string PreparationLabel;
            public long SourceBytes = -1;
            public long ReplacementBytes = -1;
            public long CompressedSourceBytes, CompressedReplacementBytes; // Estimated compressed bundle bytes; 0 = not estimated.
        }
    }
}