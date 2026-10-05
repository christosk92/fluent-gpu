using System;
using System.Collections.Generic;
using System.Numerics;
using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The <c>[wake]</c> census names each bit of <see cref="WakeReasons"/> by its POSITION in a hand-written table. That table
/// drifted from the enum twice (a short table that never printed two bits; then bit 25 printed under a retired name while it
/// counted FrameClockPaceable, and bit 29 FeedbackSettle never counted at all). These tests pin it to the enum bit by bit.
/// </summary>
public sealed class WakeDiagnosticsTests
{
    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [Fact]
    public void EveryWakeBit_IsCounted_AndNamedAfterItsEnumMember()
    {
        foreach (WakeReasons reason in Enum.GetValues<WakeReasons>())
        {
            if (reason == WakeReasons.None) continue;
            uint bits = (uint)reason;
            Assert.Equal(1, BitOperations.PopCount(bits));
            int bit = BitOperations.TrailingZeroCount(bits);
            Assert.True(bit < WakeDiagnostics.ReasonCount, $"{reason} (bit {bit}) is past the census table ({WakeDiagnostics.ReasonCount})");
            Assert.Equal(CamelCase(reason.ToString()), WakeDiagnostics.ReasonName(bit));
        }
    }

    [Fact]
    public void EveryCensusColumn_IsAnEnumBit_OrTheRetiredSlot()
    {
        var defined = new HashSet<int>();
        foreach (WakeReasons reason in Enum.GetValues<WakeReasons>())
            if (reason != WakeReasons.None) defined.Add(BitOperations.TrailingZeroCount((uint)reason));
        for (int bit = 0; bit < WakeDiagnostics.ReasonCount; bit++)
        {
            if (defined.Contains(bit)) continue;
            Assert.StartsWith("retired", WakeDiagnostics.ReasonName(bit));
        }
        // No enum bit past the table: the highest defined bit is the table's last column.
        int highest = 0;
        foreach (int bit in defined) highest = Math.Max(highest, bit);
        Assert.Equal(WakeDiagnostics.ReasonCount - 1, highest);
    }
}
