using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Arclight replaces Avatar Optimizer (AAO) and does not run alongside it. NDMF has no way to declare one plugin
    // incompatible with another, so an avatar carrying any AAO component gets a blocking build error instead, and no
    // Arclight pass runs. AAO only processes avatars with its components, so having the package installed is fine.
    internal static class AvatarOptimizerConflict
    {
        private const string Key = "arclight.aao-conflict";

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Key] = "Arclight Optimizer cannot run with Avatar Optimizer",
            [Key + ":description"] = "This avatar has Avatar Optimizer's {0} component on {1}. Arclight Optimizer replaces Avatar Optimizer and does not work alongside it, so none of its optimizations ran.",
            [Key + ":hint"] = "Remove Avatar Optimizer's components from this avatar, or disable or remove the Arclight Optimizer component. Arclight covers most of Avatar Optimizer's automatic optimizations itself; it leaves out merging PhysBones on purpose. To remove parts of a mesh, as Avatar Optimizer's Remove Mesh components did, use Modular Avatar's Mesh Cutter.",
        };

        private static readonly Localizer Localizer = new Localizer("en-US", () =>
            new List<(string, Func<string, string>)> { ("en-US", key => English.TryGetValue(key, out var value) ? value : null) });

        // The first Avatar Optimizer component on the avatar, or null.
        internal static Component Find(GameObject root) => root.GetComponentsInChildren<Component>(true)
            .FirstOrDefault(c => c && (c.GetType().Namespace ?? "").StartsWith("Anatawa12.AvatarOptimizer", StringComparison.Ordinal));

        // Reports the blocking error when the avatar carries Avatar Optimizer; true when it did.
        internal static bool Report(GameObject root)
        {
            var component = Find(root);
            if (!component) return false;
            ErrorReport.ReportError(Localizer, ErrorSeverity.Error, Key, component.GetType().Name, component.gameObject.name, component);
            Debug.LogError("Arclight Optimizer: " + string.Format(English[Key + ":description"], component.GetType().Name, component.gameObject.name) + " (" + root.name + ")");
            return true;
        }
    }
}
