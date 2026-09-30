namespace FluentGpu.Hosting.Threading;

/// <summary>Render-consumer proof connecting GPU submissions across adopted publications that were verified
/// byte-identical with empty damage and therefore elided. This is not producer drop detection: an unverified gap's
/// damage must still ride forward (<see cref="FluentGpu.Rhi.FrameInfo.CarriedFromSeq"/>).</summary>
internal struct RenderSubmissionContinuity
{
    private ulong _submitted, _verified;

    public void Submitted(ulong sequence) => _submitted = _verified = sequence;

    /// <summary>Call only after the full submit-elision predicate proves this publication changes no pixels.</summary>
    public void Elided(ulong sequence)
    {
        if (sequence > _verified) _verified = sequence;
    }

    public readonly ulong ExtendCarry(ulong carriedFrom)
    {
        // Unknown carry is never evidence. The current publication must connect to the verified range, and that
        // range must contain an actual elision after a successful submission. Using subtraction avoids overflow.
        if (carriedFrom == 0 || _submitted == 0 || _verified <= _submitted || carriedFrom - 1 > _verified)
            return carriedFrom;
        return System.Math.Min(carriedFrom, _submitted + 1);
    }
}
