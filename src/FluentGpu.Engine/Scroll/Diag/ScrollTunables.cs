using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Diag;

/// <summary>One live-tunable field of <see cref="MotionFeel"/>: its display name, slider range, the value a fresh
/// <see cref="FeelProfiles.Standard"/> carries, and zero-alloc accessor/wither delegates built ONCE at static
/// init (never per call — the whole point of storing them is that a settings UI can iterate <see cref="ScrollTunables.All"/>
/// without any reflection or per-frame allocation).</summary>
public readonly struct TunableF
{
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public Func<MotionFeel, double> Get { get; }
    public Func<MotionFeel, double, MotionFeel> With { get; }

    internal TunableF(string name, double min, double max, Func<MotionFeel, double> get, Func<MotionFeel, double, MotionFeel> with)
    {
        Name = name;
        Min = min;
        Max = max;
        Get = get;
        With = with;
        Default = get(FeelProfiles.Standard);
    }
}

/// <summary>
/// Live registry over exactly one <see cref="MotionFeel"/> (scroll-rework design B.9's "constants-by-complaint"
/// fix): a seqlock-protected current value any thread can read torn-read-free, a small set of named presets
/// (<see cref="FeelProfiles.All"/>: <see cref="FeelProfiles.Standard"/>/<see cref="FeelProfiles.Glide"/>), the
/// per-field slider metadata in <see cref="All"/>, and a NativeAOT-safe (no reflection) JSON round-trip for
/// app-settings persistence.
///
/// THREAD RULE: <see cref="Apply"/>/<see cref="ApplyProfile"/> are written for a single logical writer (the UI
/// thread / the settings page), and take an internal lock so a second concurrent writer cannot interleave with the
/// first's seqlock parity — but <see cref="Current"/> is safe to call from ANY thread, including the render thread
/// reading it once per frame, with no lock and no torn read: it is a classic seqlock (an odd sequence number means
/// a write is in flight; the reader retries whenever the sequence changed under it).
/// </summary>
public static class ScrollTunables
{
    private static readonly object s_writeGate = new();
    private static int s_seq;                                   // even = stable; writer bumps it to odd, writes, bumps to even
    private static MotionFeel s_feel = FeelProfiles.Standard;
    private static string s_activeProfileName = FeelProfiles.All[0].Name;

    /// <summary>The live value, safe to read from any thread without locking (seqlock read: retries if a writer
    /// interleaved). Never torn.</summary>
    public static MotionFeel Current
    {
        get
        {
            MotionFeel snapshot;
            int s1, s2;
            do
            {
                s1 = Volatile.Read(ref s_seq);
                while ((s1 & 1) != 0) { Thread.SpinWait(1); s1 = Volatile.Read(ref s_seq); }
                Thread.MemoryBarrier();
                snapshot = s_feel;
                Thread.MemoryBarrier();
                s2 = Volatile.Read(ref s_seq);
            } while (s1 != s2);
            return snapshot;
        }
    }

    /// <summary>Monotonically increasing version bump on every <see cref="Apply"/>/<see cref="ApplyProfile"/> —
    /// readers poll this instead of subscribing to a change event (no <c>Changed</c> event by design).</summary>
    public static uint Version => (uint)((uint)Volatile.Read(ref s_seq) >> 1);

    /// <summary>Name of the last-applied named profile, or <c>"Custom"</c> once any individual field has been
    /// changed via <see cref="Apply"/> (a whole-struct <c>with</c> edit from a slider, say) without going through
    /// <see cref="ApplyProfile"/>.</summary>
    public static string ActiveProfileName => Volatile.Read(ref s_activeProfileName)!;

    /// <summary>Every live-tunable field, in declaration order, built once at type init.</summary>
    public static IReadOnlyList<TunableF> All { get; } = BuildAll();

    /// <summary>Replace the live value wholesale (e.g. after applying several slider edits at once via
    /// <c>tunable.With(ScrollTunables.Current, newValue)</c>). Marks <see cref="ActiveProfileName"/> as
    /// <c>"Custom"</c>.</summary>
    public static void Apply(in MotionFeel f)
    {
        lock (s_writeGate)
        {
            WriteLocked(f);
            Volatile.Write(ref s_activeProfileName, "Custom");
        }
    }

    /// <summary>Apply one of the built-in named profiles (<see cref="FeelProfiles.All"/>: <c>"Standard"</c>/<c>"Glide"</c>,
    /// case-insensitive). Unknown names are a silent no-op — a corrupted settings file must not crash the scroll
    /// engine.</summary>
    public static void ApplyProfile(string name)
    {
        foreach (var (profileName, feel) in FeelProfiles.All)
        {
            if (string.Equals(profileName, name, StringComparison.OrdinalIgnoreCase))
            {
                lock (s_writeGate)
                {
                    WriteLocked(feel);
                    Volatile.Write(ref s_activeProfileName, profileName);
                }
                return;
            }
        }
    }

    private static void WriteLocked(in MotionFeel f)
    {
        int s = Volatile.Read(ref s_seq);
        Volatile.Write(ref s_seq, s + 1);   // odd: a write is in flight
        Thread.MemoryBarrier();
        s_feel = f;
        Thread.MemoryBarrier();
        Volatile.Write(ref s_seq, s + 2);   // even: stable again, version bumped
    }

    // ── JSON (NativeAOT-safe: Utf8JsonWriter/Utf8JsonReader by hand, no reflection-based JsonSerializer) ─────────

    /// <summary>Serialize the CURRENT live value.</summary>
    public static string ToJson() => ToJson(Current);

    /// <summary>Serialize an arbitrary <see cref="MotionFeel"/> (used by <see cref="ToJson()"/> and available for a
    /// caller that wants to persist a value other than the live one, e.g. a preview).</summary>
    public static string ToJson(in MotionFeel f)
    {
        using var stream = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("wheelNotchDip", f.WheelNotchDip);
            w.WriteNumber("wheelDurationS", f.WheelDurationS);
            w.WriteNumber("accelRefGapS", f.AccelRefGapS);
            w.WriteNumber("accelMax", f.AccelMax);
            w.WriteNumber("accelResetGapS", f.AccelResetGapS);
            w.WriteNumber("accelEmaWeight", f.AccelEmaWeight);
            w.WriteNumber("flingDecayPerS", f.FlingDecayPerS);
            w.WriteNumber("flingMinVelocity", f.FlingMinVelocity);
            w.WriteNumber("flingImpulseWindowS", f.FlingImpulseWindowS);
            w.WriteNumber("velocityMinSpanS", f.VelocityMinSpanS);
            w.WriteNumber("rubberBandC", f.RubberBandC);
            w.WriteNumber("springOmega", f.SpringOmega);
            w.WriteNumber("springZeta", f.SpringZeta);
            w.WriteNumber("glideOmega", f.GlideOmega);
            w.WriteNumber("lookaheadS", f.LookaheadS);
            w.WriteNumber("overscanMinPx", f.OverscanMinPx);
            w.WriteNumber("overscanMaxPx", f.OverscanMaxPx);
            w.WriteNumber("keyLineDip", f.KeyLineDip);
            w.WriteNumber("pageFraction", f.PageFraction);
            w.WriteNumber("wheelLatchSilenceS", f.WheelLatchSilenceS);
            w.WriteNumber("settleEpsilonDip", f.SettleEpsilonDip);
            w.WriteNumber("settleVelocity", f.SettleVelocity);
            w.WriteNumber("wheelRiseS", f.WheelRiseS);
            w.WriteNumber("accelUnityGapS", f.AccelUnityGapS);
            w.WriteNumber("touchpadWheelDip", f.TouchpadWheelDip);
            w.WriteNumber("touchpadReleaseCapDipPerS", f.TouchpadReleaseCapDipPerS);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parse a JSON object previously produced by <see cref="ToJson()"/> into <paramref name="feel"/>.
    /// Unknown fields are skipped; missing fields fall back to <see cref="FeelProfiles.Standard"/>'s value.
    /// Returns false (and sets <paramref name="feel"/> to <see cref="FeelProfiles.Standard"/>) on malformed JSON —
    /// never throws.</summary>
    public static bool TryFromJson(string json, out MotionFeel feel)
    {
        feel = FeelProfiles.Standard;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            MotionFeel f = feel;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                string prop = reader.GetString() ?? "";
                if (!reader.Read()) return false;
                switch (prop)
                {
                    case "wheelNotchDip": f = f with { WheelNotchDip = reader.GetDouble() }; break;
                    case "wheelDurationS": f = f with { WheelDurationS = reader.GetDouble() }; break;
                    case "accelRefGapS": f = f with { AccelRefGapS = reader.GetDouble() }; break;
                    case "accelMax": f = f with { AccelMax = reader.GetDouble() }; break;
                    case "accelResetGapS": f = f with { AccelResetGapS = reader.GetDouble() }; break;
                    case "accelEmaWeight": f = f with { AccelEmaWeight = reader.GetDouble() }; break;
                    case "flingDecayPerS": f = f with { FlingDecayPerS = reader.GetDouble() }; break;
                    case "flingMinVelocity": f = f with { FlingMinVelocity = reader.GetDouble() }; break;
                    case "flingImpulseWindowS": f = f with { FlingImpulseWindowS = reader.GetDouble() }; break;
                    case "velocityMinSpanS": f = f with { VelocityMinSpanS = reader.GetDouble() }; break;
                    case "rubberBandC": f = f with { RubberBandC = reader.GetDouble() }; break;
                    case "springOmega": f = f with { SpringOmega = reader.GetDouble() }; break;
                    case "springZeta": f = f with { SpringZeta = reader.GetDouble() }; break;
                    case "glideOmega": f = f with { GlideOmega = reader.GetDouble() }; break;
                    case "lookaheadS": f = f with { LookaheadS = reader.GetDouble() }; break;
                    case "overscanMinPx": f = f with { OverscanMinPx = reader.GetDouble() }; break;
                    case "overscanMaxPx": f = f with { OverscanMaxPx = reader.GetDouble() }; break;
                    case "keyLineDip": f = f with { KeyLineDip = reader.GetDouble() }; break;
                    case "pageFraction": f = f with { PageFraction = reader.GetDouble() }; break;
                    case "wheelLatchSilenceS": f = f with { WheelLatchSilenceS = reader.GetDouble() }; break;
                    case "settleEpsilonDip": f = f with { SettleEpsilonDip = reader.GetDouble() }; break;
                    case "settleVelocity": f = f with { SettleVelocity = reader.GetDouble() }; break;
                    case "wheelRiseS": f = f with { WheelRiseS = reader.GetDouble() }; break;
                    case "accelUnityGapS": f = f with { AccelUnityGapS = reader.GetDouble() }; break;
                    case "touchpadWheelDip": f = f with { TouchpadWheelDip = reader.GetDouble() }; break;
                    case "touchpadReleaseCapDipPerS": f = f with { TouchpadReleaseCapDipPerS = reader.GetDouble() }; break;
                    default: reader.Skip(); break;
                }
            }
            feel = f;
            return true;
        }
        catch (Exception)
        {
            // Deliberately broad: Utf8JsonReader/GetDouble/GetBoolean can throw JsonException, FormatException, or
            // InvalidOperationException (wrong token kind) for malformed input — none of them may escape this method.
            feel = FeelProfiles.Standard;
            return false;
        }
    }

    // ── slider metadata (built once) ────────────────────────────────────────────────────────────────────────────

    private static TunableF[] BuildAll() => new[]
    {
        new TunableF("WheelNotchDip", 8.0, 120.0,
            static f => f.WheelNotchDip, static (f, v) => f with { WheelNotchDip = v }),
        new TunableF("WheelDurationS", 0.05, 1.0,
            static f => f.WheelDurationS, static (f, v) => f with { WheelDurationS = v }),
        new TunableF("AccelRefGapS", 0.002, 0.1,
            static f => f.AccelRefGapS, static (f, v) => f with { AccelRefGapS = v }),
        new TunableF("AccelMax", 1.0, 10.0,
            static f => f.AccelMax, static (f, v) => f with { AccelMax = v }),
        new TunableF("AccelResetGapS", 0.05, 2.0,
            static f => f.AccelResetGapS, static (f, v) => f with { AccelResetGapS = v }),
        new TunableF("AccelEmaWeight", 0.0, 1.0,
            static f => f.AccelEmaWeight, static (f, v) => f with { AccelEmaWeight = v }),
        new TunableF("FlingDecayPerS", 0.1, 20.0,
            static f => f.FlingDecayPerS, static (f, v) => f with { FlingDecayPerS = v }),
        new TunableF("FlingMinVelocity", 0.0, 500.0,
            static f => f.FlingMinVelocity, static (f, v) => f with { FlingMinVelocity = v }),
        new TunableF("FlingImpulseWindowS", 0.01, 0.2,
            static f => f.FlingImpulseWindowS, static (f, v) => f with { FlingImpulseWindowS = v }),
        new TunableF("VelocityMinSpanS", 0.0, 0.05,
            static f => f.VelocityMinSpanS, static (f, v) => f with { VelocityMinSpanS = v }),
        new TunableF("RubberBandC", 0.1, 1.0,
            static f => f.RubberBandC, static (f, v) => f with { RubberBandC = v }),
        new TunableF("SpringOmega", 1.0, 60.0,
            static f => f.SpringOmega, static (f, v) => f with { SpringOmega = v }),
        new TunableF("SpringZeta", 0.1, 2.0,
            static f => f.SpringZeta, static (f, v) => f with { SpringZeta = v }),
        new TunableF("GlideOmega", 1.0, 60.0,
            static f => f.GlideOmega, static (f, v) => f with { GlideOmega = v }),
        new TunableF("LookaheadS", 0.0, 1.0,
            static f => f.LookaheadS, static (f, v) => f with { LookaheadS = v }),
        new TunableF("OverscanMinPx", 0.0, 2000.0,
            static f => f.OverscanMinPx, static (f, v) => f with { OverscanMinPx = v }),
        new TunableF("OverscanMaxPx", 0.0, 5000.0,
            static f => f.OverscanMaxPx, static (f, v) => f with { OverscanMaxPx = v }),
        new TunableF("KeyLineDip", 8.0, 200.0,
            static f => f.KeyLineDip, static (f, v) => f with { KeyLineDip = v }),
        new TunableF("PageFraction", 0.1, 1.0,
            static f => f.PageFraction, static (f, v) => f with { PageFraction = v }),
        new TunableF("WheelLatchSilenceS", 0.05, 1.0,
            static f => f.WheelLatchSilenceS, static (f, v) => f with { WheelLatchSilenceS = v }),
        new TunableF("SettleEpsilonDip", 0.01, 2.0,
            static f => f.SettleEpsilonDip, static (f, v) => f with { SettleEpsilonDip = v }),
        new TunableF("SettleVelocity", 0.0, 50.0,
            static f => f.SettleVelocity, static (f, v) => f with { SettleVelocity = v }),
        new TunableF("WheelRiseS", 0.0, 0.05,
            static f => f.WheelRiseS, static (f, v) => f with { WheelRiseS = v }),
        new TunableF("AccelUnityGapS", 0.0, 0.12,
            static f => f.AccelUnityGapS, static (f, v) => f with { AccelUnityGapS = v }),
        new TunableF("TouchpadWheelDip", 8.0, 120.0,
            static f => f.TouchpadWheelDip, static (f, v) => f with { TouchpadWheelDip = v }),
        new TunableF("TouchpadReleaseCapDipPerS", 0.0, 40000.0,
            static f => f.TouchpadReleaseCapDipPerS, static (f, v) => f with { TouchpadReleaseCapDipPerS = v }),
    };
}
