using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend;

public readonly record struct TransferTrackRef(
    string Uri,
    string Uid,
    byte[] Gid,
    IReadOnlyDictionary<string, string> Metadata);

public readonly record struct TransferWireState(
    string ContextUri,
    string ContextUrl,
    IReadOnlyDictionary<string, string> ContextMetadata,
    string CurrentUid,
    TransferTrackRef CurrentTrack,
    IReadOnlyList<TransferTrackRef> Queue,
    bool IsPlayingQueue,
    long TimestampMs,
    long PositionMs,
    double Speed,
    bool Paused,
    bool Shuffle,
    RepeatMode Repeat);

/// <summary>Proto-free boundary implemented by the SpotifyLive protobuf adapter.</summary>
public interface ITransferStateDecoder
{
    bool TryDecode(ReadOnlyMemory<byte> payload, out TransferWireState state);
}
