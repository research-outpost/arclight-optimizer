using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Pick an avatar (or check every avatar in the open scenes), press Check, read the list. The avatar is only changed when you press
    // a card's fix button, and that can be undone.
    internal sealed class AnalyzerWindow : EditorWindow
    {
        private GameObject avatar;
        // The avatar the cards shown belong to (the field above can be changed without checking again).
        private GameObject checkedAvatar;
        private bool checkedBuild, showIgnored;
        private List<Finding> findings;
        private string checkedName, buildError, fixedNote;
        private int syncedBits;
        private Vector2 scroll;
        // Check all: one row per avatar in the open scenes.
        private List<(GameObject Avatar, List<Finding> Findings, int Bits, string Error)> all;
        private readonly Dictionary<Severity, bool> open = new Dictionary<Severity, bool> { { Severity.Broken, true }, { Severity.WorthChecking, true }, { Severity.TidyUp, false } };

        private static readonly Dictionary<Severity, (string Name, string About, Color Colour)> Groups = new Dictionary<Severity, (string, string, Color)>
        {
            { Severity.Broken, ("Broken", "Something on the avatar doesn't work.", new Color(.9f, .3f, .3f)) },
            { Severity.WorthChecking, ("Worth checking", "Probably a mistake, but it can be on purpose.", new Color(.95f, .7f, .2f)) },
            { Severity.TidyUp, ("Tidy-up", "Harmless leftovers you can clean up.", new Color(.5f, .7f, .95f)) },
        };

        // Remembered per computer: whether the uploaded version is checked too (on by default).
        private static bool IncludeBuild
        {
            get => EditorPrefs.GetBool("Arclight.Analyzer.IncludeBuild", true);
            set => EditorPrefs.SetBool("Arclight.Analyzer.IncludeBuild", value);
        }

        // Off by default; remembered per computer.
        internal static bool ReportOnUpload
        {
            get => EditorPrefs.GetBool("Arclight.Analyzer.ReportOnUpload", false);
            set => EditorPrefs.SetBool("Arclight.Analyzer.ReportOnUpload", value);
        }

        // Findings the user marked as fine, per avatar, remembered per computer.
        private static string IgnoreKey(GameObject avatar) => "Arclight.Analyzer.Ignored." + GlobalObjectId.GetGlobalObjectIdSlow(avatar);
        internal static HashSet<string> Ignored(GameObject avatar) =>
            new HashSet<string>(EditorPrefs.GetString(IgnoreKey(avatar), "").Split('\n').Where(k => k.Length > 0));
        // A card is ignored together with what it lists, so a later, different problem of the same kind shows again.
        private static string IgnoreId(Finding f) => f.Key + "#" + Fnv(f.Identity ?? f.Detail ?? "");
        private static string Fnv(string s) { uint h = 2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h.ToString("x8"); }
        private static bool IsIgnored(HashSet<string> set, Finding f) => set.Contains(IgnoreId(f)) || set.Contains(f.Key); // Bare keys: ignored before 1.2.2.
        private static bool ChangedSinceIgnored(HashSet<string> set, Finding f) => !IsIgnored(set, f) && set.Any(k => k.StartsWith(f.Key + "#", System.StringComparison.Ordinal));

        private static void SetIgnored(GameObject avatar, string key, bool ignored)
        {
            var keys = Ignored(avatar);
            keys.RemoveWhere(k => k == key.Split('#')[0] || k.StartsWith(key.Split('#')[0] + "#", System.StringComparison.Ordinal));
            if (ignored) keys.Add(key); else keys.Remove(key);
            EditorPrefs.SetString(IgnoreKey(avatar), string.Join("\n", keys));
        }

        [MenuItem("Tools/Arclight/Analyzer")]
        private static void Open() => GetWindow<AnalyzerWindow>("Arclight Analyzer").Show();

        private void OnEnable() => PickSelected();

        private void OnSelectionChange() { if (!avatar) { PickSelected(); Repaint(); } }

        // The selected object's avatar (the nearest object above it with an avatar descriptor), when none is picked yet.
        private void PickSelected()
        {
            if (avatar || !Selection.activeGameObject) return;
            for (var t = Selection.activeGameObject.transform; t; t = t.parent)
                if (AvatarAnalyzer.DescriptorOf(t.gameObject)) { avatar = t.gameObject; return; }
        }

        // Every avatar in the open scenes, active or not, including ones grouped under another object (but not an avatar inside an avatar).
        private static List<GameObject> SceneAvatars() =>
            Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded)
                .SelectMany(s => s.GetRootGameObjects()).SelectMany(g => g.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject)
                .Where(g => AvatarAnalyzer.DescriptorOf(g) && !(g.transform.parent && g.transform.parent.GetComponentsInParent<Transform>(true).Any(p => AvatarAnalyzer.DescriptorOf(p.gameObject))))
                .ToList();

        private List<Fixes.Entry> history;
        private List<Fixes.Entry> History => history ?? (history = Fixes.History()); // Read again only after a fix or restore.

        private void OnGUI()
        {
            EditorGUILayout.Space();
            avatar = (GameObject)EditorGUILayout.ObjectField("Avatar", avatar, typeof(GameObject), true);
            EditorGUI.BeginChangeCheck();
            bool build = EditorGUILayout.ToggleLeft(new GUIContent("Also check the uploaded version (slower)",
                "Runs the same build steps an upload does (Modular Avatar, VRCFury, optimizers...) on a hidden copy, so tools that add things at upload are taken into account. " +
                "The first run can take minutes; like an upload, it fills those tools' caches."), IncludeBuild);
            bool report = EditorGUILayout.ToggleLeft(new GUIContent("Also list problems in the console when I upload",
                "Before each upload, broken and worth-checking problems that upload tools can't fix are written to the console. The upload is never stopped."), ReportOnUpload);
            if (EditorGUI.EndChangeCheck()) { IncludeBuild = build; ReportOnUpload = report; }

            bool valid = avatar && AvatarAnalyzer.DescriptorOf(avatar);
            if (avatar && !valid) EditorGUILayout.HelpBox("Pick the avatar's root object (the one with the VRC Avatar Descriptor).", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!valid))
                    if (GUILayout.Button("Check", GUILayout.Height(32))) { all = null; Run(avatar); }
                if (GUILayout.Button("Check all avatars in the scene", GUILayout.Height(32), GUILayout.Width(200))) RunAll();
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorGUILayout.HelpBox("Leave Play Mode to check an avatar.", MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (all != null) DrawAll();
            if (findings != null) DrawFindings();
            DrawHistory();
            EditorGUILayout.EndScrollView();
        }

        private bool historyOpen;

        // Every fix pressed, newest first; controller fixes can be restored from the backup taken just before them.
        private void DrawHistory()
        {
            if (History.Count == 0) return;
            var entries = History;
            EditorGUILayout.Space();
            historyOpen = EditorGUILayout.Foldout(historyOpen, "Fix history (" + (entries.Count > 50 ? "latest 50 of " : "") + entries.Count + ")", true, EditorStyles.foldoutHeader);
            if (!historyOpen) return;
            foreach (var entry in Enumerable.Reverse(entries).Take(50))
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(entry.time + " · " + entry.avatar + (string.IsNullOrEmpty(entry.asset) ? "" : " · " + Fixes.Label(entry)) + "\n" + entry.action, EditorStyles.wordWrappedMiniLabel);
                    bool restorable = Fixes.CanRestore(entry);
                    using (new EditorGUI.DisabledScope(!restorable))
                        if (GUILayout.Button(new GUIContent("Restore", restorable ? "Puts " + Fixes.Label(entry) + " back as before this fix."
                            : "Not available: a scene fix (use Edit > Undo), or the backup or the file was moved or deleted."), GUILayout.Width(70)))
                        {
                            if (Fixes.Restore(entry)) fixedNote = "Restored " + Fixes.Label(entry) + ".";
                            history = null;
                            GUIUtility.ExitGUI();
                        }
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Backups in " + Fixes.Folder + ": " + (Fixes.BackupBytes() / 1024f / 1024f).ToString("0.0") + " MB", EditorStyles.miniLabel);
                if (GUILayout.Button("Delete backups older than 30 days", GUILayout.Width(220)))
                {
                    EditorUtility.DisplayDialog("Arclight Analyzer", "Deleted " + Fixes.DeleteBackups(30) + " backup folder(s).", "OK");
                    history = null;
                }
            }
        }

        private void DrawAll()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Avatars in the open scenes", EditorStyles.boldLabel);
            foreach (var (a, list, bits, error) in all)
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var ignored = a ? Ignored(a) : new HashSet<string>();
                    var shown = list.Where(f => !IsIgnored(ignored, f)).ToList();
                    EditorGUILayout.LabelField((a ? a.name : "(removed)") + (a && !a.activeInHierarchy ? " (inactive)" : ""), EditorStyles.boldLabel, GUILayout.Width(200));
                    EditorGUILayout.LabelField((shown.Count == 0 ? "No problems" :
                        string.Join(", ", Groups.Keys.Where(s => shown.Any(f => f.Severity == s)).Select(s => shown.Count(f => f.Severity == s) + " " + Groups[s].Name.ToLowerInvariant()))) +
                        " (" + bits + " bits)" + (error != null ? "; checked as it is only (the uploaded version failed to build)" : ""));
                    using (new EditorGUI.DisabledScope(!a))
                        if (GUILayout.Button("View", GUILayout.Width(50)))
                        {
                            avatar = a; checkedAvatar = a; findings = list; syncedBits = bits; buildError = error; checkedName = a.name; fixedNote = null;
                        }
                }
        }

        private void DrawFindings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(checkedName + " (" + (!checkedBuild ? "checked as it is" : buildError == null ? "checked as it is and as uploaded" : "checked as it is only") +
                "): synced parameters use " + syncedBits + " of " + AvatarAnalyzer.SyncLimit + " bits" + (syncedBits > AvatarAnalyzer.SyncLimit ? " (over the limit)" : "") + ".");
            if (buildError != null) EditorGUILayout.HelpBox("The uploaded version couldn't be built, so only the avatar as it is was checked: " + buildError, MessageType.Warning);
            if (fixedNote != null) EditorGUILayout.HelpBox(fixedNote + " Press Check again to see the result.", MessageType.Info);
            var ignored = checkedAvatar ? Ignored(checkedAvatar) : new HashSet<string>();
            var shown = findings.Where(f => showIgnored || !IsIgnored(ignored, f)).ToList();
            int hidden = findings.Count(f => IsIgnored(ignored, f));

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(shown.Count == 0 ? "No problems found." : string.Join(", ", Groups.Keys.Where(s => shown.Any(f => f.Severity == s))
                    .Select(s => shown.Count(f => f.Severity == s) + " " + Groups[s].Name.ToLowerInvariant())) +
                    (shown.Any(f => f.Where != null) ? " (" + shown.Count(f => f.Where != null) + " only in the uploaded version)" : ""), EditorStyles.boldLabel);
                if (hidden > 0) showIgnored = GUILayout.Toggle(showIgnored, "Show " + hidden + " ignored", GUILayout.Width(130));
                if (GUILayout.Button("Copy report", GUILayout.Width(100))) EditorGUIUtility.systemCopyBuffer = Report(shown);
            }

            foreach (var severity in Groups.Keys)
            {
                var group = shown.Where(f => f.Severity == severity).ToList();
                if (group.Count == 0) continue;
                var (name, about, colour) = Groups[severity];
                var previous = GUI.contentColor;
                GUI.contentColor = colour;
                GUILayout.Space(6);
                open[severity] = EditorGUILayout.Foldout(open[severity], new GUIContent(name + " (" + group.Count + ")", about), true, EditorStyles.foldoutHeader);
                GUILayout.Space(4);
                GUI.contentColor = previous;
                if (!open[severity]) continue;
                foreach (var finding in group) Draw(finding, IsIgnored(ignored, finding), ChangedSinceIgnored(ignored, finding));
                EditorGUILayout.Space();
            }
        }

        private readonly HashSet<string> fixOpen = new HashSet<string>();
        private static GUIStyle titleStyle;
        private static GUIStyle TitleStyle => titleStyle ?? (titleStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true });

        private static readonly Dictionary<Severity, string> Icons = new Dictionary<Severity, string>
        {
            { Severity.Broken, "console.erroricon.sml" }, { Severity.WorthChecking, "console.warnicon.sml" }, { Severity.TidyUp, "console.infoicon.sml" },
        };

        // Icon, title and small buttons on one row; the detail below; "How to fix" folded away until wanted.
        private static GUIStyle cardStyle, detailStyle, fixStyle;
        // Roomier cards: inner padding, a gap between cards, and a little extra line height for the text.
        private static GUIStyle CardStyle => cardStyle ?? (cardStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(4, 4, 0, 8) });
        private static GUIStyle DetailStyle => detailStyle ?? (detailStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 12, padding = new RectOffset(2, 2, 2, 2) });
        private static GUIStyle FixStyle => fixStyle ?? (fixStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 11, padding = new RectOffset(14, 2, 2, 2) });

        // Cards whose fix was pressed since the last check.
        private readonly HashSet<Finding> pressed = new HashSet<Finding>();

        // The fix button's label for each card, worked out once per check (it looks at asset paths).
        private readonly Dictionary<Finding, (string Label, System.Func<string> Apply)?> fixes = new Dictionary<Finding, (string, System.Func<string>)?>();

        private void Draw(Finding finding, bool isIgnored, bool changed)
        {
            using (new EditorGUILayout.VerticalScope(CardStyle))
            {
                if (!fixes.TryGetValue(finding, out var fix)) fixes[finding] = fix = Fixes.For(finding);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(EditorGUIUtility.IconContent(Icons[finding.Severity]), GUILayout.Width(18), GUILayout.Height(18));
                    GUILayout.Label((pressed.Contains(finding) ? "(fix applied, check again) " : isIgnored ? "(ignored) " : changed ? "(changed since you ignored it) " : "") + finding.Title, TitleStyle, GUILayout.ExpandWidth(true));
                    GUILayout.Space(6);
                    if (finding.Target && GUILayout.Button(new GUIContent(finding.Target is UnityEditor.Animations.AnimatorController ? "Open" : "Show", "Go to it."), EditorStyles.miniButtonLeft, GUILayout.Width(50))) Reveal(finding.Target);
                    bool fileFix = Fixes.Files(finding).Count > 0;
                    if (fix.HasValue && GUILayout.Button(new GUIContent(fix.Value.Label.Split(' ')[0], fix.Value.Label + " now. " + (fileFix
                        ? "Changes files (backed up first); Edit > Undo, or Restore in Fix history, reverts it." : "Changes the scene; Edit > Undo reverts it in this session.")),
                        EditorStyles.miniButtonMid, GUILayout.Width(60)))
                    {
                        // A file another avatar also uses changes for that avatar too, so ask first.
                        var shared = Fixes.SharedWith(finding, checkedAvatar);
                        if (shared.Count == 0 || EditorUtility.DisplayDialog("Also changes another avatar",
                            "This fix edits " + string.Join(", ", Fixes.Files(finding).Select(System.IO.Path.GetFileName).Take(5)) + ", which " + string.Join(", ", shared.Take(5)) +
                            (shared.Count == 1 ? " also uses" : " also use") + ". The change applies there too (it's backed up, and Fix history can restore it).", "Fix anyway", "Cancel"))
                        {
                            var current = Fixes.Recheck(finding, checkedAvatar);
                            fixedNote = current == null ? "That changed since you checked, so nothing was done."
                                : Fixes.Apply(current, checkedAvatar ? checkedAvatar.name : checkedName);
                            // The card stays, marked, until a check confirms it is gone: a fix can fail or leave part for you to do.
                            pressed.Add(finding);
                            history = null;
                        }
                        GUIUtility.ExitGUI();
                    }
                    if (checkedAvatar && GUILayout.Button(new GUIContent(isIgnored ? "Unignore" : "Ignore", "Hide this card for this avatar while what it lists stays the same."), EditorStyles.miniButtonRight, GUILayout.Width(60)))
                        SetIgnored(checkedAvatar, isIgnored ? finding.Key : IgnoreId(finding), !isIgnored);
                }
                GUILayout.Space(4);
                if (finding.Where != null) EditorGUILayout.LabelField(finding.Where, EditorStyles.miniBoldLabel);
                GUILayout.Label(finding.Detail, DetailStyle);
                GUILayout.Space(4);
                bool wasOpen = fixOpen.Contains(finding.Key);
                bool isOpen = EditorGUILayout.Foldout(wasOpen, "How to fix", true);
                if (isOpen != wasOpen) { if (isOpen) fixOpen.Add(finding.Key); else fixOpen.Remove(finding.Key); }
                if (isOpen) GUILayout.Label((fix.HasValue ? "Press " + fix.Value.Label.Split(' ')[0] + " to do this for you (" +
                    (Fixes.Files(finding).Count > 0 ? "files are backed up first" : "Edit > Undo reverts it") + "), or do it by hand: " : "") + finding.Fix, FixStyle);
            }
        }

        // A controller opens in the Animator window (its Inspector shows almost nothing); a scene object is selected and framed in
        // the Scene view; an asset (menu, Expression Parameters) is selected and highlighted in the Project window.
        private static void Reveal(Object target)
        {
            Selection.activeObject = target;
            if (target is UnityEditor.Animations.AnimatorController)
            {
                EditorApplication.ExecuteMenuItem("Window/Animation/Animator");
                return;
            }
            if (target is GameObject)
            {
                EditorGUIUtility.PingObject(target);
                SceneView.lastActiveSceneView?.FrameSelected();
                return;
            }
            EditorUtility.FocusProjectWindow();
            EditorGUIUtility.PingObject(target);
        }

        private List<Finding> Check(GameObject target, out string error, out int bits) =>
            // Grouped by kind within each severity, so related cards sit together.
            AvatarAnalyzer.Run(target, checkedBuild, out error, out bits).OrderBy(f => f.Key, System.StringComparer.Ordinal).ToList();

        private void Run(GameObject target)
        {
            try
            {
                checkedBuild = IncludeBuild;
                EditorUtility.DisplayProgressBar("Arclight Analyzer", checkedBuild
                    ? "Building a hidden copy the way an upload does, then checking it (the first run can take minutes)..." : "Checking the avatar...", .5f);
                findings = Check(target, out buildError, out syncedBits);
                fixes.Clear(); pressed.Clear();
                checkedAvatar = target;
                checkedName = target.name;
                fixedNote = null;
                scroll = Vector2.zero;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void RunAll()
        {
            var avatars = SceneAvatars();
            all = new List<(GameObject, List<Finding>, int, string)>();
            findings = null;
            fixes.Clear(); pressed.Clear();
            checkedBuild = IncludeBuild;
            try
            {
                for (int i = 0; i < avatars.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Arclight Analyzer", "Checking " + avatars[i].name + (checkedBuild ? " and a built copy" : "") + "...", (float)i / avatars.Count)) break;
                    var list = Check(avatars[i], out string error, out int bits);
                    all.Add((avatars[i], list, bits, error));
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (avatars.Count == 0) EditorUtility.DisplayDialog("Arclight Analyzer", "No avatars (objects with a VRC Avatar Descriptor) in the open scenes.", "OK");
        }

        private string Report(List<Finding> list)
        {
            var text = new StringBuilder("Arclight Analyzer: " + checkedName + (checkedBuild ? buildError == null ? " (avatar and uploaded version)" : " (avatar only; the uploaded version failed to build: " + buildError + ")" : " (avatar only)") +
                "\nSynced parameters: " + syncedBits + " of " + AvatarAnalyzer.SyncLimit + " bits\n");
            foreach (var severity in Groups.Keys)
                foreach (var finding in list.Where(f => f.Severity == severity))
                    text.Append("\n[" + Groups[severity].Name + "] " + finding.Title + (finding.Where != null ? " (" + finding.Where + ")" : "") +
                        "\n" + finding.Detail + "\nHow to fix: " + finding.Fix + "\n");
            return text.ToString();
        }
    }
}
