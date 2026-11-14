using System;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // What the optional third-party optimizers mean for this avatar, answered once per call. This is the only type a
    // pass may ask: no pass names a rival, so the core optimizer builds and runs with neither package installed.
    // Detection is by type name and reflection (see D4rkOrdering/VrcfuryInterop), never a compile-time reference.
    internal sealed class RivalState
    {
        internal bool D4rkInstalled;
        internal bool D4rkWillRun;
        internal bool VrcfuryPresent;
        internal bool VrcfuryBuilt;

        // A rival rewrites meshes, bones or clips around Arclight: d4rk before its merge reads what a pass removed
        // (a pruned bone list changes its rounding), or it runs after Arclight and invalidates the proof. A pass whose
        // output feeds that rewrite stays out for this avatar. VRCFury that already built has finished writing; one
        // that has not is handled by AnimationIncomplete.
        internal bool StructureChanges => D4rkWillRun;

        // VRCFury will build this avatar but has not yet: its objects, readers and clips are not on the avatar, so
        // anything that proves from the animation is unsound until the later stage. When VRCFury is switched off for
        // this run it will never build, so the authored animation is final and the full pipeline may run.
        internal bool AnimationIncomplete => VrcfuryPresent && !VrcfuryBuilt && VrcfuryInterop.WillBuildThisRun();

        // True when a component could read animator parameters through data this scan cannot see. Udon is handled
        // beside this by the caller; this covers the rival-specific reader (d4rk Fighting Actions).
        internal static bool ReadsParametersOutsideTheScan(Type type) => D4rkOrdering.ReadsParameters(type);

        // d4rk ran before Arclight despite the ordering fix; the report should say so rather than silently keep
        // unoptimized materials (null when d4rk is absent or has not run).
        internal static string OrderingWarning(Renderer[] renderers) => D4rkOrdering.RanBeforeWarning(renderers);

        // Re-assert that the optimizer which runs before Arclight's late hook still does (see D4rkOrdering). Null when
        // there is nothing to order; never an error.
        internal static bool? EnsureOrderingBeforeLate() => D4rkOrdering.EnsureNdmfFirst();

        internal static RivalState Of(GameObject root) => Detect(root);

        internal static RivalState Detect(GameObject root)
        {
            if (!root) return new RivalState();
            return new RivalState
            {
                D4rkInstalled = D4rkOrdering.Installed,
                D4rkWillRun = D4rkOrdering.MayRun(root),
                VrcfuryPresent = VrcfuryInterop.Present(root),
                VrcfuryBuilt = VrcfuryInterop.Built(root),
            };
        }
    }
}
