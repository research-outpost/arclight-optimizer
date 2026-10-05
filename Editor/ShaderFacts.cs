using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // What compiling a shader variant told us (vertex inputs, uniforms read, vertex ID use), kept across editor sessions
    // in Library/AvatarTextureOptimizer/shader-facts.txt. Compiling lilToon's variants takes minutes per avatar, and the
    // in-memory caches are lost at every domain reload and in every batch build. A fact is keyed by the shader asset
    // (GUID, or name for Unity's built-in shaders), its dependency hash (the file and its includes), the keywords, the
    // target platform, the Unity version, this format's version and a hash of the caller's code tables (keyword lists,
    // lighting sets, stages), so an edited shader, include, Unity upgrade or Arclight change is compiled again.
    // Only shaders whose content the key covers go to disk: a .shader file, a built-in shader, or a lilToon container
    // (.lilcontainer, lilSSAO's entries), keyed by the shader source it generated, lilToon's own source fingerprint and every
    // file beside the container (its inserts), since its importer does not declare those inputs. A shader made in memory
    // (no path) or by another importer (ORL, ShaderCore) is compiled in every session instead.
    internal static class ShaderFacts
    {
        private const string Version = "2";
        private const string FileName = "Library/AvatarTextureOptimizer/shader-facts.txt";
        private const long CompactAbove = 4L * 1024 * 1024;
        private static Dictionary<string, string> facts;
        private const string End = "\t.";

        // Null when this shader's facts must not outlive the session.
        internal static string Key(Material material, string platform, string code)
        {
            var shader = material.shader;
            string path = AssetDatabase.GetAssetPath(shader);
            string id;
            if (path.StartsWith("Resources/", StringComparison.Ordinal)) id = "builtin:" + shader.name;
            else if ((path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lilcontainer", StringComparison.OrdinalIgnoreCase)) && AssetDatabase.AssetPathToGUID(path) is string guid && guid.Length > 0)
                id = guid + ":" + shader.name;
            else return null;
            string dependencies = Dependencies(shader, new HashSet<Shader>());
            if (dependencies == null) return null;
            return Version + "|" + code + "|" + Application.unityVersion + "|" + platform + "|" + id + "|" + dependencies + "|" +
                string.Join(" ", new SortedSet<string>(material.shaderKeywords, StringComparer.Ordinal));
        }

        private static readonly Dictionary<string, string[]> UsePassTargets = new Dictionary<string, string[]>();

        // The dependency hash of the shader's file (which follows its includes) and of every shader its UsePass lines name,
        // which it does not follow (lilToon's entries take their passes from other files); null when a target is missing.
        private static string Dependencies(Shader shader, HashSet<Shader> seen)
        {
            if (!shader) return null;
            if (!seen.Add(shader)) return "";
            string path = AssetDatabase.GetAssetPath(shader);
            string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            bool container = path.EndsWith(".lilcontainer", StringComparison.OrdinalIgnoreCase);
            if (container) { hash = ContainerInputs(path); if (hash == null) return null; }
            else if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) return hash; // Built-in: no file to read.
            if (!UsePassTargets.TryGetValue(path + "|" + hash, out var targets))
            {
                try
                {
                    targets = System.Text.RegularExpressions.Regex.Matches(container ? ContainerSource(path) : System.IO.File.ReadAllText(path), "UsePass\\s+\"([^\"]+)/[^/\"]+\"")
                        .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
                }
                catch (Exception) { return null; }
                UsePassTargets[path + "|" + hash] = targets;
            }
            var text = new System.Text.StringBuilder(hash);
            foreach (string target in targets)
            {
                string other = Dependencies(Shader.Find(target), seen);
                if (other == null) return null;
                text.Append('+').Append(other);
            }
            return text.ToString();
        }

        // The source lilToon's importer generated for a container (its "Shader Source" sub-asset), or null.
        private static string ContainerSource(string path) =>
            AssetDatabase.LoadAllAssetsAtPath(path).OfType<TextAsset>().FirstOrDefault(t => t.name == "Shader Source")?.text;

        // What a container's compiled variants depend on: the generated source, lilToon's source files (LilToonSourceGuard) and
        // every file in the container's own folder (the inserts it includes by name; their nested "Includes/" paths are
        // lilToon's); null when any cannot be read.
        private static string ContainerInputs(string path)
        {
            try
            {
                string source = ContainerSource(path);
                if (source == null || LilToonSourceGuard.Validate() != null || LilToonSourceGuard.SourceFingerprint == null) return null;
                string folder = Path.GetDirectoryName(path);
                var files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => f + "=" + FingerprintService.Hash(File.ReadAllBytes(f)));
                return "container:" + FingerprintService.Hash(System.Text.Encoding.UTF8.GetBytes(
                    source + "\n" + LilToonSourceGuard.SourceFingerprint + "\n" + string.Join("\n", files)));
            }
            catch (Exception) { return null; }
        }

        internal static bool TryGet(string kind, string key, out string value)
        {
            value = null;
            if (key == null) return false;
            Load();
            return facts.TryGetValue(kind + "\t" + key, out value);
        }

        internal static void Put(string kind, string key, string value)
        {
            if (key == null) return;
            Load();
            string entry = kind + "\t" + key;
            if (facts.TryGetValue(entry, out string old) && old == value) return;
            facts[entry] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                File.AppendAllText(FileName, Line(entry, value));
            }
            catch (IOException) { } // Only a cache: the fact is recomputed next time.
            catch (UnauthorizedAccessException) { }
        }

        private static string Line(string entry, string value) => entry.Replace("\n", " ") + "\t" + value.Replace("\n", " ") + End + "\n";

        private static void Load()
        {
            if (facts != null) return;
            facts = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FileName)) return;
                facts = Parse(File.ReadAllLines(FileName));
                // Every changed shader hash leaves its old lines behind; past a few megabytes, keep only the latest of each.
                // Keys of old shader hashes never repeat, so when that still leaves more than half, start again empty.
                if (new FileInfo(FileName).Length > CompactAbove)
                {
                    string compact = string.Concat(facts.Select(p => Line(p.Key, p.Value)));
                    if (compact.Length > CompactAbove / 2) { facts.Clear(); compact = ""; }
                    File.WriteAllText(FileName, compact);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Later lines win. A line is used only with its end marker, so a torn write (a crash mid-line) is never read as a
        // shorter value.
        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string full in lines)
            {
                if (!full.EndsWith(End, StringComparison.Ordinal)) continue;
                string line = full.Substring(0, full.Length - End.Length);
                int last = line.LastIndexOf('\t');
                int first = line.IndexOf('\t');
                if (first <= 0 || last <= first) continue;
                parsed[line.Substring(0, last)] = line.Substring(last + 1);
            }
            return parsed;
        }
    }
}
