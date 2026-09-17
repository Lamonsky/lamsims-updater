using System;
using Xunit;
using LamSims.Core;

namespace LamSims.Core.Tests;

public class PathIdentityTests
{
    [Fact]
    public void Canonical_collapses_case_and_composition_but_not_different_paths()
    {
        var a = PathIdentity.Canonical("/games/Caf\u00e9");        // composed
        var b = PathIdentity.Canonical("/games/Cafe\u0301");        // decomposed
        var c = PathIdentity.Canonical("/games/CAF\u00c9");
        var d = PathIdentity.Canonical("/games/Other");

        Assert.Equal(a, b);
        Assert.Equal(a, c, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a, d, StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Two directories that differ only in case are two directories on a case-sensitive
    /// filesystem, and a key that folded them made the second install overwrite the first's
    /// record. The re-check every reader does prevents a WRONG record being trusted; it cannot
    /// bring back one that was overwritten.
    /// </summary>
    [Fact]
    public void A_key_keeps_two_paths_that_differ_only_in_case_apart()
    {
        Assert.NotEqual(PathIdentity.DirectoryKey("/games/Prefix"),
                        PathIdentity.DirectoryKey("/games/prefix"));
    }

    /// <summary>
    /// Case is the only thing the key stops folding. Composition still folds, or a name a
    /// macOS volume hands back decomposed would key differently from the same name composed.
    /// </summary>
    [Fact]
    public void A_key_still_folds_composition()
    {
        Assert.Equal(PathIdentity.DirectoryKey("/games/Caf\u00e9"),
                     PathIdentity.DirectoryKey("/games/Cafe\u0301"));
    }

}
