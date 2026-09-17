using System;
using System.Security.Cryptography;

namespace LamSims.Core.Tests;

/// <summary>Random archive bytes and their digest, shared by every downloader test fixture.</summary>
internal static class Payloads
{
    public static byte[] Random(int size)
    {
        var bytes = new byte[size];
        System.Random.Shared.NextBytes(bytes);
        return bytes;
    }

    public static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
