using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // One-click fixes for findings whose fix is unambiguous. They run only when the user presses the button, and every one can be
    // undone with Edit > Undo. Each returns a sentence saying what it did.
    internal static class Fixes
    {
        // What a missing-parameter finding's Data holds: the names to add and their types.
        internal sealed class Parameters { public List<(string Name, AnimatorControllerParameterType Type)> Names = new List<(string, AnimatorControllerParameterType)>(); }
        // Layers to remove by name (only when no Layer Control could be pointing at a layer index that would shift).
        internal sealed class Layers { public List<string> Names = new List<string>(); }
        internal sealed class Scripts { public List<GameObject> Objects = new List<GameObject>(); }
        // Reference fields showing "Missing": (component, property path).
        internal sealed class References { public List<(Object Component, string Path)> Fields = new List<(Object, string)>(); }
        // Animator parameters to list in Expression Parameters, not synced (face tracking fills them locally over OSC).
        internal sealed class ExpressionAdds { public List<(string Name, AnimatorControllerParameterType Type)> Names = new List<(string, AnimatorControllerParameterType)>(); }
        // Curves animating things the avatar doesn't have.
        internal sealed class Curves { public List<(AnimationClip Clip, EditorCurveBinding Binding, bool ObjectReference)> Bindings = new List<(AnimationClip, EditorCurveBinding, bool)>(); }

        // A clip the fix may edit: its own .anim file in Assets (not inside a model, a controller or a read-only package).
        private static bool Editable(AnimationClip clip) => clip && AssetDatabase.IsMainAsset(clip) &&
            AssetDatabase.GetAssetPath(clip) is string path && path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);

        // Not a file inside a package: those are read-only (VCC reinstalls them), so a fix there would not last.
        internal static bool Writable(Object asset) => !AssetDatabase.GetAssetPath(asset).StartsWith("Packages/", StringComparison.Ordinal);

        // The asset files a fix changes, so they can be backed up first.
        private static List<Object> Changes(Finding finding) =>
            finding.Data is Curves c ? c.Bindings.Select(b => b.Clip).Where(Editable).Distinct().Cast<Object>().ToList()
            : (finding.Data is Parameters || finding.Data is Layers || finding.Data is ExpressionAdds) && finding.Target && AssetDatabase.GetAssetPath(finding.Target).Length > 0 ? new List<Object> { finding.Target }
            : new List<Object>();

        // The button label and action, or null when the finding has no safe fix (or what it would change is gone).
        internal static (string Label, Func<string> Apply)? For(Finding finding)
        {
            switch (finding.Data)
            {
                case Parameters p when finding.Target is AnimatorController controller && Writable(controller):
                    return ("Add them", () =>
                    {
                        Undo.RecordObject(controller, "Add missing parameters");
                        var have = new HashSet<string>(controller.parameters.Select(q => q.name), StringComparer.Ordinal);
                        var added = p.Names.Where(n => have.Add(n.Name)).ToList();
                        foreach (var (name, type) in added) controller.AddParameter(name, type);
                        EditorUtility.SetDirty(controller);
                        return "Added " + added.Count + " parameter(s) to " + controller.name + ".";
                    });
                case Layers l when finding.Target is AnimatorController controller && Writable(controller):
                    return ("Remove them", () =>
                    {
                        Undo.RegisterCompleteObjectUndo(controller, "Remove unused layers");
                        int removed = 0;
                        for (int i = controller.layers.Length - 1; i > 0; i--)
                            if (l.Names.Contains(controller.layers[i].name)) { controller.RemoveLayer(i); removed++; }
                        EditorUtility.SetDirty(controller);
                        return "Removed " + removed + " layer(s) from " + controller.name + ".";
                    });
                case Scripts s:
                    return ("Remove them", () =>
                    {
                        int removed = 0, kept = 0;
                        foreach (var o in s.Objects.Where(o => o))
                        {
                            int before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(o);
                            Undo.RegisterCompleteObjectUndo(o, "Remove missing scripts");
                            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(o);
                            int after = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(o);
                            removed += before - after; kept += after;
                        }
                        return "Removed " + removed + " missing script(s)." + (kept > 0 ? " " + kept + " sit inside a prefab; open the prefab to remove them there." : "");
                    });
                case References r:
                    return ("Clear them", () =>
                    {
                        int cleared = 0;
                        foreach (var group in r.Fields.Where(f => f.Component).GroupBy(f => f.Component))
                            using (var serialized = new SerializedObject(group.Key))
                            {
                                foreach (var (_, path) in group)
                                {
                                    var property = serialized.FindProperty(path);
                                    if (property == null || property.objectReferenceValue || property.objectReferenceInstanceIDValue == 0) continue;
                                    property.objectReferenceValue = null;
                                    cleared++;
                                }
                                serialized.ApplyModifiedProperties(); // Recorded for Undo.
                            }
                        return "Cleared " + cleared + " missing reference(s). References to objects outside the avatar were left for you to point at the right object.";
                    });
                case ExpressionAdds e when finding.Target && Writable(finding.Target):
                    return ("Add them", () =>
                    {
                        int added = 0;
                        using (var serialized = new SerializedObject(finding.Target))
                        {
                            var array = serialized.FindProperty("parameters");
                            var have = new HashSet<string>(Enumerable.Range(0, array.arraySize).Select(i => array.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue), StringComparer.Ordinal);
                            foreach (var (name, type) in e.Names.Where(n => have.Add(n.Name)))
                            {
                                array.arraySize++;
                                var entry = array.GetArrayElementAtIndex(array.arraySize - 1);
                                entry.FindPropertyRelative("name").stringValue = name;
                                entry.FindPropertyRelative("valueType").intValue = type == AnimatorControllerParameterType.Int ? 0 : type == AnimatorControllerParameterType.Bool ? 2 : 1;
                                entry.FindPropertyRelative("defaultValue").floatValue = 0;
                                var saved = entry.FindPropertyRelative("saved"); if (saved != null) saved.boolValue = false;
                                var synced = entry.FindPropertyRelative("networkSynced"); if (synced != null) synced.boolValue = false;
                                added++;
                            }
                            serialized.ApplyModifiedProperties(); // Recorded for Undo.
                        }
                        return "Added " + added + " parameter(s) to " + finding.Target.name + ", not synced (they use no synced bits).";
                    });
                case Curves c when c.Bindings.Any(b => Editable(b.Clip)):
                    return ("Remove them", () =>
                    {
                        int removed = 0;
                        foreach (var group in c.Bindings.Where(b => Editable(b.Clip)).GroupBy(b => b.Clip))
                        {
                            Undo.RecordObject(group.Key, "Remove animations of missing things");
                            foreach (var (_, binding, objectReference) in group)
                            {
                                if (objectReference) AnimationUtility.SetObjectReferenceCurve(group.Key, binding, null);
                                else AnimationUtility.SetEditorCurve(group.Key, binding, null);
                                removed++;
                            }
                            EditorUtility.SetDirty(group.Key);
                        }
                        int skipped = c.Bindings.Count(b => !Editable(b.Clip));
                        return "Removed " + removed + " curve(s) from " + c.Bindings.Select(b => b.Clip).Where(Editable).Distinct().Count() + " clip(s)." +
                            (skipped > 0 ? " " + skipped + " sit in clips inside a model, controller or package, which were left alone." : "");
                    });
                default:
                    return null;
            }
        }

        // Fix history: every fix is logged; a controller is copied before it changes, so it can be restored exactly after Unity's own
        // Undo history is gone. Kept in the project (not the Optimizer's cache, which cleans itself).
        internal static string Folder = "Assets/Arclight/Analyzer"; // Tests point it elsewhere.
        private static string HistoryFile => Folder + "/FixHistory.json";

        [Serializable] internal sealed class Entry { public string time, avatar, action, asset, backup, hashAfter; }
        [Serializable] private sealed class Log { public List<Entry> entries = new List<Entry>(); }

        internal static List<Entry> History()
        {
            try { return File.Exists(HistoryFile) ? JsonUtility.FromJson<Log>(File.ReadAllText(HistoryFile))?.entries ?? new List<Entry>() : new List<Entry>(); }
            catch (Exception) { return new List<Entry>(); }
        }

        private static void Save(List<Entry> entries)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(HistoryFile, JsonUtility.ToJson(new Log { entries = entries }, true));
            AssetDatabase.ImportAsset(HistoryFile);
        }

        private static string Hash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }

        // Saves every object in the asset's file (states, transitions and blend trees are separate sub-assets of a controller), so the
        // file on disk holds any unsaved editor work.
        private static void SaveFile(string path) => AssetDatabase.SaveAssetIfDirty(AssetDatabase.GUIDFromAssetPath(path));

        private static bool Unsaved(string path) => AssetDatabase.LoadAllAssetsAtPath(path).Any(o => o && EditorUtility.IsDirty(o));

        // The asset files this fix would change, for a confirmation when another avatar also uses them.
        internal static List<string> Files(Finding finding) => Changes(finding).Select(AssetDatabase.GetAssetPath).ToList();

        // Other avatars in the open scenes whose playable layers use one of these files (a shared controller, or a clip in one).
        internal static List<string> SharedWith(Finding finding, GameObject self)
        {
            var files = new HashSet<string>(Files(finding), StringComparer.Ordinal);
            if (files.Count == 0) return new List<string>();
            var others = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var descriptor in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).Where(c => c && c.GetType().Name == "VRCAvatarDescriptor"))
                {
                    if (descriptor.gameObject == self) continue;
                    var used = AvatarAnalyzer.Read(descriptor.gameObject).Playables.SelectMany(p => new[] { AssetDatabase.GetAssetPath(p.Controller) }
                        .Concat(AvatarAnalyzer.ClipsOf(p.Controller).Select(AssetDatabase.GetAssetPath)));
                    if (used.Any(files.Contains)) others.Add(descriptor.gameObject.name);
                }
            }
            return others.Distinct().ToList();
        }

        // Applies the finding's fix: backs up the files it changes, runs it, and logs it. Returns what was done.
        internal static string Apply(Finding finding, string avatar)
        {
            var fix = For(finding);
            if (!fix.HasValue) return null;
            var entry = new Entry { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), avatar = avatar };
            // Each file this fix changes is copied first; several files are kept one per line in asset, backup and hashAfter.
            var changes = Changes(finding);
            var assets = new List<string>();
            var backups = new List<string>();
            if (changes.Count > 0)
            {
                // "Backups~" is hidden from Unity (no import, no .meta); a unique name keeps two fixes in the same second apart.
                string folder = Folder + "/Backups~/" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Directory.CreateDirectory(folder);
                foreach (var asset in changes)
                {
                    string path = AssetDatabase.GetAssetPath(asset);
                    SaveFile(path);
                    string backup = folder + "/" + assets.Count + "_" + Path.GetFileName(path) + ".bak";
                    File.Copy(path, backup, true);
                    assets.Add(path);
                    backups.Add(backup);
                }
                entry.asset = string.Join("\n", assets);
                entry.backup = string.Join("\n", backups);
            }
            try { entry.action = fix.Value.Apply(); }
            catch (Exception e)
            {
                // Nothing was logged; put the files back as they were and leave no orphan backup.
                for (int i = 0; i < assets.Count; i++) { File.Copy(backups[i], assets[i], true); AssetDatabase.ImportAsset(assets[i], ImportAssetOptions.ForceUpdate); }
                if (backups.Count > 0) Directory.Delete(Path.GetDirectoryName(backups[0]), true);
                return "The fix failed and nothing was changed: " + e.Message;
            }
            if (changes.Count > 0)
            {
                foreach (string path in assets) SaveFile(path);
                entry.hashAfter = string.Join("\n", assets.Select(Hash));
            }
            var entries = History();
            entries.Add(entry);
            Save(entries);
            return entry.action;
        }

        // Puts the backed-up controller back. False when the user declines because it changed since the fix (or the backup is gone).
        internal static bool Restore(Entry entry, bool ask = true)
        {
            if (!CanRestore(entry)) return false;
            var assets = entry.asset.Split('\n');
            var backups = entry.backup.Split('\n');
            var hashes = (entry.hashAfter ?? "").Split('\n');
            // Unsaved edits in the editor count as changes too: restoring would throw them away.
            bool changedSince = assets.Select((a, i) => i >= hashes.Length || Unsaved(a) || Hash(a) != hashes[i]).Any(c => c);
            if (ask && changedSince &&
                !EditorUtility.DisplayDialog("Restore " + Label(entry) + "?",
                    "It was changed after this fix (saved or not). Restoring puts back the version from before the fix, so those later changes are lost too.", "Restore", "Cancel"))
                return false;
            for (int i = 0; i < assets.Length; i++)
            {
                File.Copy(backups[i], assets[i], true);
                AssetDatabase.ImportAsset(assets[i], ImportAssetOptions.ForceUpdate);
            }
            var entries = History();
            entries.Add(new Entry { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), avatar = entry.avatar, action = "Restored " + Label(entry) + " to before: " + entry.action });
            Save(entries);
            return true;
        }

        // Every backed-up file of the entry is still there.
        internal static bool CanRestore(Entry entry) =>
            !string.IsNullOrEmpty(entry.backup) && !string.IsNullOrEmpty(entry.asset) &&
            entry.backup.Split('\n').All(File.Exists) && entry.asset.Split('\n').All(File.Exists) && entry.backup.Split('\n').Length == entry.asset.Split('\n').Length;

        // "FX.controller", or "3 files".
        internal static string Label(Entry entry)
        {
            var assets = (entry.asset ?? "").Split('\n');
            return assets.Length == 1 ? Path.GetFileName(assets[0]) : assets.Length + " files";
        }

        // Backups live in "Backups~" (hidden from Unity); older versions used "Backups".
        private static IEnumerable<string> BackupFolders() => new[] { Folder + "/Backups~", Folder + "/Backups" }.Where(Directory.Exists);

        internal static long BackupBytes() => BackupFolders().Sum(f => new DirectoryInfo(f).GetFiles("*.bak", SearchOption.AllDirectories).Sum(x => x.Length));

        // Deletes backups older than the given number of days; their history entries stay, without Restore.
        internal static int DeleteBackups(int olderThanDays)
        {
            int deleted = 0;
            foreach (string root in BackupFolders().ToList())
                foreach (var dir in new DirectoryInfo(root).GetDirectories().Where(d => d.CreationTime < DateTime.Now.AddDays(-olderThanDays)))
                {
                    if (!AssetDatabase.DeleteAsset(root + "/" + dir.Name)) { dir.Delete(true); File.Delete(dir.FullName + ".meta"); }
                    deleted++;
                }
            return deleted;
        }
    }
}
