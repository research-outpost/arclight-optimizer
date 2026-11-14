using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Takes the pre-build asset snapshot before any other preprocess callback runs: VRCFury (-10000), Modular
    // Avatar's and NDMF's hooks (-1025) and d4rk (-15 or -1025) all start after this, so every asset they create is
    // recognised as build-owned without naming their folders. Uploads and Build & Test both go through here.
    internal sealed class OwnershipSnapshotHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatar)
        {
            var config = avatar ? avatar.GetComponent<AvatarTextureOptimizer>() : null;
            if (config && config.enabled) BuildOwnership.Begin(avatar);
            return true;
        }
    }
}
