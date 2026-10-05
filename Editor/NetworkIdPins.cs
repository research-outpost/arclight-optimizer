using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // VRChat syncs PhysBone grabbing and posing by network ID. The SDK numbers every network object (PhysBones on an
    // avatar) that has no ID in order of their hierarchy paths, in its AssignAvatarNetworkIDs build callback (order -1024), which runs
    // after NDMF's Optimizing phase (-1025). Removing a PhysBone there would shift the ID of every PhysBone after it, so an
    // Arclight build would disagree with a build without Arclight, and a PC build could disagree with a Quest build whose
    // passes removed different PhysBones. Before Arclight removes anything, the SDK's own routine runs once on the avatar
    // as it stands, which gives every network object the ID it would get without Arclight; IDs already set are kept.
    // This writes to the avatar descriptor, so it also covers objects under an Arclight Exclude component.
    internal static class NetworkIdPins
    {
        private static readonly Type Assign = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("AssignAvatarNetworkIDs", false)).FirstOrDefault(t => t != null);

        // False when the SDK routine is missing or reported a problem (the SDK reports it again at its own step).
        internal static bool Pin(GameObject avatar)
        {
            if (Assign == null) return false;
            var instance = Activator.CreateInstance(Assign);
            return Assign.GetMethod("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.Instance)?.Invoke(instance, new object[] { avatar }) is bool ok && ok;
        }
    }
}
