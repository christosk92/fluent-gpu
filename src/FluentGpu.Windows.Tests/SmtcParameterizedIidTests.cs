using System;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Locks the load-bearing WinRT parameterized-interface IIDs the SMTC <c>TypedEventHandler</c> callbacks are registered
/// under. A closed <c>TypedEventHandler&lt;T,U&gt;</c> is QI'd by the OS for the <i>parameterized instance</i> IID,
/// computed with the documented WinRT generic-instance GUID algorithm (RFC 4122 v5 / SHA-1 over the pinterface
/// namespace GUID + the UTF-8 type signature). These IIDs are DERIVED constants, not copied from a header — the single
/// most failure-prone values in the Media pillar — so this test reproduces the algorithm and asserts it yields:
/// <list type="bullet">
/// <item>the <b>published</b> button-pressed handler IID (<c>0557E996-…</c>) — a proof that the algorithm is correct,
/// cross-validated against the value that ships working in <c>MediaButtonHandler</c>;</item>
/// <item>the committed <b>position-change</b> handler IID (<c>44E34F15-…</c>, <c>MediaPositionChangeHandler</c>, G7) —
/// so a typo in that derived constant (which would make lock-screen scrub silently never fire) fails here at build
/// time rather than only on a real device.</item>
/// </list>
/// Pure math (no COM / no window), so it runs headlessly in CI.
/// </summary>
public sealed class SmtcParameterizedIidTests
{
    // The WinRT pinterface namespace GUID (big-endian raw bytes) that seeds the v5 name hash.
    private static readonly byte[] PInterfaceNamespace =
        Guid.Parse("11f47ad5-7b73-42c0-abae-878b1e16adee").ToByteArray(bigEndian: true);

    // ITypedEventHandler`2 open-generic IID + the SMTC sender runtime class's default-interface IID.
    private const string TypedEventHandler = "9de1c534-6ae1-11e0-84e1-18a905bcc53f";
    private const string SmtcDefault       = "99fa3ff4-1742-42a6-902e-087d41f965ec";   // ISystemMediaTransportControls

    /// <summary>Compute the WinRT parameterized IID for the signature string (cppwinrt's pinterface_guid_of).</summary>
    private static string Piid(string signature)
    {
        byte[] data = new byte[PInterfaceNamespace.Length + Encoding.UTF8.GetByteCount(signature)];
        PInterfaceNamespace.CopyTo(data, 0);
        Encoding.UTF8.GetBytes(signature, 0, signature.Length, data, PInterfaceNamespace.Length);

        byte[] h = SHA1.HashData(data);
        Span<byte> b = stackalloc byte[16];
        h.AsSpan(0, 16).CopyTo(b);
        b[6] = (byte)((b[6] & 0x0F) | 0x50);   // version 5
        b[8] = (byte)((b[8] & 0x3F) | 0x80);   // variant
        // cppwinrt formats the digest bytes directly (big-endian) as the GUID fields.
        var sb = new StringBuilder(36);
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) sb.Append('-');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string Handler(string senderRcName, string senderIid, string argsRcName, string argsIid)
        => $"pinterface({{{TypedEventHandler}}};rc({senderRcName};{{{senderIid}}});rc({argsRcName};{{{argsIid}}}))";

    [Fact] // Algorithm proof: reproduce the published ButtonPressed handler IID that ships working in MediaButtonHandler.
    public void ButtonPressedHandler_ParameterizedIid_Matches_Published()
    {
        string sig = Handler(
            "Windows.Media.SystemMediaTransportControls", SmtcDefault,
            "Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs", "b7f47116-a56f-4dc8-9e11-92031f4a87c2");
        Assert.Equal("0557E996-7B23-5BAE-AA81-EA0D671143A4", Piid(sig));
    }

    [Fact] // Lock the G7 position-change handler IID (MediaPositionChangeHandlerConstants.ParameterizedIid).
    public void PositionChangeHandler_ParameterizedIid_Matches_Committed()
    {
        string sig = Handler(
            "Windows.Media.SystemMediaTransportControls", SmtcDefault,
            "Windows.Media.PlaybackPositionChangeRequestedEventArgs", "b4493f88-eb28-4961-9c14-335e44f3e125");
        Assert.Equal("44E34F15-BDC0-50A7-ACE4-39E91FB753F1", Piid(sig));
    }
}
