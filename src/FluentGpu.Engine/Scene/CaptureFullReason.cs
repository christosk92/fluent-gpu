namespace FluentGpu.Scene;

/// <summary>Why a scene publication took a FULL capture instead of the incremental (changed-nodes-only) one — the first
/// clause of <c>SceneRecordingSnapshot.CanCaptureIncremental</c> that refused, in the order it tests them.</summary>
public enum CaptureFullReason : byte
{
    /// <summary>The capture was incremental (or nothing was captured this frame).</summary>
    None,
    /// <summary>The slot has never captured, or captured from a different store.</summary>
    NoBaseline,
    /// <summary>The slot's last capture is not the publication the caller named as the baseline.</summary>
    BaselineMismatch,
    /// <summary>The store's node high-water shrank below what the slot describes.</summary>
    StoreShrank,
    /// <summary>The store's capture ledger floor rose past the baseline (history truncated).</summary>
    LedgerTruncated,
    /// <summary>A bulk mutation (layout pass, reconciler commit, column realloc) landed since the baseline.</summary>
    BulkMutation,
}
