using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor
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

        internal static bool IsD4rkOutput(UnityEngine.Object asset) => IsD4rkOutputPath(asset ? AssetDatabase.GetAssetPath(asset) : "");

        internal static bool IsD4rkOutputPath(string path) =>
            TrashBin != null && !string.IsNullOrEmpty(path) && path.StartsWith(TrashBin, StringComparison.Ordinal);

        // d4rk's TrashBin folder, worked out as d4rkAvatarOptimizer.GetTrashBinLocation does (an embedded package's folder, the
        // package folder under Assets, else Assets/d4rkAvatarOptimizer) but without calling it, as it creates that last folder.
        // Null without d4rk.
        internal static string TrashBin => trashBin.Value;
        private static readonly Lazy<string> trashBin = new Lazy<string>(() =>
        {
            var assembly = D4rkType()?.Assembly;
            if (assembly == null) return null;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
            if (package?.source == UnityEditor.PackageManager.PackageSource.Embedded && !string.IsNullOrEmpty(package.assetPath)) return package.assetPath + "/TrashBin/";
            const string inPackage = "/Editor/d4rkpl4y3r.d4rkavataroptimizer.Editor.asmdef";
            string asmdef = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.GetName().FullName) ?? "";
            if (asmdef.StartsWith("Assets", StringComparison.Ordinal) && asmdef.EndsWith(inPackage, StringComparison.Ordinal))
                return asmdef.Substring(0, asmdef.Length - inPackage.Length) + "/TrashBin/";
            return "Assets/d4rkAvatarOptimizer/TrashBin/";
        });

        // Whether the d4rkAvatarOptimizer package is installed at all; whether it acts on this avatar is MayRun's answer.
        internal static bool Installed => D4rkType() != null;

        // d4rk Fighting Actions declares/injects its parameters at the SDK stage, after NDMF, through a component in
        // the global namespace. Matched by type name so a namespace rename alone cannot hide it (fail closed).
        internal static bool ReadsParameters(Type type)
        {
            string full = type.FullName ?? string.Empty;
            return full.IndexOf("d4rk", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   type.Name.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Whether d4rkAvatarOptimizer will optimize this avatar on upload. Mirrors d4rk's own AvatarBuildHook (4.7.x)
        // rather than "a component exists": an ACTIVE component with Apply On Upload set runs; with no active component
        // the global "Always Optimize on Upload" setting makes d4rk add one at callback -15; in Play Mode its "Optimize
        // in Play Mode" setting decides. A disabled component, or one with Apply On Upload unset, does nothing on
        // upload, so standing a structure-sensitive pass down for it would only lose that pass's savings.
        // Anything unreadable answers "will run": that direction costs bytes, the other changes what d4rk merges.
        internal static bool MayRun(GameObject root)
        {
            var optimizers = root.GetComponentsInChildren<Component>(true)
                .Where(c => c && c.GetType().Name == "d4rkAvatarOptimizer" && (!(c is Behaviour behaviour) || behaviour.isActiveAndEnabled))
                .ToList();
            // Two active components abort d4rk's build; answer as if it runs so nothing is skipped in vain.
            if (optimizers.Count > 1) return true;
            if (Application.isPlaying && !OptimizesInPlayMode()) return false;
            return optimizers.Count == 1 ? ApplyOnUpload(optimizers[0]) : D4rkType() != null && GlobalOptimizeSetting();
        }

        // d4rk's own switch, read by reflection (a public property forwarding to its settings). A renamed member or a
        // throw answers "set", so d4rk is assumed to run.
        private static bool ApplyOnUpload(Component optimizer)
        {
            try
            {
                var type = optimizer.GetType();
                if (type.GetProperty("ApplyOnUpload")?.GetValue(optimizer) is bool fromProperty) return fromProperty;
                if (type.GetField("ApplyOnUpload")?.GetValue(optimizer) is bool fromField) return fromField;
            }
            catch (Exception) { }
            return true;
        }

        // d4rk's Play Mode switch (static, on its settings type). Unreadable answers "on", so d4rk is assumed to run.
        private static bool OptimizesInPlayMode()
        {
            try
            {
                var settings = D4rkType()?.Assembly?.GetType("d4rkpl4y3r.AvatarOptimizer.AvatarOptimizerSettings");
                if (settings?.GetProperty("DoOptimizeInPlayMode")?.GetValue(null) is bool value) return value;
            }
            catch (Exception) { }
            return true;
        }

        // Read d4rk's own public setting property by reflection instead of its EditorPrefs key by name: if d4rk
        // renames the key, GetBool would read false and Arclight's mesh cleanup would run beside d4rk's.
        // The literal stays as fallback for builds where the property moved.
        private static bool GlobalOptimizeSetting()
        {
            try
            {
                var settings = D4rkType()?.Assembly?.GetType("d4rkpl4y3r.AvatarOptimizer.AvatarOptimizerSettings");
                if (settings?.GetProperty("DoOptimizeWithDefaultSettingsWhenNoComponent")?.GetValue(null) is bool value) return value;
            }
            catch (Exception) { }
            return EditorPrefs.GetBool("d4rkpl4y3r_AvatarOptimizer_DoOptimizeWithDefaultSettingsWhenNoComponent", false);
        }

        private static readonly Lazy<Type> d4rkType = new Lazy<Type>(() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("d4rkAvatarOptimizer")).FirstOrDefault(type => type != null));
        private static Type D4rkType() => d4rkType.Value;
    }
}
