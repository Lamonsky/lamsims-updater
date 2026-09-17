using System;
using System.Collections.Generic;

namespace LamSims.Core.Downloading;

/// <summary>A contiguous byte range of the archive, fetched by a single request.</summary>
public sealed record Chunk(int Index, long Start, long Length)
{
    public long EndInclusive => Start + Length - 1;
}

public static class ChunkPlan
{
    public const long DefaultChunkSize = 16L * 1024 * 1024;

    /// <summary>
    /// Divides the archive into fixed-size chunks. Workers pull from this plan through a
    /// queue rather than owning a fixed slice, so one slow mirror stalls a single chunk
    /// instead of a fixed fraction of the file.
    /// </summary>
    public static IReadOnlyList<Chunk> Create(long totalSize, long chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        var chunks = new List<Chunk>();
        var index = 0;
        for (var start = 0L; start < totalSize; start += chunkSize)
            chunks.Add(new Chunk(index++, start, Math.Min(chunkSize, totalSize - start)));

        return chunks;
    }
}
