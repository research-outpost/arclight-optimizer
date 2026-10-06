using System.Linq;
using Okarin.AvatarTextureOptimizer.Editor.Analyzer;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Optional (Arclight Analyzer window): on upload, lists the avatar's problems in the console before any build step runs. It
    // never stops the upload or changes the avatar. Play Mode and the Analyzer's own build check are left alone.
    internal sealed class AnalyzerUploadReport : IVRCSDKPreprocessAvatarCallback
    {
        // Before NDMF, Modular Avatar and VRCFury, so the avatar is checked as authored.
        public int callbackOrder => -100000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            if (!AnalyzerWindow.ReportOnUpload || AvatarAnalyzer.Building || EditorApplication.isPlayingOrWillChangePlaymode) return true;
            try
            {
                // Problems that upload tools may fix (missing parameters, menus, the budget, unused entries, animated or driven names
                // they add) need the window's check of the uploaded version, so they are left out here.
                var findings = AvatarAnalyzer.Group(AvatarAnalyzer.Check(avatarGameObject))
                    .Where(f => f.Severity != Severity.TidyUp && !AvatarAnalyzer.Breakage(f) && !f.Key.StartsWith("unused|", System.StringComparison.Ordinal)
                        && !f.Key.StartsWith("path|", System.StringComparison.Ordinal) && f.Key != "driver").ToList();
                if (findings.Count == 0) return true;
                Debug.LogWarning("Arclight Analyzer: " + findings.Count + " problem(s) on " + avatarGameObject.name + " (Tools > Arclight > Analyzer for details).");
                foreach (var finding in findings)
                    Debug.LogWarning("Arclight Analyzer [" + (finding.Severity == Severity.Broken ? "Broken" : "Worth checking") + "] " + finding.Title + ". " + finding.Detail + " How to fix: " + finding.Fix);
            }
            catch (System.Exception e) { Debug.LogWarning("Arclight Analyzer: the upload check failed and was skipped: " + e.Message); }
            return true;
        }
    }
}
