using System.Collections.Generic;
using System.Collections.Concurrent;

namespace LamSims.Core.Downloading;

/// <summary>
/// Thread-safe pool of outstanding chunks. Workers pull until it is empty. Retry lives
/// entirely in ChunkFetcher, so a failed chunk is retried in place rather than requeued.
/// </summary>
public sealed class ChunkQueue
{
    private readonly ConcurrentQueue<Chunk> _pending;

    public ChunkQueue(IEnumerable<Chunk> pending) => _pending = new ConcurrentQueue<Chunk>(pending);

    public int RemainingCount => _pending.Count;

    public bool TryTake(out Chunk? chunk) => _pending.TryDequeue(out chunk);
}
