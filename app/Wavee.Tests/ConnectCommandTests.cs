using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Stage C — parse the dealer REQUEST command JSON into the flat POD, and ack-on-dispatch via the router.
public class ConnectCommandTests
{
    const string Ident = "hm://connect-state/v1/player/command";
    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
    static WireRequest Req(string json, string key = "k") => new(key, Ident, Encoding.UTF8.GetBytes(json), NoHeaders);

    [Fact]
    public void Parse_Pause_ReadsIdsAndKey()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"message_id\":7,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}", "7/phone"), out var c));
        Assert.Equal(ConnectCmd.Pause, c.Kind);
        Assert.Equal(7, c.MessageId);
        Assert.Equal("phone", c.SenderDeviceId);
        Assert.Equal("7/phone", c.Key);
    }

    [Fact]
    public void Parse_SeekTo_NumberAndString()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"seek_to\",\"value\":12345}}"), out var a));
        Assert.Equal(ConnectCmd.SeekTo, a.Kind);
        Assert.Equal(12345, a.SeekToMs);

        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"seek_to\",\"value\":\"6789\"}}"), out var b));
        Assert.Equal(6789, b.SeekToMs);   // wire sometimes sends the position as a JSON string
    }

    [Fact]
    public void Parse_ShuffleRepeat_BoolArg()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"set_shuffling_context\",\"value\":true}}"), out var s));
        Assert.Equal(ConnectCmd.SetShufflingContext, s.Kind);
        Assert.True(s.BoolArg);

        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"set_repeating_track\",\"value\":false}}"), out var r));
        Assert.Equal(ConnectCmd.SetRepeatingTrack, r.Kind);
        Assert.False(r.BoolArg);
    }

    [Fact]
    public void Parse_UnknownEndpoint_ReturnsFalse()
        => Assert.False(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"frobnicate\"}}"), out _));

    [Fact]
    public void Router_KnownCommand_Dispatches_AndAcksSuccess()
    {
        var t = new StubTransport();
        var got = new List<ConnectCmd>();
        using var r = new ConnectCommandRouter(t, c => got.Add(c.Kind));
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"skip_next\"}}", "k1"));
        Assert.Equal(new[] { ConnectCmd.SkipNext }, got);
        Assert.Equal(RequestResult.Success, t.LastReply);
    }

    [Fact]
    public void Router_Unsupported_AcksDeviceDoesNotSupport()
    {
        var t = new StubTransport();
        using var r = new ConnectCommandRouter(t, _ => { });
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"frobnicate\"}}", "k2"));
        Assert.Equal(RequestResult.DeviceDoesNotSupportCommand, t.LastReply);
    }

    [Fact]
    public void Router_DispatchThrows_StillAcks_ContextPlayerError()
    {
        var t = new StubTransport();
        using var r = new ConnectCommandRouter(t, _ => throw new InvalidOperationException("boom"));
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"pause\"}}", "k3"));
        Assert.Equal(RequestResult.ContextPlayerError, t.LastReply);   // never withhold the ack
    }
}
