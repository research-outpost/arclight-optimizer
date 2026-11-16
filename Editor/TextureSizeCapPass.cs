using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // The opt-in texture size cap, applied last: after generation (which clears and pads at the authored resolution, where
    // the pixel geometry lives) and after cropping (which cuts full-resolution pixels and often brings a texture under the
    // cap by itself, so nothing is downscaled needlessly). Every texture the build's materials use is considered, including
    // the ones no other pass touched — which is why this is its own step and not part of the cropper.
    // A capped copy is a byte copy of the texture with its importer capped, so the source file and its importer are never
    // changed; the build's materials get a clone naming the copy, and the duplicate is registered like every other
    // replacement. Left alone: textures within the cap, textures an animation names directly, and any texture this pass
    // cannot also repoint (held by a particle, trail or line renderer, by an excluded renderer, or by a material an
    // animation swaps in) — capping those on the mesh side alone would ship the source and the copy together. Also left
    // alone: everything while VRCFury has not built the avatar yet.
    internal static class TextureSizeCapPass
    {
        internal sealed class Result { public int Textures, Materials, MenuIcons; public long Bytes; public int Unmeasured; }

        // previous: the texture pass's source -> final map, so a menu control that still names a source which the build
        // already replaced (generated, cropped) follows through to its capped copy.
        internal static Result Run(GameObject root, AvatarAnalysis analysis, int cap, Action<Object, Object> register,
            Dictionary<Texture, Texture> previous = null)
        {
            var result = new Result();
            if (cap <= 0 || !analysis.Complete) return result;
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => r).ToList();
            var audited = renderers.Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && !Exclusions.Excluded(r)).ToList();
            var exempt = new HashSet<Texture>(analysis.AnimatedObjectValues.OfType<Texture>());
            foreach (var renderer in renderers.Where(r => !audited.Contains(r))) AddTextures(renderer.sharedMaterials, exempt);
            foreach (var renderer in audited) AddTextures(analysis.SwappedMaterials(renderer), exempt);

            var capped = new Dictionary<Texture2D, Texture2D>();
            foreach (var texture in audited.SelectMany(r => r.sharedMaterials).Where(m => m)
                         .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).OfType<Texture2D>().Distinct())
            {
                if (!texture || exempt.Contains(texture) || Math.Max(texture.width, texture.height) <= cap) continue;
                var copy = CappedCopy.Make(texture, cap, dropMips: false, narrowOpaque: false, CappedCopy.Folder, CappedCopy.Marker,
                    "cap", allowCacheSource: true, out long before, out long after);
                if (!copy) continue;
                capped[texture] = copy;
                result.Textures++;
                if (before > 0 && after > 0) result.Bytes += before - after; else result.Unmeasured++;
            }
            if (capped.Count == 0) return result;

            // A menu control can name a texture the materials use (MenuIconMerger points an icon at one when they are
            // identical), and it may still name that texture's *source* after the build replaced it. Both follow the cap,
            // or the source and the capped copy would both ship.
            var menuMap = new Dictionary<Texture2D, Texture2D>(capped);
            if (previous != null)
                foreach (var pair in previous)
                    if (pair.Key is Texture2D source && pair.Value is Texture2D final && capped.TryGetValue(final, out var copy))
                        menuMap[source] = copy;
            result.MenuIcons = MenuIconMerger.Repoint(root, menuMap);

            // One clone per material, naming every capped copy it uses. Renderer slots point at the clones; a material an
            // animation swaps in keeps its own, so that state shows the authored size.
            var clones = new Dictionary<Material, Material>();
            foreach (var material in audited.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToList())
            {
                var properties = material.GetTexturePropertyNames()
                    .Where(p => material.GetTexture(p) is Texture2D texture && capped.ContainsKey(texture)).ToArray();
                if (properties.Length == 0) continue;
                var copy = new Material(material) { name = material.name };
                foreach (string property in properties) copy.SetTexture(property, capped[(Texture2D)material.GetTexture(property)]);
                clones[material] = copy;
                register(material, copy);
                result.Materials++;
            }
            // Slots are written with every Animator unbound: a bound one can write the materials it captured before this
            // pass into an animated slot (see ControllerCommit.Preserving).
            var animators = root.GetComponentsInChildren<Animator>(true).Where(a => a.runtimeAnimatorController)
                .ToDictionary(a => a, a => a.runtimeAnimatorController);
            foreach (var animator in animators.Keys) animator.runtimeAnimatorController = null;
            try
            {
                foreach (var renderer in audited)
                {
                    var slots = renderer.sharedMaterials;
                    var changed = false;
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] && clones.TryGetValue(slots[i], out var copy)) { slots[i] = copy; changed = true; }
                    if (changed) renderer.sharedMaterials = slots;
                }
            }
            finally { foreach (var pair in animators) pair.Key.runtimeAnimatorController = pair.Value; }
            // One save for the whole pass: an unflushed mark dies at the next domain reload and the cleanup can then trash a
            // copy this build is using.
            CacheUsage.FlushUsage();
            return result;
        }

        private static void AddTextures(IEnumerable<Material> materials, HashSet<Texture> into)
        {
            foreach (var material in materials.Where(m => m))
                foreach (string property in material.GetTexturePropertyNames())
                    if (material.GetTexture(property) is Texture texture && texture) into.Add(texture);
        }
    }
}
