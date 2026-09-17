using System;
using System.Linq;

namespace LamSims.Core;

internal static class Hex
{
    /// <summary>
    /// True for exactly the shape a SHA-256 digest has. Both record stores check it before
    /// trusting a value read off disk, and treat a malformed digest as an absent one.
    /// </summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
