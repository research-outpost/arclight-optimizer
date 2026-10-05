using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Without Modular Avatar, d4rkAvatarOptimizer's upload hook and NDMF's Optimizing hook (where this tool
    // runs) share VRChat SDK callbackOrder -1025. The SDK runs its preprocess callbacks through a stable
    // OrderBy(callbackOrder), so the tie falls to type discovery order and d4rk can run first; it rebuilds
    // materials with its own generated shaders, which this tool then retains. With Modular Avatar, d4rk moves
    // to -15, after NDMF. Moving NDMF's hook ahead of d4rk's in the SDK's list gives the same order without
    // it. Reflection only, so neither package is required; if the SDK list changes shape, the scan warns
    // when d4rk has already run instead.
    internal static class D4rkOrdering
    {
        private const string D4rkHook = "d4rkpl4y3r.AvatarOptimizer.AvatarBuildHook";
        private const string NdmfOptimizeHook = "nadena.dev.ndmf.VRChat.BuildFrameworkOptimizeHook";

        [InitializeOnLoadMethod]
        private static void Schedule() => EditorApplication.delayCall += () => EnsureNdmfFirst();

        // The SDK's registered preprocess callbacks, or null when the SDK or its private list is not found.
        internal static IList PreprocessCallbacks() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks"))
            .FirstOrDefault(type => type != null)
            ?.GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as IList;

        // True when NDMF's Optimizing hook now runs before d4rk's; false when that cannot be arranged;
        // null when d4rk's hook is not registered.
        internal static bool? EnsureNdmfFirst()
        {
            var callbacks = PreprocessCallbacks();
            if (callbacks == null) return D4rkType() == null ? (bool?)null : false;
            var d4rk = callbacks.Cast<object>().FirstOrDefault(c => c?.GetType().FullName == D4rkHook);
            if (d4rk == null) return null;
            var ndmf = callbacks.Cast<object>().FirstOrDefault(c => c?.GetType().FullName == NdmfOptimizeHook);
            if (!(ndmf is UnityEditor.Build.IOrderedCallback ndmfOrder) || !(d4rk is UnityEditor.Build.IOrderedCallback d4rkOrder))
                return false;
            if (ndmfOrder.callbackOrder != d4rkOrder.callbackOrder) return ndmfOrder.callbackOrder < d4rkOrder.callbackOrder;
            int ndmfIndex = callbacks.IndexOf(ndmf), d4rkIndex = callbacks.IndexOf(d4rk);
            if (ndmfIndex > d4rkIndex)
            {
                callbacks.RemoveAt(ndmfIndex);
                callbacks.Insert(d4rkIndex, ndmf);
            }
            return true;
        }

        // d4rk writes the materials it rebuilds into <its package or Assets/d4rkAvatarOptimizer>/TrashBin/.
        // Any on the avatar at scan time means d4rk ran before this tool. (d4rk's own GetTrashBinLocation
        // can create a folder, so the path is matched instead.)
        internal static string RanBeforeWarning(IEnumerable<Renderer> renderers)
        {
            if (D4rkType() == null) return null;
            bool ran = renderers.SelectMany(renderer => renderer.sharedMaterials).Any(material => IsD4rkOutput(material));
            return ran
                ? "d4rkAvatarOptimizer ran before this tool, so materials it rebuilt keep their original textures. Installing Modular Avatar moves d4rk after NDMF."
                : null;
        }

        internal static bool IsD4rkOutput(UnityEngine.Object asset)
        {
            string path = asset ? AssetDatabase.GetAssetPath(asset) : "";
            return path.IndexOf("/TrashBin/", StringComparison.Ordinal) >= 0 &&
                   path.IndexOf("d4rkavataroptimizer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Whether d4rkAvatarOptimizer may optimize this avatar on upload: it has a d4rk component (enabled or not), or d4rk is
        // installed with its global "Always Optimize on Upload" setting, under which d4rk adds its own component at callback -15.
        internal static bool MayRun(GameObject root) =>
            root.GetComponentsInChildren<Component>(true).Any(c => c && c.GetType().Name == "d4rkAvatarOptimizer") ||
            D4rkType() != null && EditorPrefs.GetBool("d4rkpl4y3r_AvatarOptimizer_DoOptimizeWithDefaultSettingsWhenNoComponent", false);

        private static readonly Lazy<Type> d4rkType = new Lazy<Type>(() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("d4rkAvatarOptimizer")).FirstOrDefault(type => type != null));
        private static Type D4rkType() => d4rkType.Value;
    }
}
