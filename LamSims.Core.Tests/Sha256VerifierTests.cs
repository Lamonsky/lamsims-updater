using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class Sha256VerifierTests
{
    [Fact]
    public async Task Computes_the_known_digest_of_abc()
    {
        using var temp = new TempDir();
        var path = temp.File("abc.bin");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("abc"));

        var digest = await Sha256Verifier.ComputeAsync(path, CancellationToken.None);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", digest);
    }

    [Fact]
    public async Task Computes_the_known_digest_of_an_empty_file()
    {
        using var temp = new TempDir();
        var path = temp.File("empty.bin");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var digest = await Sha256Verifier.ComputeAsync(path, CancellationToken.None);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", digest);
    }

    [Fact]
    public async Task Streams_a_file_larger_than_its_buffer()
    {
        using var temp = new TempDir();
        var path = temp.File("big.bin");
        var bytes = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);

        var digest = await Sha256Verifier.ComputeAsync(path, CancellationToken.None);

        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, digest);
    }
}
