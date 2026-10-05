using nadena.dev.ndmf;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer
{
    // Tells Arclight to leave this object and everything under it alone: its renderers, meshes, materials, textures,
    // bones, PhysBones, contacts, particles and audio are neither changed nor removed. An escape hatch for when an
    // automatic decision is wrong for an object.
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // Superseded by the Exclude list on Arclight Optimizer; still honoured on existing avatars.
    public sealed class ArclightExclude : MonoBehaviour, INDMFEditorOnly
    {
    }
}
