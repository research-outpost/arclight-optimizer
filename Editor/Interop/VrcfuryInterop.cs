using System;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // VRCFury's presence and build state, read without referencing its package. VRCFury's builder runs on an avatar
    // exactly when the avatar carries one of its components (its ShouldRun gate), so no component means VRCFury
    // deliberately passed this avatar over. The built marker (VRCFuryTest) is added at the end of its bake.
    internal static class VrcfuryInterop
    {
        private static readonly Lazy<bool> installed = new Lazy<bool>(() => UnityEditor.PackageManager.PackageInfo
            .GetAllRegisteredPackages().Any(p => p.name == "com.vrcfury.vrcfury"));

        // The base type its builder tests with, found once per session. A package add/remove reloads the domain, so
        // the Lazy cannot serve a stale answer. Null when the package is absent or its assembly has not loaded.
        private static readonly Lazy<Type> furyBase = new Lazy<Type>(() =>
        {
            if (!installed.Value) return null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                var found = types.FirstOrDefault(t => t.Name == "VRCFuryComponent" && typeof(Component).IsAssignableFrom(t));
                if (found != null) return found;
            }
            return null;
        });

        internal static bool PackageInstalled => installed.Value;

        // Whether VRCFury's own pre-upload hook will build this avatar in the current run. Its hook (-10000) returns
        // early when Play Mode or uploading has it switched off; in that case the authored animation is final and
        // Arclight may run its full pipeline instead of standing down. Unknown answers fail closed (assume it builds).
        internal static bool WillBuildThisRun()
        {
            if (!PackageInstalled) return false;
            try
            {
                bool playing = Application.isPlaying;
                bool uploading = !playing && (Setting("VF.Hooks.IsActuallyUploadingHook") ?? false);
                bool playModeOn = Setting("VF.Menu.PlayModeMenuItem") ?? true;
                bool useInUpload = Setting("VF.Menu.UseInUploadMenuItem") ?? true;
                return ShouldBuild(playing, uploading, playModeOn, useInUpload);
            }
            catch (Exception) { return true; }
        }

        // VRCFury's own decision, mirrored: Play Mode uses its Play Mode switch; upload uses its Use In Upload switch;
        // a Build & Test run (not uploading) always builds.
        internal static bool ShouldBuild(bool playing, bool uploading, bool playModeOn, bool useInUploadOn) =>
            playing ? playModeOn : !uploading || useInUploadOn;

        private static bool? Setting(string typeName)
        {
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, false);
                if (type != null) break;
            }
            var get = type?.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (get == null || get.ReturnType != typeof(bool)) return null;
            return (bool)get.Invoke(null, null);
        }

        internal static bool Built(GameObject avatar) => avatar &&
            avatar.GetComponents<Component>().Any(c => c && c.GetType().Name == "VRCFuryTest");

        // The components are found by the base class VRCFury's builder tests with (a VF. namespace rename alone then
        // cannot fool this); the namespace sniff stays as a second catcher. When the package is installed but its base
        // type cannot be found at all, a rename this deep would also move its built marker, so fail closed.
        internal static bool Present(GameObject avatar)
        {
            if (!avatar) return false;
            bool package = PackageInstalled;
            Type baseType = furyBase.Value;
            if (package && baseType == null) return true; // cannot see VRCFury's own gate: fail closed
            return avatar.GetComponentsInChildren<Component>(true).Any(c => c &&
                ((baseType != null && baseType.IsInstanceOfType(c)) ||
                 (c.GetType().Namespace ?? "").StartsWith("VF.", StringComparison.Ordinal)) &&
                c.GetType().Name != "VRCFuryDebugInfo");
        }
    }
}
