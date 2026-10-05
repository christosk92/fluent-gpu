using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>A feedback boundary's live state: the spec, the current per-advance warp and decay (NaN = the spec's).</summary>
public readonly record struct FeedbackState(FeedbackSpec Spec, Affine2D Warp, float Decay)
{
    public float EffectiveDecay => float.IsFinite(Decay) ? Math.Clamp(Decay, 0f, 1f) : Spec.Decay;
}

/// <summary>The static half of a <c>SpriteFieldEl</c>: kernel, paint blend, opacity. POD.</summary>
public readonly record struct SpriteSpec(SpriteKernel Kernel, PaintBlend Blend, float Opacity);

public sealed partial class SceneStore
{
    /// <summary>The SpriteFieldEl per-node instance payload: sparse, grow-only pooled <c>Sprite[]</c>, keyed by node INDEX
    /// (the series discipline, SceneStore.Series.cs). A write is a CAPTURED side-table write: <see cref="NoteCaptureChanged"/>
    /// first, then <see cref="MarkRecordDirty(int)"/>.</summary>
    private readonly Dictionary<int, (Sprite[]? Arr, int Count)> _sprites = new();
    private readonly ColdSlab<SpriteSpec> _spriteSpecs = new();

    /// <summary>Sprites dropped at write time because a node carried more than <c>SpriteFieldEl.MaxSprites</c>.</summary>
    public int SpritesDropped { get; private set; }

    public void SetSpriteSpec(NodeHandle node, in SpriteSpec spec)
    {
        int idx = (int)node.Raw.Index;
        if (_spriteSpecs.TryGet(idx, out var cur) && cur == spec) return;
        _flags[idx] |= NodeFlags.SparsePaint;
        _spriteSpecs.GetOrAdd(idx) = spec;
        MarkRecordDirty(idx);
    }

    public bool TryGetSpriteSpec(NodeHandle h, out SpriteSpec spec) => _spriteSpecs.TryGet((int)h.Raw.Index, out spec);

    public void SetSprites(NodeHandle node, ReadOnlySpan<Sprite> sprites)
    {
        int idx = (int)node.Raw.Index;
        NoteCaptureChanged(idx);
        int n = Math.Min(sprites.Length, FluentGpu.Dsl.SpriteFieldEl.MaxSprites);
        if (sprites.Length > n) SpritesDropped += sprites.Length - n;
        if (n == 0)
        {
            if (_sprites.Remove(idx)) MarkRecordDirty(idx);
            return;
        }
        _sprites.TryGetValue(idx, out var slot);
        if (slot.Arr is null || slot.Arr.Length < n) slot.Arr = new Sprite[Math.Max(64, n)];
        sprites[..n].CopyTo(slot.Arr);
        slot.Count = n;
        _sprites[idx] = slot;
        MarkRecordDirty(idx);
    }

    public bool TryGetSprites(NodeHandle h, out ReadOnlySpan<Sprite> sprites)
    {
        if (_sprites.TryGetValue((int)h.Raw.Index, out var slot) && slot.Arr is not null)
        {
            sprites = slot.Arr.AsSpan(0, slot.Count);
            return true;
        }
        sprites = default;
        return false;
    }

    /// <summary>Node release: drop the instance payload and the spec.</summary>
    private void ReleaseSprites(int idx)
    {
        if (_sprites.Count != 0) _sprites.Remove(idx);
        _spriteSpecs.Remove(idx);
    }
}
