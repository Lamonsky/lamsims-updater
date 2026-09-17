using System;
using System.Collections.Generic;
using System.Linq;

namespace LamSims.Core.Downloading;

/// <summary>A chunk already on disk, and the mirror that served it.</summary>
public sealed record CompletedChunk(int Index, string MirrorUrl);

/// <summary>
/// The resume sidecar. Records enough to skip completed chunks after a restart and to
/// detect that a mirror's entity changed underneath us. Completion is attributed per
/// mirror so a changed validator invalidates only the chunks that mirror contributed.
/// </summary>
public sealed record PartState(
    string Code,
    long TotalSize,
    string ExpectedSha256,
    long ChunkSize,
    IReadOnlyList<CompletedChunk> CompletedChunks,
    IReadOnlyList<MirrorValidator> Mirrors)
{
    public bool Equals(PartState? other) =>
        other is not null
        && Code == other.Code
        && TotalSize == other.TotalSize
        && ExpectedSha256 == other.ExpectedSha256
        && ChunkSize == other.ChunkSize
        && CompletedChunks.SequenceEqual(other.CompletedChunks)
        && Mirrors.SequenceEqual(other.Mirrors);

    public override int GetHashCode() => HashCode.Combine(Code, TotalSize, ExpectedSha256, ChunkSize);
}
