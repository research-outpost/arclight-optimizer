using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // The native dialog pumps cancellation during synchronous editor work. Unity codec/import
    // calls finish before the next checkpoint; applying replacements is one non-cancellable step.
    internal sealed class OptimizationProgress : IDisposable
    {
        private readonly Func<string, float, bool, bool> display;
        private readonly Action clear;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Texture texture;
        private string stage;
        private int index, total;
        private float fraction;
        private long nextUpdate;
        private bool cancellable = true, shown, cancelled, disposed;

        internal OptimizationProgress(Func<string, float, bool, bool> display = null, Action clear = null)
        {
            this.display = display ?? (Application.isBatchMode ? null : (Func<string, float, bool, bool>)Display);
            this.clear = clear ?? EditorUtility.ClearProgressBar;
        }

        internal void Scanning(Texture texture, string stage)
        {
            this.texture = texture;
            total = 0;
            fraction = 0;
            Stage(stage);
        }

        internal void BeginTexture(Texture texture, int zeroBasedIndex, int total)
        {
            this.texture = texture;
            index = zeroBasedIndex;
            this.total = total;
            fraction = .1f + .8f * zeroBasedIndex / Math.Max(1, total);
            Stage("Checking cached texture");
        }

        internal void Overall(string stage, float fraction, bool cancellable = true)
        {
            texture = null;
            total = 0;
            this.fraction = Mathf.Clamp01(fraction);
            Stage(stage, cancellable);
        }

        internal void Stage(string stage, bool cancellable = true)
        {
            this.stage = stage;
            this.cancellable = cancellable;
            Update(true);
        }

        internal void CheckCancelled() => Update(false);

        private void Update(bool force)
        {
            if (disposed) return;
            if (cancelled) throw new OperationCanceledException("Texture optimization cancelled.");
            if (display == null || (!force && clock.ElapsedMilliseconds < nextUpdate)) return;
            nextUpdate = clock.ElapsedMilliseconds + 100;
            string detail = texture ? (total > 0 ? $"Texture {index + 1}/{total}: " : "") + texture.name + "\n" : "";
            detail += stage + $" ({clock.Elapsed.TotalSeconds:F0}s elapsed)";
            if (cancellable) detail += "\nCancel skips this optimization pass.";
            shown = true;
            if (display(detail, fraction, cancellable) && cancellable)
            {
                cancelled = true;
                throw new OperationCanceledException("Texture optimization cancelled.");
            }
        }

        private static bool Display(string detail, float fraction, bool cancellable)
        {
            if (cancellable) return EditorUtility.DisplayCancelableProgressBar("Arclight Optimizer", detail, fraction);
            EditorUtility.DisplayProgressBar("Arclight Optimizer", detail, fraction);
            return false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (shown) clear();
        }
    }
}
