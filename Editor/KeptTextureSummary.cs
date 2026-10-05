using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Totals why textures were kept across every avatar in the open scenes. Each avatar is built on a
    // temporary clone in a preview scene through NDMF, exactly as Play Mode does, so Modular Avatar merges,
    // animation merging and other build steps are reflected. Source scenes and assets are unchanged;
    // like Play Mode, the build fills the texture cache and refreshes each avatar's build report.
    internal static class KeptTextureSummary
    {
        internal const string FileName = "ArclightTextureOptimizer_KeptTextures.txt";

        internal sealed class AvatarResult
        {
            internal string Avatar;
            internal int Textures, Applied;
            internal GenerationSummary Summary;
            internal readonly List<(string Reason, string Shader, long Bytes)> Kept = new List<(string, string, long)>();
            internal string Failure;
        }

        private static List<AvatarResult> collecting;

        // Called at the end of each enabled optimizer run; ignored unless a summary is being collected.
        internal static void Record(GameObject root, ScanResult scan, SubstitutionState state)
        {
            if (collecting == null) return;
            var result = new AvatarResult { Avatar = root ? root.name : "(unavailable)", Summary = state.Summary, Applied = state.AppliedTextures };
            if (scan != null)
            {
                foreach (var group in scan.Groups.Where(g => g.ActiveUses.Any()))
                {
                    result.Textures++;
                    if (group.Warning == null) continue;
                    // Count each texture once, under the first specific reason of its active uses.
                    var use = group.ActiveUses.FirstOrDefault(u => u.Warning != null) ?? group.ActiveUses.First();
                    string reason = use.Warning ?? group.Warning.Replace("Skipped; original texture retained for all its assignments.", "");
                    var shader = use.Material ? use.Material.shader : null;
                    result.Kept.Add((Normalize(reason), shader ? shader.name : "(missing)", group.Source ? CostHints.Bytes(group.Source) : 0));
                }
            }
            collecting.Add(result);
        }

        [MenuItem("Tools/Arclight/Optimizer/Summarize Kept Textures in Open Scenes")]
        private static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Arclight Optimizer", "Exit Play Mode before summarizing.", "OK");
                return;
            }
            var avatars = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Where(scene => scene.isLoaded)
                .SelectMany(scene => scene.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                .Where(config => config.enabled && RuntimeUtil.IsAvatarRoot(config.transform))
                .Select(config => config.gameObject).Distinct().ToArray();
            if (avatars.Length == 0)
            {
                EditorUtility.DisplayDialog("Arclight Optimizer",
                    "No avatar root with an enabled Arclight Optimizer component was found in the open scenes.", "OK");
                return;
            }
            try
            {
                string path = Summarize(avatars, (name, fraction) =>
                    EditorUtility.DisplayProgressBar("Arclight Optimizer", "Building " + name + "…", fraction));
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<TextAsset>(path));
                EditorUtility.OpenWithDefaultApp(path);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Arclight Optimizer", "Summary stopped: " + e.Message, "OK");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // Builds each avatar on a clone and writes the summary; returns its asset path.
        internal static string Summarize(IReadOnlyList<GameObject> avatars, Action<string, float> progress = null,
            string outputFolder = AvatarTextureOptimizer.OutputFolder)
        {
            var results = new List<AvatarResult>();
            string temporary = "Assets/__ArclightKeptTextureSummary_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(temporary));
            var scene = EditorSceneManager.NewPreviewScene();
            collecting = results;
            try
            {
                for (int i = 0; i < avatars.Count; i++)
                {
                    var avatar = avatars[i];
                    progress?.Invoke(avatar.name, (float)i / avatars.Count);
                    int before = results.Count;
                    var clone = Object.Instantiate(avatar);
                    clone.name = avatar.name;
                    SceneManager.MoveGameObjectToScene(clone, scene);
                    try
                    {
                        using (new OverrideTemporaryDirectoryScope(temporary)) AvatarProcessor.ProcessAvatar(clone);
                    }
                    catch (Exception e)
                    {
                        results.Add(new AvatarResult { Avatar = avatar.name, Failure = e.Message });
                    }
                    finally { Object.DestroyImmediate(clone); }
                    if (results.Count == before)
                        results.Add(new AvatarResult { Avatar = avatar.name, Failure = "The optimizer did not run (check NDMF Apply on Build)." });
                }
            }
            finally
            {
                collecting = null;
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(temporary);
            }

            AutomaticTextureOptimizer.EnsureFolder(outputFolder);
            string path = outputFolder + "/" + FileName;
            File.WriteAllText(path, Format(results), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        internal static string Format(IReadOnlyList<AvatarResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine("Arclight Optimizer: kept-texture summary");
            output.Append("Created (local): ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            output.Append("Target platform: ").AppendLine(EditorUserBuildSettings.activeBuildTarget.ToString());
            var built = results.Where(r => r.Failure == null).ToArray();
            int kept = built.Sum(r => r.Kept.Count);
            output.Append("Avatars: ").Append(results.Count).Append(" (").Append(built.Length).AppendLine(" built)");
            output.Append("Textures sampled: ").AppendLine(built.Sum(r => r.Textures).ToString(CultureInfo.InvariantCulture));
            output.Append("  Replaced: ").AppendLine(built.Sum(r => r.Applied).ToString(CultureInfo.InvariantCulture));
            output.Append("  No changes needed: ").AppendLine(built.Sum(r => r.Summary.Unchanged).ToString(CultureInfo.InvariantCulture));
            output.Append("  No PNG size saving: ").AppendLine(built.Sum(r => r.Summary.NotSmaller).ToString(CultureInfo.InvariantCulture));
            output.Append("  Kept by a safety check: ").AppendLine(kept.ToString(CultureInfo.InvariantCulture));
            output.AppendLine();

            output.AppendLine("Why textures were kept (texture memory, most first; texture count)");
            if (kept == 0) output.AppendLine("  None.");
            foreach (var reason in built.SelectMany(r => r.Kept).GroupBy(k => k.Reason)
                         .OrderByDescending(g => g.Sum(k => k.Bytes)).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                output.Append(reason.Count().ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
                    .Append((reason.Sum(k => k.Bytes) / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6)).Append(" MiB  ").AppendLine(reason.Key);
                output.Append("       Shaders: ").AppendLine(string.Join(", ", reason.GroupBy(k => k.Shader)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key + " (" + g.Count() + ")")));
            }
            output.AppendLine();

            output.AppendLine("Per avatar");
            foreach (var result in results)
            {
                output.Append("- ").Append(result.Avatar).Append(": ");
                if (result.Failure != null) { output.Append("not built: ").AppendLine(result.Failure); continue; }
                output.Append(result.Textures).Append(" textures; ").Append(result.Applied).Append(" replaced; ")
                    .Append(result.Summary.Unchanged).Append(" no changes needed; ")
                    .Append(result.Summary.NotSmaller).Append(" no PNG size saving; ")
                    .Append(result.Kept.Count).AppendLine(" kept");
            }
            output.AppendLine();
            output.AppendLine("Each avatar's own build report in this folder lists the kept textures by name.");
            return output.ToString().TrimEnd();
        }

        // Groups similar reasons: property names and numbers vary between materials.
        internal static string Normalize(string reason)
        {
            string line = (reason ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no reason given)";
            line = Regex.Replace(line, @"(?<![A-Za-z0-9])_[A-Za-z][A-Za-z0-9_]*", "<property>");
            line = Regex.Replace(line, @"(?<![A-Za-z<])\d+(\.\d+)?", "#");
            return Regex.Replace(line, @"<property>(, <property>)+", "<properties>");
        }
    }
}
