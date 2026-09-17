using System;
using System.Linq;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ChunkPlanTests
{
    [Fact]
    public void Splits_an_exact_multiple_into_equal_chunks()
    {
        var chunks = ChunkPlan.Create(300, 100);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new Chunk(0, 0, 100), chunks[0]);
        Assert.Equal(new Chunk(1, 100, 100), chunks[1]);
        Assert.Equal(new Chunk(2, 200, 100), chunks[2]);
    }

    [Fact]
    public void Gives_the_remainder_to_the_final_chunk()
    {
        var chunks = ChunkPlan.Create(250, 100);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(50, chunks[2].Length);
        Assert.Equal(249, chunks[2].EndInclusive);
    }

    [Fact]
    public void Produces_one_chunk_when_the_file_is_smaller_than_a_chunk()
    {
        var chunks = ChunkPlan.Create(10, 100);

        Assert.Single(chunks);
        Assert.Equal(new Chunk(0, 0, 10), chunks[0]);
    }

    [Fact]
    public void Produces_nothing_for_an_empty_file()
    {
        Assert.Empty(ChunkPlan.Create(0, 100));
    }

    [Fact]
    public void Chunks_cover_the_file_without_gaps_or_overlap()
    {
        var chunks = ChunkPlan.Create(1_000_003, 16 * 1024);

        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(1_000_002, chunks[^1].EndInclusive);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].EndInclusive + 1, chunks[i].Start);
        Assert.Equal(1_000_003, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void Rejects_a_non_positive_chunk_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlan.Create(100, 0));
    }

    [Fact]
    public void DownloadOptions_rejects_a_non_positive_chunk_size_at_the_setter()
    {
        // Rejected where the value is supplied, the way Connections is: reaching
        // ChunkPlan.Create with it would throw out of DownloadAsync instead.
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadOptions { ChunkSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadOptions { ChunkSize = -1 });
    }
}
