using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // A build where Arclight did less than it could (it stood down, or a pass stopped part-way) is shown as a non-fatal
    // warning in NDMF's error window, not only as a Console line, so it is noticed. Outside a build NDMF just logs it.
    internal static class BuildWarnings
    {
        private const string Key = "arclight.warning";

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Key] = "Arclight Optimizer: {0}",
            [Key + ":description"] = "{1}",
            [Key + ":hint"] = "{2}",
        };

        private static readonly Localizer Localizer = new Localizer("en-US", () =>
            new List<(string, Func<string, string>)> { ("en-US", key => English.TryGetValue(key, out var value) ? value : null) });

        internal static void Report(GameObject root, string title, string description, string hint)
        {
            ErrorReport.ReportError(Localizer, ErrorSeverity.NonFatal, Key, title, description, hint);
            Debug.LogWarning("Arclight Optimizer: " + title + ". " + description + " (" + (root ? root.name : "") + ")");
        }
    }
}
