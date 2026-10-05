using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Lowers a particle system's Max Particles to the most particles it can ever have alive, so the setting never
    // limits it and nothing on screen changes. Particles alive at one moment were emitted within the last lifetime,
    // so the count is at most (highest rate over time) x (longest start lifetime + one frame) plus every burst that
    // can fire within one lifetime, rounded up per source; a non-looping system also cannot exceed everything it
    // ever emits. Curve maxima are exact (cubic segments solved), including tangent overshoot.
    // Left alone: emission over distance (depends on movement), ring-buffer mode (particles outlive their lifetime),
    // lifetime by emitter speed, sub-emitter targets (other systems emit into them), weighted curve keys, and any
    // system an animation changes.
    internal static class ParticleUpperBound
    {
        // The longest frame the simulation takes in one step is Time.maximumDeltaTime (1/3 s by default).
        internal const float FrameMargin = .5f;

        internal sealed class Result { public int Systems, Removed, Trails, Collisions; public long Before, After; }

        // Also removes systems that can never emit (they still cost a component update every frame), unless
        // another component references them or they have particle systems below them that their playback starts;
        // and turns off modules that provably do nothing: per-particle trails whose lifetime is always 0, and
        // collision that can hit nothing (World mode colliding with no layers, Planes mode with no planes).
        internal static Result Run(AvatarAnalysis analysis)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            var subEmitterTargets = new HashSet<ParticleSystem>();
            foreach (var system in systems)
            {
                var sub = system.subEmitters;
                for (int i = 0; i < sub.subEmittersCount; i++)
                    if (sub.GetSubEmitterSystem(i)) subEmitterTargets.Add(sub.GetSubEmitterSystem(i));
            }
            foreach (var system in systems)
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (subEmitterTargets.Contains(system) || analysis.IsAnimated(system) || analysis.IsAnimated(renderer) || Exclusions.Excluded(system)) continue;
                long bound = Bound(system);
                var main = system.main;
                // A Stop Action (Disable, Destroy, Callback) still acts when the system ends, even with nothing emitted.
                if (bound == 0 && main.stopAction == ParticleSystemStopAction.None && !analysis.ReferencesTo(system).Any(c => c != renderer) && !analysis.ReferencesTo(renderer).Any(c => c != system) &&
                    system.GetComponentsInChildren<ParticleSystem>(true).Length == 1)
                {
                    result.Removed++;
                    result.Before += main.maxParticles;
                    if (renderer) UnityEngine.Object.DestroyImmediate(renderer);
                    UnityEngine.Object.DestroyImmediate(system);
                    continue;
                }
                if (TrailsInert(system)) { var trails = system.trails; trails.enabled = false; result.Trails++; }
                if (CollisionInert(system)) { var collision = system.collision; collision.enabled = false; result.Collisions++; }
                if (bound < 0 || bound >= main.maxParticles) continue;
                result.Systems++;
                result.Before += main.maxParticles;
                result.After += bound;
                main.maxParticles = (int)bound;
            }
            return result;
        }

        internal static bool TrailsInert(ParticleSystem system)
        {
            var trails = system.trails;
            return trails.enabled && trails.mode == ParticleSystemTrailMode.PerParticle && Max(trails.lifetime, out float max) && max <= 0;
        }

        internal static bool CollisionInert(ParticleSystem system)
        {
            var collision = system.collision;
            if (!collision.enabled) return false;
            if (collision.type == ParticleSystemCollisionType.World) return collision.collidesWith.value == 0;
            for (int i = 0; i < collision.planeCount; i++) if (collision.GetPlane(i)) return false;
            return true;
        }

        // The most particles the system can have alive, or -1 when it cannot be bounded.
        internal static long Bound(ParticleSystem system)
        {
            var main = system.main;
            var emission = system.emission;
            if (main.ringBufferMode != ParticleSystemRingBufferMode.Disabled || system.lifetimeByEmitterSpeed.enabled) return -1;
            if (!emission.enabled) return 0;
            if (!Max(emission.rateOverDistance, out float distanceRate) || distanceRate > 0) return -1;
            if (!Max(emission.rateOverTime, out float rate) || !Max(main.startLifetime, out float lifetime)) return -1;
            rate = Math.Max(0, rate);
            lifetime = Math.Max(0, lifetime);
            float duration = Math.Max(main.duration, 1e-4f);
            // One step advances the simulation by up to FrameMargin times its speed.
            float window = lifetime + FrameMargin * Math.Max(1f, main.simulationSpeed);

            long fromRate = rate > 0 ? (long)Math.Ceiling((double)rate * window) + 1 : 0;
            long fromBursts = 0, everBursts = 0;
            for (int i = 0; i < emission.burstCount; i++)
            {
                var burst = emission.GetBurst(i);
                if (!Max(burst.count, out float count)) return -1;
                long size = (long)Math.Ceiling(Math.Max(0, count));
                if (size == 0 || burst.time > duration) continue;
                double interval = Math.Max(burst.repeatInterval, 1e-4f);
                long perLoop = (long)Math.Floor((duration - burst.time) / interval) + 1;
                if (burst.cycleCount > 0) perLoop = Math.Min(perLoop, burst.cycleCount);
                long perWindow = Math.Min(perLoop, (long)Math.Floor(window / interval) + 1);
                long loops = main.loop ? (long)Math.Ceiling(window / duration) + 1 : 1;
                fromBursts += size * Math.Min(perLoop * loops, perWindow * loops);
                everBursts += size * perLoop;
            }
            long bound = fromRate + fromBursts;
            if (!main.loop) bound = Math.Min(bound, (rate > 0 ? (long)Math.Ceiling((double)rate * duration) + 1 : 0) + everBursts);
            return bound;
        }

        // Highest value a MinMaxCurve can take over normalized time.
        internal static bool Max(ParticleSystem.MinMaxCurve curve, out float max)
        {
            max = 0;
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant: max = curve.constant; return true;
                case ParticleSystemCurveMode.TwoConstants: max = Math.Max(curve.constantMin, curve.constantMax); return true;
                case ParticleSystemCurveMode.Curve:
                    return Scaled(curve.curve, curve.curveMultiplier, out max);
                case ParticleSystemCurveMode.TwoCurves:
                    if (!Scaled(curve.curveMin, curve.curveMultiplier, out float a) || !Scaled(curve.curveMax, curve.curveMultiplier, out float b)) return false;
                    max = Math.Max(a, b);
                    return true;
            }
            return false;
        }

        private static bool Scaled(AnimationCurve curve, float multiplier, out float max)
        {
            max = 0;
            if (curve == null || !Range(curve, out float low, out float high)) return false;
            max = Math.Max(low * multiplier, high * multiplier);
            return true;
        }

        // Lowest and highest value of the curve over its keys, wrapped or clamped (wrapping repeats the same values).
        internal static bool Range(AnimationCurve curve, out float low, out float high)
        {
            var keys = curve.keys;
            low = high = 0;
            if (keys.Length == 0) return true;
            low = high = keys[0].value;
            foreach (var key in keys)
            {
                if (key.weightedMode != WeightedMode.None) return false;
                low = Math.Min(low, key.value); high = Math.Max(high, key.value);
            }
            for (int i = 0; i + 1 < keys.Length; i++)
            {
                Keyframe a = keys[i], b = keys[i + 1];
                double dt = b.time - a.time;
                if (dt <= 0 || float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) continue; // A step holds the key values.
                // Cubic Hermite p(s) = c3 s^3 + c2 s^2 + c1 s + c0 over s in [0, 1].
                double m0 = a.outTangent * dt, m1 = b.inTangent * dt;
                double c3 = 2 * a.value + m0 - 2 * b.value + m1, c2 = -3 * a.value - 2 * m0 + 3 * b.value - m1, c1 = m0, c0 = a.value;
                foreach (double s in Roots(3 * c3, 2 * c2, c1))
                    if (s > 0 && s < 1)
                    {
                        float v = (float)(((c3 * s + c2) * s + c1) * s + c0);
                        low = Math.Min(low, v); high = Math.Max(high, v);
                    }
            }
            // Float evaluation in Unity can differ from the double solution in the last bits.
            float margin = 1e-4f * Math.Max(1, Math.Max(Math.Abs(low), Math.Abs(high)));
            low -= margin; high += margin;
            return true;
        }

        private static IEnumerable<double> Roots(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) > 1e-12) yield return -c / b;
                yield break;
            }
            double d = b * b - 4 * a * c;
            if (d < 0) yield break;
            double r = Math.Sqrt(d);
            yield return (-b + r) / (2 * a);
            yield return (-b - r) / (2 * a);
        }
    }
}
